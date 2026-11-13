namespace Highball.World;

/// How the outline round the driven and picked vehicles looks: their colours, and pixels per interface unit for its width.
public readonly record struct OutlineStyle(float4 Driven, float4 Picked, float Scale);

/// Everything the world renderer draws this frame, filled in by whatever screen is showing the world and handed to
/// WorldRenderer.Render. Plain data: the renderer reads it and keeps nothing of it, and the screen never reaches into the
/// renderer to set things up instead.
public sealed class FrameScene
{
    public Camera Camera { get; set; } = new();

    /// The ground, if there's a route loaded.
    public Terrain? Terrain { get; set; }

    /// The route's track, roads and lineside, if there's a route loaded.
    public NetworkMeshes? Network { get; set; }

    public IReadOnlyList<ModelDraw> Models { get; set; } = [];

    /// The vehicles to outline, and how.
    public IReadOnlyList<Outlined> Outlines { get; set; } = [];
    public OutlineStyle OutlineStyle { get; set; } = new(new float4(0.3f, 0.85f, 0.5f, 1f), new float4(0.35f, 0.6f, 1f, 1f), 1f);

    /// Lamps beyond those the network has (signals and light poles): a train's headlights, say.
    public IReadOnlyList<LocalLight> Lights { get; set; } = [];

    /// Exhaust smoke, and the heat haze over it.
    public IReadOnlyList<Trains.SmokeSegment> Smoke { get; set; } = [];
    public IReadOnlyList<Trains.HazeSource> Haze { get; set; } = [];
}
