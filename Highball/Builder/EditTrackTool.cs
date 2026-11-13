using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Edit Track tool: click a control point to select it (Shift adds or takes it away), drag out a box over the ground
/// to select every point in it (Shift adds them), drag a selected point to move them all, Ctrl+click a track to put a new
/// point in it. L straightens the selection, K smooths it, I simplifies it, J joins the ways it touches, U splits the
/// track at a point and Delete takes its points out.
public sealed class EditTrackTool(ToolContext context) : IEditorTool
{
    private long? _hover;
    private long? _grabbed;
    private (double X, double Z)? _grabFrom;
    private (double X, double Z)? _dragOffset;
    private float2? _boxFrom;

    /// With no control point under the pointer: the place on a track it's over, where Ctrl+click puts a new point.
    private (long Way, int Segment, double X, double Z)? _insertAt;

    /// The selected control points (the class and bed panels apply to them too).
    public HashSet<long> Selection { get; } = [];

    /// The selection's tools, for the menu and the side panel: name, key, and what it does.
    public static readonly (string Name, string Key, Func<TrackEditor, IReadOnlySet<long>, NetworkEdit?> Act)[] SelectionTools =
    [
        ("Straighten", "L", (t, s) => t.Straighten(s)),
        ("Smooth", "K", (t, s) => t.Smooth(s)),
        ("Simplify", "I", (t, s) => t.Simplify(s)),
        ("Join", "J", (t, s) => t.Join(s)),
        ("Split", "U", (t, s) => t.Split(s)),
        ("Delete", "Del", (t, s) => t.DeleteMany(s)),
    ];

    /// Not in hand, or the pointer's off the world: nothing's under it.
    public void Idle()
    {
        _hover = null;
        _insertAt = null;
    }

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        var camera = context.Camera;
        var mouse = Input.MousePosition;
        if (_grabbed is null)
            _hover = tracks.NodeAt(mouse, camera.X, camera.Z, context.ProjectGround);
        _insertAt = _hover is null && _grabbed is null && _boxFrom is null ? tracks.SegmentAt(mouse, camera.X, camera.Z, context.ProjectGround) : null;

        var shift = Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift);
        var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
        if (Input.MouseDown(GlfwMouseButton.Left) && ctrl && _hover is null && _insertAt is { } at)
        {
            // A new point in the track, selected, ready to drag.
            if (tracks.InsertPoint(at.Way, at.Segment, at.X, at.Z, out var made) is { } inserted)
            {
                context.Commit(inserted);
                Selection.Clear();
                Selection.Add(made);
                _insertAt = null;
            }

            return;
        }

        if (Input.MouseDown(GlfwMouseButton.Left))
        {
            if (_hover is { } node && shift)
            {
                if (!Selection.Remove(node))
                    Selection.Add(node);
            }
            else if (_hover is { } grabbed)
            {
                if (!Selection.Contains(grabbed))
                {
                    Selection.Clear();
                    Selection.Add(grabbed);
                }

                _grabbed = grabbed;
                _grabFrom = tracks.PositionOf(grabbed);
            }
            else
                _boxFrom = mouse;
        }

        if (_grabbed is not null && context.Hit is { } hit && _grabFrom is { } from)
            _dragOffset = (hit.X - from.X, hit.Z - from.Z);

        if (!Input.Mouse(GlfwMouseButton.Left))
        {
            if (_grabbed is not null && _dragOffset is { } o && Math.Abs(o.X) + Math.Abs(o.Z) > 0.01)
                context.Commit(tracks.MoveMany(Selection.ToDictionary(n => n, n => (tracks.PositionOf(n).X + o.X, tracks.PositionOf(n).Z + o.Z))));
            _grabbed = null;
            _grabFrom = null;
            _dragOffset = null;

            if (_boxFrom is { } start)
            {
                if (!shift)
                    Selection.Clear();
                if (MathF.Abs(mouse.X - start.X) + MathF.Abs(mouse.Y - start.Y) > 4f)
                    Selection.UnionWith(tracks.NodesIn(start, mouse, camera.X, camera.Z, context.ProjectGround));
                _boxFrom = null;
            }
        }

        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Delete))
            context.Commit(Selection.Count > 0 ? tracks.DeleteMany(Selection) : _hover is { } doomed ? tracks.Delete(doomed) : null);
        if (Input.KeyDown(GlfwKey.L))
            context.Commit(tracks.Straighten(Selection));
        if (Input.KeyDown(GlfwKey.K))
            context.Commit(tracks.Smooth(Selection));
        if (Input.KeyDown(GlfwKey.I))
            context.Commit(tracks.Simplify(Selection));
        if (Input.KeyDown(GlfwKey.J))
            context.Commit(tracks.Join(Selection));
        if (Input.KeyDown(GlfwKey.U))
            Split();
    }

    /// Unjoins track at the one point selected, which isn't selected after: there are two ends there now.
    public void Split()
    {
        var tracks = context.Tracks;
        var made = tracks.Split(Selection);
        context.Refused = tracks.SplitRefused;
        if (made is null)
            return;
        context.Commit(made);
        Selection.Clear();
    }

    /// The control points, the selection and any drag; where Ctrl+click would put a point; the box being dragged out.
    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        context.Tracks.Draw(draw, camera.X, camera.Z, context.ProjectGround, _hover, Selection, _dragOffset);
        // Where Ctrl+click would put a new point: a ring on the track under the pointer, lit while Ctrl's held.
        if (_insertAt is { } at && context.ProjectGround(at.X, at.Z) is { } spot)
        {
            var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
            draw.AddCircleFilled(spot, S(4f), ctrl ? Rgba.White : Rgba.White.WithAlpha(0.55f), 16);
            draw.AddCircle(spot, S(8f), (ctrl ? Accent : Rgba.White).WithAlpha(ctrl ? 1f : 0.45f), 20, S(1.5f));
        }

        if (_boxFrom is { } from)
        {
            var to = Input.MousePosition;
            var (min, max) = (new float2(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y)), new float2(MathF.Max(from.X, to.X), MathF.Max(from.Y, to.Y)));
            draw.AddRectFilled(min, max, Rgba.White.WithAlpha(0.06f));
            draw.AddRect(min, max, Rgba.White.WithAlpha(0.6f), 0f, ImDrawFlags.None, 1f);
        }
    }

    /// The selection, and what can be done to it.
    public void Panel(bool imperial)
    {
        Kit.BeginCard("Selection", $"{Selection.Count} point{(Selection.Count == 1 ? "" : "s")}");
        var tools = SelectionTools;
        for (var i = 0; i < tools.Length; i += 2)
        {
            var count = Math.Min(2, tools.Length - i);
            var labels = new string[count];
            var enabled = new bool[count];
            for (var k = 0; k < count; k++)
                (labels[k], enabled[k]) = (tools[i + k].Name, Selection.Count > 0);
            var hit = Kit.Buttons($"selection{i}", labels, enabled);
            if (hit >= 0)
                context.Commit(tools[i + hit].Act(context.Tracks, Selection));
        }

        Kit.EndCard();
    }

    public bool Escape()
    {
        if (Selection.Count == 0)
            return false;
        Selection.Clear();
        return true;
    }

    public (string Key, string Action)[] Hints =>
        [("LMB", "Select, drag"), ("SHIFT", "Add"), ("CTRL+LMB", "Insert point"), ("L", "Straighten"), ("K", "Smooth"), ("I", "Simplify"), ("J", "Join"), ("U", "Split"), ("DEL", "Delete")];
}
