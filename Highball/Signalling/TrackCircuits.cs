using System.Globalization;
using System.Text.Json;
using Highball.Routes;

namespace Highball.Signalling;

/// An insulated rail joint: where one track circuit ends and the next begins. Manual: placed by hand, so making the
/// automatic joints again leaves it be.
public readonly record struct Joint(double X, double Z, bool Manual);

/// A circuit's id, kept with a point on its track so the id follows the circuit through edits and rebuilds.
public readonly record struct CircuitAnchor(string Id, double X, double Z);

/// The route's track circuits: sections of track whose occupancy the signalling sees as one, each with an id
/// (TrackCircuitID, "TC0042"). Only the joints between them are stored: the circuits are the pieces the track graph
/// falls into when it's cut at every joint, so adding, moving or taking out a joint splits or merges circuits by
/// itself, and a switch is always inside one circuit with everything up to the joints on each of its legs.
///
/// Built whole from the ways, the joints and the ids' anchors (on a job), then only read: which circuit a point on
/// the track is in is a binary search along its edge.
public sealed class TrackCircuits
{
    public sealed class Circuit
    {
        public required int Index { get; init; }
        public string Id { get; set; } = "";
        /// The stretches of track it covers: an edge, and from and to how far along it.
        public List<(int Edge, double From, double To)> Spans { get; } = [];
        public double Length { get; set; }
        /// Switches and crossings inside it.
        public int Junctions { get; set; }
        /// Its id's anchor: the middle of its longest span.
        public double AnchorX { get; set; }
        public double AnchorZ { get; set; }
    }

    /// A joint as placed on the graph: where it is along which edge.
    public readonly record struct Placed(Joint Joint, int Edge, double S);

    /// How far a stored joint or anchor may be from the track and still find it again (after the track was moved).
    public const double SnapDistance = 15.0;

    public TrackGraph Graph { get; }
    public List<Circuit> Circuits { get; } = [];
    public List<Placed> Joints { get; } = [];

    // Along each edge: the joints' distances in order, and the circuit of each stretch between them (one more).
    private readonly double[][] _cuts;
    private readonly int[][] _stretches;

    private TrackCircuits(TrackGraph graph)
    {
        Graph = graph;
        _cuts = new double[graph.Edges.Count][];
        _stretches = new int[graph.Edges.Count][];
    }

    /// The circuit s metres along an edge.
    public Circuit CircuitAt(int edge, double s)
    {
        var cuts = _cuts[edge];
        var i = Array.BinarySearch(cuts, s);
        return Circuits[_stretches[edge][i < 0 ? ~i : i + 1]];
    }

    /// The circuit of the track nearest (x, z), if there's track within the given distance.
    public Circuit? Locate(double x, double z, double within) =>
        Graph.Nearest(x, z, within) is { } at ? CircuitAt(at.Edge, at.S) : null;

    // --- Building. ---

    /// Places the joints on the graph (dropping any with no track near), cuts it into circuits, and names them: each
    /// takes the id of the first anchor on its track, the rest new ids from next on.
    public static TrackCircuits Build(TrackGraph graph, IEnumerable<Joint> joints, IEnumerable<CircuitAnchor> anchors, ref int next)
    {
        var circuits = new TrackCircuits(graph);
        var onEdge = new List<double>[graph.Edges.Count];
        for (var e = 0; e < onEdge.Length; e++)
            onEdge[e] = [];

        foreach (var joint in joints)
        {
            if (graph.Nearest(joint.X, joint.Z, SnapDistance) is not { } at)
                continue;
            var length = graph.Edges[at.Edge].Length;
            if (at.S < 1.0 || at.S > length - 1.0 || onEdge[at.Edge].Any(s => Math.Abs(s - at.S) < 1.0))
                continue; // on a vertex it cuts nothing; on top of another it adds nothing
            onEdge[at.Edge].Add(at.S);
            var (x, z, _, _) = graph.PointAt(at.Edge, at.S);
            circuits.Joints.Add(new Placed(joint with { X = x, Z = z }, at.Edge, at.S));
        }

        // Union-find over every stretch between joints, and every vertex: a stretch is joined to the vertex at
        // either end of it it reaches, so everything not parted by a joint ends up together.
        var first = new int[graph.Edges.Count];
        var count = 0;
        for (var e = 0; e < graph.Edges.Count; e++)
        {
            onEdge[e].Sort();
            circuits._cuts[e] = [.. onEdge[e]];
            first[e] = count;
            count += onEdge[e].Count + 1;
        }

        var parent = new int[count + graph.Vertices.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        foreach (var edge in graph.Edges)
        {
            Union(first[edge.Index], count + edge.From);
            Union(first[edge.Index] + onEdge[edge.Index].Count, count + edge.To);
        }

        var circuitOf = new Dictionary<int, Circuit>();
        Circuit Of(int stretch)
        {
            var root = Find(stretch);
            if (!circuitOf.TryGetValue(root, out var circuit))
            {
                circuitOf[root] = circuit = new Circuit { Index = circuits.Circuits.Count };
                circuits.Circuits.Add(circuit);
            }

            return circuit;
        }

        foreach (var edge in graph.Edges)
        {
            var cuts = circuits._cuts[edge.Index];
            circuits._stretches[edge.Index] = new int[cuts.Length + 1];
            for (var k = 0; k <= cuts.Length; k++)
            {
                var (from, to) = (k == 0 ? 0 : cuts[k - 1], k == cuts.Length ? edge.Length : cuts[k]);
                var circuit = Of(first[edge.Index] + k);
                circuits._stretches[edge.Index][k] = circuit.Index;
                circuit.Spans.Add((edge.Index, from, to));
                circuit.Length += to - from;
            }
        }

        foreach (var vertex in graph.Vertices)
            if (vertex.Junction && vertex.Ends.Count > 0)
            {
                var (edge, atStart) = vertex.Ends[0];
                circuits.CircuitAt(edge, atStart ? 0 : graph.Edges[edge].Length).Junctions++;
            }

        foreach (var circuit in circuits.Circuits)
        {
            var (edge, from, to) = circuit.Spans.MaxBy(s => s.To - s.From);
            var (x, z, _, _) = graph.PointAt(edge, (from + to) * 0.5);
            (circuit.AnchorX, circuit.AnchorZ) = (x, z);
        }

        // Ids: the first anchor found on a circuit's track names it; anchors whose track went keep nothing.
        var taken = new HashSet<string>();
        foreach (var anchor in anchors)
        {
            if (taken.Contains(anchor.Id) || circuits.Locate(anchor.X, anchor.Z, SnapDistance) is not { Id: "" } circuit)
                continue;
            circuit.Id = anchor.Id;
            taken.Add(anchor.Id);
        }

        foreach (var circuit in circuits.Circuits.Where(c => c.Id.Length == 0).OrderBy(c => c.AnchorZ).ThenBy(c => c.AnchorX))
        {
            string id;
            do
                id = string.Create(CultureInfo.InvariantCulture, $"TC{next++:0000}");
            while (taken.Contains(id));
            circuit.Id = id;
            taken.Add(id);
        }

        return circuits;
    }

    /// Joints for the whole graph as a signalling engineer might first lay them out: on every leg of every switch or
    /// crossing, clearance metres out from it (so the switch and its fouling points sit inside one circuit), unless the
    /// leg is too short to part from the next one; then along plain line, every blockLength metres or so, evenly.
    /// The manual joints are kept, and no automatic one goes within minimum metres of one.
    public static List<Joint> Generate(TrackGraph graph, IEnumerable<Joint> manual, double blockLength, double clearance = 35.0, double minimum = 60.0)
    {
        var joints = manual.Where(j => j.Manual).ToList();
        var placedManual = joints.Select(j => graph.Nearest(j.X, j.Z, SnapDistance)).OfType<(int Edge, double S, double Distance)>().ToList();

        foreach (var edge in graph.Edges)
        {
            var atFrom = graph.Vertices[edge.From].Junction;
            var atTo = graph.Vertices[edge.To].Junction;
            var start = atFrom ? clearance : 0.0;
            var end = edge.Length - (atTo ? clearance : 0.0);

            var cuts = new List<double>();
            if (end - start >= minimum)
            {
                if (atFrom)
                    cuts.Add(start);
                if (atTo)
                    cuts.Add(end);

                var blocks = (int)Math.Round((end - start) / blockLength);
                for (var k = 1; k < blocks; k++)
                    cuts.Add(start + (end - start) * k / blocks);
            }

            foreach (var s in cuts)
            {
                if (placedManual.Any(m => m.Edge == edge.Index && Math.Abs(m.S - s) < minimum))
                    continue;
                var (x, z, _, _) = graph.PointAt(edge.Index, s);
                joints.Add(new Joint(x, z, false));
            }
        }

        return joints;
    }
}

/// The route's signalling as saved: Signalling.json in the route's folder, positions as latitude and longitude so
/// they mean the same whatever the projection does.
public sealed class RouteSignalling
{
    public sealed record GeoJoint(double Lat, double Lon, bool Manual);

    public sealed record GeoAnchor(string Id, double Lat, double Lon);

    /// The automatic joints' spacing along plain line, metres.
    public double BlockLength { get; set; } = 1200.0;

    /// The number the next new circuit id takes.
    public int Next { get; set; } = 1;

    public List<GeoJoint> Joints { get; set; } = [];
    public List<GeoAnchor> Circuits { get; set; } = [];

    /// False until the joints have been laid out once: the first build lays them out automatically.
    public bool Laid { get; set; }

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Signalling.json");

    public static RouteSignalling Load(RouteInfo route)
    {
        var path = PathIn(route);
        return File.Exists(path) ? JsonSerializer.Deserialize<RouteSignalling>(File.ReadAllText(path)) ?? new() : new();
    }

    public void Save(RouteInfo route)
    {
        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public List<Joint> WorldJoints(RouteProjection projection) =>
    [
        .. Joints.Select(j =>
        {
            var p = projection.Forward(new GeoPoint(j.Lat, j.Lon));
            return new Joint(p.East, -p.North, j.Manual);
        }),
    ];

    public List<CircuitAnchor> WorldAnchors(RouteProjection projection) =>
    [
        .. Circuits.Select(c =>
        {
            var p = projection.Forward(new GeoPoint(c.Lat, c.Lon));
            return new CircuitAnchor(c.Id, p.East, -p.North);
        }),
    ];

    public static GeoJoint ToGeo(Joint joint, RouteProjection projection)
    {
        var g = projection.Inverse(new RoutePoint(joint.X, -joint.Z));
        return new GeoJoint(g.Lat, g.Lon, joint.Manual);
    }

    public static GeoAnchor ToGeo(CircuitAnchor anchor, RouteProjection projection)
    {
        var g = projection.Inverse(new RoutePoint(anchor.X, -anchor.Z));
        return new GeoAnchor(anchor.Id, g.Lat, g.Lon);
    }
}
