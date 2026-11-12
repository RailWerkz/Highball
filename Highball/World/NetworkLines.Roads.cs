using Highball.Routes;

namespace Highball.World;

/// Roads laid as roads are built rather than as they're mapped: each road smoothed along its whole run between junctions
/// (NetworkLines.RoadSmoothing.cs, so the jitter in its mapped points goes), and a divided road's two carriageways kept a steady
/// median apart, never closer than their edges allow. Done to the road lines whenever the lines are made (the saved track is
/// rails only in what it decides), so the rails can't move for it.
public sealed partial class NetworkLines
{
    /// The least median between two carriageways' edges, how far apart their middles may be to count as a pair, how far along
    /// their median is evened out over, and how far from a junction (or the pair's end) it eases back to as mapped.
    private const float MinMedian = 0.6f;
    private const float PairReach = 60f;
    /// How far along a carriageway its sideways moves are evened out, each way.
    private const float MoveSmooth = 25f;
    private const float MedianSmooth = 150f;
    private const float PairEase = 60f;
    private const float MedianEven = 2f;
    private const float OverlapEase = 15f;

    private void ShapeRoads()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var roads = _lines.Where(l => !l.Rail && !l.Mark && l.Count >= 3).ToList();

        // Nodes more than one line runs through (junctions, level crossings), which stay put.
        var uses = new Dictionary<long, int>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, _) in line.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        EvenWidths(roads, uses);
        var before = roads.ToDictionary(r => r, r => ((double[])r.X.Clone(), (double[])r.Z.Clone()));
        SmoothRoads(roads, uses);
        double Moved(NetworkLine r) => Enumerable.Range(0, r.Count).Max(p => Math.Sqrt((r.X[p] - before[r].Item1[p]) * (r.X[p] - before[r].Item1[p]) + (r.Z[p] - before[r].Item2[p]) * (r.Z[p] - before[r].Item2[p])));
        var fitted = roads.Select(Moved).ToList();
        var fitMoved = (fitted.Count(m => m > 0.5), fitted.DefaultIfEmpty(0).Max());
        foreach (var r in roads)
            before[r] = ((double[])r.X.Clone(), (double[])r.Z.Clone());
        var users = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                if (!users.TryGetValue(node, out var list))
                    users[node] = list = [];
                list.Add((line, point));
            }

        SpreadPairs(roads, uses, users);
        KeepCarriagewaysApart(roads, uses, users);
        // What moving the carriageways and their ends left uneven, smoothed out lightly.
        SmoothRoads(roads, uses, FinalSmoothing);
        var apart = roads.Select(Moved).ToList();
        RailWerkz.Core.Logging.Log.Info($"Roads: fitting moved {fitMoved.Item1} roads over 0.5 m (most {fitMoved.Item2:0.0} m); {_spreadNodes.Count} carriageway pairs spread at their ends; medians moved {apart.Count(m => m > 0.25)} (most {apart.DefaultIfEmpty(0).Max():0.0} m)");

        foreach (var road in roads)
        {
            for (var p = 1; p < road.Count; p++)
                road.S[p] = road.S[p - 1] + (float)Math.Sqrt((road.X[p] - road.X[p - 1]) * (road.X[p] - road.X[p - 1]) + (road.Z[p] - road.Z[p - 1]) * (road.Z[p] - road.Z[p - 1]));
            (road.MinX, road.MaxX, road.MinZ, road.MaxZ) = (road.X.Min(), road.X.Max(), road.Z.Min(), road.Z.Max());
        }

        StretchBridges(uses);
        RailWerkz.Core.Logging.Log.Info($"Roads: {roads.Count} shaped in {watch.ElapsedMilliseconds} ms");
    }

    /// A divided road's carriageways, each mapped on its own, held a steady median apart: where two one-way roads of a class run
    /// side by side (against each other, middles within PairReach), their distance apart is evened out along them and kept at
    /// least their half widths and MinMedian, and each moves half the difference. Near a junction or where they part, it eases
    /// back to as mapped, so the slip roads still meet them.
    private void KeepCarriagewaysApart(List<NetworkLine> roads, Dictionary<long, int> uses, Dictionary<long, List<(NetworkLine Line, int Point)>> users)
    {
        var divided = roads.Where(r => r.Oneway && (RoadClass)r.Class is RoadClass.Highway or RoadClass.Major or RoadClass.Minor).ToList();
        var isDivided = divided.ToHashSet();
        var railNodes = RailNodes();

        // Where a carriageway can't move: a level crossing, the end its pair was spread at, or where it meets another carriageway (a
        // crossing divided road, a slip road) that moves its own way. Anywhere else (a side road, the next way of the same carriageway)
        // the node moves with it, and the roads there follow.
        bool Continues(long node, NetworkLine road) =>
            users[node].Count == 2 && users[node].All(u => isDivided.Contains(u.Line) && (u.Point == 0 || u.Point == u.Line.Count - 1));
        bool Pinned(long node, NetworkLine road) =>
            railNodes.Contains(node) || _spreadNodes.Contains(node) ||
            (!Continues(node, road) && users[node].Any(u => u.Line != road && isDivided.Contains(u.Line)));

        // Their segments by square, PairReach a side, so each point looks only at those near it.
        var grid = new Dictionary<(int, int), List<(NetworkLine Line, int Segment)>>();
        foreach (var road in divided)
            for (var q = 0; q + 1 < road.Count; q++)
            {
                var cell = ((int)Math.Floor((road.X[q] + road.X[q + 1]) * 0.5 / PairReach), (int)Math.Floor((road.Z[q] + road.Z[q + 1]) * 0.5 / PairReach));
                if (!grid.TryGetValue(cell, out var list))
                    grid[cell] = list = [];
                list.Add((road, q));
            }

        var shifts = new Dictionary<NetworkLine, (double X, double Z)[]>();
        Parallel.ForEach(divided, () => new List<(NetworkLine, (double, double)[])>(), (road, _, found) =>
        {
            if (Shift(road, grid, uses, Pinned) is { } shift)
                found.Add((road, shift));
            return found;
        }, found =>
        {
            lock (shifts)
                foreach (var (road, shift) in found)
                    shifts[road] = shift;
        });

        foreach (var (road, shift) in shifts)
            for (var p = 0; p < road.Count; p++)
                (road.X[p], road.Z[p]) = (road.X[p] + shift[p].X, road.Z[p] + shift[p].Z);

        // Each node a carriageway moved at moves by as much (the average where two ways of it meet), and every line there follows it,
        // easing back to where it was before its next junction.
        foreach (var (node, list) in users)
        {
            if (list.Count < 2)
                continue;
            var moved = list.Where(u => shifts.ContainsKey(u.Line)).ToList();
            if (moved.Count == 0 || moved.Any(u => Pinned(node, u.Line)))
                continue;
            var (ax, az) = (moved.Average(u => shifts[u.Line][u.Point].X), moved.Average(u => shifts[u.Line][u.Point].Z));
            foreach (var (line, point) in list)
            {
                var own = shifts.TryGetValue(line, out var s) ? s[point] : (X: 0.0, Z: 0.0);
                var (fx, fz) = (ax - own.X, az - own.Z);
                var by = Math.Sqrt(fx * fx + fz * fz);
                if (by < 0.01)
                    continue;
                Drag(line, point, (fx, fz), MathF.Max(20f, (float)by * 10f), uses);
            }
        }
    }

    /// Nodes a railway runs through.
    private HashSet<long> RailNodes() => _lines.Where(l => l.Rail).SelectMany(l => l.Nodes.Select(n => n.Node)).ToHashSet();

    /// Moves a line's point by so much, and its points either side by less and less, to nothing reach along it (or by its next junction,
    /// which stays put).
    private static void Drag(NetworkLine line, int point, (double X, double Z) by, float reach, Dictionary<long, int> uses)
    {
        var from = line.S[point];
        foreach (var (node, at) in line.Nodes)
            if (at != point && uses.GetValueOrDefault(node) > 1)
                reach = MathF.Min(reach, MathF.Abs(line.S[at] - from));
        reach = MathF.Max(reach, 1e-3f);
        for (var p = 0; p < line.Count; p++)
        {
            var t = 1f - MathF.Abs(line.S[p] - from) / reach;
            if (t <= 0f)
                continue;
            var ease = t * t * (3f - 2f * t);
            (line.X[p], line.Z[p]) = (line.X[p] + by.X * ease, line.Z[p] + by.Z * ease);
        }
    }

    /// The spread of a divided road's carriageways where both are mapped to one node, as where it becomes an undivided road (often
    /// at a junction with a cross road too) or ends at one: they're moved to lie side by side there instead of over each other, their
    /// outer edges on the undivided road's edges where there's one carrying on (or just touching, if it's narrower than the two),
    /// and a little apart where there isn't. Each runs straight on from there, easing onto its mapped way.
    private readonly HashSet<long> _spreadNodes = [];

    private void SpreadPairs(List<NetworkLine> roads, Dictionary<long, int> uses, Dictionary<long, List<(NetworkLine Line, int Point)>> users)
    {
        _spreadNodes.Clear();
        var railNodes = RailNodes();
        var ends = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var road in roads)
            foreach (var (node, point) in road.Nodes)
                if ((point == 0 || point == road.Count - 1) && road.Structures[Math.Min(point, road.Structures.Length - 1)] == Structure.Ground)
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add((road, point));
                }

        foreach (var (node, list) in ends)
        {
            if (list.Count < 2 || railNodes.Contains(node))
                continue;

            // The pair: one-way, alike, one coming in and one going out, leaving the node within about 40 degrees of each other.
            (int A, int B, double Dot)? pair = null;
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (a, b) = (list[i], list[j]);
                    if (!a.Line.Oneway || !b.Line.Oneway || a.Line.Class != b.Line.Class || (a.Point == 0) == (b.Point == 0))
                        continue;
                    var (ax, az) = Away(a.Line, a.Point, 25f);
                    var (bx, bz) = Away(b.Line, b.Point, 25f);
                    var dot = ax * bx + az * bz;
                    if (dot > 0.75 && (pair is null || dot > pair.Value.Dot))
                        pair = (i, j, dot);
                }

            if (pair is not { } found)
                continue;
            var (first, second) = (list[found.A], list[found.B]);
            var (fx, fz) = Away(first.Line, first.Point, 25f);
            var (gx, gz) = Away(second.Line, second.Point, 25f);
            var (dx, dz) = (fx + gx, fz + gz);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
            (dx, dz) = (dx / length, dz / length);
            var (sx, sz) = (-dz, dx);

            // Which side each goes: as they're mapped a little way on.
            var sideFirst = fx * sx + fz * sz - (gx * sx + gz * sz);
            if (Math.Abs(sideFirst) < 0.02)
                continue;

            // A wider road carrying on the other way.
            var main = list.Where(e => e != first && e != second)
                .Where(e => { var (ex, ez) = Away(e.Line, e.Point, 25f); return ex * dx + ez * dz < -0.8; })
                .OrderByDescending(e => e.Line.Width).Cast<(NetworkLine Line, int Point)?>().FirstOrDefault();
            var (wa, wb) = (first.Line.Width, second.Line.Width);
            var span = main is { } m ? MathF.Max(m.Line.Width, wa + wb) : wa + wb + MinMedian;
            var sign = sideFirst > 0 ? 1f : -1f;
            Spread(first, (dx, dz), (sx, sz), sign * (span - wa) * 0.5f, uses);
            Spread(second, (dx, dz), (sx, sz), -sign * (span - wb) * 0.5f, uses);
            _spreadNodes.Add(node);
        }
    }

    /// A line's end moved so far to the side, the line running straight on from there and easing back onto its way.
    /// How long a road takes to shift sideways by so much (a lane added or dropped, carriageways parting): as built, 50 to 1 on a
    /// highway, gentler still the faster the road.
    private static float TaperLength(NetworkLine line, float by) => (Routes.RoadClass)line.Class switch
    {
        Routes.RoadClass.Highway => MathF.Max(120f, MathF.Abs(by) * 55f),
        Routes.RoadClass.Major => MathF.Max(70f, MathF.Abs(by) * 35f),
        _ => MathF.Max(40f, MathF.Abs(by) * 20f),
    };

    private static void Spread((NetworkLine Line, int Point) end, (double X, double Z) onward, (double X, double Z) side, float by, Dictionary<long, int> uses)
    {
        var (line, point) = end;
        var (nx, nz) = (line.X[point], line.Z[point]);
        var from = line.S[point];
        var reach = MathF.Min(TaperLength(line, by), line.S[line.Count - 1] * 0.8f);
        foreach (var (node, at) in line.Nodes)
            if (at != point && uses.GetValueOrDefault(node) > 1)
                reach = MathF.Min(reach, MathF.Abs(line.S[at] - from));
        reach = MathF.Max(reach, 1e-3f);
        for (var p = 0; p < line.Count; p++)
        {
            var d = MathF.Abs(line.S[p] - from);
            if (d >= reach)
                continue;
            var t = d / reach;
            var ease = t * t * (3f - 2f * t);
            var (x, z) = (nx + side.X * by + onward.X * d, nz + side.Z * by + onward.Z * d);
            line.X[p] = x + (line.X[p] - x) * ease;
            line.Z[p] = z + (line.Z[p] - z) * ease;
        }

        for (var p = 1; p < line.Count; p++)
            line.S[p] = line.S[p - 1] + (float)Math.Sqrt((line.X[p] - line.X[p - 1]) * (line.X[p] - line.X[p - 1]) + (line.Z[p] - line.Z[p - 1]) * (line.Z[p] - line.Z[p - 1]));
    }

    /// The way from a line's end to its point so far along it, across the ground.
    private static (double X, double Z) Away(NetworkLine line, int point, float distance)
    {
        var from = line.S[point];
        var step = point == 0 ? 1 : -1;
        var p = point;
        while (p + step >= 0 && p + step < line.Count && MathF.Abs(line.S[p] - from) < distance)
            p += step;
        var (x, z) = (line.X[p] - line.X[point], line.Z[p] - line.Z[point]);
        var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-9);
        return (x / length, z / length);
    }

    /// How far each of a carriageway's points moves to keep the median to the other steady (null: it has no other).
    private static (double X, double Z)[]? Shift(NetworkLine road, Dictionary<(int, int), List<(NetworkLine Line, int Segment)>> grid, Dictionary<long, int> uses,
        Func<long, NetworkLine, bool> pinned)
    {
        var count = road.Count;
        var apart = new float[count];
        var toward = new (double X, double Z)[count];
        var need = new float[count];
        var paired = new bool[count];
        var partner = new NetworkLine?[count];
        var side = new int[count];
        var any = false;
        for (var p = 0; p < count; p++)
        {
            var (fx, fz) = Heading(road, p);
            var best = (double)PairReach * PairReach;
            var (cx, cz) = ((int)Math.Floor(road.X[p] / PairReach), (int)Math.Floor(road.Z[p] / PairReach));
            for (var gz = cz - 1; gz <= cz + 1; gz++)
                for (var gx = cx - 1; gx <= cx + 1; gx++)
                {
                    if (!grid.TryGetValue((gx, gz), out var near))
                        continue;
                    foreach (var (other, q) in near)
                    {
                    if (other == road || other.Class != road.Class)
                        continue;
                    var (ax, az, bx, bz) = (other.X[q], other.Z[q], other.X[q + 1], other.Z[q + 1]);
                    var (dx, dz) = (bx - ax, bz - az);
                    var length2 = dx * dx + dz * dz;
                    if (length2 < 1e-9)
                        continue;
                    var t = Math.Clamp(((road.X[p] - ax) * dx + (road.Z[p] - az) * dz) / length2, 0.0, 1.0);
                    var (ex, ez) = (ax + dx * t - road.X[p], az + dz * t - road.Z[p]);
                    var away = ex * ex + ez * ez;
                    if (away >= best)
                        continue;
                    // Against each other, side by side.
                    var length = Math.Sqrt(length2);
                    if ((dx * fx + dz * fz) / length > -0.9)
                        continue;
                    var d = Math.Sqrt(away);
                    if (d < 1e-3)
                        continue;
                    // Square across, not past the other's end: there it isn't alongside, and moving towards it would pull points along
                    // the road (bunching them up and leaving a gap) rather than across it.
                    var across = ex * -fz + ez * fx;
                    if (Math.Abs(across) < d * 0.9)
                        continue;
                    best = away;
                    var sign = Math.Sign(across);
                    (apart[p], toward[p], need[p], paired[p], partner[p], side[p]) = ((float)Math.Abs(across), (-fz * sign, fx * sign), road.Width * 0.5f + other.Width * 0.5f + MinMedian, true, other, sign);
                    }
                }

            any |= paired[p];
        }

        // A road found on both sides of another crosses it (a ramp bridging over the far carriageway): the two aren't a pair at all.
        var crossed = Enumerable.Range(0, count).Where(p => paired[p]).GroupBy(p => partner[p]!).Where(g => g.Select(p => side[p]).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        if (crossed.Count > 0)
            for (var p = 0; p < count; p++)
                if (paired[p] && crossed.Contains(partner[p]!))
                    paired[p] = false;
        any = paired.Any(b => b);

        if (!any)
            return null;

        // The median evened out along the pair, and never less than the edges allow.
        var target = new float[count];
        var even = new float[count];
        var (low, high, total, weight) = (0, 0, 0.0, 0);
        for (var p = 0; p < count; p++)
        {
            // (A window MedianSmooth long, slid along.)
            while (high < count && road.S[high] <= road.S[p] + MedianSmooth * 0.5f)
            {
                if (paired[high])
                    (total, weight) = (total + apart[high], weight + 1);
                high++;
            }

            while (road.S[low] < road.S[p] - MedianSmooth * 0.5f)
            {
                if (paired[low])
                    (total, weight) = (total - apart[low], weight - 1);
                low++;
            }

            // (Evened out by no more than MedianEven either way: as mapped is nearly right; it's the overlap that's wrong.)
            if (paired[p] && weight > 0)
            {
                even[p] = Math.Clamp((float)(total / weight), apart[p] - MedianEven, apart[p] + MedianEven);
                target[p] = MathF.Max(even[p], need[p]);
            }
        }

        // How fully it's moved: not at all where it can't move (where it's pinned, where the pair ends, and its own ends unless it
        // carries on there), fully PairEase from them. Kept from overlapping (rather than evened out) sooner: OverlapEase from them.
        var from = new float[count];
        Array.Fill(from, float.MaxValue);
        for (var p = 0; p < count; p++)
            if (!paired[p])
                from[p] = 0f;
        var endsFree = new[] { false, false };
        foreach (var (node, point) in road.Nodes)
        {
            if (uses.GetValueOrDefault(node) <= 1)
                continue;
            if (pinned(node, road))
                from[point] = 0f;
            else if (point == 0 || point == count - 1)
                endsFree[point == 0 ? 0 : 1] = true;
        }

        if (!endsFree[0])
            from[0] = 0f;
        if (!endsFree[1])
            from[count - 1] = 0f;
        for (var p = 1; p < count; p++)
            from[p] = MathF.Min(from[p], from[p - 1] + road.S[p] - road.S[p - 1]);
        for (var p = count - 2; p >= 0; p--)
            from[p] = MathF.Min(from[p], from[p + 1] + road.S[p + 1] - road.S[p]);

        static float Ease(float distance, float over)
        {
            var t = Math.Clamp(distance / over, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        // Half the difference each: away from the other where they're too close, towards it where they're further than the median.
        var moves = new double[count];
        for (var p = 0; p < count; p++)
            if (paired[p])
                moves[p] = ((apart[p] - even[p]) * Ease(from[p], PairEase) + (even[p] - target[p]) * Ease(from[p], OverlapEase)) * 0.5;

        // Evened out along the road, so where the nearest other carriageway changes (one way of it ending, the next beginning) the
        // road doesn't step sideways: a sideways move is only ever a gentle one.
        var shift = new (double X, double Z)[count];
        for (var p = 0; p < count; p++)
        {
            if (!paired[p])
                continue;
            var (sum, weights) = (0.0, 0.0);
            for (var q = p; q >= 0 && road.S[p] - road.S[q] < MoveSmooth; q--)
                (sum, weights) = (sum + moves[q] * (MoveSmooth - (road.S[p] - road.S[q])), weights + MoveSmooth - (road.S[p] - road.S[q]));
            for (var q = p + 1; q < count && road.S[q] - road.S[p] < MoveSmooth; q++)
                (sum, weights) = (sum + moves[q] * (MoveSmooth - (road.S[q] - road.S[p])), weights + MoveSmooth - (road.S[q] - road.S[p]));
            var move = sum / Math.Max(weights, 1e-9);
            shift[p] = (toward[p].X * move, toward[p].Z * move);

        }

        return shift;
    }

    /// A short stretch of road (under EvenLength) mapped with more or fewer lanes than the same road either side of it (a turn
    /// lane, as a rule, or a tag left off) takes their width, so the road doesn't swell and shrink every block.
    private const float EvenLength = 200f;

    private static void EvenWidths(List<NetworkLine> roads, Dictionary<long, int> uses)
    {
        var ends = new Dictionary<long, List<NetworkLine>>();
        foreach (var road in roads)
            foreach (var (node, point) in road.Nodes)
                if (point == 0 || point == road.Count - 1)
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add(road);
                }

        NetworkLine? Beyond(NetworkLine road, int point)
        {
            foreach (var (node, at) in road.Nodes)
                if (at == point && uses.GetValueOrDefault(node) == 2 && ends.TryGetValue(node, out var list) && list.Count == 2)
                    return list.FirstOrDefault(l => l != road);
            return null;
        }

        var changes = new List<(NetworkLine Road, float Width)>();
        foreach (var road in roads)
        {
            if (road.S[^1] >= EvenLength)
                continue;
            if (Beyond(road, 0) is not { } a || Beyond(road, road.Count - 1) is not { } b)
                continue;
            if (a.Class != road.Class || b.Class != road.Class || a.Oneway != road.Oneway || b.Oneway != road.Oneway)
                continue;
            if (MathF.Abs(a.Width - b.Width) < 0.01f && MathF.Abs(a.Width - road.Width) >= 0.01f)
                changes.Add((road, a.Width));
        }

        foreach (var (road, width) in changes)
            (road.Width, road.Formation) = (width, width * 0.5f + 1.5f);
    }

    private static (double X, double Z) Heading(NetworkLine line, int p)
    {
        var (a, b) = (Math.Max(0, p - 1), Math.Min(line.Count - 1, p + 1));
        var (dx, dz) = (line.X[b] - line.X[a], line.Z[b] - line.Z[a]);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }
}
