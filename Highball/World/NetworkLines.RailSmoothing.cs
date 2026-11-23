namespace Highball.World;

/// The rail network's heights smoothed as one, once the lines have been fitted and brought to their junctions.
///
/// Every line is fitted on its own and lines meet only at their nodes, so the stages before leave small kinks: a short piece
/// between two junctions takes the whole difference between their heights in a few metres (eased flat at both ends: a step), a
/// siding follows its main line point by point, a pinned span meets the fitted profile beside it. Here every rail point is drawn
/// a little towards its neighbours along the track, through the nodes it shares with other lines as well as along its own
/// line, many times over: a diffusion, so a kink spreads into an even change of grade over some tens of metres while a constant
/// grade stays exactly as it is. A node is one height shared by every line through it, so junctions still meet exactly. Pinned
/// spans, bridges and tunnels (whose decks are straight between their ends and lifted clear) and lines kept from the last
/// rebuild are held, and the track round them eases onto them.
public sealed partial class NetworkLines
{
    /// How many times every point is drawn towards its neighbours, and how far each time. The kinks spread over about
    /// 2 m * sqrt(2 * RailEaseRate * RailEasePasses) each way: some 18 m.
    private const int RailEasePasses = 80;
    private const float RailEaseRate = 0.5f;

    private void SmoothRailNetwork()
    {
        var rails = _lines.Where(l => l.Rail && !l.Mark && l.Count >= 2).ToList();
        if (rails.Count == 0)
            return;

        // Every rail point numbered; points on one node made one (their representative, the first of them).
        var first = new Dictionary<NetworkLine, int>();
        var total = 0;
        foreach (var line in rails)
        {
            first[line] = total;
            total += line.Count;
        }

        var rep = new int[total];
        for (var k = 0; k < total; k++)
            rep[k] = k;
        var atNode = new Dictionary<long, int>();
        foreach (var line in rails)
            foreach (var (node, point) in line.Nodes)
            {
                var k = first[line] + point;
                if (atNode.TryGetValue(node, out var r))
                    rep[k] = r;
                else
                    atNode[node] = k;
            }

        // Each representative's height (the mean of the points it stands for, or a held one's), and whether it's held.
        var y = new float[total];
        var held = new bool[total];
        var members = new int[total];
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p++)
            {
                var r = rep[first[line] + p];
                var fixedHere = line.Frozen || !OnGround(line, p) || Pinned(line, p);
                if (fixedHere && !held[r])
                {
                    (held[r], y[r], members[r]) = (true, line.Y[p], 1);
                    continue;
                }

                if (held[r])
                    continue;
                y[r] += line.Y[p];
                members[r]++;
            }

        for (var r = 0; r < total; r++)
            if (members[r] > 1 && !held[r])
                y[r] /= members[r];

        // Neighbours along the track, weighted by nearness (so with uneven spacing a point eases to the straight line between
        // its neighbours), as flat arrays.
        var degree = new int[total + 1];
        foreach (var line in rails)
            for (var p = 0; p + 1 < line.Count; p++)
            {
                var (a, b) = (rep[first[line] + p], rep[first[line] + p + 1]);
                if (a == b)
                    continue;
                degree[a]++;
                degree[b]++;
            }

        var start = new int[total + 1];
        for (var r = 0; r < total; r++)
            start[r + 1] = start[r] + degree[r];
        var next = (int[])start.Clone();
        var neighbour = new int[start[total]];
        var weight = new float[start[total]];
        foreach (var line in rails)
            for (var p = 0; p + 1 < line.Count; p++)
            {
                var (a, b) = (rep[first[line] + p], rep[first[line] + p + 1]);
                if (a == b)
                    continue;
                var w = 1f / MathF.Max(line.S[p + 1] - line.S[p], 0.05f);
                (neighbour[next[a]], weight[next[a]]) = (b, w);
                next[a]++;
                (neighbour[next[b]], weight[next[b]]) = (a, w);
                next[b]++;
            }

        var work = new float[total];
        for (var pass = 0; pass < RailEasePasses; pass++)
        {
            var from = y;
            Parallel.For(0, total, r =>
            {
                if (rep[r] != r || held[r] || start[r] == start[r + 1])
                {
                    work[r] = from[r];
                    return;
                }

                float sum = 0f, weights = 0f;
                for (var j = start[r]; j < start[r + 1]; j++)
                {
                    sum += from[neighbour[j]] * weight[j];
                    weights += weight[j];
                }

                work[r] = from[r] + RailEaseRate * (sum / weights - from[r]);
            });
            (y, work) = (work, y);
        }

        // (A held point keeps its own height; the rest take their representative's, so lines meeting at a node meet exactly.)
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p++)
                if (!line.Frozen && OnGround(line, p) && !Pinned(line, p))
                    line.Y[p] = y[rep[first[line] + p]];
    }
}
