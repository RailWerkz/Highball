namespace Highball.Builder;

/// The curve the Join tool lays between two track ends: a chain of easement spirals (clothoids), its curvature changing
/// steadily along each piece, that leaves one end along that end's heading and at its curvature and arrives at the
/// other the same way. Of all such chains it takes the gentlest: the least bending (curvature squared along it), the
/// smoothest change of curvature (so the easements run long and the middle settles into near-constant curve, as
/// spiral, arc, spiral), not much longer than it needs to be, and kept wider than a least radius where it can be.
///
/// Worked as a least-squares fit (Levenberg-Marquardt) over the length and the curvature at each knot, then pulled
/// exactly onto the far end. A cubic Bézier between the ends' headings starts it off, and is what's laid if the fit
/// can't close.
public static class JoinCurve
{
    /// A track end as the new curve meets it: where, which way the curve runs there (radians, atan2 of z over x), and
    /// its curvature (radians per metre, positive turning from +x towards +z).
    public readonly record struct Pose(double X, double Z, double Heading, double Curvature);

    /// The curve: its points from the first end to the second, how long it is, its tightest curvature, and whether the
    /// spiral fit closed (false: the Bézier fallback).
    public sealed record Fit(List<(double X, double Z)> Points, double Length, double MaxCurvature, bool Spiral);

    /// Knots along the curve: the spiral pieces are this many.
    private const int Pieces = 10;

    /// Integration steps per piece.
    private const int Sub = 6;

    // How much each wish counts (in units where the ends are one apart).
    private const double Smoothness = 0.02; // the change of curvature squared
    private const double LengthCost = 1.0;  // length (keeps it from looping out to bend less)
    private const double RadiusCost = 30.0; // curvature past the least radius
    private const double CloseCost = 300.0; // missing the far end, while fitting (then it's pulled on exactly)

    /// From a track's end inwards (back[0] the end): where it ends, which way it runs out of the end, and how it's curving
    /// there, from a parabola fitted to the points (curvature 0 with fewer than three).
    public static Pose? Estimate(IReadOnlyList<(double X, double Z)> back)
    {
        if (back.Count < 2)
            return null;
        var (x0, z0) = back[0];
        var (fx, fz) = back[^1];
        var chord = Math.Sqrt((x0 - fx) * (x0 - fx) + (z0 - fz) * (z0 - fz));
        if (chord < 1e-6)
            return null;

        // A frame at the end: t out along the chord, n to its left (+90 degrees in heading).
        var (tx, tz) = ((x0 - fx) / chord, (z0 - fz) / chord);
        var (nx, nz) = (-tz, tx);

        // b = c1 a + c2 a^2 through the end, by least squares.
        double s11 = 0, s12 = 0, s22 = 0, r1 = 0, r2 = 0;
        for (var i = 1; i < back.Count; i++)
        {
            var (dx, dz) = (back[i].X - x0, back[i].Z - z0);
            var a = dx * tx + dz * tz;
            var b = dx * nx + dz * nz;
            var a2 = a * a;
            s11 += a2;
            s12 += a2 * a;
            s22 += a2 * a2;
            r1 += a * b;
            r2 += a2 * b;
        }

        double c1, c2;
        var det = s11 * s22 - s12 * s12;
        if (back.Count >= 3 && Math.Abs(det) > 1e-9 * s11 * s22)
        {
            c1 = (r1 * s22 - r2 * s12) / det;
            c2 = (s11 * r2 - s12 * r1) / det;
        }
        else
        {
            c1 = s11 > 0 ? r1 / s11 : 0.0;
            c2 = 0.0;
        }

        var heading = Math.Atan2(tz, tx) + Math.Atan(c1);
        var curvature = 2.0 * c2 / Math.Pow(1.0 + c1 * c1, 1.5);
        return new Pose(x0, z0, heading, curvature);
    }

    /// The curve from a (leaving along its heading) to b (arriving along its heading). matchCurvature: start and end at
    /// the ends' curvatures, not just their headings. minRadius: kept wider than this where it can be (0: no least).
    /// step: about how far apart its points are.
    public static Fit? Solve(Pose a, Pose b, bool matchCurvature, double minRadius, double step)
    {
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var d = Math.Sqrt(dx * dx + dz * dz);
        if (d < 1.0)
            return null;

        // Worked in units of d, from a, turned so a heads along +x: the answer doesn't depend on where or how big.
        var (cos, sin) = (Math.Cos(-a.Heading), Math.Sin(-a.Heading));
        var bx = (dx * cos - dz * sin) / d;
        var bz = (dx * sin + dz * cos) / d;
        var turn = Wrap(b.Heading - a.Heading);
        var k0 = matchCurvature ? a.Curvature * d : double.NaN;
        var k1 = matchCurvature ? b.Curvature * d : double.NaN;
        var kMax = minRadius > 0 ? d / minRadius : double.PositiveInfinity;

        var guess = Bezier(bx, bz, turn);
        var solver = new Solver(bx, bz, turn, k0, k1, kMax);
        var v = solver.Start(guess.Length, guess.Curvature);
        var spiral = solver.Fit(v) is { } fitted && solver.Close(fitted) is { } closed ? closed : null;

        List<(double X, double Z)> local;
        double length, maxK;
        if (spiral is not null)
        {
            var (lengthUnit, ks) = solver.Unpack(spiral);
            length = lengthUnit * d;
            maxK = ks.Max(Math.Abs) / d;
            local = Trace(lengthUnit, ks, Math.Max(2, (int)Math.Ceiling(length / step)));
        }
        else
        {
            length = guess.Length * d;
            local = guess.Sample(Math.Max(2, (int)Math.Ceiling(length / step)));
            maxK = guess.MaxCurvature / d;
        }

        // Back to the world, the ends exactly where they are.
        var (wc, ws) = (Math.Cos(a.Heading), Math.Sin(a.Heading));
        var points = local.Select(p => (a.X + (p.X * wc - p.Z * ws) * d, a.Z + (p.X * ws + p.Z * wc) * d)).ToList();
        points[0] = (a.X, a.Z);
        points[^1] = (b.X, b.Z);
        return new Fit(points, length, maxK, spiral is not null);
    }

    /// A drawn path made to carry on smoothly from the track at either end: where it leaves a track (start) or arrives on
    /// one (end) at an angle to it, its first (or last) stretch, up to length metres or a third of it, is replaced by the
    /// gentlest curve from the track's pose to the path's own heading and curvature there. The ends stay where they are.
    /// start: heading into the path; end: heading arriving. Paths already running on from the track are left as they are.
    public static List<(double X, double Z)> EaseEnds(IReadOnlyList<(double X, double Z)> path, Pose? start, Pose? end, double length, double step)
    {
        var points = path.ToList();
        if (start is { } s && Ease(points, s, length, step) is { } eased)
            points = eased;
        if (end is { } e)
        {
            points.Reverse();
            var back = e with { Heading = e.Heading + Math.PI, Curvature = -e.Curvature };
            if (Ease(points, back, length, step) is { } easedBack)
                points = easedBack;
            points.Reverse();
        }

        return points;
    }

    /// The path's first stretch replaced by a curve from pose, if the path leaves at an angle to it; null if it doesn't.
    private static List<(double X, double Z)>? Ease(List<(double X, double Z)> points, Pose pose, double length, double step)
    {
        if (points.Count < 2)
            return null;
        var s = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
            s[i] = s[i - 1] + Math.Sqrt((points[i].X - points[i - 1].X) * (points[i].X - points[i - 1].X) + (points[i].Z - points[i - 1].Z) * (points[i].Z - points[i - 1].Z));
        var total = s[^1];
        var reach = Math.Min(length, total / 3.0);
        if (reach < 5.0)
            return null;

        (double X, double Z) At(double u)
        {
            var i = Math.Clamp(Array.BinarySearch(s, u) is var k && k >= 0 ? k : ~k - 1, 0, points.Count - 2);
            var f = s[i + 1] - s[i] < 1e-9 ? 0.0 : (u - s[i]) / (s[i + 1] - s[i]);
            return (points[i].X + (points[i + 1].X - points[i].X) * f, points[i].Z + (points[i + 1].Z - points[i].Z) * f);
        }

        // Already leaving along the track (within a tenth of a degree over its first few metres): nothing to do.
        var near = At(Math.Min(5.0, total));
        var leaves = Math.Atan2(near.Z - points[0].Z, near.X - points[0].X);
        if (Math.Abs(Wrap(leaves - pose.Heading)) < 0.1 * Math.PI / 180.0)
            return null;

        // The path's heading and curvature where the curve meets it, from a few points either side.
        var around = new List<(double X, double Z)>();
        for (var u = reach + 15.0; u >= reach - 0.01; u -= 5.0)
            around.Add(At(Math.Min(u, total)));
        around.Reverse();
        if (Estimate(around) is not { } meet)
            return null;
        // (Estimate reads which way the points run out of their first, back towards the start; the curve arrives going on.)
        var arrive = meet with { Heading = meet.Heading + Math.PI, Curvature = -meet.Curvature };
        if (Solve(pose with { X = points[0].X, Z = points[0].Z }, arrive, true, 0.0, step) is not { } fit)
            return null;

        var result = new List<(double X, double Z)>(fit.Points);
        for (var i = 0; i < points.Count; i++)
            if (s[i] > reach + 0.5)
                result.Add(points[i]);
        return result;
    }

    private static double Wrap(double angle)
    {
        angle %= 2.0 * Math.PI;
        if (angle > Math.PI)
            angle -= 2.0 * Math.PI;
        if (angle <= -Math.PI)
            angle += 2.0 * Math.PI;
        return angle;
    }

    // --- The spiral chain. ---

    /// Where a chain of pieces with these knot curvatures ends, over length l, from the origin heading +x.
    private static (double X, double Z, double Heading) End(double l, double[] k)
    {
        var h = l / (k.Length - 1);
        double x = 0, z = 0, th = 0;
        for (var j = 0; j + 1 < k.Length; j++)
        {
            var (ka, kb) = (k[j], k[j + 1]);
            var dk = (kb - ka) / h;
            var e = h / Sub;
            for (var m = 0; m < Sub; m++)
            {
                var s0 = m * e;
                double Th(double s) => th + ka * s + 0.5 * dk * s * s;
                var (t0, t1, t2) = (Th(s0), Th(s0 + 0.5 * e), Th(s0 + e));
                x += e / 6.0 * (Math.Cos(t0) + 4.0 * Math.Cos(t1) + Math.Cos(t2));
                z += e / 6.0 * (Math.Sin(t0) + 4.0 * Math.Sin(t1) + Math.Sin(t2));
            }

            th += 0.5 * (ka + kb) * h;
        }

        return (x, z, th);
    }

    /// The chain's points, count + 1 of them evenly along it.
    private static List<(double X, double Z)> Trace(double l, double[] k, int count)
    {
        var h = l / (k.Length - 1);
        var fine = count * 8;
        var e = l / fine;
        var points = new List<(double X, double Z)>(count + 1) { (0.0, 0.0) };
        double x = 0, z = 0;
        double Th(double s)
        {
            var j = Math.Min((int)(s / h), k.Length - 2);
            var before = 0.0;
            for (var i = 0; i < j; i++)
                before += 0.5 * (k[i] + k[i + 1]) * h;
            var u = s - j * h;
            return before + k[j] * u + 0.5 * (k[j + 1] - k[j]) / h * u * u;
        }

        for (var i = 0; i < fine; i++)
        {
            var s = i * e;
            var (t0, t1, t2) = (Th(s), Th(s + 0.5 * e), Th(s + e));
            x += e / 6.0 * (Math.Cos(t0) + 4.0 * Math.Cos(t1) + Math.Cos(t2));
            z += e / 6.0 * (Math.Sin(t0) + 4.0 * Math.Sin(t1) + Math.Sin(t2));
            if ((i + 1) % 8 == 0)
                points.Add((x, z));
        }

        return points;
    }

    private sealed class Solver(double bx, double bz, double turn, double k0, double k1, double kMax)
    {
        private bool FixedEnds => !double.IsNaN(k0);

        /// The unknowns: log of the length, then the knot curvatures that aren't fixed.
        public double[] Start(double length, Func<double, double> curvature)
        {
            var v = new List<double> { Math.Log(Math.Max(length, 0.05)) };
            for (var j = 0; j <= Pieces; j++)
                if (!FixedEnds || (j > 0 && j < Pieces))
                    v.Add(curvature((double)j / Pieces));
            return [.. v];
        }

        public (double Length, double[] K) Unpack(double[] v)
        {
            var k = new double[Pieces + 1];
            var i = 1;
            for (var j = 0; j <= Pieces; j++)
                k[j] = FixedEnds && j == 0 ? k0 : FixedEnds && j == Pieces ? k1 : v[i++];
            return (Math.Exp(v[0]), k);
        }

        private double[] Missing(double[] v)
        {
            var (l, k) = Unpack(v);
            var (x, z, th) = End(l, k);
            return [x - bx, z - bz, th - turn];
        }

        private double[] Residuals(double[] v)
        {
            var (l, k) = Unpack(v);
            var h = l / Pieces;
            var r = new List<double>();
            foreach (var m in Missing(v))
                r.Add(CloseCost * m);
            r.Add(Math.Sqrt(LengthCost * l));
            var sh = Math.Sqrt(h);
            for (var j = 0; j < Pieces; j++)
            {
                r.Add(sh * 0.5 * (k[j] + k[j + 1]));
                r.Add(Math.Sqrt(Smoothness) * (k[j + 1] - k[j]) / sh);
            }

            if (!double.IsInfinity(kMax))
                foreach (var kj in k)
                    r.Add(RadiusCost * sh * Math.Max(0.0, Math.Abs(kj) - kMax));
            return [.. r];
        }

        private static double[,] Jacobian(Func<double[], double[]> f, double[] v, double[] at)
        {
            var j = new double[at.Length, v.Length];
            for (var c = 0; c < v.Length; c++)
            {
                var step = 1e-6 * Math.Max(1.0, Math.Abs(v[c]));
                var w = (double[])v.Clone();
                w[c] += step;
                var moved = f(w);
                for (var r = 0; r < at.Length; r++)
                    j[r, c] = (moved[r] - at[r]) / step;
            }

            return j;
        }

        private static double Sum(double[] r) => r.Sum(x => x * x);

        /// Levenberg-Marquardt on the residuals.
        public double[]? Fit(double[] v)
        {
            var lambda = 1e-3;
            var r = Residuals(v);
            var cost = Sum(r);
            for (var iteration = 0; iteration < 200; iteration++)
            {
                var jac = Jacobian(Residuals, v, r);
                var n = v.Length;
                var a = new double[n, n];
                var g = new double[n];
                for (var p = 0; p < n; p++)
                {
                    for (var q = 0; q < n; q++)
                    {
                        var s = 0.0;
                        for (var i = 0; i < r.Length; i++)
                            s += jac[i, p] * jac[i, q];
                        a[p, q] = s;
                    }

                    for (var i = 0; i < r.Length; i++)
                        g[p] -= jac[i, p] * r[i];
                }

                var improved = false;
                for (var tries = 0; tries < 12 && !improved; tries++)
                {
                    var damped = (double[,])a.Clone();
                    for (var p = 0; p < n; p++)
                        damped[p, p] += lambda * (a[p, p] + 1e-9);
                    if (Linear(damped, g) is not { } delta)
                    {
                        lambda *= 10;
                        continue;
                    }

                    var w = v.Select((x, i) => x + delta[i]).ToArray();
                    w[0] = Math.Clamp(w[0], -3.0, 3.0); // between about a twentieth and twenty times the gap
                    var rw = Residuals(w);
                    var cw = Sum(rw);
                    if (double.IsFinite(cw) && cw < cost)
                    {
                        var gain = cost - cw;
                        (v, r, cost) = (w, rw, cw);
                        lambda = Math.Max(lambda / 3, 1e-9);
                        improved = true;
                        if (gain < 1e-12 * Math.Max(1.0, cost))
                            return v;
                    }
                    else
                        lambda *= 4;
                }

                if (!improved)
                    break;
            }

            return v.All(double.IsFinite) ? v : null;
        }

        /// Pulled exactly onto the far end: the smallest change that closes the gap, by Gauss-Newton on what's missing.
        public double[]? Close(double[] v)
        {
            for (var iteration = 0; iteration < 30; iteration++)
            {
                var m = Missing(v);
                if (Math.Sqrt(Sum(m)) < 1e-9)
                    return v;
                var jac = Jacobian(Missing, v, m);
                var n = v.Length;
                var jj = new double[3, 3];
                for (var p = 0; p < 3; p++)
                    for (var q = 0; q < 3; q++)
                    {
                        var s = 0.0;
                        for (var c = 0; c < n; c++)
                            s += jac[p, c] * jac[q, c];
                        jj[p, q] = s;
                    }

                if (Linear(jj, m) is not { } y)
                    return null;
                v = v.Select((x, c) => x - (jac[0, c] * y[0] + jac[1, c] * y[1] + jac[2, c] * y[2])).ToArray();
                if (!v.All(double.IsFinite))
                    return null;
            }

            return Math.Sqrt(Sum(Missing(v))) < 1e-6 ? v : null;
        }

        /// a x = b by elimination with partial pivoting; null when it's singular.
        private static double[]? Linear(double[,] a, double[] b)
        {
            var n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (var c = 0; c < n; c++)
            {
                var pivot = c;
                for (var r = c + 1; r < n; r++)
                    if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                        pivot = r;
                if (Math.Abs(m[pivot, c]) < 1e-14)
                    return null;
                if (pivot != c)
                {
                    for (var k = 0; k < n; k++)
                        (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
                    (x[c], x[pivot]) = (x[pivot], x[c]);
                }

                for (var r = c + 1; r < n; r++)
                {
                    var f = m[r, c] / m[c, c];
                    for (var k = c; k < n; k++)
                        m[r, k] -= f * m[c, k];
                    x[r] -= f * x[c];
                }
            }

            for (var c = n - 1; c >= 0; c--)
            {
                for (var k = c + 1; k < n; k++)
                    x[c] -= m[c, k] * x[k];
                x[c] /= m[c, c];
            }

            return x;
        }
    }

    // --- The Bézier start (and fallback). ---

    private sealed record Cubic(double P1X, double P1Z, double P2X, double P2Z, double P3X, double P3Z)
    {
        public (double X, double Z) At(double t)
        {
            var u = 1 - t;
            var (w1, w2, w3) = (3 * u * u * t, 3 * u * t * t, t * t * t);
            return (w1 * P1X + w2 * P2X + w3 * P3X, w1 * P1Z + w2 * P2Z + w3 * P3Z);
        }

        public double CurvatureAt(double t)
        {
            var u = 1 - t;
            var (d0, d1, d2) = (3 * u * u, 6 * u * t, 3 * t * t);
            var dx = d0 * P1X + d1 * (P2X - P1X) + d2 * (P3X - P2X);
            var dz = d0 * P1Z + d1 * (P2Z - P1Z) + d2 * (P3Z - P2Z);
            var ex = 6 * u * (P2X - 2 * P1X) + 6 * t * (P3X - 2 * P2X + P1X);
            var ez = 6 * u * (P2Z - 2 * P1Z) + 6 * t * (P3Z - 2 * P2Z + P1Z);
            var speed = Math.Pow(dx * dx + dz * dz, 1.5);
            return speed < 1e-12 ? 0.0 : (dx * ez - dz * ex) / speed;
        }
    }

    private sealed record BezierGuess(Cubic Curve, double Length, double MaxCurvature, Func<double, double> Curvature)
    {
        public List<(double X, double Z)> Sample(int count) => [.. Enumerable.Range(0, count + 1).Select(i => Curve.At((double)i / count))];
    }

    /// From the origin heading +x to (bx, bz) arriving heading turn, handles a third of the gap long.
    private static BezierGuess Bezier(double bx, double bz, double turn)
    {
        const double handle = 1.0 / 3.0;
        var c = new Cubic(handle, 0.0, bx - Math.Cos(turn) * handle, bz - Math.Sin(turn) * handle, bx, bz);
        var length = 0.0;
        var last = (X: 0.0, Z: 0.0);
        var maxK = 0.0;
        for (var i = 1; i <= 64; i++)
        {
            var p = c.At(i / 64.0);
            length += Math.Sqrt((p.X - last.X) * (p.X - last.X) + (p.Z - last.Z) * (p.Z - last.Z));
            last = p;
            maxK = Math.Max(maxK, Math.Abs(c.CurvatureAt(i / 64.0)));
        }

        return new BezierGuess(c, length, maxK, c.CurvatureAt);
    }
}
