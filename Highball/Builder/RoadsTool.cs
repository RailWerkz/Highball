using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Roads tool: roads drawn straight, curved or freehand (RoadTool), joined to whatever they land on, or removed.
public sealed class RoadsTool(ToolContext context) : IEditorTool
{
    private long? _hover;
    private LayPoint? _next;

    public RoadTool Road { get; } = new();

    public void Update(float dt)
    {
        _hover = null;
        _next = null;
        if (context.Blocked || context.Hit is not { } hit)
            return;

        var roads = context.Roads;
        var (x, z) = (hit.X, hit.Z);
        var reach = context.PickReach(x, z);
        if (Road.Mode == RoadMode.Remove)
        {
            if (roads.Nearest(x, z, reach, RoadEditor.IsRoad) is { } under)
            {
                _hover = under.Way;
                if (Input.MouseDown(GlfwMouseButton.Left))
                    context.Commit(roads.Remove(under.Way));
            }

            return;
        }

        if ((Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift)) && Road.Mode == RoadMode.Straight)
            (x, z) = Road.Stepped(x, z);

        // The control point of a curve stands free; everything else joins what it lands on.
        _next = Road.Mode == RoadMode.Curved && Road.From is not null && Road.Control is null ? new LayPoint(x, z) : roads.Snap(x, z, reach);
        if (Input.MouseDown(GlfwMouseButton.Left) && Road.Click(_next.Value) is { } points)
            context.Commit(roads.Lay(points, RoadTool.Kinds[Road.Kind]));
    }

    /// Lays a road through scripted clicks, each snapped as a click would be.
    public void LayScripted(int kind, RoadMode mode, IEnumerable<(double X, double Z)> clicks)
    {
        Road.Clear();
        (Road.Kind, Road.Mode) = (kind, mode);
        foreach (var (x, z) in clicks)
            if (Road.Click(context.Roads.Snap(x, z, 5.0)) is { } laid)
                context.Commit(context.Roads.Lay(laid, RoadTool.Kinds[Road.Kind]));
    }

    public void Draw(ImDrawListPtr draw)
    {
        Road.Draw(draw, context.ProjectGround, _next, S(1f));
        if (_hover is { } doomed && context.Roads.Shape(doomed) is { } shape)
            context.DrawPicked(draw, shape.X, shape.Z, Stop);
    }

    /// How it draws, and what.
    public void Panel(bool imperial)
    {
        var modes = new[] { RoadMode.Straight, RoadMode.Curved, RoadMode.Freeform, RoadMode.Remove };
        var current = Array.IndexOf(modes, Road.Mode);
        var pick = ToolPanel.Mode("roadmode", ["Straight", "Curved", "Freeform", "Remove"], current, 2);
        if (pick != current && pick >= 0)
        {
            Road.Mode = modes[pick];
            Road.Clear();
        }

        if (Road.Mode == RoadMode.Remove)
            return;
        Kit.BeginCard("Road");
        for (var i = 0; i < RoadTool.Kinds.Length; i++)
            if (ToolPanel.Item($"roadkind{i}", ToolIcon.Road, RoadTool.Kinds[i].Name, null, Road.Kind == i))
                Road.Kind = i;
        Kit.EndCard();
    }

    public bool Escape()
    {
        if (!Road.Busy)
            return false;
        Road.Clear();
        return true;
    }

    public (string Key, string Action)[] Hints => Road.Mode == RoadMode.Remove
        ? [("LMB", "Remove road"), ("CTRL+Z", "Undo")]
        : [("LMB", "Next point"), ("SHIFT", "15 degree steps"), ("ESC", "Let go"), ("CTRL+Z", "Undo")];
}
