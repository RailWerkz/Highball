namespace Highball.World;

/// Measuring the roads as built, everywhere at once, so what's wrong is found by the numbers and not by flying round looking:
/// decks that wave, grades too steep, sharp changes of grade, wobble and too-tight corners in plan, too little headroom under a
/// bridge, a bridge's end standing on another road, and decks side by side at different heights. Each kind counted, with the
/// worst places.
public sealed partial class NetworkLines
{
    public sealed record AuditFinding(string Kind, double X, double Z, float Value, int Line);

    public sealed record AuditReport(Dictionary<string, int> Counts, List<AuditFinding> Worst);

    /// The tightest a road of each class should curve, metres (a link or a street corner can be tighter).
    private static float TightestRoad(Routes.RoadClass road) => road switch
    {
        Routes.RoadClass.Highway => 120f,
        Routes.RoadClass.Major => 40f,
        _ => 12f,
    };

    public AuditReport AuditRoads()
    {
        var counts = new Dictionary<string, int>();
        var findings = new List<AuditFinding>();
        void Add(string kind, NetworkLine line, int p, float value)
        {
            lock (findings)
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                findings.Add(new AuditFinding(kind, line.X[p], line.Z[p], value, line.Index));
            }
        }

        var roads = _lines.Where(l => !l.Rail && !l.Mark && l.Count >= 3).ToList();
        Parallel.ForEach(roads, line =>
        {
            float YAt(float s)
            {
                var i = Array.BinarySearch(line.S, s);
                if (i >= 0)
                    return line.Y[i];
                i = Math.Clamp(~i, 1, line.Count - 1);
                var t = (s - line.S[i - 1]) / MathF.Max(line.S[i] - line.S[i - 1], 1e-4f);
                return line.Y[i - 1] + (line.Y[i] - line.Y[i - 1]) * Math.Clamp(t, 0f, 1f);
            }

            // Grades, over 10 m, and how sharply they change.
            var (worstGrade, gradeAt, worstKink, kinkAt) = (0f, 0, 0f, 0);
            for (var p = 0; p < line.Count; p++)
            {
                var s = line.S[p];
                if (s < 10f || s > line.S[^1] - 10f)
                    continue;
                var (behind, ahead) = ((line.Y[p] - YAt(s - 10f)) / 10f, (YAt(s + 10f) - line.Y[p]) / 10f);
                if (MathF.Abs(ahead) > worstGrade)
                    (worstGrade, gradeAt) = (MathF.Abs(ahead), p);
                if (MathF.Abs(ahead - behind) > worstKink)
                    (worstKink, kinkAt) = (MathF.Abs(ahead - behind), p);
            }

            if (worstGrade > RoadGrade((Routes.RoadClass)line.Class) + 0.02f)
                Add("steeper than its class allows", line, gradeAt, worstGrade * 100f);
            if (worstKink > 0.03f)
                Add("grade changes over 3% in 10 m", line, kinkAt, worstKink * 100f);

            // Deck waves: how far below the deck's own crest it dips.
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (a, b) = (p, to + 1);
                var (sag, sagAt) = (0f, a);
                var hull = new List<int>();
                for (var q = a; q <= b; q++)
                {
                    while (hull.Count >= 2)
                    {
                        var (i, j) = (hull[^2], hull[^1]);
                        if ((line.S[j] - line.S[i]) * (line.Y[q] - line.Y[i]) - (line.Y[j] - line.Y[i]) * (line.S[q] - line.S[i]) >= 0f)
                            hull.RemoveAt(hull.Count - 1);
                        else
                            break;
                    }

                    hull.Add(q);
                }

                for (var k = 0; k + 1 < hull.Count; k++)
                    for (var q = hull[k] + 1; q < hull[k + 1]; q++)
                    {
                        var (i, j) = (hull[k], hull[k + 1]);
                        var under = line.Y[i] + (line.Y[j] - line.Y[i]) * (line.S[q] - line.S[i]) / MathF.Max(line.S[j] - line.S[i], 1e-4f) - line.Y[q];
                        if (under > sag)
                            (sag, sagAt) = (under, q);
                    }

                if (sag > 0.2f)
                    Add("deck dips (waves)", line, sagAt, sag);
                p = to;
            }

            // Plan: wobble against the chord either side, and corners too tight for the class.
            var (wobble, wobbleAt, tightest, tightAt) = (0.0, 0, double.MaxValue, 0);
            for (var p = 0; p < line.Count; p++)
            {
                var (a, b) = (p, p);
                while (a > 0 && line.S[p] - line.S[a] < 6f)
                    a--;
                while (b < line.Count - 1 && line.S[b] - line.S[p] < 6f)
                    b++;
                if (a == p || b == p)
                    continue;
                var (cx, cz) = (line.X[b] - line.X[a], line.Z[b] - line.Z[a]);
                var chord = Math.Sqrt(cx * cx + cz * cz);
                if (chord < 1.0)
                    continue;
                var off = Math.Abs((line.X[p] - line.X[a]) * cz - (line.Z[p] - line.Z[a]) * cx) / chord;
                // Off the chord by the sagitta: a radius.
                var radius = chord * chord / (8.0 * Math.Max(off, 1e-6));
                if (radius < tightest)
                    (tightest, tightAt) = (radius, p);
                // Wobble: off the chord more than its neighbours either side are.
                var neighbours = 0.0;
                foreach (var n in (int[])[Math.Max(p - 1, a), Math.Min(p + 1, b)])
                    neighbours = Math.Max(neighbours, Math.Abs((line.X[n] - line.X[a]) * cz - (line.Z[n] - line.Z[a]) * cx) / chord);
                if (off - neighbours * 0.5 > wobble)
                    (wobble, wobbleAt) = (off - neighbours * 0.5, p);
            }

            if (tightest < TightestRoad((Routes.RoadClass)line.Class) && !line.Junction.Any(j => j < 0f))
                Add($"tighter than {TightestRoad((Routes.RoadClass)line.Class):0} m for its class", line, tightAt, (float)tightest);
        });

        // Headroom under each road bridge, and bridge ends standing on a road.
        var lines = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        foreach (var line in roads)
        {
            var depth = BridgePlanner.DeckDepth(line);
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;
                var shared = line.Nodes.Select(n => n.Node).ToHashSet();
                foreach (var other in lines)
                {
                    if (other == line || other.MaxX < Math.Min(line.X[first], line.X[last]) - 40 || other.MinX > Math.Max(line.X[first], line.X[last]) + 40 ||
                        other.MaxZ < Math.Min(line.Z[first], line.Z[last]) - 40 || other.MinZ > Math.Max(line.Z[first], line.Z[last]) + 40)
                        continue;
                    var joined = other.Nodes.Any(n => shared.Contains(n.Node));
                    for (var i = 0; i + 1 < other.Count; i++)
                    {
                        if (other.Structures[i] != Structure.Ground)
                            continue;
                        for (var q = first; q < last && !joined; q++)
                        {
                            if (Crossing(other, i, line, q) is not { } hit)
                                continue;
                            var mine = line.Y[q] + (line.Y[q + 1] - line.Y[q]) * hit.T;
                            var theirs = other.Y[i] + (other.Y[i + 1] - other.Y[i]) * (float)OtherT(other, i, line, q);
                            var room = mine - depth - theirs;
                            if (room < (other.Rail ? RailHeadroom : RoadHeadroom) - 0.5f)
                                Add("too little headroom under a bridge", line, q, room);
                        }

                        // A bridge end within another road's width (not one it joins).
                        if (joined || other.Rail)
                            continue;
                        foreach (var end in (int[])[first, last])
                        {
                            var (dx, dz) = (other.X[i + 1] - other.X[i], other.Z[i + 1] - other.Z[i]);
                            var span = Math.Max(dx * dx + dz * dz, 1e-9);
                            var t = Math.Clamp(((line.X[end] - other.X[i]) * dx + (line.Z[end] - other.Z[i]) * dz) / span, 0.0, 1.0);
                            var d = Math.Sqrt(Math.Pow(other.X[i] + dx * t - line.X[end], 2) + Math.Pow(other.Z[i] + dz * t - line.Z[end], 2));
                            if (d < other.Width * 0.5f + 1f)
                                Add("bridge end on a road", line, end, (float)d);
                        }
                    }
                }
            }
        }

        foreach (var (a, a0, a1, b, b0, b1) in _sideBySide)
        {
            float Top(NetworkLine l, float from, float to) => Enumerable.Range(0, l.Count).Where(p => l.S[p] >= from && l.S[p] <= to).Select(p => l.Y[p]).DefaultIfEmpty(float.NaN).Max();
            var (ha, hb) = (Top(a, a0, a1), Top(b, b0, b1));
            if (MathF.Abs(ha - hb) > 0.5f)
                Add("decks side by side at different heights", a, Array.FindIndex(a.S, s => s >= (a0 + a1) * 0.5f) is var m and >= 0 ? m : 0, MathF.Abs(ha - hb));
        }

        var worst = findings.GroupBy(f => f.Kind).SelectMany(g => g.OrderByDescending(f => f.Kind.StartsWith("tighter") || f.Kind.StartsWith("too little") || f.Kind.StartsWith("bridge end") ? -f.Value : f.Value).Take(6)).ToList();
        return new AuditReport(counts, worst);
    }
}
