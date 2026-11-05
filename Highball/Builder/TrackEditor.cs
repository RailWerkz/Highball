using Brutal.ImGuiApi;
using Highball.Routes;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

/// One change to the network, kept so it can be undone and redone: every way it touched, before and after (null
/// where the way didn't exist).
public sealed record NetworkEdit(List<(long Id, NetworkWay? Before, NetworkWay? After)> Ways);

/// Where a point of track being laid sits: on an existing control point (it joins there), partway along an existing
/// track (a new control point is put in, and a switch made there), or on open ground.
public readonly record struct LayPoint(double X, double Z, long? Node = null, (long Way, int Segment)? Split = null);

/// A way to lay along with others. From: the point of the first way it branches off, sharing its control point there.
/// Shared: its points that are another way's in the same lay, by that way's place in the list (an earlier one) and its point there.
public sealed record PlannedWay(List<LayPoint> Points, int? From = null, IReadOnlyList<(int Point, int Way, int Other)>? Shared = null);

/// Track editing in the editors, on the route's network: OpenStreetMap's rail ways, whose nodes are the control
/// points. Moving a node moves every way through it (so junctions stay joined); new track is a new way, joined to
/// whatever it starts or ends on. A selection of control points can be straightened, smoothed, thinned out, joined
/// into one way or deleted together. Every edited or new way is tagged so fetching OpenStreetMap again leaves it be.
public sealed class TrackEditor
{
    private const float PickPixels = 14f;
    private const double JoinGap = 50.0;     // the furthest apart two selected track ends can be and still be joined
    private const double SimplifyTolerance = 0.25;

    private readonly RouteInfo _route;
    private readonly RouteNetwork _network;
    private readonly Dictionary<long, (double X, double Z)> _nodes = [];
    private readonly List<NetworkWay> _rails = [];
    private int _version = -1;

    public RouteNetwork Network => _network;

    /// Control points are shown and picked this close to the camera.
    public double Reach { get; set; } = 1500.0;

    public bool Has(long node)
    {
        Refresh();
        return _nodes.ContainsKey(node);
    }

    /// The control points within radius metres of (x, z).
    public IEnumerable<long> NodesNear(double x, double z, double radius)
    {
        Refresh();
        foreach (var (id, (nx, nz)) in _nodes)
            if ((nx - x) * (nx - x) + (nz - z) * (nz - z) <= radius * radius)
                yield return id;
    }

    /// The control points inside a rectangle on the screen.
    public IEnumerable<long> NodesIn(float2 a, float2 b, double cx, double cz, Func<double, double, float2?> project)
    {
        Refresh();
        var (min, max) = (new float2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y)), new float2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y)));
        foreach (var (id, (x, z)) in _nodes)
            if (Math.Abs(x - cx) <= Reach && Math.Abs(z - cz) <= Reach && project(x, z) is { } s && s.X >= min.X && s.X <= max.X && s.Y >= min.Y && s.Y <= max.Y)
                yield return id;
    }

    public TrackEditor(RouteInfo route, RouteNetwork network)
    {
        _route = route;
        _network = network;
    }

    /// The rail nodes in world metres (x east, z south), redone whenever the network changes.
    private void Refresh()
    {
        if (_version == _network.Version)
            return;

        _version = _network.Version;
        _nodes.Clear();
        _rails.Clear();
        var projection = _route.Projection!;
        foreach (var way in _network.Ways.Values)
        {
            if (!way.Rail || way.Nodes.Length < 2 || way.Lat.Length != way.Nodes.Length || RouteNetwork.IsGuide(way))
                continue;
            _rails.Add(way);
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                _nodes[way.Nodes[i]] = (p.East, -p.North);
            }
        }
    }

    public (double X, double Z) PositionOf(long node)
    {
        Refresh();
        return _nodes[node];
    }

    /// The control point nearest the mouse on screen, if one is within a few pixels.
    public long? NodeAt(float2 mouse, double cx, double cz, Func<double, double, float2?> project)
    {
        Refresh();
        long? best = null;
        var bestDistance = PickPixels;
        foreach (var (id, (x, z)) in _nodes)
        {
            if (Math.Abs(x - cx) > Reach || Math.Abs(z - cz) > Reach || project(x, z) is not { } screen)
                continue;
            var d = MathF.Sqrt((screen.X - mouse.X) * (screen.X - mouse.X) + (screen.Y - mouse.Y) * (screen.Y - mouse.Y));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = id;
            }
        }

        return best;
    }

    /// The rail segment nearest the mouse on screen, and where along it (world metres), if one is within a few pixels.
    public (long Way, int Segment, double X, double Z)? SegmentAt(float2 mouse, double cx, double cz, Func<double, double, float2?> project)
    {
        Refresh();
        (long, int, double, double)? best = null;
        var bestDistance = PickPixels * 0.75f;
        foreach (var way in _rails)
        {
            for (var i = 0; i < way.Nodes.Length - 1; i++)
            {
                var (ax, az) = _nodes[way.Nodes[i]];
                var (bx, bz) = _nodes[way.Nodes[i + 1]];
                if (Math.Abs(ax - cx) > Reach || Math.Abs(az - cz) > Reach || project(ax, az) is not { } a || project(bx, bz) is not { } b)
                    continue;

                var ab = b - a;
                var t = Math.Clamp(((mouse.X - a.X) * ab.X + (mouse.Y - a.Y) * ab.Y) / MathF.Max(ab.X * ab.X + ab.Y * ab.Y, 1e-3f), 0.05f, 0.95f);
                var p = a + ab * t;
                var d = MathF.Sqrt((p.X - mouse.X) * (p.X - mouse.X) + (p.Y - mouse.Y) * (p.Y - mouse.Y));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = (way.Id, i, ax + (bx - ax) * t, az + (bz - az) * t);
                }
            }
        }

        return best;
    }

    /// The rail segment nearest a point on the ground (world metres) within the given distance, and the nearest point
    /// on it.
    public (long Way, int Segment, double X, double Z)? SegmentNear(double x, double z, double within)
    {
        Refresh();
        (long, int, double, double)? best = null;
        var bestDistance = within;
        foreach (var way in _rails)
        {
            for (var i = 0; i < way.Nodes.Length - 1; i++)
            {
                var (ax, az) = _nodes[way.Nodes[i]];
                var (bx, bz) = _nodes[way.Nodes[i + 1]];
                var dx = bx - ax;
                var dz = bz - az;
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-6), 0.05, 0.95);
                var px = ax + dx * t;
                var pz = az + dz * t;
                var d = Math.Sqrt((px - x) * (px - x) + (pz - z) * (pz - z));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = (way.Id, i, px, pz);
                }
            }
        }

        return best;
    }

    // --- Changes. ---

    private NetworkWay Copy(NetworkWay way, long[] nodes, double[] lat, double[] lon)
    {
        var tags = new Dictionary<string, string>(way.Tags) { [RouteNetwork.EditedTag] = "yes" };
        return new NetworkWay { Id = way.Id, Rail = way.Rail, Class = way.Class, Nodes = nodes, Lat = lat, Lon = lon, Tags = tags };
    }

    private GeoPoint Geo(double x, double z) => _route.Projection!.Inverse(new RoutePoint(x, -z));

    /// Moves a control point, and every way through it, to (x, z).
    public NetworkEdit? Move(long node, double x, double z)
    {
        var geo = Geo(x, z);
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in _network.Ways.Values.Where(w => w.Nodes.Contains(node)).ToList())
        {
            var lat = (double[])way.Lat.Clone();
            var lon = (double[])way.Lon.Clone();
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                if (way.Nodes[i] == node)
                {
                    lat[i] = geo.Lat;
                    lon[i] = geo.Lon;
                }
            }

            changes.Add((way.Id, way, Copy(way, way.Nodes, lat, lon)));
        }

        return Commit(changes);
    }

    /// Takes a control point out of every way through it; a way left with fewer than two goes.
    public NetworkEdit? Delete(long node) => DeleteMany([node]);

    /// Takes control points out of every way through them, in one edit.
    public NetworkEdit? DeleteMany(IReadOnlyCollection<long> doomed)
    {
        var set = doomed.ToHashSet();
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in _network.Ways.Values.Where(w => w.Nodes.Any(set.Contains)).ToList())
        {
            var keep = Enumerable.Range(0, way.Nodes.Length).Where(i => !set.Contains(way.Nodes[i])).ToArray();
            changes.Add((way.Id, way, keep.Length < 2 ? null
                : Copy(way, [.. keep.Select(i => way.Nodes[i])], [.. keep.Select(i => way.Lat[i])], [.. keep.Select(i => way.Lon[i])])));
        }

        return Commit(changes);
    }

    /// Moves control points, and every way through them, in one edit.
    public NetworkEdit? MoveMany(IReadOnlyDictionary<long, (double X, double Z)> to)
    {
        if (to.Count == 0)
            return null;

        var geo = to.ToDictionary(e => e.Key, e => Geo(e.Value.X, e.Value.Z));
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in _network.Ways.Values.Where(w => w.Nodes.Any(geo.ContainsKey)).ToList())
        {
            var lat = (double[])way.Lat.Clone();
            var lon = (double[])way.Lon.Clone();
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                if (geo.TryGetValue(way.Nodes[i], out var p))
                {
                    lat[i] = p.Lat;
                    lon[i] = p.Lon;
                }
            }

            changes.Add((way.Id, way, Copy(way, way.Nodes, lat, lon)));
        }

        return Commit(changes);
    }

    // --- Working on a selection. ---

    /// Every run of three or more selected control points that follow one another along a rail way, as indices into it.
    private IEnumerable<(NetworkWay Way, int From, int To)> Runs(IReadOnlySet<long> selected)
    {
        Refresh();
        foreach (var way in _rails)
        {
            var start = -1;
            for (var i = 0; i <= way.Nodes.Length; i++)
            {
                var inside = i < way.Nodes.Length && selected.Contains(way.Nodes[i]);
                if (inside && start < 0)
                    start = i;
                else if (!inside && start >= 0)
                {
                    if (i - start >= 3)
                        yield return (way, start, i - 1);
                    start = -1;
                }
            }
        }
    }

    /// Lines up each run of selected points on the straight line between its first and last, keeping how far along
    /// it each one was.
    public NetworkEdit? Straighten(IReadOnlySet<long> selected)
    {
        var to = new Dictionary<long, (double X, double Z)>();
        foreach (var (way, from, last) in Runs(selected))
        {
            var along = new double[last - from + 1];
            for (var i = from + 1; i <= last; i++)
            {
                var (ax, az) = _nodes[way.Nodes[i - 1]];
                var (bx, bz) = _nodes[way.Nodes[i]];
                along[i - from] = along[i - from - 1] + Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            }

            var (sx, sz) = _nodes[way.Nodes[from]];
            var (ex, ez) = _nodes[way.Nodes[last]];
            for (var i = from + 1; i < last; i++)
            {
                var t = along[^1] > 0 ? along[i - from] / along[^1] : 0;
                to[way.Nodes[i]] = (sx + (ex - sx) * t, sz + (ez - sz) * t);
            }
        }

        return MoveMany(to);
    }

    /// Evens out the kinks in each run of selected points, its ends staying put. Taubin's smoothing: each pass pulls
    /// every point towards its neighbours' middle, then pushes it a little back out, so curves don't shrink.
    public NetworkEdit? Smooth(IReadOnlySet<long> selected)
    {
        var to = new Dictionary<long, (double X, double Z)>();
        foreach (var (way, from, last) in Runs(selected))
        {
            var points = Enumerable.Range(from, last - from + 1).Select(i => to.TryGetValue(way.Nodes[i], out var p) ? p : _nodes[way.Nodes[i]]).ToArray();
            for (var pass = 0; pass < 20; pass++)
            {
                foreach (var factor in (ReadOnlySpan<double>)[0.5, -0.53])
                {
                    var next = (((double X, double Z)[])points.Clone());
                    for (var i = 1; i < points.Length - 1; i++)
                    {
                        var mx = (points[i - 1].X + points[i + 1].X) * 0.5;
                        var mz = (points[i - 1].Z + points[i + 1].Z) * 0.5;
                        next[i] = (points[i].X + (mx - points[i].X) * factor, points[i].Z + (mz - points[i].Z) * factor);
                    }

                    points = next;
                }
            }

            for (var i = 1; i < points.Length - 1; i++)
                to[way.Nodes[from + i]] = points[i];
        }

        return MoveMany(to);
    }

    /// Takes out the selected points that add nothing to the shape (within SimplifyTolerance of the line the rest
    /// make), keeping way ends and any point another way runs through.
    public NetworkEdit? Simplify(IReadOnlySet<long> selected)
    {
        var uses = new Dictionary<long, int>();
        foreach (var way in _network.Ways.Values)
            foreach (var node in way.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        var doomed = new HashSet<long>();
        foreach (var (way, from, last) in Runs(selected))
        {
            var keep = new bool[last - from + 1];
            keep[0] = keep[^1] = true;
            Keep(way, from, 0, last - from, keep);
            for (var i = 1; i < keep.Length - 1; i++)
                if (!keep[i] && uses.GetValueOrDefault(way.Nodes[from + i]) == 1)
                    doomed.Add(way.Nodes[from + i]);
        }

        return doomed.Count > 0 ? DeleteMany(doomed) : null;
    }

    /// Douglas and Peucker: keep the point furthest from the line between a and b if it's further than the
    /// tolerance, and look again either side of it.
    private void Keep(NetworkWay way, int offset, int a, int b, bool[] keep)
    {
        if (b - a < 2)
            return;
        var (ax, az) = _nodes[way.Nodes[offset + a]];
        var (bx, bz) = _nodes[way.Nodes[offset + b]];
        var (dx, dz) = (bx - ax, bz - az);
        var length = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        var (worst, far) = (-1, 0.0);
        for (var i = a + 1; i < b; i++)
        {
            var (px, pz) = _nodes[way.Nodes[offset + i]];
            var d = Math.Abs((px - ax) * dz - (pz - az) * dx) / length;
            if (d > far)
                (worst, far) = (i, d);
        }

        if (far <= SimplifyTolerance)
            return;
        keep[worst] = true;
        Keep(way, offset, a, worst, keep);
        Keep(way, offset, worst, b, keep);
    }

    /// Makes the rail ways the selection touches into as few as it can: two that end on the same control point, with
    /// no other way through it, become one; then two whose loose ends (no other track there) are selected and within
    /// JoinGap of each other, nearest first, closing the gap. A point where three ways meet is a junction, and stays
    /// one. The joined way keeps the first one's id and tags.
    public NetworkEdit? Join(IReadOnlySet<long> selected)
    {
        Refresh();
        var uses = new Dictionary<long, int>();
        foreach (var way in _network.Ways.Values)
            foreach (var node in way.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        var current = _rails.Where(w => w.Nodes.Any(selected.Contains)).ToDictionary(w => w.Id);
        var changes = new Dictionary<long, (NetworkWay? Before, NetworkWay? After)>();

        void Record(NetworkWay keep, NetworkWay gone, NetworkWay joined)
        {
            changes[keep.Id] = (changes.TryGetValue(keep.Id, out var ck) ? ck.Before : _network.Ways[keep.Id], joined);
            changes[gone.Id] = (changes.TryGetValue(gone.Id, out var cg) ? cg.Before : _network.Ways[gone.Id], null);
            current[keep.Id] = joined;
            current.Remove(gone.Id);
        }

        double Gap(long m, long n)
        {
            var (mx, mz) = _nodes[m];
            var (nx, nz) = _nodes[n];
            return Math.Sqrt((mx - nx) * (mx - nx) + (mz - nz) * (mz - nz));
        }

        bool JoinOne()
        {
            var ways = current.Values.OrderByDescending(w => w.Id > 0).ThenBy(w => w.Id).ToList();
            foreach (var a in ways)
                foreach (var b in ways)
                    if (a.Id != b.Id && Shared(a, b) is { } joined)
                    {
                        Record(a, b, joined);
                        return true;
                    }

            (NetworkWay A, NetworkWay B, NetworkWay Joined, double Gap)? best = null;
            foreach (var a in ways)
                foreach (var b in ways)
                    if (a.Id != b.Id && Bridged(a, b) is { } bridged && (best is null || bridged.Gap < best.Value.Gap))
                        best = (a, b, bridged.Joined, bridged.Gap);
            if (best is not { } pick)
                return false;
            Record(pick.A, pick.B, pick.Joined);
            return true;
        }

        // b onto a where they end on the same control point and nothing else runs through it; null if they don't.
        NetworkWay? Shared(NetworkWay a, NetworkWay b)
        {
            var (aStart, aEnd, bStart, bEnd) = (a.Nodes[0], a.Nodes[^1], b.Nodes[0], b.Nodes[^1]);
            if (aStart == aEnd || bStart == bEnd)
                return null;
            bool Only(long node) => uses.GetValueOrDefault(node) == 2;
            if (aEnd == bStart && Only(aEnd)) return Concat(a, false, b, false, true);
            if (aEnd == bEnd && Only(aEnd)) return Concat(a, false, b, true, true);
            if (aStart == bEnd && Only(aStart)) return Concat(b, false, a, false, true, a);
            if (aStart == bStart && Only(aStart)) return Concat(b, true, a, false, true, a);
            return null;
        }

        // b onto a across the gap between a loose end of each, both selected and within JoinGap; the nearest such.
        (NetworkWay Joined, double Gap)? Bridged(NetworkWay a, NetworkWay b)
        {
            var (aStart, aEnd, bStart, bEnd) = (a.Nodes[0], a.Nodes[^1], b.Nodes[0], b.Nodes[^1]);
            if (aStart == aEnd || bStart == bEnd)
                return null;
            bool Loose(long node) => selected.Contains(node) && uses.GetValueOrDefault(node) == 1;
            (NetworkWay, double)? best = null;
            void Try(long m, long n, Func<NetworkWay> join)
            {
                if (!Loose(m) || !Loose(n))
                    return;
                var gap = Gap(m, n);
                if (gap <= JoinGap && (best is null || gap < best.Value.Item2))
                    best = (join(), gap);
            }

            Try(aEnd, bStart, () => Concat(a, false, b, false, false));
            Try(aEnd, bEnd, () => Concat(a, false, b, true, false));
            Try(aStart, bEnd, () => Concat(b, false, a, false, false, a));
            Try(aStart, bStart, () => Concat(b, true, a, false, false, a));
            return best;
        }

        // first then second, either reversed; shared: their meeting node is the same one, so keep it once. The result
        // takes keep's id and tags (the first way's, if not given).
        NetworkWay Concat(NetworkWay first, bool firstReversed, NetworkWay second, bool secondReversed, bool shared, NetworkWay? keep = null)
        {
            static IEnumerable<int> Order(NetworkWay w, bool reversed) => reversed ? Enumerable.Range(0, w.Nodes.Length).Reverse() : Enumerable.Range(0, w.Nodes.Length);
            var parts = Order(first, firstReversed).Select(i => (first.Nodes[i], first.Lat[i], first.Lon[i]))
                .Concat(Order(second, secondReversed).Skip(shared ? 1 : 0).Select(i => (second.Nodes[i], second.Lat[i], second.Lon[i]))).ToList();
            return Copy(keep ?? first, [.. parts.Select(p => p.Item1)], [.. parts.Select(p => p.Item2)], [.. parts.Select(p => p.Item3)]);
        }

        while (JoinOne()) { }
        return Commit([.. changes.Select(c => (c.Key, c.Value.Before, c.Value.After))]);
    }

    /// Lays new main-line track through the points. Points on an existing track's segment put a new control point in
    /// that track, so the new one branches off there: a switch. lead: put in a turnout's lead where it leaves or joins a
    /// track (for points clicked one by one; a drawn curve already says how it leaves).
    /// exact: yard track laid along a guide, as it's given, and left as it is by the fitting.
    public NetworkEdit? Lay(IReadOnlyList<LayPoint> laid, bool lead = true, bool exact = false)
    {
        if (laid.Count < 2)
            return null;

        // Where the new track leaves or joins a track, it runs along it for a turnout's length first and curves
        // away, rather than meeting it at whatever angle the next point makes.
        var points = laid.ToList();
        if (lead && Lead(points[0], points[1]) is { } leaving)
            points.Insert(1, leaving);
        if (lead && Lead(points[^1], points[^2]) is { } joining)
            points.Insert(points.Count - 1, joining);

        var changes = new Dictionary<long, (NetworkWay? Before, NetworkWay? After)>();
        NetworkWay Current(long id) => changes.TryGetValue(id, out var c) ? c.After! : _network.Ways[id];

        var nodes = new long[points.Count];
        var lat = new double[points.Count];
        var lon = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var geo = Geo(point.X, point.Z);
            lat[i] = geo.Lat;
            lon[i] = geo.Lon;

            if (point.Node is { } existing)
            {
                nodes[i] = existing;
                var (x, z) = PositionOf(existing);
                var at = Geo(x, z);
                lat[i] = at.Lat;
                lon[i] = at.Lon;
                continue;
            }

            nodes[i] = _network.NewId();
            if (point.Split is { } split && _network.Ways.ContainsKey(split.Way))
            {
                // Put the new node into the track it lands on, just after the segment's start.
                var way = Current(split.Way);
                var start = Array.IndexOf(way.Nodes, _network.Ways[split.Way].Nodes[split.Segment]);
                var n = way.Nodes.ToList();
                var la = way.Lat.ToList();
                var lo = way.Lon.ToList();
                n.Insert(start + 1, nodes[i]);
                la.Insert(start + 1, geo.Lat);
                lo.Insert(start + 1, geo.Lon);
                changes[split.Way] = (changes.TryGetValue(split.Way, out var c) ? c.Before : way, Copy(way, [.. n], [.. la], [.. lo]));
            }
        }

        var id = _network.NewId();
        var track = new NetworkWay
        {
            Id = id, Rail = true, Class = exact ? (int)RailClass.Yard : (int)RailClass.Main, Nodes = nodes, Lat = lat, Lon = lon,
            Tags = exact
                ? new Dictionary<string, string> { ["railway"] = "rail", ["service"] = "yard", [RouteNetwork.ExactTag] = "yes", [RouteNetwork.EditedTag] = "yes" }
                : new Dictionary<string, string> { ["railway"] = "rail", ["usage"] = "main", [RouteNetwork.EditedTag] = "yes" },
        };
        changes[id] = (null, track);
        return Commit([.. changes.Select(c => (c.Key, c.Value.Before, c.Value.After))]);
    }

    /// Lays yard track as one edit: ways joined to what they start or end on, and branches off the first of them. laid, if
    /// given, gets each way's nodes.
    public NetworkEdit? LayWays(IReadOnlyList<PlannedWay> ways, List<long[]>? laid = null)
    {
        if (ways.Count == 0)
            return null;

        var changes = new Dictionary<long, (NetworkWay? Before, NetworkWay? After)>();
        long[]? firstNodes = null;
        double[]? firstLat = null, firstLon = null;
        var made = new List<(long[] Nodes, double[] Lat, double[] Lon)>();
        foreach (var planned in ways)
        {
            var points = planned.Points;
            var (nodes, lat, lon) = (new long[points.Count], new double[points.Count], new double[points.Count]);
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var index = i;
                if (planned.Shared?.Where(s => s.Point == index).Select(s => ((int Way, int Other)?)(s.Way, s.Other)).FirstOrDefault() is { } link)
                {
                    var (otherNodes, otherLat, otherLon) = made[link.Way];
                    (nodes[i], lat[i], lon[i]) = (otherNodes[link.Other], otherLat[link.Other], otherLon[link.Other]);
                    continue;
                }

                if (i == 0 && planned.From is { } from)
                {
                    (nodes[i], lat[i], lon[i]) = (firstNodes![from], firstLat![from], firstLon![from]);
                    continue;
                }

                if (point.Node is { } existing)
                {
                    var (x, z) = PositionOf(existing);
                    var at = Geo(x, z);
                    (nodes[i], lat[i], lon[i]) = (existing, at.Lat, at.Lon);
                    continue;
                }

                var geo = Geo(point.X, point.Z);
                (nodes[i], lat[i], lon[i]) = (_network.NewId(), geo.Lat, geo.Lon);
                if (point.Split is { } split && _network.Ways.TryGetValue(split.Way, out var original))
                {
                    var way = changes.TryGetValue(split.Way, out var c) ? c.After! : original;
                    var begin = Array.IndexOf(way.Nodes, original.Nodes[split.Segment]);
                    var start = begin + 1;
                    double Away(double a, double o) => (a - way.Lat[begin]) * (a - way.Lat[begin]) + (o - way.Lon[begin]) * (o - way.Lon[begin]);
                    while (start < way.Nodes.Length && Array.IndexOf(original.Nodes, way.Nodes[start]) < 0 && Away(way.Lat[start], way.Lon[start]) < Away(geo.Lat, geo.Lon))
                        start++;
                    var (n, la, lo) = (way.Nodes.ToList(), way.Lat.ToList(), way.Lon.ToList());
                    n.Insert(start, nodes[i]);
                    la.Insert(start, geo.Lat);
                    lo.Insert(start, geo.Lon);
                    changes[split.Way] = (changes.TryGetValue(split.Way, out var was) ? was.Before : way, Copy(way, [.. n], [.. la], [.. lo]));
                }
            }

            firstNodes ??= nodes;
            firstLat ??= lat;
            firstLon ??= lon;
            made.Add((nodes, lat, lon));
            laid?.Add(nodes);
            var id = _network.NewId();
            changes[id] = (null, new NetworkWay
            {
                Id = id, Rail = true, Class = (int)RailClass.Yard, Nodes = nodes, Lat = lat, Lon = lon,
                Tags = new Dictionary<string, string> { ["railway"] = "rail", ["service"] = "yard", [RouteNetwork.ExactTag] = "yes", [RouteNetwork.EditedTag] = "yes" },
            });
        }

        return Commit([.. changes.Select(c => (c.Key, c.Value.Before, c.Value.After))]);
    }

    /// Whether a path comes within clearance of built track, or crosses it. The first or last step can be left out, where
    /// the path joins the track it starts or ends on.
    public bool RunsInto(IReadOnlyList<(double X, double Z)> path, double clearance, bool skipFirst, bool skipLast)
    {
        Refresh();
        if (path.Count < 2)
            return false;
        var (minX, maxX, minZ, maxZ) = (path.Min(p => p.X) - clearance, path.Max(p => p.X) + clearance, path.Min(p => p.Z) - clearance, path.Max(p => p.Z) + clearance);
        var (from, to) = (skipFirst ? 1 : 0, skipLast ? path.Count - 2 : path.Count - 1);
        foreach (var way in _rails)
        {
            for (var i = 0; i + 1 < way.Nodes.Length; i++)
            {
                var (a, b) = (_nodes[way.Nodes[i]], _nodes[way.Nodes[i + 1]]);
                if (Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX || Math.Max(a.Z, b.Z) < minZ || Math.Min(a.Z, b.Z) > maxZ)
                    continue;
                for (var k = from; k < to; k++)
                    if (Crosses(path[k], path[k + 1], a, b) || (clearance > 0.0 && Nearer(path[k], a, b, clearance)))
                        return true;
            }
        }

        return false;
    }

    private static bool Crosses((double X, double Z) p, (double X, double Z) q, (double X, double Z) a, (double X, double Z) b)
    {
        static double Side((double X, double Z) o, (double X, double Z) d, (double X, double Z) t) => (d.X - o.X) * (t.Z - o.Z) - (d.Z - o.Z) * (t.X - o.X);
        return Side(p, q, a) * Side(p, q, b) < 0.0 && Side(a, b, p) * Side(a, b, q) < 0.0;
    }

    private static bool Nearer((double X, double Z) p, (double X, double Z) a, (double X, double Z) b, double within)
    {
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
        var (ex, ez) = (a.X + dx * t - p.X, a.Z + dz * t - p.Z);
        return ex * ex + ez * ez < within * within;
    }

    private Dictionary<long, int> RailUses()
    {
        Refresh();
        var uses = new Dictionary<long, int>();
        foreach (var way in _rails)
            foreach (var node in way.Nodes.Distinct())
                uses[node] = uses.GetValueOrDefault(node) + 1;
        return uses;
    }

    /// A point more than one way runs through, other than two ending on each other.
    public bool IsJunction(long node)
    {
        Refresh();
        var through = _rails.Where(w => w.Nodes.Contains(node)).ToList();
        return through.Count > 2 || (through.Count == 2 && through.Any(w => w.Nodes[0] != node && w.Nodes[^1] != node));
    }

    /// Whether a junction stands on the line between two places.
    public bool JunctionAlong((double X, double Z) a, (double X, double Z) b)
    {
        foreach (var (node, count) in RailUses())
            if (count > 1 && Nearer(_nodes[node], a, b, 1.0) && IsJunction(node))
                return true;
        return false;
    }

    /// A track's end with nothing else there.
    public bool IsLooseEnd(long node)
    {
        if (RailUses().GetValueOrDefault(node) != 1)
            return false;
        var way = _rails.First(w => w.Nodes.Contains(node));
        return way.Nodes[0] == node || way.Nodes[^1] == node;
    }

    /// The nearest place two built tracks cross with nothing joining them, within reach.
    public (long WayA, int SegmentA, long WayB, int SegmentB, double X, double Z, double Degrees)? CrossingNear(double x, double z, double within)
    {
        Refresh();
        var near = new List<(NetworkWay Way, int Segment, (double X, double Z) A, (double X, double Z) B)>();
        foreach (var way in _rails)
            for (var i = 0; i + 1 < way.Nodes.Length; i++)
            {
                var (a, b) = (_nodes[way.Nodes[i]], _nodes[way.Nodes[i + 1]]);
                if (Nearer((x, z), a, b, within))
                    near.Add((way, i, a, b));
            }

        (long, int, long, int, double, double, double)? found = null;
        var best = double.MaxValue;
        for (var i = 0; i < near.Count; i++)
            for (var j = i + 1; j < near.Count; j++)
            {
                var (p, q) = (near[i], near[j]);
                if (p.Way.Id == q.Way.Id || !Crosses(p.A, p.B, q.A, q.B))
                    continue;
                var (rx, rz, sx, sz) = (p.B.X - p.A.X, p.B.Z - p.A.Z, q.B.X - q.A.X, q.B.Z - q.A.Z);
                var t = ((q.A.X - p.A.X) * sz - (q.A.Z - p.A.Z) * sx) / (rx * sz - rz * sx);
                var (cx, cz) = (p.A.X + rx * t, p.A.Z + rz * t);
                var away = (cx - x) * (cx - x) + (cz - z) * (cz - z);
                if (away >= best)
                    continue;
                best = away;
                var cross = Math.Abs(rx * sz - rz * sx) / Math.Sqrt((rx * rx + rz * rz) * (sx * sx + sz * sz));
                found = (p.Way.Id, p.Segment, q.Way.Id, q.Segment, cx, cz, Math.Asin(Math.Min(cross, 1.0)) * 180.0 / Math.PI);
            }

        return found;
    }

    /// How near one of a way's points a crossing is taken to be at it.
    private const double CrossingMerge = 0.5;

    /// Joins two tracks where they cross, with a control point in both: a diamond.
    public NetworkEdit? JoinCrossing((long WayA, int SegmentA, long WayB, int SegmentB, double X, double Z, double Degrees) crossing)
    {
        var node = _network.NewId();
        var geo = Geo(crossing.X, crossing.Z);
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var (id, segment) in new[] { (crossing.WayA, crossing.SegmentA), (crossing.WayB, crossing.SegmentB) })
        {
            var way = _network.Ways[id];
            var (nodes, lat, lon) = (way.Nodes.ToList(), way.Lat.ToList(), way.Lon.ToList());

            // A point of the way right here becomes it (the line would drop a second one so close, and the two tracks wouldn't
            // be joined); else it's a new point.
            var uses = RailUses();
            var near = ((int[])[segment, segment + 1]).Where(i => uses.GetValueOrDefault(nodes[i]) == 1)
                .Select(i => (Index: i, Away: Math.Sqrt(Math.Pow(_nodes[nodes[i]].X - crossing.X, 2) + Math.Pow(_nodes[nodes[i]].Z - crossing.Z, 2))))
                .Where(p => p.Away < CrossingMerge).OrderBy(p => p.Away).Select(p => (int?)p.Index).FirstOrDefault();
            if (near is { } at)
                (nodes[at], lat[at], lon[at]) = (node, geo.Lat, geo.Lon);
            else
            {
                nodes.Insert(segment + 1, node);
                lat.Insert(segment + 1, geo.Lat);
                lon.Insert(segment + 1, geo.Lon);
            }

            changes.Add((id, way, Copy(way, [.. nodes], [.. lat], [.. lon])));
        }

        return Commit(changes);
    }

    /// The nearest track end with nothing else there, within reach.
    public long? LooseEndNear(double x, double z, double within)
    {
        var uses = RailUses();
        long? found = null;
        var best = within * within;
        foreach (var way in _rails)
            foreach (var node in (long[])[way.Nodes[0], way.Nodes[^1]])
            {
                var (nx, nz) = _nodes[node];
                var away = (nx - x) * (nx - x) + (nz - z) * (nz - z);
                if (away < best && uses.GetValueOrDefault(node) == 1)
                    (best, found) = (away, node);
            }

        return found;
    }

    /// The way a track carries on past its end.
    public (double X, double Z)? LeavingEnd(long node)
    {
        Refresh();
        if (_rails.FirstOrDefault(w => w.Nodes[0] == node || w.Nodes[^1] == node) is not { } way)
            return null;
        var inner = way.Nodes[0] == node ? way.Nodes[1] : way.Nodes[^2];
        return Unit(_nodes[node], _nodes[inner]);
    }

    /// Whether a place on a track is on yard or industrial track.
    public bool IsYard(LayPoint point)
    {
        Refresh();
        var way = point.Split is { } split ? _network.Ways.GetValueOrDefault(split.Way) : point.Node is { } node ? _rails.FirstOrDefault(w => w.Nodes.Contains(node)) : null;
        return way?.Class is (int)RailClass.Yard or (int)RailClass.Industrial;
    }

    /// A turnout's lead from a point on a track towards the next point: 40 m along the track (whichever way faces the
    /// next point), 1.5 m out to its side. Null for a point on open ground, or if the next point is nearer than that.
    private LayPoint? Lead(LayPoint at, LayPoint toward)
    {
        const double length = 40.0;
        const double offset = 1.5;

        (double X, double Z)? direction = null;
        if (at.Split is { } split && _network.Ways.TryGetValue(split.Way, out var way))
        {
            var (ax, az) = PositionOf(way.Nodes[split.Segment]);
            var (bx, bz) = PositionOf(way.Nodes[split.Segment + 1]);
            direction = (bx - ax, bz - az);
        }
        else if (at.Node is { } node && _rails.FirstOrDefault(w => w.Nodes.Contains(node)) is { } through)
        {
            var i = Array.IndexOf(through.Nodes, node);
            var (ax, az) = PositionOf(through.Nodes[Math.Max(i - 1, 0)]);
            var (bx, bz) = PositionOf(through.Nodes[Math.Min(i + 1, through.Nodes.Length - 1)]);
            direction = (bx - ax, bz - az);
        }

        var (tx, tz) = (toward.X - at.X, toward.Z - at.Z);
        if (direction is not { } d || Math.Sqrt(tx * tx + tz * tz) < length * 1.5)
            return null;

        var norm = Math.Sqrt(d.X * d.X + d.Z * d.Z);
        if (norm < 1e-6)
            return null;
        var (fx, fz) = (d.X / norm, d.Z / norm);
        if (fx * tx + fz * tz < 0)
            (fx, fz) = (-fx, -fz);
        var side = fx * tz - fz * tx > 0 ? 1.0 : -1.0; // which side of the track the next point is on
        return new LayPoint(at.X + fx * length - fz * offset * side, at.Z + fz * length + fx * offset * side);
    }

    private NetworkEdit? Commit(List<(long Id, NetworkWay? Before, NetworkWay? After)> changes)
    {
        if (changes.Count == 0)
            return null;

        var edit = new NetworkEdit(changes);
        Apply(edit, undo: false);
        return edit;
    }

    /// A yard's ways made guides, not built, or put back to be built again.
    public NetworkEdit? SetGuide(IEnumerable<long> ways, bool guide)
    {
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var id in ways)
        {
            var way = _network.Ways[id];
            if (RouteNetwork.IsGuide(way) == guide)
                continue;
            var tags = new Dictionary<string, string>(way.Tags);
            if (guide)
            {
                tags[RouteNetwork.GuideTag] = tags.ContainsKey(RouteNetwork.EditedTag) ? "edited" : "mapped";
                tags[RouteNetwork.EditedTag] = "yes";
            }
            else
            {
                if (tags[RouteNetwork.GuideTag] == "mapped")
                    tags.Remove(RouteNetwork.EditedTag);
                tags.Remove(RouteNetwork.GuideTag);
            }

            changes.Add((id, way, new NetworkWay { Id = way.Id, Rail = way.Rail, Class = way.Class, Nodes = way.Nodes, Lat = way.Lat, Lon = way.Lon, Tags = tags }));
        }

        return Commit(changes);
    }

    /// The built track nearest a place in the world, if it's within reach: right at one of its points if that's near, or
    /// else along a segment, and the way the track runs there.
    public (LayPoint Point, (double X, double Z) Runs)? TrackNear(double x, double z, double within, double atPoint)
    {
        Refresh();
        (long Way, int Segment, double T, double X, double Z, double UX, double UZ)? best = null;
        var bestAway = within * within;
        foreach (var way in _rails)
        {
            for (var i = 0; i + 1 < way.Nodes.Length; i++)
            {
                var (ax, az) = _nodes[way.Nodes[i]];
                var (bx, bz) = _nodes[way.Nodes[i + 1]];
                if (Math.Min(ax, bx) > x + within || Math.Max(ax, bx) < x - within || Math.Min(az, bz) > z + within || Math.Max(az, bz) < z - within)
                    continue;
                var (dx, dz) = (bx - ax, bz - az);
                var lengthSquared = Math.Max(dx * dx + dz * dz, 1e-9);
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / lengthSquared, 0.0, 1.0);
                var (px, pz) = (ax + dx * t, az + dz * t);
                var away = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (away < bestAway)
                {
                    var length = Math.Sqrt(lengthSquared);
                    bestAway = away;
                    best = (way.Id, i, t, px, pz, dx / length, dz / length);
                }
            }
        }

        if (best is not { } found)
            return null;

        var nodes = _network.Ways[found.Way].Nodes;
        foreach (var node in (long[])[nodes[found.Segment], nodes[found.Segment + 1]])
        {
            var (nx, nz) = _nodes[node];
            if ((nx - found.X) * (nx - found.X) + (nz - found.Z) * (nz - found.Z) <= atPoint * atPoint)
                return (new LayPoint(nx, nz, Node: node), (found.UX, found.UZ));
        }

        return (new LayPoint(found.X, found.Z, Split: (found.Way, found.Segment)), (found.UX, found.UZ));
    }

    /// The way the track runs at a built point.
    public (double X, double Z)? RunsAt(long node)
    {
        Refresh();
        if (_rails.FirstOrDefault(w => w.Nodes.Contains(node)) is not { } way)
            return null;
        var i = Array.IndexOf(way.Nodes, node);
        var (ax, az) = _nodes[way.Nodes[Math.Max(i - 1, 0)]];
        var (bx, bz) = _nodes[way.Nodes[Math.Min(i + 1, way.Nodes.Length - 1)]];
        var length = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        return length < 1e-6 ? null : ((bx - ax) / length, (bz - az) / length);
    }

    /// How near a slid switch may come to the next junction or the end of the ways it stands on.
    private const double SlideClearance = 15.0;

    /// Moves a switch's points along the track it stands on, to the nearest place to (x, z) that's short of the next
    /// junction: the shape points it passes change sides, and the diverging way loses any it would now run back over.
    public NetworkEdit? SlideSwitch(Turnout turnout, double x, double z)
    {
        Refresh();
        var node = turnout.Node;
        var ways = _network.Ways.Values.Where(w => w.Rail && !RouteNetwork.IsGuide(w) && w.Nodes.Contains(node)).ToList();
        var legs = new List<(NetworkWay Way, int Direction)>();
        foreach (var way in ways)
        {
            var at = Array.IndexOf(way.Nodes, node);
            if (at > 0)
                legs.Add((way, -1));
            if (at < way.Nodes.Length - 1)
                legs.Add((way, 1));
        }

        if (legs.Count != 3)
            return null;

        var positions = new Dictionary<long, (double X, double Z)>();
        foreach (var way in ways)
        {
            var points = Positions(way);
            for (var i = 0; i < points.Length; i++)
                positions[way.Nodes[i]] = points[i];
        }

        List<(long Node, double Lat, double Lon)> Outward((NetworkWay Way, int Direction) leg)
        {
            var list = new List<(long, double, double)>();
            for (var i = Array.IndexOf(leg.Way.Nodes, node) + leg.Direction; i >= 0 && i < leg.Way.Nodes.Length; i += leg.Direction)
                list.Add((leg.Way.Nodes[i], leg.Way.Lat[i], leg.Way.Lon[i]));
            return list;
        }

        (double X, double Z) Out((NetworkWay Way, int Direction) leg, double metres)
        {
            var (from, walked) = (positions[node], 0.0);
            foreach (var (next, _, _) in Outward(leg))
            {
                var to = positions[next];
                var length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
                if (walked + length >= metres)
                {
                    var t = (metres - walked) / Math.Max(length, 1e-9);
                    return (from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t);
                }

                (from, walked) = (to, walked + length);
            }

            return from;
        }

        static double Off(NetworkLine line, (double X, double Z) p)
        {
            var best = double.MaxValue;
            for (var i = 0; i + 1 < line.MappedX.Length; i++)
            {
                var (dx, dz) = (line.MappedX[i + 1] - line.MappedX[i], line.MappedZ[i + 1] - line.MappedZ[i]);
                var t = Math.Clamp(((p.X - line.MappedX[i]) * dx + (p.Z - line.MappedZ[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (line.MappedX[i] + dx * t - p.X, line.MappedZ[i] + dz * t - p.Z);
                best = Math.Min(best, ex * ex + ez * ez);
            }

            return Math.Sqrt(best);
        }

        // Which leg is which: the diverging one is on the reverse line and off the normal one; of the others, the toe runs
        // back the opposite way to it.
        var reverse = legs.MinBy(l => Off(turnout.Reverse.Line, Out(l, 30.0)) - Off(turnout.Normal.Line, Out(l, 30.0)));
        var rest = legs.Where(l => l != reverse).ToList();
        var (rx, rz) = Out(reverse, 6.0);
        double Facing((NetworkWay Way, int Direction) l)
        {
            var (px, pz) = Out(l, 6.0);
            return (px - positions[node].X) * (rx - positions[node].X) + (pz - positions[node].Z) * (rz - positions[node].Z);
        }

        var (toe, normal) = Facing(rest[0]) < Facing(rest[1]) ? (rest[0], rest[1]) : (rest[1], rest[0]);

        // The through track as one run, measured from the node: toe side below nought, normal side above.
        var through = new List<(long Node, double Lat, double Lon, double S)>();
        var uses = new Dictionary<long, int>();
        foreach (var way in _rails)
            foreach (var used in way.Nodes.Distinct())
                uses[used] = uses.GetValueOrDefault(used) + 1;

        var (low, high) = (double.MinValue, double.MaxValue);
        foreach (var (leg, sign) in new[] { (toe, -1.0), (normal, 1.0) })
        {
            var (from, walked) = (positions[node], 0.0);
            foreach (var (next, lat, lon) in Outward(leg))
            {
                var to = positions[next];
                walked += Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
                from = to;
                through.Add((next, lat, lon, walked * sign));
                if (uses.GetValueOrDefault(next) > 1)
                    break;
            }

            var last = Math.Max(walked - SlideClearance, 0.0);
            if (sign > 0)
                high = last;
            else
                low = -last;
        }

        through.Add((node, ways[0].Lat[Array.IndexOf(ways[0].Nodes, node)], ways[0].Lon[Array.IndexOf(ways[0].Nodes, node)], 0.0));
        through.Sort((a, b) => a.S.CompareTo(b.S));

        // Where (x, z) falls on it.
        var (wanted, away) = (0.0, double.MaxValue);
        for (var i = 0; i + 1 < through.Count; i++)
        {
            var (a, b) = (positions[through[i].Node], positions[through[i + 1].Node]);
            var (dx, dz) = (b.X - a.X, b.Z - a.Z);
            var t = Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (px, pz) = (a.X + dx * t, a.Z + dz * t);
            var d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < away)
                (wanted, away) = (through[i].S + (through[i + 1].S - through[i].S) * t, d);
        }

        wanted = Math.Clamp(wanted, low, high);
        if (Math.Abs(wanted) < 1.0)
            return null;

        var segment = through.FindIndex(p => p.S >= wanted);
        var (before, after) = (through[Math.Max(segment - 1, 0)], through[segment]);
        var share = (wanted - before.S) / Math.Max(after.S - before.S, 1e-9);
        var (newLat, newLon) = (before.Lat + (after.Lat - before.Lat) * share, before.Lon + (after.Lon - before.Lon) * share);
        var moved = Forward(newLat, newLon);
        var forward = Unit(positions[after.Node], positions[before.Node]);

        // Each side of it, outward, keeping everything beyond the run's first junction on its way.
        var toeOut = through.Where(p => p.S < wanted - 0.5 && p.Node != node).OrderByDescending(p => p.S).Select(p => (p.Node, p.Lat, p.Lon)).ToList();
        var normalOut = through.Where(p => p.S > wanted + 0.5 && p.Node != node).OrderBy(p => p.S).Select(p => (p.Node, p.Lat, p.Lon)).ToList();
        if (toeOut.Count == 0 || normalOut.Count == 0)
            return null;

        // The diverging way, without what it would run back over.
        var reverseOut = Outward(reverse);
        while (reverseOut.Count > 1 && uses.GetValueOrDefault(reverseOut[0].Node) <= 1)
        {
            var (px, pz) = Forward(reverseOut[0].Lat, reverseOut[0].Lon);
            if ((px - moved.X) * forward.X + (pz - moved.Z) * forward.Z >= 3.0)
                break;
            reverseOut.RemoveAt(0);
        }

        var (qx, qz) = Forward(reverseOut[0].Lat, reverseOut[0].Lon);
        if ((qx - moved.X) * forward.X + (qz - moved.Z) * forward.Z < 3.0)
            return null;

        List<(long Node, double Lat, double Lon)> SideOf((NetworkWay Way, int Direction) leg) => leg == toe ? toeOut : leg == normal ? normalOut : reverseOut;

        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in ways)
        {
            var built = new List<(long Node, double Lat, double Lon)>();
            if (legs.Contains((way, -1)))
                built.AddRange(Enumerable.Reverse(SideOf((way, -1))));
            built.Add((node, newLat, newLon));
            if (legs.Contains((way, 1)))
                built.AddRange(SideOf((way, 1)));
            changes.Add((way.Id, way, Copy(way, [.. built.Select(p => p.Node)], [.. built.Select(p => p.Lat)], [.. built.Select(p => p.Lon)])));
        }

        // (A guide that ends here goes with it.)
        foreach (var guide in _network.Ways.Values.Where(w => RouteNetwork.IsGuide(w) && w.Lat.Length == w.Nodes.Length && w.Nodes.Contains(node)).ToList())
        {
            var (lat, lon) = ((double[])guide.Lat.Clone(), (double[])guide.Lon.Clone());
            var i = Array.IndexOf(guide.Nodes, node);
            (lat[i], lon[i]) = (newLat, newLon);
            changes.Add((guide.Id, guide, Copy(guide, guide.Nodes, lat, lon)));
        }

        return Commit(changes);
    }

    /// Moves both a crossover's switches along their tracks, as one edit; neither moves if either can't.
    public NetworkEdit? SlideCrossover(Turnout a, (double X, double Z) toA, Turnout b, (double X, double Z) toB)
    {
        if (SlideSwitch(a, toA.X, toA.Z) is not { } first)
            return null;
        if (SlideSwitch(b, toB.X, toB.Z) is not { } second)
        {
            Apply(first, undo: true);
            return null;
        }

        var merged = first.Ways.ToDictionary(w => w.Id);
        foreach (var way in second.Ways)
            merged[way.Id] = merged.TryGetValue(way.Id, out var was) ? (way.Id, was.Before, way.After) : way;
        return new NetworkEdit([.. merged.Values]);
    }

    private (double X, double Z) Forward(double lat, double lon)
    {
        var p = _route.Projection!.Forward(new GeoPoint(lat, lon));
        return (p.East, -p.North);
    }

    private static (double X, double Z) Unit((double X, double Z) to, (double X, double Z) from)
    {
        var (dx, dz) = (to.X - from.X, to.Z - from.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }

    /// Tracks that can be cleared by area: yards, sidings, spurs and crossovers, not the main lines or track laid by hand.
    private static bool Clearable(NetworkWay way) =>
        way.Rail && way.Nodes.Length >= 2 && way.Lat.Length == way.Nodes.Length && way.Tags.ContainsKey("service") && !way.Tags.ContainsKey(RouteNetwork.ExactTag);

    private (double X, double Z)[] Positions(NetworkWay way)
    {
        var points = new (double X, double Z)[way.Nodes.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = _route.Projection!.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
            points[i] = (p.East, -p.North);
        }

        return points;
    }

    /// Along a way in steps: how far along each step is, and whether it's inside.
    private static List<(double S, bool Inside)> Steps((double X, double Z)[] points, Func<double, double, bool> inside)
    {
        var steps = new List<(double, bool)> { (0.0, inside(points[0].X, points[0].Z)) };
        var along = 0.0;
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var (dx, dz) = (points[i + 1].X - points[i].X, points[i + 1].Z - points[i].Z);
            var length = Math.Sqrt(dx * dx + dz * dz);
            var count = Math.Max(1, (int)Math.Ceiling(length / AreaStep));
            for (var k = 1; k <= count; k++)
            {
                var t = (double)k / count;
                steps.Add((along + length * t, inside(points[i].X + dx * t, points[i].Z + dz * t)));
            }

            along += length;
        }

        return steps;
    }

    /// How far apart an area's edge is looked for along a track.
    private const double AreaStep = 4.0;

    /// The shortest piece of track an area's edge leaves on either side of it.
    private const double ShortestCut = 30.0;

    /// An edge this near a junction, or any point of the way, cuts there rather than making a new point.
    private const double CutAtJunction = 30.0;
    private const double CutAtPoint = 8.0;

    /// How much clearable track is inside an area: still built, and cleared to guides.
    public (double Built, double Cleared) LengthIn(Func<double, double, bool> inside)
    {
        var (built, cleared) = (0.0, 0.0);
        foreach (var way in _network.Ways.Values.Where(Clearable))
        {
            var steps = Steps(Positions(way), inside);
            var length = 0.0;
            for (var i = 0; i + 1 < steps.Count; i++)
                if (steps[i].Inside && steps[i + 1].Inside)
                    length += steps[i + 1].S - steps[i].S;
            if (RouteNetwork.IsGuide(way))
                cleared += length;
            else
                built += length;
        }

        return (built, cleared);
    }

    /// The tracks inside an area made guides, or put back to be built again. A track running over the area's edge is cut
    /// there into two ways, so only what's inside changes; the piece left built ends at the cut, ready to join.
    public NetworkEdit? SetGuide(Func<double, double, bool> inside, bool guide)
    {
        var uses = new Dictionary<long, int>();
        foreach (var way in _network.Ways.Values.Where(w => w.Rail))
            foreach (var node in way.Nodes.Distinct())
                uses[node] = uses.GetValueOrDefault(node) + 1;

        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in _network.Ways.Values.Where(w => Clearable(w) && RouteNetwork.IsGuide(w) != guide).ToList())
        {
            var points = Positions(way);
            var runs = Runs(Steps(points, inside));
            if (runs.Count == 1)
            {
                if (runs[0].Inside)
                    changes.Add((way.Id, way, Tagged(way, way.Id, way.Nodes, way.Lat, way.Lon, guide, true, false)));
                continue;
            }

            changes.AddRange(CutWay(way, points, runs, uses, guide));
        }

        return Commit(changes);
    }

    /// The stretches of a way in and out of an area, none so short it would leave a useless piece of track.
    private static List<(double From, double To, bool Inside)> Runs(List<(double S, bool Inside)> steps)
    {
        var runs = new List<(double From, double To, bool Inside)>();
        foreach (var (s, stepInside) in steps)
        {
            if (runs.Count == 0)
                runs.Add((s, s, stepInside));
            else if (runs[^1].Inside == stepInside)
                runs[^1] = (runs[^1].From, s, stepInside);
            else
            {
                var edge = (runs[^1].To + s) / 2.0;
                runs[^1] = (runs[^1].From, edge, runs[^1].Inside);
                runs.Add((edge, s, stepInside));
            }
        }

        // The shortest goes in with what's either side of it, until none is too short.
        while (runs.Count > 1)
        {
            var shortest = Enumerable.Range(0, runs.Count).MinBy(i => runs[i].To - runs[i].From);
            if (runs[shortest].To - runs[shortest].From >= ShortestCut)
                break;

            var first = Math.Max(shortest - 1, 0);
            var last = Math.Min(shortest + 1, runs.Count - 1);
            var merged = (runs[first].From, runs[last].To, shortest > 0 ? runs[shortest - 1].Inside : runs[shortest + 1].Inside);
            runs.RemoveRange(first, last - first + 1);
            runs.Insert(first, merged);
        }

        return runs;
    }

    /// A way as pieces, one for each run, the pieces inside the area changed; the first keeps the way's id.
    private List<(long, NetworkWay?, NetworkWay?)> CutWay(NetworkWay way, (double X, double Z)[] points, List<(double From, double To, bool Inside)> runs, Dictionary<long, int> uses, bool guide)
    {
        var along = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            along[i] = along[i - 1] + Math.Sqrt((points[i].X - points[i - 1].X) * (points[i].X - points[i - 1].X) + (points[i].Z - points[i - 1].Z) * (points[i].Z - points[i - 1].Z));

        // The way's points with those the cuts add, and how far along each cut falls.
        var (nodes, lat, lon, at) = (way.Nodes.ToList(), way.Lat.ToList(), way.Lon.ToList(), along.ToList());
        var cuts = new List<double>();
        for (var r = 0; r + 1 < runs.Count; r++)
        {
            var edge = runs[r].To;
            int Nearest(Func<int, bool> allowed) => Enumerable.Range(0, nodes.Count).Where(allowed).DefaultIfEmpty(-1).MinBy(i => i < 0 ? double.MaxValue : Math.Abs(at[i] - edge));
            var junction = Nearest(i => uses.GetValueOrDefault(nodes[i]) > 1);
            var near = Nearest(_ => true);
            if (junction >= 0 && Math.Abs(at[junction] - edge) <= CutAtJunction)
                cuts.Add(at[junction]);
            else if (Math.Abs(at[near] - edge) <= CutAtPoint)
                cuts.Add(at[near]);
            else
            {
                var after = at.FindIndex(s => s > edge);
                var t = (edge - at[after - 1]) / (at[after] - at[after - 1]);
                nodes.Insert(after, _network.NewId());
                lat.Insert(after, lat[after - 1] + (lat[after] - lat[after - 1]) * t);
                lon.Insert(after, lon[after - 1] + (lon[after] - lon[after - 1]) * t);
                at.Insert(after, edge);
                cuts.Add(edge);
            }
        }

        // A piece with nothing left (its cuts fell on one point) goes, as does one wholly behind the cut before it.
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        var start = 0;
        for (var r = 0; r < runs.Count; r++)
        {
            var end = r < cuts.Count ? at.FindIndex(s => s >= cuts[r]) : nodes.Count - 1;
            if (end <= start)
                continue;

            var count = end - start + 1;
            var id = changes.Count == 0 ? way.Id : _network.NewId();
            changes.Add((id, id == way.Id ? way : null, Tagged(way, id, [.. nodes.GetRange(start, count)], [.. lat.GetRange(start, count)], [.. lon.GetRange(start, count)], guide, runs[r].Inside, true)));
            start = end;
        }

        return changes;
    }

    /// A way with these points, made a guide or not if change is set. Pieces of a cut way aren't the way the map has any more.
    private static NetworkWay Tagged(NetworkWay way, long id, long[] nodes, double[] lat, double[] lon, bool guide, bool change, bool cut)
    {
        var tags = new Dictionary<string, string>(way.Tags);
        var wasGuide = tags.TryGetValue(RouteNetwork.GuideTag, out var how);
        if (change && guide)
            tags[RouteNetwork.GuideTag] = tags.ContainsKey(RouteNetwork.EditedTag) || cut ? "edited" : "mapped";
        else if (change)
        {
            if (how == "mapped" && !cut)
                tags.Remove(RouteNetwork.EditedTag);
            tags.Remove(RouteNetwork.GuideTag);
        }
        else if (wasGuide && cut)
            tags[RouteNetwork.GuideTag] = "edited";

        if (cut || (change && guide))
            tags[RouteNetwork.EditedTag] = "yes";
        return new NetworkWay { Id = id, Rail = way.Rail, Class = way.Class, Nodes = nodes, Lat = lat, Lon = lon, Tags = tags };
    }

    /// Puts an edit's ways back as they were, or as it left them.
    public void Apply(NetworkEdit edit, bool undo)
    {
        foreach (var (id, before, after) in edit.Ways)
            _network.Set(id, undo ? before : after);
    }

    // --- Drawing. ---

    /// The rail ways near the camera and their control points, over the view: the hovered one larger, the selected
    /// ones white, shown moved by offset while they're dragged.
    public void Draw(ImDrawListPtr draw, double cx, double cz, Func<double, double, float2?> project, long? hover, IReadOnlySet<long> selected,
        (double X, double Z)? offset)
    {
        Refresh();
        var line = Style.Accent.WithAlpha(0.55f);
        var dot = Style.Accent;
        float2? At(long node)
        {
            var (x, z) = _nodes[node];
            if (offset is { } o && selected.Contains(node))
                (x, z) = (x + o.X, z + o.Z);
            return Math.Abs(x - cx) > Reach || Math.Abs(z - cz) > Reach ? null : project(x, z);
        }

        foreach (var way in _rails)
        {
            float2? previous = null;
            foreach (var node in way.Nodes)
            {
                var screen = At(node);
                if (previous is { } a && screen is { } b)
                    draw.AddLine(a, b, line, Style.S(1.5f));
                previous = screen;
            }
        }

        foreach (var node in _nodes.Keys)
        {
            if (At(node) is not { } p)
                continue;
            var picked = selected.Contains(node);
            var half = Style.S(node == hover ? 5f : picked ? 3.5f : 2.5f);
            draw.AddRectFilled(p - new float2(half), p + new float2(half), node == hover || picked ? Rgba.White : dot);
        }
    }
}
