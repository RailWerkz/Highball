using System.IO.Compression;

namespace Highball.Routes;

/// The route's network in brief, for what draws or measures all of it (the Planner's map) without loading it: Network/Overview.bin,
/// written with the network's cells (RouteNetwork.Save), per cell the ways stored in it as polylines thinned to within Tolerance
/// metres (Douglas-Peucker), and each kind's length. A cell's part is worked out again only when the cell's written; the rest kept as
/// they were (docs/cell-routes.md, Streaming).
///
/// The file, Brotli (quality 5): uint magic "HBNO", int version, string the network index's grid key, varint cells; per cell: string
/// its name, varint kinds then per kind (byte rail, byte class, double metres), varint polylines then per polyline (byte rail, byte
/// class, varint points, then the points as zigzag deltas of whole 1e-6 degrees, lat then lon).
public sealed class NetworkOverview
{
    public const string Name = "Overview.bin";
    private const uint Magic = 0x4F4E4248; // "HBNO"
    private const int Version = 1;

    /// How far a thinned polyline may stray from the way, metres: rail closer than roads.
    private const double RailTolerance = 5.0, RoadTolerance = 15.0;

    public sealed record Polyline(bool Rail, int Class, double[] Lat, double[] Lon);

    public sealed class Cell
    {
        public List<Polyline> Lines { get; } = [];
        public Dictionary<(bool Rail, int Class), double> Lengths { get; } = [];
    }

    public Dictionary<string, Cell> Cells { get; } = new(StringComparer.Ordinal);

    public string Grid { get; private set; } = "";

    public static string PathIn(RouteInfo route) => Path.Combine(NetworkCells.FolderIn(route), Name);

    /// One cell's part, from the ways stored in it.
    public static Cell Of(IEnumerable<NetworkWay> ways)
    {
        var cell = new Cell();
        foreach (var way in ways)
        {
            var n = Math.Min(way.Lat.Length, way.Lon.Length);
            if (n < 2)
                continue;
            var length = 0.0;
            for (var i = 1; i < n; i++)
                length += Metres(way.Lat[i - 1], way.Lon[i - 1], way.Lat[i], way.Lon[i]);
            cell.Lengths[(way.Rail, way.Class)] = cell.Lengths.GetValueOrDefault((way.Rail, way.Class)) + length;
            var keep = Thin(way.Lat, way.Lon, n, way.Rail ? RailTolerance : RoadTolerance);
            cell.Lines.Add(new Polyline(way.Rail, way.Class, [.. keep.Select(i => way.Lat[i])], [.. keep.Select(i => way.Lon[i])]));
        }

        return cell;
    }

    /// The points a polyline keeps (Douglas-Peucker on a local flat projection), first and last always.
    private static List<int> Thin(double[] lat, double[] lon, int n, double tolerance)
    {
        var cos = Math.Cos(lat[0] * Math.PI / 180.0);
        const double m = 111_320.0;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var (ax, ay, bx, by) = (lon[a] * cos * m, lat[a] * m, lon[b] * cos * m, lat[b] * m);
            var (dx, dy) = (bx - ax, by - ay);
            var len2 = dx * dx + dy * dy;
            var (worst, at) = (0.0, -1);
            for (var i = a + 1; i < b; i++)
            {
                var (px, py) = (lon[i] * cos * m, lat[i] * m);
                var t = len2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1) : 0;
                var (ex, ey) = (ax + dx * t - px, ay + dy * t - py);
                var d = ex * ex + ey * ey;
                if (d > worst)
                    (worst, at) = (d, i);
            }

            if (at >= 0 && worst > tolerance * tolerance)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }

        var kept = new List<int>();
        for (var i = 0; i < n; i++)
            if (keep[i])
                kept.Add(i);
        return kept;
    }

    private static double Metres(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// The overview kept, if there's one of this version on the network's grid as it is now.
    public static NetworkOverview? Read(RouteInfo route)
    {
        var path = PathIn(route);
        if (!File.Exists(path))
            return null;
        try
        {
            using var file = File.OpenRead(path);
            using var r = new BinaryReader(new BrotliStream(file, CompressionMode.Decompress));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version)
                return null;
            var overview = new NetworkOverview { Grid = r.ReadString() };
            if (overview.Grid != NetworkCells.GridKey(route))
                return null;
            for (var cells = r.Read7BitEncodedInt(); cells > 0; cells--)
            {
                var cell = new Cell();
                overview.Cells[r.ReadString()] = cell;
                for (var kinds = r.Read7BitEncodedInt(); kinds > 0; kinds--)
                    cell.Lengths[(r.ReadBoolean(), r.ReadByte())] = r.ReadDouble();
                for (var lines = r.Read7BitEncodedInt(); lines > 0; lines--)
                {
                    var (rail, cls, n) = (r.ReadBoolean(), (int)r.ReadByte(), r.Read7BitEncodedInt());
                    var (lat, lon) = (new double[n], new double[n]);
                    long qa = 0, qo = 0;
                    for (var i = 0; i < n; i++)
                    {
                        qa += Unzig(r.Read7BitEncodedInt64());
                        qo += Unzig(r.Read7BitEncodedInt64());
                        (lat[i], lon[i]) = (qa * 1e-6, qo * 1e-6);
                    }

                    cell.Lines.Add(new Polyline(rail, cls, lat, lon));
                }
            }

            return overview;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
        {
            RailWerkz.Core.Logging.Log.Warn($"Network overview: unreadable ({e.Message})");
            return null;
        }
    }

    public void Write(RouteInfo route)
    {
        Grid = NetworkCells.GridKey(route);
        using var memory = new MemoryStream();
        using (var w = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Grid);
            w.Write7BitEncodedInt(Cells.Count);
            foreach (var (name, cell) in Cells.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                w.Write(name);
                w.Write7BitEncodedInt(cell.Lengths.Count);
                foreach (var ((rail, cls), metres) in cell.Lengths)
                {
                    w.Write(rail);
                    w.Write((byte)cls);
                    w.Write(metres);
                }

                w.Write7BitEncodedInt(cell.Lines.Count);
                foreach (var line in cell.Lines)
                {
                    w.Write(line.Rail);
                    w.Write((byte)line.Class);
                    w.Write7BitEncodedInt(line.Lat.Length);
                    long qa = 0, qo = 0;
                    for (var i = 0; i < line.Lat.Length; i++)
                    {
                        var (a, o) = ((long)Math.Round(line.Lat[i] * 1e6), (long)Math.Round(line.Lon[i] * 1e6));
                        w.Write7BitEncodedInt64(Zig(a - qa));
                        w.Write7BitEncodedInt64(Zig(o - qo));
                        (qa, qo) = (a, o);
                    }
                }
            }
        }

        var raw = memory.ToArray();
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, packed, out var written, 5, 22))
            throw new IOException("Network overview: couldn't compress");
        NetworkCells.WriteAtomic(PathIn(route), packed[..written]);
    }

    private static long Zig(long v) => (v << 1) ^ (v >> 63);
    private static long Unzig(long v) => (long)((ulong)v >> 1) ^ -(v & 1);
}
