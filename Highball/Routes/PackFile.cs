using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Highball.Routes;

/// A client pack: one region's tiles, and the track to draw on them, in one file the game reads tiles from as it needs
/// them. Laid out as the tiles one after another, then the embedded files, then an index of where each is (with its
/// hash, for patches), then the index's offset in the last 8 bytes.
///
/// A tile is Brotli-compressed: its heights as decimetres above its lowest point, each stored as the difference from
/// what its left, upper and upper-left neighbours predict (small numbers, which compress far better than the heights),
/// with hand sculpting already added in; then its land cover.
public static class PackFile
{
    private const uint Magic = 0x4B504248; // "HBPK"
    private const int Version = 1;

    public sealed record TileEntry(TileId Id, long Offset, int Length, byte[] Hash);

    public sealed record FileEntry(string Name, long Offset, int Length, byte[] Hash);

    public sealed record Header(int Zone, string Region, double TileSize, int Samples, int CoverCells, List<TileEntry> Tiles, List<FileEntry> Files);

    /// One tile as the pack keeps it.
    public sealed record Tile(TileId Id, float MinHeight, float MaxHeight, string HeightSource, float[] Heights, byte[] Cover);

    public sealed class Writer : IDisposable
    {
        private readonly FileStream _file;
        private readonly List<TileEntry> _tiles = [];
        private readonly List<FileEntry> _files = [];
        private readonly int _zone;
        private readonly string _region;
        private readonly RouteGrid _grid;

        public Writer(string path, int zone, string region, RouteGrid grid)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _file = File.Create(path);
            (_zone, _region, _grid) = (zone, region, grid);
        }

        public void Add(Tile tile)
        {
            var blob = Encode(tile, _grid.Samples, _grid.CoverCells);
            _tiles.Add(new TileEntry(tile.Id, _file.Position, blob.Length, Hash(blob)));
            _file.Write(blob);
        }

        /// A file kept whole: the track and what's placed beside it, for the client to lay out as the editor does.
        public void AddFile(string name, byte[] data)
        {
            _files.Add(new FileEntry(name, _file.Position, data.Length, Hash(data)));
            _file.Write(data);
        }

        public long Length => _file.Length;

        public IReadOnlyList<TileEntry> Tiles => _tiles;

        public void Dispose()
        {
            var index = _file.Position;
            using (var w = new BinaryWriter(_file, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(_zone);
                w.Write(_region);
                w.Write(_grid.TileSize);
                w.Write(_grid.Samples);
                w.Write(_grid.CoverCells);
                w.Write(_tiles.Count);
                foreach (var t in _tiles)
                {
                    w.Write(t.Id.I);
                    w.Write(t.Id.J);
                    w.Write(t.Offset);
                    w.Write(t.Length);
                    w.Write(t.Hash);
                }

                w.Write(_files.Count);
                foreach (var f in _files)
                {
                    w.Write(f.Name);
                    w.Write(f.Offset);
                    w.Write(f.Length);
                    w.Write(f.Hash);
                }

                w.Write(index);
            }

            _file.Dispose();
        }
    }

    /// The first 16 bytes of the SHA-256: what a patch compares to know a tile's changed.
    public static byte[] Hash(byte[] data) => SHA256.HashData(data)[..16];

    private static byte[] Encode(Tile tile, int samples, int cells)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(tile.MinHeight);
            w.Write(tile.MaxHeight);
            w.Write(tile.HeightSource);
            var packed = new int[samples * samples];
            for (var i = 0; i < packed.Length; i++)
                packed[i] = (int)Math.Clamp(MathF.Round((tile.Heights[i] - tile.MinHeight) * 10f), 0f, 65535f);
            for (var z = 0; z < samples; z++)
            for (var x = 0; x < samples; x++)
            {
                var residual = packed[z * samples + x] - Predict(packed, samples, x, z);
                if (residual is > short.MaxValue or < short.MinValue)
                    throw new InvalidDataException($"tile {tile.Id}: heights jump too far between samples to pack");
                // Zigzag, so small negatives are small too.
                w.Write((ushort)((residual << 1) ^ (residual >> 31)));
            }

            w.Write(tile.Cover);
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return output.ToArray();
    }

    private static int Predict(int[] p, int samples, int x, int z)
    {
        if (x == 0 && z == 0)
            return 0;
        if (z == 0)
            return p[x - 1];
        if (x == 0)
            return p[(z - 1) * samples];
        return p[z * samples + x - 1] + p[(z - 1) * samples + x] - p[(z - 1) * samples + x - 1];
    }

    public static Header ReadHeader(Stream file)
    {
        file.Seek(-8, SeekOrigin.End);
        Span<byte> tail = stackalloc byte[8];
        file.ReadExactly(tail);
        file.Seek(BinaryPrimitives.ReadInt64LittleEndian(tail), SeekOrigin.Begin);
        using var r = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("not a client pack");
        if (r.ReadInt32() is var version && version != Version)
            throw new InvalidDataException($"pack version {version}, not {Version}");
        var (zone, region, size, samples, cells) = (r.ReadInt32(), r.ReadString(), r.ReadDouble(), r.ReadInt32(), r.ReadInt32());
        var tiles = new List<TileEntry>();
        for (var n = r.ReadInt32(); n > 0; n--)
            tiles.Add(new TileEntry(new TileId(r.ReadInt32(), r.ReadInt32()), r.ReadInt64(), r.ReadInt32(), r.ReadBytes(16)));
        var files = new List<FileEntry>();
        for (var n = r.ReadInt32(); n > 0; n--)
            files.Add(new FileEntry(r.ReadString(), r.ReadInt64(), r.ReadInt32(), r.ReadBytes(16)));
        return new Header(zone, region, size, samples, cells, tiles, files);
    }

    public static byte[] ReadBlob(Stream file, long offset, int length)
    {
        var data = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(data);
        return data;
    }

    public static Tile ReadTile(Stream file, Header header, TileEntry entry)
    {
        using var brotli = new BrotliStream(new MemoryStream(ReadBlob(file, entry.Offset, entry.Length)), CompressionMode.Decompress);
        using var r = new BinaryReader(brotli);
        var (min, max, source) = (r.ReadSingle(), r.ReadSingle(), r.ReadString());
        var samples = header.Samples;
        var packed = new int[samples * samples];
        for (var z = 0; z < samples; z++)
        for (var x = 0; x < samples; x++)
        {
            var zig = r.ReadUInt16();
            var residual = (zig >> 1) ^ -(zig & 1);
            packed[z * samples + x] = Predict(packed, samples, x, z) + residual;
        }

        var heights = new float[packed.Length];
        for (var i = 0; i < packed.Length; i++)
            heights[i] = min + packed[i] / 10f;
        return new Tile(entry.Id, min, max, source, heights, r.ReadBytes(header.CoverCells * header.CoverCells));
    }
}
