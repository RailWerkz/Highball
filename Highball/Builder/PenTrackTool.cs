using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Pen tool: track drawn point by point (PenTool), each snapped to a control point or a track under the pointer, a
/// drag making a curve; Enter lays it, Backspace takes the last point back.
public sealed class PenTrackTool(ToolContext context, IReadOnlySet<long> selection) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    private long? _hover;

    /// Where the next point would go, snapped.
    private LayPoint? _next;

    /// The next point is snapped onto a guide (the mapped track drawn on the ground).
    private bool _onGuide;

    /// The place on a guide under the pointer, within a few pixels; none with Alt held (to put a point beside one).
    private GuidePlace? GuideAt(float2 mouse)
    {
        if (context.Guides is not { } guides || Input.Key(GlfwKey.LeftAlt) || Input.Key(GlfwKey.RightAlt))
            return null;
        var camera = context.Camera;
        return guides.PlaceAt(mouse, context.ProjectGround, camera.X, camera.Z, context.Reach, S(12f));
    }

    public PenTool Pen { get; } = new();

    /// What's drawn so far: its length, tightest curve and the ground's grade along it.
    public PenTool.Measure? Measure { get; private set; }

    /// Where the path would run on to if the pointer were clicked now.
    private (double X, double Z)? Preview => !Pen.Busy && _next is { } next ? (next.X, next.Z) : null;

    /// Not in hand, or the pointer's off the world.
    public void Idle()
    {
        _hover = null;
        Measure = null;
        Pen.Straighten(null, context.Tracks, false);
    }

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        var camera = context.Camera;
        var mouse = Input.MousePosition;
        var hit = context.Hit;
        _hover = tracks.NodeAt(mouse, camera.X, camera.Z, context.ProjectGround);
        var guide = GuideAt(mouse);
        _next = _hover is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : tracks.SegmentAt(mouse, camera.X, camera.Z, context.ProjectGround) is { } segment
                ? new LayPoint(segment.X, segment.Z, Split: (segment.Way, segment.Segment))
                : guide is { } onGuide ? new LayPoint(onGuide.X, onGuide.Z)
                : hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;
        _onGuide = _next is { Node: null, Split: null } n && guide is { } g && g.X == n.X && g.Z == n.Z;
        var straight = Pen.Straighten(_next, tracks, Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift), context.Source.Lines);
        _onGuide &= straight == _next;
        _next = straight;

        Pen.Update(mouse, _next, hit is { } h ? (h.X, h.Z) : null, context.ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
        if (Input.KeyDown(GlfwKey.Backspace))
            Pen.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter) && Pen.Count >= 2)
        {
            context.Commit(tracks.Lay(PenTool.Thin(Eased(Pen.Points())), lead: false));
            Pen.Clear();
        }

        Measure = PenTool.Measured(Pen.Points(Preview), context.Source.HeightAt);
    }

    /// How far into the drawn track its ends are eased onto the track they leave or join, metres.
    private const double EaseLength = 80.0;

    /// The drawn track with each end that's on a track's loose end eased on to it (JoinCurve.EaseEnds), so it carries on
    /// the way that track's rails run and curve, rather than leaving at whatever angle the first click made.
    private List<LayPoint> Eased(List<LayPoint> points)
    {
        if (points.Count < 2)
            return points;
        var tracks = context.Tracks;
        var lines = context.Source.Lines;
        JoinCurve.Pose? Pose(LayPoint p) => p.Node is { } node ? BuiltTrack.EndPose(lines, tracks, node, 30.0) : null;
        var start = Pose(points[0]);
        // (Arriving at a track's end, the path runs against the way that track runs out of it.)
        var end = Pose(points[^1]) is { } e ? e with { Heading = e.Heading + Math.PI, Curvature = -e.Curvature } : (JoinCurve.Pose?)null;
        if (start is null && end is null)
            return points;

        var eased = JoinCurve.EaseEnds([.. points.Select(p => (p.X, p.Z))], start, end, EaseLength, 5.0);
        var result = new List<LayPoint> { points[0] };
        for (var i = 1; i < eased.Count - 1; i++)
            result.Add(new LayPoint(eased[i].X, eased[i].Z));
        result.Add(points[^1]);
        return result;
    }

    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        context.Tracks.Draw(draw, camera.X, camera.Z, context.ProjectGround, _hover, selection, null);
        Pen.Draw(draw, context.ProjectGround, Preview);
        if (_onGuide && _next is { } snapped && !Pen.Busy && context.ProjectGround(snapped.X, snapped.Z) is { } at)
        {
            // Snapped onto the guide: a diamond on it.
            var r = S(7f);
            ReadOnlySpan<float2> corners = [new(0f, -r), new(r, 0f), new(0f, r), new(-r, 0f), new(0f, -r)];
            for (var i = 0; i < 4; i++)
                draw.AddLine(at + corners[i], at + corners[i + 1], Accent, S(2.5f));
        }
        context.Kinks.Update(context);
        context.Kinks.Draw(draw, context.ProjectGround);
        if (Measure is { } m && !context.Blocked)
            PenTool.Label(draw, Input.MousePosition, PenTool.Describe(m, Settings.Current.Units == UnitSystem.Imperial), Style.Text);
    }

    /// What's drawn so far: its points, length, tightest curve and the ground's grade along it.
    public void Panel(bool imperial)
    {
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
        context.Kinks.Panel();
        if (Measure is not { } m)
            return;
        Kit.BeginCard("Drawn", $"{Pen.Count} point{(Pen.Count == 1 ? "" : "s")}");
        Kit.Stats(
        [
            new Stat("Length", Length(m.Length)),
            new Stat("Tightest curve", double.IsInfinity(m.MinRadius) ? "Straight" : Length(m.MinRadius)),
            new Stat("Ground grade", string.Create(CultureInfo.InvariantCulture, $"{m.Grade * 100:0.00}%")),
            new Stat("Steepest", string.Create(CultureInfo.InvariantCulture, $"{m.SteepestGrade * 100:0.00}%")),
        ], 2);
        Kit.EndCard();
    }

    public bool Escape()
    {
        if (Pen.Count == 0)
            return false;
        Pen.Clear();
        return true;
    }

    public (string Key, string Action)[] Hints =>
        [("LMB", "Point"), ("DRAG", "Curve"), ("SHIFT", "Straight on"), ("ALT", "No guide snap"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")];
}
