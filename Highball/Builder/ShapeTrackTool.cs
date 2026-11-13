using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Shape Track tool: a solved line's handles dragged, straightened (L) or eased into a smooth curve (K), each change
/// kept with the route as a hand-shaped line (TrackShapeEdit) and undone like any edit. ShapeTool does the shaping.
internal sealed class ShapeTrackTool(ToolContext context) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    public ShapeTool Shape { get; } = new();

    public void Update(float dt)
    {
        var source = context.Source;
        if (source.Lines is not { } lines)
            return;
        var camera = context.Camera;
        var blocked = context.Blocked;
        var keys = !Keyboard.KeysBlocked && !blocked;
        var change = Shape.Update(lines, Input.MousePosition, blocked || context.Hit is not { } hit ? null : (hit.X, hit.Z), context.Project, S(1f),
            camera.X, camera.Z, context.Reach,
            !blocked && Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left),
            Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift), Input.Key(GlfwKey.LeftAlt) || Input.Key(GlfwKey.RightAlt),
            keys && Input.KeyDown(GlfwKey.Delete), keys && Input.KeyDown(GlfwKey.L), keys && Input.KeyDown(GlfwKey.K), context.Rebuilding);
        if (Shape.Refused is { } refused)
            (context.Refused, Shape.Refused) = (refused, null);
        if (change is { } made)
            Commit(made.Line, made.Shape);
    }

    /// A line's shape set by hand (null: back to as solved), kept for undoing, and the track laid out again.
    public void Commit(NetworkLine line, Alignment? shape)
    {
        var source = context.Source;
        var edit = new TrackShapeEdit(line.Key, source.TrackEdits.Get(line.Key), shape);
        source.TrackEdits.Set(line.Key, shape);
        context.Record(edit);
        context.RebuildNetwork();
    }

    public void Draw(ImDrawListPtr draw)
    {
        if (context.Source.Lines is { } lines)
            Shape.Draw(draw, lines, context.Project, S(1f));
    }

    public void Panel(bool imperial)
    {
        var source = context.Source;
        if (source.Lines is not { } lines)
            return;
        if (Shape.LineIn(lines) is not { } line)
        {
            ToolPanel.Hint("Click a track.");
            return;
        }

        string Length(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{metres:0.#} m");
        var shaped = source.TrackEdits.Has(line.Key);
        Kit.BeginCard("Line", shaped ? "Shaped by hand" : "As solved");
        var stats = new List<Stat> { new("Length", Length(line.S[^1])) };
        if (Shape.Preview is { } preview)
        {
            stats.Add(new Stat("Tightest curve", preview.Radius < double.MaxValue ? Length(preview.Radius) : "Straight"));
            stats.Add(new Stat("Off the map", Length(preview.OffMap)));
        }

        Kit.Stats([.. stats], 2);
        if (Kit.Button("backtosolved", "Back to Solved", 0f, ButtonKind.Secondary, shaped && !context.Rebuilding))
            Commit(line, null);
        Kit.EndCard();

        // How far K's smooth curve may move the track for smoothness.
        Kit.BeginCard("Smooth Curve", "Shift-click two handles, then K");
        var factor = imperial ? FeetPerMeter : 1f;
        var hold = (float)TrackShaping.EaseHold * factor;
        if (ToolPanel.Value("easehold", imperial ? "Leeway (ft)" : "Leeway (m)", ref hold, 0.05f * factor, 3f * factor, "0.00", log: true))
            TrackShaping.EaseHold = Math.Clamp(hold / factor, 0.05, 3.0);
        Kit.EndCard();
    }

    public bool Escape() => false;

    public (string Key, string Action)[] Hints =>
        [("LMB", "Select, drag handle"), ("SHIFT+LMB", "Mark handles"), ("L", "Straighten"), ("K", "Smooth curve"), ("DEL", "Remove handle"), ("ALT", "No snap"), ("CTRL+Z", "Undo")];
}
