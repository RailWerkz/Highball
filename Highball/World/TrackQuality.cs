namespace Highball.World;

public enum DefectKind { Bend, HeightStep, JunctionStep, Clearance, Wiggle }

/// Something wrong with the track as built. Value: a bend's radius, or a step's height, or a clearance (metres).
public sealed record TrackDefect(DefectKind Kind, double X, double Z, float Value, int Line);

/// Measures the track as it finally came out, whatever made it that way: the honest answer to whether it's right.
/// Bends sharper than any real track, steps in height along a line or between lines meeting at a node, tracks closer
/// together than two trains could pass.
public static class TrackQuality
{
    /// The tightest radius of real track, measured over 8 m (so it's the bend that shows, not a survey point).
    public const float MinRadius = 60f;
    private const float BendWindow = 4f;
    /// Steeper than this over one point spacing, and at least this tall, is a step rather than a grade.
    private const float StepGrade = 0.08f;
    private const float StepHeight = 0.08f;
    /// Lines meeting at a node differing by more than this. (Rail lines are layered a few millimetres apart.)
    private const float JunctionHeight = 0.05f;

    public static List<TrackDefect> Find(NetworkLines lines)
    {
        var defects = new List<TrackDefect>();
        var rails = lines.Lines.Where(l => l.Rail && !l.Disused).ToList();

        foreach (var line in rails)
        {
            Bends(line, defects);
            Wiggles(line, defects);
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var ds = MathF.Max(line.S[i + 1] - line.S[i], 0.01f);
                var dy = MathF.Abs(line.Y[i + 1] - line.Y[i]);
                if (dy >= StepHeight && dy / ds >= StepGrade)
                    defects.Add(new TrackDefect(DefectKind.HeightStep, line.X[i], line.Z[i], dy, line.Index));
            }
        }

        var heights = new Dictionary<long, (float Low, float High, double X, double Z, int Line)>();
        foreach (var line in rails)
            foreach (var (node, point) in line.Nodes)
            {
                var y = line.Y[point];
                heights[node] = heights.TryGetValue(node, out var h)
                    ? (MathF.Min(h.Low, y), MathF.Max(h.High, y), h.X, h.Z, h.Line)
                    : (y, y, line.X[point], line.Z[point], line.Index);
            }

        foreach (var (_, h) in heights)
            if (h.High - h.Low > JunctionHeight)
                defects.Add(new TrackDefect(DefectKind.JunctionStep, h.X, h.Z, h.High - h.Low, h.Line));
        return defects;
    }

    /// A curve one way then the other within WiggleLength, both sharper than WiggleRadius: track that should be straight
    /// or one gentle curve. Value: the sharper one's radius.
    private static void Wiggles(NetworkLine line, List<TrackDefect> defects)
    {
        (double X, double Z) At(float s)
        {
            var i = Array.BinarySearch(line.S, s);
            if (i >= 0)
                return (line.X[i], line.Z[i]);
            i = Math.Clamp(~i, 1, line.Count - 1);
            var t = (s - line.S[i - 1]) / MathF.Max(line.S[i] - line.S[i - 1], 1e-4f);
            return (line.X[i - 1] + (line.X[i] - line.X[i - 1]) * t, line.Z[i - 1] + (line.Z[i] - line.Z[i - 1]) * t);
        }

        (float Curvature, float S, int Point)? last = null, peak = null;
        for (var p = 0; p < line.Count; p++)
        {
            var s = line.S[p];
            if (s < WiggleWindow || s > line.S[^1] - WiggleWindow)
                continue;
            var (a, c) = (At(s - WiggleWindow), At(s + WiggleWindow));
            var (ux, uz, vx, vz) = (line.X[p] - a.X, line.Z[p] - a.Z, c.X - line.X[p], c.Z - line.Z[p]);
            var curvature = (float)(Math.Atan2(ux * vz - uz * vx, ux * vx + uz * vz) / WiggleWindow);
            if (peak is { } now && MathF.Sign(now.Curvature) != MathF.Sign(curvature))
            {
                if (MathF.Abs(now.Curvature) > 1f / WiggleRadius)
                    last = now;
                peak = null;
            }

            if (peak is null || MathF.Abs(curvature) > MathF.Abs(peak.Value.Curvature))
                peak = (curvature, s, p);
            if (peak is { } here && MathF.Abs(here.Curvature) > 1f / WiggleRadius && last is { } before &&
                MathF.Sign(before.Curvature) != MathF.Sign(here.Curvature) && here.S - before.S < WiggleLength && here.Point == p)
            {
                defects.Add(new TrackDefect(DefectKind.Wiggle, line.X[p], line.Z[p], 1f / MathF.Max(MathF.Abs(here.Curvature), MathF.Abs(before.Curvature)), line.Index));
                last = null;
                peak = null;
            }
        }
    }

    private const float WiggleWindow = 8f;
    private const float WiggleRadius = 300f;
    private const float WiggleLength = 60f;

    /// The furthest anything may lay the track from its mapped line: the data is the truth, within what a mapper's clicks
    /// are out by.
    public const double MaxStray = 1.5;
    /// The same for a switch's diverging route, which has to cut the corner of however sharply it was drawn.
    public const double MaxSwitchStray = 3.0;

    /// How far a place is from the line's mapped shape around one of its points.
    public static double OffMapped(NetworkLine line, double x, double z, int point)
    {
        const int reach = 12;
        var (mx, mz) = (line.MappedX, line.MappedZ);
        if (mx.Length < 2)
            return 0.0;
        var best = double.MaxValue;
        for (var i = Math.Max(0, point - reach); i < Math.Min(mx.Length - 1, point + reach); i++)
        {
            var (dx, dz) = (mx[i + 1] - mx[i], mz[i + 1] - mz[i]);
            var t = Math.Clamp(((x - mx[i]) * dx + (z - mz[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (ex, ez) = (mx[i] + dx * t - x, mz[i] + dz * t - z);
            best = Math.Min(best, ex * ex + ez * ez);
        }

        return Math.Sqrt(best);
    }

    /// How far the track strays from its mapped line, sampled every few metres: the distance at each sample, and where
    /// the worst is.
    public static (List<float> Distances, double X, double Z) Stray(NetworkLines lines)
    {
        var distances = new List<float>();
        var (worst, wx, wz) = (0f, 0.0, 0.0);
        foreach (var line in lines.Lines.Where(l => l.Rail && !l.Disused && l.MappedX.Length >= 2))
        {
            var (mx, mz) = (line.MappedX, line.MappedZ);
            for (var p = 0; p < line.Count; p += 3)
            {
                var best = double.MaxValue;
                for (var i = 0; i + 1 < mx.Length; i++)
                {
                    var (dx, dz) = (mx[i + 1] - mx[i], mz[i + 1] - mz[i]);
                    var t = Math.Clamp(((line.X[p] - mx[i]) * dx + (line.Z[p] - mz[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                    var (ex, ez) = (mx[i] + dx * t - line.X[p], mz[i] + dz * t - line.Z[p]);
                    best = Math.Min(best, ex * ex + ez * ez);
                }

                var d = (float)Math.Sqrt(best);
                distances.Add(d);
                if (d > worst)
                    (worst, wx, wz) = (d, line.X[p], line.Z[p]);
            }
        }

        return (distances, wx, wz);
    }

    /// The radius the line bends at a point, measured over 8 m.
    public static float RadiusAt(NetworkLine line, int p)
    {
        (double X, double Z) At(float s)
        {
            var i = Array.BinarySearch(line.S, s);
            if (i >= 0)
                return (line.X[i], line.Z[i]);
            i = Math.Clamp(~i, 1, line.Count - 1);
            var t = (s - line.S[i - 1]) / MathF.Max(line.S[i] - line.S[i - 1], 1e-4f);
            return (line.X[i - 1] + (line.X[i] - line.X[i - 1]) * t, line.Z[i - 1] + (line.Z[i] - line.Z[i - 1]) * t);
        }

        var s = line.S[p];
        if (s < BendWindow || s > line.S[^1] - BendWindow)
            return float.MaxValue;
        var (a, b, c) = (At(s - BendWindow), (X: line.X[p], Z: line.Z[p]), At(s + BendWindow));
        var (ux, uz, vx, vz) = (b.X - a.X, b.Z - a.Z, c.X - b.X, c.Z - b.Z);
        var turn = Math.Abs(Math.Atan2(ux * vz - uz * vx, ux * vx + uz * vz));
        return turn > 1e-6 ? (float)(BendWindow / turn) : float.MaxValue;
    }

    /// The sharpest place in each run of a line sharper than MinRadius.
    private static void Bends(NetworkLine line, List<TrackDefect> defects)
    {
        (float Radius, int Point)? run = null;
        for (var p = 0; p < line.Count; p++)
        {
            var radius = RadiusAt(line, p);
            if (radius < MinRadius)
                run = run is { } r && r.Radius <= radius ? r : (radius, p);
            else if (run is { } done)
            {
                defects.Add(new TrackDefect(DefectKind.Bend, line.X[done.Point], line.Z[done.Point], done.Radius, line.Index));
                run = null;
            }
        }

        if (run is { } last)
            defects.Add(new TrackDefect(DefectKind.Bend, line.X[last.Point], line.Z[last.Point], last.Radius, line.Index));
    }
}
