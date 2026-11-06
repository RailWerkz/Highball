using Brutal.ImGuiApi;
using Highball.Routes;
using RailWerkz.UI;

namespace Highball.Builder;

public enum RoadMode { Straight, Curved, Freeform, Remove }

/// A kind of road to lay: what it's tagged as, which is all that decides how it's built and drawn.
public sealed record RoadKind(string Name, RoadClass Class, string Highway, int Lanes, bool Oneway = false, string? Surface = null);

/// Changes roads, and bridges on any line, as edits of the network: the same undo and rebuild as track.
public sealed class RoadEditor
{
    /// A place on a way.
    public readonly record struct Hit(long Way, int Segment, double T, double X, double Z);

    private const int LongestBridgeChain = 12;

    private readonly RouteInfo _route;
    private readonly RouteNetwork _network;
    private readonly TrackEditor _tracks;
    private readonly List<(NetworkWay Way, double[] X, double[] Z)> _ways = [];
    private int _version = -1;

    public RoadEditor(RouteInfo route, RouteNetwork network, TrackEditor tracks)
    {
        (_route, _network, _tracks) = (route, network, tracks);
    }

    private void Refresh()
    {
        if (_version == _network.Version)
            return;

        _version = _network.Version;
        _ways.Clear();
        var projection = _route.Projection!;
        foreach (var way in _network.Ways.Values)
        {
            if (way.Nodes.Length < 2 || way.Lat.Length != way.Nodes.Length)
                continue;
            var (x, z) = (new double[way.Nodes.Length], new double[way.Nodes.Length]);
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                (x[i], z[i]) = (p.East, -p.North);
            }

            _ways.Add((way, x, z));
        }
    }

    public static bool IsRoad(NetworkWay way) => !way.Rail && way.Tags.ContainsKey("highway") && !way.Tags.ContainsKey(RouteNetwork.TrackMarkTag);

    public static bool IsBridge(NetworkWay way) => way.Tags.TryGetValue("bridge", out var bridge) && bridge != "no";

    /// The nearest place on a way that passes the test, within that many metres.
    public Hit? Nearest(double x, double z, double within, Func<NetworkWay, bool> wanted)
    {
        Refresh();
        _placed.Clear();
        Hit? best = null;
        var closest = within * within;
        foreach (var (way, xs, zs) in _ways)
        {
            if (!wanted(way))
                continue;
            for (var s = 0; s + 1 < xs.Length; s++)
            {
                var (dx, dz) = (xs[s + 1] - xs[s], zs[s + 1] - zs[s]);
                var t = Math.Clamp(((x - xs[s]) * dx + (z - zs[s]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (px, pz) = (xs[s] + dx * t, zs[s] + dz * t);
                var distance = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (distance < closest)
                    (closest, best) = (distance, new Hit(way.Id, s, t, px, pz));
            }
        }

        return best;
    }

    /// Where a road laid at this place would join: a road's point there, a road passing there, or just the ground.
    public LayPoint Snap(double x, double z, double within)
    {
        if (Nearest(x, z, within, IsRoad) is not { } hit)
            return new LayPoint(x, z);

        var (_, xs, zs) = _ways.First(w => w.Way.Id == hit.Way);
        foreach (var i in (int[])[hit.Segment, hit.Segment + 1])
        {
            if (Math.Sqrt((xs[i] - hit.X) * (xs[i] - hit.X) + (zs[i] - hit.Z) * (zs[i] - hit.Z)) < Math.Max(within, 4.0))
                return new LayPoint(xs[i], zs[i], Node: _network.Ways[hit.Way].Nodes[i]);
        }

        return new LayPoint(hit.X, hit.Z, Split: (hit.Way, hit.Segment));
    }

    private GeoPoint Geo(double x, double z) => _route.Projection!.Inverse(new RoutePoint(x, -z));

    private static NetworkWay Edited(NetworkWay way, long id, long[] nodes, double[] lat, double[] lon, Dictionary<string, string>? tags = null) => new()
    {
        Id = id, Rail = way.Rail, Class = way.Class, Nodes = nodes, Lat = lat, Lon = lon,
        Tags = new Dictionary<string, string>(tags ?? way.Tags) { [RouteNetwork.EditedTag] = "yes" },
    };

    private NetworkEdit? Commit(List<(long Id, NetworkWay? Before, NetworkWay? After)> changes)
    {
        if (changes.Count == 0)
            return null;
        var edit = new NetworkEdit(changes);
        _tracks.Apply(edit, undo: false);
        return edit;
    }

    /// Lays a road through these points. The first and last may join what they landed on.
    public NetworkEdit? Lay(IReadOnlyList<LayPoint> points, RoadKind kind)
    {
        if (points.Count < 2)
            return null;

        var changes = new Dictionary<long, (NetworkWay? Before, NetworkWay? After)>();
        NetworkWay Current(long id) => changes.TryGetValue(id, out var c) ? c.After! : _network.Ways[id];
        var (nodes, lat, lon) = (new long[points.Count], new double[points.Count], new double[points.Count]);
        for (var i = 0; i < points.Count; i++)
        {
            var geo = Geo(points[i].X, points[i].Z);
            (lat[i], lon[i]) = (geo.Lat, geo.Lon);
            if (points[i].Node is { } existing)
            {
                nodes[i] = existing;
                continue;
            }

            nodes[i] = _network.NewId();
            if (points[i].Split is not { } split || !_network.Ways.ContainsKey(split.Way))
                continue;

            // Into the way, on whichever of its segments as it now stands the point's nearest.
            var way = Current(split.Way);
            var at = NearestSegment(way, geo);
            var (n, la, lo) = (way.Nodes.ToList(), way.Lat.ToList(), way.Lon.ToList());
            n.Insert(at + 1, nodes[i]);
            la.Insert(at + 1, geo.Lat);
            lo.Insert(at + 1, geo.Lon);
            changes[split.Way] = (changes.TryGetValue(split.Way, out var c) ? c.Before : way, Edited(way, way.Id, [.. n], [.. la], [.. lo]));
        }

        var tags = new Dictionary<string, string> { ["highway"] = kind.Highway, ["lanes"] = kind.Lanes.ToString(), [RouteNetwork.EditedTag] = "yes" };
        if (kind.Oneway)
            tags["oneway"] = "yes";
        if (kind.Surface is { } surface)
            tags["surface"] = surface;
        var id = _network.NewId();
        changes[id] = (null, new NetworkWay { Id = id, Rail = false, Class = (int)kind.Class, Nodes = nodes, Lat = lat, Lon = lon, Tags = tags });
        return Commit([.. changes.Select(c => (c.Key, c.Value.Before, c.Value.After))]);
    }

    private static int NearestSegment(NetworkWay way, GeoPoint geo)
    {
        var (best, closest) = (0, double.MaxValue);
        var squeeze = Math.Cos(geo.Lat * Math.PI / 180.0);
        for (var s = 0; s + 1 < way.Nodes.Length; s++)
        {
            var (dx, dy) = ((way.Lon[s + 1] - way.Lon[s]) * squeeze, way.Lat[s + 1] - way.Lat[s]);
            var (px, py) = ((geo.Lon - way.Lon[s]) * squeeze, geo.Lat - way.Lat[s]);
            var t = Math.Clamp((px * dx + py * dy) / Math.Max(dx * dx + dy * dy, 1e-18), 0.0, 1.0);
            var distance = (px - dx * t) * (px - dx * t) + (py - dy * t) * (py - dy * t);
            if (distance < closest)
                (best, closest) = (s, distance);
        }

        return best;
    }

    public NetworkEdit? Remove(long way) =>
        _network.Ways.TryGetValue(way, out var before) ? Commit([(way, before, null)]) : null;

    /// Makes the line between two places on it a bridge: both on one way, or on ways joined end to end.
    public NetworkEdit? AddBridge(Hit a, Hit b, out string? refused)
    {
        refused = null;
        if (!_network.Ways.ContainsKey(a.Way) || !_network.Ways.ContainsKey(b.Way))
            return null;

        if (a.Way == b.Way)
        {
            var (from, to) = a.Segment + a.T <= b.Segment + b.T ? (a, b) : (b, a);
            var way = _network.Ways[a.Way];
            var changes = new List<(long, NetworkWay?, NetworkWay?)>();
            AddPieces(way, changes, Range(way, 0, from.Segment, from), Middle(way, from, to), Range(way, to, way.Nodes.Length - 1));
            return Commit(changes);
        }

        if (Chain(a.Way, b.Way) is not { } chain)
        {
            refused = "Both ends of a bridge have to be on the same line";
            return null;
        }

        var edits = new List<(long, NetworkWay?, NetworkWay?)>();
        var first = _network.Ways[a.Way];
        var last = _network.Ways[b.Way];
        var joinFirst = SharedEnd(first, _network.Ways[chain[1]]);
        var joinLast = SharedEnd(last, _network.Ways[chain[^2]]);
        if (joinFirst == 0)
            AddPieces(first, edits, null, Range(first, 0, a.Segment, a), Range(first, a, first.Nodes.Length - 1));
        else
            AddPieces(first, edits, Range(first, 0, a.Segment, a), Range(first, a, first.Nodes.Length - 1), null);
        if (joinLast == 0)
            AddPieces(last, edits, null, Range(last, 0, b.Segment, b), Range(last, b, last.Nodes.Length - 1));
        else
            AddPieces(last, edits, Range(last, 0, b.Segment, b), Range(last, b, last.Nodes.Length - 1), null);
        foreach (var id in chain.Skip(1).SkipLast(1))
            edits.Add((id, _network.Ways[id], Edited(_network.Ways[id], id, _network.Ways[id].Nodes, _network.Ways[id].Lat, _network.Ways[id].Lon, WithBridge(_network.Ways[id].Tags, true))));
        return Commit(edits);
    }

    private int SharedEnd(NetworkWay way, NetworkWay next) =>
        next.Nodes[0] == way.Nodes[0] || next.Nodes[^1] == way.Nodes[0] ? 0 : way.Nodes.Length - 1;

    /// A stretch of a way's points, from a point to a place on it, or from a place on it to a point.
    private (long[] N, double[] La, double[] Lo) Range(NetworkWay way, int from, int segment, Hit to)
    {
        var (n, la, lo) = (new List<long>(), new List<double>(), new List<double>());
        for (var i = from; i <= segment; i++)
            (n, la, lo) = Added(n, la, lo, way.Nodes[i], way.Lat[i], way.Lon[i]);
        return Ended(way, n, la, lo, to);
    }

    private (long[] N, double[] La, double[] Lo) Range(NetworkWay way, Hit from, int to)
    {
        var (n, la, lo) = (new List<long>(), new List<double>(), new List<double>());
        var (node, lat, lon) = PlaceOn(way, from);
        (n, la, lo) = Added(n, la, lo, node, lat, lon);
        for (var i = from.Segment + 1; i <= to; i++)
            if (way.Nodes[i] != node)
                (n, la, lo) = Added(n, la, lo, way.Nodes[i], way.Lat[i], way.Lon[i]);
        return ([.. n], [.. la], [.. lo]);
    }

    private (long[] N, double[] La, double[] Lo) Middle(NetworkWay way, Hit from, Hit to)
    {
        var (n, la, lo) = (new List<long>(), new List<double>(), new List<double>());
        var (node, lat, lon) = PlaceOn(way, from);
        (n, la, lo) = Added(n, la, lo, node, lat, lon);
        for (var i = from.Segment + 1; i <= to.Segment; i++)
            if (way.Nodes[i] != node)
                (n, la, lo) = Added(n, la, lo, way.Nodes[i], way.Lat[i], way.Lon[i]);
        return Ended(way, n, la, lo, to);
    }

    private static (List<long>, List<double>, List<double>) Added(List<long> n, List<double> la, List<double> lo, long node, double lat, double lon)
    {
        n.Add(node);
        la.Add(lat);
        lo.Add(lon);
        return (n, la, lo);
    }

    private (long[] N, double[] La, double[] Lo) Ended(NetworkWay way, List<long> n, List<double> la, List<double> lo, Hit to)
    {
        var (node, lat, lon) = PlaceOn(way, to);
        if (n.Count == 0 || n[^1] != node)
            Added(n, la, lo, node, lat, lon);
        return ([.. n], [.. la], [.. lo]);
    }

    private readonly Dictionary<Hit, long> _placed = [];

    /// The node at a place on a way: the point it's on if it's that close, or a new one (the same for every stretch that asks).
    private (long Node, double Lat, double Lon) PlaceOn(NetworkWay way, Hit at)
    {
        const double close = 1.5;
        var (_, xs, zs) = _ways.First(w => w.Way.Id == way.Id);
        foreach (var i in (int[])[at.Segment, at.Segment + 1])
            if (Math.Sqrt((xs[i] - at.X) * (xs[i] - at.X) + (zs[i] - at.Z) * (zs[i] - at.Z)) < close)
                return (way.Nodes[i], way.Lat[i], way.Lon[i]);

        var geo = Geo(at.X, at.Z);
        if (!_placed.TryGetValue(at, out var node))
            _placed[at] = node = _network.NewId();
        return (node, geo.Lat, geo.Lon);
    }

    /// Puts a way back as stretches: the first keeps its id, the bridge stretch alone is tagged. Stretches of one point are nothing.
    private void AddPieces(NetworkWay way, List<(long, NetworkWay?, NetworkWay?)> changes, (long[] N, double[] La, double[] Lo)? head,
        (long[] N, double[] La, double[] Lo)? bridge, (long[] N, double[] La, double[] Lo)? tail)
    {
        var keptId = false;
        void Piece((long[] N, double[] La, double[] Lo)? piece, bool isBridge)
        {
            if (piece is not { } p || p.N.Length < 2)
                return;
            var id = keptId ? _network.NewId() : way.Id;
            var after = Edited(way, id, p.N, p.La, p.Lo, WithBridge(way.Tags, isBridge ? true : IsBridge(way)));
            changes.Add((id, keptId ? null : way, after));
            keptId = true;
        }

        // On which end of the way the bridge lies, it's the stretch passed as bridge.
        Piece(head, false);
        Piece(bridge, true);
        Piece(tail, false);
    }

    private static Dictionary<string, string> WithBridge(Dictionary<string, string> tags, bool bridge)
    {
        var result = new Dictionary<string, string>(tags);
        if (bridge)
        {
            result["bridge"] = "yes";
            if (!result.ContainsKey("layer"))
                result["layer"] = "1";
        }
        else
        {
            result.Remove("bridge");
            result.Remove("layer");
        }

        return result;
    }

    /// The ways from one to another joined end to end, by the shortest way round, if they're near enough along the line.
    private List<long>? Chain(long from, long to)
    {
        var rail = _network.Ways[from].Rail;
        var byEnd = new Dictionary<long, List<NetworkWay>>();
        foreach (var way in _network.Ways.Values.Where(w => w.Nodes.Length >= 2 && w.Rail == rail))
        {
            foreach (var end in (long[])[way.Nodes[0], way.Nodes[^1]])
            {
                if (!byEnd.TryGetValue(end, out var list))
                    byEnd[end] = list = [];
                list.Add(way);
            }
        }

        var back = new Dictionary<long, long> { [from] = from };
        var frontier = new Queue<(long Id, int Depth)>();
        frontier.Enqueue((from, 0));
        while (frontier.TryDequeue(out var current))
        {
            if (current.Id == to)
            {
                var chain = new List<long> { to };
                while (chain[^1] != from)
                    chain.Add(back[chain[^1]]);
                chain.Reverse();
                return chain;
            }

            if (current.Depth >= LongestBridgeChain)
                continue;
            var way = _network.Ways[current.Id];
            foreach (var end in (long[])[way.Nodes[0], way.Nodes[^1]])
            {
                foreach (var next in byEnd.GetValueOrDefault(end, []))
                {
                    if (back.TryAdd(next.Id, current.Id))
                        frontier.Enqueue((next.Id, current.Depth + 1));
                }
            }
        }

        return null;
    }

    /// Takes a bridge out: the way there, and any bridge joined to it end to end, go back on the ground.
    public NetworkEdit? RemoveBridge(long id)
    {
        if (!_network.Ways.TryGetValue(id, out var start) || !IsBridge(start))
            return null;

        var seen = new HashSet<long> { id };
        var todo = new Stack<NetworkWay>();
        todo.Push(start);
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        while (todo.TryPop(out var way))
        {
            changes.Add((way.Id, way, Edited(way, way.Id, way.Nodes, way.Lat, way.Lon, WithBridge(way.Tags, false))));
            foreach (var next in _network.Ways.Values.Where(w => w.Rail == way.Rail && w.Nodes.Length >= 2 && IsBridge(w) && !seen.Contains(w.Id) &&
                         (w.Nodes[0] == way.Nodes[0] || w.Nodes[0] == way.Nodes[^1] || w.Nodes[^1] == way.Nodes[0] || w.Nodes[^1] == way.Nodes[^1])))
            {
                seen.Add(next.Id);
                todo.Push(next);
            }

            // A bridge carrying more than one line is one bridge: the lines beside this one come off with it.
            foreach (var beside in Beside(way).Where(w => seen.Add(w.Id)))
                todo.Push(beside);
        }

        return Commit(changes);
    }

    /// The other bridges within a track or two of this one, over the same stretch.
    private IEnumerable<NetworkWay> Beside(NetworkWay way)
    {
        const double near = 14.0;
        Refresh();
        var (_, xs, zs) = _ways.First(w => w.Way.Id == way.Id);
        foreach (var (other, ox, oz) in _ways)
        {
            if (other.Id == way.Id || other.Rail != way.Rail || !IsBridge(other))
                continue;
            var close = false;
            for (var i = 0; i < ox.Length && !close; i++)
                for (var k = 0; k < xs.Length && !close; k++)
                    close = (ox[i] - xs[k]) * (ox[i] - xs[k]) + (oz[i] - zs[k]) * (oz[i] - zs[k]) < near * near;
            if (close)
                yield return other;
        }
    }

    /// A way's points in world metres, to draw it picked.
    public (double[] X, double[] Z)? Shape(long id)
    {
        Refresh();
        foreach (var (way, x, z) in _ways)
            if (way.Id == id)
                return (x, z);
        return null;
    }
}

/// Drawing roads, after the road tools of city builders: Straight lays from click to click, Curved takes a start, a point the curve
/// pulls towards and an end, Freeform runs on from the last road's direction, and Remove takes out the road clicked. Each stretch is
/// laid as soon as its last click lands, and the next starts where it ended; Esc lets go. Shift keeps a straight to 15 degree steps.
public sealed class RoadTool
{
    public static readonly RoadKind[] Kinds =
    [
        new("Dirt track", RoadClass.Service, "track", 1, Surface: "dirt"),
        new("Service road", RoadClass.Service, "service", 1),
        new("Local, 2 lanes", RoadClass.Local, "residential", 2),
        new("Collector, 2 lanes", RoadClass.Minor, "tertiary", 2),
        new("Arterial, 2 lanes", RoadClass.Major, "secondary", 2),
        new("Arterial, 4 lanes", RoadClass.Major, "primary", 4),
        new("One way, 2 lanes", RoadClass.Major, "secondary", 2, Oneway: true),
        new("Highway, 4 lanes", RoadClass.Highway, "trunk", 4),
        new("Freeway side, 2 lanes", RoadClass.Highway, "motorway", 2, Oneway: true),
        new("Freeway side, 3 lanes", RoadClass.Highway, "motorway", 3, Oneway: true),
    ];

    private const double SampleEvery = 6.0;

    public RoadMode Mode = RoadMode.Straight;
    public int Kind = 2;

    /// Where the road being drawn has got to, the point its curve pulls towards, and which way it was going when it got there.
    public LayPoint? From { get; private set; }
    public (double X, double Z)? Control { get; private set; }
    public (double X, double Z)? Heading { get; private set; }

    public void Clear() => (From, Control, Heading) = (null, null, null);

    public bool Busy => From is not null;

    /// The point after a Shift's snap: the same distance away, turned to a step of 15 degrees.
    public (double X, double Z) Stepped(double x, double z)
    {
        if (From is not { } from)
            return (x, z);
        var (dx, dz) = (x - from.X, z - from.Z);
        var step = Math.PI / 12.0;
        var angle = Math.Round(Math.Atan2(dz, dx) / step) * step;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return (from.X + Math.Cos(angle) * length, from.Z + Math.Sin(angle) * length);
    }

    /// A click at this place. Returns the points to lay if it ends a stretch.
    public List<LayPoint>? Click(LayPoint at)
    {
        if (Mode == RoadMode.Remove)
            return null;
        if (From is not { } from)
        {
            From = at;
            return null;
        }

        if (Mode == RoadMode.Curved && Control is null)
        {
            Control = (at.X, at.Z);
            return null;
        }

        var points = Path(from, at);
        if (points.Count < 2)
            return null;

        var (a, b) = (points[^2], points[^1]);
        var length = Math.Max(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)), 1e-6);
        Heading = ((b.X - a.X) / length, (b.Z - a.Z) / length);
        From = at;
        Control = null;
        return points;
    }

    /// The road that would be laid to this place, for drawing it first.
    public List<LayPoint> Preview(LayPoint at) => From is { } from ? Path(from, at) : [];

    private List<LayPoint> Path(LayPoint from, LayPoint to)
    {
        var control = Mode switch
        {
            RoadMode.Curved => Control,
            RoadMode.Freeform when Heading is { } heading => Along(from, to, heading),
            _ => null,
        };
        if (control is not { } c)
            return [from, to];

        // A quadratic Bézier, a point every few metres, ends as they were clicked.
        var approximate = Distance(from.X, from.Z, c.X, c.Z) + Distance(c.X, c.Z, to.X, to.Z);
        var count = Math.Clamp((int)(approximate / SampleEvery), 2, 400);
        var points = new List<LayPoint> { from };
        for (var i = 1; i < count; i++)
        {
            var t = i / (double)count;
            var (u, v, w) = ((1 - t) * (1 - t), 2 * t * (1 - t), t * t);
            points.Add(new LayPoint(u * from.X + v * c.X + w * to.X, u * from.Z + v * c.Z + w * to.Z));
        }

        points.Add(to);
        return points;
    }

    /// Running on from a heading: the curve pulls straight ahead for as far as makes it about a circular arc.
    private static (double X, double Z) Along(LayPoint from, LayPoint to, (double X, double Z) heading)
    {
        var (dx, dz) = (to.X - from.X, to.Z - from.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
        var cosine = Math.Clamp((dx * heading.X + dz * heading.Z) / length, 0.2, 1.0);
        var reach = length * 0.5 / cosine;
        return (from.X + heading.X * reach, from.Z + heading.Z * reach);
    }

    private static double Distance(double ax, double az, double bx, double bz) => Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));

    /// What's about to be laid, over the view: the width of the road, its centre, and the curve's handle.
    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, LayPoint? at, float scale)
    {
        if (From is not { } from)
        {
            if (at is { } first && project(first.X, first.Z) is { } start)
                draw.AddCircleFilled(start, 5f * scale, first.Node is not null || first.Split is not null ? Style.Accent : Rgba.White);
            return;
        }

        if (at is not { } to)
            return;

        if (project(from.X, from.Z) is { } origin)
            draw.AddCircleFilled(origin, 5f * scale, Style.Accent);
        if (Control is { } c && project(c.X, c.Z) is { } handle)
        {
            draw.AddCircleFilled(handle, 4f * scale, Rgba.White);
            if (project(from.X, from.Z) is { } back)
                draw.AddLine(back, handle, Rgba.White.WithAlpha(0.5f), scale);
        }

        var half = Width(Kinds[Kind]) * 0.5;
        var points = Path(from, to);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var length = Math.Max(Distance(a.X, a.Z, b.X, b.Z), 1e-6);
            var (rx, rz) = (-(b.Z - a.Z) / length * half, (b.X - a.X) / length * half);
            if (project(a.X + rx, a.Z + rz) is { } p0 && project(b.X + rx, b.Z + rz) is { } p1 &&
                project(b.X - rx, b.Z - rz) is { } p2 && project(a.X - rx, a.Z - rz) is { } p3)
            {
                draw.AddQuadFilled(p0, p1, p2, p3, Style.Accent.WithAlpha(0.35f));
                draw.AddLine(p0, p1, Style.Accent, 1.5f * scale);
                draw.AddLine(p3, p2, Style.Accent, 1.5f * scale);
            }
        }
    }

    /// About how wide it'll be built, for the preview.
    public static double Width(RoadKind kind) => kind.Class switch
    {
        RoadClass.Highway => kind.Lanes * 3.6 + 4.2,
        RoadClass.Major => kind.Lanes * 3.4 + 1.0,
        RoadClass.Minor => kind.Lanes * 3.2 + 0.6,
        RoadClass.Local => kind.Lanes * 3.0,
        _ => kind.Highway == "track" ? 3.0 : 4.0,
    };
}
