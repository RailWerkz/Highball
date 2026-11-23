using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Highball.Routes;

/// How a route's network is stored on disk: Network/ in the route's folder, one file per cell (a route tile, 2 km) that
/// has anything in it, and an index. A way is stored in the cell of its first point, a tagged node in its own cell; ways
/// with no position (what's left of one taken out by hand) are stored in Loose.bin. The index lists every cell file with a
/// hash of it and the other cells its ways reach into, so a later stage can find every way crossing a cell by reading the
/// index alone, then only the cell files that name that cell. RouteNetwork is the only user.
///
/// A cell file, Brotli-compressed (quality 5, window 22):
///   uint magic "HBNC", int format
///   varint ways; per way (by id): zigzag id delta, byte flags (1 rail), zigzag class, varint nodes, zigzag node id
///     deltas, varint lats, varint lons, then the lats and the lons (Degrees), varint tags, then key, value strings
///   varint nodes; per node (by id): zigzag id delta, lat, lon (Degrees), tags as above
/// A degree value is a varint: the zigzag delta from the last whole value in 1e-7 degrees, shifted left 2, with how it's
/// made from that whole q in the low 2 bits: 1 q / 1e7 (as JSON's read), 2 1e-9 * (100 q) (as the PBF reader works it
/// out), or 0 not whole (laid by hand), then its 8 bytes. Every value reads back bit for bit.
internal static class NetworkCells
{
    public const string Folder = "Network";
    public const string IndexName = "Index.json";

    /// The cell of everything without a position.
    public const string Loose = "Loose";

    /// Bumped when the layout of a cell file or the index changes: an index of another format isn't read.
    public const int Format = 1;

    private const uint Magic = 0x434E4248; // "HBNC"

    public sealed class Index
    {
        public int Format { get; set; }

        /// What the cells were cut by (GridKey): a route whose grid or zone changed has its every way in a cell again.
        public string Grid { get; set; } = "";

        public SortedDictionary<string, Cell> Cells { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class Cell
    {
        /// SHA-256 of the file, its first 16 bytes in hex.
        public string Hash { get; set; } = "";
        public long Bytes { get; set; }
        public int Ways { get; set; }
        public int Nodes { get; set; }

        /// The other cells this cell's ways pass through (by their points and the boxes of the segments between them).
        public List<string> Reach { get; set; } = [];
    }

    public static string FolderIn(RouteInfo route) => Path.Combine(route.Folder, Folder);
    public static string IndexIn(RouteInfo route) => Path.Combine(route.Folder, Folder, IndexName);
    public static string PathIn(RouteInfo route, string cell) => Path.Combine(route.Folder, Folder, cell + ".bin");

    /// A cell's file relative to the route's folder, as RouteNetwork.Files gives it.
    public static string Relative(string cell) => $"{Folder}/{cell}.bin";

    public static string GridKey(RouteInfo route)
    {
        if (route.Grid is not { } grid || route.Projection is null)
            return "none";
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return route.Zone is { } zone
            ? string.Create(c, $"zone {zone} tile {grid.TileSize:R}")
            : string.Create(c, $"origin {route.Origin.Latitude:R},{route.Origin.Longitude:R} tile {grid.TileSize:R}");
    }

    public static Index? ReadIndex(RouteInfo route)
    {
        var path = IndexIn(route);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<Index>(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"{path}: empty");
    }

    public static void WriteIndex(RouteInfo route, Index index) =>
        WriteAtomic(IndexIn(route), JsonSerializer.SerializeToUtf8Bytes(index));

    /// Written beside the file and moved over it, so a file is never seen half written.
    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data).AsSpan(0, 16));

    /// Which cell things are in, on one route's grid.
    public sealed class Locator(RouteInfo route)
    {
        private readonly RouteGrid? _grid = route.Projection is null ? null : route.Grid;
        private readonly RouteProjection? _projection = route.Projection;

        private TileId? Tile(double lat, double lon) =>
            _grid is null || !double.IsFinite(lat) || !double.IsFinite(lon) ? null : _grid.TileAt(_projection!.Forward(new GeoPoint(lat, lon)));

        public string Of(NetworkWay way) =>
            way.Lat.Length > 0 && way.Lon.Length > 0 && Tile(way.Lat[0], way.Lon[0]) is { } t ? t.ToString() : Loose;

        public string Of(NetworkNode node) => Tile(node.Lat, node.Lon) is { } t ? t.ToString() : Loose;

        /// Every cell a way's points and the boxes of its segments touch.
        public void Reach(NetworkWay way, HashSet<string> into)
        {
            var count = Math.Min(way.Lat.Length, way.Lon.Length);
            TileId? last = null;
            for (var i = 0; i < count; i++)
            {
                if (Tile(way.Lat[i], way.Lon[i]) is not { } t)
                    continue;
                if (last is { } p && p != t)
                {
                    var (i0, i1, j0, j1) = (Math.Min(p.I, t.I), Math.Max(p.I, t.I), Math.Min(p.J, t.J), Math.Max(p.J, t.J));
                    if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) <= 4096)
                        for (var j = j0; j <= j1; j++)
                        for (var k = i0; k <= i1; k++)
                            into.Add(new TileId(k, j).ToString());
                }

                into.Add(t.ToString());
                last = t;
            }
        }
    }

    // --- Cell files. ---

    public static byte[] Encode(IReadOnlyList<NetworkWay> ways, IReadOnlyList<NetworkNode> nodes) => Compress(Raw(ways, nodes));

    /// How hard cells are compressed (Brotli's 0 to 11). On World (9.1 MB raw in 1,570 cells): 4 5.9 MB, 5 5.6 MB in 0.2 s on
    /// every core for all of them, 11 5.2 MB in 3 s.
    private const int Quality = 5;

    private static byte[] Compress(byte[] raw)
    {
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, output, out var length, Quality, 22))
            throw new InvalidOperationException("Brotli failed");
        return output[..length];
    }

    private static byte[] Raw(IReadOnlyList<NetworkWay> ways, IReadOnlyList<NetworkNode> nodes)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Format);
            w.Write7BitEncodedInt(ways.Count);
            var id = 0L;
            foreach (var way in ways)
            {
                WriteSigned(w, way.Id - id);
                id = way.Id;
                w.Write((byte)(way.Rail ? 1 : 0));
                WriteSigned(w, way.Class);
                w.Write7BitEncodedInt(way.Nodes.Length);
                var node = 0L;
                foreach (var n in way.Nodes)
                {
                    WriteSigned(w, n - node);
                    node = n;
                }

                w.Write7BitEncodedInt(way.Lat.Length);
                w.Write7BitEncodedInt(way.Lon.Length);
                WriteDegrees(w, way.Lat);
                WriteDegrees(w, way.Lon);
                WriteTags(w, way.Tags);
            }

            w.Write7BitEncodedInt(nodes.Count);
            id = 0L;
            foreach (var node in nodes)
            {
                WriteSigned(w, node.Id - id);
                id = node.Id;
                WriteDegrees(w, [node.Lat, node.Lon]);
                WriteTags(w, node.Tags);
            }
        }

        return raw.ToArray();
    }

    public static (List<NetworkWay> Ways, List<NetworkNode> Nodes) Decode(byte[] data, string name)
    {
        using var raw = new MemoryStream();
        using (var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress))
            brotli.CopyTo(raw);
        raw.Position = 0;
        using var r = new BinaryReader(raw, System.Text.Encoding.UTF8);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException($"{name}: not a network cell");
        if (r.ReadInt32() is var format && format != Format)
            throw new InvalidDataException($"{name}: network cell format {format}, not {Format}");

        var wayCount = r.Read7BitEncodedInt();
        var ways = new List<NetworkWay>(wayCount);
        var id = 0L;
        for (var c = 0; c < wayCount; c++)
        {
            id += ReadSigned(r);
            var flags = r.ReadByte();
            var way = new NetworkWay { Id = id, Rail = (flags & 1) != 0, Class = (int)ReadSigned(r) };
            way.Nodes = new long[r.Read7BitEncodedInt()];
            var node = 0L;
            for (var i = 0; i < way.Nodes.Length; i++)
                way.Nodes[i] = node += ReadSigned(r);
            way.Lat = new double[r.Read7BitEncodedInt()];
            way.Lon = new double[r.Read7BitEncodedInt()];
            ReadDegrees(r, way.Lat);
            ReadDegrees(r, way.Lon);
            way.Tags = ReadTags(r);
            ways.Add(way);
        }

        var nodeCount = r.Read7BitEncodedInt();
        var nodes = new List<NetworkNode>(nodeCount);
        id = 0L;
        for (var c = 0; c < nodeCount; c++)
        {
            id += ReadSigned(r);
            var at = new double[2];
            ReadDegrees(r, at);
            nodes.Add(new NetworkNode { Id = id, Lat = at[0], Lon = at[1], Tags = ReadTags(r) });
        }

        return (ways, nodes);
    }

    private const double Step = 1e7;

    /// How v is made from a whole number q of 1e-7 degrees, bit for bit: 1 q / 1e7, 2 1e-9 * (100 q); 0 it isn't.
    private static int Whole(double v, out long q)
    {
        q = 0;
        if (!double.IsFinite(v) || Math.Abs(v) > 1000.0)
            return 0;
        q = (long)Math.Round(v * Step);
        var bits = BitConverter.DoubleToInt64Bits(v);
        if (BitConverter.DoubleToInt64Bits(q / Step) == bits)
            return 1;
        return BitConverter.DoubleToInt64Bits(1e-9 * (q * 100L)) == bits ? 2 : 0;
    }

    private static void WriteDegrees(BinaryWriter w, double[] values)
    {
        var last = 0L;
        foreach (var v in values)
        {
            var how = Whole(v, out var q);
            if (how == 0)
            {
                w.Write7BitEncodedInt64(0);
                w.Write(v);
                continue;
            }

            var d = q - last;
            w.Write7BitEncodedInt64((((d << 1) ^ (d >> 63)) << 2) | (long)how);
            last = q;
        }
    }

    private static void ReadDegrees(BinaryReader r, double[] into)
    {
        var last = 0L;
        for (var i = 0; i < into.Length; i++)
        {
            var u = r.Read7BitEncodedInt64();
            var how = (int)(u & 3);
            if (how == 0)
            {
                into[i] = r.ReadDouble();
                continue;
            }

            var z = (long)((ulong)u >> 2);
            last += (long)((ulong)z >> 1) ^ -(z & 1);
            into[i] = how == 1 ? last / Step : 1e-9 * (last * 100L);
        }
    }

    private static void WriteTags(BinaryWriter w, Dictionary<string, string> tags)
    {
        w.Write7BitEncodedInt(tags.Count);
        foreach (var (key, value) in tags)
        {
            w.Write(key);
            w.Write(value);
        }
    }

    private static Dictionary<string, string> ReadTags(BinaryReader r)
    {
        var count = r.Read7BitEncodedInt();
        var tags = new Dictionary<string, string>(count);
        for (var i = 0; i < count; i++)
            tags[r.ReadString()] = r.ReadString();
        return tags;
    }

    private static void WriteSigned(BinaryWriter w, long v) => w.Write7BitEncodedInt64((v << 1) ^ (v >> 63));

    private static long ReadSigned(BinaryReader r)
    {
        var u = r.Read7BitEncodedInt64();
        return (long)((ulong)u >> 1) ^ -(u & 1);
    }
}
