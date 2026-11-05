using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

public enum YardMode { Follow, Parallel, Crossover, Ladder }

/// What a yard tool would lay. Ways: the first of a ladder is its lead, the rest branch off it. Problem: why it can't be laid.
public sealed record YardPlan(List<PlannedWay> Ways, string Summary, string? Problem, int Size)
{
    public static YardPlan Refused(string why) => new([], "", why, 0);
}

/// Laying a yard from built track, in two clicks. Parallel: the stretch of a track to run beside. Crossover: a place on each
/// of two tracks, the difference between them saying which way it leans. Ladder: where the lead leaves a track, then where
/// the yard tracks end and on which side.
public sealed class YardTool
{
    public const float MinSpacing = 3.5f;
    public const float MaxSpacing = 12f;
    public const int MaxTracks = 20;

    /// A guard against a mistyped count, not a design limit: past this they'd be kilometres wide.
    public const int MaxParallel = 500;

    /// How near a click has to be to a track, and to one of its points to use that point.
    private const double OnTrack = 4.0;
    private const double AtPoint = 2.5;
    private const double LooseEndSnap = 8.0;
    private const double ShortestStub = 6.0;
    private const double DesignOnTrack = 2.5;
    private const double DesignAtPoint = 0.5;

    /// How near a new track may run to another along its length.
    private const double RunClearance = 3.0;
    private const double ShortestParallel = 20.0;
    private const double SampleStep = 2.0;

    /// A crossover or ladder keeps this far from any other junction on its tracks.
    private const double JunctionClearance = 3.0;
    private const double ShortestBody = 20.0;
    private const double RoomBetweenSwitches = 6.0;
    private const double RoomAfterFirstFrog = 4.0;

    /// The tracks a crossover can be laid out between, as CrossoverFitting has it.
    private const double MaxAngle = CrossoverFitting.LinkAngle;

    private static readonly int[] YardSizes = [8, 7, 6, 10, 15, 20];
    private static readonly int[] MainSizes = [10, 15, 20, 8, 7, 6];

    private (double X, double Z)? _pointer;
    private object? _key;

    public YardMode Mode { get; private set; }
    public float Spacing = 4.6f;
    public int Tracks = 4;

    /// How many to lay at once with Parallel, the first beside the track and each of the others beside the last.
    public int ParallelTracks = 1;

    public static YardTool ForParallel() => new() { Mode = YardMode.Parallel };

    /// The switch size wanted; nought for automatic.
    public int Size;

    public (LayPoint Point, (double X, double Z) Runs)? Anchor { get; private set; }
    public YardPlan? Plan { get; private set; }

    public void SetMode(YardMode mode)
    {
        Mode = mode;
        if (mode == YardMode.Ladder && Size > 10)
            Size = 0;
        Clear();
    }

    public void Clear() => (Anchor, Plan, _pointer, _key) = (null, null, null, null);

    /// A click with no anchor yet: starts on the track there, if there is one.
    public bool Start(TrackEditor tracks, double x, double z)
    {
        if (tracks.TrackNear(x, z, OnTrack, AtPoint) is not { } on)
            return false;
        Anchor = on;
        if (Mode == YardMode.Ladder && tracks.LooseEndNear(x, z, LooseEndSnap) is { } end && tracks.LeavingEnd(end) is { } leaving)
        {
            var (ex, ez) = tracks.PositionOf(end);
            Anchor = (new LayPoint(ex, ez, Node: end), leaving);
        }

        _key = null;
        return true;
    }

    /// Works out what would be laid with the pointer at (x, z).
    public void Update(TrackEditor tracks, NetworkLines lines, (double X, double Z)? pointer, Func<double, string> length)
    {
        if (Anchor is not { } anchor || pointer is not { } at)
        {
            Plan = null;
            return;
        }

        object key = (Mode, anchor.Point.X, anchor.Point.Z, Math.Round(at.X * 4.0), Math.Round(at.Z * 4.0), Spacing, Tracks, ParallelTracks, Size, tracks.Network.Version);
        _pointer = at;
        if (key.Equals(_key))
            return;
        _key = key;
        Plan = Mode switch
        {
            YardMode.Parallel => Parallel(tracks, lines, anchor, at, length),
            YardMode.Crossover => Crossover(tracks, anchor, at, length),
            YardMode.Ladder => Ladder(tracks, anchor, at, length),
            _ => null,
        };
    }

    // --- Parallel. ---

    private static (NetworkLine Line, float S)? NearestLine(NetworkLines lines, double x, double z)
    {
        (NetworkLine, float)? found = null;
        var best = double.MaxValue;
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Disused || x < line.MinX - 50.0 || x > line.MaxX + 50.0 || z < line.MinZ - 50.0 || z > line.MaxZ + 50.0)
                continue;
            if (OnLine(line, x, z) is { } on && on.Away < best)
                (best, found) = (on.Away, (line, on.S));
        }

        return found;
    }

    private static (float S, double Away)? OnLine(NetworkLine line, double x, double z)
    {
        (float, double)? found = null;
        var best = double.MaxValue;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (dx, dz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
            var t = Math.Clamp(((x - line.X[i]) * dx + (z - line.Z[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (ex, ez) = (line.X[i] + dx * t - x, line.Z[i] + dz * t - z);
            var away = ex * ex + ez * ez;
            if (away < best)
                (best, found) = (away, (line.S[i] + (line.S[i + 1] - line.S[i]) * (float)t, Math.Sqrt(away)));
        }

        return found;
    }

    private static (double X, double Z) AlongLine(NetworkLine line, double s)
    {
        s = Math.Clamp(s, 0.0, line.S[^1]);
        var i = Array.BinarySearch(line.S, (float)s);
        i = i >= 0 ? Math.Min(i, line.Count - 2) : Math.Clamp(~i - 1, 0, line.Count - 2);
        var t = (s - line.S[i]) / Math.Max(line.S[i + 1] - line.S[i], 1e-6);
        return (line.X[i] + (line.X[i + 1] - line.X[i]) * t, line.Z[i] + (line.Z[i + 1] - line.Z[i]) * t);
    }

    /// The way a line runs at s, measured over a few metres so a sample's rounding doesn't show.
    private static (double X, double Z) LineRuns(NetworkLine line, double s)
    {
        var (a, b) = (AlongLine(line, s - 4.0), AlongLine(line, s + 4.0));
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }

    private YardPlan Parallel(TrackEditor tracks, NetworkLines lines, (LayPoint Point, (double X, double Z) Runs) anchor, (double X, double Z) at, Func<double, string> length)
    {
        if (NearestLine(lines, anchor.Point.X, anchor.Point.Z) is not { } from || OnLine(from.Line, at.X, at.Z) is not { } to)
            return YardPlan.Refused("No track here");

        var line = from.Line;
        var (first, last) = (Math.Min(from.S, to.S), Math.Max(from.S, to.S));
        if (last - first < ShortestParallel)
            return YardPlan.Refused($"Shorter than {length(ShortestParallel)}");

        // Which side: where the pointer is, off the track at the place it's level with.
        var (px, pz) = AlongLine(line, to.S);
        var (rx, rz) = LineRuns(line, to.S);
        var side = rx * (at.Z - pz) - rz * (at.X - px) >= 0.0 ? 1.0 : -1.0;

        var count = Math.Max(2, (int)Math.Ceiling((last - first) / 4.0));
        var (spacing, n) = (Spacing, ParallelTracks);
        var fitted = new (bool Fitted, double Radius, double Length, List<(double X, double Z)> Points)[n];
        System.Threading.Tasks.Parallel.For(0, n, t =>
        {
            var path = new List<(double X, double Z)>(count + 1);
            var off = side * spacing * (t + 1);
            for (var k = 0; k <= count; k++)
            {
                var s = first + (last - first) * (double)k / count;
                var (x, z) = AlongLine(line, s);
                var (ux, uz) = LineRuns(line, s);
                path.Add((x - uz * off, z + ux * off));
            }

            var fit = TrackAlignment.FitPath(path);
            fitted[t] = (fit.Fitted, fit.Radius, Alignment.LengthOf(fit.Pieces), Sampled(fit.Pieces, []).Points);
        });

        string? problem = null;
        for (var t = 0; t < n && problem is null; t++)
        {
            var one = fitted[t];
            var why = !one.Fitted ? "No clean fit along this stretch"
                : one.Radius < TrackQuality.MinRadius ? $"Too sharp: curve {length(one.Radius)}"
                : tracks.RunsInto(one.Points, Math.Min(RunClearance, Spacing - 0.4), false, false) ? "Runs into another track"
                : null;
            if (why is not null)
                problem = n > 1 ? $"Track {t + 1}: {why}" : why;
        }

        var tightest = fitted.Min(f => f.Radius);
        var curve = tightest < double.MaxValue / 2.0 ? $", tightest curve {length(tightest)}" : ", straight";
        var total = fitted.Sum(f => f.Length);
        var summary = n == 1 ? $"{length(total)} track, {length(Spacing)} apart{curve}" : $"{n} tracks, {length(total)} in all, {length(Spacing)} apart{curve}";
        return new YardPlan([.. fitted.Select(f => new PlannedWay([.. f.Points.Select(p => new LayPoint(p.X, p.Z))]))], summary, problem, 0);
    }

    // --- Crossover. ---

    private YardPlan Crossover(TrackEditor tracks, (LayPoint Point, (double X, double Z) Runs) anchor, (double X, double Z) at, Func<double, string> length)
    {
        if (tracks.TrackNear(at.X, at.Z, OnTrack, DesignAtPoint) is not { } other)
            return YardPlan.Refused("Click the other track");

        var (ax, az) = (anchor.Point.X, anchor.Point.Z);
        var (dx, dz) = (other.Point.X - ax, other.Point.Z - az);
        var u = anchor.Runs;
        var along = dx * u.X + dz * u.Z;
        if (along < 0.0)
            (u, along) = ((-u.X, -u.Z), -along);
        var cross = u.X * dz - u.Z * dx;
        var spacing = (float)Math.Abs(cross);
        var n = cross >= 0.0 ? (X: -u.Z, Z: u.X) : (X: u.Z, Z: -u.X);

        var least = 4f * Turnout.RailOffset + 0.3f;
        if (spacing < least)
            return YardPlan.Refused("Click the other track");
        if (spacing > MaxSpacing)
            return YardPlan.Refused($"Tracks {length(spacing)} apart: a crossover spans {length(MaxSpacing)} at most");
        var angle = Math.Acos(Math.Min(1.0, Math.Abs(u.X * other.Runs.X + u.Z * other.Runs.Z))) * 180.0 / Math.PI;
        if (angle >= MaxAngle)
            return YardPlan.Refused(string.Create(CultureInfo.InvariantCulture, $"Tracks {angle:0} degrees apart: not parallel"));

        var yard = tracks.IsYard(anchor.Point) && tracks.IsYard(other.Point);
        string? why = null;
        foreach (var number in Size != 0 ? [Size] : yard ? YardSizes : MainSizes)
        {
            var plan = CrossoverOf(tracks, (ax, az), u, n, along, spacing, number, length, out var problem);
            if (plan is not null)
                return plan;
            why ??= problem;
        }

        return YardPlan.Refused(why ?? "No crossover fits");
    }

    internal static YardPlan? CrossoverOf(TrackEditor tracks, (double X, double Z) a, (double X, double Z) u, (double X, double Z) n, double along, float spacing, int number, Func<double, string> length, out string? problem)
    {
        var total = CrossoverFitting.Length(number, spacing);
        var from = along / 2.0 - total / 2.0;
        var start = (X: a.X + u.X * from, Z: a.Z + u.Z * from);
        var end = (X: start.X + u.X * total + n.X * spacing, Z: start.Z + u.Z * total + n.Z * spacing);
        var far = (X: start.X + n.X * spacing, Z: start.Z + n.Z * spacing);

        if (tracks.TrackNear(start.X, start.Z, DesignOnTrack, DesignAtPoint) is not { } first || tracks.TrackNear(end.X, end.Z, DesignOnTrack, DesignAtPoint) is not { } last)
        {
            problem = $"A track ends too soon for a No. {number}";
            return null;
        }

        var beyond = JunctionClearance;
        (double X, double Z) On((double X, double Z) p, double s) => (p.X + u.X * s, p.Z + u.Z * s);
        bool Track((double X, double Z) p) => tracks.TrackNear(p.X, p.Z, DesignOnTrack, DesignAtPoint) is not null;
        double frog = Turnout.FrogOf(number);
        if (!Track(On(start, -ShortestStub)) || !Track(On(start, frog + beyond)) || !Track(On(far, total + ShortestStub)) || !Track(On(far, total - frog - beyond)))
        {
            problem = $"A track ends too soon for a No. {number}";
            return null;
        }

        if (tracks.JunctionAlong(On(start, -beyond), On(start, total + beyond)) || tracks.JunctionAlong(On(far, -beyond), On(far, total + beyond)))
        {
            problem = $"Another junction is in the way of a No. {number}";
            return null;
        }

        var steps = Math.Max(4, (int)Math.Ceiling(total / SampleStep));
        var points = new List<LayPoint>(steps + 1) { first.Point };
        var path = new List<(double X, double Z)> { (first.Point.X, first.Point.Z) };
        for (var k = 1; k < steps; k++)
        {
            var s = total * k / steps;
            var off = CrossoverFitting.Offset(number, spacing, s);
            var share = (double)k / steps;
            var x = start.X + u.X * s + n.X * off + (first.Point.X - start.X) * (1.0 - share) + (last.Point.X - end.X) * share;
            var z = start.Z + u.Z * s + n.Z * off + (first.Point.Z - start.Z) * (1.0 - share) + (last.Point.Z - end.Z) * share;
            points.Add(new LayPoint(x, z));
            path.Add((x, z));
        }

        points.Add(last.Point);
        path.Add((last.Point.X, last.Point.Z));
        problem = null;
        var refused = tracks.RunsInto(path, 0.0, true, true) ? "Runs into another track" : null;
        return new YardPlan([new PlannedWay(points)], $"No. {number} crossover, {length(total)} long, tracks {length(spacing)} apart", refused, number);
    }

    // --- Ladder. ---

    private YardPlan Ladder(TrackEditor tracks, (LayPoint Point, (double X, double Z) Runs) anchor, (double X, double Z) at, Func<double, string> length)
    {
        var number = Size != 0 ? Size : 8;
        var (ax, az) = (anchor.Point.X, anchor.Point.Z);
        var (dx, dz) = (at.X - ax, at.Z - az);

        // A loose end carries on as the yard's first track; anything else stays as it is and the lead leaves it by a switch.
        var loose = anchor.Point.Node is { } node && tracks.IsLooseEnd(node);
        var u = loose && anchor.Point.Node is { } end && tracks.LeavingEnd(end) is { } leaving ? leaving : anchor.Runs;
        var reach = dx * u.X + dz * u.Z;
        if (reach < 0.0)
        {
            if (loose)
                return YardPlan.Refused("Point away from the track's end");
            (u, reach) = ((-u.X, -u.Z), -reach);
        }

        var side = u.X * dz - u.Z * dx >= 0.0 ? 1.0 : -1.0;
        var heading = Math.Atan2(u.Z, u.X);

        // The curve that gives this size's frog angle where the routes part, which is what the fitting names it by.
        var angle = Math.Atan(1.0 / number);
        var radius = 4.0 * Turnout.RailOffset / (angle * angle);
        var frog = radius * angle;
        var (sine, cosine) = (Math.Sin(angle), Math.Cos(angle));
        var (lift, run) = (radius * (1.0 - cosine), radius * sine);

        var between = Spacing / sine;
        if (Tracks > 1 && between - frog < RoomBetweenSwitches)
            return YardPlan.Refused($"Tracks too close for a No. {number} ladder");
        if ((Spacing - 2.0 * lift) / sine < (Tracks > 1 ? RoomAfterFirstFrog : 0.0))
            return YardPlan.Refused($"Tracks too close for a No. {number} ladder");

        // Along the lead's straight, to each switch: track i (1 up) leaves where one curve back brings it level at its spacing.
        double SwitchOn(int track) => (track * Spacing - 2.0 * lift) / sine;
        var leadStraight = SwitchOn(Tracks);
        var lastTrackStarts = run + leadStraight * cosine + run;
        if (reach < lastTrackStarts + ShortestBody)
            return YardPlan.Refused($"Too short: needs {length(lastTrackStarts + ShortestBody)}");

        var lead = new List<TrackPiece>();
        var (x0, z0) = (ax, az);
        void Add(List<TrackPiece> pieces, double piece, double curvature)
        {
            var (x, z, h) = pieces.Count == 0 ? (x0, z0, heading) : (pieces[^1].At(pieces[^1].Length).X, pieces[^1].At(pieces[^1].Length).Z, pieces[^1].HeadingAt(pieces[^1].Length));
            pieces.Add(new TrackPiece(x, z, h, piece, curvature, true));
        }

        Add(lead, radius * angle, side / radius);
        Add(lead, leadStraight, 0.0);
        Add(lead, radius * angle, -side / radius);
        Add(lead, reach - lastTrackStarts, 0.0);

        var breaks = Enumerable.Range(1, Tracks - 1).Select(track => radius * angle + SwitchOn(track)).ToList();
        var (leadPoints, marks) = Sampled(lead, breaks);
        var ways = new List<PlannedWay>();
        var layPoints = leadPoints.Select(p => new LayPoint(p.X, p.Z)).ToList();
        layPoints[0] = anchor.Point;
        ways.Add(new PlannedWay(layPoints));
        var refused = tracks.RunsInto(leadPoints, 0.0, true, false);

        for (var track = 1; track < Tracks; track++)
        {
            var at0 = Alignment.Position(lead, breaks[track - 1]);
            var branch = new List<TrackPiece>();
            (x0, z0) = at0;
            var (x, z) = at0;
            branch.Add(new TrackPiece(x, z, heading + side * angle, radius * angle, -side / radius, true));
            var startsAt = (x - ax) * u.X + (z - az) * u.Z;
            Add(branch, reach - startsAt - run, 0.0);
            var points = Sampled(branch, []).Points;
            ways.Add(new PlannedWay([.. points.Select(p => new LayPoint(p.X, p.Z))], marks[track - 1]));
            refused |= tracks.RunsInto(points, 0.0, true, false);
            refused |= tracks.RunsInto(points.Skip(points.Count / 2).ToList(), RunClearance, false, false);
        }

        if (loose)
        {
            var straight = new List<(double X, double Z)>();
            var count = Math.Max(2, (int)Math.Ceiling(reach / SampleStep));
            for (var k = 0; k <= count; k++)
                straight.Add((ax + u.X * reach * k / count, az + u.Z * reach * k / count));
            var laid = straight.Select(p => new LayPoint(p.X, p.Z)).ToList();
            laid[0] = anchor.Point;
            ways.Add(new PlannedWay(laid));
            refused |= tracks.RunsInto(straight, 0.0, true, false);
        }

        var far = (X: ax + u.X * (frog + JunctionClearance), Z: az + u.Z * (frog + JunctionClearance));
        var back = (X: ax - u.X * JunctionClearance, Z: az - u.Z * JunctionClearance);
        var stub = (X: ax - u.X * ShortestStub, Z: az - u.Z * ShortestStub);
        string? problem = !loose && anchor.Point.Node is { } joined && tracks.IsJunction(joined) ? "Already a junction here"
            : !loose && (tracks.TrackNear(far.X, far.Z, DesignOnTrack, DesignAtPoint) is null || tracks.TrackNear(stub.X, stub.Z, DesignOnTrack, DesignAtPoint) is null) ? "Too near the end of the track"
            : !loose && tracks.JunctionAlong(back, far) ? $"Another junction is in the way of a No. {number}"
            : refused ? "Runs into another track"
            : null;
        return new YardPlan(ways, $"No. {number} ladder, {Tracks + (loose ? 1 : 0)} tracks {length(Spacing)} apart, {length(reach)} long", problem, number);
    }

    /// Points along pieces, no more than a sample step apart, and falling exactly on each break; and where each break fell.
    internal static (List<(double X, double Z)> Points, List<int> Marks) Sampled(IReadOnlyList<TrackPiece> pieces, IReadOnlyList<double> breaks)
    {
        var total = Alignment.LengthOf(pieces);
        var (points, marks) = (new List<(double X, double Z)> { Alignment.Position(pieces, 0.0) }, new List<int>());
        var from = 0.0;
        foreach (var to in breaks.Append(total))
        {
            var steps = Math.Max(1, (int)Math.Ceiling((to - from) / SampleStep));
            for (var k = 1; k <= steps; k++)
                points.Add(Alignment.Position(pieces, from + (to - from) * k / steps));
            if (marks.Count < breaks.Count)
                marks.Add(points.Count - 1);
            from = to;
        }

        return (points, marks);
    }

    // --- Drawing. ---

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, float scale)
    {
        if (Anchor is { } anchor && project(anchor.Point.X, anchor.Point.Z) is { } start)
            draw.AddCircleFilled(start, 5f * scale, Rgba.White, 12);
        if (Plan is not { } plan)
            return;

        var color = plan.Problem is null ? Style.Caution : Style.Stop;
        foreach (var way in plan.Ways)
        {
            float2? previous = null;
            for (var i = 0; i < way.Points.Count; i++)
            {
                var (x, z) = way.From is { } from && i == 0 ? (plan.Ways[0].Points[from].X, plan.Ways[0].Points[from].Z) : (way.Points[i].X, way.Points[i].Z);
                var at = project(x, z);
                if (previous is { } a && at is { } b)
                    draw.AddLine(a, b, color, 3f * scale);
                previous = at;
            }

            if (way.From is { } branch && project(plan.Ways[0].Points[branch].X, plan.Ways[0].Points[branch].Z) is { } node)
                draw.AddCircleFilled(node, 4f * scale, Rgba.White, 12);
        }

        if (_pointer is { } pointer && project(pointer.X, pointer.Z) is { } label)
            PenTool.Label(draw, label, plan.Problem ?? plan.Summary, color);
    }
}
