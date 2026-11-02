namespace RailWerkz.UI.Anim;

public enum Wave { Sine, Triangle, Saw, Square }

/// Motion that needs no state: oscillators, smooth noise, wiggle and shake, all read from the interface clock.
public static class Procedural
{
    /// -1..1 at frequency hz.
    public static float Oscillate(float hz, Wave wave = Wave.Sine, float phase = 0f)
    {
        if (Tween.ReduceMotion)
            return 0f;

        var t = (float)(Tween.Now * hz) + phase;
        var f = t - MathF.Floor(t);

        return wave switch
        {
            Wave.Triangle => 1f - 4f * MathF.Abs(f - 0.5f),
            Wave.Saw => f * 2f - 1f,
            Wave.Square => f < 0.5f ? 1f : -1f,
            _ => MathF.Sin(t * MathF.Tau),
        };
    }

    /// 0..1 breathing pulse, handy for "press any key" and live indicators.
    public static float Pulse(float hz = 0.8f, float phase = 0f) => 0.5f + 0.5f * Oscillate(hz, Wave.Sine, phase);

    private static float Hash(int n)
    {
        unchecked
        {
            var h = (uint)n * 0x27d4eb2du;
            h ^= h >> 15;
            h *= 0x85ebca6bu;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0xFFFFFF * 2f - 1f;
        }
    }

    /// Smooth 1D value noise, -1..1.
    public static float Noise(float x)
    {
        var i = (int)MathF.Floor(x);
        var f = x - i;
        var u = f * f * (3f - 2f * f);
        return Hash(i) + (Hash(i + 1) - Hash(i)) * u;
    }

    /// A gentle random drift, -amplitude..amplitude, different per seed.
    public static float Wiggle(float hz, float amplitude, int seed = 0) =>
        Tween.ReduceMotion ? 0f : Noise((float)Tween.Now * hz + seed * 17.13f) * amplitude;

    /// A shake that dies away over duration seconds after start: for errors and rejected input.
    public static float2 Shake(double start, float duration = 0.35f, float amplitude = 8f, float hz = 28f)
    {
        var t = (float)(Tween.Now - start);
        if (Tween.ReduceMotion || t < 0f || t > duration)
            return default;

        var decay = 1f - t / duration;
        decay *= decay;
        return new float2(Noise(t * hz) * amplitude * decay, Noise(t * hz + 31.7f) * amplitude * 0.4f * decay);
    }
}
