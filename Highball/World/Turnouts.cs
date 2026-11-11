namespace Highball.World;

/// One way along a line from a point: the line, the point, and which way along it (+1 up the points, -1 down). s is
/// metres along the leg from the point.
public readonly record struct Leg(NetworkLine Line, int Point, int Direction)
{
    public float Start => Line.S[Point];

    public float Length => Direction > 0 ? Line.S[^1] - Start : Start;

    /// The leg's s as the line's own distance along it.
    public float LineS(float s) => Start + Direction * s;

    /// Where the leg is s metres out: the centreline (world x, z), its formation height, and the horizontal unit
    /// vectors along the leg and to its right.
    public (double X, double Z, float Y, float Fx, float Fz, float Rx, float Rz) At(float s)
    {
        var target = Math.Clamp(LineS(s), 0f, Line.S[^1]);
        var i = Array.BinarySearch(Line.S, target);
        if (i < 0)
            i = ~i;
        i = Math.Clamp(i, 1, Line.Count - 1);
        var span = Line.S[i] - Line.S[i - 1];
        var t = span > 1e-5f ? (target - Line.S[i - 1]) / span : 0f;
        var x = Line.X[i - 1] + (Line.X[i] - Line.X[i - 1]) * t;
        var z = Line.Z[i - 1] + (Line.Z[i] - Line.Z[i - 1]) * t;
        var y = Line.Y[i - 1] + (Line.Y[i] - Line.Y[i - 1]) * t;
        var fx = (float)(Line.X[i] - Line.X[i - 1]) * Direction;
        var fz = (float)(Line.Z[i] - Line.Z[i - 1]) * Direction;
        var length = MathF.Max(MathF.Sqrt(fx * fx + fz * fz), 1e-6f);
        (fx, fz) = (fx / length, fz / length);
        return (x, z, y, fx, fz, -fz, fx);
    }
}

/// A turnout (a switch) where one track divides into two: worked out from the lines meeting at an OpenStreetMap node,
/// with its parts placed from their real geometry rather than a fixed drawing.
///
/// The node is the point of switch. Two legs leave it the same way, close together: the one running straighter on
/// from the third (the toe) is the normal route, the other the reverse (diverging) route, to Side of it. The frog is
/// where the reverse route's inner rail crosses the normal route's: where the two centrelines are two rail spacings
/// apart. The switch blades run from the point of switch for BladeLength; closure rails carry on from them to the
/// frog; a guard rail faces the frog on each route's outer rail; long timbers carry both tracks out past the frog.
public sealed class Turnout
{
    /// How far apart the rails' centres are, for the standard gauge with 136 lb rail.
    public const float RailOffset = 1.435f * 0.5f + 0.0373f;
    /// How far the blades swing at their tips, and how far inside its stock rail a closed blade sits there.
    public const float Throw = 0.12f;
    public const float Inset = 0.07f;
    /// Where a procedural frog's point rails pick up past the crossing point.
    public const float FrogGapAfter = 0.3f;
    /// The gap a wheel flange runs in beside the frog point and the guard rails (1 7/8 in).
    public const float Flangeway = 0.0476f;
    /// A guard rail's centre from its running rail's: a rail head and a flangeway.
    public const float GuardOffset = 2f * 0.0373f + Flangeway;
    public const float GuardLength = 5f;
    /// How far past the frog the long timbers go.
    public const float TimberTail = 4f;
    /// How far apart the routes are at the blades' heels, and at the last long timber.
    public const float HeelSpread = 0.14f;
    private const float TimberClear = 2.8f;
    /// How far out along the reverse route a frog is looked for.
    private const float FrogSearch = 250f;

    public required long Node { get; init; }
    public required Leg Toe { get; init; }
    public required Leg Normal { get; init; }
    public required Leg Reverse { get; init; }
    /// +1 if the reverse route goes off to the right of the normal one (seen from the toe), -1 to the left.
    public required int Side { get; init; }
    /// The crossing point's distance along each route.
    public required float FrogNormal { get; init; }
    public required float FrogReverse { get; init; }
    public required float BladeLength { get; init; }

    /// How far the reverse route's centreline is from the normal one's (along Side), every SpreadStep metres along
    /// the normal route out to FrogNormal + TimberTail, or BallastNormal if that's further.
    public required float[] Spread { get; init; }

    /// Where the two routes' ballast tops part, along the normal and the reverse route: up to there the normal route's
    /// ballast widens to carry both, and the reverse route lays none of its own.
    public required float BallastNormal { get; init; }
    public required float BallastReverse { get; init; }
    public const float SpreadStep = 0.5f;

    /// The standard size it was fitted to (frog number), if it was.
    public int? Number { get; init; }

    /// Whether it's been laid out to a design (and has a size to its name).
    public bool Modelled => Number is not null;

    /// How far along the normal route the long timbers carry both routes: past the frog till a tie of each would clear
    /// the other's.
    public required float TimberEnd { get; init; }

    /// The standard sizes: frog numbers and their lead curves' radii (US practice), as modelled in Tools/Blender/turnouts.py.
    public static readonly (int Number, float Radius)[] Sizes = Trains.TurnoutSizes.Sizes;

    public static float RadiusOf(int number) => Trains.TurnoutSizes.RadiusOf(number);

    /// A size's frog, from the point of switch: where the routes' centrelines are two rail spacings apart.
    public static float FrogOf(int number) => MathF.Sqrt(4f * RadiusOf(number) * RailOffset);

    /// A size's blades: to where the routes' rails are HeelSpread apart, so an open blade clears its stock rail.
    public static float BladeOf(int number) => Math.Clamp(MathF.Sqrt(2f * RadiusOf(number) * HeelSpread), 3f, 16f);

    public double X => Normal.Line.X[Normal.Point];
    public double Z => Normal.Line.Z[Normal.Point];

    public float SpreadAt(float s)
    {
        var f = Math.Clamp(s / SpreadStep, 0f, Spread.Length - 1);
        var i = Math.Min((int)f, Spread.Length - 2);
        return i < 0 ? Spread[0] : Spread[i] + (Spread[i + 1] - Spread[i]) * (f - i);
    }

    /// Every turnout in the lines: nodes where exactly three legs meet, two leaving close together and the third
    /// opposite, all single standard-gauge track on the ground (multiple tracks, street track and the rest are left
    /// as they are). Marks each one's rails and ties on its lines (NetworkLine.TurnoutZones).
    /// sizes: the standard size each fitted switch was laid out to (TurnoutFitting); skipped, if given, counts why each
    /// three-legged junction that didn't become a turnout was left.
    public static List<Turnout> Find(IReadOnlyList<NetworkLine> lines, IReadOnlyDictionary<(long Node, int Line), int>? sizes = null,
        Dictionary<string, int>? skipped = null)
    {
        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines)
        {
            line.TurnoutZones.Clear();
            if (!line.Rail)
                continue;
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }
        }

        var turnouts = new List<Turnout>();
        foreach (var (node, list) in uses)
        {
            if (list.Count < 2)
                continue;
            var legs = new List<Leg>();
            foreach (var (line, point) in list)
            {
                if (point > 0)
                    legs.Add(new Leg(line, point, -1));
                if (point < line.Count - 1)
                    legs.Add(new Leg(line, point, 1));
            }

            if (legs.Count == 4 && IsCrossing(legs))
                continue;

            if (legs.Count == 4 && legs.All(Plain))
            {
                // Two tracks merging and two dividing at the one node: two turnouts point to point, one facing each
                // way, each with the straighter of the other pair as its toe.
                foreach (var made in BackToBack(node, legs, sizes))
                    turnouts.Add(made);
                continue;
            }

            if (legs.Count != 3)
                continue;
            var odd = legs.FindIndex(l => !Plain(l));
            var why = odd >= 0 ? Why(legs[odd]) : null;
            if (why is null && Make(node, legs, sizes, out why) is { } turnout)
                turnouts.Add(turnout);
            else if (skipped is not null)
                skipped[why!] = skipped.GetValueOrDefault(why!) + 1;
        }

        return turnouts;
    }

    /// Why three legs at a node make no turnout, for the audit.
    internal static string Diagnose(long node, List<Leg> legs, IReadOnlyDictionary<(long Node, int Line), int>? sizes)
    {
        var zones = legs.Select(l => l.Line.TurnoutZones.Count).ToList();
        var made = Make(node, legs, sizes, out var why);
        for (var k = 0; k < legs.Count && made is not null; k++)
            if (legs[k].Line.TurnoutZones.Count > zones[k])
                legs[k].Line.TurnoutZones.RemoveRange(zones[k], legs[k].Line.TurnoutZones.Count - zones[k]);
        return made is not null ? "would make one" : why ?? "unknown";
    }

    /// Four legs as two tracks each running through the node dead straight: a crossing however shallow, which two switches
    /// point to point would only be if their diverging routes happened to line up exactly.
    internal static bool IsCrossing(List<Leg> legs)
    {
        if (legs.Count != 4 || legs.GroupBy(l => (l.Line, l.Point)).Any(g => g.Count() != 2))
            return false;
        foreach (var through in legs.GroupBy(l => (l.Line, l.Point)))
        {
            var (a, b) = (Heading(through.First()), Heading(through.Last()));
            if (a.X * b.X + a.Z * b.Z > -0.995f)
                return false;
        }

        return true;
    }

    internal static bool Plain(Leg leg) =>
        leg.Line is { Tracks: 1, Embedded: false, Disused: false } && MathF.Abs(leg.Line.Gauge - 1.435f) < 0.01f && leg.Length > 3f &&
        leg.Line.Structures[Math.Clamp(leg.Direction > 0 ? leg.Point : leg.Point - 1, 0, leg.Line.Structures.Length - 1)] != Structure.Tunnel;

    internal static string Why(Leg leg) =>
        leg.Line.Tracks != 1 ? "multiple tracks" : leg.Line.Embedded ? "street track" : leg.Line.Disused ? "disused" :
        MathF.Abs(leg.Line.Gauge - 1.435f) >= 0.01f ? "gauge" : leg.Length <= 3f ? "a track under 3 m long" : "on a bridge or in a tunnel";

    private static IEnumerable<Turnout> BackToBack(long node, List<Leg> legs, IReadOnlyDictionary<(long Node, int Line), int>? sizes)
    {
        foreach (var three in Pairs(legs))
            if (Make(node, three, sizes, out _) is { } made)
                yield return made;
    }

    internal static (float X, float Z) Heading(Leg leg)
    {
        var (x0, z0, _, _, _, _, _) = leg.At(0f);
        var (x1, z1, _, _, _, _, _) = leg.At(MathF.Min(15f, leg.Length));
        var (dx, dz) = ((float)(x1 - x0), (float)(z1 - z0));
        var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-6f);
        return (dx / length, dz / length);
    }

    /// Which of three legs is which: the two closest together leave towards the heel, the third (roughly opposite) is
    /// the toe; of the two, the one running straighter on from the toe is the normal route. Side: +1 if the reverse
    /// route goes off to the right of the normal one. False if the legs aren't shaped like a switch.
    internal static bool Arrange(List<Leg> legs, out Leg toeLeg, out Leg normal, out Leg reverse, out int side)
    {
        (toeLeg, normal, reverse, side) = (default, default, default, 0);
        var headings = legs.Select(Heading).ToArray();
        float Dot(int a, int b) => headings[a].X * headings[b].X + headings[a].Z * headings[b].Z;

        var (i, j) = new[] { (0, 1), (0, 2), (1, 2) }.MaxBy(p => Dot(p.Item1, p.Item2));
        var toe = 3 - i - j;
        if (Dot(i, j) < MathF.Cos(25f * MathF.PI / 180f) || Dot(toe, i) > MathF.Cos(150f * MathF.PI / 180f) || Dot(toe, j) > MathF.Cos(150f * MathF.PI / 180f))
            return false;

        toeLeg = legs[toe];
        (normal, reverse) = Dot(toe, i) < Dot(toe, j) ? (legs[i], legs[j]) : (legs[j], legs[i]);
        var n = Heading(normal);
        var r = Heading(reverse);
        side = n.X * r.Z - n.Z * r.X > 0 ? 1 : -1; // right of (fx, fz) is (-fz, fx)
        return true;
    }

    /// Four legs as two switches point to point: each pair leaving together, with the straighter of the other pair
    /// as its toe, as three-leg lists. Empty if they aren't shaped like that.
    internal static IEnumerable<List<Leg>> Pairs(List<Leg> legs)
    {
        var headings = legs.Select(Heading).ToArray();
        float Dot(int a, int b) => headings[a].X * headings[b].X + headings[a].Z * headings[b].Z;
        var (i, j) = new[] { (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3) }.MaxBy(p => Dot(p.Item1, p.Item2));
        var others = Enumerable.Range(0, 4).Where(k => k != i && k != j).ToArray();
        var (k, l) = (others[0], others[1]);
        if (Dot(i, j) < MathF.Cos(25f * MathF.PI / 180f) || Dot(k, l) < MathF.Cos(25f * MathF.PI / 180f) || Dot(i, k) > MathF.Cos(150f * MathF.PI / 180f))
            yield break;

        var toeForIJ = Dot(k, i) + Dot(k, j) < Dot(l, i) + Dot(l, j) ? k : l;
        var toeForKL = Dot(i, k) + Dot(i, l) < Dot(j, k) + Dot(j, l) ? i : j;
        yield return [legs[toeForIJ], legs[i], legs[j]];
        yield return [legs[toeForKL], legs[k], legs[l]];
    }

    private static Turnout? Make(long node, List<Leg> legs, IReadOnlyDictionary<(long Node, int Line), int>? sizes, out string? why)
    {
        why = "not shaped like a switch";
        if (!Arrange(legs, out var toeLeg, out var normal, out var reverse, out var side))
            return null;

        // The routes as the fitting took them: on a switch whose two routes leave nearly alike, laying the diverging
        // one out can leave it the straighter of the two.
        if (sizes is not null && !sizes.ContainsKey((node, reverse.Line.Index)) && sizes.ContainsKey((node, normal.Line.Index)))
            (normal, reverse, side) = (reverse, normal, -side);

        // The frog: walking out along the reverse route, the first place its centreline is two rail spacings off.
        float? frogReverse = null, frogNormal = null;
        for (var s = 0.5f; s < MathF.Min(FrogSearch, reverse.Length); s += 0.25f)
        {
            var (x, z, _, _, _, _, _) = reverse.At(s);
            if (Offset(normal, x, z) is not { } at || at.Offset * side < 2f * RailOffset)
                continue;
            (frogReverse, frogNormal) = (s, at.S);
            break;
        }

        why = "no frog within 250 m, or too close";
        if (frogReverse is not { } fr || frogNormal is not { } fn || fn + 1f > normal.Length || fr + 1f > reverse.Length || fn < 6f)
            return null;
        why = null;

        // The spread, on out past the timbers till the two routes' ballast tops part (or a route ends).
        var apart = normal.Line.BallastHalfTop + reverse.Line.BallastHalfTop;
        var spreads = new List<float>();
        float ballastNormal = 0f, ballastReverse = 0f;
        for (var k = 0; ; k++)
        {
            var s = k * SpreadStep;
            var (x, z, _, _, _, _, _) = normal.At(MathF.Min(s, normal.Length));
            var at = Offset(reverse, x, z);
            var value = at is { } a ? -a.Offset * side : 0f; // the normal route seen from the reverse, turned round
            spreads.Add(value);
            (ballastNormal, ballastReverse) = (MathF.Min(s, normal.Length), MathF.Min(at?.S ?? fr, reverse.Length));
            if (s >= fn + TimberTail && (value >= apart || at is null || s >= normal.Length || ballastReverse >= reverse.Length || s >= FrogSearch))
                break;
        }

        var spread = spreads.ToArray();

        // The blades run to where the routes have parted enough for an open one to clear its stock rail, whatever curve
        // they part on.
        var heel = Array.FindIndex(spread, v => v >= HeelSpread) * SpreadStep;
        var blade = Math.Clamp(heel, 3f, MathF.Min(16f, fn * 0.6f));
        var timbers = Array.FindIndex(spread, (int)((fn + TimberTail) / SpreadStep), v => v >= TimberClear);
        var timberEnd = timbers < 0 ? fn + TimberTail : timbers * SpreadStep;

        var turnout = new Turnout
        {
            Node = node, Toe = toeLeg, Normal = normal, Reverse = reverse, Side = side, FrogNormal = fn, FrogReverse = fr,
            BladeLength = blade, TimberEnd = timberEnd, Spread = spread, BallastNormal = ballastNormal, BallastReverse = ballastReverse,
            Number = sizes is not null && sizes.TryGetValue((node, reverse.Line.Index), out var number) ? number : null,
        };

        // The normal route's rail on the reverse side, and the reverse route's on the normal side, are the turnout's
        // own as far as the frog; the long timbers carry the normal route out past it, and the reverse route has none
        // of its own till then.
        normal.Line.TurnoutZones.Add(new TurnoutZone(turnout, true, Span(normal, 0f, fn + FrogGapAfter), Span(normal, 0f, timberEnd), side * normal.Direction,
            Span(normal, 0f, ballastNormal)));
        reverse.Line.TurnoutZones.Add(new TurnoutZone(turnout, false, Span(reverse, 0f, fr + FrogGapAfter), Span(reverse, 0f, fr + timberEnd - fn), -side * reverse.Direction,
            Span(reverse, 0f, ballastReverse)));
        return turnout;
    }

    /// A stretch of a leg as the line's own distances, low to high.
    private static (float From, float To) Span(Leg leg, float a, float b)
    {
        var (sa, sb) = (leg.LineS(a), leg.LineS(b));
        return (MathF.Min(sa, sb), MathF.Max(sa, sb));
    }

    /// How far (x, z) is to the right of a leg (negative: left), and how far along the leg, within its first 120 m.
    internal static (float Offset, float S)? Offset(Leg leg, double x, double z)
    {
        (float, float)? best = null;
        var bestDistance = double.MaxValue;
        var line = leg.Line;
        for (var k = 0; k < line.Count; k++)
        {
            var p = leg.Point + leg.Direction * k;
            var q = p + leg.Direction;
            if (q < 0 || q >= line.Count || MathF.Abs(line.S[p] - leg.Start) > 120f)
                break;

            var (ax, az, bx, bz) = (line.X[p], line.Z[p], line.X[q], line.Z[q]);
            var (dx, dz) = (bx - ax, bz - az);
            var length2 = dx * dx + dz * dz;
            var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0, 1) : 0;
            var (px, pz) = (ax + dx * t, az + dz * t);
            var d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d >= bestDistance)
                continue;
            bestDistance = d;
            var length = Math.Max(Math.Sqrt(length2), 1e-9);
            var cross = (dx * (z - az) - dz * (x - ax)) / length; // positive to the right, (-dz, dx)
            best = ((float)cross, MathF.Abs(line.S[p] - leg.Start) + (float)(t * length));
        }

        return best;
    }
}

/// A turnout's claim on one of its lines: the rail it takes over (RailSide, in the line's own right-hand sense; 0 for
/// both) over Rails, its timbers over Timbers, and its ballast over Ballast (line distances). Normal: the normal route
/// (whose ties become the long timbers and whose ballast widens to carry both routes), else the reverse route (whose
/// ties and ballast give way to them) or the toe.
public readonly record struct TurnoutZone(Turnout Turnout, bool Normal, (float From, float To) Rails, (float From, float To) Timbers, int RailSide,
    (float From, float To) Ballast);
