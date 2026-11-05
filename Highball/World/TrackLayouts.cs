using System.Text.Json;
using System.Text.Json.Serialization;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

public enum JunctionKind { Turnout, Crossover, LadderLink, ScissorsCrossover, Diamond, SingleSlip, DoubleSlip, ThreeWay, TwoAtOneNode, Unknown }

/// One piece of track layout at a junction, read from the tracks around it rather than one node alone: a turnout, a
/// crossover (two turnouts joining parallel tracks), a ladder link (two joining converging tracks, as from a ladder's
/// lead to a yard track), a scissors crossover, a diamond, a slip and so on. Nodes: the
/// OpenStreetMap nodes it's made of (a crossover's two switches); Number: the standard size it's laid out to, if it is;
/// Issue: why it needs fixing by hand (no room for a real design, a shape no template matches), or null.
public sealed record Layout(JunctionKind Kind, double X, double Z, long[] Nodes, int? Number, string? Issue);

/// Finds the layouts in a route's track (NetworkLines.Layouts). OpenStreetMap's tags say what a node is where a mapper
/// tagged it (railway=railway_crossing, railway:switch=double_slip and the rest), and those come first; elsewhere the
/// shape decides: how many tracks meet, which run straight through, and where each switch's diverging track leads.
public static class TrackLayouts
{
    /// How far along a turnout's diverging track its partner in a crossover may be.
    private const float CrossoverReach = 200f;

    public static List<Layout> Find(NetworkLines lines, IReadOnlyDictionary<long, NetworkNode> tagged)
    {
        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Disused)
                continue;
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }
        }

        var turnouts = lines.Turnouts.GroupBy(t => t.Node).ToDictionary(g => g.Key, g => g.ToList());
        var layouts = new List<Layout>();
        var claimed = new HashSet<long>();

        // Crossovers and ladder links: a turnout whose diverging track runs into another turnout's diverging track, the
        // two straight tracks side by side (a crossover) or converging (a link).
        var crossovers = new List<(Turnout A, Turnout B, float Spacing, float Apart)>();
        foreach (var a in lines.Turnouts)
        {
            if (claimed.Contains(a.Node) || turnouts[a.Node].Count > 1 || Next(a.Reverse, uses) is not { } next || next.Node == a.Node)
                continue;
            // (Each diverging towards the other: a switch further along that diverges away from this one is just the next
            // switch on the track.)
            if (!turnouts.TryGetValue(next.Node, out var there) || there.Count > 1 || there[0] is not { } b || b.Reverse.Line != a.Reverse.Line ||
                b.Reverse.Direction == a.Reverse.Direction)
                continue;
            if (b.Normal.Line == a.Normal.Line || Apart(a.Normal, b.Normal) >= CrossoverFitting.MaxLink || Turnout.Offset(a.Normal, b.X, b.Z) is not { } off)
                continue;
            if (Apart(a.Normal, b.Normal) >= CrossoverFitting.LinkAngle && next.Distance > CrossoverFitting.MaxLinkLength)
                continue;
            crossovers.Add((a, b, MathF.Abs(off.Offset), next.Distance));
            claimed.Add(a.Node);
            claimed.Add(b.Node);
        }

        // Two crossovers between the same tracks, overlapping: a scissors crossover (and the diamond where they cross).
        var scissored = new HashSet<int>();
        for (var i = 0; i < crossovers.Count; i++)
            for (var j = i + 1; j < crossovers.Count; j++)
            {
                var (p, q) = (crossovers[i], crossovers[j]);
                if (scissored.Contains(i) || scissored.Contains(j) || !SameTracks(p, q))
                    continue;
                var (px, pz) = Middle(p.A, p.B);
                var (qx, qz) = Middle(q.A, q.B);
                if ((px - qx) * (px - qx) + (pz - qz) * (pz - qz) > 30.0 * 30.0)
                    continue;

                // With a real crossing between them it's built: each crossover stands as it is, and the crossing's a diamond.
                if (uses.Any(u => u.Value.Count == 2 && Near(u.Value[0].Line, u.Value[0].Point, (px + qx) / 2, (pz + qz) / 2, 20.0) && Turnout.IsCrossing(LegsOf(u.Value))))
                    continue;
                scissored.Add(i);
                scissored.Add(j);
                foreach (var (node, list) in uses)
                    if (list.Count > 1 && Near(list[0].Line, list[0].Point, (px + qx) / 2, (pz + qz) / 2, 20.0))
                        claimed.Add(node);
                layouts.Add(new Layout(JunctionKind.ScissorsCrossover, (px + qx) / 2, (pz + qz) / 2, [p.A.Node, p.B.Node, q.A.Node, q.B.Node], null,
                    "Scissors crossover: no template yet"));
            }

        for (var i = 0; i < crossovers.Count; i++)
        {
            if (scissored.Contains(i))
                continue;
            var (a, b, spacing, apart) = crossovers[i];
            var (x, z) = Middle(a, b);
            var link = Apart(a.Normal, b.Normal) >= CrossoverFitting.LinkAngle || lines.Links.Contains(a.Node);
            var name = link ? "Ladder link" : "Crossover";
            var number = a.Number is { } n && b.Number == n ? n : (int?)null;
            var issue = a.Number is null || b.Number is null
                ? lines.CrossoverProblems.GetValueOrDefault(a.Node) ?? lines.CrossoverProblems.GetValueOrDefault(b.Node) ??
                  (lines.SwitchOptions.GetValueOrDefault(a.Node)?.Chosen == SwitchSizes.AsMapped ? null :
                      string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} with no standard design: switches {apart:0} m apart, tracks {spacing:0.0} m apart"))
                : a.Number != b.Number && apart <= CrossoverFitting.MaxLinkLength ? $"{name} with mismatched switches: No. {a.Number} and No. {b.Number}" : null;
            layouts.Add(new Layout(link ? JunctionKind.LadderLink : JunctionKind.Crossover, x, z, [a.Node, b.Node], number, issue));
        }

        // Every other junction node on its own.
        foreach (var (node, list) in uses)
        {
            if (list.Count < 2 || claimed.Contains(node))
                continue;
            var legs = new List<Leg>();
            foreach (var (line, point) in list)
            {
                if (point > 0)
                    legs.Add(new Leg(line, point, -1));
                if (point < line.Count - 1)
                    legs.Add(new Leg(line, point, 1));
            }

            if (legs.Count < 3)
                continue;
            var (x, z) = (list[0].Line.X[list[0].Point], list[0].Line.Z[list[0].Point]);
            var tags = tagged.GetValueOrDefault(node)?.Tags;
            var railway = tags?.GetValueOrDefault("railway") ?? "";
            var kind = tags?.GetValueOrDefault("railway:switch") ?? "";

            Layout Other(JunctionKind k, string issue) => new(k, x, z, [node], null, issue);
            if (railway == "railway_crossing")
                layouts.Add(Other(JunctionKind.Diamond, "Diamond: no template yet"));
            else if (kind == "double_slip")
                layouts.Add(Other(JunctionKind.DoubleSlip, "Double slip: no template yet"));
            else if (kind == "single_slip")
                layouts.Add(Other(JunctionKind.SingleSlip, "Single slip: no template yet"));
            else if (kind == "three_way")
                layouts.Add(Other(JunctionKind.ThreeWay, "Three-way switch: no template yet"));
            else if (turnouts.TryGetValue(node, out var here) && here.Count == 1)
                layouts.Add(Single(here[0], lines.SwitchOptions.GetValueOrDefault(node)));
            else if (here is { Count: > 1 })
            {
                // Two switches point to point (their toes together): real track, each laid out on its own.
                foreach (var t in here)
                    layouts.Add(Single(t, lines.SwitchOptions.GetValueOrDefault(node)) with { Kind = JunctionKind.TwoAtOneNode });
            }
            else if (legs.Count == 4 && Crossing(legs))
                layouts.Add(Other(JunctionKind.Diamond, "Diamond: no template yet"));
            else if (legs.FirstOrDefault(l => !Turnout.Plain(l)) is { Line: not null } odd)
                layouts.Add(Other(JunctionKind.Unknown, $"Switch left as mapped: {Turnout.Why(odd)}"));
            else
                layouts.Add(Other(JunctionKind.Unknown, legs.Count == 3 ? "Not shaped like a switch" : $"{legs.Count} tracks meet at one node"));
        }

        return layouts;
    }

    private static List<Leg> LegsOf(List<(NetworkLine Line, int Point)> at)
    {
        var legs = new List<Leg>();
        foreach (var (line, point) in at)
        {
            if (point > 0)
                legs.Add(new Leg(line, point, -1));
            if (point < line.Count - 1)
                legs.Add(new Leg(line, point, 1));
        }

        return legs;
    }

    private static Layout Single(Turnout t, SwitchOptions? options)
    {
        string? issue = null;
        if (t.Number is null && options?.Chosen != SwitchSizes.AsMapped)
            issue = options is null ? "No room for a standard switch: the next junction or the track's end is too close" :
                options.Chosen is { } chosen ? $"No. {chosen} picked, but there's no room for it" :
                options.Why is { } why ? $"No room for a standard switch: {why}" :
                options.Fits.Length > 0 ? $"Fits No. {string.Join(", ", options.Fits)} but wasn't laid out (shape changed in fitting)" : "No room for a standard switch";
        return new Layout(JunctionKind.Turnout, t.X, t.Z, [t.Node], t.Number, issue);
    }

    /// The next junction along a leg, within CrossoverReach, and how far.
    private static (long Node, float Distance)? Next(Leg leg, Dictionary<long, List<(NetworkLine Line, int Point)>> uses)
    {
        (long, float)? best = null;
        foreach (var (node, point) in leg.Line.Nodes)
        {
            var distance = (leg.Line.S[point] - leg.Start) * leg.Direction;
            if (distance > 0.5f && distance < CrossoverReach && uses.TryGetValue(node, out var list) && list.Count > 1 && (best is null || distance < best.Value.Item2))
                best = (node, distance);
        }

        return best;
    }

    /// The angle between two tracks, in degrees, whichever way each runs.
    private static double Apart(Leg a, Leg b)
    {
        var (ax, az) = Turnout.Heading(a);
        var (bx, bz) = Turnout.Heading(b);
        return Math.Acos(Math.Min(1f, MathF.Abs(ax * bx + az * bz))) * 180.0 / Math.PI;
    }

    private static bool SameTracks((Turnout A, Turnout B, float, float) p, (Turnout A, Turnout B, float, float) q)
    {
        var a = new HashSet<NetworkLine> { p.A.Normal.Line, p.B.Normal.Line };
        return a.SetEquals([q.A.Normal.Line, q.B.Normal.Line]);
    }

    private static (double X, double Z) Middle(Turnout a, Turnout b) => ((a.X + b.X) / 2, (a.Z + b.Z) / 2);

    private static bool Near(NetworkLine line, int point, double x, double z, double within) =>
        Math.Abs(line.X[point] - x) < within && Math.Abs(line.Z[point] - z) < within;

    /// Four legs as two tracks straight through one another (each pair opposite), at more than a sliver of an angle.
    internal static bool Crossing(List<Leg> legs)
    {
        var h = legs.Select(Turnout.Heading).ToArray();
        float Dot(int a, int b) => h[a].X * h[b].X + h[a].Z * h[b].Z;
        foreach (var (a, b, c, d) in new[] { (0, 1, 2, 3), (0, 2, 1, 3), (0, 3, 1, 2) })
            if (Dot(a, b) < -0.96f && Dot(c, d) < -0.96f && MathF.Abs(Dot(a, c)) < 0.995f)
                return true;
        return false;
    }

    // --- For the Planner's map: Junctions.json in the route's folder, written whenever the track is laid. ---

    /// A layout as the map shows it: where, what, its size, whether it's drawn with models, and what needs fixing.
    public sealed record Marker(double Lat, double Lon, JunctionKind Kind, int Number, bool Modelled, string? Issue);

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string MarkersPath(RouteInfo route) => Path.Combine(route.Folder, "Junctions.json");

    public static void SaveMarkers(RouteInfo route, IEnumerable<Layout> layouts)
    {
        if (route.Projection is not { } projection)
            return;
        try
        {
            var markers = layouts.Select(l =>
            {
                var g = projection.Inverse(new RoutePoint(l.X, -l.Z));
                return new Marker(Math.Round(g.Lat, 7), Math.Round(g.Lon, 7), l.Kind, l.Number ?? 0, l.Number is not null, l.Issue);
            }).ToList();
            File.WriteAllText(MarkersPath(route), JsonSerializer.Serialize(markers, Options));
        }
        catch (Exception e)
        {
            Log.Warn($"Junction markers: {e.Message}");
        }
    }

    public static List<Marker> LoadMarkers(RouteInfo route)
    {
        try
        {
            var path = MarkersPath(route);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Marker>>(File.ReadAllText(path), Options) ?? [] : [];
        }
        catch (Exception e)
        {
            Log.Warn($"Junction markers: {e.Message}");
            return [];
        }
    }

    /// What a layout is called, for labels and the issues list.
    public static string Name(JunctionKind kind, int number) => kind switch
    {
        JunctionKind.Turnout => number > 0 ? $"Switch, No. {number}" : "Switch",
        JunctionKind.Crossover => number > 0 ? $"Crossover, No. {number}" : "Crossover",
        JunctionKind.LadderLink => number > 0 ? $"Ladder link, No. {number}" : "Ladder link",
        JunctionKind.ScissorsCrossover => "Scissors crossover",
        JunctionKind.Diamond => "Diamond",
        JunctionKind.SingleSlip => "Single slip",
        JunctionKind.DoubleSlip => "Double slip",
        JunctionKind.ThreeWay => "Three-way switch",
        JunctionKind.TwoAtOneNode => number > 0 ? $"Switch point to point, No. {number}" : "Switch point to point",
        _ => "Junction",
    };
}
