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
    public required float Y { get; init; }
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

        return result;
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
            return "Diamond too shallow (under 8 degrees): needs a moveable-point crossing, no template yet";

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

        return clash ?? "";
    }
}
