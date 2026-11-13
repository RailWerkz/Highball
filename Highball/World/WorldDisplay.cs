using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Highball.World;

/// What a screen showing the world holds: the renderer, its lighting (sky, sun, weather: Lighting), and the scene the
/// screen fills in each frame. Render draws the scene with the lighting applied; Composite lays the result on the screen.
public sealed class WorldDisplay : IDisposable
{
    public WorldRenderer Renderer { get; } = new();
    public Lighting Lighting { get; }

    /// Filled in by the screen before Render.
    public FrameScene Scene { get; } = new();

    /// The sun and sky worked out for a place on the globe (degrees) and the route's grid north.
    public WorldDisplay(double latitude = 34.9, double longitude = -117.09, double gridNorth = 0.0) =>
        Lighting = new Lighting(Renderer, latitude, longitude, gridNorth);

    /// Draws the scene into the renderer's picture. Outside any render pass.
    public void Render(CommandBuffer cmd, int width, int height)
    {
        Lighting.Apply();
        Renderer.Render(cmd, new int2(width, height), Scene);
    }

    /// Lays the picture on the screen. Inside the present pass, before the interface.
    public void Composite(CommandBuffer cmd) => Renderer.Composite(cmd);

    public void Dispose() => Renderer.Dispose();
}
