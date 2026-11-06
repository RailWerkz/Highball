using Brutal.ImGuiApi;
using Brutal.VulkanApi;

namespace Highball.App;

/// One page of the game: the splash, a menu, the cab. The top of the stack updates and draws; screens under an
/// overlay (a pause menu, a dialog) still draw.
public abstract class Screen
{
    /// Screens under this one keep drawing (it only covers part of the picture).
    public virtual bool IsOverlay => false;

    /// Whether the 3D world is drawn behind this screen, and how.
    public virtual WorldView View => WorldView.None;

    /// Mouse captured for looking around.
    public virtual bool CapturesMouse => false;

    public virtual void Enter() { }
    public virtual void Exit() { }

    /// Back on top after the screen above it closed.
    public virtual void Resume() { }

    /// ImGui windows of the top screen's own (a main menu bar), drawn after the deck with ImGui's own style.
    public virtual void DrawWindows() { }

    public abstract void Update(float dt);

    /// Another screen is open over this one and shows its world: keeps that world up to date without taking any input.
    public virtual void UpdateBehind() { }

    /// Draws the screen's interface on the deck's draw list.
    public abstract void Draw(ImDrawListPtr draw, float2 size);
}

/// A screen that draws a world of its own behind it (driving, the route builder).
public interface IWorldScreen
{
    /// Records the world offscreen, before the present pass begins.
    void RenderWorld(CommandBuffer cmd, int width, int height);

    /// Puts it on the screen, inside the present pass, under the interface.
    void CompositeWorld(CommandBuffer cmd);
}

public enum WorldView
{
    None,
    /// The screen draws its own world (IWorldScreen).
    World,
}
