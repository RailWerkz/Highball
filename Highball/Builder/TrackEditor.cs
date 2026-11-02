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

/// Track editing in the 3D view, on the route's network: OpenStreetMap's rail ways, whose nodes are the control
/// points. Moving a node moves every way through it (so junctions stay joined); new track is a new way, joined to
/// whatever it starts or ends on. Every edited or new way is tagged so fetching OpenStreetMap again leaves it be.
public sealed class TrackEditor
{
    private const double Reach = 1500.0;   // control points are shown and picked this close to the camera
    private const float PickPixels = 14f;

    private readonly RouteInfo _route;
    private readonly RouteNetwork _network;
    private readonly Dictionary<long, (double X, double Z)> _nodes = [];
    private readonly List<NetworkWay> _rails = [];
    private int _version = -1;

    public RouteNetwork Network => _network;

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
            if (!way.Rail || way.Nodes.Length < 2 || way.Lat.Length != way.Nodes.Length)
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
    public NetworkEdit? Delete(long node)
    {
        var changes = new List<(long, NetworkWay?, NetworkWay?)>();
        foreach (var way in _network.Ways.Values.Where(w => w.Nodes.Contains(node)).ToList())
        {
            var keep = Enumerable.Range(0, way.Nodes.Length).Where(i => way.Nodes[i] != node).ToArray();
            changes.Add((way.Id, way, keep.Length < 2 ? null
                : Copy(way, [.. keep.Select(i => way.Nodes[i])], [.. keep.Select(i => way.Lat[i])], [.. keep.Select(i => way.Lon[i])])));
        }

        return Commit(changes);
    }

    /// Lays new main-line track through the points. Points on an existing track's segment put a new control point in
    /// that track, so the new one branches off there: a switch.
    public NetworkEdit? Lay(IReadOnlyList<LayPoint> laid)
    {
        if (laid.Count < 2)
            return null;

        // Where the new track leaves or joins a track, it runs along it for a turnout's length first and curves
        // away, rather than meeting it at whatever angle the next point makes.
        var points = laid.ToList();
        if (Lead(points[0], points[1]) is { } leaving)
            points.Insert(1, leaving);
        if (Lead(points[^1], points[^2]) is { } joining)
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
            Id = id, Rail = true, Class = (int)RailClass.Main, Nodes = nodes, Lat = lat, Lon = lon,
            Tags = new Dictionary<string, string> { ["railway"] = "rail", ["usage"] = "main", [RouteNetwork.EditedTag] = "yes" },
        };
        changes[id] = (null, track);
        return Commit([.. changes.Select(c => (c.Key, c.Value.Before, c.Value.After))]);
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

    /// Puts an edit's ways back as they were, or as it left them.
    public void Apply(NetworkEdit edit, bool undo)
    {
        foreach (var (id, before, after) in edit.Ways)
            _network.Set(id, undo ? before : after);
    }

    // --- Drawing. ---

    /// The rail ways near the camera and their control points, over the view: the hovered one larger, a dragged one
    /// shown where it will go, and track being laid.
    public void Draw(ImDrawListPtr draw, double cx, double cz, Func<double, double, float2?> project, long? hover, long? dragging,
        (double X, double Z)? dragTo, IReadOnlyList<LayPoint> laying, (double X, double Z)? next)
    {
        Refresh();
        var line = Style.Accent.WithAlpha(0.55f);
        var dot = Style.Accent;
        float2? At(long node)
        {
            var (x, z) = node == dragging && dragTo is { } d ? d : _nodes[node];
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
            var half = Style.S(node == hover || node == dragging ? 5f : 2.5f);
            draw.AddRectFilled(p - new float2(half), p + new float2(half), node == hover || node == dragging ? Rgba.White : dot);
        }

        float2? previousLaid = null;
        var points = laying.Select(p => (p.X, p.Z)).ToList();
        if (next is { } n && laying.Count > 0)
            points.Add(n);
        foreach (var (x, z) in points)
        {
            var screen = project(x, z);
            if (previousLaid is { } a && screen is { } b)
                draw.AddLine(a, b, Rgba.White, Style.S(2f));
            if (screen is { } s)
                draw.AddRectFilled(s - new float2(Style.S(4f)), s + new float2(Style.S(4f)), Rgba.White);
            previousLaid = screen;
        }
    }
}
