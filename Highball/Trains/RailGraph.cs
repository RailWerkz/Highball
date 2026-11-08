using Highball.World;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// One end of an edge: the edge, and whether it's the end at s = 0.
public readonly record struct EdgeEnd(int Edge, bool AtStart)
{
    /// The direction a train leaving the vertex along this edge travels in: +1 up s from the start, -1 down from the end.
    public int Departing => AtStart ? 1 : -1;

    public override string ToString() => $"{Edge}{(AtStart ? "<" : ">")}";
}

/// What a vertex is to a train going through it.
public enum RailJunction
{
    /// Track ends: a buffer stop, or the edge of what's built.
    End,
    /// Two edges join and a train simply runs on.
    Through,
    /// One or more switches: where a train goes depends on how they're set.
    Switch,
    /// Two tracks cross on the level and each runs straight over.
    Crossing,
    /// Three or more edges meet in a way no switch was found for; each way in runs on along the straightest way out.
    Other,
}

/// A place where track ends or meets other track.
public sealed class RailVertex
{
    public required int Index { get; init; }
    /// The OpenStreetMap node it stands on, or 0 for the end of a line with none.
    public required long Node { get; init; }
    public required double X { get; init; }
    public required double Z { get; init; }
    public List<EdgeEnd> Ends { get; } = [];
    public List<RailSwitch> Switches { get; } = [];
    public RailJunction Kind { get; internal set; }

    /// Where a train arriving along each end runs on, for every kind but Switch (whose way depends on its setting).
    internal Dictionary<EdgeEnd, EdgeEnd> Onward { get; } = [];
}

/// A switch, as a train sees it: from the toe a train goes to the normal or the reverse route as it's set; from either of
/// those it goes to the toe (and if the switch was set for the other one, it's run through: trailed).
public sealed class RailSwitch
{
    public required long Node { get; init; }
    public required int Vertex { get; init; }
    public required EdgeEnd Toe { get; init; }
    public required EdgeEnd Normal { get; init; }
    public required EdgeEnd Reverse { get; init; }
    /// +1 if the reverse route goes off to the right seen from the toe, -1 to the left.
    public required int Side { get; init; }
    /// Its standard size (frog number), if it was laid out to one.
    public int? Number { get; init; }
    /// Lead curve radius, metres, if it has a size: what limits the speed through the reverse route.
    public float? Radius => Number is { } n ? Turnout.RadiusOf(n) : null;
}

/// The track a train runs on, between two vertices: the exact straights and arcs it was laid as, and the height of the rail
/// tops along it. Everything along it is measured in metres of s from its From vertex, in doubles, so a train's place
/// on it is exact however far the route reaches and however long it runs.
public sealed class RailEdge
{
    public required int Index { get; init; }
    /// The NetworkLine it's part of (by index), and the line's points it runs between.
    public required int Line { get; init; }
    public required int FromPoint { get; init; }
    public required int ToPoint { get; init; }
    public required int From { get; init; }
    public required int To { get; init; }

    public required double Length { get; init; }

    /// Its plan: pieces end to end from s = 0, and where each starts.
    public required TrackPiece[] Pieces { get; init; }
    public required double[] PieceStart { get; init; }

    /// Rail top height at each of the line's points along it, and their s.
    public required double[] HeightS { get; init; }
    public required float[] Height { get; init; }

    public double MinX, MinZ, MaxX, MaxZ;

    /// The vertex at one end.
    public int VertexAt(bool start) => start ? From : To;

    private int PieceAt(double s)
    {
        var i = Array.BinarySearch(PieceStart, s);
        if (i < 0)
            i = ~i - 1;
        return Math.Clamp(i, 0, Pieces.Length - 1);
    }

    /// Where the track's centreline is s metres along, on the ground plan.
    public (double X, double Z) Plan(double s)
    {
        s = Math.Clamp(s, 0.0, Length);
        var i = PieceAt(s);
        return Pieces[i].At(s - PieceStart[i]);
    }

    /// Its heading s metres along, travelling up s: radians in the X-Z plane, as TrackPiece measures it.
    public double Heading(double s)
    {
        s = Math.Clamp(s, 0.0, Length);
        var i = PieceAt(s);
        return Pieces[i].HeadingAt(s - PieceStart[i]);
    }

    /// Signed, travelling up s (positive turns the heading up); 1 / radius.
    public double Curvature(double s) => Pieces[PieceAt(Math.Clamp(s, 0.0, Length))].Curvature;

    /// The rail tops' height s metres along.
    public double HeightAt(double s)
    {
        s = Math.Clamp(s, 0.0, Length);
        var i = Array.BinarySearch(HeightS, s);
        if (i >= 0)
            return Height[i];
        i = ~i;
        if (i <= 0)
            return Height[0];
        if (i >= HeightS.Length)
            return Height[^1];
        var span = HeightS[i] - HeightS[i - 1];
        var t = span > 1e-12 ? (s - HeightS[i - 1]) / span : 0.0;
        return Height[i - 1] + (Height[i] - Height[i - 1]) * t;
    }

    /// How far the window a grade is measured over reaches either side: long enough to smooth the profile's points out, and
    /// short against any train.
    public const double GradeWindow = 10.0;

    /// Rise over run s metres along, travelling up s.
    public double Grade(double s)
    {
        var a = Math.Max(0.0, s - GradeWindow);
        var b = Math.Min(Length, s + GradeWindow);
        return b - a > 1e-6 ? (HeightAt(b) - HeightAt(a)) / (b - a) : 0.0;
    }

    /// The world position of the rail tops' centre s metres along: x east, y up, z south.
    public (double X, double Y, double Z) Position(double s)
    {
        var (x, z) = Plan(s);
        return (x, HeightAt(s), z);
    }

    /// The way out of the vertex at one end, as a unit vector on the plan.
    internal (double X, double Z) Outward(bool start)
    {
        var h = start ? Heading(0.0) : Heading(Length) + Math.PI;
        return (Math.Cos(h), Math.Sin(h));
    }
}

/// The track as trains see it: edges between vertices, and how a train passes through each vertex. Built from the laid
/// track's lines (NetworkLines) once their profiles are done; built again whenever the track changes. Read freely once
/// built; only the switch settings change, and they're asked for as a train needs them.
public sealed partial class RailGraph
{
    public List<RailVertex> Vertices { get; } = [];
    public List<RailEdge> Edges { get; } = [];
    public List<RailSwitch> Switches { get; } = [];

    /// Lines left out, and why: what to put right before trains can use them.
    public Dictionary<string, int> Skipped { get; } = [];

    public double TotalLength => Edges.Sum(e => e.Length);

    /// Whether each switch is set for its reverse route, by its node. Nothing set means every switch lies normal.
    public Func<long, bool> Reversed { get; set; } = _ => false;

    private readonly Dictionary<(int Line, int Point, int Direction), EdgeEnd> _legs = [];

    /// The edge leaving a line's point one way along it (+1 up its points), if there's one.
    public EdgeEnd? EndOf(int line, int point, int direction) => _legs.TryGetValue((line, point, direction), out var end) ? end : null;

    public static RailGraph Build(NetworkLines lines)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var graph = new RailGraph();
        var used = new List<NetworkLine>();
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Count < 2)
                continue;
            var why = line.Mark ? "mapped, not laid" : line.Disused ? "disused" : line.Tracks != 1 ? "more than one track to the line" : null;
            if (why is not null)
            {
                graph.Skipped[why] = graph.Skipped.GetValueOrDefault(why) + 1;
                continue;
            }

            used.Add(line);
        }

        // A vertex at each end of every line and wherever lines share a node.
        var uses = new Dictionary<long, int>();
        foreach (var line in used)
            foreach (var (node, _) in line.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        var byNode = new Dictionary<long, int>();
        int VertexFor(NetworkLine line, int point)
        {
            var node = line.Nodes.FirstOrDefault(n => n.Point == point).Node;
            if (node != 0 && byNode.TryGetValue(node, out var known))
                return known;
            var vertex = new RailVertex { Index = graph.Vertices.Count, Node = node, X = line.X[point], Z = line.Z[point] };
            graph.Vertices.Add(vertex);
            if (node != 0)
                byNode[node] = vertex.Index;
            return vertex.Index;
        }

        foreach (var line in used)
        {
            var points = new SortedSet<int> { 0, line.Count - 1 };
            foreach (var (node, point) in line.Nodes)
                if (uses[node] > 1)
                    points.Add(point);

            var u = AlongOf(line);
            var cuts = points.ToArray();
            for (var k = 0; k + 1 < cuts.Length; k++)
            {
                var (a, b) = (cuts[k], cuts[k + 1]);
                if (u[b] - u[a] < 1e-6)
                    continue;
                var edge = MakeEdge(graph.Edges.Count, line, a, b, u, VertexFor(line, a), VertexFor(line, b));
                graph.Edges.Add(edge);
                graph.Vertices[edge.From].Ends.Add(new EdgeEnd(edge.Index, true));
                graph.Vertices[edge.To].Ends.Add(new EdgeEnd(edge.Index, false));
                graph._legs[(line.Index, a, 1)] = new EdgeEnd(edge.Index, true);
                graph._legs[(line.Index, b, -1)] = new EdgeEnd(edge.Index, false);
            }
        }

        graph.AddSwitches(lines.Turnouts);
        foreach (var vertex in graph.Vertices)
            graph.Classify(vertex);

        Log.Info($"Rail graph: {graph.Vertices.Count} vertices, {graph.Edges.Count} edges, {graph.Switches.Count} switches, " +
                 $"{graph.TotalLength / 1000.0:0.00} km in {watch.ElapsedMilliseconds} ms" +
                 (graph.Skipped.Count > 0 ? "; left out: " + string.Join(", ", graph.Skipped.Select(s => $"{s.Value} {s.Key}")) : ""));
        return graph;
    }

    /// Distance along the line's real shape at each of its points: its alignment's, or along the points themselves if it has
    /// none (track not yet fitted).
    private static double[] AlongOf(NetworkLine line)
    {
        if (line.Alignment is { } alignment && alignment.U.Length == line.Count && alignment.Pieces.Count > 0)
            return alignment.U;
        var u = new double[line.Count];
        for (var i = 1; i < line.Count; i++)
            u[i] = u[i - 1] + Math.Sqrt((line.X[i] - line.X[i - 1]) * (line.X[i] - line.X[i - 1]) + (line.Z[i] - line.Z[i - 1]) * (line.Z[i] - line.Z[i - 1]));
        return u;
    }

    /// The edge along the line from point a to point b: its alignment's pieces cut to that stretch, so the plan is exact.
    private static RailEdge MakeEdge(int index, NetworkLine line, int a, int b, double[] u, int from, int to)
    {
        var (u0, u1) = (u[a], u[b]);
        var pieces = new List<TrackPiece>();
        if (line.Alignment is { } alignment && ReferenceEquals(u, alignment.U))
        {
            var start = 0.0;
            foreach (var piece in alignment.Pieces)
            {
                var (o0, o1) = (Math.Max(start, u0), Math.Min(start + piece.Length, u1));
                if (o1 - o0 > 1e-9)
                {
                    var (x, z) = piece.At(o0 - start);
                    pieces.Add(new TrackPiece(x, z, piece.HeadingAt(o0 - start), o1 - o0, piece.Curvature, piece.Fitted));
                }

                start += piece.Length;
                if (start >= u1)
                    break;
            }
        }

        // No alignment, or it doesn't cover the stretch: chords between the points.
        if (pieces.Count == 0)
            for (var i = a; i < b; i++)
            {
                var (dx, dz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
                pieces.Add(new TrackPiece(line.X[i], line.Z[i], Math.Atan2(dz, dx), Math.Sqrt(dx * dx + dz * dz), 0.0, false));
            }

        var starts = new double[pieces.Count];
        for (var i = 1; i < pieces.Count; i++)
            starts[i] = starts[i - 1] + pieces[i - 1].Length;
        var length = starts[^1] + pieces[^1].Length;

        var heightS = new double[b - a + 1];
        var height = new float[b - a + 1];
        for (var i = a; i <= b; i++)
        {
            heightS[i - a] = Math.Clamp(u[i] - u0, 0.0, length);
            height[i - a] = line.Y[i] + NetworkMeshes.RailTopRise;
        }

        var edge = new RailEdge
        {
            Index = index, Line = line.Index, FromPoint = a, ToPoint = b, From = from, To = to, Length = length,
            Pieces = [.. pieces], PieceStart = starts, HeightS = heightS, Height = height,
            MinX = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxZ = double.MinValue,
        };
        for (var i = a; i <= b; i++)
        {
            edge.MinX = Math.Min(edge.MinX, line.X[i]);
            edge.MaxX = Math.Max(edge.MaxX, line.X[i]);
            edge.MinZ = Math.Min(edge.MinZ, line.Z[i]);
            edge.MaxZ = Math.Max(edge.MaxZ, line.Z[i]);
        }

        return edge;
    }

    /// The turnouts the lines found, as switches on the graph: each of their legs is the edge leaving its node that way.
    private void AddSwitches(IEnumerable<Turnout> turnouts)
    {
        foreach (var t in turnouts)
        {
            EdgeEnd? Of(Leg leg) => EndOf(leg.Line.Index, leg.Point, leg.Direction);
            if (Of(t.Toe) is not { } toe || Of(t.Normal) is not { } normal || Of(t.Reverse) is not { } reverse)
            {
                Skipped["switches on lines left out"] = Skipped.GetValueOrDefault("switches on lines left out") + 1;
                continue;
            }

            var vertex = Edges[toe.Edge].VertexAt(toe.AtStart);
            var sw = new RailSwitch { Node = t.Node, Vertex = vertex, Toe = toe, Normal = normal, Reverse = reverse, Side = t.Side, Number = t.Number };
            Switches.Add(sw);
            Vertices[vertex].Switches.Add(sw);
        }
    }

    private void Classify(RailVertex vertex)
    {
        var ends = vertex.Ends;
        vertex.Onward.Clear();
        if (vertex.Switches.Count > 0)
        {
            vertex.Kind = RailJunction.Switch;
            return;
        }

        switch (ends.Count)
        {
            case 0:
            case 1:
                vertex.Kind = RailJunction.End;
                return;
            case 2:
                vertex.Kind = RailJunction.Through;
                vertex.Onward[ends[0]] = ends[1];
                vertex.Onward[ends[1]] = ends[0];
                return;
        }

        // Each way in runs on along the way out most nearly straight ahead; a level crossing is four ends in two straight
        // pairs.
        var pairs = 0;
        foreach (var arrive in ends)
        {
            var (ax, az) = Edges[arrive.Edge].Outward(arrive.AtStart);
            EdgeEnd? best = null;
            var bestDot = 0.5; // nothing sharper than 60 degrees off straight
            foreach (var leave in ends)
            {
                if (leave == arrive)
                    continue;
                var (lx, lz) = Edges[leave.Edge].Outward(leave.AtStart);
                var dot = -(ax * lx + az * lz); // arriving runs against its own outward way
                if (dot > bestDot)
                    (best, bestDot) = (leave, dot);
            }

            if (best is { } found)
            {
                vertex.Onward[arrive] = found;
                if (bestDot > 0.995)
                    pairs++;
            }
        }

        vertex.Kind = ends.Count == 4 && pairs == 4 && vertex.Onward.All(o => vertex.Onward.GetValueOrDefault(o.Value) == o.Key)
            ? RailJunction.Crossing
            : RailJunction.Other;
    }
}
