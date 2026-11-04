using System.IO.Compression;
using System.Runtime.InteropServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The lines shaped by hand: each one's straights and arcs by the line's key, kept with the route (TrackEdits.bin.gz) and
/// laid over the solved track, whatever's been solved since.
public sealed class TrackEdits
{
    private const int Version = 1;

    private readonly Dictionary<long, Alignment> _shapes = [];

    /// Changed since it was loaded or saved.
    public bool Unsaved { get; private set; }

    public bool Has(long key) => _shapes.ContainsKey(key);

    public Alignment? Get(long key) => _shapes.GetValueOrDefault(key);

    /// A line's shape by hand; null puts it back to as solved.
    public void Set(long key, Alignment? shape)
    {
        if (shape is null)
            _shapes.Remove(key);
        else
            _shapes[key] = shape;
        Unsaved = true;
    }

    /// As they are now, for a rebuild on another thread.
    public Dictionary<long, Alignment> Snapshot() => new(_shapes);

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "TrackEdits.bin.gz");

    public static TrackEdits Load(RouteInfo route)
    {
        var edits = new TrackEdits();
        var path = PathIn(route);
        if (!File.Exists(path))
            return edits;

        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(zip, 1 << 16));
            if (r.ReadInt32() != Version)
                throw new InvalidDataException("a newer format");
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var key = r.ReadInt64();
                var pieces = new TrackPiece[r.ReadInt32()];
                r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(pieces.AsSpan()));
                var u = new double[r.ReadInt32()];
                r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(u.AsSpan()));
                edits._shapes[key] = Alignment.Restored(pieces, u);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Log.Warn($"Track edits unreadable: {e.Message}");
            edits._shapes.Clear();
        }

        return edits;
    }

    public void Save(RouteInfo route)
    {
        var path = PathIn(route);
        var temporary = path + ".tmp";
        using (var file = File.Create(temporary))
        using (var zip = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(Version);
            w.Write(_shapes.Count);
            foreach (var (key, shape) in _shapes)
            {
                w.Write(key);
                w.Write(shape.Pieces.Count);
                w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(shape.Pieces)));
                w.Write(shape.U.Length);
                w.Write(MemoryMarshal.AsBytes(shape.U.AsSpan()));
            }
        }

        File.Move(temporary, path, true);
        Unsaved = false;
    }
}

/// A change to a line's shape, for undo: its hand shape before and after (null: as solved).
public sealed record TrackShapeEdit(long Key, Alignment? Before, Alignment? After);

/// Reshaping a line by its handles. Everything here works out a new shape and touches nothing; a handle at a junction
/// stays put, along with the way the line runs through it, so the track either side of a move still joins what it joined.
public static class TrackShaping
{
    /// Handles on a line are at least this far apart, unless put there by hand.
    private const float HandleSpacing = 25f;
    /// How near the pointer has to be to the mapped line or the straight between two handles for a handle to snap to it.
    private const double SnapReach = 0.75;

    /// What a change would come to: the new shape, its tightest curve, and how far from the map it goes at most.
    public sealed record Result(Alignment Shape, double Radius, double OffMap);

    /// A line's handles: its ends, its junctions, and the joints between its pieces.
    public static List<int> Handles(NetworkLine line, Func<long, bool> junction, IEnumerable<int> extra)
    {
        var fixedPoints = new SortedSet<int> { 0, line.Count - 1 };
        foreach (var (node, point) in line.Nodes)
            if (junction(node))
                fixedPoints.Add(point);
        var handles = new SortedSet<int>(fixedPoints);
        foreach (var p in extra)
            if (p > 0 && p < line.Count - 1)
                handles.Add(p);

        var alignment = line.Alignment!;
        var (walked, at) = (0.0, 0);
        foreach (var piece in alignment.Pieces)
        {
            walked += piece.Length;
            while (at < line.Count - 1 && alignment.U[at] < walked)
                at++;
            if (handles.All(p => MathF.Abs(line.S[p] - line.S[at]) >= HandleSpacing))
                handles.Add(at);
        }

        return [.. handles];
    }

    private static (double X, double Z) Direction(double heading) => (Math.Cos(heading), Math.Sin(heading));

    private static (double X, double Z) Unit(double x, double z)
    {
        var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-9);
        return (x / length, z / length);
    }

    private static Alignment Copy(Alignment shape) => Alignment.Restored([.. shape.Pieces], (double[])shape.U.Clone());

    /// Where a handle goes for the pointer at (x, z): on to the straight between its neighbours, or the mapped line, if
    /// it's that close.
    public static (double X, double Z) Snapped(NetworkLine line, List<int> handles, int point, double x, double z)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return (x, z);
        var (a, b) = (handles[i - 1], handles[i + 1]);
        var (dx, dz) = Unit(line.X[b] - line.X[a], line.Z[b] - line.Z[a]);
        var along = (x - line.X[a]) * dx + (z - line.Z[a]) * dz;
        var (cx, cz) = (line.X[a] + dx * along, line.Z[a] + dz * along);
        if ((cx - x) * (cx - x) + (cz - z) * (cz - z) < SnapReach * SnapReach)
            return (cx, cz);

        var (mx, mz) = (line.MappedX, line.MappedZ);
        var (best, bx, bz) = (SnapReach * SnapReach, x, z);
        for (var k = Math.Max(0, point - 20); k < Math.Min(mx.Length - 1, point + 20); k++)
        {
            var (sx, sz) = (mx[k + 1] - mx[k], mz[k + 1] - mz[k]);
            var t = Math.Clamp(((x - mx[k]) * sx + (z - mz[k]) * sz) / Math.Max(sx * sx + sz * sz, 1e-9), 0.0, 1.0);
            var (px, pz) = (mx[k] + sx * t, mz[k] + sz * t);
            var d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < best)
                (best, bx, bz) = (d, px, pz);
        }

        return (bx, bz);
    }

    /// A handle moved: the line from the handle before it to the handle after it laid in again through the new place,
    /// running there along the line between those two. Null if no curve goes that way.
    public static Result? Move(NetworkLine line, List<int> handles, int point, double x, double z)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return null;
        var (from, to) = (handles[i - 1], handles[i + 1]);
        var shape = line.Alignment!;
        var (start, end) = ((line.X[from], line.Z[from]), (line.X[to], line.Z[to]));
        var through = Unit(end.Item1 - start.Item1, end.Item2 - start.Item2);
        if (Alignment.Join(start, Direction(shape.HeadingAt(shape.U[from])), (x, z), through) is not { } first ||
            Alignment.Join((x, z), through, end, Direction(shape.HeadingAt(shape.U[to]))) is not { } second)
            return null;

        var (l1, l2) = (Alignment.LengthOf(first.Pieces), Alignment.LengthOf(second.Pieces));
        var along = new double[to - from + 1];
        for (var p = from; p <= to; p++)
            along[p - from] = p <= point
                ? l1 * (shape.U[p] - shape.U[from]) / Math.Max(shape.U[point] - shape.U[from], 1e-9)
                : l1 + l2 * (shape.U[p] - shape.U[point]) / Math.Max(shape.U[to] - shape.U[point], 1e-9);
        List<TrackPiece> pieces = [.. first.Pieces, .. second.Pieces];
        var result = Copy(shape);
        result.Splice(from, to, pieces, along);
        return Finish(line, result, Math.Min(first.Radius, second.Radius), from, to);
    }

    /// A handle taken out: one curve from the handle before it to the handle after it.
    public static Result? Remove(NetworkLine line, List<int> handles, int point)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return null;
        var (from, to) = (handles[i - 1], handles[i + 1]);
        var shape = line.Alignment!;
        if (Alignment.Join((line.X[from], line.Z[from]), Direction(shape.HeadingAt(shape.U[from])), (line.X[to], line.Z[to]), Direction(shape.HeadingAt(shape.U[to]))) is not { } curve)
            return null;
        var result = Copy(shape);
        result.Splice(from, to, curve.Pieces);
        return Finish(line, result, curve.Radius, from, to);
    }

    /// Exactly straight between two handles, and curves either side on to it from the handles beyond. A junction at either
    /// end keeps its place, and the line turns there if it has to; canTurn says which handles the curves may start from.
    public static Result? Straighten(NetworkLine line, List<int> handles, int a, int b, Func<int, bool> isJunction)
    {
        if (a > b)
            (a, b) = (b, a);
        var (i, j) = (handles.IndexOf(a), handles.IndexOf(b));
        if (i < 0 || j <= i)
            return null;

        var shape = line.Alignment!;
        var (pa, pb) = ((line.X[a], line.Z[a]), (line.X[b], line.Z[b]));
        var direction = Unit(pb.Item1 - pa.Item1, pb.Item2 - pa.Item2);
        var length = Math.Sqrt((pb.Item1 - pa.Item1) * (pb.Item1 - pa.Item1) + (pb.Item2 - pa.Item2) * (pb.Item2 - pa.Item2));
        var result = Copy(shape);
        var (radius, from, to) = (double.MaxValue, a, b);
        result.Splice(a, b, [new TrackPiece(pa.Item1, pa.Item2, Math.Atan2(direction.Z, direction.X), length, 0.0, true)]);

        if (i > 0 && !isJunction(a))
        {
            var before = handles[i - 1];
            if (Alignment.Join((line.X[before], line.Z[before]), Direction(shape.HeadingAt(shape.U[before])), pa, direction) is not { } curve)
                return null;
            result.Splice(before, a, curve.Pieces);
            (radius, from) = (Math.Min(radius, curve.Radius), before);
        }

        if (j < handles.Count - 1 && !isJunction(b))
        {
            var after = handles[j + 1];
            if (Alignment.Join(pb, direction, (line.X[after], line.Z[after]), Direction(shape.HeadingAt(shape.U[after]))) is not { } curve)
                return null;
            result.Splice(b, after, curve.Pieces);
            (radius, to) = (Math.Min(radius, curve.Radius), after);
        }

        return Finish(line, result, radius, from, to);
    }

    private static Result Finish(NetworkLine line, Alignment shape, double radius, int from, int to)
    {
        var off = 0.0;
        var pieces = shape.Pieces;
        for (var p = from; p <= to; p++)
        {
            var (x, z) = Alignment.Position(pieces, shape.U[p]);
            off = Math.Max(off, TrackQuality.OffMapped(line, x, z, p));
        }

        return new Result(shape, radius, off);
    }
}
