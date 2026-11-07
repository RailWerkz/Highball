using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.Routes;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

/// A connected group of the route's yard tracks. Cleared: its map data is only a guide to lay track along, not built.
public sealed record YardArea(long Id, List<long> Ways, double Length, int Junctions, double X, double Z, bool Cleared);

/// An area marked on the ground: four corners in order, in world metres.
public readonly record struct GroundQuad((double X, double Z) A, (double X, double Z) B, (double X, double Z) C, (double X, double Z) D)
{
    public bool Contains(double x, double z)
    {
        static double Side((double X, double Z) p, (double X, double Z) q, double x, double z) => (q.X - p.X) * (z - p.Z) - (q.Z - p.Z) * (x - p.X);
        var (ab, bc, cd, da) = (Side(A, B, x, z), Side(B, C, x, z), Side(C, D, x, z), Side(D, A, x, z));
        return (ab >= 0 && bc >= 0 && cd >= 0 && da >= 0) || (ab <= 0 && bc <= 0 && cd <= 0 && da <= 0);
    }

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, Rgba color)
    {
        const int pieces = 24;
        (double X, double Z)[] corners = [A, B, C, D, A];
        for (var side = 0; side < 4; side++)
        {
            var (p, q) = (corners[side], corners[side + 1]);
            float2? previous = null;
            for (var k = 0; k <= pieces; k++)
            {
                var at = project(p.X + (q.X - p.X) * k / pieces, p.Z + (q.Z - p.Z) * k / pieces);
                if (previous is { } from && at is { } to)
                    draw.AddLine(from, to, color, Style.S(2.5f));
                previous = at;
            }
        }
    }
}

/// A place on a guide path: along the segment between two of its nodes.
public readonly record struct GuidePlace(long A, long B, double T, double X, double Z);

/// The yards in the map data, and the guide paths of those cleared to be laid by hand: drawn on the ground to follow,
/// fading where track's been laid along them, and walked by the Follow tool.
public sealed class YardGuides
{
    /// How close laid track has to be to a guide for that stretch to count as done.
    private const double DoneWithin = 3.0;
    private const double DoneCell = 8.0;

    private readonly RouteNetwork _network;
    private readonly RouteProjection _projection;
    private readonly Dictionary<long, (double X, double Z)> _nodes = [];
    private readonly Dictionary<long, List<(long Other, double Length)>> _links = [];
    private readonly List<(long A, long B, bool Done)> _segments = [];
    private int _version = -1;
    private NetworkLines? _lines;

    public List<YardArea> Areas { get; } = [];
    public double GuideLength { get; private set; }
    public double DoneLength { get; private set; }

    public YardGuides(RouteNetwork network, RouteProjection projection)
    {
        _network = network;
        _projection = projection;
    }

    public void Refresh(NetworkLines lines)
    {
        if (_version == _network.Version && _lines == lines)
            return;
        (_version, _lines) = (_network.Version, lines);

        FindAreas();
        _nodes.Clear();
        _links.Clear();
        _segments.Clear();

        // Where hand-laid track runs.
        var laid = new HashSet<(int, int)>();
        foreach (var line in lines.Lines.Where(l => l.Exact))
            for (var p = 0; p < line.Count; p++)
                laid.Add(((int)Math.Floor(line.X[p] / DoneCell), (int)Math.Floor(line.Z[p] / DoneCell)));
        var laidPoints = new Dictionary<(int, int), List<(double X, double Z)>>();
        foreach (var line in lines.Lines.Where(l => l.Exact))
            for (var p = 0; p < line.Count; p++)
            {
                var key = ((int)Math.Floor(line.X[p] / DoneCell), (int)Math.Floor(line.Z[p] / DoneCell));
                if (!laidPoints.TryGetValue(key, out var list))
                    laidPoints[key] = list = [];
                list.Add((line.X[p], line.Z[p]));
            }

        bool NearLaid(double x, double z)
        {
            var (cx, cz) = ((int)Math.Floor(x / DoneCell), (int)Math.Floor(z / DoneCell));
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (laidPoints.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (var q in list)
                            if ((q.X - x) * (q.X - x) + (q.Z - z) * (q.Z - z) < DoneWithin * DoneWithin)
                                return true;
            return false;
        }

        (GuideLength, DoneLength) = (0.0, 0.0);
        foreach (var way in _network.Ways.Values.Where(RouteNetwork.IsGuide))
        {
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                var p = _projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                _nodes[way.Nodes[i]] = (p.East, -p.North);
            }

            for (var i = 0; i + 1 < way.Nodes.Length; i++)
            {
                var (a, b) = (way.Nodes[i], way.Nodes[i + 1]);
                var (pa, pb) = (_nodes[a], _nodes[b]);
                var length = Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Z - pa.Z) * (pb.Z - pa.Z));
                Link(a, b, length);
                Link(b, a, length);

                // Long map segments are judged a piece at a time: done only if most of it is.
                var (hits, tries) = (0, Math.Max(2, (int)(length / 10.0)));
                for (var k = 0; k <= tries; k++)
                    if (NearLaid(pa.X + (pb.X - pa.X) * k / tries, pa.Z + (pb.Z - pa.Z) * k / tries))
                        hits++;
                var done = hits > tries * 0.7;
                _segments.Add((a, b, done));
                GuideLength += length;
                if (done)
                    DoneLength += length;
            }
        }
    }

    private void Link(long from, long to, double length)
    {
        if (!_links.TryGetValue(from, out var list))
            _links[from] = list = [];
        list.Add((to, length));
    }

    private void FindAreas()
    {
        Areas.Clear();
        var yards = _network.Ways.Values.Where(w => w.Rail && w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length && w.Tags.GetValueOrDefault("service") == "yard" && !w.Tags.ContainsKey(RouteNetwork.ExactTag)).ToList();
        var byNode = new Dictionary<long, List<NetworkWay>>();
        foreach (var way in yards)
            foreach (var node in way.Nodes)
            {
                if (!byNode.TryGetValue(node, out var list))
                    byNode[node] = list = [];
                list.Add(way);
            }

        var seen = new HashSet<long>();
        foreach (var seed in yards.OrderBy(w => w.Id))
        {
            if (!seen.Add(seed.Id))
                continue;
            var group = new List<NetworkWay>();
            var open = new Stack<NetworkWay>();
            open.Push(seed);
            while (open.TryPop(out var way))
            {
                group.Add(way);
                foreach (var node in way.Nodes)
                    foreach (var other in byNode[node])
                        if (seen.Add(other.Id))
                            open.Push(other);
            }

            var (length, sx, sz, count) = (0.0, 0.0, 0.0, 0);
            foreach (var way in group)
                for (var i = 0; i < way.Nodes.Length; i++)
                {
                    var p = _projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                    (sx, sz, count) = (sx + p.East, sz - p.North, count + 1);
                    if (i > 0)
                    {
                        var q = _projection.Forward(new GeoPoint(way.Lat[i - 1], way.Lon[i - 1]));
                        length += Math.Sqrt((p.East - q.East) * (p.East - q.East) + (p.North - q.North) * (p.North - q.North));
                    }
                }

            var junctions = group.SelectMany(w => w.Nodes).Count(n => byNode[n].Count > 1) / 2;
            Areas.Add(new YardArea(seed.Id, [.. group.Select(w => w.Id)], length, junctions, sx / count, sz / count, group.All(RouteNetwork.IsGuide)));
        }

        Areas.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    /// The guide under the pointer.
    public GuidePlace? PlaceAt(float2 mouse, Func<double, double, float2?> project, double cx, double cz, double reach, float within)
    {
        GuidePlace? found = null;
        var best = within;
        foreach (var (a, b, _) in _segments)
        {
            var (pa, pb) = (_nodes[a], _nodes[b]);
            if ((Math.Abs(pa.X - cx) > reach && Math.Abs(pb.X - cx) > reach) || (Math.Abs(pa.Z - cz) > reach && Math.Abs(pb.Z - cz) > reach))
                continue;
            if (project(pa.X, pa.Z) is not { } sa || project(pb.X, pb.Z) is not { } sb)
                continue;
            var (dx, dy) = (sb.X - sa.X, sb.Y - sa.Y);
            var t = Math.Clamp(((mouse.X - sa.X) * dx + (mouse.Y - sa.Y) * dy) / MathF.Max(dx * dx + dy * dy, 1e-3f), 0f, 1f);
            var (ex, ey) = (sa.X + dx * t - mouse.X, sa.Y + dy * t - mouse.Y);
            var d = MathF.Sqrt(ex * ex + ey * ey);
            if (d < best)
                (best, found) = (d, new GuidePlace(a, b, t, pa.X + (pb.X - pa.X) * t, pa.Z + (pb.Z - pa.Z) * t));
        }

        return found;
    }

    /// The guide nearest a place in the world.
    public GuidePlace? PlaceNear(double x, double z, double within)
    {
        GuidePlace? found = null;
        var best = within * within;
        foreach (var (a, b, _) in _segments)
        {
            var (pa, pb) = (_nodes[a], _nodes[b]);
            var (dx, dz) = (pb.X - pa.X, pb.Z - pa.Z);
            var t = Math.Clamp(((x - pa.X) * dx + (z - pa.Z) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (px, pz) = (pa.X + dx * t, pa.Z + dz * t);
            var d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < best)
                (best, found) = (d, new GuidePlace(a, b, t, px, pz));
        }

        return found;
    }

    /// The guide's node a place is right at, if it's within this of one.
    public long? NodeAt(GuidePlace place, double within)
    {
        foreach (var node in (long[])[place.A, place.B])
        {
            var p = _nodes[node];
            if ((p.X - place.X) * (p.X - place.X) + (p.Z - place.Z) * (p.Z - place.Z) < within * within)
                return node;
        }

        return null;
    }

    /// The shortest way along the guides from one place to another, as points; null if they aren't connected.
    public List<(double X, double Z)>? Path(GuidePlace from, GuidePlace to)
    {
        if ((from.A, from.B) == (to.A, to.B) || (from.A, from.B) == (to.B, to.A))
            return [(from.X, from.Z), (to.X, to.Z)];

        double Along(GuidePlace place, long node)
        {
            var p = _nodes[node];
            return Math.Sqrt((p.X - place.X) * (p.X - place.X) + (p.Z - place.Z) * (p.Z - place.Z));
        }

        // A train can't turn back through a switch, so the walk is over steps along a segment, each following on from the
        // last no sharper than MaxTurn.
        bool Follows((double X, double Z) a, (double X, double Z) b, (double X, double Z) c)
        {
            var (ux, uz, vx, vz) = (b.X - a.X, b.Z - a.Z, c.X - b.X, c.Z - b.Z);
            var lengths = Math.Sqrt((ux * ux + uz * uz) * (vx * vx + vz * vz));
            return lengths < 1e-9 || (ux * vx + uz * vz) / lengths >= CosMaxTurn;
        }

        var start = (X: from.X, Z: from.Z);
        var end = (X: to.X, Z: to.Z);
        var distance = new Dictionary<(long Before, long Node), double>
        {
            [(from.B, from.A)] = Along(from, from.A),
            [(from.A, from.B)] = Along(from, from.B),
        };
        var previous = new Dictionary<(long Before, long Node), (long, long)>();
        var open = new PriorityQueue<(long Before, long Node), double>();
        foreach (var (state, cost) in distance)
            open.Enqueue(state, cost);

        (long Before, long Node)? best = null;
        var bestCost = double.MaxValue;
        while (open.TryDequeue(out var state, out var cost))
        {
            if (cost > distance[state] || cost >= bestCost)
                continue;

            var (before, node) = state;
            var last = previous.ContainsKey(state) ? _nodes[before] : (start.X, start.Z);
            if ((node == to.A || node == to.B) && Follows(last, _nodes[node], (end.X, end.Z)) && cost + Along(to, node) < bestCost)
                (bestCost, best) = (cost + Along(to, node), state);
            if (!_links.TryGetValue(node, out var links))
                continue;
            foreach (var (other, length) in links)
            {
                if (other == before || !Follows(last, _nodes[node], _nodes[other]))
                    continue;
                var next = (node, other);
                if (cost + length < distance.GetValueOrDefault(next, double.MaxValue))
                {
                    distance[next] = cost + length;
                    previous[next] = state;
                    open.Enqueue(next, cost + length);
                }
            }
        }

        if (best is not { } reached)
            return null;

        var nodes = new List<long>();
        for (var at = reached; ; at = previous[at])
        {
            nodes.Add(at.Node);
            if (!previous.ContainsKey(at))
                break;
        }

        nodes.Reverse();
        List<(double, double)> path = [(from.X, from.Z), .. nodes.Select(n => _nodes[n]), (to.X, to.Z)];
        return path;
    }

    /// The sharpest a walk along the guides may turn from one segment to the next (the cosine of 35 degrees).
    private const double CosMaxTurn = 0.82;

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, double cx, double cz, double reach)
    {
        var (open, done) = (new Rgba(0.2f, 0.85f, 0.6f, 0.9f), new Rgba(0.2f, 0.85f, 0.6f, 0.2f));
        foreach (var (a, b, finished) in _segments)
        {
            var (pa, pb) = (_nodes[a], _nodes[b]);
            if ((Math.Abs(pa.X - cx) > reach && Math.Abs(pb.X - cx) > reach) || (Math.Abs(pa.Z - cz) > reach && Math.Abs(pb.Z - cz) > reach))
                continue;
            // (Long map segments follow the ground in lengths.)
            var pieces = Math.Clamp((int)(Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Z - pa.Z) * (pb.Z - pa.Z)) / 20.0), 1, 32);
            float2? previous = null;
            for (var k = 0; k <= pieces; k++)
            {
                var at = project(pa.X + (pb.X - pa.X) * k / pieces, pa.Z + (pb.Z - pa.Z) * k / pieces);
                if (previous is { } from && at is { } to)
                    draw.AddLine(from, to, finished ? done : open, Style.S(finished ? 1.5f : 2.5f));
                previous = at;
            }
        }
    }
}

/// Laying track along the guides: click along a guide, or start from a track, and the path between the clicks comes out as
/// straights and curves, fitted the same way as the solved track. Enter lays it; Backspace takes the last click off.
public sealed class FollowTool
{
    /// A click: where, on which guide if any, and what it joins.
    public readonly record struct Click(double X, double Z, GuidePlace? Guide, LayPoint Point, (double X, double Z)? Runs = null);

    /// The tightest a branch is led off a track, if there's room; and how far along the laid path it looks for it.
    private const double BranchRadius = 80.0;
    private const double BranchMin = 12.0;
    private const double BranchMax = 150.0;

    private readonly List<Click> _clicks = [];

    public int Count => _clicks.Count;
    public IReadOnlyList<TrackPiece>? Preview { get; private set; }
    public double Radius { get; private set; }
    public double Length { get; private set; }

    /// The path as drawn couldn't be fitted, so it would be laid as the map has it.
    public bool Unfitted { get; private set; }

    public void Clear()
    {
        _clicks.Clear();
        (Preview, Radius, Length, Unfitted) = (null, double.MaxValue, 0.0, false);
    }

    public void Add(Click click) => _clicks.Add(click);

    public void RemoveLast()
    {
        if (_clicks.Count > 0)
            _clicks.RemoveAt(_clicks.Count - 1);
        if (_clicks.Count == 0)
            Clear();
    }

    /// Works out the fit through the clicks and on to where the pointer is, if it's somewhere.
    public void Update(YardGuides guides, Click? pointer)
    {
        List<Click> all = [.. _clicks];
        if (pointer is { } p && all.Count > 0)
            all.Add(p);
        if (all.Count < 2)
        {
            (Preview, Radius, Length, Unfitted) = (null, double.MaxValue, 0.0, false);
            return;
        }

        var path = new List<(double X, double Z)> { (all[0].X, all[0].Z) };
        for (var i = 0; i + 1 < all.Count; i++)
        {
            var (a, b) = (all[i], all[i + 1]);
            var piece = a.Guide is { } ga && b.Guide is { } gb ? guides.Path(ga, gb) : null;
            piece ??= [(a.X, a.Z), (b.X, b.Z)];
            path.AddRange(piece.Skip(1));
        }

        var fit = TrackAlignment.FitPath(path);
        if (fit.Pieces.Count == 0)
        {
            (Preview, Radius, Length, Unfitted) = (null, double.MaxValue, 0.0, false);
            return;
        }

        var (pieces, radius) = (fit.Pieces, fit.Radius);
        if (all[0].Runs is { } leaving && Led(pieces, leaving) is { } led)
            (pieces, radius) = (led.Pieces, Math.Min(radius, led.Radius));
        if (all[^1].Runs is { } arriving && Led(Alignment.Reversed(pieces), arriving) is { } back)
            (pieces, radius) = (Alignment.Reversed(back.Pieces), Math.Min(radius, back.Radius));
        (Preview, Radius, Length, Unfitted) = (pieces, radius, Alignment.LengthOf(pieces), !fit.Fitted);
    }

    /// The path made to leave along the track it starts on, so it can be a switch: a curve from there onto the path, as near
    /// as is gentle enough, or as gentle as there's room for. Null if it already does, or nothing reaches it.
    private static (List<TrackPiece> Pieces, double Radius)? Led(List<TrackPiece> pieces, (double X, double Z) runs)
    {
        var total = Alignment.LengthOf(pieces);
        var start = Alignment.Position(pieces, 0.0);
        var (ax, az) = Alignment.Position(pieces, Math.Min(total, 20.0));
        (double X, double Z) leave = (ax - start.X) * runs.X + (az - start.Z) * runs.Z >= 0.0 ? runs : (-runs.X, -runs.Z);

        (double X, double Z) Heading(double u)
        {
            foreach (var piece in pieces)
            {
                if (u <= piece.Length)
                {
                    var h = piece.HeadingAt(Math.Max(u, 0.0));
                    return (Math.Cos(h), Math.Sin(h));
                }

                u -= piece.Length;
            }

            var end = pieces[^1].HeadingAt(pieces[^1].Length);
            return (Math.Cos(end), Math.Sin(end));
        }

        var first = Heading(0.0);
        if (Math.Abs(first.X * leave.Z - first.Z * leave.X) < 0.002 && first.X * leave.X + first.Z * leave.Z > 0.0)
            return null;

        (List<TrackPiece> Pieces, double Radius, double At)? best = null;
        for (var u = BranchMin; u <= Math.Min(BranchMax, total - 1.0); u += 2.0)
        {
            if (Alignment.Join(start, leave, Alignment.Position(pieces, u), Heading(u)) is not { } join)
                continue;
            if (best is null || join.Radius > best.Value.Radius)
                best = (join.Pieces, join.Radius, u);
            if (join.Radius >= BranchRadius && join.OneWay)
            {
                best = (join.Pieces, join.Radius, u);
                break;
            }
        }

        if (best is not { } chosen)
            return null;

        var laid = new List<TrackPiece>(chosen.Pieces);
        var skip = chosen.At;
        foreach (var piece in pieces)
        {
            if (skip >= piece.Length)
            {
                skip -= piece.Length;
                continue;
            }

            var (x, z) = piece.At(skip);
            laid.Add(piece with { X = x, Z = z, Heading = piece.HeadingAt(skip), Length = piece.Length - skip });
            skip = 0.0;
        }

        return (laid, chosen.Radius);
    }

    /// The points to lay: the fit every couple of metres, the ends what they join.
    public List<LayPoint>? Points()
    {
        if (Preview is not { } pieces || _clicks.Count < 2)
            return null;
        var total = Alignment.LengthOf(pieces);
        var count = Math.Max(2, (int)Math.Ceiling(total / 2.0));
        var points = new List<LayPoint>(count + 1) { _clicks[0].Point };
        for (var k = 1; k < count; k++)
        {
            var (x, z) = Alignment.Position(pieces, total * k / count);
            points.Add(new LayPoint(x, z));
        }

        points.Add(_clicks[^1].Point);
        return points;
    }

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, float2? mouse)
    {
        if (Preview is { } pieces)
        {
            var total = Alignment.LengthOf(pieces);
            var count = Math.Clamp((int)(total / 2.0), 2, 2000);
            float2? previous = null;
            var color = Unfitted ? Style.Stop : Radius < TrackQuality.MinRadius ? Style.Stop : Style.Caution;
            for (var k = 0; k <= count; k++)
            {
                var (x, z) = Alignment.Position(pieces, total * k / count);
                var at = project(x, z);
                if (previous is { } from && at is { } to)
                    draw.AddLine(from, to, color, Style.S(3f));
                previous = at;
            }
        }

        foreach (var click in _clicks)
            if (project(click.X, click.Z) is { } at)
                draw.AddCircleFilled(at, Style.S(5f), Rgba.White, 12);
    }
}
