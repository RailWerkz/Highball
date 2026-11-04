using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// Lays every crossover out as one standard design, before the ways become lines. OpenStreetMap draws a crossover as a
/// short track from a node on one track to a node on the other, at whatever angle and length the mapper clicked; fitted
/// a switch at a time, each half gets only to the middle and nothing fits. Here the pair is fitted as one: two turnouts
/// of the same size point to point, each one's lead curve out to its frog, a straight at the frog angle between, at the
/// tracks' measured spacing; centred where the mapper put it, with both switch points slid along their tracks to where
/// the design puts them. TurnoutFitting then lays each end out exactly to its frog, and the straight between (Forced).
public static class CrossoverFitting
{
    /// The ways as fitted (crossovers redrawn, their switch nodes moved; the network's own ways aren't touched), each
    /// fitted switch's size and half the crossover's length, what each crossover or ladder link could be fitted to,
    /// why any couldn't be, by both its switches' nodes, and the switches that are ladder links'.
    public sealed record Result(List<NetworkWay> Ways, Dictionary<long, (int Number, float Half)> Forced, Dictionary<long, SwitchOptions> Options,
        Dictionary<long, string> Problems, HashSet<long> Links);

    /// Tracks running between two converging tracks this many degrees apart or more are ladder links rather than
    /// crossovers; past MaxLink, not a pair of switches joining tracks at all.
    public const double LinkAngle = 4.0;
    public const double MaxLink = 35.0;

    /// A ladder link is short: a track between two switches further apart than this is a yard track with a switch at
    /// each end, each fitted on its own.
    public const double MaxLinkLength = 120.0;

    /// The rail ways cut at every junction inside them, so every switch is at the ends of the ways it joins (a track
    /// running through a switch becomes two ways meeting there). The pieces after the first get ids of their own,
    /// the same every time for the same way; pieces gets each cut way's pieces, in order (Join puts them back).
    private static IEnumerable<NetworkWay> Split(IEnumerable<NetworkWay> network, Dictionary<long, List<long>> pieces)
    {
        var list = network.ToList();
        var degree = new Dictionary<long, int>();
        foreach (var way in list.Where(w => w.Rail && w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length))
            foreach (var node in way.Nodes)
                degree[node] = degree.GetValueOrDefault(node) + 1;

        foreach (var way in list)
        {
            var cuts = way.Rail && way.Lat.Length == way.Nodes.Length
                ? Enumerable.Range(1, Math.Max(0, way.Nodes.Length - 2)).Where(i => degree.GetValueOrDefault(way.Nodes[i]) > 1).ToList()
                : [];
            if (cuts.Count == 0)
            {
                yield return way;
                continue;
            }

            var from = 0;
            var ids = pieces[way.Id] = [];
            for (var k = 0; k <= cuts.Count; k++)
            {
                var to = k < cuts.Count ? cuts[k] : way.Nodes.Length - 1;
                ids.Add(k == 0 ? way.Id : (way.Id >= 0 ? 4_000_000_000_000_000L : 5_000_000_000_000_000L) + Math.Abs(way.Id) * 256 + k);
                yield return new NetworkWay
                {
                    Id = ids[^1],
                    Rail = way.Rail, Class = way.Class, Nodes = way.Nodes[from..(to + 1)], Lat = way.Lat[from..(to + 1)], Lon = way.Lon[from..(to + 1)],
                    Tags = way.Tags,
                };
                from = to;
            }
        }
    }

    private const double MinLength = 15.0;
    private const double MaxLength = 250.0;
    private const double Clearance = 3.0;

    /// How far a switch may be slid along its track to make room for the one before it in a ladder.
    private const float MaxSlide = 40f;

    /// How far past a switch's frog the next switch along a ladder goes: room for the model's end, the run onto the mapped
    /// track, and TurnoutFitting's clearance before a junction.
    private const float LadderRoom = 12f;
    /// The steepest a slid switch's other route may leave its track at (about 8 degrees).
    private const double SlideSlope = 0.14;

    /// The furthest a slid switch's other route may run straight to its next point, before it gets a new point on its way.
    private const double LongestSkip = 60.0;

    /// The length of a crossover of a size at a track spacing (metres), from one point of switch to the other.
    public static float Length(int number, float spacing)
    {
        var radius = Turnout.RadiusOf(number);
        var frog = Turnout.FrogOf(number);
        return 2f * frog + (spacing - 4f * Turnout.RailOffset) / (frog / radius);
    }

    /// The crossover's centreline off the first track (towards the second), s along it, for a size and spacing.
    public static float Offset(int number, float spacing, float s)
    {
        var radius = Turnout.RadiusOf(number);
        var frog = Turnout.FrogOf(number);
        var length = Length(number, spacing);
        if (s <= frog)
            return MathF.Max(s, 0f) * MathF.Max(s, 0f) / (2f * radius);
        if (s >= length - frog)
            return spacing - MathF.Max(length - s, 0f) * MathF.Max(length - s, 0f) / (2f * radius);
        return 2f * Turnout.RailOffset + (s - frog) * frog / radius;
    }

    /// A track through a switch node: one way running through it, or two ways meeting at it end to end, as one run of
    /// points with the node at Index. Parts: the ways it's made of, in order, and whether each runs backwards in it.
    private sealed class Track
    {
        public required List<(NetworkWay Way, bool Reversed)> Parts;
        public required List<(long Node, double X, double Z)> Points;
        public required int Index;
    }

    public static Result Apply(IEnumerable<NetworkWay> network, RouteInfo route, IReadOnlyDictionary<long, int> chosen)
    {
        var projection = route.Projection!;
        var pieces = new Dictionary<long, List<long>>();
        var ways = Split(network, pieces).ToDictionary(w => w.Id);
        var forced = new Dictionary<long, (int, float)>();
        var options = new Dictionary<long, SwitchOptions>();
        var problems = new Dictionary<long, string>();

        bool Usable(NetworkWay w) => w.Rail && w.Class != (int)RailClass.Disused && w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length;
        var degree = new Dictionary<long, int>();
        foreach (var way in ways.Values.Where(Usable))
            foreach (var node in way.Nodes)
                degree[node] = degree.GetValueOrDefault(node) + 1;
        var ends = new Dictionary<long, int>();
        foreach (var way in ways.Values.Where(Usable))
            foreach (var node in (long[])[way.Nodes[0], way.Nodes[^1]])
                ends[node] = ends.GetValueOrDefault(node) + 1;
        bool Junction(long node) => degree.GetValueOrDefault(node) > 1;
        bool Plain(long node) => degree.GetValueOrDefault(node) == 2 && ends.GetValueOrDefault(node) == 2;

        (double X, double Z) P(double lat, double lon)
        {
            var p = projection.Forward(new GeoPoint(lat, lon));
            return (p.East, p.North);
        }

        var nextId = Math.Min(-1_000_000_000L, ways.Values.SelectMany(w => w.Nodes).DefaultIfEmpty(0).Min() - 1);
        var done = new HashSet<long>();
        var links = new HashSet<long>();

        foreach (var (id0, backwards) in ways.Values.Where(Usable).OrderBy(w => w.Id).SelectMany(w => new[] { (w.Id, false), (w.Id, true) }).ToList())
        {
            // (As it is now: an earlier crossover may have moved it.)
            if (done.Contains(id0) || !ways.TryGetValue(id0, out var seed))
                continue;
            var begin = backwards ? seed.Nodes[^1] : seed.Nodes[0];
            if (!Junction(begin) || Plain(begin))
                continue;

            // The crossover's track: from a switch, on through plain joins between ways, to the next junction.
            var path = new List<(NetworkWay Way, bool Reversed)> { (seed, backwards) };
            var end = backwards ? seed.Nodes[0] : seed.Nodes[^1];
            while (Plain(end) && path.Count < 8)
            {
                var (prev, _) = path[^1];
                var next = ways.Values.FirstOrDefault(o => Usable(o) && o != prev && (o.Nodes[0] == end || o.Nodes[^1] == end));
                if (next is null || path.Any(q => q.Way == next))
                    break;
                path.Add((next, next.Nodes[^1] == end));
                end = path[^1].Reversed ? next.Nodes[0] : next.Nodes[^1];
            }

            var (first, last) = (begin, end);
            var mine = path.Select(q => q.Way.Id).ToHashSet();
            void Why(string reason)
            {
                problems.TryAdd(first, reason);
                problems.TryAdd(last, reason);
            }

            if (first == last || !Junction(last) || done.Contains(first) || done.Contains(last))
                continue;
            var interior = path.SelectMany(q => q.Way.Nodes).Where(n => n != first && n != last);
            if (interior.Any(n => degree.GetValueOrDefault(n) > 2 || (degree.GetValueOrDefault(n) == 2 && !path.Any(q => q.Way.Nodes[0] == n || q.Way.Nodes[^1] == n))))
            {
                Why("Another junction on the crossover's own track");
                continue;
            }

            // The toe of a switch at either end: a track leading to a switch, not a crossover. Each is fitted on its own.
            if (IsToe(ways.Values.Where(Usable), first, path[0].Way, mine, P) || IsToe(ways.Values.Where(Usable), last, path[^1].Way, mine, P))
                continue;

            if (TrackAt(ways.Values.Where(Usable), first, mine, P) is not { } a || TrackAt(ways.Values.Where(Usable), last, mine, P) is not { } b)
            {
                Why("A switch's track doesn't run straight on through it");
                continue;
            }

            // Both ends on the one track (a loop that leaves it and comes back): not joining two tracks at all.
            if (a.Parts.Any(p => b.Parts.Any(q => q.Way.Id == p.Way.Id)))
                continue;

            var points = path.SelectMany((q, i) =>
            {
                var list = q.Reversed ? q.Way.Nodes.Select((n, k) => (n, q.Way.Lat[k], q.Way.Lon[k])).Reverse() : q.Way.Nodes.Select((n, k) => (n, q.Way.Lat[k], q.Way.Lon[k]));
                return i == 0 ? list : list.Skip(1);
            }).Select(t => P(t.Item2, t.Item3)).ToList();

            var f = (a.Points[a.Index].X, a.Points[a.Index].Z);
            var l = (b.Points[b.Index].X, b.Points[b.Index].Z);
            var tA = Tangent(a);
            var tB = Tangent(b);
            var angle = Math.Acos(Math.Min(1, Math.Abs(tA.X * tB.X + tA.Z * tB.Z))) * 180 / Math.PI;
            if (angle >= MaxLink)
            {
                Why($"The two tracks are {angle:0} degrees apart, too far for switches to join");
                continue;
            }

            if (angle >= LinkAngle)
            {
                if (Math.Sqrt((l.Item1 - f.Item1) * (l.Item1 - f.Item1) + (l.Item2 - f.Item2) * (l.Item2 - f.Item2)) <= MaxLinkLength)
                    Link(a, b, first, last, points, angle, mine);
                continue;
            }

            // Along the first track the way the crossover runs; across towards the second.
            var w1 = points[1];
            var signA = (w1.X - f.X) * tA.X + (w1.Z - f.Z) * tA.Z >= 0 ? 1 : -1;
            var u = (X: tA.X * signA, Z: tA.Z * signA);
            var along = (l.Item1 - f.Item1) * u.X + (l.Item2 - f.Item2) * u.Z;
            var cross = u.X * (l.Item2 - f.Item2) - u.Z * (l.Item1 - f.Item1);
            var spacing = (float)Math.Abs(cross);
            var n = cross >= 0 ? (X: -u.Z, Z: u.X) : (X: u.Z, Z: -u.X);
            if (along < MinLength || along > MaxLength || spacing > 12f)
                continue;

            done.Add(first);
            done.Add(last);
            foreach (var id in mine)
                done.Add(id);

            double Along((double X, double Z) p) => (p.X - f.Item1) * u.X + (p.Z - f.Item2) * u.Z;
            var middle = along / 2;

            // The other junctions on either track, as distances along: the crossover mustn't reach over any of them.
            var blockers = a.Points.Concat(b.Points).Where(p => p.Node != first && p.Node != last && Junction(p.Node)).Select(p => Along((p.X, p.Z))).ToList();
            var fits = new List<int>();
            string? why = spacing < 4f * Turnout.RailOffset + 0.3f ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tracks only {spacing:0.0} m apart") : null;
            if (why is null)
                foreach (var (number, _) in Turnout.Sizes)
                {
                    var length = Length(number, spacing);
                    var (from, to) = (middle - length / 2, middle + length / 2);
                    if (Slide(a, u, from) is null || Slide(b, u, to - along) is null)
                        why ??= "a track ends too soon";
                    else if (blockers.Any(s => s > from - Clearance && s < to + Clearance))
                        why ??= $"another junction is in the way of a No. {number}";
                    else
                        fits.Add(number);
                }

            // Yards and industry take No. 6 to 10, main and branch lines No. 10 to 20; nearest the mapped length wins.
            bool Yardish(Track t) => t.Parts.All(q => q.Way.Class is (int)RailClass.Yard or (int)RailClass.Industrial);
            var yard = Yardish(a) && Yardish(b);
            var suited = fits.Where(x => yard ? x <= 10 : x >= 10).ToList();
            int? auto = (suited.Count > 0 ? suited : fits).Select(x => (int?)x).MinBy(x => Math.Abs(Length(x!.Value, spacing) - along));
            var pick = chosen.TryGetValue(first, out var c) ? c : chosen.TryGetValue(last, out c) ? c : (int?)null;
            options[first] = options[last] = new SwitchOptions(auto, [.. fits], pick);

            var size = pick ?? auto;
            if (size is not { } number2 || number2 == SwitchSizes.AsMapped)
            {
                if (fits.Count == 0 && pick != SwitchSizes.AsMapped)
                    problems[first] = problems[last] = $"No standard crossover fits: {why}";
                continue;
            }

            if (!fits.Contains(number2))
            {
                problems[first] = problems[last] = $"No. {number2} picked, but it doesn't fit: {why}";
                continue;
            }

            // Slide both switch points to the design's, and draw the crossover between them.
            var total = Length(number2, spacing);
            var start = (X: f.Item1 + u.X * (middle - total / 2), Z: f.Item2 + u.Z * (middle - total / 2));
            var atA = Move(a, u, middle - total / 2, ways, ref nextId, projection);
            // (Read again: the two tracks may share a way, which the first move just changed.)
            var atB = Move(TrackAt(ways.Values.Where(Usable), last, mine, P) ?? b, u, middle + total / 2 - along, ways, ref nextId, projection);

            var designEnd = (X: start.X + u.X * total + n.X * spacing, Z: start.Z + u.Z * total + n.Z * spacing);
            var lat = new List<double>();
            var lon = new List<double>();
            var nodes = new List<long>();
            var steps = Math.Max(4, (int)Math.Ceiling(total / 2.0));
            for (var k = 0; k <= steps; k++)
            {
                var s = total * k / steps;
                var off = Offset(number2, spacing, s);
                // (The tracks aren't quite straight: the difference from where the design would put each switch point
                // is shared out along it.)
                var share = (double)k / steps;
                var (x, z) = (start.X + u.X * s + n.X * off + (atA.X - start.X) * (1 - share) + (atB.X - designEnd.X) * share,
                    start.Z + u.Z * s + n.Z * off + (atA.Z - start.Z) * (1 - share) + (atB.Z - designEnd.Z) * share);
                var g = projection.Inverse(new RoutePoint(x, z));
                lat.Add(g.Lat);
                lon.Add(g.Lon);
                nodes.Add(k == 0 ? first : k == steps ? last : nextId--);
            }

            // The crossover becomes its first way, redrawn (first switch to last); the rest of its ways go.
            ways[seed.Id] = new NetworkWay { Id = seed.Id, Rail = seed.Rail, Class = seed.Class, Nodes = [.. nodes], Lat = [.. lat], Lon = [.. lon], Tags = seed.Tags };
            foreach (var (way, _) in path.Skip(1))
                ways.Remove(way.Id);
            forced[first] = forced[last] = (number2, total / 2);
        }

        // A track between converging tracks (a ladder's link from its lead to a yard track): each switch's lead curve
        // out to its frog where it was mapped, and a smooth curve between the frogs (TurnoutFitting draws both). The
        // biggest size that fits wins: the one with the gentlest curves.
        void Link(Track a, Track b, long first, long last, List<(double X, double Z)> points, double angle, HashSet<long> mine)
        {
            done.Add(first);
            done.Add(last);
            foreach (var id in mine)
                done.Add(id);

            var f = (X: a.Points[a.Index].X, Z: a.Points[a.Index].Z);
            var l = (X: b.Points[b.Index].X, Z: b.Points[b.Index].Z);
            ((double X, double Z) U, (double X, double Z) N) Frame(Track t, (double X, double Z) at, (double X, double Z) toward)
            {
                var d = Tangent(t);
                var sign = (toward.X - at.X) * d.X + (toward.Z - at.Z) * d.Z >= 0 ? 1 : -1;
                var u = (d.X * sign, d.Z * sign);
                var left = (-u.Item2, u.Item1);
                var side = (toward.X - at.X) * left.Item1 + (toward.Z - at.Z) * left.Item2 >= 0 ? 1 : -1;
                return (u, (left.Item1 * side, left.Item2 * side));
            }

            var (uA, nA) = Frame(a, f, points[Math.Min(2, points.Count - 1)]);
            var (uB, nB) = Frame(b, l, points[Math.Max(0, points.Count - 3)]);
            var junctions = a.Points.Concat(b.Points).Where(p => p.Node != first && p.Node != last && Junction(p.Node)).ToList();

            var fits = new List<int>();
            string? why = null;
            foreach (var (number, radius) in Turnout.Sizes)
            {
                var frog = Turnout.FrogOf(number);
                var theta = frog / radius;
                (double X, double Z) FrogAt((double X, double Z) at, (double X, double Z) u, (double X, double Z) nn) =>
                    (at.X + u.X * frog + nn.X * 2 * Turnout.RailOffset, at.Z + u.Z * frog + nn.Z * 2 * Turnout.RailOffset);
                (double X, double Z) Heading((double X, double Z) u, (double X, double Z) nn) =>
                    (u.X * Math.Cos(theta) + nn.X * Math.Sin(theta), u.Z * Math.Cos(theta) + nn.Z * Math.Sin(theta));
                var (fa, fb) = (FrogAt(f, uA, nA), FrogAt(l, uB, nB));
                var (da, db) = (Heading(uA, nA), Heading(uB, nB));
                var chord = Math.Sqrt((fb.X - fa.X) * (fb.X - fa.X) + (fb.Z - fa.Z) * (fb.Z - fa.Z));
                var toward = chord > 0 ? ((fb.X - fa.X) / chord, (fb.Z - fa.Z) / chord) : (0.0, 0.0);

                bool Blocked((double X, double Z) at, (double X, double Z) u) => junctions.Any(p =>
                {
                    var s = (p.X - at.X) * u.X + (p.Z - at.Z) * u.Z;
                    var off = Math.Abs((p.X - at.X) * -u.Z + (p.Z - at.Z) * u.X);
                    return off < 2.0 && s > -Clearance && s < frog + Clearance;
                });

                if (Slide(a, uA, frog) is null || Slide(b, uB, frog) is null)
                    why ??= "a track ends too soon";
                else if (Blocked(f, uA) || Blocked(l, uB))
                    why ??= $"another junction is in the way of a No. {number}";
                else if (chord < 4.0 || da.X * toward.Item1 + da.Z * toward.Item2 < 0.6 || -(db.X * toward.Item1 + db.Z * toward.Item2) < 0.6)
                    why ??= "the switches are too close together";
                else if (TightestRadius(fa, da, fb, (-db.X, -db.Z), chord) < 0.6 * radius)
                    why ??= $"the curve between a No. {number}'s frogs would be too sharp";
                else
                    fits.Add(number);
            }

            bool Yardish(Track t) => t.Parts.All(q => q.Way.Class is (int)RailClass.Yard or (int)RailClass.Industrial);
            var yard = Yardish(a) && Yardish(b);
            var suited = fits.Where(x => yard ? x <= 10 : x >= 10).ToList();
            int? auto = (suited.Count > 0 ? suited : fits).Select(x => (int?)x).LastOrDefault();
            var pick = chosen.TryGetValue(first, out var c) ? c : chosen.TryGetValue(last, out c) ? c : (int?)null;
            options[first] = options[last] = new SwitchOptions(auto, [.. fits], pick);
            links.Add(first);
            links.Add(last);

            var size = pick ?? auto;
            if (size is not { } number2 || number2 == SwitchSizes.AsMapped)
            {
                if (fits.Count == 0 && pick != SwitchSizes.AsMapped)
                    problems[first] = problems[last] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Ladder link with no standard size: {why} (tracks {angle:0} degrees apart)");
                return;
            }

            if (!fits.Contains(number2))
            {
                problems[first] = problems[last] = $"No. {number2} picked, but it doesn't fit: {why}";
                return;
            }

            forced[first] = forced[last] = (number2, 0f);
        }

        // Switches one after another along a diverging track (a ladder): OpenStreetMap often has the next one too close,
        // where the two routes of this one haven't parted enough for its frog. Each is slid on along its track to where
        // this one's frog fits; the switches after it make room in turn over a few passes.
        var spaced = 0;
        for (var pass = 0; pass < 3; pass++)
            foreach (var node in degree.Keys.Where(n => Junction(n) && !done.Contains(n)).OrderBy(n => n).ToList())
                if (Space(node))
                    spaced++;
        if (spaced > 0)
            Log.Info($"Ladders: {spaced} switches slid along their tracks to make room");

        // A switch's three legs, each way leaving the node with its points outward: its two heels (the straighter on
        // from the toe first) and its toe.
        (List<(NetworkWay Way, List<(double X, double Z)> Points)> Heels, NetworkWay Toe)? Legs(long at)
        {
            var legs = ways.Values.Where(w => Usable(w) && (w.Nodes[0] == at || w.Nodes[^1] == at)).Select(w =>
            {
                var points = w.Nodes.Select((_, k) => P(w.Lat[k], w.Lon[k])).ToList();
                if (w.Nodes[0] != at)
                    points.Reverse();
                return (Way: w, Points: points);
            }).ToList();
            if (legs.Count != 3 || degree.GetValueOrDefault(at) != 3)
                return null;

            (double X, double Z) Out(List<(double X, double Z)> points)
            {
                var far = points.FirstOrDefault(q => Math.Sqrt((q.X - points[0].X) * (q.X - points[0].X) + (q.Z - points[0].Z) * (q.Z - points[0].Z)) >= 15.0, points[^1]);
                var length = Math.Max(Math.Sqrt((far.X - points[0].X) * (far.X - points[0].X) + (far.Z - points[0].Z) * (far.Z - points[0].Z)), 1e-9);
                return ((far.X - points[0].X) / length, (far.Z - points[0].Z) / length);
            }

            var heading = legs.Select(l => Out(l.Points)).ToArray();
            double Dot(int a, int b) => heading[a].X * heading[b].X + heading[a].Z * heading[b].Z;
            var (i, j) = new[] { (0, 1), (0, 2), (1, 2) }.MaxBy(q => Dot(q.Item1, q.Item2));
            var toe = 3 - i - j;
            if (Dot(i, j) < Math.Cos(25.0 * Math.PI / 180.0) || Dot(toe, i) > Math.Cos(150.0 * Math.PI / 180.0) || Dot(toe, j) > Math.Cos(150.0 * Math.PI / 180.0))
                return null;
            return (Dot(toe, i) < Dot(toe, j) ? [legs[i], legs[j]] : [legs[j], legs[i]], legs[toe].Way);
        }

        // Slides the next switch along this one's diverging route, if it's too close. True if it moved it.
        bool Space(long at)
        {
            if (Legs(at) is not { } here)
                return false;
            var (normal, reverse) = (here.Heels[0], here.Heels[1]);
            var next = reverse.Way.Nodes[0] == at ? reverse.Way.Nodes[^1] : reverse.Way.Nodes[0];
            if (done.Contains(next) || Legs(next) is not { } there || there.Toe != reverse.Way)
                return false;

            // The diverging route carried on through the next switch along its straighter route.
            static double Along(List<(double X, double Z)> q) => q.Zip(q.Skip(1), (a, b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z))).Sum();
            var run = reverse.Points.Concat(there.Heels[0].Points.Skip(1)).ToList();
            var apart = Along(reverse.Points);
            var onward = Along(there.Heels[0].Points);
            // Which way the diverging route goes off the normal route.
            (double X, double Z) Toward(List<(double X, double Z)> q)
            {
                var far = q.FirstOrDefault(c => Math.Sqrt((c.X - q[0].X) * (c.X - q[0].X) + (c.Z - q[0].Z) * (c.Z - q[0].Z)) >= 15.0, q[^1]);
                var length = Math.Max(Math.Sqrt((far.X - q[0].X) * (far.X - q[0].X) + (far.Z - q[0].Z) * (far.Z - q[0].Z)), 1e-9);
                return ((far.X - q[0].X) / length, (far.Z - q[0].Z) / length);
            }

            var nd = Toward(normal.Points);

            // A point and the heading there, s along a run of points.
            static ((double X, double Z) At, (double X, double Z) Heading)? OnPath(List<(double X, double Z)> q, double s)
            {
                for (var k = 0; k < q.Count - 1; k++)
                {
                    var piece = Math.Sqrt((q[k + 1].X - q[k].X) * (q[k + 1].X - q[k].X) + (q[k + 1].Z - q[k].Z) * (q[k + 1].Z - q[k].Z));
                    if (s <= piece && piece > 1e-9)
                        return ((q[k].X + (q[k + 1].X - q[k].X) * s / piece, q[k].Z + (q[k + 1].Z - q[k].Z) * s / piece), ((q[k + 1].X - q[k].X) / piece, (q[k + 1].Z - q[k].Z) / piece));
                    s -= piece;
                }

                return null;
            }

            // How far along the diverging route a curve from the points first reaches the mapped track no sharper than the
            // track's class allows: the next switch goes just past there.
            var yard = reverse.Way.Class is (int)RailClass.Yard or (int)RailClass.Industrial;
            var radius = yard ? 80.0 : 237.0;
            var total = Along(run);
            var needed = double.MaxValue;
            var leave = nd;
            for (var reach = 12.0; reach <= 250.0 && reach < total - 1.0; reach += 1.0)
            {
                if (OnPath(run, reach) is not { } there2)
                    break;
                var (ex, ez) = (there2.At.X - reverse.Points[0].X, there2.At.Z - reverse.Points[0].Z);
                var chord = Math.Sqrt(ex * ex + ez * ez);
                if (chord < 1.0 || (ex * leave.X + ez * leave.Z) / chord < 0.5 || there2.Heading.X * leave.X + there2.Heading.Z * leave.Z < 0.5)
                    continue;
                if (TightestRadius(reverse.Points[0], leave, there2.At, there2.Heading, chord) < radius)
                    continue;
                needed = reach + 3.0;
                break;
            }

            var distance = needed - apart;
            if (needed == double.MaxValue || distance < 0.5 || distance > MaxSlide || distance > onward - LadderRoom)
                return false;

            // The track through the next switch, and the switch's other route, which starts from wherever it goes.
            var other = there.Heels[1];
            if (TrackAt(ways.Values.Where(Usable), next, [other.Way.Id], P) is not { } track)
                return false;
            var ahead = there.Heels[0].Points[Math.Min(2, there.Heels[0].Points.Count - 1)];
            var tangent = Tangent(track);
            var sign = (ahead.X - there.Heels[0].Points[0].X) * tangent.X + (ahead.Z - there.Heels[0].Points[0].Z) * tangent.Z >= 0 ? 1 : -1;
            var u = (X: tangent.X * sign, Z: tangent.Z * sign);

            if (Slide(track, u, distance) is not { } landing)
                return false;

            // (The other route's next point is the first it can run on to without a kink: its points just past the switch's new
            // place are still beside the track, and going straight to one would be a jump sideways. If that's a long way off, the
            // route's own shape would go with what's skipped, pulling a whole track off its line: then the switch stays.)
            bool Reachable((double X, double Z) q)
            {
                var (along, across) = ((q.X - landing.At.X) * u.X + (q.Z - landing.At.Z) * u.Z, (q.Z - landing.At.Z) * u.X - (q.X - landing.At.X) * u.Z);
                return along > 1.0 && Math.Abs(across) <= along * SlideSlope;
            }

            var rest = other.Points.Skip(1).ToList();
            var first = rest.FindIndex(Reachable);
            var kept = first >= 0 ? rest.Skip(first).ToList() : [];
            if (kept.Count == 0)
                kept.Add(other.Points[^1]);
            // Too long a run to the next point would lose the route's own shape, pulling a whole track off its line: it joins the route
            // at the first place along it that it can reach instead, a new point there.
            (double X, double Z)? joinAt = null;
            var (jx, jz) = (kept[0].X - landing.At.X, kept[0].Z - landing.At.Z);
            if (jx * jx + jz * jz > LongestSkip * LongestSkip)
            {
                for (var k = 0; k + 1 < other.Points.Count && joinAt is null; k++)
                {
                    var (ax, az, bx, bz) = (other.Points[k].X, other.Points[k].Z, other.Points[k + 1].X, other.Points[k + 1].Z);
                    var steps = Math.Max(1, (int)(Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)) / 2.0));
                    for (var i = 1; i <= steps; i++)
                    {
                        var q = (X: ax + (bx - ax) * i / steps, Z: az + (bz - az) * i / steps);
                        if (Reachable(q))
                        {
                            joinAt = q;
                            kept = other.Points.Skip(k + 1).ToList();
                            if (i == steps)
                                kept = kept.Skip(1).ToList();
                            break;
                        }
                    }
                }

                if (joinAt is null || kept.Count == 0)
                    return false;
            }

            var moved = Move(track, u, distance, ways, ref nextId, projection);
            var ids = other.Way.Nodes[0] == next ? other.Way.Nodes.AsEnumerable() : other.Way.Nodes.Reverse();
            var nodes = new[] { next }.Concat(ids.Skip(other.Points.Count - kept.Count)).ToList();
            var geo = new[] { moved }.Concat(kept.Select(q => (q.X, q.Z))).ToList();
            if (joinAt is { } join)
            {
                nodes.Insert(1, nextId--);
                geo.Insert(1, join);
            }

            var geoPoints = geo.Select(q => projection.Inverse(new RoutePoint(q.X, q.Z))).ToList();
            if (other.Way.Nodes[0] != next)
            {
                nodes.Reverse();
                geoPoints.Reverse();
            }

            ways[other.Way.Id] = new NetworkWay
            {
                Id = other.Way.Id, Rail = other.Way.Rail, Class = other.Way.Class, Tags = other.Way.Tags, Nodes = [.. nodes],
                Lat = [.. geoPoints.Select(g => g.Lat)], Lon = [.. geoPoints.Select(g => g.Lon)],
            };
            return true;
        }

        foreach (var node in forced.Keys)
            problems.Remove(node);
        if (forced.Count > 0)
            Log.Info($"Crossovers and ladder links: {forced.Count / 2} laid out to a standard design ({forced.Keys.Count(links.Contains) / 2} links)");
        return new Result(Join(ways, pieces), forced, options, problems, links);
    }

    /// The ways Split cut, put back together (each run of pieces still there, end to end), so a track keeps running
    /// on through its switches as one line.
    private static List<NetworkWay> Join(Dictionary<long, NetworkWay> ways, Dictionary<long, List<long>> pieces)
    {
        var joined = new List<NetworkWay>();
        var used = new HashSet<long>();
        foreach (var (_, ids) in pieces)
        {
            NetworkWay? run = null;
            foreach (var id in ids)
            {
                used.Add(id);
                if (!ways.TryGetValue(id, out var piece))
                {
                    if (run is not null)
                        joined.Add(run);
                    run = null;
                    continue;
                }

                run = run is null || run.Nodes[^1] != piece.Nodes[0]
                    ? Flush(run, piece)
                    : new NetworkWay
                    {
                        Id = run.Id, Rail = run.Rail, Class = run.Class, Tags = run.Tags, Nodes = [.. run.Nodes, .. piece.Nodes[1..]],
                        Lat = [.. run.Lat, .. piece.Lat[1..]], Lon = [.. run.Lon, .. piece.Lon[1..]],
                    };
            }

            if (run is not null)
                joined.Add(run);

            NetworkWay Flush(NetworkWay? done, NetworkWay next)
            {
                if (done is not null)
                    joined.Add(done);
                return next;
            }
        }

        joined.AddRange(ways.Values.Where(w => !used.Contains(w.Id)));
        return joined;
    }

    /// The tightest radius of the cubic from a to b leaving a along da and arriving at b along db (unit directions),
    /// its tangents the chord long: the curve TurnoutFitting draws between a ladder link's frogs.
    internal static double TightestRadius((double X, double Z) a, (double X, double Z) da, (double X, double Z) b, (double X, double Z) db, double chord)
    {
        var tightest = double.MaxValue;
        (double X, double Z) At(double t)
        {
            var (t2, t3) = (t * t, t * t * t);
            var (h00, h10, h01, h11) = (2 * t3 - 3 * t2 + 1, t3 - 2 * t2 + t, -2 * t3 + 3 * t2, t3 - t2);
            return (h00 * a.X + h10 * chord * da.X + h01 * b.X + h11 * chord * db.X, h00 * a.Z + h10 * chord * da.Z + h01 * b.Z + h11 * chord * db.Z);
        }

        const int steps = 24;
        for (var k = 1; k < steps; k++)
        {
            var (p0, p1, p2) = (At((k - 1.0) / steps), At((double)k / steps), At((k + 1.0) / steps));
            var (ax, az, bx, bz) = (p1.X - p0.X, p1.Z - p0.Z, p2.X - p1.X, p2.Z - p1.Z);
            var turn = Math.Abs(Math.Atan2(ax * bz - az * bx, ax * bx + az * bz));
            var length = (Math.Sqrt(ax * ax + az * az) + Math.Sqrt(bx * bx + bz * bz)) / 2;
            if (turn > 1e-9)
                tightest = Math.Min(tightest, length / turn);
        }

        return tightest;
    }

    /// Whether a way comes into a node from the opposite side to every other track there: the switch's toe.
    private static bool IsToe(IEnumerable<NetworkWay> rail, long node, NetworkWay way, HashSet<long> mine, Func<double, double, (double X, double Z)> p)
    {
        var at = Neighbours(way, node).ToList();
        if (at.Count != 1)
            return false;
        var index = Array.IndexOf(way.Nodes, node);
        var (x, z) = p(way.Lat[index], way.Lon[index]);
        var others = rail.Where(w => !mine.Contains(w.Id) && w.Nodes.Contains(node)).SelectMany(w => Neighbours(w, node)).ToList();
        return others.Count >= 2 && others.All(o => (o.X - x) * (at[0].X - x) + (o.Z - z) * (at[0].Z - z) < 0);

        IEnumerable<(double X, double Z)> Neighbours(NetworkWay w, long n)
        {
            var i = Array.IndexOf(w.Nodes, n);
            if (i > 0)
                yield return p(w.Lat[i - 1], w.Lon[i - 1]);
            if (i < w.Nodes.Length - 1)
                yield return p(w.Lat[i + 1], w.Lon[i + 1]);
        }
    }

    /// The track through a switch node, the crossover's own ways left out: one way through it, or two meeting at it
    /// nearly straight on.
    private static Track? TrackAt(IEnumerable<NetworkWay> rail, long node, HashSet<long> crossover, Func<double, double, (double X, double Z)> p)
    {
        var at = rail.Where(w => !crossover.Contains(w.Id) && w.Nodes.Contains(node)).ToList();
        List<(long, double, double)> Run(NetworkWay w, bool reversed)
        {
            var list = w.Nodes.Select((n, k) => (n, p(w.Lat[k], w.Lon[k]).X, p(w.Lat[k], w.Lon[k]).Z)).ToList();
            if (reversed)
                list.Reverse();
            return list;
        }

        if (at.Count == 1)
        {
            var i = Array.IndexOf(at[0].Nodes, node);
            if (i <= 0 || i >= at[0].Nodes.Length - 1)
                return null;
            return new Track { Parts = [(at[0], false)], Points = Run(at[0], false), Index = i };
        }

        if (at.Count != 2 || at.Any(w => w.Nodes[0] != node && w.Nodes[^1] != node))
            return null;

        // The first runs into the node, the second out of it.
        var one = Run(at[0], at[0].Nodes[0] == node);
        var two = Run(at[1], at[1].Nodes[^1] == node);
        var (ax, az) = (one[^1].Item2 - one[^2].Item2, one[^1].Item3 - one[^2].Item3);
        var (bx, bz) = (two[1].Item2 - two[0].Item2, two[1].Item3 - two[0].Item3);
        var dot = (ax * bx + az * bz) / Math.Max(Math.Sqrt((ax * ax + az * az) * (bx * bx + bz * bz)), 1e-9);
        if (dot < Math.Cos(20.0 * Math.PI / 180.0))
            return null;
        return new Track
        {
            Parts = [(at[0], at[0].Nodes[0] == node), (at[1], at[1].Nodes[^1] == node)], Points = [.. one, .. two.Skip(1)], Index = one.Count - 1,
        };
    }

    /// The track's direction at its node, from points about 15 m either side (OpenStreetMap's points around a switch
    /// are often only a metre or two apart, and bent).
    private static (double X, double Z) Tangent(Track t)
    {
        var node = t.Points[t.Index];
        double Distance(int i) => Math.Sqrt((t.Points[i].X - node.X) * (t.Points[i].X - node.X) + (t.Points[i].Z - node.Z) * (t.Points[i].Z - node.Z));
        var i0 = t.Index;
        while (i0 > 0 && Distance(i0) < 15.0)
            i0--;
        var i1 = t.Index;
        while (i1 < t.Points.Count - 1 && Distance(i1) < 15.0)
            i1++;
        var (a, b) = (t.Points[i0], t.Points[i1]);
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }

    /// Where along a track, walking from its node, the projection onto direction u has moved by distance: the point
    /// it lands at and the run index it goes in after. Null past the track's end.
    private static (int After, (double X, double Z) At)? Slide(Track t, (double X, double Z) u, double distance)
    {
        var origin = t.Points[t.Index];
        double Along(int i) => (t.Points[i].X - origin.X) * u.X + (t.Points[i].Z - origin.Z) * u.Z;
        if (Math.Abs(distance) < 0.05)
            return (t.Index, (origin.X, origin.Z));

        var step = t.Index + 1 < t.Points.Count && Along(t.Index + 1) * distance > 0 ? 1 : -1;
        for (var k = t.Index; k + step >= 0 && k + step < t.Points.Count; k += step)
        {
            var (s0, s1) = (Along(k), Along(k + step));
            if ((distance - s0) * (distance - s1) <= 0 && Math.Abs(s1 - s0) > 1e-6)
            {
                var r = (distance - s0) / (s1 - s0);
                var (q0, q1) = (t.Points[k], t.Points[k + step]);
                return (Math.Min(k, k + step), (q0.X + (q1.X - q0.X) * r, q0.Z + (q1.Z - q0.Z) * r));
            }
        }

        return null;
    }

    /// Moves a track's switch node along it by distance: the old point stays (under a new id, so the shape is kept),
    /// the node goes in at the new point, and where the track is two ways, they now meet there. Returns where it went.
    private static (double X, double Z) Move(Track t, (double X, double Z) u, double distance, Dictionary<long, NetworkWay> ways, ref long nextId,
        RouteProjection projection)
    {
        if (Math.Abs(distance) < 0.05 || Slide(t, u, distance) is not { } slid)
            return (t.Points[t.Index].X, t.Points[t.Index].Z);

        var node = t.Points[t.Index].Node;
        var run = t.Points.ToList();
        run[t.Index] = (nextId--, run[t.Index].X, run[t.Index].Z);
        run.Insert(slid.After + 1, (node, slid.At.X, slid.At.Z));
        var cut = slid.After + 1;

        NetworkWay Make(NetworkWay like, IEnumerable<(long Node, double X, double Z)> points, bool reversed)
        {
            var list = points.ToList();
            if (reversed)
                list.Reverse();
            var geo = list.Select(q => projection.Inverse(new RoutePoint(q.X, q.Z))).ToList();
            return new NetworkWay
            {
                Id = like.Id, Rail = like.Rail, Class = like.Class, Nodes = [.. list.Select(q => q.Node)], Lat = [.. geo.Select(g => g.Lat)],
                Lon = [.. geo.Select(g => g.Lon)], Tags = like.Tags,
            };
        }

        if (t.Parts.Count == 1)
            ways[t.Parts[0].Way.Id] = Make(t.Parts[0].Way, run, false);
        else
        {
            ways[t.Parts[0].Way.Id] = Make(t.Parts[0].Way, run.Take(cut + 1), t.Parts[0].Reversed);
            ways[t.Parts[1].Way.Id] = Make(t.Parts[1].Way, run.Skip(cut), t.Parts[1].Reversed);
        }

        return slid.At;
    }
}
