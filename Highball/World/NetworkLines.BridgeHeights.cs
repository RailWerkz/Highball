namespace Highball.World;

/// Bridges high enough for what they cross. A road's deck runs straight from the ground at one end to the ground at the other,
/// which is right where the map's heights already have the embankments, but nothing made it clear what goes underneath: where
/// the ground's flat the deck lay on the road below. So a road bridge is lifted wherever it crosses a road or railway, to that
/// line's height plus the headroom it needs and the deck's own depth, and climbs to it and back down at an easy grade either
/// side (past the bridge's ends, onto the approach embankments, if it must). Where a railway bridges a road, the road dips
/// under it instead: a railway can't climb like that. Side-by-side decks cross the same things and come out level together.
public sealed partial class NetworkLines
{
    /// Headroom under a bridge over a road, and over a railway.
    private const float RoadHeadroom = 5.0f;
    private const float RailHeadroom = 7.0f;
    /// How steeply a road climbs onto a bridge, or dips under one.
    private const float ApproachGrade = 0.05f;
    private const float UnderGrade = 0.06f;
    /// The most a bridge is lifted, or a road dipped, for headroom.
    private const float MostLift = 10f;
    private const float MostDip = 8f;

    /// Every line's segments (but the marks') filed by the grid cells they cross, and each line's place in the list, for LiftBridges
    /// to look only at what's near each bridge. Made once and kept while the lines' places stay put (it's let go when the profiles
    /// begin, and after SpreadForks moves them), as LiftBridges is run several times over.
    private (Dictionary<(int X, int Z), List<(NetworkLine Line, int I)>> Cells, Dictionary<NetworkLine, int> Order)? _liftCells;

    private (Dictionary<(int X, int Z), List<(NetworkLine Line, int I)>> Cells, Dictionary<NetworkLine, int> Order) LiftCells(List<NetworkLine> lines)
    {
        if (_liftCells is { } kept)
            return kept;
        var order = new Dictionary<NetworkLine, int>();
        foreach (var line in lines)
            order[line] = order.Count;
        // (In a local rebuild, only where a bridge laid again looks: a kept one's lifted already.)
        HashSet<(int X, int Z)>? wanted = null;
        if (lines.Any(l => l.Frozen))
        {
            wanted = [];
            foreach (var line in lines.Where(l => !l.Frozen))
                for (var p = 0; p < line.Structures.Length; p++)
                {
                    if (line.Structures[p] != Structure.Bridge)
                        continue;
                    var to = p;
                    while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                        to++;
                    var (bx0, bx1, bz0, bz1) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
                    for (var q = p; q <= to + 1; q++)
                        (bx0, bx1, bz0, bz1) = (Math.Min(bx0, line.X[q]), Math.Max(bx1, line.X[q]), Math.Min(bz0, line.Z[q]), Math.Max(bz1, line.Z[q]));
                    foreach (var cell in CellsOf(bx0, bz0, bx1, bz1))
                        wanted.Add(cell);
                    p = to;
                }
        }

        var made = (Cells(lines, null, wanted), order);
        _liftCells = made;
        return made;
    }

    private void LiftBridges()
    {
        var lines = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        var (cells, order) = LiftCells(lines);
        var near = new HashSet<(NetworkLine Line, int I)>();
        var joins = new Dictionary<NetworkLine, bool>();
        var dips = new Dictionary<NetworkLine, List<(float S, float Height, float Half)>>();
        List<(NetworkLine Line, List<(float S, float Height, float Half)> Needs)> Collect(bool withDips)
        {
        var lifts = new List<(NetworkLine Line, List<(float S, float Height, float Half)> Needs)>();
        foreach (var line in lines)
        {
            // (A kept line, in a local rebuild, is lifted already.)
            if (line.Frozen)
                continue;
            var depth = BridgePlanner.DeckDepth(line);
            var deckHalf = BridgePlanner.DeckHalf(line);
            var needs = new List<(float S, float Height, float Half)>();
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;
                var (bx0, bx1, bz0, bz1) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
                for (var q = first; q <= last; q++)
                    (bx0, bx1, bz0, bz1) = (Math.Min(bx0, line.X[q]), Math.Max(bx1, line.X[q]), Math.Min(bz0, line.Z[q]), Math.Max(bz1, line.Z[q]));

                var mineNodes = line.Nodes.Select(n => n.Node).ToHashSet();
                // What's near (LiftCells), in the order the lines and their segments come, so what's found is found in the same order.
                near.Clear();
                joins.Clear();
                foreach (var cell in CellsOf(bx0, bz0, bx1, bz1))
                    if (cells.TryGetValue(cell, out var inCell))
                        foreach (var segment in inCell)
                            if (segment.Line != line)
                                near.Add(segment);
                foreach (var (other, i) in near.OrderBy(n => order[n.Line]).ThenBy(n => n.I))
                {
                    // A road joined to this one meets it, at its height: it isn't under it.
                    if (!joins.TryGetValue(other, out var joined))
                        joins[other] = joined = other.Nodes.Any(n => mineNodes.Contains(n.Node));
                    if (joined)
                        continue;
                    {
                        if (Math.Max(other.X[i], other.X[i + 1]) < bx0 || Math.Min(other.X[i], other.X[i + 1]) > bx1 ||
                            Math.Max(other.Z[i], other.Z[i + 1]) < bz0 || Math.Min(other.Z[i], other.Z[i + 1]) > bz1)
                            continue;
                        for (var q = first; q < last; q++)
                        {
                            if (Crossing(other, i, line, q) is not { } hit)
                                continue;
                            var s = line.S[q] + (line.S[q + 1] - line.S[q]) * hit.T;
                            var mine = line.Y[q] + (line.Y[q + 1] - line.Y[q]) * hit.T;
                            var otherT = OtherT(other, i, line, q);
                            var theirs = other.Y[i] + (other.Y[i + 1] - other.Y[i]) * (float)otherT;
                            // Which is the one on top: a bridge over ground; of two bridges, the one on the higher level of the
                            // interchange (OpenStreetMap's layer), or failing that the higher.
                            if (other.Structures[i] != Structure.Ground)
                            {
                                var (myLayer, theirLayer) = (LayerOf(line, q), LayerOf(other, i));
                                // (Same level: the higher one is on top; dead level, the later line.)
                                if (theirLayer > myLayer || (theirLayer == myLayer && (theirs > mine + 0.01f || (MathF.Abs(theirs - mine) <= 0.01f && other.Index > line.Index))))
                                    continue;
                            }
                            var headroom = other.Rail ? RailHeadroom : RoadHeadroom;
                            var half = (other.Formation + 1f) / hit.Sine + deckHalf * hit.Cosine / hit.Sine;
                            if (line.Rail)
                            {
                                // A railway over a road: the road dips to clear it.
                                if (!withDips || other.Rail || other.Structures[i] != Structure.Ground)
                                    continue;
                                var t = (float)otherT;
                                var at = other.S[i] + (other.S[i + 1] - other.S[i]) * t;
                                if (!dips.TryGetValue(other, out var list))
                                    dips[other] = list = [];
                                list.Add((at, mine - depth - headroom, (deckHalf + 1f) / hit.Sine));
                            }
                            else
                                needs.Add((s, theirs + headroom + depth, half));
                        }
                    }
                }
            }

            if (needs.Count > 0)
                lifts.Add((line, needs));
        }

        // The climb onto a bridge carries on past its line's end into the road it continues into (and on, until it's down to the
        // ground), or the junction there would average the deck's end back down to the unlifted road.

        return lifts;
        }

        var lifts = Collect(true);

        var ends = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        var uses = new Dictionary<long, int>();
        foreach (var l in _lines.Where(l => !l.Mark))
            foreach (var (node, point) in l.Nodes)
            {
                uses[node] = uses.GetValueOrDefault(node) + 1;
                if (!l.Rail && (point == 0 || point == l.Count - 1))
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add((l, point));
                }
            }

        (NetworkLine Line, int Point)? Onward(NetworkLine line, int point)
        {
            var node = line.Nodes.FirstOrDefault(n => n.Point == point).Node;
            if (node == 0 || uses.GetValueOrDefault(node) != 2 || !ends.TryGetValue(node, out var list) || list.Count != 2)
                return null;
            var other = list[0].Line == line ? list[1] : list[0];
            return other.Line == line ? null : other;
        }

        void Carry(List<(NetworkLine Line, List<(float S, float Height, float Half)> Needs)> set)
        {
            var byLine = set.ToDictionary(l => l.Line, l => l.Needs);
            foreach (var (line, needs) in set.ToList())
                foreach (var (s, height, half) in needs.ToList())
                    foreach (var end in (int[])[0, line.Count - 1])
                    {
                        var (at, from) = (line, end);
                        var distance = MathF.Abs(line.S[end] - s);
                        for (var hop = 0; hop < 4; hop++)
                        {
                            // Down to the ground by here: nothing more to carry on.
                            var top = height - ApproachGrade * MathF.Max(0f, distance - half);
                            if (top <= at.Ground[from] + 0.2f || Onward(at, from) is not { } next)
                                break;
                            var (into, point) = next;
                            var sInto = point == 0 ? -distance : into.S[^1] + distance;
                            if (!byLine.TryGetValue(into, out var list))
                            {
                                byLine[into] = list = [];
                                set.Add((into, list));
                            }

                            list.Add((sInto, height, half));
                            (at, from) = (into, point == 0 ? into.Count - 1 : 0);
                            distance += into.S[^1];
                        }
                    }
        }

        Carry(lifts);

        // Each line lifted (or dipped) to the tent of what it must clear, never more than so much above (or below) the ground.
        var (lifted, most) = (0, 0f);
        bool Lift(NetworkLine line, List<(float S, float Height, float Half)> needs)
        {
            if (line.Frozen)
                return false;
            var changed = false;
            var raised = new bool[line.Count];
            var wanted = new float[line.Count];
            var top = line.Layers.Length > 0 ? line.Layers.Max() : 1;
            for (var p = 0; p < line.Count; p++)
            {
                wanted[p] = float.MinValue;
                var want = float.MinValue;
                foreach (var (s, height, half) in needs)
                    want = MathF.Max(want, MathF.Min(height, line.Ground[p] + MostLift * MathF.Max(1, top)) - ApproachGrade * MathF.Max(0f, MathF.Abs(line.S[p] - s) - half));
                wanted[p] = want;
                if (want <= line.Y[p] + 0.01f)
                    continue;
                most = MathF.Max(most, want - line.Y[p]);
                line.Y[p] = want;
                raised[p] = true;
                changed = true;
            }

            // Each raised stretch one crest, not a hump over each thing crossed: the least shape that never dips, over it all.
            for (var p = 0; p < line.Count; p++)
            {
                if (!raised[p])
                    continue;
                var (a, b) = (Math.Max(p - 1, 0), p);
                while (b + 1 < line.Count && raised[b + 1])
                    b++;
                b = Math.Min(b + 1, line.Count - 1);
                Crest(line, a, b);
                p = b;
            }

            if (changed)
            {
                // Smoothed, then never below what it must clear (smoothing would round the crest off under it).
                Smooth(line, 12f);
                for (var p = 0; p < line.Count; p++)
                    line.Y[p] = MathF.Max(line.Y[p], wanted[p]);
                Smooth(line, 4f);
                for (var p = 0; p < line.Count; p++)
                    line.Y[p] = MathF.Max(line.Y[p], wanted[p]);
            }

            return changed;
        }

        // An interchange's levels one on another: each pass lifts what must clear what's under it as it now is, until none moves.
        for (var pass = 0; pass < 6; pass++)
        {
            if (pass > 0)
            {
                lifts = Collect(false);
                Carry(lifts);
            }

            var any = false;
            foreach (var (line, needs) in lifts)
                if (Lift(line, needs))
                {
                    lifted++;
                    any = true;
                }

            if (!any)
                break;
        }

        // Decks side by side over the same gap come out level with each other: the lower lifted to the higher.
        var levelled = 0;
        float Highest(NetworkLine line, float from, float to)
        {
            var top = float.MinValue;
            for (var p = 0; p < line.Count; p++)
                if (line.S[p] >= from && line.S[p] <= to)
                    top = MathF.Max(top, line.Y[p]);
            return top;
        }

        var level = new List<(NetworkLine Line, List<(float S, float Height, float Half)> Needs)>();
        foreach (var (a, a0, a1, b, b0, b1) in _sideBySide)
        {
            // Decks on different levels of an interchange aren't meant to be level.
            int Mid(NetworkLine l, float from, float to) => LayerOf(l, Math.Clamp(Array.FindIndex(l.S, x => x >= (from + to) * 0.5f), 0, Math.Max(l.Structures.Length - 1, 0)));
            if (Mid(a, a0, a1) != Mid(b, b0, b1))
                continue;
            var (ha, hb) = (Highest(a, a0, a1), Highest(b, b0, b1));
            if (ha == float.MinValue || hb == float.MinValue || MathF.Abs(ha - hb) < 0.3f)
                continue;
            var (low, l0, l1, high) = ha < hb ? (a, a0, a1, hb) : (b, b0, b1, ha);
            if (level.FirstOrDefault(l => l.Line == low) is { Line: not null } had)
                had.Needs.Add(((l0 + l1) * 0.5f, high, (l1 - l0) * 0.5f));
            else
                level.Add((low, [((l0 + l1) * 0.5f, high, (l1 - l0) * 0.5f)]));
        }

        Carry(level);
        foreach (var (line, needs) in level)
            if (Lift(line, needs))
                levelled++;

        CrestDecks();

        var dipped = 0;
        foreach (var (line, needs) in dips)
        {
            if (line.Frozen)
                continue;
            var changed = false;
            for (var p = 0; p < line.Count; p++)
            {
                var want = float.MaxValue;
                foreach (var (s, height, half) in needs)
                    want = MathF.Min(want, MathF.Max(height, line.Ground[p] - MostDip) + UnderGrade * MathF.Max(0f, MathF.Abs(line.S[p] - s) - half));
                if (want >= line.Y[p])
                    continue;
                line.Y[p] = want;
                changed = true;
            }

            if (changed)
            {
                Smooth(line, 12f);
                dipped++;
            }
        }

        RailWerkz.Core.Logging.Log.Info($"Bridges: {lifted} roads lifted to clear what they cross (most {most:0.0} m), {levelled} levelled with the deck beside them, {dipped} roads dipped under railways");
    }

    /// Every road deck one profile from end to end: straight, or a single crest, never a wave.
    private void CrestDecks()
    {
        Parallel.ForEach(_lines.Where(l => !l.Rail && !l.Mark && !l.Frozen), line =>
        {
            var touched = false;
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                Crest(line, p, to + 1);
                touched = true;
                p = to;
            }

            if (touched)
            {
                var before = (float[])line.Y.Clone();
                Smooth(line, 12f);
                // A deck never comes down for the smoothing: only its approaches are rounded.
                for (var p = 0; p < line.Structures.Length; p++)
                    if (line.Structures[p] == Structure.Bridge)
                    {
                        line.Y[p] = MathF.Max(line.Y[p], before[p]);
                        line.Y[p + 1] = MathF.Max(line.Y[p + 1], before[p + 1]);
                    }
                // The smoothing rounds the crest's corners; the deck itself put back on its hull so it can't sag.
                for (var p = 0; p < line.Structures.Length; p++)
                {
                    if (line.Structures[p] != Structure.Bridge)
                        continue;
                    var to = p;
                    while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                        to++;
                    Crest(line, p, to + 1);
                    p = to;
                }
            }
        });
    }

    /// Which level of an interchange a line's segment is on: its OpenStreetMap layer, or for a line without one, on a bridge 1 and
    /// otherwise 0.
    private static int LayerOf(NetworkLine line, int segment) =>
        segment < line.Layers.Length ? line.Layers[segment] : segment < line.Structures.Length && line.Structures[segment] == Structure.Bridge ? 1 : 0;

    /// The points from a to b laid on the upper hull of their heights (against distance along), so between its ends the line only
    /// rises and then falls, never dipping.
    private static void Crest(NetworkLine line, int a, int b)
    {
        var hull = new List<int>();
        for (var p = a; p <= b; p++)
        {
            while (hull.Count >= 2)
            {
                var (i, j) = (hull[^2], hull[^1]);
                // j lies on or under the line from i to p: drop it.
                var cross = (line.S[j] - line.S[i]) * (line.Y[p] - line.Y[i]) - (line.Y[j] - line.Y[i]) * (line.S[p] - line.S[i]);
                if (cross >= 0f)
                    hull.RemoveAt(hull.Count - 1);
                else
                    break;
            }

            hull.Add(p);
        }

        for (var k = 0; k + 1 < hull.Count; k++)
        {
            var (i, j) = (hull[k], hull[k + 1]);
            for (var p = i + 1; p < j; p++)
                line.Y[p] = line.Y[i] + (line.Y[j] - line.Y[i]) * (line.S[p] - line.S[i]) / MathF.Max(line.S[j] - line.S[i], 1e-4f);
        }
    }

    /// How far along segment i of one line its crossing with segment q of another is.
    private static double OtherT(NetworkLine a, int i, NetworkLine b, int q)
    {
        var (ax, az, ux, uz) = (a.X[i], a.Z[i], a.X[i + 1] - a.X[i], a.Z[i + 1] - a.Z[i]);
        var (bx, bz, vx, vz) = (b.X[q], b.Z[q], b.X[q + 1] - b.X[q], b.Z[q + 1] - b.Z[q]);
        var cross = ux * vz - uz * vx;
        if (Math.Abs(cross) < 1e-9)
            return 0.0;
        var (wx, wz) = (bx - ax, bz - az);
        return Math.Clamp((wx * vz - wz * vx) / cross, 0.0, 1.0);
    }
}
