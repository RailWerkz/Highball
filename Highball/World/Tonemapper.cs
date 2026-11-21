using System.Runtime.InteropServices;
using Brutal;

namespace Highball.World;

/// A curve that fits the graded HDR picture to the screen (tonemap.frag, Post/Tonemapping.glsl). Index is tonemapSwitch's case.
public sealed record Tonemapper(string Id, string Label, int Index)
{
    public static readonly Tonemapper[] All =
    [
        new("Aces", "ACES", 3),
        new("AcesFitted", "ACES fitted", 2),
        new("AgX", "AgX", 1),
        new("GranTurismo", "Gran Turismo 7", 8),
        new("Hable", "Hable", 10),
        new("Uncharted2", "Uncharted 2", 0),
        new("Filmic", "Filmic (Hejl)", 4),
        new("RomBinDaHouse", "RomBinDaHouse", 5),
        new("Reinhard", "Reinhard", 6),
        new("ReinhardLuma", "Reinhard (luma)", 7),
        new("Linear", "Linear", 9),
    ];

    public static Tonemapper Find(string id) => Array.Find(All, mapper => mapper.Id == id) ?? All[0];

    /// Hable's curve as PostUniforms.hable holds it: three segments of (offsetX, offsetY, scaleX, scaleY, lnA, B), then 1/W, x0,
    /// x1 and a spare.
    [StructLayout(LayoutKind.Sequential)]
    public struct HableNumbers
    {
        public float4 A, B, C, D, E, F;
    }

    /// Built once: toe strength 0.5 and length 0.5, a shoulder 2 stops strong, 0.5 long, fully angled, no gamma.
    public static readonly HableNumbers Hable = HableCurve(0.5f, 0.5f, 2f, 0.5f, 1f, 1f);

    private struct Segment
    {
        public float OffsetX, OffsetY, ScaleX = 1f, ScaleY = 1f, LnA, B = 1f;

        public Segment()
        {
        }

        public readonly float Eval(float x)
        {
            var x0 = (x - OffsetX) * ScaleX;
            var y0 = x0 > 0f ? MathF.Exp(LnA + B * MathF.Log(x0)) : 0f;
            return y0 * ScaleY + OffsetY;
        }
    }

    /// John Hable's piecewise power curve, from his public domain C++ (filmicworlds.com, "Filmic Tonemapping with Piecewise
    /// Power Curves"): CalcDirectParamsFromUser then CreateCurve.
    public static HableNumbers HableCurve(float toeStrength, float toeLength, float shoulderStrength, float shoulderLength, float shoulderAngle, float gamma)
    {
        toeLength = Math.Clamp(toeLength, 0f, 1f);
        toeStrength = Math.Clamp(toeStrength, 0f, 1f);
        shoulderAngle = Math.Clamp(shoulderAngle, 0f, 1f);
        shoulderLength = Math.Clamp(shoulderLength, 1e-5f, 0.999f);
        shoulderStrength = MathF.Max(0f, shoulderStrength);

        // The user's parameters as points on the curve. The toe goes from 0 to 0.5; the shoulder's strength is in stops.
        var x0 = toeLength * 0.5f;
        var y0 = (1f - toeStrength) * x0;
        var remainingY = 1f - y0;
        var initialW = x0 + remainingY;
        var y1Offset = (1f - shoulderLength) * remainingY;
        var x1 = x0 + y1Offset;
        var y1 = y0 + y1Offset;
        var w = initialW + (MathF.Pow(2f, shoulderStrength) - 1f);
        var overshootX = w * 2f * shoulderAngle * shoulderStrength;
        var overshootY = 0.5f * shoulderAngle * shoulderStrength;

        // Normalised to a white of 1.
        x0 /= w;
        x1 /= w;
        overshootX /= w;

        var segments = new Segment[3];

        // The straight middle, y = (mx + b)^g.
        var m = x1 - x0 == 0f ? 1f : (y1 - y0) / (x1 - x0);
        var b = y0 - x0 * m;
        segments[1] = new Segment { OffsetX = -(b / m), LnA = gamma * MathF.Log(m), B = gamma };
        float Slope(float x) => gamma * m * MathF.Pow(m * x + b, gamma - 1f);
        var toeM = Slope(x0);
        var shoulderM = Slope(x1);
        y0 = MathF.Max(1e-5f, MathF.Pow(y0, gamma));
        y1 = MathF.Max(1e-5f, MathF.Pow(y1, gamma));
        overshootY = MathF.Pow(1f + overshootY, gamma) - 1f;

        static (float LnA, float B) SolveAB(float x, float y, float slope)
        {
            var bb = slope * x / y;
            return (MathF.Log(y) - bb * MathF.Log(x), bb);
        }

        var (toeLnA, toeB) = SolveAB(x0, y0, toeM);
        segments[0] = new Segment { LnA = toeLnA, B = toeB };

        var (shoulderLnA, shoulderB) = SolveAB(1f + overshootX - x1, 1f + overshootY - y1, shoulderM);
        segments[2] = new Segment { OffsetX = 1f + overshootX, OffsetY = 1f + overshootY, ScaleX = -1f, ScaleY = -1f, LnA = shoulderLnA, B = shoulderB };

        // Scaled so white lands on 1 (the overshoot would leave it short).
        var invScale = 1f / segments[2].Eval(1f);
        for (var i = 0; i < 3; i++)
        {
            segments[i].OffsetY *= invScale;
            segments[i].ScaleY *= invScale;
        }

        var n = new float[24];
        for (var i = 0; i < 3; i++)
        {
            var s = segments[i];
            n[i * 6] = s.OffsetX;
            n[i * 6 + 1] = s.OffsetY;
            n[i * 6 + 2] = s.ScaleX;
            n[i * 6 + 3] = s.ScaleY;
            n[i * 6 + 4] = s.LnA;
            n[i * 6 + 5] = s.B;
        }

        (n[18], n[19], n[20]) = (1f / w, x0, x1);
        float4 Row(int r) => new(n[r * 4], n[r * 4 + 1], n[r * 4 + 2], n[r * 4 + 3]);
        return new HableNumbers { A = Row(0), B = Row(1), C = Row(2), D = Row(3), E = Row(4), F = Row(5) };
    }
}
