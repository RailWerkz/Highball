namespace Highball.World;

/// A diamond: two tracks crossing, each as a run through the crossing with s = 0 at their shared node. Everything in it is worked
/// out from those two tracks as they really run, curves and all, so it can't help but meet them (NetworkMeshes.Diamonds.cs).
public sealed class CrossingAssembly
{
    /// A track through the crossing: Out the way s runs, Back the other. To the right is positive; a track's right turns with it.
    public readonly record struct Track(Leg Out, Leg Back)
    {
        public (double X, double Z, float Y, float Fx, float Fz, float Rx, float Rz) At(float s)
        {
            if (s >= 0f)
                return Out.At(s);
            var (x, z, y, fx, fz, rx, rz) = Back.At(-s);
            return (x, z, y, -fx, -fz, -rx, -rz);
        }

        public (double X, double Z) On(float s, float off)
        {
            var (x, z, _, _, _, rx, rz) = At(s);
            return (x + rx * off, z + rz * off);
        }
    }

    public required Track[] Tracks { get; init; }

    /// How far each track's assembly runs either way, in its s.
    public required (float From, float To)[] Range { get; init; }

    /// The middle: where the two centrelines cross, and the height the castings lie at.
    public required double X { get; init; }
    public required double Z { get; init; }
    public required float Y { get; set; }
    public required float Degrees { get; init; }

    /// How far a place is off a track, to its right, and where along it, starting the search from s.
    public static float Off(Track track, double x, double z, ref float s)
    {
        for (var k = 0; k < 3; k++)
        {
            var (px, pz, _, fx, fz, _, _) = track.At(s);
            s += (float)((x - px) * fx + (z - pz) * fz);
        }

        var (qx, qz, _, _, _, rx, rz) = track.At(s);
        return (float)((x - qx) * rx + (z - qz) * rz);
    }

    /// Where along a track, off to its right, the other track's offset comes to otherOff; start: where to begin looking.
    public static float Along(Track track, Track other, float off, float otherOff, float start, float otherStart)
    {
        var (s, t) = (start, otherStart);
        for (var k = 0; k < 8; k++)
        {
            var (x, z) = track.On(s, off);
            var error = Off(other, x, z, ref t) - otherOff;
            var (_, _, _, fx, fz, _, _) = track.At(s);
            var (_, _, _, _, _, rx, rz) = other.At(t);
            var slope = fx * rx + fz * rz;
            if (MathF.Abs(slope) < 0.05f)
                slope = MathF.Sign(slope + 1e-6f) * 0.05f;
            s -= error / slope;
        }

        return s;
    }

    /// The place off to each track's right by these.
    public (double X, double Z) Place(float offA, float offB)
    {
        var s = Along(Tracks[0], Tracks[1], offA, offB, Middle[0], Middle[1]);
        return Tracks[0].On(s, offA);
    }

    /// Where each track is at the middle.
    public required float[] Middle { get; init; }

    /// Each track's ends that run on into the next diamond along it (double track crossed): its guard rails and bars carry
    /// straight on there instead of flaring off.
    public (bool From, bool To)[] Joined { get; } = new (bool, bool)[2];
}

/// Finds the diamonds: two tracks crossing at a node with no way from one to the other. Each becomes an assembly built to the
/// tracks themselves, whatever angle they make and however they curve: the game's own rails stop at its ends, and where the two
/// tracks' ties would pass through one another, only the first track's are laid.
public static class Diamonds
{
    /// How far along each track from the node the crossing is looked for.
    private const float Reach = 20f;

    private const float TieLength = 2.5908f;

    /// How far an assembly's rails run on past its last rail crossing.
    public const float Tail = 1.2f;

    /// Builds the diamond layouts it can; returns the layouts with those no longer needing fixing.
    public static List<Layout> Fit(NetworkLines lines, List<Layout> layouts)
    {
        lines.CrossingAssemblies.Clear();
        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines.Lines.Where(l => l.Rail && !l.Disused))
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }

        var result = new List<Layout>(layouts.Count);
        foreach (var layout in layouts)
        {
            if (layout.Kind != JunctionKind.Diamond || !uses.TryGetValue(layout.Nodes[0], out var at) || Build(at, lines, layout.Nodes[0]) is not { } why)
            {
                result.Add(layout);
                continue;
            }

            result.Add(layout with { Issue = why.Length == 0 ? null : why });
        }

        JoinNeighbours(lines);
        Level(lines);
        return result;
    }

    /// How far each track eases from a diamond's plane back onto its own grade.
    private const float LevelEase = 15f;

    /// A diamond's rails all lie in one plane, the two tracks' crowns meeting flush; each line's profile is fitted only for itself, so
    /// two crossing tracks on their own grades meet at their shared node and part by centimetres either side of it, one's rails standing
    /// proud of the other's. So every diamond (and diamonds run into one another, one track over a double track) is laid level: each
    /// track's rails lie flat through the assembly, and its profile eases back to its own grade over LevelEase beyond.
    /// (Before the ground's shaped round the track: its bed and the ground follow.)
    private static void Level(NetworkLines lines)
    {
        var all = lines.CrossingAssemblies;
        if (all.Count == 0)
            return;

        // Diamonds that share a track close by, as one.
        var group = Enumerable.Range(0, all.Count).ToArray();
        int Root(int i) => group[i] == i ? i : group[i] = Root(group[i]);
        for (var a = 0; a < all.Count; a++)
            for (var b = a + 1; b < all.Count; b++)
            {
                var shared = all[a].Tracks.Any(t => all[b].Tracks.Any(u => ReferenceEquals(t.Out.Line, u.Out.Line)));
                var (dx, dz) = (all[a].X - all[b].X, all[a].Z - all[b].Z);
                if (shared && dx * dx + dz * dz < 20.0 * 20.0)
                    group[Root(a)] = Root(b);
            }

        // Each line's best claim to a level, point by point (the strongest, where two diamonds' easings overlap), from the profiles as fitted.
        var claims = new Dictionary<NetworkLine, (float Weight, float Y)[]>();
        foreach (var members in Enumerable.Range(0, all.Count).GroupBy(Root).Select(g => g.Select(i => all[i]).ToList()))
        {
            // The plane: level, at the tracks' mean height through it. (A track's rails lie level across it, so only a level plane has
            // all four rails meet the other track's flush: tilted, every track but one along its fall would stand a rail proud.)
            var (sum, n) = (0.0, 0);
            foreach (var c in members)
                for (var i = 0; i < 2; i++)
                    for (var t = c.Range[i].From; t <= c.Range[i].To; t += 0.5f)
                    {
                        sum += c.Tracks[i].At(t).Y;
                        n++;
                    }

            if (n == 0)
                continue;
            var level = (float)(sum / n);
            foreach (var c in members)
                c.Y = level;

            // Each line's stretch under the group's assemblies, and its easing either side.
            var spans = new Dictionary<NetworkLine, (float Lo, float Hi)>();
            foreach (var c in members)
                for (var i = 0; i < 2; i++)
                {
                    var track = c.Tracks[i];
                    var (a, b) = (track.Out.LineS(c.Range[i].To), track.Back.LineS(-c.Range[i].From));
                    var line = track.Out.Line;
                    var (lo, hi) = (MathF.Min(a, b), MathF.Max(a, b));
                    spans[line] = spans.TryGetValue(line, out var was) ? (MathF.Min(was.Lo, lo), MathF.Max(was.Hi, hi)) : (lo, hi);
                }

            foreach (var (line, (lo, hi)) in spans)
            {
                if (!claims.TryGetValue(line, out var claim))
                    claims[line] = claim = new (float, float)[line.Count];
                for (var p = 0; p < line.Count; p++)
                {
                    var away = MathF.Max(lo - line.S[p], line.S[p] - hi);
                    if (away >= LevelEase)
                        continue;
                    var t = Math.Clamp(1f - away / LevelEase, 0f, 1f);
                    var weight = t * t * (3f - 2f * t);
                    if (weight > claim[p].Weight)
                        claim[p] = (weight, level);
                }
            }
        }

        foreach (var (line, claim) in claims)
            for (var p = 0; p < line.Count; p++)
                if (claim[p].Weight > 0f)
                    line.Y[p] += (claim[p].Y - line.Y[p]) * claim[p].Weight;
    }


    /// How far apart two diamonds' ends along the same track can be and be joined into one run.
    private const float JoinGap = 3f;

    /// Diamonds close along the same track (one track crossing a double track) run into one another: each one's end there is
    /// taken to the midpoint between them, so their rails, guard rails and bars meet rather than each flaring off into the other.
    private static void JoinNeighbours(NetworkLines lines)
    {
        var ends = new List<(CrossingAssembly C, int Track, bool To, Leg Leg, float End, float Middle)>();
        foreach (var c in lines.CrossingAssemblies)
            for (var i = 0; i < 2; i++)
            {
                var (out_, back) = (c.Tracks[i].Out, c.Tracks[i].Back);
                ends.Add((c, i, true, out_, out_.LineS(c.Range[i].To), out_.LineS(0f)));
                ends.Add((c, i, false, back, back.LineS(-c.Range[i].From), back.LineS(0f)));
            }

        for (var a = 0; a < ends.Count; a++)
            for (var b = a + 1; b < ends.Count; b++)
            {
                var (p, q) = (ends[a], ends[b]);
                if (ReferenceEquals(p.C, q.C) || !ReferenceEquals(p.Leg.Line, q.Leg.Line))
                    continue;
                // Facing one another: each end on the side of its middle the other is.
                var way = MathF.Sign(q.Middle - p.Middle);
                if (way == 0f || MathF.Sign(p.End - p.Middle) != way || MathF.Sign(q.End - q.Middle) != -way)
                    continue;
                var gap = (q.End - p.End) * way;
                if (gap > JoinGap || gap < -JoinGap)
                    continue;

                var meet = (p.End + q.End) * 0.5f;
                foreach (var e in new[] { p, q })
                {
                    var local = (meet - e.Leg.Start) * e.Leg.Direction;
                    if (e.To)
                    {
                        e.C.Range[e.Track].To = local;
                        e.C.Joined[e.Track].To = true;
                    }
                    else
                    {
                        e.C.Range[e.Track].From = -local;
                        e.C.Joined[e.Track].From = true;
                    }
                }

                foreach (var side in (int[])[-1, 1])
                    p.Leg.Line.RailCuts.Add((MathF.Min(p.End, q.End), MathF.Max(p.End, q.End), side));
            }
    }

    /// Builds one diamond. Returns "" when built, or why it couldn't be or has a problem (null: not two tracks crossing).
    private static string? Build(List<(NetworkLine Line, int Point)> at, NetworkLines lines, long node)
    {
        var legs = new List<Leg>();
        foreach (var (line, point) in at)
        {
            if (point > 0)
                legs.Add(new Leg(line, point, -1));
            if (point < line.Count - 1)
                legs.Add(new Leg(line, point, 1));
        }

        if (legs.Count != 4)
            return null;

        // The two tracks: the legs in opposite pairs.
        var h = legs.Select(Turnout.Heading).ToArray();
        float Dot(int a, int b) => h[a].X * h[b].X + h[a].Z * h[b].Z;
        var pairs = new[] { (0, 1, 2, 3), (0, 2, 1, 3), (0, 3, 1, 2) }.MinBy(p => Dot(p.Item1, p.Item2) + Dot(p.Item3, p.Item4));
        Leg[] first = [legs[pairs.Item1], legs[pairs.Item2]];
        Leg[] second = [legs[pairs.Item3], legs[pairs.Item4]];
        if (first.Concat(second).Any(l => l.Line.Tracks != 1 || l.Line.Embedded))
            return "Diamond on multiple-track or street track: no template yet";

        var angle = MathF.Acos(Math.Clamp(MathF.Abs(h[pairs.Item1].X * h[pairs.Item3].X + h[pairs.Item1].Z * h[pairs.Item3].Z), 0f, 1f));
        if (angle < 8f * MathF.PI / 180f)
            return "Diamond under 8 degrees: no template yet";

        CrossingAssembly.Track[] tracks = [new(first[0], first[1]), new(second[0], second[1])];

        // The middle: where the two centrelines truly cross, which needn't be quite the node.
        var middleA = CrossingAssembly.Along(tracks[0], tracks[1], 0f, 0f, 0f, 0f);
        var (mx, mz) = tracks[0].On(middleA, 0f);
        var middleB = 0f;
        CrossingAssembly.Off(tracks[1], mx, mz, ref middleB);

        float[] middle = [middleA, middleB];
        var g = Turnout.RailOffset;
        var range = new (float From, float To)[2];
        for (var i = 0; i < 2; i++)
        {
            var (low, high) = (float.MaxValue, float.MinValue);
            foreach (var off in (float[])[-g, g])
                foreach (var other in (float[])[-g, g])
                {
                    var s = CrossingAssembly.Along(tracks[i], tracks[1 - i], off, other, middle[i], middle[1 - i]);
                    (low, high) = (MathF.Min(low, s), MathF.Max(high, s));
                }

            range[i] = (MathF.Max(low - Tail, -tracks[i].Back.Length), MathF.Min(high + Tail, tracks[i].Out.Length));
        }

        var (_, _, y, _, _, _, _) = tracks[0].At(middleA);
        lines.CrossingAssemblies.Add(new CrossingAssembly { Tracks = tracks, Range = range, X = mx, Z = mz, Y = y, Degrees = angle * 180f / MathF.PI, Middle = middle });

        // Its rails take over from the lines'.
        string? clash = null;
        for (var i = 0; i < 2; i++)
        {
            foreach (var (leg, reach) in new[] { (tracks[i].Out, range[i].To), (tracks[i].Back, -range[i].From) })
            {
                if (reach <= 0f)
                    continue;
                var (a, b) = (leg.LineS(0f), leg.LineS(reach));
                foreach (var side in (int[])[-1, 1])
                    leg.Line.RailCuts.Add((MathF.Min(a, b), MathF.Max(a, b), side));
                foreach (var (other, point) in leg.Line.Nodes)
                {
                    var s = (leg.Line.S[point] - leg.Start) * leg.Direction;
                    if (other != node && s > 0.5f && s < reach && lines.IsJunction(other))
                        clash = "Another junction inside the diamond";
                }
            }
        }

        // The second track's ties give way where they'd pass through the first track's.
        foreach (var leg in second)
        {
            float? from = null, to = null;
            for (var s = 0f; s <= MathF.Min(Reach + MathF.Abs(range[1].To), leg.Length); s += 0.25f)
            {
                var (x, z, _, _, _, _, _) = leg.At(s);
                var near = first.Any(f => Turnout.Offset(f, x, z) is { } off && MathF.Abs(off.Offset) < TieLength * 0.5f + 0.3f && off.S < Reach + MathF.Abs(range[0].To));
                if (!near)
                    continue;
                var ls = leg.LineS(s);
                (from, to) = (MathF.Min(from ?? ls, ls), MathF.Max(to ?? ls, ls));
            }

            if (from is { } a2 && to is { } b2)
                leg.Line.TieCuts.Add((a2 - 0.3f, b2 + 0.3f));
        }

        // Level through the crossing (Level), the two beds' tops would lie in one plane and fight where they overlap: one lies just under
        // the other's wherever its top reaches it.
        // (Always the line later in the list: where one track crosses a double track, each pair agrees which bed's on top.)
        var (over, below) = first[0].Line.Index < second[0].Line.Index ? (first, second) : (second, first);
        var reachTop = first[0].Line.BallastHalfTop + second[0].Line.BallastHalfTop + 0.3f;
        var run = reachTop / MathF.Max(MathF.Sin(angle), 0.14f) + 1f;
        foreach (var leg in below)
        {
            float? from = null, to = null;
            for (var s = 0f; s <= MathF.Min(run, leg.Length); s += 0.25f)
            {
                var (x, z, _, _, _, _, _) = leg.At(s);
                if (!over.Any(f => Turnout.Offset(f, x, z) is { } off && MathF.Abs(off.Offset) < reachTop && off.S < run))
                    continue;
                var ls = leg.LineS(s);
                (from, to) = (MathF.Min(from ?? ls, ls), MathF.Max(to ?? ls, ls));
            }

            if (from is { } a3 && to is { } b3)
                leg.Line.BedDrops.Add((a3, b3));
        }

        return clash ?? "";
    }
}
