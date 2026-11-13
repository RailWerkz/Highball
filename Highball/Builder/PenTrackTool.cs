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
    }

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        var camera = context.Camera;
        var mouse = Input.MousePosition;
        var hit = context.Hit;
        _hover = tracks.NodeAt(mouse, camera.X, camera.Z, context.ProjectGround);
        _next = _hover is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : tracks.SegmentAt(mouse, camera.X, camera.Z, context.ProjectGround) is { } segment
                ? new LayPoint(segment.X, segment.Z, Split: (segment.Way, segment.Segment))
                : hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;

        Pen.Update(mouse, _next, hit is { } h ? (h.X, h.Z) : null, context.ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
        if (Input.KeyDown(GlfwKey.Backspace))
            Pen.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter) && Pen.Count >= 2)
        {
            context.Commit(tracks.Lay(Pen.Points(), lead: false));
            Pen.Clear();
        }

        Measure = PenTool.Measured(Pen.Points(Preview), context.Source.HeightAt);
    }

    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        context.Tracks.Draw(draw, camera.X, camera.Z, context.ProjectGround, _hover, selection, null);
        Pen.Draw(draw, context.ProjectGround, Preview);
        if (Measure is { } m && !context.Blocked)
            PenTool.Label(draw, Input.MousePosition, PenTool.Describe(m, Settings.Current.Units == UnitSystem.Imperial), Style.Text);
    }

    /// What's drawn so far: its points, length, tightest curve and the ground's grade along it.
    public void Panel(bool imperial)
    {
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
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
        [("LMB", "Point"), ("DRAG", "Curve"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")];
}
