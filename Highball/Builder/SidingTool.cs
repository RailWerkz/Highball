using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// A siding junction's lead, as laid on straight track: the switch's own lead curve at its size's radius from the points
/// out past the frog, a straight at the frog angle, and a reverse curve of the same radius bringing the siding in parallel
/// at exactly the spacing. On a curved line the same offset is taken across the line, so the siding follows it.
public static class SidingLead
{
    /// How far along the main line the lead takes (metres), for a size and spacing.
    public static double Length(int number, double spacing)
    {
        var (radius, angle, rise, straight) = Parts(number, spacing);
        return 2.0 * radius * Math.Sin(angle) + straight;
    }

    /// How far off the main line the siding is, x metres along it from the points.
    public static double Offset(int number, double spacing, double x)
    {
        var (radius, angle, rise, straight) = Parts(number, spacing);
        var arc = radius * Math.Sin(angle);
        var length = 2.0 * arc + straight;
        if (x <= 0.0)
            return 0.0;
        if (x >= length)
            return spacing;
        if (x <= arc)
            return radius - Math.Sqrt(radius * radius - x * x);
        if (x <= arc + straight)
            return rise + (x - arc) * Math.Tan(angle);
        var back = length - x;
        return spacing - (radius - Math.Sqrt(radius * radius - back * back));
    }

    /// The lead curve's radius, the angle it turns to (the frog's, where the spacing leaves room), how far over that takes
    /// it, and the straight between it and the reverse curve, along the line.
    private static (double Radius, double Angle, double Rise, double Straight) Parts(int number, double spacing)
    {
        double radius = Turnout.RadiusOf(number);
        var angle = Math.Asin(Math.Min(1.0, Turnout.FrogOf(number) / radius));
        var rise = radius * (1.0 - Math.Cos(angle));
        if (2.0 * rise > spacing)
        {
            // Too close for the lead curve to reach the frog angle and come back: an S of two curves, no straight.
            angle = Math.Acos(1.0 - spacing / (2.0 * radius));
            return (radius, angle, radius * (1.0 - Math.Cos(angle)), 0.0);
        }

        return (radius, angle, rise, (spacing - 2.0 * rise) / Math.Tan(angle));
    }
}

/// The Siding tool: a siding junction laid in two clicks. The first click on a track (away from its switches and bridges)
/// sets where the first switch's points go; the pointer then shows the whole siding, leaving through a standard turnout,
/// easing over to the spacing and running beside the track, following its curves, to level with the pointer. The second
/// click sets the end: a second turnout back into the track (Passing) or a plain end (Stub). Until Enter lays it, F flips
/// the side, [ and ] step the switch size, the panel sets the spacing, and either end can be dragged or clicked again.
public sealed class SidingTool(ToolContext context, RouteInfo route) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    /// The least parallel track a passing siding has between its leads, and a stub past its lead.
    private const double LeastParallel = 10.0;
    /// How far a switch keeps from another junction on the line, and from the line's end.
    private const double JunctionClear = 12.0;
    private const double EndClear = 3.0;
    /// How far the siding's laid points, taken straight between, may stray from where it should be (metres), and the longest
    /// gap between two of them: the track's faired through them as the smoothest line that passes through every one, and across
    /// a longer gap that line swings (a long straight between two curves bows out into them).
    private const double LaidTolerance = 0.05;
    private const double LaidGap = 20.0;

    private enum Kind { Passing, Stub }

    private static Kind _kind = Kind.Passing;
    private static int _size = 10;
    private static float _spacing = 4.5f;

    /// The widest a siding can be set from its track: 200 ft, for one well off to the side (an industry's spur, a long
    /// reverse curve out). Wider than yards and crossovers go (YardTool.MaxSpacing): the lead just runs straight between its
    /// two curves for longer.
    private const float MaxSpacing = 200f * 0.3048f;

    /// The line the siding leaves, the points' distance along it, and the end's (null till the second click) or the
    /// pointer's. Side: +1 to the right of the line's own direction.
    private SidingRun? _line;
    private IReadOnlyList<NetworkLine>? _lines;
    /// The run last built under the pointer, by the line it was built from.
    private (NetworkLine Seed, SidingRun Run)? _hoverRun;
    private (double X, double Z) _startAt, _endAt;
    private double _start;
    private double? _end;
    private double? _pointer;
    private int _side = 1;
    private bool _sideSet;

    private enum Handle { None, Start, End }
    private Handle _drag, _hoverHandle;

    // Before a start: what a click would start on, and why not if it can't.
    private (double X, double Z)? _hoverAt;
    private string? _hoverWhy;

    private Plan? _plan;
    private object? _key;

    /// The siding worked out: its centreline, the points it's laid through (between its ends), where its switches are,
    /// its lengths and tightest curve, and what stops it being laid (Problem) or is worth knowing (Warnings).
    private sealed record Plan(
        List<(double X, double Z)> Path, List<(double X, double Z)> Laid, double StartS, double EndS, int Dir,
        (double X, double Z) Start, (double X, double Z) End, double Length, double Parallel, double Lead, double MinRadius,
        string? Problem, List<string> Warnings);

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        if (context.Source.Lines is not { } lines)
            return;
        if (!ReferenceEquals(lines.Lines, _lines))
        {
            _hoverRun = null;
            if (_line is not null)
                Refind(lines);
        }

        _lines = lines.Lines;

        if (!Keyboard.KeysBlocked && _line is not null)
        {
            if (Input.KeyDown(GlfwKey.Tab))
                _kind = _kind == Kind.Passing ? Kind.Stub : Kind.Passing;
            if (Input.KeyDown(GlfwKey.F) && !ImGui.IsKeyDown(ImGuiKey.LeftShift))
                (_side, _sideSet) = (-_side, true);
            if (Input.KeyDown(GlfwKey.RightBracket) || Input.KeyDown(GlfwKey.LeftBracket))
                StepSize(Input.KeyDown(GlfwKey.RightBracket) ? 1 : -1);
        }

        var hit = context.Hit;
        _hoverAt = null;
        _hoverWhy = null;
        _hoverHandle = Handle.None;
        if (!context.Blocked && !context.Rebuilding)
        {
            if (_line is null)
                Hover(lines, hit);
            else
                Pointer(hit);
        }

        Replan(tracks);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && _end is not null && _plan is { Problem: null } plan)
            Lay(tracks, plan);
    }

    /// Steps the switch size through the standard ones.
    private static void StepSize(int by)
    {
        var sizes = Turnout.Sizes.Select(s => s.Number).ToArray();
        var at = Array.IndexOf(sizes, _size);
        _size = sizes[Math.Clamp((at < 0 ? Array.IndexOf(sizes, 10) : at) + by, 0, sizes.Length - 1)];
    }

    /// The line again after the track's been laid out afresh (its lines are new objects): the one under the start.
    private void Refind(NetworkLines lines)
    {
        if (NearestLine(lines, _startAt.X, _startAt.Z, 3.0) is not { } found)
        {
            Clear();
            return;
        }

        var run = SidingRun.Of(found.Line, context.Tracks, lines.Lines);
        _line = run;
        _start = OnLine(run, _startAt.X, _startAt.Z)?.S ?? 0.0;
        if (_end is not null && OnLine(run, _endAt.X, _endAt.Z) is { } end)
            _end = end.S;
        _key = null;
    }

    /// No start yet: the track under the pointer, and whether a siding can leave it there.
    private void Hover(NetworkLines lines, double3? hit)
    {
        if (hit is not { } h || NearestLine(lines, h.X, h.Z, context.PickReach(h.X, h.Z)) is not { } found)
            return;
        if (_hoverRun is not { } cached || cached.Seed != found.Line)
            _hoverRun = cached = (found.Line, SidingRun.Of(found.Line, context.Tracks, lines.Lines));
        var run = cached.Run;
        if (OnLine(run, h.X, h.Z) is not { } on)
            return;
        _hoverAt = AlongLine(run, on.S);
        _hoverWhy = WhyNot(run, on.S);
        if (_hoverWhy is null && Input.MouseDown(GlfwMouseButton.Left))
        {
            (_line, _start, _end, _pointer, _sideSet, _key) = (run, on.S, null, null, false, null);
            _startAt = _hoverAt.Value;
            var (rx, rz) = LineRuns(run, on.S);
            _side = rx * (h.Z - _startAt.Z) - rz * (h.X - _startAt.X) >= 0.0 ? 1 : -1;
        }
    }

    /// Why a switch can't go on a line at s: null if it can.
    private static string? WhyNot(SidingRun run, double s)
    {
        var line = run.LineAt(s);
        if (line.Tracks != 1 || line.Embedded || MathF.Abs(line.Gauge - 1.435f) > 0.01f)
            return "A switch needs single standard-gauge track";
        if (line.Disused)
            return "Disused track";
        if (StructureAt(run, s) is var structure and not Structure.Ground)
            return structure == Structure.Bridge ? "On a bridge" : "In a tunnel";
        if (run.Zones.Any(z => s >= z.From - 2.0 && s <= z.To + 2.0))
            return "On a switch";
        if (run.Junctions.Any(j => Math.Abs(j - s) < JunctionClear))
            return "Too near a junction";
        if (s < EndClear || s > run.Length - EndClear)
            return "Too near the track's end";
        return null;
    }

    private static Structure StructureAt(SidingRun run, double s)
    {
        if (run.Structures.Length == 0)
            return Structure.Ground;
        var i = Array.BinarySearch(run.S, s);
        i = i >= 0 ? i : ~i - 1;
        return run.Structures[Math.Clamp(i, 0, run.Structures.Length - 1)];
    }

    /// Started: the pointer sets the end (till it's clicked), or drags either end, or a click moves the nearer one there.
    private void Pointer(double3? hit)
    {
        var line = _line!;
        var project = context.ProjectGround;
        var mouse = Input.MousePosition;
        bool Near((double X, double Z) at) => project(at.X, at.Z) is { } p && Vector2Distance(p, mouse) < S(10f);
        if (_end is not null && _drag == Handle.None)
            _hoverHandle = Near(_startAt) ? Handle.Start : Near(_endAt) ? Handle.End : Handle.None;

        (double S, double Away)? on = hit is { } h ? OnLine(line, h.X, h.Z) : null;
        if (on is { } o && hit is { } at && !_sideSet && _end is null && o.Away > 0.5)
        {
            var (px, pz) = AlongLine(line, o.S);
            var (rx, rz) = LineRuns(line, o.S);
            _side = rx * (at.Z - pz) - rz * (at.X - px) >= 0.0 ? 1 : -1;
        }

        if (_end is null)
        {
            _pointer = on?.S;
            if (on is { } click && Input.MouseDown(GlfwMouseButton.Left) && Math.Abs(click.S - _start) >= 1.0)
            {
                _end = click.S;
                _endAt = AlongLine(line, click.S);
            }

            return;
        }

        if (Input.MouseDown(GlfwMouseButton.Left))
        {
            if (_hoverHandle != Handle.None)
                _drag = _hoverHandle;
            else if (on is { } click && hit is { } h2 && click.Away < Math.Max(_spacing + 3.0, context.PickReach(h2.X, h2.Z)))
                Move(Math.Abs(click.S - _start) < Math.Abs(click.S - _end.Value) ? Handle.Start : Handle.End, click.S);
        }

        if (_drag != Handle.None)
        {
            if (!Input.Mouse(GlfwMouseButton.Left))
                _drag = Handle.None;
            else if (on is { } dragged)
                Move(_drag, dragged.S);
        }
    }

    private static float Vector2Distance(float2 a, float2 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void Move(Handle which, double s)
    {
        if (which == Handle.Start)
            (_start, _startAt) = (s, AlongLine(_line!, s));
        else
            (_end, _endAt) = (s, AlongLine(_line!, s));
    }

    /// Works the siding out again when anything it depends on has changed.
    private void Replan(TrackEditor tracks)
    {
        var to = _end ?? _pointer;
        if (_line is not { } line || to is not { } end || Math.Abs(end - _start) < 1.0)
        {
            (_plan, _key) = (null, null);
            return;
        }

        object key = (line, Math.Round(_start * 10.0), Math.Round(end * 10.0), _side, _size, _spacing, _kind, tracks.Network.Version);
        if (key.Equals(_key))
            return;
        _key = key;
        _plan = Make(tracks, line, _start, end);
    }

    private Plan Make(TrackEditor tracks, SidingRun line, double from, double to)
    {
        var dir = to > from ? 1 : -1;
        var length = Math.Abs(to - from);
        double spacing = _spacing;
        var lead = SidingLead.Length(_size, spacing);
        var passing = _kind == Kind.Passing;
        var warnings = new List<string>();
        string? problem = null;

        double OffsetAt(double u) => passing ? SidingLead.Offset(_size, spacing, Math.Min(u, length - u)) : SidingLead.Offset(_size, spacing, u);
        (double X, double Z) At(double u)
        {
            var s = from + dir * u;
            var (x, z) = AlongLine(line, s);
            var (ux, uz) = LineRuns(line, s);
            var off = _side * OffsetAt(u);
            return (x - uz * off, z + ux * off);
        }

        // Along the siding: every metre to draw; to lay, only as many of those as keep it within LaidTolerance of where it should
        // be (a straight needs only its ends, a curve a point every ten metres or more), each run between two kept points taken
        // straight. The track's laid through them, faired, so it's nearer still.
        var path = new List<(double X, double Z)>();
        for (var u = 0.0; u < length; u += 1.0)
            path.Add(At(u));
        path.Add(At(length));
        var kept = Simplified(path, LaidTolerance);
        // (The path's a point a metre: a gap of n points is n metres.)
        for (var k = kept.Count - 1; k > 0; k--)
        {
            var gap = kept[k] - kept[k - 1];
            var pieces = (int)Math.Ceiling(gap / LaidGap);
            for (var j = pieces - 1; j >= 1; j--)
                kept.Insert(k, kept[k - 1] + gap * j / pieces);
        }

        var laid = kept.Skip(1).Take(kept.Count - 2).Select(i => path[i]).ToList();
        if (!passing)
            laid.Add(path[^1]);

        // The siding's tightest curve: through every three points five metres apart.
        var minRadius = double.PositiveInfinity;
        for (var u = 5.0; u + 5.0 <= length; u += 2.5)
        {
            var (a, b, c) = (At(u - 5.0), At(u), At(u + 5.0));
            var cross = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
            if (Math.Abs(cross) < 1e-9)
                continue;
            double D((double X, double Z) p, (double X, double Z) q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z));
            minRadius = Math.Min(minRadius, D(a, b) * D(b, c) * D(a, c) / (2.0 * Math.Abs(cross)));
        }

        // What stops it: room on the line, and its switches clear of others and of bridges.
        string Length(double m) => $"{m:0} m";
        var least = passing ? 2.0 * lead + LeastParallel : lead + LeastParallel;
        var (lo, hi) = (Math.Min(from, to), Math.Max(from, to));
        if (length < least)
            problem = $"Too short for a No. {_size} {(passing ? "passing siding" : "siding")}: needs {Length(least)}";
        else if (lo < EndClear || hi > line.Length - EndClear)
            problem = line.EndsAtJunction ? "The track meets a junction before the siding ends" : "The track ends before the siding does";
        else if ((passing ? new[] { from, to } : [from]).Any(s => line.LineAt(s) is { } at && (at.Tracks != 1 || at.Embedded)))
            problem = "A switch needs single standard-gauge track";
        else
        {
            foreach (var (s, away) in passing ? new[] { (from, dir), (to, -dir) } : [(from, dir)])
            {
                var (a, b) = (Math.Min(s - away * JunctionClear, s + away * (lead + JunctionClear)), Math.Max(s - away * JunctionClear, s + away * (lead + JunctionClear)));
                var frogEnd = s + away * (Turnout.FrogOf(_size) + Turnout.TimberTail);
                if (line.Junctions.Any(j => j > a && j < b))
                    problem = "Another junction is in the way of a switch";
                else if (line.Zones.Any(z => z.To > Math.Min(s, frogEnd) - 2.0 && z.From < Math.Max(s, frogEnd) + 2.0))
                    problem = "A switch would overlap another";
                else if (Enumerable.Range(0, 9).Any(k => StructureAt(line, s + (frogEnd - s) * k / 8.0) != Structure.Ground))
                    problem = "A switch can't go on a bridge or in a tunnel";
                if (problem is not null)
                    break;
            }
        }

        if (problem is null)
        {
            // Crossing another track anywhere, or running too near one where it's beside the line.
            var beside = new List<(double X, double Z)>();
            for (var u = lead; u <= (passing ? length - lead : length); u += 2.0)
                beside.Add(At(u));
            // (Not where it's still all but on the line at its switches: the line's control points are a little off its built
            // curve, so a path leaving it from on it always seems to cross it.)
            var clear = new List<(double X, double Z)>();
            for (var u = 0.0; u <= length; u += 1.0)
                if (OffsetAt(u) >= 1.0)
                    clear.Add(At(u));
            if (clear.Count >= 2 && tracks.RunsInto(clear, 0.0, false, false))
                problem = "Crosses another track";
            else if (beside.Count >= 2 && tracks.RunsInto(beside, Math.Min(3.0, spacing - 0.6), false, false))
                problem = "Runs into another track";
        }

        if (Enumerable.Range(0, (int)(length / 4.0) + 1).Any(k => StructureAt(line, from + dir * Math.Min(k * 4.0, length)) != Structure.Ground))
            warnings.Add("The line's on a bridge or in a tunnel beside the siding: the siding is laid on the ground there");
        if (minRadius < Turnout.RadiusOf(_size) * 0.5)
            warnings.Add($"The line curves so sharply the siding curves {Length(minRadius)}: try a bigger switch");

        var parallel = Math.Max(0.0, passing ? length - 2.0 * lead : length - lead);
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
            total += Math.Sqrt((path[i].X - path[i - 1].X) * (path[i].X - path[i - 1].X) + (path[i].Z - path[i - 1].Z) * (path[i].Z - path[i - 1].Z));
        return new Plan(path, laid, from, to, dir, AlongLine(line, from), passing ? AlongLine(line, to) : At(length), total, parallel, lead, minRadius,
            problem, warnings);
    }

    /// Which of a path's points to keep so the rest are all within tolerance of the straight between the kept ones either side
    /// (Douglas-Peucker): their indices, in order, the first and last always among them.
    private static List<int> Simplified(IReadOnlyList<(double X, double Z)> path, double tolerance)
    {
        var keep = new bool[path.Count];
        keep[0] = keep[^1] = true;
        var spans = new Stack<(int A, int B)>();
        spans.Push((0, path.Count - 1));
        while (spans.Count > 0)
        {
            var (a, b) = spans.Pop();
            var (ax, az, bx, bz) = (path[a].X, path[a].Z, path[b].X, path[b].Z);
            var (dx, dz) = (bx - ax, bz - az);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
            var (worst, at) = (0.0, -1);
            for (var i = a + 1; i < b; i++)
            {
                var off = Math.Abs((path[i].X - ax) * dz - (path[i].Z - az) * dx) / length;
                if (off > worst)
                    (worst, at) = (off, i);
            }

            if (at < 0 || worst <= tolerance)
                continue;
            keep[at] = true;
            spans.Push((a, at));
            spans.Push((at, b));
        }

        return Enumerable.Range(0, path.Count).Where(i => keep[i]).ToList();
    }

    /// Lays the siding: from a new point in the line at the first switch, through its points, to a new point in the line
    /// at the second (or its own end), with both switches set to the size picked.
    private void Lay(TrackEditor tracks, Plan plan)
    {
        var passing = _kind == Kind.Passing;
        if (OnMain(tracks, plan.StartS) is not { } first || (passing ? OnMain(tracks, plan.EndS) : new LayPoint(plan.End.X, plan.End.Z)) is not { } last)
        {
            context.Refused = "Couldn't find the track under a switch";
            return;
        }

        var points = new List<LayPoint> { first };
        points.AddRange(plan.Laid.Select(p => new LayPoint(p.X, p.Z)));
        points.Add(last);
        var edit = tracks.Lay(points, lead: false, exact: true, along: (RailClass)_line!.LineAt(plan.StartS).Class);
        if (edit?.Ways.Where(w => w.Before is null && w.After is { Rail: true }).Select(w => w.After).FirstOrDefault() is not { } siding)
        {
            context.Refused = "The siding couldn't be laid";
            return;
        }

        SwitchSizes.Set(route, siding.Nodes[0], _size);
        if (passing)
            SwitchSizes.Set(route, siding.Nodes[^1], _size);
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Siding: {(passing ? "passing" : "stub")}, No. {_size}, {_spacing:0.0} m apart, {plan.Length:0} m, switches {siding.Nodes[0]}{(passing ? $" and {siding.Nodes[^1]}" : "")}"));
        context.Commit(edit);
        Clear();
    }

    /// The line's track at s as a place to lay to: its own point there if it has one within a metre, or else a new point
    /// put into the segment it's on.
    private LayPoint? OnMain(TrackEditor tracks, double s)
    {
        var line = _line!;
        foreach (var (node, at) in line.Nodes)
            if (Math.Abs(at - s) < 1.0 && tracks.Has(node))
            {
                if (tracks.IsJunction(node) || tracks.IsLooseEnd(node))
                    return null;
                var (nx, nz) = tracks.PositionOf(node);
                return new LayPoint(nx, nz, Node: node);
            }

        var (x, z) = AlongLine(line, s);
        return tracks.TrackNear(x, z, 2.5, 0.05) switch
        {
            { Point.Node: { } node } when !tracks.IsJunction(node) => new LayPoint(x, z, Node: node),
            { Point.Split: { } split } => new LayPoint(x, z, Split: split),
            _ => null,
        };
    }

    private void Clear()
    {
        (_line, _end, _pointer, _plan, _key, _drag, _sideSet) = (null, null, null, null, null, Handle.None, false);
    }

    // --- The line, measured. ---

    /// The rail line nearest a place within reach, and how far along it.
    private static (NetworkLine Line, double S)? NearestLine(NetworkLines lines, double x, double z, double within)
    {
        (NetworkLine, double)? found = null;
        var best = within;
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || x < line.MinX - within || x > line.MaxX + within || z < line.MinZ - within || z > line.MaxZ + within)
                continue;
            if (OnLine(line, x, z) is { } on && on.Away < best)
                (best, found) = (on.Away, (line, on.S));
        }

        return found;
    }

    /// The nearest place on a line to a point: how far along, and how far off.
    private static (double S, double Away)? OnLine(NetworkLine line, double x, double z) => OnLine(line.X, line.Z, i => line.S[i], line.Count, x, z);

    private static (double S, double Away)? OnLine(SidingRun run, double x, double z) => OnLine(run.X, run.Z, i => run.S[i], run.Count, x, z);

    private static (double S, double Away)? OnLine(double[] xs, double[] zs, Func<int, double> along, int count, double x, double z)
    {
        (double, double)? found = null;
        var best = double.MaxValue;
        for (var i = 0; i + 1 < count; i++)
        {
            var (dx, dz) = (xs[i + 1] - xs[i], zs[i + 1] - zs[i]);
            var t = Math.Clamp(((x - xs[i]) * dx + (z - zs[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (ex, ez) = (xs[i] + dx * t - x, zs[i] + dz * t - z);
            var away = ex * ex + ez * ez;
            if (away < best)
                (best, found) = (away, (along(i) + (along(i + 1) - along(i)) * t, away));
        }

        return found is { } f ? (f.Item1, Math.Sqrt(f.Item2)) : null;
    }

    private static (double X, double Z) AlongLine(SidingRun line, double s)
    {
        s = Math.Clamp(s, 0.0, line.Length);
        var i = Array.BinarySearch(line.S, s);
        i = i >= 0 ? Math.Min(i, line.Count - 2) : Math.Clamp(~i - 1, 0, line.Count - 2);
        var t = (s - line.S[i]) / Math.Max(line.S[i + 1] - line.S[i], 1e-6);
        return (line.X[i] + (line.X[i + 1] - line.X[i]) * t, line.Z[i] + (line.Z[i + 1] - line.Z[i]) * t);
    }

    /// The way a line runs at s, measured over a few metres so a sample's rounding doesn't show.
    private static (double X, double Z) LineRuns(SidingRun line, double s)
    {
        var (a, b) = (AlongLine(line, s - 3.0), AlongLine(line, s + 3.0));
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }

    // --- Drawing and the panel. ---

    public void Draw(ImDrawListPtr draw)
    {
        var project = context.ProjectGround;
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        if (_line is null)
        {
            if (_hoverAt is { } h && project(h.X, h.Z) is { } hp)
                draw.AddCircle(hp, S(7f), _hoverWhy is null ? Accent : Caution, 0, S(2f));
            if (!context.Blocked)
                PenTool.Label(draw, Input.MousePosition, _hoverWhy is { } why ? why : "Siding: click a track where the first switch goes", _hoverWhy is null ? TextDim : Caution);
            return;
        }

        var plan = _plan;
        if (plan is not null)
        {
            var color = plan.Problem is not null ? Caution : _end is null ? Rgba.White.WithAlpha(0.85f) : Rgba.White;
            for (var i = 1; i < plan.Path.Count; i++)
                if (project(plan.Path[i - 1].X, plan.Path[i - 1].Z) is { } a && project(plan.Path[i].X, plan.Path[i].Z) is { } b)
                    draw.AddLine(a, b, color, S(2.5f));

            // A stub's end: a buffer across it.
            if (_kind == Kind.Stub && plan.Path.Count >= 2)
            {
                var (e, before) = (plan.Path[^1], plan.Path[^2]);
                var (dx, dz) = (e.X - before.X, e.Z - before.Z);
                var d = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
                var (nx, nz) = (-dz / d * 1.5, dx / d * 1.5);
                if (project(e.X + nx, e.Z + nz) is { } p && project(e.X - nx, e.Z - nz) is { } q)
                    draw.AddLine(p, q, color, S(4f));
            }
        }

        // The switches' points, and the ends' handles once both are set.
        void Point((double X, double Z) at, bool handle, bool hot)
        {
            if (project(at.X, at.Z) is not { } p)
                return;
            draw.AddCircleFilled(p, S(5f), Accent);
            if (handle)
                draw.AddCircle(p, S(hot ? 10f : 8f), hot ? Accent : Rgba.White, 0, S(2f));
        }

        var placed = _end is not null;
        Point(_startAt, placed, _hoverHandle == Handle.Start || _drag == Handle.Start);
        if (placed)
        {
            if (_kind == Kind.Passing)
                Point(_endAt, true, _hoverHandle == Handle.End || _drag == Handle.End);
            else if (plan is not null && project(plan.End.X, plan.End.Z) is { } ep)
                draw.AddCircle(ep, S(_hoverHandle == Handle.End || _drag == Handle.End ? 10f : 8f), Rgba.White, 0, S(2f));
        }

        if (context.Blocked || plan is null)
            return;
        var text = plan.Problem ?? string.Create(CultureInfo.InvariantCulture,
            $"{(_kind == Kind.Passing ? "Passing" : "Stub")}   No. {_size}   {Length(plan.Length, imperial)}   {Length(_spacing, imperial, true)} apart{(placed ? "   Enter lays it" : "")}");
        PenTool.Label(draw, Input.MousePosition, text, plan.Problem is not null ? Caution : plan.Warnings.Count > 0 ? Caution : Style.Text);
    }

    private static string Length(double metres, bool imperial, bool fine = false) => imperial
        ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : fine ? $"{metres * FeetPerMeter:0.0} ft" : $"{metres * FeetPerMeter:0} ft"
        : metres >= 1000 ? $"{metres / 1000:0.00} km" : fine ? $"{metres:0.0} m" : $"{metres:0} m";

    public void Panel(bool imperial)
    {
        var unit = imperial ? FeetPerMeter : 1f;
        Kit.BeginCard("Siding", _line is null ? "click a track" : _end is null ? "click where it ends" : "Enter lays it");
        var kind = ToolPanel.Mode("siding.kind", ["Passing", "Stub"], (int)_kind, 2);
        if (kind >= 0 && kind != (int)_kind)
            _kind = (Kind)kind;

        // Which side, as seen leaving the first switch towards the end.
        var dir = _plan?.Dir ?? 1;
        var right = _side * dir > 0 ? 1 : 0;
        var side = ToolPanel.Mode("siding.side", ["Left", "Right"], right, 2, _line is not null);
        if (side >= 0 && side != right)
            (_side, _sideSet) = (-_side, true);

        var sizes = Turnout.Sizes.Select(s => s.Number).ToArray();
        var at = Array.IndexOf(sizes, _size);
        var size = ToolPanel.Mode("siding.size", [.. sizes.Select(n => $"No. {n}")], at, 3);
        if (size >= 0 && size != at)
            _size = sizes[size];

        var spacing = _spacing * unit;
        if (ToolPanel.Value("siding.spacing", imperial ? "Spacing (ft)" : "Spacing (m)", ref spacing, YardTool.MinSpacing * unit, MaxSpacing * unit, "0.0", log: true))
            _spacing = (float)Math.Round(Math.Clamp(spacing / unit, YardTool.MinSpacing, MaxSpacing) * 10.0) / 10f;

        if (_line is null && _hoverWhy is { } why)
            ToolPanel.Problem(why);
        if (_plan is { } plan)
        {
            if (plan.Problem is { } problem)
                ToolPanel.Problem(problem);
            foreach (var warning in plan.Warnings)
                Kit.Status(Caution, warning);
        }

        Kit.EndCard();

        if (_plan is not { } p)
            return;
        Kit.BeginCard("Track");
        Kit.Stats(
        [
            new Stat("Length", Length(p.Length, imperial)),
            new Stat(_kind == Kind.Passing ? "Between leads" : "Past the lead", Length(p.Parallel, imperial)),
            new Stat("Switch", $"No. {_size}, R {Length(Turnout.RadiusOf(_size), imperial)}"),
            new Stat("Lead", Length(p.Lead, imperial)),
            new Stat("Spacing", Length(_spacing, imperial, true)),
            new Stat("Tightest curve", double.IsInfinity(p.MinRadius) ? "Straight" : Length(p.MinRadius, imperial)),
            new Stat("Control points", (p.Laid.Count + 2).ToString(CultureInfo.InvariantCulture)),
        ], 2);
        Kit.EndCard();
    }

    public bool Escape()
    {
        if (_line is null)
            return false;
        Clear();
        return true;
    }

    public (string Key, string Action)[] Hints => _line is null
        ? [("LMB", "Start on a track")]
        : _end is null
            ? [("LMB", "Set the end"), ("TAB", _kind == Kind.Passing ? "Stub" : "Passing"), ("F", "Flip side"), ("[ ]", "Switch size"), ("ESC", "Cancel")]
            : [("DRAG", "Move an end"), ("ENTER", "Lay siding"), ("TAB", _kind == Kind.Passing ? "Stub" : "Passing"), ("F", "Flip side"), ("[ ]", "Switch size"), ("ESC", "Cancel")];

    // --- Scripted (--shapeops "siding"), for testing: through the tool's own steps. ---

    /// Starts at a place on a track, ends at another, and lays it: false if any step was refused.
    public bool Script((double X, double Z) a, (double X, double Z) b, int side, int size, float spacing, bool passing, bool lay)
    {
        if (context.Source.Lines is not { } lines || NearestLine(lines, a.X, a.Z, 6.0) is not { } found)
        {
            Log.Warn("Siding script: no track at the start");
            return false;
        }

        (_kind, _size, _spacing) = (passing ? Kind.Passing : Kind.Stub, size, spacing);
        var run = SidingRun.Of(found.Line, context.Tracks, lines.Lines);
        var start = OnLine(run, a.X, a.Z)!.Value.S;
        (_line, _lines, _start, _startAt, _pointer, _key) = (run, lines.Lines, start, AlongLine(run, start), null, null);
        if (WhyNot(run, start) is { } why)
            Log.Warn($"Siding script: start refused: {why}");
        if (OnLine(run, b.X, b.Z) is not { } end)
            return false;
        (_end, _endAt) = (end.S, AlongLine(run, end.S));
        var dir = end.S > start ? 1 : -1;
        (_side, _sideSet) = (side * dir, true);
        Replan(context.Tracks);
        var plan = _plan;
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Siding script: line {found.Line.Index} in a run of {run.Parts.Count} lines, {run.Length:0} m, {start:0}..{end.S:0}, {plan?.Length:0} m, {plan?.Laid.Count + 2} points, lead {plan?.Lead:0.0} m, tightest {plan?.MinRadius:0} m, problem: {plan?.Problem ?? "none"}, warnings: {string.Join("; ", plan?.Warnings ?? [])}"));
        if (lay && plan is { Problem: null })
            Lay(context.Tracks, plan);
        return plan is { Problem: null };
    }
}

/// A track as the Siding tool follows it: built lines joined end to end wherever the track itself runs on (two ways meeting,
/// their tags differing, with nothing else there), as far as a real junction or the track's end each way. Distances (S)
/// are along the whole run; the switches on it (Zones) and its junctions are measured the same way.
public sealed class SidingRun
{
    public required double[] X { get; init; }
    public required double[] Z { get; init; }
    public required double[] S { get; init; }
    /// Per segment (point i to i + 1).
    public required Structure[] Structures { get; init; }
    /// The control points it passes, and where.
    public required List<(long Node, double S)> Nodes { get; init; }
    /// Where real junctions are on it.
    public required List<double> Junctions { get; init; }
    /// Where existing switches' timbers are on it.
    public required List<(double From, double To)> Zones { get; init; }
    /// The lines it's made of, each over a stretch of it.
    public required List<(NetworkLine Line, double From, double To)> Parts { get; init; }
    /// Whether it stops at a junction (rather than the track's end) at either end.
    public bool EndsAtJunction { get; init; }

    public int Count => X.Length;
    public double Length => S[^1];

    /// The line under a place along it.
    public NetworkLine LineAt(double s) => Parts.FirstOrDefault(p => s >= p.From && s <= p.To).Line ?? (s < 0 ? Parts[0].Line : Parts[^1].Line);

    public static SidingRun Of(NetworkLine seed, TrackEditor tracks, IReadOnlyList<NetworkLine> lines)
    {
        // The rail lines ending at each node.
        var ends = new Dictionary<long, List<NetworkLine>>();
        foreach (var line in lines)
        {
            if (!line.Rail || line.Count < 2)
                continue;
            foreach (var (node, point) in line.Nodes)
                if (point == 0 || point == line.Count - 1)
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add(line);
                }
        }

        long? EndNode(NetworkLine line, bool last) =>
            line.Nodes.Where(n => n.Point == (last ? line.Count - 1 : 0)).Select(n => (long?)n.Node).FirstOrDefault();

        var parts = new LinkedList<(NetworkLine Line, bool Reversed)>();
        parts.AddFirst((seed, false));
        var seen = new HashSet<NetworkLine> { seed };
        var atJunction = false;
        for (var forward = 0; forward < 2; forward++)
        {
            while (true)
            {
                var (edge, reversed) = forward == 0 ? parts.Last!.Value : parts.First!.Value;
                if (EndNode(edge, (forward == 0) != reversed) is not { } node)
                    break;
                if (tracks.Has(node) && tracks.IsJunction(node))
                {
                    atJunction = true;
                    break;
                }

                var others = ends.TryGetValue(node, out var list) ? list.Where(l => l != edge).ToList() : [];
                if (others.Count != 1 || !seen.Add(others[0]))
                    break;
                var next = others[0];
                // Forwards, the next line should start at the node; backwards, end at it.
                var nextReversed = forward == 0 ? EndNode(next, false) != node : EndNode(next, true) != node;
                if (forward == 0)
                    parts.AddLast((next, nextReversed));
                else
                    parts.AddFirst((next, nextReversed));
            }
        }

        var (xs, zs, ss) = (new List<double>(), new List<double>(), new List<double>());
        var structures = new List<Structure>();
        var nodes = new List<(long, double)>();
        var zones = new List<(double, double)>();
        var spans = new List<(NetworkLine, double, double)>();
        foreach (var (line, reversed) in parts)
        {
            var offset = ss.Count > 0 ? ss[^1] : 0.0;
            var total = (double)line.S[^1];
            double Run(double lineS) => offset + (reversed ? total - lineS : lineS);
            for (var k = 0; k < line.Count; k++)
            {
                var i = reversed ? line.Count - 1 - k : k;
                if (k == 0 && xs.Count > 0)
                    continue;
                xs.Add(line.X[i]);
                zs.Add(line.Z[i]);
                ss.Add(Run(line.S[i]));
                // (The segment just added: from the point before it along the run.)
                if (xs.Count > 1)
                    structures.Add(line.Structures.Length == 0 ? Structure.Ground
                        : line.Structures[Math.Clamp(reversed ? i : i - 1, 0, line.Structures.Length - 1)]);
            }

            foreach (var (node, point) in line.Nodes)
                nodes.Add((node, Run(line.S[point])));
            foreach (var zone in line.TurnoutZones)
            {
                var (a, b) = (Run(zone.Timbers.From), Run(zone.Timbers.To));
                zones.Add((Math.Min(a, b), Math.Max(a, b)));
            }

            spans.Add((line, offset, offset + total));
        }

        var junctions = nodes.Where(n => tracks.Has(n.Item1) && tracks.IsJunction(n.Item1)).Select(n => n.Item2).ToList();
        return new SidingRun
        {
            X = [.. xs], Z = [.. zs], S = [.. ss], Structures = [.. structures], Nodes = nodes, Junctions = junctions, Zones = zones,
            Parts = spans, EndsAtJunction = atJunction,
        };
    }
}
