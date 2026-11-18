using System.Globalization;

namespace Highball.World;

/// A corner of track laid by straights and curves: where the straights either side of it would meet (the point of
/// intersection, off the track itself), the radius of the curve round it, and the length of the easement (a clothoid
/// spiral, curvature growing evenly from nothing) on each side of that curve.
public readonly record struct Corner(double X, double Z, double Radius, double Easing);

/// Track laid as real track is designed: long straights, and at each corner between two of them a circular curve with an
/// easement spiral either side. A way of this kind keeps its start, its corners and its end as its nodes and the radius
/// and easement of each corner in a tag, and is built from them exactly: a straight is exactly straight, a curve exactly
/// its radius, and each piece runs on from the last with no kink, as no spline through points can promise.
public static class AlignmentTrack
{
    /// The tag a way laid this way carries: each corner's radius and easement in metres, "r,e;r,e;...", in node order.
    public const string Tag = "highball:alignment";

    /// The usual easement for a curve of a radius, metres: longer for gentler curves, which are run faster.
    public static double DefaultEasing(double radius) => Math.Clamp(20.0 + radius * 0.06, 20.0, 120.0);

    public static string Encode(IEnumerable<Corner> corners) =>
        string.Join(";", corners.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Radius:0.###},{c.Easing:0.###}")));

    /// Each corner's radius and easement from the tag; null if it doesn't read.
    public static List<(double Radius, double Easing)>? Decode(string? tag)
    {
        if (tag is null)
            return null;
        var list = new List<(double, double)>();
        if (tag.Length == 0)
            return list;
        foreach (var part in tag.Split(';'))
        {
            var bits = part.Split(',');
            if (bits.Length != 2 || !double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ||
                !double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
                return null;
            list.Add((r, e));
        }

        return list;
    }

    /// The track built: its points every couple of metres, the leg (0: start to the first corner) each lies along, the point
    /// at the middle of each corner's curve, its tightest radius, and whether a curve was made tighter to fit.
    public sealed record Geometry(List<(double X, double Z)> Points, List<int> Legs, List<int> Middles, double MinRadius, bool Tightened);

    /// What a corner's curve takes up: the distance from the corner back along each straight to where the easement begins,
    /// the easement and arc lengths, and the turn (radians, positive towards +z from +x).
    public readonly record struct Turn(double Tangent, double Easing, double Arc, double Angle, double Radius);

    /// The curve round a corner of turn angle (radians, signed) with this radius and easement.
    public static Turn CurveAt(double angle, double radius, double easing)
    {
        var turn = Math.Abs(angle);
        if (turn < 1e-9 || radius <= 0.0)
            return new Turn(0.0, 0.0, 0.0, angle, radius);
        // The two easements together can turn the whole corner at most: then there's no arc between them.
        easing = Math.Max(0.0, Math.Min(easing, radius * turn));
        var spiralTurn = easing / (2.0 * radius);
        var (sx, sy) = SpiralEnd(easing, radius);
        var shift = sy - radius * (1.0 - Math.Cos(spiralTurn));
        var along = sx - radius * Math.Sin(spiralTurn);
        var tangent = (radius + shift) * Math.Tan(turn / 2.0) + along;
        return new Turn(tangent, easing, radius * (turn - 2.0 * spiralTurn), angle, radius);
    }

    /// Where a clothoid of this length ends, run from the origin along +x, curving up to 1/radius (+y side).
    private static (double X, double Y) SpiralEnd(double length, double radius)
    {
        if (length <= 0.0)
            return (0.0, 0.0);
        const int steps = 64;
        var h = length / steps;
        double x = 0, y = 0;
        double Theta(double s) => s * s / (2.0 * radius * length);
        for (var i = 0; i < steps; i++)
        {
            var (a, b, c) = (Theta(i * h), Theta((i + 0.5) * h), Theta((i + 1) * h));
            x += h / 6.0 * (Math.Cos(a) + 4.0 * Math.Cos(b) + Math.Cos(c));
            y += h / 6.0 * (Math.Sin(a) + 4.0 * Math.Sin(b) + Math.Sin(c));
        }

        return (x, y);
    }

    /// The signed turn at a corner from the straight coming in to the one going out.
    private static double Angle((double X, double Z) a, (double X, double Z) corner, (double X, double Z) b)
    {
        var turn = Math.Atan2(b.Z - corner.Z, b.X - corner.X) - Math.Atan2(corner.Z - a.Z, corner.X - a.X);
        return Math.Atan2(Math.Sin(turn), Math.Cos(turn));
    }

    /// The curves at the corners, each made tighter (radius and easement together) where it and the next wouldn't both fit on
    /// the straight between them. Tightened says whether any was.
    public static (Turn[] Turns, bool Tightened) Fit((double X, double Z) start, IReadOnlyList<Corner> corners, (double X, double Z) end)
    {
        var n = corners.Count;
        var at = new List<(double X, double Z)> { start };
        at.AddRange(corners.Select(c => (c.X, c.Z)));
        at.Add(end);
        var scale = Enumerable.Repeat(1.0, n).ToArray();
        var turns = new Turn[n];
        var tightened = false;
        for (var round = 0; round < 40; round++)
        {
            for (var i = 0; i < n; i++)
                turns[i] = CurveAt(Angle(at[i], at[i + 1], at[i + 2]), corners[i].Radius * scale[i], corners[i].Easing * scale[i]);

            var fits = true;
            for (var leg = 0; leg <= n; leg++)
            {
                var length = Math.Sqrt((at[leg + 1].X - at[leg].X) * (at[leg + 1].X - at[leg].X) + (at[leg + 1].Z - at[leg].Z) * (at[leg + 1].Z - at[leg].Z));
                var need = (leg > 0 ? turns[leg - 1].Tangent : 0.0) + (leg < n ? turns[leg].Tangent : 0.0);
                if (need <= length + 1e-6)
                    continue;
                fits = false;
                var f = Math.Max(0.05, length / need) * 0.999;
                if (leg > 0)
                    scale[leg - 1] *= f;
                if (leg < n)
                    scale[leg] *= f;
            }

            if (fits)
                break;
            tightened = true;
        }

        return (turns, tightened);
    }

    /// How far the last Build's integrated curves ended from where they exactly should (metres): a check, should be ~0.
    public static double LastClosure { get; private set; }

    /// The track from start through the corners to end, a point about every spacing metres.
    public static Geometry? Build((double X, double Z) start, IReadOnlyList<Corner> corners, (double X, double Z) end, double spacing = 2.0)
    {
        var n = corners.Count;
        var at = new List<(double X, double Z)> { start };
        at.AddRange(corners.Select(c => (c.X, c.Z)));
        at.Add(end);
        for (var i = 0; i + 1 < at.Count; i++)
            if (Math.Abs(at[i + 1].X - at[i].X) + Math.Abs(at[i + 1].Z - at[i].Z) < 1e-6)
                return null;

        var (turns, tightened) = Fit(start, corners, end);
        LastClosure = 0.0;
        (double X, double Z) Dir(int leg)
        {
            var (dx, dz) = (at[leg + 1].X - at[leg].X, at[leg + 1].Z - at[leg].Z);
            var d = Math.Sqrt(dx * dx + dz * dz);
            return (dx / d, dz / d);
        }

        var points = new List<(double X, double Z)> { start };
        var legs = new List<int> { 0 };
        var middles = new List<int>();
        var minRadius = double.PositiveInfinity;
        var since = 0.0; // distance run since the last point put down

        void Emit(double x, double z, int leg)
        {
            points.Add((x, z));
            legs.Add(leg);
        }

        // A straight from p to q along a leg: points every spacing, carrying on the count from before it.
        void Straight((double X, double Z) p, (double X, double Z) q, int leg)
        {
            var (dx, dz) = (q.X - p.X, q.Z - p.Z);
            var length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 1e-9)
                return;
            var u = spacing - since;
            while (u < length - 1e-9)
            {
                Emit(p.X + dx * u / length, p.Z + dz * u / length, leg);
                u += spacing;
            }

            since = length - (u - spacing);
        }

        var from = start;
        for (var i = 0; i < n; i++)
        {
            var t = turns[i];
            var (ix, iz) = Dir(i);
            var (ox, oz) = Dir(i + 1);
            var corner = at[i + 1];
            if (t.Tangent < 1e-9)
            {
                middles.Add(points.Count - 1);
                continue;
            }
            var ts = (corner.X - ix * t.Tangent, corner.Z - iz * t.Tangent);
            Straight(from, ts, i);

            // Easement in, arc, easement out, by curvature along it, integrated from where it begins.
            var sign = Math.Sign(t.Angle);
            var total = 2.0 * t.Easing + t.Arc;
            double Curvature(double s) => sign * (s < t.Easing ? s / (t.Radius * t.Easing)
                : s < t.Easing + t.Arc ? 1.0 / t.Radius
                : (total - s) / (t.Radius * t.Easing));
            if (t.Radius > 0)
                minRadius = Math.Min(minRadius, t.Radius);

            var (x, z) = ts;
            var heading = Math.Atan2(iz, ix);
            const double step = 0.25;
            var s0 = 0.0;
            var middleDone = false;
            while (s0 < total - 1e-9)
            {
                var h = Math.Min(step, total - s0);
                var (k0, k1, k2) = (Curvature(s0), Curvature(s0 + h * 0.5), Curvature(s0 + h));
                var hMid = heading + h * 0.5 * (k0 + k1) * 0.5;
                var hEnd = heading + h * (k0 + 4.0 * k1 + k2) / 6.0;
                x += h / 6.0 * (Math.Cos(heading) + 4.0 * Math.Cos(hMid) + Math.Cos(hEnd));
                z += h / 6.0 * (Math.Sin(heading) + 4.0 * Math.Sin(hMid) + Math.Sin(hEnd));
                heading = hEnd;
                s0 += h;
                since += h;
                if (since >= spacing - 1e-9)
                {
                    Emit(x, z, s0 < total * 0.5 ? i : i + 1);
                    since = 0.0;
                }

                if (!middleDone && s0 >= total * 0.5)
                {
                    middles.Add(points.Count - 1);
                    middleDone = true;
                }
            }

            if (!middleDone)
                middles.Add(points.Count - 1);
            LastClosure = Math.Max(LastClosure, Math.Sqrt((x - corner.X - ox * t.Tangent) * (x - corner.X - ox * t.Tangent) + (z - corner.Z - oz * t.Tangent) * (z - corner.Z - oz * t.Tangent)));
            // (Where the curve ends, exactly: on the next straight, its tangent length past the corner.)
            from = (corner.X + ox * t.Tangent, corner.Z + oz * t.Tangent);
        }

        Straight(from, end, n);
        if (since < spacing * 0.3 && points.Count > 1)
        {
            points.RemoveAt(points.Count - 1);
            legs.RemoveAt(legs.Count - 1);
        }

        Emit(end.X, end.Z, n);
        // A corner whose curve was nothing (straight on) still has a middle: the nearest point to it.
        while (middles.Count < n)
            middles.Add(points.Count / 2);
        return new Geometry(points, legs, middles, minRadius, tightened);
    }
}
