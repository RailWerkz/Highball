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

    /// What's worked out from the guides and the track: made again whenever either changes, off the main thread (it goes
    /// through the whole route), and swapped in whole when it's done, the last one used meanwhile.
    private sealed class Found
    {
        public readonly Dictionary<long, (double X, double Z)> Nodes = [];
        public readonly Dictionary<long, List<(long Other, double Length)>> Links = [];
        public readonly List<(long A, long B, bool Done)> Segments = [];
        /// Every guide segment's class, and whether any built track (hand-laid or drawn) already runs along it.
        public readonly List<(int Class, bool Covered)> Classes = [];
        /// Each guide segment's class, by its two nodes (either way round).
        public readonly Dictionary<(long, long), int> SegmentClass = [];
        public readonly List<YardArea> Areas = [];
        public double GuideLength, DoneLength;
    }

    private Found _found = new();
    private Task<Found>? _finding;
    private Dictionary<long, (double X, double Z)> _nodes => _found.Nodes;
    private Dictionary<long, List<(long Other, double Length)>> _links => _found.Links;
    private List<(long A, long B, bool Done)> _segments => _found.Segments;
    private List<(int Class, bool Covered)> _classes => _found.Classes;

    /// How close any built track has to be to a mapped segment for the class overlay to leave the map line out.
    private const double CoveredWithin = 3.5;
    private int _version = -1;
    private NetworkLines? _lines;

    public List<YardArea> Areas => _found.Areas;
    public double GuideLength => _found.GuideLength;
    public double DoneLength => _found.DoneLength;

    public YardGuides(RouteNetwork network, RouteProjection projection)
    {
        _network = network;
        _projection = projection;
    }

    /// Brought up to date with the network and the lines, if they've changed: the first time at once, after that in the
    /// background (the last found is used until it's done).
    public void Refresh(NetworkLines lines)
    {
        if (_finding is { IsCompleted: true } done)
        {
            _finding = null;
            if (done.IsCompletedSuccessfully)
                _found = done.Result;
            else
                RailWerkz.Core.Logging.Log.Error($"Yard guides: {done.Exception?.InnerException?.Message}");
        }

        if ((_version == _network.Version && _lines == lines) || _finding is not null)
            return;
        var first = _lines is null;
        (_version, _lines) = (_network.Version, lines);
        // Edits replace ways rather than change them, so a list of them now is a snapshot.
        var ways = _network.Ways.Values.ToList();
        var projection = _projection;
        if (first)
            _found = Find(ways, lines, projection);
        else
            _finding = Task.Run(() => Find(ways, lines, projection));
    }

    private static Found Find(List<NetworkWay> ways, NetworkLines lines, RouteProjection projection)
    {
        var found = new Found();
        FindAreas(found, ways, projection);

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

        // Where any built track runs: hand-laid, drawn with the Pen, or solved from the map.
        var builtPoints = new Dictionary<(int, int), List<(double X, double Z)>>();
        foreach (var line in lines.Lines.Where(l => l.Rail))
            for (var p = 0; p < line.Count; p++)
            {
                var key = ((int)Math.Floor(line.X[p] / DoneCell), (int)Math.Floor(line.Z[p] / DoneCell));
                if (!builtPoints.TryGetValue(key, out var list))
                    builtPoints[key] = list = [];
                list.Add((line.X[p], line.Z[p]));
            }

        bool NearBuilt(double x, double z)
        {
            var (cx, cz) = ((int)Math.Floor(x / DoneCell), (int)Math.Floor(z / DoneCell));
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (builtPoints.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (var q in list)
                            if ((q.X - x) * (q.X - x) + (q.Z - z) * (q.Z - z) < CoveredWithin * CoveredWithin)
                                return true;
            return false;
        }

        foreach (var way in ways.Where(RouteNetwork.IsGuide))
        {
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                found.Nodes[way.Nodes[i]] = (p.East, -p.North);
            }

            for (var i = 0; i + 1 < way.Nodes.Length; i++)
            {
                var (a, b) = (way.Nodes[i], way.Nodes[i + 1]);
                var (pa, pb) = (found.Nodes[a], found.Nodes[b]);
                var length = Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Z - pa.Z) * (pb.Z - pa.Z));
                Link(found, a, b, length);
                Link(found, b, a, length);

                // Long map segments are judged a piece at a time: done only if most of it is.
                var (hits, tries) = (0, Math.Max(2, (int)(length / 10.0)));
                for (var k = 0; k <= tries; k++)
                    if (NearLaid(pa.X + (pb.X - pa.X) * k / tries, pa.Z + (pb.Z - pa.Z) * k / tries))
                        hits++;
                var done = hits > tries * 0.7;
                found.Segments.Add((a, b, done));
                var covered = 0;
                for (var k = 0; k <= tries; k++)
                    if (NearBuilt(pa.X + (pb.X - pa.X) * k / tries, pa.Z + (pb.Z - pa.Z) * k / tries))
                        covered++;
                found.Classes.Add((way.Class, covered > tries * 0.7));
                found.SegmentClass[(a, b)] = found.SegmentClass[(b, a)] = way.Class;
                found.GuideLength += length;
                if (done)
                    found.DoneLength += length;
            }
        }

        return found;
    }

    /// The class of the mapped track a place on a guide is on, if known.
    public RailClass? ClassAt(GuidePlace place) =>
        _found.SegmentClass.TryGetValue((place.A, place.B), out var c) ? (RailClass)c : null;

    private static void Link(Found found, long from, long to, double length)
    {
        if (!found.Links.TryGetValue(from, out var list))
            found.Links[from] = list = [];
        list.Add((to, length));
    }

    private static void FindAreas(Found found, List<NetworkWay> ways, RouteProjection projection)
    {
        var yards = ways.Where(w => w.Rail && w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length && w.Tags.GetValueOrDefault("service") == "yard" && !w.Tags.ContainsKey(RouteNetwork.ExactTag)).ToList();
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
                    var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                    (sx, sz, count) = (sx + p.East, sz - p.North, count + 1);
                    if (i > 0)
                    {
                        var q = projection.Forward(new GeoPoint(way.Lat[i - 1], way.Lon[i - 1]));
                        length += Math.Sqrt((p.East - q.East) * (p.East - q.East) + (p.North - q.North) * (p.North - q.North));
                    }
                }

            var junctions = group.SelectMany(w => w.Nodes).Count(n => byNode[n].Count > 1) / 2;
            found.Areas.Add(new YardArea(seed.Id, [.. group.Select(w => w.Id)], length, junctions, sx / count, sz / count, group.All(RouteNetwork.IsGuide)));
        }

        found.Areas.Sort((a, b) => b.Length.CompareTo(a.Length));
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
        // No further round than a few times as far as the places are apart: a walk that can only get there the long way (a
        // switch it would have to back through) isn't what's meant, and looking for one goes through the whole route, every
        // frame the pointer moves.
        var straight = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Z - start.Z) * (end.Z - start.Z));
        var farthest = straight * PathDetour + PathSlack;
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
            if (cost > distance[state] || cost >= bestCost || cost > farthest)
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

    /// How much further than straight a walk along the guides may go (times as far, and metres more) before it's given up.
    private const double PathDetour = 3.0;
    private const double PathSlack = 200.0;

    /// The sharpest a walk along the guides may turn from one segment to the next (the cosine of 35 degrees).
    private const double CosMaxTurn = 0.82;

    /// The mapped track not yet built over, dashed in its class's colour: what the map says each track is, beside the
    /// class overlay's solid lines for the track that's built.
    public void DrawClasses(ImDrawListPtr draw, Func<double, double, float2?> project, double cx, double cz, double reach, float2 screen)
    {
        var (dash, gap) = (Style.S(7f), Style.S(5f));
        for (var s = 0; s < _segments.Count; s++)
        {
            var (cls, covered) = _classes[s];
            if (covered)
                continue;
            var (a, b, _) = _segments[s];
            var (pa, pb) = (_nodes[a], _nodes[b]);
            if ((Math.Abs(pa.X - cx) > reach && Math.Abs(pb.X - cx) > reach) || (Math.Abs(pa.Z - cz) > reach && Math.Abs(pb.Z - cz) > reach))
                continue;
            var color = RailColours.Of(cls).WithAlpha(0.85f);
            var pieces = Math.Clamp((int)(Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Z - pa.Z) * (pb.Z - pa.Z)) / 20.0), 1, 32);
            float2? previous = null;
            for (var k = 0; k <= pieces; k++)
            {
                var at = project(pa.X + (pb.X - pa.X) * k / pieces, pa.Z + (pb.Z - pa.Z) * k / pieces);
                if (previous is { } p0 && at is { } p1 && Clip(p0, p1, screen) is var (from, to))
                {
                    // Dashes counted whole along the clipped piece: a far point projected millions of pixels off the
                    // screen can't make this loop run long (or, with float steps, forever).
                    var d = to - from;
                    var length = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
                    var period = dash + gap;
                    var count = Math.Min((int)MathF.Ceiling(length / period), 2000);
                    for (var n = 0; n < count; n++)
                    {
                        var t0 = n * period;
                        var t1 = MathF.Min(t0 + dash, length);
                        if (t1 > t0)
                            draw.AddLine(from + d * (t0 / length), from + d * (t1 / length), color, Style.S(2f));
                    }
                }

                previous = at;
            }
        }
    }

    /// A screen segment cut to the screen (with a little margin), or null if none of it is on it (Liang-Barsky).
    private static (float2 From, float2 To)? Clip(float2 a, float2 b, float2 screen)
    {
        const float margin = 8f;
        var d = b - a;
        var (t0, t1) = (0f, 1f);
        bool Edge(float p, float q)
        {
            if (p == 0f)
                return q >= 0f;
            var r = q / p;
            if (p < 0f)
                t0 = MathF.Max(t0, r);
            else
                t1 = MathF.Min(t1, r);
            return t0 <= t1;
        }

        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            return null;
        if (!Edge(-d.X, a.X + margin) || !Edge(d.X, screen.X + margin - a.X) || !Edge(-d.Y, a.Y + margin) || !Edge(d.Y, screen.Y + margin - a.Y))
            return null;
        return (a + d * t0, a + d * t1);
    }

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

    /// The last path fitted, and its fit.
    private List<(double X, double Z)>? _lastPath;
    private (List<TrackPiece> Pieces, double Radius, bool Fitted) _lastFit;

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
        (_lastPath, _lastFit, _fitting) = (null, default, null);
    }

    public void Add(Click click) => _clicks.Add(click);

    /// The class of the mapped track the clicks are on, most of them (null: none on a guide): what Auto lays along it as.
    public RailClass? GuideClass(YardGuides guides) =>
        _clicks.Select(c => c.Guide is { } g ? guides.ClassAt(g) : null).OfType<RailClass>().GroupBy(c => c)
            .OrderByDescending(g => g.Count()).Select(g => (RailClass?)g.Key).FirstOrDefault();

    public void RemoveLast()
    {
        if (_clicks.Count > 0)
            _clicks.RemoveAt(_clicks.Count - 1);
        if (_clicks.Count == 0)
            Clear();
    }

    /// Works out the fit through the clicks and on to where the pointer is, if it's somewhere.
    /// The fit of a path being worked out in the background, for the preview while the pointer moves.
    private (List<(double X, double Z)> Path, Task<(List<TrackPiece> Pieces, double Radius, bool Fitted)> Fit)? _fitting;

    /// The path through the clicks (and the pointer, as the next) along the guides, fitted. wait: fitted now, as laying needs;
    /// otherwise a path that's changed is fitted in the background and the last fit shown until it's done, so the pointer
    /// moving never holds up a frame (a long path takes a while to fit).
    public void Update(YardGuides guides, Click? pointer, bool wait = false)
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

        // (Fitted again only when the path's changed: a long one takes a while, and this runs every frame.)
        if (_fitting is { Fit.IsCompleted: true } done)
        {
            _fitting = null;
            if (done.Fit.IsCompletedSuccessfully)
                (_lastPath, _lastFit) = (done.Path, done.Fit.Result);
        }

        if (_lastPath is null || !_lastPath.SequenceEqual(path))
        {
            if (wait)
                (_lastPath, _lastFit) = (path, TrackAlignment.FitPath(path));
            else if (_fitting is null)
                _fitting = (path, Task.Run(() => TrackAlignment.FitPath(path)));
        }

        var fit = _lastFit;
        if (_lastPath is null || (_lastFit.Pieces is null))
        {
            (Preview, Radius, Length, Unfitted) = (null, double.MaxValue, 0.0, false);
            return;
        }

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
        var keep = ControlPoints(pieces);
        var points = new List<LayPoint>(keep.Count) { _clicks[0].Point };
        for (var k = 1; k + 1 < keep.Count; k++)
        {
            var (x, z) = Alignment.Position(pieces, keep[k]);
            points.Add(new LayPoint(x, z));
        }

        points.Add(_clicks[^1].Point);
        return points;
    }

    /// How far the laid track may stray from the fit; the most a curve may turn, and the longest a straight may run,
    /// between two control points to start with; and the shortest span worth cutting again.
    private const double PointTolerance = 0.05;
    private const double PointTurn = 10.0 * Math.PI / 180.0;
    private const double PointSpan = 400.0;
    private const double PointMinSpan = 4.0;

    /// As few control points (distances along the fit) as lay the same track: one where each straight and curve meets the
    /// next, enough on each curve that no span turns more than PointTurn, then more only where the track laid through them
    /// (a centripetal Catmull-Rom spline, as an exact way is built) would stray from the fit by more than PointTolerance.
    internal static List<double> ControlPoints(IReadOnlyList<TrackPiece> pieces)
    {
        var total = Alignment.LengthOf(pieces);
        var keep = new List<double> { 0.0 };
        var u = 0.0;
        foreach (var piece in pieces)
        {
            var parts = Math.Max(1, (int)Math.Max(Math.Ceiling(Math.Abs(piece.Curvature) * piece.Length / PointTurn), Math.Ceiling(piece.Length / PointSpan)));
            for (var i = 1; i <= parts; i++)
                if (u + piece.Length * i / parts - keep[^1] >= PointMinSpan * 0.5)
                    keep.Add(u + piece.Length * i / parts);
            u += piece.Length;
        }

        // The end, exactly (a last point too near it is moved onto it).
        if (keep.Count > 1 && total - keep[^1] < PointMinSpan * 0.5)
            keep[^1] = total;
        else if (keep[^1] < total)
            keep.Add(total);

        // The fit itself, finely, to measure against.
        const double step = 0.5;
        var count = (int)Math.Ceiling(total / step);
        var fine = new (double X, double Z)[count + 1];
        for (var k = 0; k <= count; k++)
            fine[k] = Alignment.Position(pieces, Math.Min(total, k * step));

        for (var pass = 0; pass < 16; pass++)
        {
            var at = keep.Select(d => Alignment.Position(pieces, d)).ToArray();
            var next = new List<double> { keep[0] };
            var added = false;
            for (var i = 0; i + 1 < keep.Count; i++)
            {
                if (keep[i + 1] - keep[i] >= 2.0 * PointMinSpan && Strays(at, i, fine, keep[i], keep[i + 1], step) > PointTolerance)
                {
                    next.Add((keep[i] + keep[i + 1]) * 0.5);
                    added = true;
                }

                next.Add(keep[i + 1]);
            }

            keep = next;
            if (!added)
                break;
        }

        return keep;
    }

    /// How far the spline through the points strays from the fit over the span from point i to the next.
    private static double Strays((double X, double Z)[] at, int i, (double X, double Z)[] fine, double from, double to, double step)
    {
        var n = at.Length;
        (double X, double Z) P(int k) => k < 0 ? (2 * at[0].X - at[1].X, 2 * at[0].Z - at[1].Z)
            : k >= n ? (2 * at[n - 1].X - at[n - 2].X, 2 * at[n - 1].Z - at[n - 2].Z)
            : at[k];
        static double Knot(double t, (double X, double Z) a, (double X, double Z) b) =>
            t + Math.Max(Math.Sqrt(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z))), 1e-4);
        var (p0, p1, p2, p3) = (P(i - 1), P(i), P(i + 1), P(i + 2));
        var t1 = Knot(0.0, p0, p1);
        var t2 = Knot(t1, p1, p2);
        var t3 = Knot(t2, p2, p3);

        // The fit's points over the span (a little either side), searched in order as the spline's points go along it.
        var (first, last) = (Math.Max(0, (int)Math.Floor(from / step) - 2), Math.Min(fine.Length - 1, (int)Math.Ceiling(to / step) + 2));
        var j = first;
        var worst = 0.0;
        var samples = Math.Clamp((int)Math.Ceiling((to - from) / 1.0), 4, 800);
        for (var s = 1; s < samples; s++)
        {
            var t = t1 + (t2 - t1) * s / samples;
            (double X, double Z) Mix((double X, double Z) a, (double X, double Z) b, double ta, double tb) =>
                ((tb - t) / (tb - ta) * a.X + (t - ta) / (tb - ta) * b.X, (tb - t) / (tb - ta) * a.Z + (t - ta) / (tb - ta) * b.Z);
            var b1 = Mix(Mix(p0, p1, 0.0, t1), Mix(p1, p2, t1, t2), 0.0, t2);
            var b2 = Mix(Mix(p1, p2, t1, t2), Mix(p2, p3, t2, t3), t1, t3);
            var c = Mix(b1, b2, t1, t2);
            double Away(int k) => (fine[k].X - c.X) * (fine[k].X - c.X) + (fine[k].Z - c.Z) * (fine[k].Z - c.Z);
            while (j < last && Away(j + 1) <= Away(j))
                j++;
            // (Between two of the fit's points: the nearer segment's distance.)
            var best = Math.Sqrt(Away(j));
            foreach (var k in (int[])[j - 1, j])
                if (k >= first && k + 1 <= last)
                {
                    var (ax, az, bx, bz) = (fine[k].X, fine[k].Z, fine[k + 1].X, fine[k + 1].Z);
                    var (dx, dz) = (bx - ax, bz - az);
                    var f = Math.Clamp(((c.X - ax) * dx + (c.Z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-12), 0.0, 1.0);
                    var (ex, ez) = (ax + dx * f - c.X, az + dz * f - c.Z);
                    best = Math.Min(best, Math.Sqrt(ex * ex + ez * ez));
                }

            worst = Math.Max(worst, best);
        }

        return worst;
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
