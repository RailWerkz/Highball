using Brutal.ImGuiApi;

namespace RailWerkz.UI;

/// A linear-ish sRGB colour with alpha, 0..1 per channel. Mix blends in OKLAB so a fade between two hues stays
/// bright through the middle instead of going muddy, as ImAnim's perceptual blending does.
public readonly record struct Rgba(float R, float G, float B, float A = 1f)
{
    public static readonly Rgba White = new(1f, 1f, 1f);
    public static readonly Rgba Black = new(0f, 0f, 0f);
    public static readonly Rgba Clear = new(0f, 0f, 0f, 0f);

    /// From 0xRRGGBB.
    public static Rgba Hex(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public Rgba WithAlpha(float alpha) => this with { A = alpha };

    public Rgba Fade(float k) => this with { A = A * k };

    public Rgba Scale(float k) => new(R * k, G * k, B * k, A);

    /// Packed for ImGui's draw lists (ABGR).
    public uint U32
    {
        get
        {
            var a = (uint)(Math.Clamp(A, 0f, 1f) * 255f + 0.5f);
            var r = (uint)(Math.Clamp(R, 0f, 1f) * 255f + 0.5f);
            var g = (uint)(Math.Clamp(G, 0f, 1f) * 255f + 0.5f);
            var b = (uint)(Math.Clamp(B, 0f, 1f) * 255f + 0.5f);
            return (a << 24) | (b << 16) | (g << 8) | r;
        }
    }

    public float4 Float4 => new(R, G, B, A);

    public static implicit operator uint(Rgba c) => c.U32;
    public static implicit operator ImColor8(Rgba c) => c.U32;

    /// A plain per-channel blend.
    public static Rgba Lerp(Rgba a, Rgba b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// A perceptual blend through OKLAB.
    public static Rgba Mix(Rgba a, Rgba b, float t)
    {
        var la = ToOklab(a);
        var lb = ToOklab(b);
        var l = la + (lb - la) * t;
        var c = FromOklab(l);
        return c with { A = a.A + (b.A - a.A) * t };
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static float ToGamma(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private static float3 ToOklab(Rgba c)
    {
        var r = ToLinear(c.R);
        var g = ToLinear(c.G);
        var b = ToLinear(c.B);
        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return new float3(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    private static Rgba FromOklab(float3 lab)
    {
        var l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        var m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        var s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l *= l * l;
        m *= m * m;
        s *= s * s;
        return new Rgba(
            Math.Clamp(ToGamma(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s), 0f, 1f),
            Math.Clamp(ToGamma(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s), 0f, 1f),
            Math.Clamp(ToGamma(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s), 0f, 1f));
    }
}
