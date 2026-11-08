using System.Globalization;
using System.Text;
using Highball.World;

namespace Highball.Trains;

/// Checks a rail graph against the lines it was built from, and that walking it behaves: what --railgraph reports.
public static class RailGraphCheck
{
    private const double Step = 0.5;
    private const double Walk = 2000.0;

    public static string Report(RailGraph graph, NetworkLines lines)
    {
        var text = new StringBuilder();
        void Line(FormattableString s) => text.AppendLine(s.ToString(CultureInfo.InvariantCulture));

        Line($"Rail graph: {graph.Vertices.Count} vertices, {graph.Edges.Count} edges, {graph.Switches.Count} switches, {graph.TotalLength:0.0} m of track");
        foreach (var (why, count) in graph.Skipped)
            Line($"  left out: {count} {why}");
        foreach (var kind in Enum.GetValues<RailJunction>())
            Line($"  {kind}: {graph.Vertices.Count(v => v.Kind == kind)}");
        if (graph.Edges.Count == 0)
            return text.ToString();

        // The plan and heights against the line's own points, and each edge's pieces meeting end to end.
        double planOff = 0, heightOff = 0, pieceGap = 0, vertexGap = 0;
        foreach (var edge in graph.Edges)
        {
            var line = lines.Lines[edge.Line];
            for (var i = edge.FromPoint; i <= edge.ToPoint; i++)
            {
                var s = edge.HeightS[i - edge.FromPoint];
                var (x, z) = edge.Plan(s);
                planOff = Math.Max(planOff, Math.Sqrt((x - line.X[i]) * (x - line.X[i]) + (z - line.Z[i]) * (z - line.Z[i])));
                heightOff = Math.Max(heightOff, Math.Abs(edge.HeightAt(s) - (line.Y[i] + NetworkMeshes.RailTopRise)));
            }

            for (var k = 0; k + 1 < edge.Pieces.Length; k++)
            {
                var (ex, ez) = edge.Pieces[k].At(edge.Pieces[k].Length);
                pieceGap = Math.Max(pieceGap, Math.Sqrt((ex - edge.Pieces[k + 1].X) * (ex - edge.Pieces[k + 1].X) + (ez - edge.Pieces[k + 1].Z) * (ez - edge.Pieces[k + 1].Z)));
            }

            foreach (var start in (bool[])[true, false])
            {
                var v = graph.Vertices[edge.VertexAt(start)];
                var (px, pz) = edge.Plan(start ? 0.0 : edge.Length);
                vertexGap = Math.Max(vertexGap, Math.Sqrt((px - v.X) * (px - v.X) + (pz - v.Z) * (pz - v.Z)));
            }
        }

        Line($"  plan off the line's points: {planOff * 1000.0:0.000} mm; heights: {heightOff * 1000.0:0.000} mm");
        Line($"  gaps between pieces: {pieceGap * 1000.0:0.000} mm; edge ends off their vertex: {vertexGap * 1000.0:0.000} mm");

        // Walks: from the middle of edges, 2 km on in half-metre steps. No step may jump; walking back must come home; one
        // long move must land where the many short ones did.
        var walked = 0;
        double worstJump = 0, worstHome = 0, worstSplit = 0, distance = 0;
        var ends = 0;
        var every = Math.Max(1, graph.Edges.Count / 300);
        for (var e = 0; e < graph.Edges.Count; e += every)
        {
            var start = new TrackPosition(e, graph.Edges[e].Length * 0.5, 1);
            var at = start;
            var (lx, ly, lz) = graph.PositionOf(at);
            var gone = 0.0;
            while (gone < Walk)
            {
                var move = graph.Move(ref at, Step);
                var (x, y, z) = graph.PositionOf(at);
                var jump = Math.Sqrt((x - lx) * (x - lx) + (y - ly) * (y - ly) + (z - lz) * (z - lz));
                worstJump = Math.Max(worstJump, jump - move.Moved);
                (lx, ly, lz) = (x, y, z);
                gone += move.Moved;
                if (move.Stop != TrackStop.None)
                {
                    ends++;
                    break;
                }
            }

            var once = start;
            graph.Move(ref once, gone);
            worstSplit = Math.Max(worstSplit, Apart(graph, once, at));

            var home = at;
            graph.Move(ref home, -gone);
            worstHome = Math.Max(worstHome, Apart(graph, home, start));
            distance += gone;
            walked++;
        }

        Line($"  walked from {walked} edges, {distance / 1000.0:0.0} km in all, {ends} reached the end of the track");
        Line($"  worst jump past a step: {worstJump * 1000.0:0.000} mm; one move against many: {worstSplit * 1e6:0.000} um; back home off by {worstHome * 1e6:0.000} um");

        // Switches: up to each from its toe, then on through, set each way.
        var wrong = 0;
        var saved = graph.Reversed;
        foreach (var sw in graph.Switches)
        {
            foreach (var reversed in (bool[])[false, true])
            {
                graph.Reversed = node => node == sw.Node ? reversed : saved(node);
                var toe = graph.Edges[sw.Toe.Edge];
                var back = Math.Min(1.0, toe.Length * 0.5);
                // Facing the switch, a metre short of it on its toe edge.
                var at = sw.Toe.AtStart ? new TrackPosition(toe.Index, back, -1) : new TrackPosition(toe.Index, toe.Length - back, 1);
                graph.Move(ref at, back + 0.01);
                var want = reversed ? sw.Reverse : sw.Normal;
                if (at.Edge != want.Edge)
                    wrong++;
            }
        }

        graph.Reversed = saved;
        Line($"  switches routed the wrong way: {wrong} of {graph.Switches.Count * 2} tries");
        return text.ToString();
    }

    private static double Apart(RailGraph graph, TrackPosition a, TrackPosition b)
    {
        var (ax, ay, az) = graph.PositionOf(a);
        var (bx, by, bz) = graph.PositionOf(b);
        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));
    }
}
