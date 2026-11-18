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
/// with hand sculpting already added in; then its land cover. Version 2 stores those differences' low bytes, then their
/// high bytes (nearly all zero, so they all but vanish), at Brotli quality 9: about 73% of version 1's size, at about
/// 0.1 s a tile to pack. Version 1 packs (each difference's two bytes together, quality 4) are still read.
public static class PackFile
{
    private const uint Magic = 0x4B504248; // "HBPK"
    private const int Version = 3;

    /// Brotli's quality and window for tiles: 9 is the knee (10 and 11 save another 5-7% at 8-20 times the time, which across
    /// the whole country is days). Release builds of the packs can raise it.
    public static int TileQuality { get; set; } = 9;
    private const int TileWindow = 22;

    public sealed record TileEntry(TileId Id, long Offset, int Length, byte[] Hash);

    public sealed record FileEntry(string Name, long Offset, int Length, byte[] Hash);

    public sealed record Header(int Zone, string Region, double TileSize, int Samples, int CoverCells, List<TileEntry> Tiles, List<FileEntry> Files, int Version = 1);

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

        public void Add(Tile tile) => AddEncoded(tile.Id, Encode(tile, _grid.Samples, _grid.CoverCells));

        /// A tile already encoded (EncodeTile), so many can be encoded at once and written in order.
        public void AddEncoded(TileId id, byte[] blob)
        {
            _tiles.Add(new TileEntry(id, _file.Position, blob.Length, Hash(blob)));
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

    /// A tile as the pack stores it, for Writer.AddEncoded. Thread-safe.
    public static byte[] EncodeTile(Tile tile, RouteGrid grid, NearTrack? near = null) => Encode(tile, grid.Samples, grid.CoverCells, grid.TileSize, near);

    private static byte[] Encode(Tile tile, int samples, int cells, double tileSize = 2000.0, NearTrack? near = null)
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
            if (TerrainTolerance > 0 && Adaptive(samples))
                WriteAdaptive(w, tile.Id, packed, samples, tileSize, near);
            else
            {
                // Every sample: the predictor's residuals, zigzagged (small negatives small too), low bytes then high.
                w.Write((byte)0);
                w.Write(Planes(packed, samples, tile.Id));
            }

            w.Write(tile.Cover);
        }

        var source = raw.GetBuffer().AsSpan(0, (int)raw.Length);
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, output, out var written, TileQuality, TileWindow))
            throw new InvalidDataException($"tile {tile.Id}: couldn't compress");
        return output[..written];
    }

    // --- Version 3: ground away from the track at 10 m, wherever that's within TerrainTolerance. ---

    /// How far (decimetres) ground away from the track may be off in a pack: there, heights are kept every Coarse samples (10 m,
    /// what most of the country's source data has anyway) and filled in between, and only where that misses by more than this
    /// (a cutting, a ridge) or near track (NearTrack) is every 2 m sample kept. 0 keeps every sample everywhere. About a
    /// quarter of the size at 1 m.
    public static int TerrainTolerance { get; set; } = 10;

    /// Whether ground at a place (route metres east and north) is near track: kept exact.
    public delegate bool NearTrack(double east, double north);

    private const int Coarse = 5;
    private const int Block = 50;

    private static bool Adaptive(int samples) => (samples - 1) % Block == 0 && Block % Coarse == 0;

    /// The ground between the coarse samples: bilinear from the four round it, in whole decimetres (integers, so packing and
    /// unpacking agree to the last bit).
    private static int Between(int[] coarse, int across, int x, int z)
    {
        var (i, a, j, b) = (x / Coarse, x % Coarse, z / Coarse, z % Coarse);
        long sum = (long)(Coarse - a) * (Coarse - b) * coarse[j * across + i];
        if (a > 0)
            sum += (long)a * (Coarse - b) * coarse[j * across + i + 1];
        if (b > 0)
            sum += (long)(Coarse - a) * b * coarse[(j + 1) * across + i];
        if (a > 0 && b > 0)
            sum += (long)a * b * coarse[(j + 1) * across + i + 1];
        return (int)((2 * sum + Coarse * Coarse) / (2 * Coarse * Coarse));
    }

    /// The samples each correction run covers, in order: every fine block's inside (the tile's border left out), then every
    /// exact stretch of the four borders (top, bottom, left, right; each Block + 1 samples, ends shared).
    private static IEnumerable<int> Corrected(int samples, bool[] blocks, bool[] borders)
    {
        var n = (samples - 1) / Block;
        for (var bj = 0; bj < n; bj++)
        for (var bi = 0; bi < n; bi++)
        {
            if (!blocks[bj * n + bi])
                continue;
            for (var z = Math.Max(bj * Block, 1); z < Math.Min((bj + 1) * Block + (bj == n - 1 ? 1 : 0), samples - 1); z++)
            for (var x = Math.Max(bi * Block, 1); x < Math.Min((bi + 1) * Block + (bi == n - 1 ? 1 : 0), samples - 1); x++)
                yield return z * samples + x;
        }

        for (var edge = 0; edge < 4; edge++)
        for (var k = 0; k < n; k++)
        {
            if (!borders[edge * n + k])
                continue;
            for (var t = k * Block; t <= (k + 1) * Block; t++)
                yield return edge switch
                {
                    0 => t,
                    1 => (samples - 1) * samples + t,
                    2 => t * samples,
                    _ => t * samples + samples - 1,
                };
        }
    }

    /// Writes a tile's heights (whole decimetres above its lowest) as version 3 (see TerrainTolerance).
    private static void WriteAdaptive(BinaryWriter w, TileId id, int[] packed, int samples, double tileSize, NearTrack? near)
    {
        var across = (samples - 1) / Coarse + 1;
        var coarse = new int[across * across];
        for (var j = 0; j < across; j++)
        for (var i = 0; i < across; i++)
            coarse[j * across + i] = packed[j * Coarse * samples + i * Coarse];

        var n = (samples - 1) / Block;
        var spacing = tileSize / (samples - 1);
        var (east0, north1) = (id.I * tileSize, (id.J + 1) * tileSize);
        bool Near(double x, double z) => near is not null && near(east0 + x * spacing, north1 - z * spacing);
        int Off(int i) => Math.Abs(packed[i] - Between(coarse, across, i % samples, i / samples));

        var blocks = new bool[n * n];
        for (var bj = 0; bj < n; bj++)
        for (var bi = 0; bi < n; bi++)
        {
            var fine = Near((bi + 0.5) * Block, (bj + 0.5) * Block);
            for (var z = Math.Max(bj * Block, 1); !fine && z < Math.Min((bj + 1) * Block + (bj == n - 1 ? 1 : 0), samples - 1); z++)
            for (var x = Math.Max(bi * Block, 1); !fine && x < Math.Min((bi + 1) * Block + (bi == n - 1 ? 1 : 0), samples - 1); x++)
                fine = Off(z * samples + x) > TerrainTolerance;
            blocks[bj * n + bi] = fine;
        }

        // A border stretch is judged on its own samples and place alone, so the tile beside it, sharing them, judges it the same.
        var borders = new bool[4 * n];
        for (var edge = 0; edge < 4; edge++)
        for (var k = 0; k < n; k++)
        {
            var (mx, mz) = edge switch
            {
                0 => ((k + 0.5) * Block, 0.0),
                1 => ((k + 0.5) * Block, samples - 1.0),
                2 => (0.0, (k + 0.5) * Block),
                _ => (samples - 1.0, (k + 0.5) * Block),
            };
            var exact = Near(mx, mz);
            for (var t = k * Block; !exact && t <= (k + 1) * Block; t++)
                exact = Off(edge switch { 0 => t, 1 => (samples - 1) * samples + t, 2 => t * samples, _ => t * samples + samples - 1 }) > TerrainTolerance;
            borders[edge * n + k] = exact;
        }

        w.Write((byte)1);
        w.Write(Planes(coarse, across, id));
        w.Write(Bits(blocks));
        w.Write(Bits(borders));
        var corrections = Corrected(samples, blocks, borders).Select(i => packed[i] - Between(coarse, across, i % samples, i / samples)).ToArray();
        w.Write(corrections.Length);
        var planes = new byte[corrections.Length * 2];
        for (var i = 0; i < corrections.Length; i++)
        {
            var zig = (ushort)((corrections[i] << 1) ^ (corrections[i] >> 31));
            (planes[i], planes[corrections.Length + i]) = ((byte)zig, (byte)(zig >> 8));
        }

        w.Write(planes);
    }

    private static int[] ReadAdaptive(BinaryReader r, int samples)
    {
        var across = (samples - 1) / Coarse + 1;
        var coarse = Unplanes(r.ReadBytes(across * across * 2), across);
        var n = (samples - 1) / Block;
        var blocks = Unbits(r.ReadBytes((n * n + 7) / 8), n * n);
        var borders = Unbits(r.ReadBytes((4 * n + 7) / 8), 4 * n);
        var count = r.ReadInt32();
        var planes = r.ReadBytes(count * 2);

        var packed = new int[samples * samples];
        for (var z = 0; z < samples; z++)
        for (var x = 0; x < samples; x++)
            packed[z * samples + x] = Between(coarse, across, x, z);
        var k = 0;
        foreach (var i in Corrected(samples, blocks, borders))
        {
            int zig = planes[k] | (planes[count + k] << 8);
            packed[i] = Between(coarse, across, i % samples, i / samples) + ((zig >> 1) ^ -(zig & 1));
            k++;
        }

        if (k != count)
            throw new InvalidDataException($"tile corrections: {count} stored, {k} expected");
        return packed;
    }

    /// Whole-decimetre heights as the predictor's residuals, zigzagged, low bytes then high (version 2's layout).
    private static byte[] Planes(int[] packed, int samples, TileId id)
    {
        var planes = new byte[packed.Length * 2];
        for (var z = 0; z < samples; z++)
        for (var x = 0; x < samples; x++)
        {
            var i = z * samples + x;
            var residual = packed[i] - Predict(packed, samples, x, z);
            if (residual is > short.MaxValue or < short.MinValue)
                throw new InvalidDataException($"tile {id}: heights jump too far between samples to pack");
            var zig = (ushort)((residual << 1) ^ (residual >> 31));
            planes[i] = (byte)zig;
            planes[packed.Length + i] = (byte)(zig >> 8);
        }

        return planes;
    }

    private static int[] Unplanes(byte[] planes, int samples)
    {
        var count = samples * samples;
        var packed = new int[count];
        for (var z = 0; z < samples; z++)
        for (var x = 0; x < samples; x++)
        {
            var i = z * samples + x;
            int zig = planes[i] | (planes[count + i] << 8);
            packed[i] = Predict(packed, samples, x, z) + ((zig >> 1) ^ -(zig & 1));
        }

        return packed;
    }

    private static byte[] Bits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i])
                bytes[i >> 3] |= (byte)(1 << (i & 7));
        return bytes;
    }

    private static bool[] Unbits(byte[] bytes, int count)
    {
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
            bits[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
        return bits;
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
        if (r.ReadInt32() is var version && version is < 1 or > Version)
            throw new InvalidDataException($"pack version {version}, not 1 to {Version}");
        var (zone, region, size, samples, cells) = (r.ReadInt32(), r.ReadString(), r.ReadDouble(), r.ReadInt32(), r.ReadInt32());
        var tiles = new List<TileEntry>();
        for (var n = r.ReadInt32(); n > 0; n--)
            tiles.Add(new TileEntry(new TileId(r.ReadInt32(), r.ReadInt32()), r.ReadInt64(), r.ReadInt32(), r.ReadBytes(16)));
        var files = new List<FileEntry>();
        for (var n = r.ReadInt32(); n > 0; n--)
            files.Add(new FileEntry(r.ReadString(), r.ReadInt64(), r.ReadInt32(), r.ReadBytes(16)));
        return new Header(zone, region, size, samples, cells, tiles, files, version);
    }

    public static byte[] ReadBlob(Stream file, long offset, int length)
    {
        var data = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(data);
        return data;
    }

    public static Tile ReadTile(Stream file, Header header, TileEntry entry) => DecodeTile(header, entry, ReadBlob(file, entry.Offset, entry.Length));

    /// A tile from its bytes as stored (read with ReadBlob), so the reading can be done under a lock and this outside it.
    public static Tile DecodeTile(Header header, TileEntry entry, byte[] blob)
    {
        using var brotli = new BrotliStream(new MemoryStream(blob), CompressionMode.Decompress);
        using var r = new BinaryReader(brotli);
        var (min, max, source) = (r.ReadSingle(), r.ReadSingle(), r.ReadString());
        var samples = header.Samples;
        int[] packed;
        if (header.Version >= 3 && r.ReadByte() == 1)
            packed = ReadAdaptive(r, samples);
        else if (header.Version >= 2)
            packed = Unplanes(r.ReadBytes(samples * samples * 2), samples);
        else
        {
            packed = new int[samples * samples];
            for (var z = 0; z < samples; z++)
            for (var x = 0; x < samples; x++)
            {
                int zig = r.ReadUInt16();
                packed[z * samples + x] = Predict(packed, samples, x, z) + ((zig >> 1) ^ -(zig & 1));
            }
        }

        var heights = new float[packed.Length];
        for (var i = 0; i < packed.Length; i++)
            heights[i] = min + packed[i] / 10f;
        return new Tile(entry.Id, min, max, source, heights, r.ReadBytes(header.CoverCells * header.CoverCells));
    }
}
