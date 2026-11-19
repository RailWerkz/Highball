namespace Highball.World;

/// A look for the finished picture, applied in the tonemap pass (tonemap.frag) before the filmic curve. Warmth moves white towards
/// orange (above 0) or blue; contrast is about mid grey; saturation is 1 for as is, 0 for black and white. Lift raises the shadows
/// towards a colour (linear RGB, as a share of mid grey: faded, or tinted blacks), and Vignette darkens the corners (0.1 a lens's own).
/// Hand-tuned here, not from LUT files.
public sealed record ColorGrade(string Id, string Label, float Warmth, float Contrast, float Saturation, float LiftR = 0f, float LiftG = 0f, float LiftB = 0f,
    float Vignette = 0.1f)
{
    public static readonly ColorGrade[] All =
    [
        new("Filmic", "Filmic", 0.012f, 1.08f, 1.05f),
        new("Neutral", "Neutral", 0f, 1f, 1f, Vignette: 0f),
        new("Golden", "Golden", 0.05f, 1.08f, 1.1f),
        new("Cool", "Cool", -0.04f, 1.05f, 0.95f),
        new("Faded", "Faded", 0.01f, 0.88f, 0.82f),
        // Rich old slide film: warm, punchy, saturated, deep blacks.
        new("Kodachrome", "Kodachrome", 0.035f, 1.15f, 1.22f, Vignette: 0.16f),
        // Strong colour and contrast, kept neutral.
        new("Vivid", "Vivid", 0.005f, 1.12f, 1.3f),
        // Grey day: cool, soft, muted.
        new("Overcast", "Overcast", -0.03f, 0.92f, 0.8f, 0.01f, 0.012f, 0.016f),
        // Silver kept in the print: hard contrast, little colour.
        new("BleachBypass", "Bleach bypass", 0f, 1.25f, 0.5f, Vignette: 0.18f),
        // Warm highlights over teal shadows, as in films.
        new("TealOrange", "Teal and orange", 0.04f, 1.1f, 1.1f, 0f, 0.022f, 0.03f),
        // An old print: warm, washed out, its blacks gone brown.
        new("Vintage", "Vintage", 0.06f, 0.9f, 0.6f, 0.04f, 0.028f, 0.012f, 0.22f),
        // Brown-toned black and white.
        new("Sepia", "Sepia", 0.09f, 1.05f, 0.08f, 0.03f, 0.018f, 0.006f, 0.2f),
        // Black and white, hard.
        new("Noir", "Noir", 0f, 1.25f, 0f, Vignette: 0.25f),
        // Black and white, soft, like an old railway photo.
        new("Archive", "Archive", 0f, 0.95f, 0f, 0.02f, 0.02f, 0.02f, 0.18f),
        // Cold and clear, blue in the shadows.
        new("Winter", "Winter", -0.06f, 1.05f, 0.85f, 0f, 0.008f, 0.025f),
        // Low warm light, plum shadows.
        new("Dusk", "Dusk", 0.05f, 1.06f, 1.05f, 0.02f, 0f, 0.022f, 0.15f),
    ];

    public static ColorGrade Find(string id) => Array.Find(All, grade => grade.Id == id) ?? All[0];
}
