namespace Highball.World;

/// A look for the finished picture. Warmth moves white towards orange or blue, contrast is about mid grey, and saturation is 1 for as is.
public sealed record ColorGrade(string Id, string Label, float Warmth, float Contrast, float Saturation)
{
    public static readonly ColorGrade[] All =
    [
        new("Filmic", "Filmic", 0.012f, 1.08f, 1.05f),
        new("Neutral", "Neutral", 0f, 1f, 1f),
        new("Golden", "Golden", 0.05f, 1.08f, 1.1f),
        new("Cool", "Cool", -0.04f, 1.05f, 0.95f),
        new("Faded", "Faded", 0.01f, 0.88f, 0.82f),
    ];

    public static ColorGrade Find(string id) => Array.Find(All, grade => grade.Id == id) ?? All[0];
}
