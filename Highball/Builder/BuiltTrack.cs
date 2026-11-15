using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// Reading the track as it's built (the rail lines, NetworkLines), rather than its control points: the built line is a spline
/// through them (or, for mapped track, fitted to straights and curves up to a metre off them), so which way it runs at an
/// end is what the rails do there, not the chord to the last control point.
public static class BuiltTrack
{
    /// At a track's loose end, which way the built rails run out of it and how they're curving, read off the last sample
    /// metres of them; from the control points if the line isn't built (yet). Null if it isn't a loose end.
    public static JoinCurve.Pose? EndPose(NetworkLines? lines, TrackEditor tracks, long node, double sample)
    {
        if (!tracks.Has(node) || !tracks.IsLooseEnd(node))
            return null;
        if (lines is not null)
            foreach (var line in lines.Lines)
            {
                if (!line.Rail || line.Mark || line.Count < 2)
                    continue;
                foreach (var (n, point) in line.Nodes)
                {
                    if (n != node || (point != 0 && point != line.Count - 1))
                        continue;
                    var back = new List<(double X, double Z)>();
                    var step = point == 0 ? 1 : -1;
                    var along = 0.0;
                    for (var i = point; i >= 0 && i < line.Count; i += step)
                    {
                        if (back.Count > 0)
                            along += Math.Sqrt((line.X[i] - back[^1].X) * (line.X[i] - back[^1].X) + (line.Z[i] - back[^1].Z) * (line.Z[i] - back[^1].Z));
                        back.Add((line.X[i], line.Z[i]));
                        if (along >= sample)
                            break;
                    }

                    // (The pose at the control point itself, where new track is joined, even if the line ends a hair off it.)
                    var (nx, nz) = tracks.PositionOf(node);
                    if (JoinCurve.Estimate(back) is { } built && Math.Abs(built.X - nx) + Math.Abs(built.Z - nz) < 2.0)
                        return built with { X = nx, Z = nz };
                }
            }

        return tracks.Behind(node, sample) is { } points ? JoinCurve.Estimate(points) : null;
    }
}

/// After track is laid, checks the built rails where it joins other track (its two ends) once the track's been laid out again:
/// a turn in a few metres there that the track either side doesn't make is a kink. Shown on the track and in the panel.
public sealed class KinkCheck
{
    /// Turns sharper than this at a join, beyond what the curve either side accounts for, are reported, degrees.
    public const double Threshold = 0.5;

    /// How far either side of the join the direction is read, metres.
    private const double Reach = 6.0;

    /// Gives up waiting for a rebuild after this long, seconds.
    private const double Patience = 60.0;

    private readonly List<long> _waiting = [];
    private DateTime _since;
    private bool _sawRebuild;

    /// What the last check found: where, and how sharp (degrees).
    public List<(double X, double Z, double Degrees)> Found { get; } = [];

    /// Whether it's still waiting for the track to be laid out again before it can look.
    public bool Checking => _waiting.Count > 0;

    /// New track laid: its ends (where it joins other track) are checked once it's built.
    public void Watch(NetworkEdit? edit, TrackEditor tracks)
    {
        if (edit is null)
            return;
        var ends = new List<long>();
        foreach (var (_, before, after) in edit.Ways)
            if (before is null && after is { Rail: true, Nodes.Length: >= 2 })
                ends.AddRange([after.Nodes[0], after.Nodes[^1]]);
        if (ends.Count == 0)
            return;
        Found.Clear();
        _waiting.Clear();
        _waiting.AddRange(ends.Distinct());
        (_since, _sawRebuild) = (DateTime.UtcNow, false);
    }

    /// Each frame: once the rebuild that follows the edit is done, looks at the built rails through each end.
    public void Update(ToolContext context)
    {
        if (_waiting.Count == 0)
            return;
        if (context.Rebuilding)
        {
            _sawRebuild = true;
            return;
        }

        if ((DateTime.UtcNow - _since).TotalSeconds > Patience)
        {
            _waiting.Clear();
            return;
        }

        if (!_sawRebuild || context.Source.Lines is not { } lines)
            return;

        var tracks = context.Tracks;
        foreach (var node in _waiting)
            if (tracks.Has(node) && !tracks.IsJunction(node) && Measure(lines, node) is { } kink && kink.Degrees > Threshold)
                Found.Add(kink);
        _waiting.Clear();
    }

    /// The kink at a node: through a line that runs on past it, the turn there less the turns a little either side (so a curve
    /// isn't a kink); where two lines end on it, the angle between the ways their ends run (each read with its curve).
    private static (double X, double Z, double Degrees)? Measure(NetworkLines lines, long node)
    {
        var ends = new List<JoinCurve.Pose>();
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Mark || line.Count < 3)
                continue;
            foreach (var (n, point) in line.Nodes)
            {
                if (n != node)
                    continue;
                if (point > 0 && point < line.Count - 1)
                {
                    double Turn(int at) => TurnAt(line, at);
                    var stride = Math.Max(1, (int)Math.Round(Reach / Math.Max(line.S[Math.Min(point + 1, line.Count - 1)] - line.S[point], 0.5)));
                    var here = Turn(point);
                    var aside = 0.5 * (Turn(point - stride) + Turn(point + stride));
                    if (double.IsNaN(here))
                        return null;
                    var excess = double.IsNaN(aside) ? Math.Abs(here) : Math.Abs(here - aside);
                    return (line.X[point], line.Z[point], excess * 180.0 / Math.PI);
                }

                var back = new List<(double X, double Z)>();
                var step = point == 0 ? 1 : -1;
                for (var i = point; i >= 0 && i < line.Count && (back.Count < 2 || Math.Abs(line.S[i] - line.S[point]) <= 20.0); i += step)
                    back.Add((line.X[i], line.Z[i]));
                if (JoinCurve.Estimate(back) is { } pose)
                    ends.Add(pose);
            }
        }

        if (ends.Count != 2)
            return null;
        // Each end's heading runs out of its line; straight through, they're opposite.
        var angle = ends[0].Heading - (ends[1].Heading + Math.PI);
        angle = Math.Atan2(Math.Sin(angle), Math.Cos(angle));
        return (ends[0].X, ends[0].Z, Math.Abs(angle) * 180.0 / Math.PI);
    }

    /// The turn of a line at a point, between the chords Reach metres back and on (radians); NaN too near its ends.
    private static double TurnAt(NetworkLine line, int at)
    {
        if (at <= 0 || at >= line.Count - 1)
            return double.NaN;
        int Find(double s, int from, int step)
        {
            var i = from;
            while (i + step >= 0 && i + step < line.Count && Math.Abs(line.S[i] - line.S[from]) < s)
                i += step;
            return i;
        }

        var (a, b) = (Find(Reach, at, -1), Find(Reach, at, 1));
        if (a == at || b == at)
            return double.NaN;
        var h0 = Math.Atan2(line.Z[at] - line.Z[a], line.X[at] - line.X[a]);
        var h1 = Math.Atan2(line.Z[b] - line.Z[at], line.X[b] - line.X[at]);
        var turn = h1 - h0;
        return Math.Atan2(Math.Sin(turn), Math.Cos(turn));
    }

    /// A ring and its angle on each kink found.
    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project)
    {
        foreach (var (x, z, degrees) in Found)
            if (project(x, z) is { } at)
            {
                draw.AddCircle(at, S(14f), Caution, 0, S(2.5f));
                PenTool.Label(draw, at, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Kink {degrees:0.0}°"), Caution);
            }
    }

    /// The panel's word on the last track laid.
    public void Panel()
    {
        if (Checking)
            ToolPanel.Hint("Checking the joins for kinks...");
        else if (Found.Count > 0)
            ToolPanel.Problem(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Kink at a join: {Found.Max(f => f.Degrees):0.0}°. The new track and the one it joins are built as two lines (their class or bed differ: set Track Class and Bed to Auto, or the same as that track), so their ends can meet at an angle. Undo and re-lay."));
    }
}
