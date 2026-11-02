namespace RailWerkz.UI.Anim;

/// The easing curves, after ImAnim's presets (and Robert Penner's before it). In, Out and InOut of each family, plus
/// Linear. Everything maps 0..1 to 0..1; Back and Elastic overshoot on the way.
public enum Ease
{
    Linear,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InSine, OutSine, InOutSine,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InBack, OutBack, InOutBack,
    InElastic, OutElastic, InOutElastic,
    InBounce, OutBounce, InOutBounce,
}

public static class Easing
{
    private const float BackC1 = 1.70158f;
    private const float BackC2 = BackC1 * 1.525f;
    private const float BackC3 = BackC1 + 1f;
    private const float ElasticC4 = 2f * MathF.PI / 3f;
    private const float ElasticC5 = 2f * MathF.PI / 4.5f;

    public static float Apply(Ease ease, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return ease switch
        {
            Ease.Linear => t,
            Ease.InQuad => t * t,
            Ease.OutQuad => 1f - (1f - t) * (1f - t),
            Ease.InOutQuad => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f,
            Ease.InCubic => t * t * t,
            Ease.OutCubic => 1f - MathF.Pow(1f - t, 3f),
            Ease.InOutCubic => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
            Ease.InQuart => t * t * t * t,
            Ease.OutQuart => 1f - MathF.Pow(1f - t, 4f),
            Ease.InOutQuart => t < 0.5f ? 8f * t * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 4f) / 2f,
            Ease.InQuint => t * t * t * t * t,
            Ease.OutQuint => 1f - MathF.Pow(1f - t, 5f),
            Ease.InOutQuint => t < 0.5f ? 16f * t * t * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 5f) / 2f,
            Ease.InSine => 1f - MathF.Cos(t * MathF.PI / 2f),
            Ease.OutSine => MathF.Sin(t * MathF.PI / 2f),
            Ease.InOutSine => -(MathF.Cos(MathF.PI * t) - 1f) / 2f,
            Ease.InExpo => t <= 0f ? 0f : MathF.Pow(2f, 10f * t - 10f),
            Ease.OutExpo => t >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * t),
            Ease.InOutExpo => t <= 0f ? 0f : t >= 1f ? 1f : t < 0.5f ? MathF.Pow(2f, 20f * t - 10f) / 2f : (2f - MathF.Pow(2f, -20f * t + 10f)) / 2f,
            Ease.InCirc => 1f - MathF.Sqrt(1f - t * t),
            Ease.OutCirc => MathF.Sqrt(1f - (t - 1f) * (t - 1f)),
            Ease.InOutCirc => t < 0.5f ? (1f - MathF.Sqrt(1f - 4f * t * t)) / 2f : (MathF.Sqrt(1f - MathF.Pow(-2f * t + 2f, 2f)) + 1f) / 2f,
            Ease.InBack => BackC3 * t * t * t - BackC1 * t * t,
            Ease.OutBack => 1f + BackC3 * MathF.Pow(t - 1f, 3f) + BackC1 * MathF.Pow(t - 1f, 2f),
            Ease.InOutBack => t < 0.5f
                ? MathF.Pow(2f * t, 2f) * ((BackC2 + 1f) * 2f * t - BackC2) / 2f
                : (MathF.Pow(2f * t - 2f, 2f) * ((BackC2 + 1f) * (t * 2f - 2f) + BackC2) + 2f) / 2f,
            Ease.InElastic => t <= 0f ? 0f : t >= 1f ? 1f : -MathF.Pow(2f, 10f * t - 10f) * MathF.Sin((t * 10f - 10.75f) * ElasticC4),
            Ease.OutElastic => t <= 0f ? 0f : t >= 1f ? 1f : MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * ElasticC4) + 1f,
            Ease.InOutElastic => t <= 0f ? 0f : t >= 1f ? 1f : t < 0.5f
                ? -(MathF.Pow(2f, 20f * t - 10f) * MathF.Sin((20f * t - 11.125f) * ElasticC5)) / 2f
                : MathF.Pow(2f, -20f * t + 10f) * MathF.Sin((20f * t - 11.125f) * ElasticC5) / 2f + 1f,
            Ease.InBounce => 1f - OutBounce(1f - t),
            Ease.OutBounce => OutBounce(t),
            Ease.InOutBounce => t < 0.5f ? (1f - OutBounce(1f - 2f * t)) / 2f : (1f + OutBounce(2f * t - 1f)) / 2f,
            _ => t,
        };
    }

    private static float OutBounce(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;

        if (t < 1f / d1)
            return n1 * t * t;
        if (t < 2f / d1)
            return n1 * (t -= 1.5f / d1) * t + 0.75f;
        if (t < 2.5f / d1)
            return n1 * (t -= 2.25f / d1) * t + 0.9375f;
        return n1 * (t -= 2.625f / d1) * t + 0.984375f;
    }

    /// A CSS-style cubic-bezier(x1, y1, x2, y2) curve, solved for x by Newton's method with a bisection fallback.
    public static float CubicBezier(float x1, float y1, float x2, float y2, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        static float Sample(float a1, float a2, float u) => ((1f - 3f * a2 + 3f * a1) * u + (3f * a2 - 6f * a1)) * u * u + 3f * a1 * u;
        static float Slope(float a1, float a2, float u) => 3f * (1f - 3f * a2 + 3f * a1) * u * u + 2f * (3f * a2 - 6f * a1) * u + 3f * a1;

        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Sample(x1, x2, s) - t;
            var d = Slope(x1, x2, s);
            if (MathF.Abs(x) < 1e-5f)
                return Sample(y1, y2, s);
            if (MathF.Abs(d) < 1e-6f)
                break;
            s -= x / d;
        }

        float lo = 0f, hi = 1f;
        s = t;
        for (var i = 0; i < 20; i++)
        {
            var x = Sample(x1, x2, s);
            if (MathF.Abs(x - t) < 1e-5f)
                break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) * 0.5f;
        }

        return Sample(y1, y2, s);
    }

    /// A staircase of n equal steps.
    public static float Steps(int count, float t) => count <= 0 ? t : MathF.Floor(Math.Clamp(t, 0f, 1f) * count) / count;
}
