namespace Highball.World;

/// A colour grade for the finished picture, applied in the tonemap pass (tonemap.frag) before the tonemapper. Warmth moves white
/// towards orange (above 0) or blue; contrast is about mid grey; saturation is 1 for as is, 0 for black and white. Lift raises the
/// shadows towards a colour (linear RGB, as a share of mid grey), and Vignette darkens the corners. Neutral changes nothing, so the
/// tonemapper alone makes the look.
public sealed record ColorGrade(string Id, string Label, float Warmth, float Contrast, float Saturation, float LiftR = 0f, float LiftG = 0f, float LiftB = 0f,
    float Vignette = 0f)
{
    public static readonly ColorGrade[] All =
    [
        new("Neutral", "Neutral", 0f, 1f, 1f),
        new("Warm", "Warm", 0.05f, 1.04f, 1.05f),
        new("Cool", "Cool", -0.04f, 1.03f, 0.95f),
        // Bright and lively: a little warm, more colour and punch.
        new("Summer", "Summer", 0.03f, 1.08f, 1.15f),
        // Cold and clear, blue in the shadows, less colour.
        new("Winter", "Winter", -0.06f, 1.05f, 0.85f, 0f, 0.008f, 0.025f),
        // Black and white, hard, dark corners.
        new("Noir", "Noir", 0f, 1.25f, 0f, Vignette: 0.25f),
    ];

    /// The grade by its id; Neutral for one no longer listed.
    public static ColorGrade Find(string id) => Array.Find(All, grade => grade.Id == id) ?? All[0];
}
