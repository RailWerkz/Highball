namespace Highball.World;

/// Lays out every switch's routes before anything else is built on the track. OpenStreetMap draws a junction however the
/// mapper clicked it, so the diverging route is laid out again as one curve, from the point of switch (leaving along the
/// normal route, which isn't touched) onto the mapped track (in line with it). Its shape comes from the track around it,
/// not from a fixed design, so it never has to double back to get on to the track it's joining; the frog, at whatever
/// angle that makes, falls wherever the routes come apart, and the size a switch is called is the standard one nearest
/// that angle. A size chosen by hand (SwitchSizes) sets the tightest the curve may be, if it can be kept to.
public static class TurnoutFitting
{
    /// How far a crossover's track as mapped may bow off the line between its switches.
    private const float MaxLinkBow = 3f;
    /// The tightest curves a diverging route is laid to (No. 6's and No. 10's), and the tightest laid at all.
    private const float YardRadius = 80f;
    private const float MainRadius = 237f;
    private const double TightestLaid = 70.0;
    /// The curves tried in turn where the route must ease over and back: the gentlest that fits, for a smooth reverse curve.
    private static readonly double[] EasedRadii = [500.0, 300.0, 200.0, 0.0];
    /// How far along the diverging route the curve may end, nearest and furthest; short of what's next on it.
    private const float MinReach = 12f;
    private const float MaxReach = 250f;
    private const float SwitchClearance = 2f;
    /// The most a line may turn at a switch's points (the sine of it) before its approach is laid in again, and how far back that goes.
    private const double TangentAngle = 0.01;
    private const float TangentReach = 80f;
    private const float EndClearance = 3f;
    /// Over how much of a leg its heading at a node is read.
    private const float HeadingReach = 6f;
    /// A curve gentler than this counts as straight, when judging whether a curve doubles back.
    private const double StraightRadius = 3000.0;

    /// Fits every switch in the lines, and returns the size each came out nearest: by its node and its diverging line's
    /// index. chosen: sizes picked by hand, by node, each a tightest curve to keep to; options, if given, gets what each
    /// switch could be fitted to. forced: switches placed as part of a crossover (CrossoverFitting): a pair's track is
    /// one curve from one's points to the other's. held gets the points that mustn't move after this.
    public static Dictionary<(long Node, int Line), int> Fit(IReadOnlyList<NetworkLine> lines, IReadOnlyDictionary<long, int>? chosen = null,
        Dictionary<long, SwitchOptions>? options = null, IReadOnlyDictionary<long, (int Number, float Half)>? forced = null,
        Dictionary<NetworkLine, List<(float From, float To)>>? held = null, IReadOnlyDictionary<long, float>? curves = null)
    {
        var fitted = new Dictionary<(long Node, int Line), int>();

        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines)
        {
            if (!line.Rail)
                continue;
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }
        }

        var junctions = uses.Where(u => u.Value.Count > 1).Select(u => u.Key).ToHashSet();
        var changed = new HashSet<NetworkLine>();
        var arranged = new List<(long Node, Leg Normal, Leg Reverse, int Side)>();
        foreach (var node in junctions)
        {
            var legs = new List<Leg>();
            foreach (var (line, point) in uses[node])
            {
                if (point > 0)
                    legs.Add(new Leg(line, point, -1));
                if (point < line.Count - 1)
                    legs.Add(new Leg(line, point, 1));
            }

            if (!legs.All(Turnout.Plain))
                continue;
            var switches = legs.Count switch
            {
                3 => [legs],
                4 => Turnout.Pairs(legs).ToList(),
                _ => [],
            };

            foreach (var three in switches)
                if (Turnout.Arrange(three, out _, out var normal, out var reverse, out var side))
                {
                    // A crossover's switch diverges along the crossover, whichever of its routes OpenStreetMap drew
                    // straighter (on near-parallel tracks either may be).
                    long? Next(Leg leg) => leg.Line.Nodes.Where(n => junctions.Contains(n.Node) && (n.Point - leg.Point) * leg.Direction > 0)
                        .OrderBy(n => (n.Point - leg.Point) * leg.Direction).Select(n => (long?)n.Node).FirstOrDefault();
                    if (forced?.ContainsKey(node) == true && forced.ContainsKey(Next(normal) ?? 0) && !forced.ContainsKey(Next(reverse) ?? 0))
                        (normal, reverse, side) = (reverse, normal, -side);
                    arranged.Add((node, normal, reverse, side));
                }
        }

        // A short, nearly straight track that's the diverging route of two switches, each diverging towards the other from
        // a different track with no junction between them (a crossover), is shared between them: it's one curve from one
        // point of switch to the other. (Any other is a track in its own right with a switch at each end, such as a siding:
        // each switch runs on to it on its own, and its shape is kept.)
        bool Facing((long Node, Leg Normal, Leg Reverse, int Side) a, (long Node, Leg Normal, Leg Reverse, int Side) b)
        {
            var (from, to) = (a.Reverse, b.Reverse);
            if (to.Direction != -from.Direction || (to.Point - from.Point) * from.Direction <= 0 || a.Normal.Line == b.Normal.Line ||
                from.Line.Nodes.Any(n => junctions.Contains(n.Item1) && (n.Item2 - from.Point) * from.Direction > 0 && (to.Point - n.Item2) * from.Direction > 0))
                return false;
            if (forced?.ContainsKey(a.Node) == true && forced.ContainsKey(b.Node))
                return true;
            if (MathF.Abs(to.Start - from.Start) > CrossoverFitting.MaxLinkLength)
                return false;

            var line = from.Line;
            var (ax, az, bx, bz) = (line.X[from.Point], line.Z[from.Point], line.X[to.Point], line.Z[to.Point]);
            var chord = Math.Max(Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)), 1e-6);
            for (var p = Math.Min(from.Point, to.Point); p <= Math.Max(from.Point, to.Point); p++)
                if (Math.Abs((line.X[p] - ax) * (bz - az) - (line.Z[p] - az) * (bx - ax)) / chord > MaxLinkBow)
                    return false;
            return true;
        }

        var pairs = new List<(int A, int B)>();
        for (var i = 0; i < arranged.Count; i++)
            for (var j = 0; j < arranged.Count; j++)
                if (arranged[i].Reverse.Line == arranged[j].Reverse.Line && arranged[i].Reverse.Direction > 0 && Facing(arranged[i], arranged[j]))
                    pairs.Add((i, j));
        var paired = pairs.SelectMany(q => new[] { q.A, q.B }).ToHashSet();

        var settled = new List<(long Node, Leg Normal, Leg Reverse, int Last, float Frog)>();
        void Done(int index, int number, int last, float frog)
        {
            var (node, normal, reverse, _) = arranged[index];
            changed.Add(reverse.Line);
            fitted[(node, reverse.Line.Index)] = number;
            settled.Add((node, normal, reverse, last, frog));
        }

        for (var i = 0; i < arranged.Count; i++)
        {
            if (paired.Contains(i))
                continue;
            var (node, normal, reverse, side) = arranged[i];
            var pick = chosen?.GetValueOrDefault(node, -1) is { } picked and >= 0 ? picked : (int?)null;
            var handSet = curves?.TryGetValue(node, out var byHand) == true ? byHand : (float?)null;
            if (Reshape(node, normal, reverse, side, junctions, pick, options, handSet) is { } solved)
                Done(i, solved.Number, solved.Last, solved.Frog);

            // Two routes leaving nearly alike could be laid either way about; the other way may be the one with room.
            else if (AlmostAlike(normal, reverse) && Reshape(node, reverse, normal, -side, junctions, pick, options, handSet) is { } other)
            {
                arranged[i] = (node, reverse, normal, -side);
                Done(i, other.Number, other.Last, other.Frog);
            }
        }

        foreach (var (first, second) in pairs)
        {
            var (a, b) = (arranged[first], arranged[second]);
            var line = a.Reverse.Line;
            var (start, end) = ((line.X[a.Reverse.Point], line.Z[a.Reverse.Point]), (line.X[b.Reverse.Point], line.Z[b.Reverse.Point]));
            var leave = HeadingOf(a.Normal);
            var away = HeadingOf(b.Normal);
            var arrive = (-away.X, -away.Z);
            if (Alignment.Join(start, leave, end, arrive) is not { } join || join.Radius < TightestLaid)
                continue;

            var (lo, hi) = (a.Reverse.Point, b.Reverse.Point);
            line.Alignment?.Splice(lo, hi, join.Pieces);
            line.Alignment?.Sample(line, lo, hi);

            // (Both switches of a pair are called the same size: the nearer to a real frog of the two.)
            var points = Enumerable.Range(lo + 1, hi - lo).Select(p => (line.X[p], line.Z[p])).ToList();
            if (FrogOf(a.Normal, a.Side, points) is not { } frogA)
                continue;
            var back = Enumerable.Range(lo, hi - lo).Reverse().Select(p => (line.X[p], line.Z[p])).ToList();
            if (FrogOf(b.Normal, b.Side, back) is not { } frogB)
                continue;
            // (A crossover laid out to a size is that size, whatever its curve's frog angle rounds to.)
            var number = forced?.TryGetValue(a.Node, out var laidOut) == true ? laidOut.Number : Math.Min(frogA.Number, frogB.Number);
            Done(first, number, hi, frogA.S);
            Done(second, number, lo, frogB.S);
            Tangent(a.Node, a.Reverse, leave, junctions, YardRadius);
            Tangent(b.Node, b.Reverse, away, junctions, YardRadius);
        }

        foreach (var line in changed)
            Measure(line);

        // What's been laid out: the points that can't be moved after this, the diverging route's own curve and the normal
        // route out past the frog.
        if (held is not null)
            foreach (var (_, normal, reverse, last, frog) in settled)
            {
                void Hold(NetworkLine line, int a, int b)
                {
                    if (!held.TryGetValue(line, out var list))
                        held[line] = list = [];
                    list.Add((Math.Min(a, b), Math.Max(a, b)));
                }

                Hold(reverse.Line, reverse.Point, last);
                var to = normal.Point;
                while (to + normal.Direction >= 0 && to + normal.Direction < normal.Line.Count && MathF.Abs(normal.Line.S[to] - normal.Start) < frog + Turnout.TimberTail)
                    to += normal.Direction;
                Hold(normal.Line, normal.Point, to);
            }

        return fitted;
    }

    /// The way a leg leaves its node, over its first few metres (OpenStreetMap's last point before a junction is often a
    /// metre off, and bent).
    private static (double X, double Z) HeadingOf(Leg leg)
    {
        var (x0, z0, _, _, _, _, _) = leg.At(0f);
        var (x1, z1, _, _, _, _, _) = leg.At(MathF.Min(HeadingReach, leg.Length));
        var length = Math.Max(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)), 1e-9);
        return ((x1 - x0) / length, (z1 - z0) / length);
    }

    /// Where the frog falls walking out over points from a switch: the first place the route is two rail spacings off
    /// the normal one. Its distance along the normal route, and the standard size whose frog angle is nearest the angle
    /// the routes cross at there. Null if it never does, or the normal route ends first.
    private static (float S, int Number)? FrogOf(Leg normal, int side, List<(double X, double Z)> points)
    {
        for (var k = 1; k < points.Count; k++)
        {
            if (Turnout.Offset(normal, points[k].X, points[k].Z) is not { } at || at.S >= normal.Length - 1f)
                return null;
            if (at.Offset * side < 2f * Turnout.RailOffset)
                continue;
            if (at.S < 6f)
                return null;
            var (_, _, _, fx, fz, _, _) = normal.At(at.S);
            var (dx, dz) = (points[k].X - points[k - 1].X, points[k].Z - points[k - 1].Z);
            var angle = Math.Abs(Math.Atan2(fx * dz - fz * dx, fx * dx + fz * dz));
            return (at.S, Turnout.Sizes.Select(z => z.Number).MinBy(n => Math.Abs(Math.Atan(1.0 / n) - angle)));
        }

        return null;
    }

    /// Whether a switch's two routes leave within a few degrees of one another.
    private static bool AlmostAlike(Leg a, Leg b)
    {
        var (u, v) = (HeadingOf(a), HeadingOf(b));
        return u.X * v.X + u.Z * v.Z > AlikeCos;
    }

    private const double AlikeCos = 0.98;

    /// How far past the points a real switch's routes have parted by.
    private const float LongestFrog = 75f;

    /// Lays the diverging route out as two arcs leaving the normal route at the point of switch, along it, and running on
    /// to the mapped track at one of its points, in line with it: at the nearest point that lets it bend one way only and
    /// no tighter than the track's class allows, so it arrives without doubling back. Where the mapped track settles
    /// alongside (a yard track parallel a few metres over), it can't: there the curve eases over and back, as gently as
    /// the room allows. The frog falls wherever the two routes come apart. The last point it laid out and the frog, or null
    /// if no curve would do, where it's left as mapped.
    private static (int Number, int Last, float Frog)? Reshape(long node, Leg normal, Leg reverse, int side, HashSet<long> junctions, int? chosen,
        Dictionary<long, SwitchOptions>? options, float? byHand = null)
    {
        // As far as the diverging route can be moved: short of the next junction on it, and of its end.
        var limit = reverse.Length - EndClearance;
        var (nextSwitch, nextAt) = (false, 0f);
        foreach (var (other, point) in reverse.Line.Nodes)
        {
            var s = (reverse.Line.S[point] - reverse.Start) * reverse.Direction;
            if (s > 0.5f && junctions.Contains(other) && other != node && s - SwitchClearance < limit)
                (limit, nextSwitch, nextAt) = (s - SwitchClearance, true, s);
        }

        string Room() => normal.Length < MinReach
            ? $"the straight route ends {normal.Length:0} m past the points"
            : nextSwitch ? $"the next junction on the diverging track is only {nextAt:0} m past the points" : $"the diverging track ends {reverse.Length:0} m past the points";

        var fail = (string why) =>
        {
            if (options is not null)
                lock (options)
                    options[node] = new SwitchOptions(null, [], chosen, why);
            return ((int, int, float)?)null;
        };
        if (chosen == SwitchSizes.AsMapped)
        {
            if (options is not null)
                lock (options)
                    options[node] = new SwitchOptions(null, [], chosen);
            return null;
        }

        if (limit < MinReach)
            return fail(Room());

        var yard = (Routes.RailClass)reverse.Line.Class is Routes.RailClass.Yard or Routes.RailClass.Industrial ||
                   (Routes.RailClass)normal.Line.Class is Routes.RailClass.Yard or Routes.RailClass.Industrial;
        var target = (double)(chosen is { } size ? Turnout.RadiusOf(size) : yard ? YardRadius : MainRadius);

        var line = reverse.Line;
        var mapped = new List<(int Point, float S, double X, double Z)>();
        for (var k = 1; ; k++)
        {
            var p = reverse.Point + reverse.Direction * k;
            if (p < 0 || p >= line.Count)
                break;
            var along = (line.S[p] - reverse.Start) * reverse.Direction;
            mapped.Add((p, along, line.X[p], line.Z[p]));
            if (along > MaxReach + 20f)
                break;
        }

        (double X, double Z) HeadingAt(int i)
        {
            var (a, b) = (mapped[Math.Max(0, i - 2)], mapped[Math.Min(mapped.Count - 1, i + 2)]);
            var length = Math.Max(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)), 1e-9);
            return ((b.X - a.X) / length, (b.Z - a.Z) / length);
        }

        var start = (line.X[reverse.Point], line.Z[reverse.Point]);
        var leave = HeadingOf(normal);

        // Whether a curve to one of the mapped points stays near the mapped track.
        bool Near(List<TrackPiece> pieces, int to)
        {
            var total = Alignment.LengthOf(pieces);
            for (var i = 0; i < to; i++)
            {
                var (x, z) = Alignment.Position(pieces, total * mapped[i].S / mapped[to].S);
                if (TrackQuality.OffMapped(line, x, z, mapped[i].Point) > TrackQuality.MaxSwitchStray)
                    return false;
            }

            return true;
        }

        // The choices along the mapped track, nearest first: the first that bends one way only, then the first gentle
        // enough for each of the easier curves, then whichever is gentlest.
        int? single = null;
        var eased = new int?[EasedRadii.Length];
        var (gentlest, gentlestRadius) = (-1, 0.0);
        var candidates = new List<(int Index, double Radius)>();
        for (var i = 0; i < mapped.Count; i++)
        {
            var q = mapped[i];
            if (q.S < MinReach)
                continue;
            if (q.S > limit || q.S > MaxReach)
                break;
            var (ex, ez) = (q.X - start.Item1, q.Z - start.Item2);
            var chord = Math.Sqrt(ex * ex + ez * ez);
            var arrive = HeadingAt(i);
            if (chord < 1.0 || (ex * leave.X + ez * leave.Z) / chord < 0.5 || arrive.X * leave.X + arrive.Z * leave.Z < 0.5 ||
                Alignment.Join(start, leave, (q.X, q.Z), arrive) is not { } joined || !Near(joined.Pieces, i))
                continue;
            var radius = joined.Radius;
            candidates.Add((i, radius));
            if (radius > gentlestRadius)
                (gentlest, gentlestRadius) = (i, radius);
            if (radius >= target && single is null && joined.OneWay)
                single = i;
            for (var r = 0; r < EasedRadii.Length; r++)
                if (radius >= Math.Max(EasedRadii[r], target) && eased[r] is null)
                    eased[r] = i;
        }

        var fits = Turnout.Sizes.Where(z => z.Radius <= gentlestRadius).Select(z => z.Number).ToArray();

        // A curve's end set by hand goes where it was put, if there's still a curve to it.
        int? placed = null;
        if (byHand is { } wanted && mapped.Count > 0)
        {
            var at = Enumerable.Range(0, mapped.Count).Where(i => mapped[i].S >= MinReach && mapped[i].S <= limit).DefaultIfEmpty(-1).MinBy(i => i < 0 ? float.MaxValue : MathF.Abs(mapped[i].S - wanted));
            if (at >= 0 && Alignment.Join(start, leave, (mapped[at].X, mapped[at].Z), HeadingAt(at)) is { } handLaid && handLaid.Radius >= TightestLaid)
                placed = at;
        }

        if (placed is null && (gentlest < 0 || gentlestRadius < TightestLaid || (chosen is not null && gentlestRadius < target)))
            return fail(gentlest < 0 ? Room() : $"{Room()}, and no curve onto the mapped track is gentle enough");

        // The best of them whose routes come apart before the normal route ends (it may be short, ending at a junction),
        // or else any gentle enough that does, gentlest first.
        var order = new List<int>();
        if (placed is { } here)
            order.Add(here);
        if (single is { } one)
            order.Add(one);
        order.AddRange(eased.Where(e => e is not null).Select(e => e!.Value));
        if (gentlest >= 0)
            order.Add(gentlest);
        order.AddRange(candidates.Where(c => c.Radius >= Math.Max(TightestLaid, chosen is null ? 0.0 : target)).OrderByDescending(c => c.Radius).Select(c => c.Index));
        // Taken first among those whose routes part within a real switch's length: the gentlest curve's no good if it leaves two
        // tracks running together for a hundred metres. (A curve end set by hand goes where it was put.)
        foreach (var limitFrog in placed is null ? (float[])[LongestFrog, float.MaxValue] : [float.MaxValue])
            foreach (var join in order.Distinct())
            {
                var end = mapped[join];
                if (Alignment.Join(start, leave, (end.X, end.Z), HeadingAt(join)) is not { } curve)
                    continue;
                var length = Alignment.LengthOf(curve.Pieces);
                var laid = new List<(double X, double Z)> { start };
                for (var i = 0; i <= join; i++)
                    laid.Add(i == join ? (end.X, end.Z) : Alignment.Position(curve.Pieces, length * mapped[i].S / end.S));
                laid.AddRange(mapped.Skip(join + 1).Select(m => (m.X, m.Z)));
                if (FrogOf(normal, side, laid) is not { } frog || frog.S > limitFrog)
                    continue;

                if (options is not null)
                    lock (options)
                        options[node] = new SwitchOptions(frog.Number, fits, chosen, null, end.S, join == placed);
                if (reverse.Direction > 0)
                    line.Alignment?.Splice(reverse.Point, end.Point, curve.Pieces);
                else
                    line.Alignment?.Splice(end.Point, reverse.Point, Alignment.Reversed(curve.Pieces));
                if (line.Alignment is null)
                    for (var i = 0; i < join; i++)
                        (line.X[mapped[i].Point], line.Z[mapped[i].Point]) = laid[i + 1];
                else
                    line.Alignment.Sample(line, Math.Min(reverse.Point, end.Point), Math.Max(reverse.Point, end.Point));
                Tangent(node, reverse, leave, junctions, target);
                return (chosen ?? frog.Number, end.Point, frog.S);
            }

        return fail($"the straight route ends {normal.Length:0} m past the points, before the two routes part");
    }

    /// What a switch's curve would come to, ending this far along its diverging track.
    public sealed record CurveFit(List<TrackPiece> Pieces, double Radius, int Number, bool Fits);

    /// How far along its diverging track a curve can end nearest a place: past the shortest it can be, short of what's next.
    public static float ReachNear(Turnout turnout, double x, double z, Func<long, bool> junction)
    {
        var reverse = turnout.Reverse;
        var limit = MathF.Min(reverse.Length - EndClearance, MaxReach);
        foreach (var (other, point) in reverse.Line.Nodes)
        {
            var s = (reverse.Line.S[point] - reverse.Start) * reverse.Direction;
            if (s > 0.5f && other != turnout.Node && junction(other))
                limit = MathF.Min(limit, s - SwitchClearance);
        }

        var (best, bestAway) = (MinReach, double.MaxValue);
        for (var s = MinReach; s <= limit; s += 1f)
        {
            var (px, pz, _, _, _, _, _) = reverse.At(s);
            var away = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (away < bestAway)
                (best, bestAway) = (s, away);
        }

        return best;
    }

    /// The curve from a switch's points to this far along its diverging track: onto the track as it is past where the
    /// curve ends now, and onto the track as mapped if it's being drawn in nearer than that.
    public static CurveFit? CurveTo(Turnout turnout, float reach, float now)
    {
        var (normal, reverse) = (turnout.Normal, turnout.Reverse);
        var line = reverse.Line;
        var (xs, zs) = reach < now && line.MappedX.Length == line.Count ? (line.MappedX, line.MappedZ) : (line.X, line.Z);
        var target = reverse.LineS(reach);
        var found = Array.BinarySearch(line.S, target);
        var i = Math.Clamp(found >= 0 ? found : ~found, 2, Math.Max(line.Count - 3, 2));
        if (line.Count < 5)
            return null;
        var (ax, az, bx, bz) = (xs[i - 2], zs[i - 2], xs[i + 2], zs[i + 2]);
        var length = Math.Max(Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)), 1e-9);
        var arrive = ((bx - ax) / length * reverse.Direction, (bz - az) / length * reverse.Direction);
        if (Alignment.Join((line.X[reverse.Point], line.Z[reverse.Point]), HeadingOf(normal), (xs[i], zs[i]), arrive) is not { } join)
            return null;

        var total = Alignment.LengthOf(join.Pieces);
        var laid = new List<(double X, double Z)>();
        for (var d = 0.0; d <= total; d += 1.0)
            laid.Add(Alignment.Position(join.Pieces, d));
        for (var p = i; p >= 0 && p < line.Count && laid.Count < 400; p += reverse.Direction)
            laid.Add((xs[p], zs[p]));
        var frog = FrogOf(normal, turnout.Side, laid);
        return new CurveFit(join.Pieces, join.Radius, frog?.Number ?? 0, frog is not null && join.Radius >= TightestLaid);
    }

    /// Where the diverging route is the line coming in through the switch's toe, the line must arrive running the way it
    /// now leaves, or there's a kink at the points: its approach is laid in again as a curve, over the shortest stretch
    /// short of the junction before it that's gentle enough and stays near the mapped track.
    private static void Tangent(long node, Leg reverse, (double X, double Z) leave, HashSet<long> junctions, double target)
    {
        var line = reverse.Line;
        var back = -reverse.Direction;
        var at = reverse.Point;
        if (line.Alignment is null || at + back < 0 || at + back >= line.Count)
            return;
        var stop = line.Nodes.Where(n => junctions.Contains(n.Node) && n.Node != node && (n.Point - at) * back > 0).Select(n => (int?)n.Point).OrderBy(p => (p - at) * back).FirstOrDefault();
        var node2 = (line.X[at], line.Z[at]);
        (double X, double Z) Toward(int from, int to)
        {
            var (dx, dz) = (line.X[to] - line.X[from], line.Z[to] - line.Z[from]);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
            return (dx / length, dz / length);
        }

        var arriving = Toward(at + back * Math.Min(3, Math.Abs((stop ?? at + back * 3) - at)), at);
        if (Math.Abs(arriving.X * leave.Z - arriving.Z * leave.X) < TangentAngle)
            return;

        for (var p = at + back * 3; p >= 2 && p < line.Count - 2; p += back)
        {
            if ((stop is { } limit && (limit - p) * back <= 0) || MathF.Abs(line.S[p] - line.S[at]) > TangentReach)
                break;
            var start = (line.X[p], line.Z[p]);
            if (Alignment.Join(start, Toward(p + back * 2, p), node2, leave) is not { } curve || curve.Radius < target)
                continue;
            var total = Alignment.LengthOf(curve.Pieces);
            var (count, near) = (Math.Abs(p - at), true);
            for (var k = 1; k < count && near; k++)
            {
                var (x, z) = Alignment.Position(curve.Pieces, total * k / count);
                near = TrackQuality.OffMapped(line, x, z, p - back * k) <= TrackQuality.MaxStray;
            }

            if (!near)
                continue;
            if (back < 0)
                line.Alignment.Splice(p, at, curve.Pieces);
            else
                line.Alignment.Splice(at, p, Alignment.Reversed(curve.Pieces));
            line.Alignment.Sample(line, Math.Min(p, at), Math.Max(p, at));
            return;
        }
    }

    /// A reshaped line's distances along it and its extent, again.
    internal static void Measure(NetworkLine line)
    {
        line.S[0] = 0f;
        for (var i = 1; i < line.Count; i++)
            line.S[i] = line.S[i - 1] + (float)Math.Sqrt((line.X[i] - line.X[i - 1]) * (line.X[i] - line.X[i - 1]) + (line.Z[i] - line.Z[i - 1]) * (line.Z[i] - line.Z[i - 1]));
        (line.MinX, line.MaxX) = (line.X.Min(), line.X.Max());
        (line.MinZ, line.MaxZ) = (line.Z.Min(), line.Z.Max());
    }
}
