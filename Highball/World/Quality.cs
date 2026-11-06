using RailWerkz.Core.Settings;

namespace Highball.World;

/// What the Quality setting (0 low, 1 medium, 2 high, 3 ultra) buys. High is the game as it was tuned.
public static class Quality
{
    private static readonly float[] TerrainDetails = [45f, 58f, 70f, 90f];
    private static readonly int[] ShadowSizes = [1024, 1536, 2048, 4096];
    private static readonly float[] PlantReaches = [0.6f, 0.8f, 1f, 1.35f];

    private static int Level => Math.Clamp(Settings.Current.Quality, 0, 3);

    /// How many vertex spacings off the ground stays at each level of detail (Terrain.DetailFactor).
    public static float TerrainDetail => TerrainDetails[Level];

    /// The sun's shadow map, per side of each cascade.
    public static int ShadowSize => ShadowSizes[Level];

    /// How far plants and rocks reach, as a share of what they were tuned to.
    public static float PlantReach => PlantReaches[Level];
}
