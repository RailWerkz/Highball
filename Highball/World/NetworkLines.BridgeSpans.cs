namespace Highball.World;

/// Bridges long enough for what they cross. A bridge is mapped as a stretch of road or track, and the stretch is often drawn
/// shorter than the gap: its ends land on the edge of the road or track underneath, and the abutments stand on it. So each
/// bridge is lengthened, as far as BridgeStretch each end, until it clears everything on the ground that crosses under any
/// part of it, by its full width (along the bridge, at the angle it crosses) and BridgeClear to spare. It never stretches past a
/// junction or the line's end. Done once the lines are shaped, before the profiles, so the deck, the ramps and the ground follow.
public sealed partial class NetworkLines
{
    /// The furthest a bridge is lengthened at each end: enough for a road crossed at a skew (which a deck crosses the width of over a
    /// long way), short of carrying a bridge on over what only runs alongside it.
    private const float BridgeStretch = 60f;
    /// The flattest crossing that still counts as under a bridge (sine of the angle), rather than alongside it.
    private const float SquareEnough = 0.26f; // (Crossing gives no less than 0.25, about 15 degrees: anything flatter is alongside.)

    /// Bridges found side by side over the same gap (each line's bridged stretch, by distance along it), for their decks to be levelled.
    private readonly List<(NetworkLine A, float A0, float A1, NetworkLine B, float B0, float B1)> _sideBySide = [];
    private const float BridgeClear = 1.5f;

    /// How near a guide's bridged middle line a track has to run to take its bridge.
    private const double GuideBridgeReach = 3.0;

    /// Track laid where there should be a bridge, but laid as plain track, gets one: wherever it runs along a guide's bridge (the
    /// mapped railway it was laid over), and wherever track laid by hand crosses a highway on the ground with no crossing mapped (no
    /// train crosses a freeway at grade). The bridge is then lengthened and lifted as any other.
    private void BridgeTracks(Dictionary<long, int> uses)
    {
        var guides = _lines.Where(l => l.Mark && l.Structures.Contains(Structure.Bridge)).ToList();
        var highways = _lines.Where(l => !l.Rail && !l.Mark && (Routes.RoadClass)l.Class == Routes.RoadClass.Highway).ToList();
        var bridged = 0;
        foreach (var track in _lines.Where(l => l.Rail && !l.Mark && l.Count >= 2))
        {
            var shared = track.Nodes.Select(n => n.Node).ToHashSet();
            var changed = false;
            for (var p = 0; p + 1 < track.Count; p++)
            {
                if (track.Structures[p] != Structure.Ground)
                    continue;
                var (mx, mz) = ((track.X[p] + track.X[p + 1]) * 0.5, (track.Z[p] + track.Z[p + 1]) * 0.5);
                var over = false;
                foreach (var guide in guides)
                {
                    if (guide.MaxX < mx - GuideBridgeReach || guide.MinX > mx + GuideBridgeReach || guide.MaxZ < mz - GuideBridgeReach || guide.MinZ > mz + GuideBridgeReach)
                        continue;
                    for (var q = 0; q + 1 < guide.Count && !over; q++)
                    {
                        if (guide.Structures[q] != Structure.Bridge)
                            continue;
                        var (dx, dz) = (guide.X[q + 1] - guide.X[q], guide.Z[q + 1] - guide.Z[q]);
                        var t = Math.Clamp(((mx - guide.X[q]) * dx + (mz - guide.Z[q]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                        over = Math.Pow(guide.X[q] + dx * t - mx, 2) + Math.Pow(guide.Z[q] + dz * t - mz, 2) < GuideBridgeReach * GuideBridgeReach;
                    }

                    if (over)
                        break;
                }

                if (!over && track.Exact)
                    foreach (var road in highways)
                    {
                        if (road.MaxX < Math.Min(track.X[p], track.X[p + 1]) || road.MinX > Math.Max(track.X[p], track.X[p + 1]) ||
                            road.MaxZ < Math.Min(track.Z[p], track.Z[p + 1]) || road.MinZ > Math.Max(track.Z[p], track.Z[p + 1]) || road.Nodes.Any(n => shared.Contains(n.Node)))
                            continue;
                        for (var i = 0; i + 1 < road.Count && !over; i++)
                            over = road.Structures[i] == Structure.Ground && Crossing(road, i, track, p) is not null;
                        if (over)
                            break;
                    }

                if (over)
                {
                    track.Structures[p] = Structure.Bridge;
                    changed = true;
                }
            }

            if (changed)
                bridged++;
        }

        if (bridged > 0)
            RailWerkz.Core.Logging.Log.Info($"Bridges: {bridged} tracks given the bridges they were laid over");
    }

    private void StretchBridges(Dictionary<long, int> uses)
    {
        BridgeTracks(uses);
        var lines = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        var stretched = 0;
        var most = 0f;
        foreach (var line in lines)
        {
            var junctions = line.Nodes.Where(n => uses.GetValueOrDefault(n.Node) > 1).Select(n => n.Point).ToHashSet();
            var deckHalf = BridgePlanner.DeckHalf(line);
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;

                // How far along this line the bridge has to reach, by everything crossing under it.
                var (from, until) = (line.S[first], line.S[last]);
                var (need0, need1) = (from, until);
                var (look0, look1) = (from - BridgeStretch, until + BridgeStretch);
                var shared = line.Nodes.Select(n => n.Node).ToHashSet();
                // This line's points within the look either side, and the box round them.
                var (q0, q1) = (first, last);
                while (q0 > 0 && line.S[q0] > look0)
                    q0--;
                while (q1 < line.Count - 1 && line.S[q1] < look1)
                    q1++;
                var (bx0, bx1, bz0, bz1) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
                for (var q = q0; q <= q1; q++)
                    (bx0, bx1, bz0, bz1) = (Math.Min(bx0, line.X[q]), Math.Max(bx1, line.X[q]), Math.Min(bz0, line.Z[q]), Math.Max(bz1, line.Z[q]));
                foreach (var other in lines)
                {
                    if (other == line || other.MaxX < bx0 || other.MinX > bx1 || other.MaxZ < bz0 || other.MinZ > bz1)
                        continue;
                    // A road that joins this one is beside it, not under it.
                    if (other.Nodes.Any(n => shared.Contains(n.Node)))
                        continue;
                    for (var i = 0; i + 1 < other.Count; i++)
                    {
                        if (other.Structures[i] != Structure.Ground || Math.Max(other.X[i], other.X[i + 1]) < bx0 || Math.Min(other.X[i], other.X[i + 1]) > bx1 ||
                            Math.Max(other.Z[i], other.Z[i + 1]) < bz0 || Math.Min(other.Z[i], other.Z[i + 1]) > bz1)
                            continue;
                        for (var q = q0; q < q1; q++)
                        {
                            // Only what crosses square enough to be under it, not a road running alongside.
                            if (Crossing(other, i, line, q) is not { } hit || hit.Sine < SquareEnough)
                                continue;
                            var s = line.S[q] + (line.S[q + 1] - line.S[q]) * hit.T;
                            var reach = (other.Formation + BridgeClear) / hit.Sine + deckHalf * hit.Cosine / hit.Sine;
                            // Only what's partly under the bridge already.
                            if (s + reach < from || s - reach > until)
                                continue;
                            need0 = MathF.Min(need0, s - reach);
                            need1 = MathF.Max(need1, s + reach);
                        }
                    }
                }

                need0 = MathF.Max(need0, from - BridgeStretch);
                need1 = MathF.Min(need1, until + BridgeStretch);
                var newFirst = first;
                while (newFirst > 0 && line.S[newFirst] > need0 && !junctions.Contains(newFirst))
                    newFirst--;
                var newLast = last;
                while (newLast < line.Count - 1 && line.S[newLast] < need1 && !junctions.Contains(newLast))
                    newLast++;
                if (newFirst == first && newLast == last)
                    continue;
                for (var k = newFirst; k < newLast; k++)
                    line.Structures[k] = Structure.Bridge;
                stretched++;
                most = MathF.Max(most, MathF.Max(from - line.S[newFirst], line.S[newLast] - until));
                p = newLast - 1;
            }
        }

        var lined = LineUpBridges(lines, uses);
        RailWerkz.Core.Logging.Log.Info($"Bridges: {stretched} lengthened to clear what's under them (most {most:0} m at one end), {_sideBySide.Count} pairs side by side");
    }

    /// Bridges side by side over the same gap (a divided road's carriageways, a slip road beside them) start and end together, as one
    /// structure would: each is lengthened to reach as far as any of them, measured square across, up to BridgeStretch each end.
    private int LineUpBridges(List<NetworkLine> lines, Dictionary<long, int> uses)
    {
        _sideBySide.Clear();
        var runs = new List<(NetworkLine Line, int First, int Last)>();
        foreach (var line in lines)
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                runs.Add((line, p, to + 1));
                p = to;
            }

        (double X, double Z, double Fx, double Fz) Middle((NetworkLine Line, int First, int Last) r)
        {
            var (l, a, b) = r;
            var (fx, fz) = (l.X[b] - l.X[a], l.Z[b] - l.Z[a]);
            var length = Math.Max(Math.Sqrt(fx * fx + fz * fz), 1e-6);
            return ((l.X[a] + l.X[b]) * 0.5, (l.Z[a] + l.Z[b]) * 0.5, fx / length, fz / length);
        }

        // Where a place falls along a line's run, measured square across it (its S), searching near the run.
        static float Along(NetworkLine line, int first, int last, double x, double z)
        {
            var (best, s) = (double.MaxValue, line.S[first]);
            var from = first;
            while (from > 0 && line.S[first] - line.S[from] < BridgeStretch + 20f)
                from--;
            var until = last;
            while (until < line.Count - 1 && line.S[until] - line.S[last] < BridgeStretch + 20f)
                until++;
            for (var q = from; q < until; q++)
            {
                var (dx, dz) = (line.X[q + 1] - line.X[q], line.Z[q + 1] - line.Z[q]);
                var span = Math.Max(dx * dx + dz * dz, 1e-9);
                var t = Math.Clamp(((x - line.X[q]) * dx + (z - line.Z[q]) * dz) / span, 0.0, 1.0);
                var d = Math.Pow(line.X[q] + dx * t - x, 2) + Math.Pow(line.Z[q] + dz * t - z, 2);
                if (d < best)
                    (best, s) = (d, line.S[q] + (line.S[q + 1] - line.S[q]) * (float)t);
            }

            return s;
        }

        var wanted = new (float From, float To)[runs.Count];
        var pairs = new List<(int A, int B)>();
        for (var i = 0; i < runs.Count; i++)
            wanted[i] = (runs[i].Line.S[runs[i].First], runs[i].Line.S[runs[i].Last]);
        for (var i = 0; i < runs.Count; i++)
        {
            var a = Middle(runs[i]);
            var halfA = BridgePlanner.DeckHalf(runs[i].Line);
            var lengthA = runs[i].Line.S[runs[i].Last] - runs[i].Line.S[runs[i].First];
            for (var j = 0; j < runs.Count; j++)
            {
                if (i == j || runs[i].Line == runs[j].Line)
                    continue;
                var b = Middle(runs[j]);
                if (Math.Abs(a.Fx * b.Fx + a.Fz * b.Fz) < 0.95)
                    continue;
                var (dx, dz) = (b.X - a.X, b.Z - a.Z);
                var across = Math.Abs(dx * -a.Fz + dz * a.Fx);
                var along = Math.Abs(dx * a.Fx + dz * a.Fz);
                var lengthB = runs[j].Line.S[runs[j].Last] - runs[j].Line.S[runs[j].First];
                if (across > halfA + BridgePlanner.DeckHalf(runs[j].Line) + 6.0 || along > (lengthA + lengthB) * 0.5)
                    continue;
                var (l, f, t) = runs[i];
                var (lj, fj, tj) = runs[j];
                if (i < j)
                    pairs.Add((i, j));
                var s0 = Along(l, f, t, lj.X[fj], lj.Z[fj]);
                var s1 = Along(l, f, t, lj.X[tj], lj.Z[tj]);
                wanted[i] = (MathF.Min(wanted[i].From, MathF.Min(s0, s1)), MathF.Max(wanted[i].To, MathF.Max(s0, s1)));
            }
        }

        var lined = 0;

        // The pairs, by where each now reaches.
        foreach (var (ia, ib) in pairs)
        {
            (float, float) Reach(int r)
            {
                var (line, first, last) = runs[r];
                while (first > 0 && line.Structures[first - 1] == Structure.Bridge)
                    first--;
                while (last < line.Count - 1 && line.Structures[last] == Structure.Bridge)
                    last++;
                return (line.S[first], line.S[last]);
            }

            var ((a0, a1), (b0, b1)) = (Reach(ia), Reach(ib));
            _sideBySide.Add((runs[ia].Line, a0, a1, runs[ib].Line, b0, b1));
        }

        return lined;
    }

    /// Where segment i of one line crosses segment q of another: how far along q's segment, and the sine and cosine of the angle.
    private static (float T, float Sine, float Cosine)? Crossing(NetworkLine a, int i, NetworkLine b, int q)
    {
        var (ax, az, ux, uz) = (a.X[i], a.Z[i], a.X[i + 1] - a.X[i], a.Z[i + 1] - a.Z[i]);
        var (bx, bz, vx, vz) = (b.X[q], b.Z[q], b.X[q + 1] - b.X[q], b.Z[q + 1] - b.Z[q]);
        var cross = ux * vz - uz * vx;
        if (Math.Abs(cross) < 1e-9)
            return null;
        var (wx, wz) = (bx - ax, bz - az);
        var t = (wx * vz - wz * vx) / cross;
        var r = (wx * uz - wz * ux) / cross;
        if (t < 0.0 || t > 1.0 || r < 0.0 || r > 1.0)
            return null;
        var (lu, lv) = (Math.Sqrt(ux * ux + uz * uz), Math.Sqrt(vx * vx + vz * vz));
        var sine = (float)Math.Max(Math.Abs(cross) / Math.Max(lu * lv, 1e-9), 0.25);
        var cosine = (float)Math.Abs((ux * vx + uz * vz) / Math.Max(lu * lv, 1e-9));
        return ((float)r, sine, cosine);
    }
}
