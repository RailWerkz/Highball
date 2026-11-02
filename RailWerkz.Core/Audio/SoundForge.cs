using RailWerkz.Core.Utils;

namespace RailWerkz.Core.Audio;

/// Sounds the content doesn't have, built from oscillators and noise at startup: the interface's blips and beeps.
public static class SoundForge
{
    private const int Rate = 44100;

    private delegate float Voice(float t, int i);

    private static Sound Render(float seconds, Voice voice, float gain = 0.8f)
    {
        var count = (int)(seconds * Rate);
        var samples = new short[count];

        for (var i = 0; i < count; i++)
            samples[i] = (short)(Math.Clamp(MathF.Tanh(voice(i / (float)Rate, i) * gain), -1f, 1f) * 32767f);

        return new Sound(samples, 1, Rate);
    }

    private static float Sine(float hz, float t) => MathF.Sin(MathEx.Tau * hz * t);
    private static float Square(float hz, float t) => ((hz * t) % 1f) < 0.5f ? 1f : -1f;
    private static float Env(float t, float attack, float decay) => t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);

    /// Menu cursor moving: a short tick.
    public static Sound Blip() => Render(0.06f, (t, _) => Square(1760f, t) * 0.25f * Env(t, 0.002f, 0.015f));

    /// Something chosen: two notes up.
    public static Sound Select() => Render(0.18f, (t, _) =>
        (t < 0.07f ? Square(880f, t) : Square(1318.5f, t)) * 0.28f * Env(t < 0.07f ? t : t - 0.07f, 0.002f, 0.04f));

    /// Backing out: two notes down.
    public static Sound Back() => Render(0.16f, (t, _) =>
        (t < 0.06f ? Square(988f, t) : Square(659f, t)) * 0.25f * Env(t < 0.06f ? t : t - 0.06f, 0.002f, 0.035f));

    /// A startup check settling.
    public static Sound Beep() => Render(0.09f, (t, _) => Sine(2093f, t) * 0.4f * Env(t, 0.003f, 0.03f));

    /// One key of the typewriter.
    public static Sound Tick()
    {
        var rng = new Rng(77);
        return Render(0.02f, (t, _) => (rng.Next() * 2f - 1f) * 0.35f * Env(t, 0.0005f, 0.004f));
    }

    /// Power on: a rising whine and a thump.
    public static Sound PowerOn()
    {
        var rng = new Rng(5);
        return Render(0.6f, (t, _) =>
            Sine(60f + 9000f * t * t, t) * 0.08f * (1f - t / 0.6f) + Sine(55f, t) * 0.5f * Env(t, 0.005f, 0.12f) +
            (rng.Next() * 2f - 1f) * 0.05f * Env(t, 0.001f, 0.05f));
    }

    // The modern interface's set: rounder sines, quieter, for menus that glide rather than click.

    /// The pointer or cursor landing on an item: a soft, short glassy tap.
    public static Sound Hover() => Render(0.07f, (t, _) =>
        (Sine(1568f, t) * 0.6f + Sine(3136f, t) * 0.15f) * 0.22f * Env(t, 0.003f, 0.018f));

    /// Something chosen: a quick rising pair of bells.
    public static Sound Confirm() => Render(0.32f, (t, _) =>
    {
        var second = MathF.Max(0f, t - 0.06f);
        return (Sine(987.8f, t) * Env(t, 0.003f, 0.07f) + Sine(1479.9f, second) * Env(second, 0.003f, 0.11f) * (t > 0.06f ? 1f : 0f)) * 0.3f;
    });

    /// Backing out: the same pair, falling.
    public static Sound Dismiss() => Render(0.26f, (t, _) =>
    {
        var second = MathF.Max(0f, t - 0.05f);
        return (Sine(1318.5f, t) * Env(t, 0.003f, 0.05f) + Sine(880f, second) * Env(second, 0.003f, 0.08f) * (t > 0.05f ? 1f : 0f)) * 0.26f;
    });

    // Alternatives to pick from in Options > Audio.

    /// A sine gliding exponentially from one pitch down to another (phase integrated, so it doesn't warble).
    private static float Glide(float from, float to, float time, float t) =>
        MathF.Sin(MathEx.Tau * (to * t + (from - to) * time * (1f - MathF.Exp(-t / time))));

    /// Hover: a crisp mouse-click tick.
    public static Sound Click()
    {
        var rng = new Rng(31);
        return Render(0.03f, (t, _) => ((rng.Next() * 2f - 1f) * 0.45f + Sine(3800f, t) * 0.35f) * Env(t, 0.0004f, 0.005f));
    }

    /// Hover: a soft wooden tap.
    public static Sound Tap() => Render(0.06f, (t, _) => Glide(900f, 480f, 0.012f, t) * 0.4f * Env(t, 0.001f, 0.016f));

    /// Hover: a small bubble pop.
    public static Sound Pop() => Render(0.06f, (t, _) => Glide(1500f, 320f, 0.01f, t) * 0.35f * Env(t, 0.001f, 0.014f));

    /// Select: three notes up, quick.
    public static Sound Chime() => Render(0.36f, (t, _) =>
    {
        var sum = 0f;
        float[] notes = [784f, 987.8f, 1174.7f];
        for (var n = 0; n < notes.Length; n++)
        {
            var local = t - n * 0.05f;
            if (local > 0f)
                sum += Sine(notes[n], local) * Env(local, 0.002f, 0.08f);
        }

        return sum * 0.22f;
    });

    /// Select: one warm note with a fifth over it.
    public static Sound Soft() => Render(0.3f, (t, _) => (Sine(659.3f, t) + Sine(987.8f, t) * 0.4f) * 0.25f * Env(t, 0.005f, 0.09f));

    /// Select: two firm knocks.
    public static Sound Knock() => Render(0.12f, (t, _) =>
    {
        var second = t - 0.045f;
        return (Glide(700f, 380f, 0.01f, t) * Env(t, 0.001f, 0.014f) + (second > 0f ? Glide(900f, 460f, 0.01f, second) * Env(second, 0.001f, 0.016f) : 0f)) * 0.45f;
    });

    /// Back: one low warm note.
    public static Sound SoftLow() => Render(0.26f, (t, _) => (Sine(440f, t) + Sine(660f, t) * 0.3f) * 0.25f * Env(t, 0.005f, 0.07f));

    /// Back: one low knock.
    public static Sound KnockLow() => Render(0.08f, (t, _) => Glide(520f, 260f, 0.012f, t) * 0.45f * Env(t, 0.001f, 0.018f));

    /// Typing: a mechanical key, noise with a ring.
    public static Sound Key()
    {
        var rng = new Rng(53);
        return Render(0.03f, (t, _) => (rng.Next() * 2f - 1f) * 0.3f * Env(t, 0.0004f, 0.003f) + Sine(2600f, t) * 0.18f * Env(t, 0.0005f, 0.008f));
    }

    /// Typing: a muffled tick, noise through a low-pass.
    public static Sound Muffled()
    {
        var rng = new Rng(97);
        var low = 0f;
        return Render(0.03f, (t, _) =>
        {
            low += (rng.Next() * 2f - 1f - low) * 0.18f;
            return low * 1.2f * Env(t, 0.0005f, 0.006f);
        });
    }

    /// Typing: a tiny square blip.
    public static Sound Pip() => Render(0.02f, (t, _) => Square(2400f, t) * 0.12f * Env(t, 0.0005f, 0.004f));
}
