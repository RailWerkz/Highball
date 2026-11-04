using RailWerkz.Core.Logging;

namespace Highball.World;

/// Takes the kinks out of the track, whatever put them there, knowing what's around it. After the switches are laid out,
/// every place a rail line bends sharper than real track could is re-curved: the shortest stretch either side that lets
/// one smooth curve (two arcs, from the position and heading at one end to those at the other) stay gentle enough. A stretch never
/// runs over what can't move, a switch's model or a node other lines meet at, and a curve that would bring the track
/// closer to a neighbouring one than two trains could pass isn't taken. What it can't repair TrackQuality still reports.
///
/// It runs twice: before the switches are laid out (Prepare), so a switch is laid along a smooth track, and after (Fair), to
/// clean up what's left around them, which can't move.
public static class TrackFairing
{
    /// How close to another track a curve may bring this one, centre to centre, unless it was closer already.
    private const double Clearance = 3.6;
    private const double CellSize = 8.0;
    /// How far either side of a bend a repair may reach.
    private static readonly float[] Reaches = [6f, 10f, 15f, 22f, 32f, 45f, 65f, 90f, 130f];

    /// Fairs the mapped track before the switches are laid out. fixedNodes: nodes other lines meet at.
    public static (int Found, int Repaired) Prepare(IReadOnlyList<NetworkLine> lines, IReadOnlySet<long> fixedNodes)
    {
        var rails = lines.Where(l => l.Rail && !l.Disused).ToList();
        var cells = Cells(rails);
        var (found, repaired) = (0, 0);
        foreach (var line in rails.Where(l => !l.Exact))
        {
            var (f, r) = FairLine(line, null, fixedNodes, cells);
            found += f;
            repaired += r;
        }

        Log.Info($"Track fairing, mapped track: {repaired} of {found} sharp bends repaired");
        return (found, repaired);
    }

    private static Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> Cells(IEnumerable<NetworkLine> rails)
    {
        var cells = new Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>>();
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p++)
            {
                var key = Cell(line.X[p], line.Z[p]);
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = [];
                list.Add((line.X[p], line.Z[p], line));
            }

        return cells;
    }

    /// anchors: the stretches of each line (point ranges) that can't move; fixedNodes: nodes other lines meet at.
    public static (int Found, int Repaired) Fair(IReadOnlyList<NetworkLine> lines, IReadOnlyDictionary<NetworkLine, List<(float From, float To)>> anchors,
        IReadOnlySet<long> fixedNodes)
    {
        var rails = lines.Where(l => l.Rail && !l.Disused).ToList();
        var cells = Cells(rails);
        var (found, repaired) = (0, 0);
        Parallel.ForEach(rails, line =>
        {
            var (f, r) = FairLine(line, anchors.GetValueOrDefault(line), fixedNodes, cells);
            Interlocked.Add(ref found, f);
            Interlocked.Add(ref repaired, r);
        });
        Log.Info($"Track fairing: {repaired} of {found} sharp bends repaired");
        return (found, repaired);
    }

    private static (int X, int Z) Cell(double x, double z) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));

    /// The distance from a point to the nearest other line.
    private static double Nearest(double x, double z, NetworkLine line, Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> cells)
    {
        var best = double.MaxValue;
        var (cx, cz) = Cell(x, z);
        for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
                if (cells.TryGetValue((cx + dx, cz + dz), out var list))
                    foreach (var q in list)
                        if (q.Line != line)
                            best = Math.Min(best, (q.X - x) * (q.X - x) + (q.Z - z) * (q.Z - z));
        return Math.Sqrt(best);
    }

    private static (int Found, int Repaired) FairLine(NetworkLine line, List<(float From, float To)>? anchors, IReadOnlySet<long> fixedNodes,
        Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> cells)
    {
        var (found, repaired) = (0, 0);
        var stuck = new HashSet<int>();
        for (var round = 0; round < 24; round++)
        {
            // What can't move: as the line stands now, since a repair changes distances along it.
            var held = new bool[line.Count];
            foreach (var (node, point) in line.Nodes)
                if (fixedNodes.Contains(node))
                    held[point] = true;
            if (anchors is not null)
                for (var p = 0; p < line.Count; p++)
                    foreach (var (from, to) in anchors)
                        if (p >= from && p <= to)
                            held[p] = true;

            var worst = -1;
            var sharpest = TrackQuality.MinRadius;
            for (var p = 0; p < line.Count; p++)
            {
                if (held[p] || stuck.Contains(p))
                    continue;
                var radius = TrackQuality.RadiusAt(line, p);
                if (radius < sharpest)
                    (worst, sharpest) = (p, radius);
            }

            if (worst < 0)
                break;
            if (round == 0 || !stuck.Contains(worst))
                found++;

            if (Repair(line, worst, held, sharpest, cells))
                repaired++;
            else
                stuck.Add(worst);
        }

        return (found, repaired);
    }

    /// The best curve round a bend, over the shortest reach that makes it gentle enough, or else the gentlest there is.
    private static bool Repair(NetworkLine line, int bend, bool[] held, float current, Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> cells)
    {
        var low = bend;
        while (low > 0 && !held[low])
            low--;
        var high = bend;
        while (high < line.Count - 1 && !held[high])
            high++;

        (int From, int To, double Radius)? best = null;
        (int, int) tried = (-1, -1);
        foreach (var reach in Reaches)
        {
            var from = bend;
            while (from > low && line.S[bend] - line.S[from] < reach)
                from--;
            var to = bend;
            while (to < high && line.S[to] - line.S[bend] < reach)
                to++;
            if ((from, to) == tried || to - from < 2)
                continue;
            tried = (from, to);

            if (Curve(line, from, to) is not { } curve)
                continue;
            var radius = curve.Radius;
            if (radius <= (best?.Radius ?? current * 1.2) || !Clear(line, from, to, curve.Pieces, cells))
                continue;
            best = (from, to, radius);
            if (radius >= TrackQuality.MinRadius)
                break;
        }

        if (best is not { } chosen || Curve(line, chosen.From, chosen.To) is not { } laid)
            return false;

        line.Alignment!.Splice(chosen.From, chosen.To, laid.Pieces);
        line.Alignment.Sample(line, chosen.From, chosen.To);
        TurnoutFitting.Measure(line);
        return true;
    }

    private static ((double X, double Z) Start, (double X, double Z) Leave, (double X, double Z) End, (double X, double Z) Arrive) Ends(NetworkLine line, int from, int to)
    {
        (double X, double Z) Unit(double x, double z)
        {
            var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-9);
            return (x / length, z / length);
        }

        var (start, end) = ((line.X[from], line.Z[from]), (line.X[to], line.Z[to]));
        // Each end's heading from the track just beyond it; at the line's end, it's free, along the chord.
        var before = Math.Max(0, from - 2);
        var after = Math.Min(line.Count - 1, to + 2);
        var leave = from > before ? Unit(line.X[from] - line.X[before], line.Z[from] - line.Z[before]) : Unit(end.Item1 - start.Item1, end.Item2 - start.Item2);
        var arrive = after > to ? Unit(line.X[after] - line.X[to], line.Z[after] - line.Z[to]) : Unit(end.Item1 - start.Item1, end.Item2 - start.Item2);
        return (start, leave, end, arrive);
    }

    private static (List<TrackPiece> Pieces, double Radius, bool OneWay)? Curve(NetworkLine line, int from, int to)
    {
        var (start, leave, end, arrive) = Ends(line, from, to);
        return Alignment.Join(start, leave, end, arrive);
    }

    /// Whether the curve keeps its distance from other tracks: no closer than Clearance, or than it was already.
    private static bool Clear(NetworkLine line, int from, int to, List<TrackPiece> pieces, Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> cells)
    {
        var total = Alignment.LengthOf(pieces);
        var span = Math.Max(line.S[to] - line.S[from], 1e-3f);
        for (var p = from + 1; p < to; p++)
        {
            var (x, z) = Alignment.Position(pieces, total * (line.S[p] - line.S[from]) / span);
            if ((x - line.X[p]) * (x - line.X[p]) + (z - line.Z[p]) * (z - line.Z[p]) < 0.01)
                continue;
            if (TrackQuality.OffMapped(line, x, z, p) > TrackQuality.MaxStray * 2.0)
                return false;
            var now = Nearest(x, z, line, cells);
            if (now < Clearance && now < Nearest(line.X[p], line.Z[p], line, cells) - 0.05)
                return false;
        }

        return true;
    }
}
