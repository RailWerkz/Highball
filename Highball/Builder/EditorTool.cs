using Brutal.ImGuiApi;
using Highball.World;

namespace Highball.Builder;

/// A tool in the editor's left hand: worked each frame it's in hand, drawn over the world, and given the side panel.
public interface IEditorTool
{
    /// Each frame it's in hand, after the camera's moved.
    void Update(float dt);

    /// Over the world, while it's in hand.
    void Draw(ImDrawListPtr draw);

    /// Its cards in the side panel.
    void Panel(bool imperial);

    /// Esc: lets go of whatever's under way. False when there was nothing, so Esc means something else.
    bool Escape();

    /// The footer's keys for it.
    (string Key, string Action)[] Hints { get; }
}

/// What the editor gives its tools: the route as loaded, the camera on the screen, the ground under the pointer, and
/// the undo history.
public sealed class ToolContext(RouteWorld world, CameraView view, Func<bool> blocked, Func<double3?> hit, Action<object?> record,
    Func<bool> topDown, Func<double> reach)
{
    public TerrainSource Source => world.Source!;
    public Terrain Terrain => world.Terrain!;
    public CameraView View => view;
    public Camera Camera => view.Camera;

    /// The pointer's over a window, the menu bar or the footer, not the world.
    public bool Blocked => blocked();

    /// The ground under the pointer, with a tool in hand.
    public double3? Hit => hit();

    /// Seen straight down (the 2D Editor).
    public bool TopDown => topDown();

    /// How far round the camera there's anything to draw, metres.
    public double Reach => reach();

    /// Keeps an edit (of any kind) for undoing.
    public void Record(object? edit) => record(edit);

    /// Where a point on the ground (a little over it) is on the screen.
    public float2? ProjectGround(double x, double z) => view.Project(x, Source.HeightAt(x, z) + 0.6, z);

    /// The ground in a rectangle of grid cells changed: its patches built again.
    public void Invalidate((int X0, int Z0, int X1, int Z1)? rect)
    {
        if (rect is { } r)
            Terrain.Invalidate(r.X0, r.Z0, r.X1, r.Z1);
    }
}
