namespace Highball.World;

/// One volume of cloud, in metres over the route's ground. Tiles are the noise's size in metres, and must all divide
/// Clouds.NoisePeriod (2,520 km) so nothing jumps when the camera's place in the noise wraps.
public sealed record CloudLayerShape(float Base, float Top, float Coverage, float Density, float TileX, float TileZ, float ShapeTile, float DetailTile)
{
    /// How much of where clouds are comes from honeycomb cells rather than smooth blobs, 0 to 1.
    public float Cells { get; init; }
    /// How slowly a cloud thickens from its edge to its middle: small makes cliffs, large makes domes.
    public float Ramp { get; init; } = 0.3f;
    /// How deep the billows bite, and how hard the edge they leave.
    public float Erosion { get; init; } = 0.85f;
    public float Sharpness { get; init; } = 0.35f;
    /// How much skylight reaches it: low for a thick layer with more cloud above.
    public float Ambient { get; init; } = 0.8f;
    /// How much broad patches of cloud and clear sky show over the single clouds, 0 to 1.
    public float Banks { get; init; } = 0.3f;
    /// How far the top leans over the bottom, in metres.
    public float Shear { get; init; } = 300f;
    /// Where a cloud's top falls away, as parts of the layer's height: at the edge of a cloud, and how much higher in its middle.
    public float TopStart { get; init; } = 0.05f;
    public float TopReach { get; init; } = 0.6f;
    /// How much of the sun gets into it: a sheet seen from under loses most of it on the way through.
    public float SunGain { get; init; } = 1f;
    /// How much of the layer's height its base fades in over: small is a flat base.
    public float BottomFade { get; init; } = 0.05f;
    /// How far the top spreads into an anvil, 0 to 1.
    public float Anvil { get; init; }
}

public enum HighCloud
{
    None = -1,
    Cirrus = 0,
    Cirrostratus = 1,
    Cirrocumulus = 2,
}

/// A sky's worth of cloud, from the ten basic types and a few that are usually seen together. There's room for two volumes, and a
/// thin ice cloud high over them. High clouds are thin enough to draw as a sheet, which costs almost nothing.
public sealed record CloudType(string Id, string Label, string Group, CloudLayerShape? Lower = null, CloudLayerShape? Upper = null, HighCloud High = HighCloud.None)
{
    public float HighHeight { get; init; } = 9000f;
    public float HighOpacity { get; init; } = 0.6f;
    /// How far this much cloud greys the sky, 0 to 1, at the amount a type usually has.
    public float Overcast { get; init; }

    public float OvercastAt(float amount) => Id == "Clear" ? 0f : Math.Clamp(Overcast * MathF.Min(amount * 2f, 1f), 0f, 1f);

    private static readonly CloudLayerShape Cumulus = new(1100f, 3000f, 0.42f, 0.025f, 9000f, 9000f, 3000f, 700f)
    {
        Cells = 0.25f, Ramp = 0.3f, Banks = 0.4f, Ambient = 0.85f, Shear = 250f, TopStart = 0.04f, TopReach = 0.55f, BottomFade = 0.04f,
    };

    private static readonly CloudLayerShape Cumulonimbus = new(900f, 11000f, 0.28f, 0.02f, 60000f, 60000f, 8000f, 1800f)
    {
        Cells = 0.1f, Ramp = 0.45f, Banks = 0.2f, Ambient = 0.55f, Shear = 1200f, TopStart = 0.15f, TopReach = 0.85f, BottomFade = 0.015f, Anvil = 1f,
    };

    private static readonly CloudLayerShape Stratocumulus = new(700f, 1500f, 0.75f, 0.02f, 4000f, 6000f, 1600f, 500f)
    {
        Cells = 0.75f, Ramp = 0.25f, Banks = 0.25f, Ambient = 0.6f, Shear = 100f, TopStart = 0.45f, TopReach = 0.5f, BottomFade = 0.1f,
    };

    private static readonly CloudLayerShape Stratus = new(250f, 650f, 1.15f, 0.006f, 12000f, 12000f, 6000f, 1500f)
    {
        SunGain = 0.3f, Ramp = 0.5f, Banks = 0.2f, Ambient = 0.5f, Erosion = 0.35f, Sharpness = 0.1f, Shear = 0f, TopStart = 0.7f, TopReach = 0.3f, BottomFade = 0.25f,
    };

    private static readonly CloudLayerShape Nimbostratus = new(600f, 3600f, 1.2f, 0.012f, 20000f, 20000f, 5000f, 1200f)
    {
        SunGain = 0.5f, Ramp = 0.5f, Banks = 0.25f, Ambient = 0.42f, Erosion = 0.4f, Sharpness = 0.15f, Shear = 0f, TopStart = 0.7f, TopReach = 0.3f, BottomFade = 0.12f,
    };

    private static readonly CloudLayerShape Altostratus = new(3000f, 4200f, 1.2f, 0.0016f, 24000f, 24000f, 8000f, 1800f)
    {
        SunGain = 0.35f, Ramp = 0.5f, Banks = 0.3f, Ambient = 0.6f, Erosion = 0.25f, Sharpness = 0.1f, Shear = 0f, TopStart = 0.7f, TopReach = 0.3f, BottomFade = 0.2f,
    };

    private static readonly CloudLayerShape Altocumulus = new(3400f, 4000f, 0.62f, 0.012f, 2400f, 3600f, 1000f, 300f)
    {
        Cells = 0.85f, Ramp = 0.2f, Banks = 0.45f, Ambient = 0.85f, Shear = 60f, TopStart = 0.3f, TopReach = 0.6f, BottomFade = 0.12f,
    };

    public static readonly IReadOnlyList<CloudType> All =
    [
        new("Clear", "Clear sky", ""),
        new("FairWeather", "Fair weather (cumulus, cirrus)", "Mixed", Cumulus, High: HighCloud.Cirrus) { HighOpacity = 0.45f },
        new("Layered", "Layered (stratocumulus, altostratus)", "Mixed", Stratocumulus, Altostratus) { Overcast = 0.7f },
        new("Storm", "Storm (cumulonimbus, cirrostratus)", "Mixed", Cumulonimbus, High: HighCloud.Cirrostratus) { Overcast = 0.25f, HighOpacity = 0.3f },

        new("Cirrus", "Cirrus (Ci)", "High", High: HighCloud.Cirrus) { HighOpacity = 0.5f },
        new("Cirrocumulus", "Cirrocumulus (Cc)", "High", High: HighCloud.Cirrocumulus) { HighHeight = 7500f, HighOpacity = 0.8f },
        new("Cirrostratus", "Cirrostratus (Cs)", "High", High: HighCloud.Cirrostratus) { HighHeight = 8000f, HighOpacity = 0.45f, Overcast = 0.25f },

        new("Altocumulus", "Altocumulus (Ac)", "Middle", Altocumulus) { Overcast = 0.15f },
        new("Altostratus", "Altostratus (As)", "Middle", Altostratus) { Overcast = 0.55f },
        new("Nimbostratus", "Nimbostratus (Ns)", "Middle", Nimbostratus) { Overcast = 1f },

        new("Stratus", "Stratus (St)", "Low", Stratus) { Overcast = 0.9f },
        new("Stratocumulus", "Stratocumulus (Sc)", "Low", Stratocumulus) { Overcast = 0.45f },
        new("Cumulus", "Cumulus (Cu)", "Low", Cumulus),
        new("Cumulonimbus", "Cumulonimbus (Cb)", "Low", Cumulonimbus) { Overcast = 0.2f },
    ];

    /// By id, or fair weather for one that isn't known.
    public static CloudType Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[1];
}
