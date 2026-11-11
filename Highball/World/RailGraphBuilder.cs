using Highball.Trains;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// Builds the trains' rail graph from the laid track's lines (NetworkLines) once their profiles are done: in the game,
/// where the lines are; a server reads the graph from its exported file instead (RailGraphFile).
public static class RailGraphBuilder
{
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
                graph.AddLeg(line.Index, a, 1, new EdgeEnd(edge.Index, true));
                graph.AddLeg(line.Index, b, -1, new EdgeEnd(edge.Index, false));
            }
        }

        AddSwitches(graph, lines.Turnouts);
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
    private static void AddSwitches(RailGraph graph, IEnumerable<Turnout> turnouts)
    {
        foreach (var t in turnouts)
        {
            EdgeEnd? Of(Leg leg) => graph.EndOf(leg.Line.Index, leg.Point, leg.Direction);
            if (Of(t.Toe) is not { } toe || Of(t.Normal) is not { } normal || Of(t.Reverse) is not { } reverse)
            {
                graph.Skipped["switches on lines left out"] = graph.Skipped.GetValueOrDefault("switches on lines left out") + 1;
                continue;
            }

            var vertex = graph.Edges[toe.Edge].VertexAt(toe.AtStart);
            var sw = new RailSwitch { Node = t.Node, Vertex = vertex, Toe = toe, Normal = normal, Reverse = reverse, Side = t.Side, Number = t.Number };
            graph.Switches.Add(sw);
            graph.Vertices[vertex].Switches.Add(sw);
        }
    }
}
