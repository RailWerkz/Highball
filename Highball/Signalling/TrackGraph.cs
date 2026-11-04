using Highball.Routes;

namespace Highball.Signalling;

/// The rail network as a graph: a vertex wherever track ends or meets other track (a switch, a crossing, a buffer
/// stop), and an edge for the track between two of them, however many of OpenStreetMap's ways it runs through (two
/// ways that simply follow on make no vertex). Edges are polylines in world metres (x east, z south). Built from a
/// snapshot of the ways, so it can be built on a job, and read freely once built.
public sealed class TrackGraph
{
    public sealed class Vertex
    {
        public required int Index { get; init; }
        public required long Node { get; init; }
        public required double X { get; init; }
        public required double Z { get; init; }
        /// Each edge end here: the edge, and whether it's the edge's start.
        public List<(int Edge, bool AtStart)> Ends { get; } = [];
        public int Degree => Ends.Count;
        /// Three or more ways off it: a switch, a crossing, or something made of them.
        public bool Junction => Degree >= 3;
    }

    public sealed class Edge
    {
        public required int Index { get; init; }
        public required int From { get; init; }
        public required int To { get; init; }
        public required double[] X { get; init; }
        public required double[] Z { get; init; }
        /// Distance along the edge at each point.
        public required double[] S { get; init; }
        public double Length => S[^1];
        public double MinX, MinZ, MaxX, MaxZ;
    }

    private const double CellSize = 200.0;

    private readonly Dictionary<(int, int), List<(int Edge, int Segment)>> _cells = [];

    public List<Vertex> Vertices { get; } = [];
    public List<Edge> Edges { get; } = [];

    /// Every rail way still in use (not disused, not taken out), nodes in world metres.
    public static TrackGraph Build(IReadOnlyList<NetworkWay> ways, RouteProjection projection)
    {
        var graph = new TrackGraph();
        var position = new Dictionary<long, (double X, double Z)>();
        var neighbours = new Dictionary<long, List<(long Node, int Link)>>();
        var links = 0;

        foreach (var way in ways)
        {
            if (!way.Rail || way.Class == (int)RailClass.Disused || way.Nodes.Length < 2 || way.Lat.Length != way.Nodes.Length ||
                way.Tags.ContainsKey(RouteNetwork.RemovedTag))
                continue;

            for (var i = 0; i < way.Nodes.Length; i++)
            {
                if (!position.ContainsKey(way.Nodes[i]))
                {
                    var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                    position[way.Nodes[i]] = (p.East, -p.North);
                }

                if (i == 0 || way.Nodes[i] == way.Nodes[i - 1])
                    continue;
                Neighbours(way.Nodes[i - 1]).Add((way.Nodes[i], links));
                Neighbours(way.Nodes[i]).Add((way.Nodes[i - 1], links));
                links++;
            }
        }

        List<(long, int)> Neighbours(long node)
        {
            if (!neighbours.TryGetValue(node, out var list))
                neighbours[node] = list = [];
            return list;
        }

        // Vertices: every node that isn't simply on the way through (two links).
        var vertexOf = new Dictionary<long, int>();
        int VertexAt(long node)
        {
            if (vertexOf.TryGetValue(node, out var index))
                return index;
            var (x, z) = position[node];
            graph.Vertices.Add(new Vertex { Index = graph.Vertices.Count, Node = node, X = x, Z = z });
            return vertexOf[node] = graph.Vertices.Count - 1;
        }

        foreach (var (node, list) in neighbours)
            if (list.Count != 2)
                VertexAt(node);

        // Edges: from each vertex along each link not yet walked, on through plain nodes to the next vertex. A loop
        // with no vertex on it at all gets one where it's first found.
        var walked = new bool[links];
        void Walk(long start, (long Node, int Link) first)
        {
            if (walked[first.Link])
                return;
            var nodes = new List<long> { start };
            var (previous, step) = (start, first);
            while (true)
            {
                walked[step.Link] = true;
                nodes.Add(step.Node);
                if (vertexOf.ContainsKey(step.Node))
                    break;
                var list = neighbours[step.Node];
                var next = list[0].Link == step.Link ? list[1] : list[0];
                (previous, step) = (step.Node, next);
                if (walked[step.Link])
                    break;
            }

            graph.AddEdge(VertexAt(start), VertexAt(nodes[^1]), nodes.Select(n => position[n]).ToList());
        }

        foreach (var vertex in graph.Vertices.ToList())
            foreach (var link in neighbours[vertex.Node])
                Walk(vertex.Node, link);

        foreach (var (node, list) in neighbours)
        {
            if (list.Count != 2 || (walked[list[0].Link] && walked[list[1].Link]))
                continue;
            VertexAt(node);
            Walk(node, list[0]);
        }

        return graph;
    }

    private void AddEdge(int from, int to, List<(double X, double Z)> points)
    {
        var s = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
            s[i] = s[i - 1] + Math.Sqrt((points[i].X - points[i - 1].X) * (points[i].X - points[i - 1].X) + (points[i].Z - points[i - 1].Z) * (points[i].Z - points[i - 1].Z));

        var edge = new Edge
        {
            Index = Edges.Count, From = from, To = to, X = [.. points.Select(p => p.X)], Z = [.. points.Select(p => p.Z)], S = s,
            MinX = points.Min(p => p.X), MinZ = points.Min(p => p.Z), MaxX = points.Max(p => p.X), MaxZ = points.Max(p => p.Z),
        };
        Edges.Add(edge);
        Vertices[from].Ends.Add((edge.Index, true));
        Vertices[to].Ends.Add((edge.Index, false));

        for (var i = 1; i < points.Count; i++)
        {
            var (ax, az, bx, bz) = (points[i - 1].X, points[i - 1].Z, points[i].X, points[i].Z);
            for (var cz = Cell(Math.Min(az, bz)); cz <= Cell(Math.Max(az, bz)); cz++)
                for (var cx = Cell(Math.Min(ax, bx)); cx <= Cell(Math.Max(ax, bx)); cx++)
                {
                    if (!_cells.TryGetValue((cx, cz), out var list))
                        _cells[(cx, cz)] = list = [];
                    list.Add((edge.Index, i - 1));
                }
        }
    }

    private static int Cell(double v) => (int)Math.Floor(v / CellSize);

    /// The point on the track nearest (x, z) within the given distance: which edge, how far along it, how far off.
    public (int Edge, double S, double Distance)? Nearest(double x, double z, double within)
    {
        (int, double, double)? best = null;
        var bestDistance = within;
        for (var cz = Cell(z - within); cz <= Cell(z + within); cz++)
        {
            for (var cx = Cell(x - within); cx <= Cell(x + within); cx++)
            {
                if (!_cells.TryGetValue((cx, cz), out var list))
                    continue;
                foreach (var (index, segment) in list)
                {
                    var e = Edges[index];
                    var (ax, az, bx, bz) = (e.X[segment], e.Z[segment], e.X[segment + 1], e.Z[segment + 1]);
                    var (dx, dz) = (bx - ax, bz - az);
                    var length = dx * dx + dz * dz;
                    var t = length > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length, 0, 1) : 0;
                    var (px, pz) = (ax + dx * t, az + dz * t);
                    var d = Math.Sqrt((px - x) * (px - x) + (pz - z) * (pz - z));
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = (index, e.S[segment] + (e.S[segment + 1] - e.S[segment]) * t, d);
                    }
                }
            }
        }

        return best;
    }

    /// The point s metres along an edge, and the direction the edge runs there (unit).
    public (double X, double Z, double Dx, double Dz) PointAt(int edge, double s)
    {
        var e = Edges[edge];
        var i = Array.BinarySearch(e.S, s);
        if (i < 0)
            i = ~i;
        i = Math.Clamp(i, 1, e.S.Length - 1);
        var span = e.S[i] - e.S[i - 1];
        var t = span > 0 ? Math.Clamp((s - e.S[i - 1]) / span, 0, 1) : 0;
        var (dx, dz) = (e.X[i] - e.X[i - 1], e.Z[i] - e.Z[i - 1]);
        var length = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        return (e.X[i - 1] + dx * t, e.Z[i - 1] + dz * t, dx / length, dz / length);
    }
}
