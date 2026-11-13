using Highball.World;

namespace Highball.Builder;

/// What a pole goes by: nothing, or a road or track that it stands off.
public enum PoleSnap { Anywhere, Road, Track }

/// How clicks lay them: one each; a fence, on from the last at a gap; or a pen, a path of corners and curves laid whole at a gap.
public enum PoleDraw { One, Fence, Pen }

/// Where poles and loose ties go: one at a click, or a run of them at a gap. A utility pole faces along its line, the way its wires would
/// run, and a tie's section runs along it. A light faces the road or track it's beside, or to the left of the way a run's going, for its
/// arm to reach over. A tie put down near a track's open end starts from that end, going on the way the track was going.
public static class PoleRuns
{
    public const float MaxOffset = 40f;
    public const float MaxGap = 300f;
    private const double EndReach = 8.0;

    /// How close together they can go, and how many a click can lay.
    public static float MinGap(PlacedKind kind) => kind.IsTie() ? 0.3f : 3f;
    private static int MaxIn(PlacedKind kind) => kind.IsTie() ? 2000 : 500;

    /// How finely a pen's path is walked for these: ties close together would show its corners.
    public static double PenStep(PlacedKind kind) => kind.IsTie() ? 1.0 : 5.0;

    /// How far off a road or track a click may be and still go by it, which is to say past the furthest a pole may stand off one.
    private const double SnapReach = MaxOffset + 25.0;

    /// A way along a road or track a run may go, in hops between lines, is at most this many times as long as it is direct.
    private const double Detour = 4.0;
    private const double DetourLeeway = 300.0;

    public readonly record struct Pole(double X, double Z, double Heading);

    /// The poles a click would add, the way they follow, and why not if it won't do.
    public sealed record Plan(List<Pole> Poles, List<(double X, double Z)> Path, double Length, string? Problem);

    private static double HeadingOf(PlacedKind kind, double fx, double fz, double side) =>
        (kind.IsLightPole() ? Math.Atan2(-fx * side, fz * side) : Math.Atan2(fz, fx)) * 180.0 / Math.PI;

    private static (NetworkLine Line, float S, double Away)? Nearest(NetworkLines lines, PoleSnap snap, double x, double z) =>
        PlaceTool.NearestLine(lines, x, z, SnapReach, snap == PoleSnap.Track);

    /// How far a pole stands off a line's centre: a road's offset's from its edge.
    private static double Clear(NetworkLine line, double offset) => line.Rail ? offset : line.Width * 0.5 + offset;

    /// A single pole at a click, or null if it's to snap and there's nothing there to snap to.
    public static Pole? At(NetworkLines lines, PoleSnap snap, PlacedKind kind, float offset, double x, double z)
    {
        if (snap == PoleSnap.Anywhere && kind.IsTie() && TrackEnd(lines, x, z) is { } end)
            return end;
        if (snap == PoleSnap.Anywhere)
            return new Pole(x, z, 0.0);
        if (Nearest(lines, snap, x, z) is not { } on)
            return null;
        var (px, pz, _, fx, fz, rx, rz) = new Leg(on.Line, 0, 1).At(on.S);
        var side = (x - px) * rx + (z - pz) * rz >= 0.0 ? 1.0 : -1.0;
        var clear = Clear(on.Line, offset);
        return new Pole(px + rx * side * clear, pz + rz * side * clear, HeadingOf(kind, fx, fz, side));
    }

    /// The open end of a track near a place, if there is one, and the way on from it. Other lines joining there, it isn't open.
    public static Pole? TrackEnd(NetworkLines lines, double x, double z)
    {
        var joins = Joins(lines, true);
        Pole? found = null;
        var best = EndReach * EndReach;
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Disused || x < line.MinX - EndReach || x > line.MaxX + EndReach || z < line.MinZ - EndReach || z > line.MaxZ + EndReach)
                continue;
            foreach (var (point, s, way) in ((int, float, double)[])[(0, 0f, -1.0), (line.Count - 1, line.S[^1], 1.0)])
            {
                var away = (line.X[point] - x) * (line.X[point] - x) + (line.Z[point] - z) * (line.Z[point] - z);
                if (away >= best || line.Nodes.Any(n => n.Point == point && joins.ContainsKey(n.Node)) || OnOtherTrack(lines, line, line.X[point], line.Z[point]))
                    continue;
                var (px, pz, _, fx, fz, _, _) = new Leg(line, 0, 1).At(s);
                best = away;
                found = new Pole(px, pz, Math.Atan2(fz * way, fx * way) * 180.0 / Math.PI);
            }
        }

        return found;
    }

    /// Whether another track runs through a place: a line that ends on another isn't open, whether or not they share a node there.
    private static bool OnOtherTrack(NetworkLines lines, NetworkLine line, double x, double z)
    {
        foreach (var other in lines.Lines)
        {
            if (other == line || !other.Rail || other.Disused || x < other.MinX - 1.5 || x > other.MaxX + 1.5 || z < other.MinZ - 1.5 || z > other.MaxZ + 1.5)
                continue;
            for (var i = 0; i + 1 < other.Count; i++)
            {
                var (dx, dz) = (other.X[i + 1] - other.X[i], other.Z[i + 1] - other.Z[i]);
                var t = Math.Clamp(((x - other.X[i]) * dx + (z - other.Z[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (other.X[i] + dx * t - x, other.Z[i] + dz * t - z);
                if (ex * ex + ez * ez < 2.25)
                    return true;
            }
        }

        return false;
    }

    /// The poles along a pen's path, the first at its start.
    public static Plan Along(IReadOnlyList<LayPoint> points, PlacedKind kind, float gap) =>
        Along(points.Select(p => (p.X, p.Z)).ToList(), kind, gap, 1.0, first: true);

    /// The poles on from a place to a click, every gap, none beyond the click: straight there, or along the road or track and on the
    /// side the place is on.
    public static Plan Lay(NetworkLines lines, PoleSnap snap, PlacedKind kind, float offset, float gap, (double X, double Z) from, (double X, double Z) to)
    {
        if (snap == PoleSnap.Anywhere)
            return Along([from, to], kind, gap, 1.0, first: false);
        if (Nearest(lines, snap, from.X, from.Z) is not { } start || Nearest(lines, snap, to.X, to.Z) is not { } end)
            return new Plan([], [], 0.0, snap == PoleSnap.Road ? "No road here" : "No track here");
        if (Route(lines, start.Line, start.S, end.Line, end.S) is not { } centre)
            return new Plan([], [], 0.0, snap == PoleSnap.Road ? "Not along the same road" : "Not along the same track");
        // Which side it's on goes by the way the line's going a little way on: its first points may be next to each other.
        var (ax, az) = centre[0];
        var (bx, bz) = centre.FirstOrDefault(p => Math.Abs(p.X - ax) + Math.Abs(p.Z - az) > 0.5, centre[^1]);
        var (dx, dz) = (bx - ax, bz - az);
        var side = (from.X - ax) * -dz + (from.Z - az) * dx >= 0.0 ? 1.0 : -1.0;
        var path = OffsetBy(centre, side * Clear(start.Line, offset));
        // It starts at the pole it's going on from, wherever that is: it may have been dragged.
        path[0] = from;
        return Along(path, kind, gap, side, first: false);
    }

    /// Poles along a path, every gap on from its start, and at its start if first.
    private static Plan Along(List<(double X, double Z)> path, PlacedKind kind, double gap, double side, bool first)
    {
        var poles = new List<Pole>();
        var walked = 0.0;
        var next = first ? 0.0 : gap;
        var most = MaxIn(kind);
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (dx, dz) = (path[i + 1].X - path[i].X, path[i + 1].Z - path[i].Z);
            var length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 1e-6)
                continue;
            var (fx, fz) = (dx / length, dz / length);
            while (next <= walked + length)
            {
                if (poles.Count >= most)
                    return new Plan([], path, walked + length, $"Over {most} poles");
                var t = next - walked;
                poles.Add(new Pole(path[i].X + fx * t, path[i].Z + fz * t, HeadingOf(kind, fx, fz, side)));
                next += gap;
            }

            walked += length;
        }

        return new Plan(poles, path, walked, null);
    }

    /// A line of points moved across to its right (or left, if negative), each corner kept square to both its sides.
    private static List<(double X, double Z)> OffsetBy(List<(double X, double Z)> points, double by)
    {
        var moved = new List<(double X, double Z)>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var (ax, az) = points[Math.Max(i - 1, 0)];
            var (bx, bz) = points[Math.Min(i + 1, points.Count - 1)];
            var (dx, dz) = (bx - ax, bz - az);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
            moved.Add((points[i].X - dz / length * by, points[i].Z + dx / length * by));
        }

        return moved;
    }

    /// A stretch of one line's points, from one distance along it to another, either way.
    private static void Stretch(List<(double X, double Z)> into, NetworkLine line, float from, float to)
    {
        var leg = new Leg(line, 0, 1);
        void Add(float s)
        {
            var (x, z, _, _, _, _, _) = leg.At(s);
            if (into.Count == 0 || Math.Abs(into[^1].X - x) + Math.Abs(into[^1].Z - z) > 1e-3)
                into.Add((x, z));
        }

        Add(from);
        if (to >= from)
        {
            for (var i = 0; i < line.Count; i++)
                if (line.S[i] > from && line.S[i] < to)
                    Add(line.S[i]);
        }
        else
        {
            for (var i = line.Count - 1; i >= 0; i--)
                if (line.S[i] < from && line.S[i] > to)
                    Add(line.S[i]);
        }

        Add(to);
    }

    /// The centreline between two places on roads or tracks: along the one line if they're on it, or the shortest way there through
    /// the nodes the lines share. Null if there isn't one that's anything like direct.
    private static List<(double X, double Z)>? Route(NetworkLines lines, NetworkLine a, float sa, NetworkLine b, float sb)
    {
        var path = new List<(double X, double Z)>();
        if (a == b)
        {
            Stretch(path, a, sa, sb);
            return path;
        }

        var (ax, az, _, _, _, _, _) = new Leg(a, 0, 1).At(sa);
        var (bx, bz, _, _, _, _, _) = new Leg(b, 0, 1).At(sb);
        var limit = Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz)) * Detour + DetourLeeway;
        var joins = Joins(lines, a.Rail);

        // Shortest way through the joins: each is reached along one line from where the last left it.
        var best = new Dictionary<long, double>();
        var cameBy = new Dictionary<long, (long? From, NetworkLine Line, float S0, float S1)>();
        var queue = new PriorityQueue<long, double>();
        void Visit(long node, double cost, long? from, NetworkLine line, float s0, float s1)
        {
            if (cost > limit || (best.TryGetValue(node, out var known) && known <= cost))
                return;
            best[node] = cost;
            cameBy[node] = (from, line, s0, s1);
            queue.Enqueue(node, cost);
        }

        foreach (var (node, point) in a.Nodes)
            if (joins.ContainsKey(node))
                Visit(node, Math.Abs(a.S[point] - sa), null, a, sa, a.S[point]);

        long? arrived = null;
        var arrivedAt = 0f;
        var total = double.MaxValue;
        while (queue.TryDequeue(out var node, out var cost))
        {
            if (cost > best[node] || cost >= total)
                continue;
            foreach (var (line, point) in joins[node])
            {
                var s = line.S[point];
                if (line == b && cost + Math.Abs(s - sb) < total)
                    (arrived, arrivedAt, total) = (node, s, cost + Math.Abs(s - sb));
                foreach (var (other, otherPoint) in line.Nodes)
                    if (other != node && joins.ContainsKey(other))
                        Visit(other, cost + Math.Abs(line.S[otherPoint] - s), node, line, s, line.S[otherPoint]);
            }
        }

        if (arrived is not { } at)
            return null;

        var legs = new List<(NetworkLine Line, float S0, float S1)> { (b, arrivedAt, sb) };
        while (true)
        {
            var step = cameBy[at];
            legs.Add((step.Line, step.S0, step.S1));
            if (step.From is not { } from)
                break;
            at = from;
        }

        legs.Reverse();
        foreach (var (line, s0, s1) in legs)
            Stretch(path, line, s0, s1);
        return path;
    }

    private static NetworkLines? _joinsOf;
    private static readonly Dictionary<bool, Dictionary<long, List<(NetworkLine Line, int Point)>>> Joined = [];

    /// The nodes two or more lines share (the tracks' or the roads'), and where on each. Kept until the lines are built again.
    private static Dictionary<long, List<(NetworkLine Line, int Point)>> Joins(NetworkLines lines, bool rail)
    {
        if (_joinsOf != lines)
        {
            _joinsOf = lines;
            Joined.Clear();
        }

        if (Joined.TryGetValue(rail, out var known))
            return known;
        var all = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines.Lines)
        {
            if (line.Rail != rail || line.Disused)
                continue;
            foreach (var (node, point) in line.Nodes)
            {
                if (!all.TryGetValue(node, out var list))
                    all[node] = list = [];
                list.Add((line, point));
            }
        }

        return Joined[rail] = all.Where(p => p.Value.Count > 1).ToDictionary(p => p.Key, p => p.Value);
    }
}
