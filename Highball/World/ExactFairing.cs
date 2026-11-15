namespace Highball.World;

/// Takes the wobble out of track laid by hand (exact lines). Such a line is built as a Catmull-Rom spline through its control
/// points, which bends only as much as it must at each one but whose curvature jumps there, and which overshoots where the
/// points are unevenly spaced (a long straight span beside short ones round a curve): seen along the track, a little left
/// and right where there should be none. Here the spline's samples are pulled into the smoothest line through the very same
/// control points, the least change of curvature along it (a smoothing spline on the third differences), so it still
/// passes exactly through every control point and leaves each end the way it did, and curves come on gradually between them.
public static class ExactFairing
{
    /// Wobbles shorter than this are smoothed out between control points, metres.
    private const double Over = 100.0;

    /// Points of the track beyond each end held as well, so the line runs on into it.
    private const int Beyond = 4;

    /// The line's samples (2 m or so apart) faired, every control point's sample held. before, after: which way the track
    /// runs on past each end, away from the line (null: nothing carries on there, and the line ends straight along its
    /// first or last span). Points of that track stand beyond the end, so the line meets it running and curving as it
    /// does: without them, the line's curve would carry on right to its end, and turn there where the track it meets doesn't.
    public static void Fair(List<double> xs, List<double> zs, IEnumerable<int> held, (double X, double Z)? before = null, (double X, double Z)? after = null)
    {
        var count = xs.Count;
        if (count < 6)
            return;
        var length = 0.0;
        for (var i = 1; i < count; i++)
            length += Math.Sqrt((xs[i] - xs[i - 1]) * (xs[i] - xs[i - 1]) + (zs[i] - zs[i - 1]) * (zs[i] - zs[i - 1]));
        var step = length / (count - 1);
        if (step < 1e-6)
            return;

        static (double X, double Z) Away(double fromX, double fromZ, double toX, double toZ)
        {
            var (dx, dz) = (fromX - toX, fromZ - toZ);
            var d = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
            return (dx / d, dz / d);
        }

        // The samples with the track beyond each end before and after them.
        var (bx, bz) = before ?? Away(xs[0], zs[0], xs[1], zs[1]);
        var (ax, az) = after ?? Away(xs[^1], zs[^1], xs[^2], zs[^2]);
        var px = new List<double>();
        var pz = new List<double>();
        for (var k = Beyond; k >= 1; k--)
        {
            px.Add(xs[0] + bx * k * step);
            pz.Add(zs[0] + bz * k * step);
        }

        px.AddRange(xs);
        pz.AddRange(zs);
        for (var k = 1; k <= Beyond; k++)
        {
            px.Add(xs[^1] + ax * k * step);
            pz.Add(zs[^1] + az * k * step);
        }

        var m = px.Count;
        var isFixed = new bool[m];
        for (var k = 0; k <= Beyond; k++)
            (isFixed[k], isFixed[m - 1 - k]) = (true, true);
        foreach (var i in held)
            if (i >= 0 && i < count)
                isFixed[Beyond + i] = true;

        // The smoothest line through the control points: nothing but the least change of curvature decides where it runs
        // between them (the spline's samples only steady it, at a millionth of the weight). Pulling it towards the spline
        // kept the spline's wobble; towards the straights between the points flattened a gentle curve into chords.
        var lambda = Math.Pow(1.0 / (2.0 * Math.Sin(Math.PI / Math.Max(Over / step, 3.0))), 6.0);
        var weight = new double[m];
        for (var i = 0; i < m; i++)
            weight[i] = isFixed[i] ? 0.0 : 1e-6;
        var targetX = px.ToArray();
        var targetZ = pz.ToArray();

        if (Solve(m, lambda, weight, isFixed, targetX, targetZ) is not { } faired)
            return;
        for (var i = 0; i < count; i++)
            (xs[i], zs[i]) = faired[Beyond + i];
    }

    /// The smoothing as a banded system, both coordinates: weight (p - target)^2 + lambda (third difference)^2, the fixed
    /// points held. Null if it couldn't be solved.
    internal static List<(double X, double Z)>? Solve(int m, double lambda, double[] weight, bool[] isFixed, double[] targetX, double[] targetZ)
    {
        const int band = 3;
        // a[i, d]: the entry at (i, i + d), symmetric.
        var a = new double[m, band + 1];
        var bx = new double[m];
        var bz = new double[m];
        for (var i = 0; i < m; i++)
        {
            a[i, 0] = weight[i];
            bx[i] = weight[i] * targetX[i];
            bz[i] = weight[i] * targetZ[i];
        }

        ReadOnlySpan<double> c = [-1.0, 3.0, -3.0, 1.0];
        for (var j = 0; j + 3 < m; j++)
            for (var p = 0; p < 4; p++)
                for (var q = p; q < 4; q++)
                    a[j + p, q - p] += lambda * c[p] * c[q];

        double Get(int i, int k) => i <= k ? (k - i <= band ? a[i, k - i] : 0.0) : (i - k <= band ? a[k, i - k] : 0.0);
        void Set(int i, int k, double v)
        {
            if (i <= k)
                a[i, k - i] = v;
            else
                a[k, i - k] = v;
        }

        // Fixed points: their columns moved over to the right, their rows made to say what they are.
        for (var f = 0; f < m; f++)
        {
            if (!isFixed[f])
                continue;
            for (var r = Math.Max(0, f - band); r <= Math.Min(m - 1, f + band); r++)
            {
                if (r == f)
                    continue;
                var v = Get(r, f);
                bx[r] -= v * targetX[f];
                bz[r] -= v * targetZ[f];
                Set(r, f, 0.0);
            }

            a[f, 0] = 1.0;
            bx[f] = targetX[f];
            bz[f] = targetZ[f];
        }

        // Banded Cholesky: l[i, d] is L at (i, i - d).
        var l = new double[m, band + 1];
        for (var i = 0; i < m; i++)
        {
            for (var j = Math.Max(0, i - band); j <= i; j++)
            {
                var sum = Get(j, i);
                for (var k = Math.Max(0, i - band); k < j; k++)
                    if (j - k <= band)
                        sum -= l[i, i - k] * l[j, j - k];
                if (i == j)
                {
                    if (sum <= 1e-14)
                        return null;
                    l[i, 0] = Math.Sqrt(sum);
                }
                else
                    l[i, i - j] = sum / l[j, 0];
            }
        }

        double[] Back(double[] b)
        {
            var y = new double[m];
            for (var i = 0; i < m; i++)
            {
                var sum = b[i];
                for (var k = Math.Max(0, i - band); k < i; k++)
                    sum -= l[i, i - k] * y[k];
                y[i] = sum / l[i, 0];
            }

            var x = new double[m];
            for (var i = m - 1; i >= 0; i--)
            {
                var sum = y[i];
                for (var k = i + 1; k <= Math.Min(m - 1, i + band); k++)
                    sum -= l[k, k - i] * x[k];
                x[i] = sum / l[i, 0];
            }

            return x;
        }

        var xs = Back(bx);
        var zs = Back(bz);
        var result = new List<(double X, double Z)>(m);
        for (var i = 0; i < m; i++)
        {
            if (!double.IsFinite(xs[i]) || !double.IsFinite(zs[i]))
                return null;
            result.Add((xs[i], zs[i]));
        }

        return result;
    }
}
