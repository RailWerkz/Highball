using Highball.Routes;

namespace Highball.World;

/// Smoothing the roads as mapped. A road's mapped points jitter, and it's mapped in many ways (split wherever its lanes, its
/// name or a bridge changes), so each is joined on to the next wherever one simply carries on into the other, and the whole run
/// between real junctions is smoothed as one: the way it heads, along it, is averaged over a stretch (wider for faster roads),
/// which takes out the jitter and the corners between mapped points but leaves a curve a curve, then the run is laid along that
/// heading from its start and eased so it still ends where it ends. Only what other lines meet (junctions, level crossings) stays put.
public sealed partial class NetworkLines
{
    /// How far along a road its heading is averaged over (the Gaussian's spread), by class.
    private static double RoadSmoothing(RoadClass road) => road switch
    {
        RoadClass.Highway => 30.0,
        RoadClass.Major => 16.0,
        RoadClass.Minor => 10.0,
        _ => 7.0,
    };

    /// The furthest smoothing may take a road off its mapped way, metres.
    private const double StrayMost = 1.5;

    /// The spread of the light pass after the carriageways are moved.
    private const double FinalSmoothing = 5.0;

    /// The spacing the run is smoothed at.
    private const double SmoothStep = 1.0;

    private void SmoothRoads(List<NetworkLine> roads, Dictionary<long, int> uses, double? only = null)
    {
        // Where only side roads end on a road running through (a T junction), the road's smoothed straight through and their ends
        // follow it; where two lines run through (a crossroads, a level crossing) it stays put.
        var through = new Dictionary<long, int>();
        var railNodes = new HashSet<long>();
        var sideEnds = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                if (line.Rail)
                    railNodes.Add(node);
                if (point > 0 && point < line.Count - 1)
                    through[node] = through.GetValueOrDefault(node) + 1;
                else if (!line.Rail)
                {
                    if (!sideEnds.TryGetValue(node, out var list))
                        sideEnds[node] = list = [];
                    list.Add((line, point));
                }
            }

        bool Soft(long node) => through.GetValueOrDefault(node) == 1 && !railNodes.Contains(node);
        var moved = new System.Collections.Concurrent.ConcurrentDictionary<long, (double X, double Z)>();

        // Each road's runs between the points other lines run through.
        var runs = new List<(NetworkLine Line, int A, int B)>();
        var nodeAt = new Dictionary<NetworkLine, Dictionary<int, long>>();
        foreach (var road in roads)
        {
            var points = road.Nodes.GroupBy(n => n.Point).ToDictionary(g => g.Key, g => g.First().Node);
            nodeAt[road] = points;
            var cuts = new SortedSet<int> { 0, road.Count - 1 };
            foreach (var (point, node) in points)
                if (point > 0 && point < road.Count - 1 && uses.GetValueOrDefault(node) > 1 && !Soft(node))
                    cuts.Add(point);
            var list = cuts.ToList();
            for (var k = 0; k + 1 < list.Count; k++)
                runs.Add((road, list[k], list[k + 1]));
        }

        // A run's end at a road's end joins the run at the other road's end there, if that's all that meets there.
        var ends = new Dictionary<long, List<(int Run, bool Start)>>();
        for (var r = 0; r < runs.Count; r++)
        {
            var (line, a, b) = runs[r];
            foreach (var (point, start) in new[] { (a, true), (b, false) })
            {
                if ((start && point != 0) || (!start && point != line.Count - 1) || !nodeAt[line].TryGetValue(point, out var node) || uses.GetValueOrDefault(node) != 2)
                    continue;
                if (!ends.TryGetValue(node, out var at))
                    ends[node] = at = [];
                at.Add((r, start));
            }
        }

        var link = new Dictionary<(int Run, bool Start), (int Run, bool Start)>();
        foreach (var at in ends.Values)
            if (at.Count == 2 && at[0].Run != at[1].Run)
            {
                link[at[0]] = at[1];
                link[at[1]] = at[0];
            }

        // The chains: from a run whose start (or end) joins nothing, on through each join.
        var seen = new bool[runs.Count];
        var chains = new List<List<(int Run, bool Reversed)>>();
        void Walk(int first, bool reversed)
        {
            var chain = new List<(int Run, bool Reversed)>();
            var (run, back) = (first, reversed);
            while (!seen[run])
            {
                seen[run] = true;
                chain.Add((run, back));
                if (!link.TryGetValue((run, back), out var next))
                    break;
                // Entered next at next.Start: forwards if that's its start.
                (run, back) = (next.Run, !next.Start);
            }

            chains.Add(chain);
        }

        for (var r = 0; r < runs.Count; r++)
            if (!seen[r] && !link.ContainsKey((r, true)))
                Walk(r, false);
        for (var r = 0; r < runs.Count; r++)
            if (!seen[r] && !link.ContainsKey((r, false)))
                Walk(r, true);
        for (var r = 0; r < runs.Count; r++)
            if (!seen[r])
                Walk(r, false);

        Parallel.ForEach(chains, chain =>
        {
            // (A chain of kept roads, in a local rebuild, is finished already.)
            if (chain.All(c => runs[c.Run].Line.Frozen))
                return;
            // The chain's points in order, each with the line points it is (a join is the end of one line and the start of the next).
            var xs = new List<double>();
            var zs = new List<double>();
            var refs = new List<List<(NetworkLine Line, int Point)>>();
            var spread = double.MaxValue;
            foreach (var (r, reversed) in chain)
            {
                var (line, a, b) = runs[r];
                spread = Math.Min(spread, only ?? RoadSmoothing((RoadClass)line.Class));
                for (var k = 0; k <= b - a; k++)
                {
                    var p = reversed ? b - k : a + k;
                    if (k == 0 && refs.Count > 0)
                    {
                        refs[^1].Add((line, p));
                        continue;
                    }

                    xs.Add(line.X[p]);
                    zs.Add(line.Z[p]);
                    refs.Add([(line, p)]);
                }
            }

            // Never much further off its mapped way than StrayMost (wider roads a little more): where the average would take it further
            // (a tight hook at the end of a short ramp) the move is eased off towards that, smoothly, and the rest of the run keeps its
            // full smoothing.
            var most = Math.Max(StrayMost, chain.Max(c => runs[c.Run].Line.Width) * 0.15);
            var smooth = Smoothed(xs, zs, spread);
            if (smooth is not null)
                for (var i = 0; i < xs.Count; i++)
                {
                    var (dx, dz) = (smooth[i].X - xs[i], smooth[i].Z - zs[i]);
                    var keep = 1.0 / Math.Sqrt(1.0 + (dx * dx + dz * dz) / (most * most));
                    smooth[i] = (xs[i] + dx * keep, zs[i] + dz * keep);
                }

            if (smooth is null)
                return;
            for (var i = 1; i + 1 < refs.Count; i++)
                foreach (var (line, p) in refs[i])
                {
                    if (line.Frozen)
                        continue;
                    if (nodeAt[line].TryGetValue(p, out var node) && p > 0 && p < line.Count - 1 && Soft(node))
                        moved[node] = (smooth[i].X - xs[i], smooth[i].Z - zs[i]);
                    (line.X[p], line.Z[p]) = smooth[i];
                }
        });

        // The side roads' ends follow where the road through moved, easing back onto their own way.
        foreach (var (node, by) in moved)
        {
            var distance = Math.Sqrt(by.X * by.X + by.Z * by.Z);
            if (distance < 0.005 || !sideEnds.TryGetValue(node, out var list))
                continue;
            foreach (var (line, point) in list)
                Drag(line, point, by, MathF.Max(15f, (float)distance * 10f), uses);
        }
    }

    /// A run of points smoothed by averaging its heading over spread metres, ends kept where they are. Null if it's too short to.
    private static (double X, double Z)[]? Smoothed(List<double> xs, List<double> zs, double spread)
    {
        var m = xs.Count;
        if (m < 3)
            return null;
        var s = new double[m];
        for (var i = 1; i < m; i++)
            s[i] = s[i - 1] + Math.Sqrt((xs[i] - xs[i - 1]) * (xs[i] - xs[i - 1]) + (zs[i] - zs[i - 1]) * (zs[i] - zs[i - 1]));
        var length = s[^1];
        if (length < 4.0 * SmoothStep)
            return null;

        // Evenly spaced along it.
        var n = (int)Math.Ceiling(length / SmoothStep);
        var step = length / n;
        var (qx, qz) = (new double[n + 1], new double[n + 1]);
        for (int k = 0, i = 0; k <= n; k++)
        {
            var at = k * step;
            while (i < m - 2 && s[i + 1] < at)
                i++;
            var t = s[i + 1] > s[i] ? Math.Clamp((at - s[i]) / (s[i + 1] - s[i]), 0.0, 1.0) : 0.0;
            (qx[k], qz[k]) = (xs[i] + (xs[i + 1] - xs[i]) * t, zs[i] + (zs[i + 1] - zs[i]) * t);
        }

        // Its heading over each step, unwrapped, then averaged (a Gaussian, cut off and weighed again at the ends).
        var heading = new double[n];
        for (var k = 0; k < n; k++)
        {
            heading[k] = Math.Atan2(qz[k + 1] - qz[k], qx[k + 1] - qx[k]);
            if (k > 0)
                heading[k] = heading[k - 1] + Math.IEEERemainder(heading[k] - heading[k - 1], 2.0 * Math.PI);
        }

        var sigma = spread / step;
        var reach = (int)Math.Ceiling(3.0 * sigma);
        var weights = new double[reach + 1];
        for (var j = 0; j <= reach; j++)
            weights[j] = Math.Exp(-0.5 * j * j / (sigma * sigma));
        // Past each end the heading carries on as it was turning there (the run reflected about its end's heading, found by a straight
        // line fitted over its first stretch), so an end in a curve stays in the curve and points the way it did: cut off instead,
        // the average there leans on the far side only, turning the end off its way, and putting it back bends the whole run.
        double EndHeading(bool atStart)
        {
            var count = Math.Clamp((int)Math.Round(sigma), 2, Math.Max(2, n / 3));
            var (sx, sy, sxx, sxy) = (0.0, 0.0, 0.0, 0.0);
            for (var j = 0; j < count; j++)
            {
                var h = heading[atStart ? j : n - 1 - j];
                (sx, sy, sxx, sxy) = (sx + j, sy + h, sxx + j * j, sxy + j * h);
            }

            var slope = (count * sxy - sx * sy) / Math.Max(count * sxx - sx * sx, 1e-9);
            return (sy - slope * sx) / count - slope * 0.5;
        }

        var (h0, h1) = (EndHeading(true), EndHeading(false));
        double HeadingAt(int j) => j < 0 ? 2.0 * h0 - heading[Math.Min(-j - 1, n - 1)] : j >= n ? 2.0 * h1 - heading[Math.Max(2 * n - 1 - j, 0)] : heading[j];
        var smooth = new double[n];
        for (var k = 0; k < n; k++)
        {
            var (sum, total) = (0.0, 0.0);
            for (var j = k - reach; j <= k + reach; j++)
            {
                var w = weights[Math.Abs(j - k)];
                sum += w * HeadingAt(j);
                total += w;
            }

            smooth[k] = sum / total;
        }

        // Laid along that heading from the start, then eased so it ends where it did.
        var (rx, rz) = (new double[n + 1], new double[n + 1]);
        (rx[0], rz[0]) = (qx[0], qz[0]);
        for (var k = 0; k < n; k++)
            (rx[k + 1], rz[k + 1]) = (rx[k] + step * Math.Cos(smooth[k]), rz[k] + step * Math.Sin(smooth[k]));
        var (ex, ez) = (qx[n] - rx[n], qz[n] - rz[n]);
        for (var k = 0; k <= n; k++)
        {
            var t = (double)k / n;
            (rx[k], rz[k]) = (rx[k] + ex * t, rz[k] + ez * t);
        }

        // Each point at the same share of the way along.
        var result = new (double X, double Z)[m];
        for (var i = 0; i < m; i++)
        {
            var at = s[i] / step;
            var k = Math.Clamp((int)Math.Floor(at), 0, n - 1);
            var t = at - k;
            result[i] = (rx[k] + (rx[k + 1] - rx[k]) * t, rz[k] + (rz[k + 1] - rz[k]) * t);
        }

        result[0] = (xs[0], zs[0]);
        result[^1] = (xs[^1], zs[^1]);
        return result;
    }
}
