using System.Globalization;
using System.Text;
using Highball.Routes;

namespace Highball.World;

/// One thing wrong with a route's track as generated. Severe: wrong to look at or drive over; else worth knowing about.
public sealed record AuditFinding(string Check, bool Severe, double X, double Z, string Detail);

/// Goes over the finished track looking for everything that can be wrong with it, however it came about: `--audit file` writes the
/// lot, by check, with where each one is. It measures what was built, not what the fitting meant to do.
public static class TrackAudit
{
    /// Tracks closer together than this, side by side, foul one another.
    private const double Fouling = 3.2;
    private const float FoulingRun = 15f;
    private const double GapWithin = 8.0;
    private const float ShortStub = 8f;
    private const float KinkInner = 1.5f;
    private const float KinkOuter = 8f;
    private const double KinkDegrees = 1.5;
    private const double JoinKinkDegrees = 2.0;
    private const double NodeOff = 0.03;
    private const float TieGap = 2f;
    private const float SteepGrade = 0.04f;
    private const float GradeWindow = 100f;
    private const float HighAboveGround = 6f;
    private const double CellSize = 25.0;

    private readonly record struct Segment(NetworkLine Line, int Index, double Ax, double Az, double Bx, double Bz);

    private static HashSet<TileId> _built = [];
    private static double _tileSize = 2000.0;
    private const float EdgeReach = 80f;

    /// Whether a line's end is where the built area stops.
    private static bool EdgeEnd(NetworkLine line, int point)
    {
        var (x, z) = (line.X[point], line.Z[point]);
        foreach (var (dx, dz) in ((double, double)[])[(-4.0, 0.0), (4.0, 0.0), (0.0, -4.0), (0.0, 4.0)])
            if (!_built.Contains(new TileId((int)Math.Floor((x + dx) / _tileSize), (int)Math.Floor(-(z + dz) / _tileSize))))
                return true;
        return false;
    }

    public static List<AuditFinding> Run(NetworkLines lines, IReadOnlyList<PlacedObject> objects, RouteProjection projection, HashSet<TileId> built, double tileSize)
    {
        var findings = new List<AuditFinding>();
        var rails = lines.Lines.Where(l => l.Rail && !l.Disused).ToList();
        _built = built;
        _tileSize = tileSize;

        // A junction by the edge of the built area has its tracks cut there, which is the edge, not a fault.
        bool AtEdge(long node) => rails.Any(l => l.Nodes.Any(n => n.Node == node && ((EdgeEnd(l, 0) && l.S[n.Point] < EdgeReach) || (EdgeEnd(l, l.Count - 1) && l.S[^1] - l.S[n.Point] < EdgeReach))));

        // Junctions as the layouts read them.
        foreach (var layout in lines.Layouts.Where(l => l.Issue is not null))
        {
            var issue = layout.Issue!;
            var check = issue.StartsWith("Long switch") ? "Long switch" : issue.StartsWith("Sharp bend") ? "Sharp bend" : issue.StartsWith("Height step") ? "Height step" :
                issue.StartsWith("No room") ? "No room for a switch" : issue.StartsWith("Switch left as mapped") ? "Switch left as mapped" : issue;
            findings.Add(new AuditFinding(check, !AtEdge(layout.Nodes[0]), layout.X, layout.Z, $"{issue} [{Describe(lines, layout.Nodes[0])}]"));
        }

        // (A switch's diverging route easing over and back to run alongside is what it should do.)
        bool InSwitch(double x, double z) => rails.Any(l => x >= l.MinX && x <= l.MaxX && z >= l.MinZ && z <= l.MaxZ && l.TurnoutZones.Any(q => (q.Turnout.X - x) * (q.Turnout.X - x) + (q.Turnout.Z - z) * (q.Turnout.Z - z) < 100.0 * 100.0));
        foreach (var wiggle in TrackQuality.Find(lines).Where(d => d.Kind == DefectKind.Wiggle && !InSwitch(d.X, d.Z)))
            findings.Add(new AuditFinding("Wiggle", true, wiggle.X, wiggle.Z, string.Create(CultureInfo.InvariantCulture, $"Curves one way then the other, {wiggle.Value:0} m radius")));

        Kinks(rails, findings);
        Nodes(rails, findings, [.. lines.Layouts.Where(l => l.Kind == JunctionKind.Diamond).SelectMany(l => l.Nodes)]);
        Ends(rails, findings);
        CrossingsAndFouling(rails, findings);
        SwitchClashes(lines, findings);
        Ties(rails, findings, lines.CrossingAssemblies);
        Stray(rails, findings);
        Profile(rails, findings);
        Roads(lines, rails, objects, projection, findings);
        Placed(lines, objects, projection, findings);
        return findings;
    }

    /// A node's lines, for finding out what's there.
    private static string Describe(NetworkLines lines, long node)
    {
        var parts = new List<string>();
        foreach (var line in lines.Lines.Where(l => l.Rail))
            foreach (var (n, point) in line.Nodes.Where(q => q.Node == node))
                foreach (var direction in (int[])[-1, 1])
                {
                    var q = point + direction;
                    if (q < 0 || q >= line.Count)
                        continue;
                    var leg = new Leg(line, point, direction);
                    var (hx, hz) = Turnout.Heading(leg);
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"line {line.Index} heading {Math.Atan2(hx, -hz) * 180.0 / Math.PI:0} length {leg.Length:0}"));
                }

        var legs = new List<Leg>();
        foreach (var line in lines.Lines.Where(l => l.Rail))
            foreach (var (_, point) in line.Nodes.Where(q => q.Node == node))
                foreach (var direction in (int[])[-1, 1])
                    if (point + direction >= 0 && point + direction < line.Count)
                        legs.Add(new Leg(line, point, direction));
        var why = legs.Count == 3 ? $" ({Turnout.Diagnose(node, legs, lines.Fitted)}; fit: {lines.SwitchOptions.GetValueOrDefault(node)?.Why ?? "ok"}{(lines.Fitted.Keys.Any(k => k.Node == node) ? "" : ", not fitted")})" : "";
        return $"node {node}{why}: {string.Join("; ", parts)}";
    }

    private static (double X, double Z) At(NetworkLine line, float s)
    {
        s = Math.Clamp(s, 0f, line.S[^1]);
        var i = Array.BinarySearch(line.S, s);
        if (i >= 0)
            return (line.X[i], line.Z[i]);
        i = Math.Clamp(~i, 1, line.Count - 1);
        var t = (s - line.S[i - 1]) / MathF.Max(line.S[i] - line.S[i - 1], 1e-4f);
        return (line.X[i - 1] + (line.X[i] - line.X[i - 1]) * t, line.Z[i - 1] + (line.Z[i] - line.Z[i - 1]) * t);
    }

    private static double Turn(NetworkLine line, float s, float window)
    {
        var (a, b, c) = (At(line, s - window), At(line, s), At(line, s + window));
        var (ux, uz, vx, vz) = (b.X - a.X, b.Z - a.Z, c.X - b.X, c.Z - b.Z);
        return Math.Atan2(ux * vz - uz * vx, ux * vx + uz * vz);
    }

    /// A sudden change of direction over a metre or two that the curve around it doesn't account for.
    private static void Kinks(List<NetworkLine> rails, List<AuditFinding> findings)
    {
        foreach (var line in rails)
        {
            (double Excess, int Point)? run = null;
            for (var p = 0; p < line.Count; p++)
            {
                var s = line.S[p];
                var excess = 0.0;
                if (s > KinkOuter && s < line.S[^1] - KinkOuter)
                {
                    var (inner, outer) = (Turn(line, s, KinkInner), Turn(line, s, KinkOuter));
                    excess = Math.Abs(inner - outer * KinkInner / KinkOuter) * 180.0 / Math.PI;
                }

                if (excess >= KinkDegrees)
                    run = run is { } r && r.Excess >= excess ? r : (excess, p);
                else if (run is { } done)
                {
                    findings.Add(new AuditFinding("Kink", true, line.X[done.Point], line.Z[done.Point], string.Create(CultureInfo.InvariantCulture, $"{done.Excess:0.0} degrees over 3 m")));
                    run = null;
                }
            }
        }
    }

    /// Lines meeting at a node should be at one place, and run on from one another where only two meet.
    private static void Nodes(List<NetworkLine> rails, List<AuditFinding> findings, HashSet<long> diamonds)
    {
        var at = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in rails)
            foreach (var (node, point) in line.Nodes)
            {
                if (!at.TryGetValue(node, out var list))
                    at[node] = list = [];
                list.Add((line, point));
            }

        foreach (var (node, list) in at.Where(n => n.Value.Count > 1))
        {
            var (a, p) = list[0];
            var worst = list.Skip(1).Max(o => Math.Sqrt(Math.Pow(o.Line.X[o.Point] - a.X[p], 2) + Math.Pow(o.Line.Z[o.Point] - a.Z[p], 2)));
            if (worst > NodeOff && !diamonds.Contains(node))
                findings.Add(new AuditFinding("Tracks don't meet at their junction", true, a.X[p], a.Z[p], string.Create(CultureInfo.InvariantCulture, $"{worst:0.00} m apart")));

            // Two lines ending on each other: one track, which should carry straight on.
            if (list.Count == 2 && list.All(e => e.Point == 0 || e.Point == e.Line.Count - 1))
            {
                (double X, double Z) Out((NetworkLine Line, int Point) end)
                {
                    var q = end.Point == 0 ? Math.Min(3, end.Line.Count - 1) : Math.Max(end.Line.Count - 4, 0);
                    var (dx, dz) = (end.Line.X[q] - end.Line.X[end.Point], end.Line.Z[q] - end.Line.Z[end.Point]);
                    var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
                    return (dx / length, dz / length);
                }

                var (u, v) = (Out(list[0]), Out(list[1]));
                var angle = Math.Acos(Math.Clamp(-(u.X * v.X + u.Z * v.Z), -1.0, 1.0)) * 180.0 / Math.PI;
                if (angle > JoinKinkDegrees)
                    findings.Add(new AuditFinding("Kink where two tracks join", true, a.X[p], a.Z[p], string.Create(CultureInfo.InvariantCulture, $"{angle:0.0} degrees")));
            }
        }
    }

    /// Loose ends: two of them close together with no join between them, and tiny stubs.
    private static void Ends(List<NetworkLine> rails, List<AuditFinding> findings)
    {
        var uses = new Dictionary<long, int>();
        foreach (var line in rails)
            foreach (var (node, _) in line.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        var loose = new List<(NetworkLine Line, double X, double Z)>();
        foreach (var line in rails)
        {
            foreach (var point in (int[])[0, line.Count - 1])
                if (!line.Nodes.Any(n => n.Point == point && uses.GetValueOrDefault(n.Node) > 1) && !EdgeEnd(line, point))
                    loose.Add((line, line.X[point], line.Z[point]));
            if (line.S[^1] < ShortStub)
                findings.Add(new AuditFinding("Tiny piece of track", true, line.X[0], line.Z[0], string.Create(CultureInfo.InvariantCulture, $"{line.S[^1]:0.0} m long")));
        }

        for (var i = 0; i < loose.Count; i++)
            for (var j = i + 1; j < loose.Count; j++)
            {
                if (loose[i].Line == loose[j].Line)
                    continue;
                var d = Math.Sqrt(Math.Pow(loose[i].X - loose[j].X, 2) + Math.Pow(loose[i].Z - loose[j].Z, 2));
                if (d < GapWithin)
                    findings.Add(new AuditFinding("Gap between two track ends", true, loose[i].X, loose[i].Z, string.Create(CultureInfo.InvariantCulture, $"{d:0.0} m")));
            }
    }

    private static Dictionary<(int, int), List<Segment>> Grid(IEnumerable<NetworkLine> lines)
    {
        var grid = new Dictionary<(int, int), List<Segment>>();
        foreach (var line in lines)
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var segment = new Segment(line, i, line.X[i], line.Z[i], line.X[i + 1], line.Z[i + 1]);
                var (x0, x1) = ((int)Math.Floor(Math.Min(segment.Ax, segment.Bx) / CellSize), (int)Math.Floor(Math.Max(segment.Ax, segment.Bx) / CellSize));
                var (z0, z1) = ((int)Math.Floor(Math.Min(segment.Az, segment.Bz) / CellSize), (int)Math.Floor(Math.Max(segment.Az, segment.Bz) / CellSize));
                for (var cx = x0; cx <= x1; cx++)
                    for (var cz = z0; cz <= z1; cz++)
                    {
                        if (!grid.TryGetValue((cx, cz), out var list))
                            grid[(cx, cz)] = list = [];
                        list.Add(segment);
                    }
            }

        return grid;
    }

    private static bool OnGround(NetworkLine line, int i) => line.Structures[Math.Clamp(i, 0, line.Structures.Length - 1)] == Structure.Ground;

    /// Tracks crossing with nothing joining them, and tracks running so close that trains would hit, away from any junction.
    private static void CrossingsAndFouling(List<NetworkLine> rails, List<AuditFinding> findings)
    {
        var grid = Grid(rails);
        var nodes = new Dictionary<NetworkLine, HashSet<long>>();
        foreach (var line in rails)
            nodes[line] = [.. line.Nodes.Select(n => n.Node)];

        // Lines that meet, directly or at one remove, may run close either side of where they do: that's a switch.
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var cell in grid.Values)
            for (var i = 0; i < cell.Count; i++)
                for (var j = i + 1; j < cell.Count; j++)
                {
                    var (a, b) = (cell[i], cell[j]);
                    if (a.Line == b.Line || !OnGround(a.Line, a.Index) || !OnGround(b.Line, b.Index))
                        continue;
                    var key = a.Line.Index < b.Line.Index ? (a.Line.Index, a.Index, b.Line.Index, b.Index) : (b.Line.Index, b.Index, a.Line.Index, a.Index);
                    if (!seen.Add(key))
                        continue;
                    if (!Cross(a, b, out var x, out var z))
                        continue;
                    var near = a.Line.Nodes.Concat(b.Line.Nodes).Any(n => nodes[a.Line].Contains(n.Node) && nodes[b.Line].Contains(n.Node) && Math.Pow(a.Line.X[Math.Min(a.Line.Count - 1, a.Line.Nodes.First(q => q.Node == n.Node).Point)] - x, 2) + Math.Pow(a.Line.Z[Math.Min(a.Line.Count - 1, a.Line.Nodes.First(q => q.Node == n.Node).Point)] - z, 2) < 4.0);
                    if (!near)
                        findings.Add(new AuditFinding("Tracks cross with no junction", true, x, z, string.Create(CultureInfo.InvariantCulture, $"Two tracks pass through each other at {Angle(a, b):0.0} degrees")));
                }

        foreach (var line in rails)
        {
            var (from, last) = (-1f, -1f);
            NetworkLine? with = null;
            for (var p = 0; p < line.Count; p++)
            {
                var (x, z) = (line.X[p], line.Z[p]);
                var cell = ((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));
                NetworkLine? close = null;
                for (var dx = -1; dx <= 1 && close is null; dx++)
                    for (var dz = -1; dz <= 1 && close is null; dz++)
                        if (grid.TryGetValue((cell.Item1 + dx, cell.Item2 + dz), out var list))
                            foreach (var s in list)
                                if (s.Line != line && OnGround(line, p - 1) && OnGround(s.Line, s.Index) && Distance(s, x, z) < Fouling && Parallel(line, p, s) && !ZoneHere(line, line.S[p]))
                                {
                                    close = s.Line;
                                    break;
                                }

                if (close is not null)
                {
                    if (from < 0f)
                        (from, with) = (line.S[p], close);
                    last = line.S[p];
                }

                if ((close is null || p == line.Count - 1) && from >= 0f)
                {
                    if (last - from >= FoulingRun && line.Index < with!.Index)
                    {
                        var (mx, mz) = At(line, (from + last) * 0.5f);
                        var mapped = MappedApart(line, with, mx, mz);
                        findings.Add(new AuditFinding(mapped < Fouling ? "Tracks mapped too close together" : "Tracks too close together", true, mx, mz, string.Create(CultureInfo.InvariantCulture, $"Under {Fouling:0.0} m apart for {last - from:0} m; {mapped:0.0} m apart on the map")));
                    }

                    from = -1f;
                }
            }
        }
    }

    private static double Angle(Segment a, Segment b)
    {
        var (ux, uz, vx, vz) = (a.Bx - a.Ax, a.Bz - a.Az, b.Bx - b.Ax, b.Bz - b.Az);
        return Math.Acos(Math.Clamp(Math.Abs(ux * vx + uz * vz) / Math.Max(Math.Sqrt((ux * ux + uz * uz) * (vx * vx + vz * vz)), 1e-9), 0.0, 1.0)) * 180.0 / Math.PI;
    }

    /// How far apart two lines are as mapped, around a place.
    private static double MappedApart(NetworkLine a, NetworkLine b, double x, double z)
    {
        static (double X, double Z)? Nearest(NetworkLine line, double x, double z)
        {
            if (line.MappedX.Length < 2)
                return null;
            (double, double)? found = null;
            var best = double.MaxValue;
            for (var i = 0; i + 1 < line.MappedX.Length; i++)
            {
                var (dx, dz) = (line.MappedX[i + 1] - line.MappedX[i], line.MappedZ[i + 1] - line.MappedZ[i]);
                var t = Math.Clamp(((x - line.MappedX[i]) * dx + (z - line.MappedZ[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (px, pz) = (line.MappedX[i] + dx * t, line.MappedZ[i] + dz * t);
                var d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d < best)
                    (best, found) = (d, (px, pz));
            }

            return found;
        }

        if (Nearest(a, x, z) is not { } p || Nearest(b, p.X, p.Z) is not { } q)
            return double.MaxValue;
        return Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Z - q.Z, 2));
    }

    private static bool ZoneHere(NetworkLine line, float s) => line.TurnoutZones.Any(z => s >= MathF.Min(z.Ballast.From, z.Timbers.From) - 3f && s <= MathF.Max(z.Ballast.To, z.Timbers.To) + 3f);

    private static bool Parallel(NetworkLine line, int p, Segment s)
    {
        var q = Math.Min(p + 1, line.Count - 1);
        var r = Math.Max(q - 1, 0);
        var (ux, uz, vx, vz) = (line.X[q] - line.X[r], line.Z[q] - line.Z[r], s.Bx - s.Ax, s.Bz - s.Az);
        var lengths = Math.Sqrt((ux * ux + uz * uz) * (vx * vx + vz * vz));
        return lengths > 1e-9 && Math.Abs(ux * vx + uz * vz) / lengths > 0.9;
    }

    private static double Distance(Segment s, double x, double z)
    {
        var (dx, dz) = (s.Bx - s.Ax, s.Bz - s.Az);
        var t = Math.Clamp(((x - s.Ax) * dx + (z - s.Az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
        var (ex, ez) = (s.Ax + dx * t - x, s.Az + dz * t - z);
        return Math.Sqrt(ex * ex + ez * ez);
    }

    private static bool Cross(Segment a, Segment b, out double x, out double z)
    {
        (x, z) = (0.0, 0.0);
        var (rx, rz, sx, sz) = (a.Bx - a.Ax, a.Bz - a.Az, b.Bx - b.Ax, b.Bz - b.Az);
        var denominator = rx * sz - rz * sx;
        if (Math.Abs(denominator) < 1e-9)
            return false;
        var t = ((b.Ax - a.Ax) * sz - (b.Az - a.Az) * sx) / denominator;
        var u = ((b.Ax - a.Ax) * rz - (b.Az - a.Az) * rx) / denominator;
        if (t < 0.0 || t >= 1.0 || u < 0.0 || u >= 1.0)
            return false;
        (x, z) = (a.Ax + rx * t, a.Az + rz * t);
        return true;
    }

    /// Two switches' special work on top of each other: the second's points within the first's blades or frog.
    private static void SwitchClashes(NetworkLines lines, List<AuditFinding> findings)
    {
        foreach (var line in lines.Lines.Where(l => l.Rail && l.TurnoutZones.Count > 1))
        {
            var zones = line.TurnoutZones.Select(z => (z.Turnout, z.Rails, z.RailSide)).ToList();
            for (var i = 0; i < zones.Count; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var (a, b) = (zones[i], zones[j]);
                    if (a.Turnout == b.Turnout || a.RailSide != b.RailSide && a.RailSide != 0 && b.RailSide != 0)
                        continue;
                    var overlap = MathF.Min(a.Rails.To, b.Rails.To) - MathF.Max(a.Rails.From, b.Rails.From);
                    if (overlap > 0.5f)
                        findings.Add(new AuditFinding("Switches on top of one another", true, a.Turnout.X, a.Turnout.Z, string.Create(CultureInfo.InvariantCulture, $"Their rails overlap for {overlap:0} m")));
                }
        }
    }

    /// Stretches of track with no ties under them.
    private static void Ties(List<NetworkLine> rails, List<AuditFinding> findings, List<CrossingAssembly> crossings)
    {
        bool CrossingNear(double x, double z) => crossings.Any(c => Math.Pow(c.X - x, 2) + Math.Pow(c.Z - z, 2) < Math.Pow(c.Range[1].To - c.Range[1].From, 2));
        foreach (var line in rails.Where(l => !l.Embedded))
        {
            var from = -1f;
            for (var s = 0f; s <= line.S[^1] + 0.25f; s += 0.5f)
            {
                // (Left out for a turnout's long timber only if that timber's there, which TieZone sees to; so what's left is a
                // cut for a diamond running on past what the diamond's first track carries.)
                var (x0, z0) = At(line, s);
                var missing = s <= line.S[^1] && line.TieCuts.Any(c => s >= c.From && s <= c.To) && !CrossingNear(x0, z0);
                if (missing && from < 0f)
                    from = s;
                else if (!missing && from >= 0f)
                {
                    if (s - from > TieGap)
                    {
                        var (x, z) = At(line, (from + s) * 0.5f);
                        findings.Add(new AuditFinding("Ties missing", true, x, z, string.Create(CultureInfo.InvariantCulture, $"{s - from:0} m with no ties")));
                    }

                    from = -1f;
                }
            }
        }
    }

    private static void Stray(List<NetworkLine> rails, List<AuditFinding> findings)
    {
        foreach (var line in rails.Where(l => !l.Exact && l.MappedX.Length >= 2))
        {
            (double Off, int Point)? run = null;
            for (var p = 0; p < line.Count; p += 2)
            {
                var limit = line.TurnoutZones.Any(z => line.S[p] >= z.Ballast.From && line.S[p] <= z.Ballast.To) ? TrackQuality.MaxSwitchStray : TrackQuality.MaxStray;
                var off = TrackQuality.OffMapped(line, line.X[p], line.Z[p], p);
                if (off > limit + 0.25)
                    run = run is { } r && r.Off >= off ? r : (off, p);
                else if (run is { } done)
                {
                    findings.Add(new AuditFinding("Off the mapped line", false, line.X[done.Point], line.Z[done.Point], string.Create(CultureInfo.InvariantCulture, $"{done.Off:0.0} m from the map")));
                    run = null;
                }
            }
        }
    }

    /// Grades steeper than a railway builds, and track far above or below the ground off a bridge or out of a tunnel.
    private static void Profile(List<NetworkLine> rails, List<AuditFinding> findings)
    {
        foreach (var line in rails.Where(l => l.S[^1] >= GradeWindow))
        {
            (float Grade, int Point)? worst = null;
            for (var p = 0; p < line.Count; p += 5)
            {
                var q = Array.BinarySearch(line.S, line.S[p] + GradeWindow);
                q = q >= 0 ? q : ~q;
                if (q >= line.Count)
                    break;
                var grade = MathF.Abs(line.Y[q] - line.Y[p]) / MathF.Max(line.S[q] - line.S[p], 1f);
                if (grade > SteepGrade && (worst is null || grade > worst.Value.Grade))
                    worst = (grade, p);
            }

            if (worst is { } steep)
                findings.Add(new AuditFinding("Steep grade", false, line.X[steep.Point], line.Z[steep.Point], string.Create(CultureInfo.InvariantCulture, $"{steep.Grade * 100f:0.0}% over {GradeWindow:0} m")));
        }

        foreach (var line in rails.Where(l => l.Ground.Length == l.Count))
        {
            (float Height, int Point)? run = null;
            for (var p = 0; p < line.Count; p += 2)
            {
                var height = MathF.Abs(line.Y[p] - line.Ground[p]);
                if (height > HighAboveGround && OnGround(line, p))
                    run = run is { } r && r.Height >= height ? r : (height, p);
                else if (run is { } done)
                {
                    findings.Add(new AuditFinding("Far above or below the ground", false, line.X[done.Point], line.Z[done.Point], string.Create(CultureInfo.InvariantCulture, $"{done.Height:0} m")));
                    run = null;
                }
            }
        }
    }

    /// Roads crossing track on the level with no grade crossing placed.
    private static void Roads(NetworkLines lines, List<NetworkLine> rails, IReadOnlyList<PlacedObject> objects, RouteProjection projection, List<AuditFinding> findings)
    {
        var grid = Grid(rails);
        var placed = objects.Where(o => o.Kind == PlacedKind.GradeCrossing).Select(o =>
        {
            var p = projection.Forward(new GeoPoint(o.Lat, o.Lon));
            return (X: p.East, Z: -p.North, Reach: o.Width * 0.5 + 30.0);
        }).ToList();
        var seen = new List<(double X, double Z)>();
        foreach (var road in lines.Lines.Where(l => !l.Rail))
            for (var i = 0; i + 1 < road.Count; i++)
            {
                if (!OnGround(road, i))
                    continue;
                var a = new Segment(road, i, road.X[i], road.Z[i], road.X[i + 1], road.Z[i + 1]);
                if (!grid.TryGetValue(((int)Math.Floor(a.Ax / CellSize), (int)Math.Floor(a.Az / CellSize)), out var list))
                    continue;
                foreach (var b in list)
                    if (OnGround(b.Line, b.Index) && Cross(a, b, out var x, out var z) && !seen.Any(q => Math.Pow(q.X - x, 2) + Math.Pow(q.Z - z, 2) < 900.0))
                    {
                        seen.Add((x, z));
                        if (!placed.Any(c => Math.Pow(c.X - x, 2) + Math.Pow(c.Z - z, 2) < c.Reach * c.Reach))
                            findings.Add(new AuditFinding("Road crosses track with no grade crossing", false, x, z, "No crossing placed"));
                    }
            }
    }

    private static void Placed(NetworkLines lines, IReadOnlyList<PlacedObject> objects, RouteProjection projection, List<AuditFinding> findings)
    {
        var switches = lines.Turnouts.Select(t => t.Node).ToHashSet();
        foreach (var o in objects)
        {
            var p = projection.Forward(new GeoPoint(o.Lat, o.Lon));
            var (x, z) = (p.East, -p.North);
            if (o.Kind == PlacedKind.HandThrow && o.Node is { } node && !switches.Contains(node))
                findings.Add(new AuditFinding("Hand throw with no switch", true, x, z, "Its switch has gone"));
            else if (o.Kind is PlacedKind.Signal or PlacedKind.GradeCrossing && Builder.PlaceTool.NearestRail(lines, x, z, o.Kind == PlacedKind.Signal ? 12.0 : 3.0) is null)
                findings.Add(new AuditFinding(o.Kind == PlacedKind.Signal ? "Signal with no track" : "Crossing with no track", true, x, z, "Nothing near it"));
        }
    }

    /// The findings as text, worst first: how many of each, then each one with where it is.
    public static string Report(List<AuditFinding> findings, RouteProjection projection, string route)
    {
        var text = new StringBuilder();
        var groups = findings.GroupBy(f => f.Check).OrderByDescending(g => g.First().Severe).ThenByDescending(g => g.Count()).ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {route}: {findings.Count(f => f.Severe)} to fix, {findings.Count(f => !f.Severe)} to know about");
        foreach (var group in groups)
            text.AppendLine(CultureInfo.InvariantCulture, $"{group.Count(),5}  {(group.First().Severe ? "FIX " : "note")}  {group.Key}");
        foreach (var group in groups)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            foreach (var f in group)
            {
                var g = projection.Inverse(new RoutePoint(f.X, -f.Z));
                text.AppendLine(CultureInfo.InvariantCulture, $"{g.Lat:0.000000},{g.Lon:0.000000}  {f.Detail}");
            }
        }

        return text.ToString();
    }
}
