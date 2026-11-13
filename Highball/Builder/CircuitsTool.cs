using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

/// The Track Circuits tool: the route's circuits and their insulated joints (CircuitEditor), picked, added, moved and
/// removed with the pointer.
public sealed class CircuitsTool(ToolContext context, CircuitEditor editor) : IEditorTool
{
    /// The route's circuits, kept whatever's in hand (built as the track changes, saved and undone with the rest).
    public CircuitEditor Editor => editor;

    public void Update(float dt)
    {
        if (context.Blocked)
            return;
        var camera = context.Camera;
        var pixel = 2.0 * Math.Tan(camera.FieldOfView * 0.5) / context.View.Size.Y;
        var metresPerPixel = context.Hit is { } h
            ? Math.Sqrt((h.X - camera.X) * (h.X - camera.X) + (h.Y - camera.Y) * (h.Y - camera.Y) + (h.Z - camera.Z) * (h.Z - camera.Z)) * pixel
            : 1.0;
        var edit = editor.Update(Input.MousePosition, context.Hit is { } g ? (g.X, g.Z) : null, metresPerPixel, context.ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left),
            Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift), Input.KeyDown(GlfwKey.Delete) && !Keyboard.KeysBlocked);
        context.Record(edit);
    }

    /// The circuits in their colours, in place of the track overlay; from high above, without their joints.
    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        editor.Draw(draw, camera.X, camera.Z, context.Reach, context.ProjectGround,
            !context.TopDown || camera.Y - context.Source.HeightAt(camera.X, camera.Z) < 5000.0);
    }

    public void Panel(bool imperial) => context.Record(editor.Panel(imperial));

    public bool Escape() => false;

    public (string Key, string Action)[] Hints =>
        [("LMB", "Select circuit"), ("SHIFT+LMB", "Add joint"), ("DRAG", "Move joint"), ("DEL", "Remove joint"), ("CTRL+Z", "Undo")];
}
