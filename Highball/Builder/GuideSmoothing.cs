using Highball.World;

namespace Highball.Builder;

/// Smooths a path traced along a guide before it's laid. The mapped guides were clicked by hand over aerial photos, so they
/// wander a metre or more either way and kink at every click; fitting straights and arcs within a metre of them keeps much of
/// that. Here the path is taken every couple of metres and pulled into the smoothest path that stays near it: the least
/// change of curvature along it (a smoothing spline on the third differences), so wobbles shorter than Over metres go,
/// curves keep their radius, and the curvature eases on and off as it would through an easement spiral. Where it would stray
/// more than MaxOffset from the guide, the guide holds it there more strongly until it doesn't.
///
/// Each end is held where it is, and an end joining a track carries on from it: a few points of that track's own line (from
/// its heading and curvature there) stand before it, so the smoothing runs on through the join without a kink.
public static class GuideSmoothing
{
    /// How far apart the path is taken, metres.
    public const double Spacing = 2.0;

    /// Points of a joined track's line before each end.
    private const int Ghosts = 3;

    /// The smoothed path, the most it strays from the guide anywhere, and the guide as it was taken (to show).
    public sealed record Result(List<(double X, double Z)> Points, double MaxOffset);

    /// path: the guide path, first click to last. over: wobbles shorter than this many metres are smoothed out.
    /// maxOffset: held within about this of the guide (0: as near as the smoothing puts it). start: the track the path
    /// leaves (heading into the path, curvature along it), if any; end: the track it arrives on (heading arriving).
    public static Result? Smooth(IReadOnlyList<(double X, double Z)> path, double over, double maxOffset, JoinCurve.Pose? start, JoinCurve.Pose? end)
    {
        var guide = Resample(path);
        if (guide is null)
            return null;
        var n = guide.Count;
        if (n < 4 || over <= Spacing)
            return new Result(guide, 0.0);

        var step = Length(guide) / (n - 1);
        var before = start is { } s ? Ghost(s, step, -1) : [];
        var after = end is { } e ? Ghost(e, step, +1) : [];
        before.Reverse();

        // All the points in order: the joined track's before, the path, the joined track's after.
        var m = before.Count + n + after.Count;
        var first = before.Count;
        var isFixed = new bool[m];
        var targetX = new double[m];
        var targetZ = new double[m];
        void Fix(int i, (double X, double Z) p)
        {
            isFixed[i] = true;
            (targetX[i], targetZ[i]) = p;
        }

        for (var i = 0; i < before.Count; i++)
            Fix(i, before[i]);
        for (var i = 0; i < after.Count; i++)
            Fix(first + n + i, after[i]);
        Fix(first, guide[0]);
        Fix(first + n - 1, guide[^1]);
        for (var i = 0; i < n; i++)
            if (!isFixed[first + i])
                (targetX[first + i], targetZ[first + i]) = guide[i];

        // A wobble of wavelength Over is about halved: lambda (2 sin(pi / L))^6 = 1, L in points.
        var wave = over / step;
        var lambda = Math.Pow(1.0 / (2.0 * Math.Sin(Math.PI / Math.Max(wave, 3.0))), 6.0);
        var weight = new double[m];
        for (var i = 0; i < m; i++)
            weight[i] = isFixed[i] ? 0.0 : 1.0;

        List<(double X, double Z)> points = guide;
        var worst = 0.0;
        for (var round = 0; round < 12; round++)
        {
            if (ExactFairing.Solve(m, lambda, weight, isFixed, targetX, targetZ) is not { } solved)
                return null;
            points = solved.GetRange(first, n);
            points[0] = guide[0];
            points[^1] = guide[^1];
            worst = 0.0;
            var over2 = false;
            for (var i = 1; i < n - 1; i++)
            {
                var away = Offset(guide, points[i], i);
                worst = Math.Max(worst, away);
                if (maxOffset > 0.0 && away > maxOffset)
                {
                    weight[first + i] *= Math.Min(100.0, (away / maxOffset) * (away / maxOffset) * 2.0);
                    over2 = true;
                }
            }

            if (!over2)
                break;
        }

        return new Result(points, worst);
    }

    /// The smoothed path as track pieces, an arc between each two points (for the preview and the control points).
    public static List<TrackPiece> Pieces(IReadOnlyList<(double X, double Z)> points)
    {
        var n = points.Count;
        var curvature = new double[n];
        for (var i = 1; i + 1 < n; i++)
            curvature[i] = Curvature(points[i - 1], points[i], points[i + 1]);
        if (n >= 3)
            (curvature[0], curvature[^1]) = (curvature[1], curvature[^2]);

        var pieces = new List<TrackPiece>(n);
        for (var i = 0; i + 1 < n; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            if (chord < 1e-9)
                continue;
            var k = 0.5 * (curvature[i] + curvature[i + 1]);
            // An arc through both ends: it leaves turned back by half what it turns over the chord.
            var half = Math.Abs(k) * chord * 0.5 < 1e-9 ? 0.0 : Math.Asin(Math.Clamp(k * chord * 0.5, -1.0, 1.0));
            var length = Math.Abs(k) < 1e-9 ? chord : 2.0 * half / k;
            pieces.Add(new TrackPiece(a.X, a.Z, Math.Atan2(b.Z - a.Z, b.X - a.X) - half, length, k, true));
        }

        return pieces;
    }

    /// Signed curvature through three points (positive turning from +x towards +z).
    private static double Curvature((double X, double Z) a, (double X, double Z) b, (double X, double Z) c)
    {
        var ab = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        var bc = Math.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Z - b.Z) * (c.Z - b.Z));
        var ca = Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Z - c.Z) * (a.Z - c.Z));
        var cross = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        var d = ab * bc * ca;
        return d < 1e-12 ? 0.0 : 2.0 * cross / d;
    }

    /// The joined track's line beyond an end, the nearest first: side -1 before the path's start, +1 past its end.
    private static List<(double X, double Z)> Ghost(JoinCurve.Pose pose, double step, int side)
    {
        var (tx, tz) = (Math.Cos(pose.Heading), Math.Sin(pose.Heading));
        var (nx, nz) = (-tz, tx);
        var list = new List<(double X, double Z)>();
        for (var k = 1; k <= Ghosts; k++)
        {
            var s = side * k * step;
            var lateral = 0.5 * pose.Curvature * s * s;
            list.Add((pose.X + tx * s + nx * lateral, pose.Z + tz * s + nz * lateral));
        }

        return list;
    }

    /// The path every Spacing metres or so (evenly, so its ends are its ends).
    private static List<(double X, double Z)>? Resample(IReadOnlyList<(double X, double Z)> path)
    {
        var clean = new List<(double X, double Z)>();
        foreach (var p in path)
            if (clean.Count == 0 || Math.Abs(p.X - clean[^1].X) + Math.Abs(p.Z - clean[^1].Z) > 1e-6)
                clean.Add(p);
        if (clean.Count < 2)
            return null;

        var total = Length(clean);
        var count = Math.Max(1, (int)Math.Ceiling(total / Spacing));
        var each = total / count;
        var result = new List<(double X, double Z)>(count + 1) { clean[0] };
        var (j, along) = (0, 0.0); // the segment from clean[j], and how far along the path it starts
        for (var k = 1; k < count; k++)
        {
            var want = k * each;
            while (j + 1 < clean.Count - 1 && along + Distance(clean[j], clean[j + 1]) < want)
                along += Distance(clean[j], clean[j++ + 1]);
            var seg = Distance(clean[j], clean[j + 1]);
            var f = seg < 1e-12 ? 0.0 : Math.Clamp((want - along) / seg, 0.0, 1.0);
            result.Add((clean[j].X + (clean[j + 1].X - clean[j].X) * f, clean[j].Z + (clean[j + 1].Z - clean[j].Z) * f));
        }

        result.Add(clean[^1]);
        return result;
    }

    private static double Distance((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static double Length(IReadOnlyList<(double X, double Z)> p)
    {
        var total = 0.0;
        for (var i = 1; i < p.Count; i++)
            total += Distance(p[i - 1], p[i]);
        return total;
    }

    /// How far a point is from the guide, looking along it near where it was taken.
    private static double Offset(List<(double X, double Z)> guide, (double X, double Z) p, int near)
    {
        var best = double.MaxValue;
        for (var k = Math.Max(0, near - 25); k < Math.Min(guide.Count - 1, near + 25); k++)
        {
            var (a, b) = (guide[k], guide[k + 1]);
            var (dx, dz) = (b.X - a.X, b.Z - a.Z);
            var f = Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / Math.Max(dx * dx + dz * dz, 1e-12), 0.0, 1.0);
            var (ex, ez) = (a.X + dx * f - p.X, a.Z + dz * f - p.Z);
            best = Math.Min(best, ex * ex + ez * ez);
        }

        return Math.Sqrt(best);
    }
}
