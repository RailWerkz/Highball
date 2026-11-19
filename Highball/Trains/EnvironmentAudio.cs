using Brutal.Numerics;
using Highball.World;
using RailWerkz.Core.Audio;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Utils;

namespace Highball.Trains;

/// The world's own sound round the camera (Content/Audio/Env), from the hour, the season and the weather:
/// - a quiet outdoor bed (env_background01) always;
/// - by day, birds singing now and then (bird01-08) and the odd crow (bird_crow01-03), each from somewhere round about;
/// - on summer days, cicadas (Cicada01);
/// - at night, crickets (env_crickets01_short looped, env_crickets07_short now and then);
/// - in rain, the rain (Rain_Ext01 outside, Rain_Int01 heard from a cab), and in heavy rain, thunder far off (thunder01-03).
/// Wildlife falls quiet in the rain and as the camera climbs away from the ground; everything outside is softened in a cab. How
/// loud, how often: AudioTuning.Environment, tuned in the Audio Editor's Environment tab, read live.
public sealed class EnvironmentAudio : IDisposable
{
    private static AudioTuning.EnvironmentTuning Tuning => AudioTuning.Current.Environment;

    private sealed class Bed(string name, string file)
    {
        public readonly string Name = name;
        public readonly string File = file;
        public AudioEngine.LoopHandle? Handle;
    }

    private readonly Bed _background = new("env:background", "Env/env_background01.wav");
    private readonly Bed _cicadas = new("env:cicadas", "Env/Cicada01.wav");
    private readonly Bed _crickets = new("env:crickets", "Env/env_crickets01_short.wav");
    private readonly Bed _rainOut = new("env:rain.out", "Env/Rain_Ext01.wav");
    private readonly Bed _rainIn = new("env:rain.in", "Env/Rain_Int01.wav");

    private static readonly string[] Birds = [.. Enumerable.Range(1, 8).Select(i => $"Env/bird{i:00}.wav")];
    private static readonly string[] Crows = ["Env/bird_crow01.wav", "Env/bird_crow02.wav", "Env/bird_crow03.wav"];
    private static readonly string[] Chirps = ["Env/env_crickets07_short.wav"];
    private static readonly string[] Thunder = ["Env/thunder01.wav", "Env/thunder02.wav", "Env/thunder03.wav"];
    private static readonly HashSet<string> Loaded = [];

    private float _nextBird = 2f, _nextCrow = 20f, _nextChirp = 3f, _nextThunder = 12f;
    private float _rain;
    private bool _settled;

    /// How much it's day (1), night (0), or between, by the sun's height: full day once it's 4 degrees up, full night 6 degrees down.
    public static float Daylight(RailWerkz.Core.Settings.Settings s, double latitude, double longitude)
    {
        var sun = SunPosition.At(latitude, longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
        return Math.Clamp((sun.Elevation + 6f) / 10f, 0f, 1f);
    }

    /// After the listener's set this frame. above: the camera's height over the ground there; inCab: heard from a cab.
    public void Update(float dt, double latitude, double longitude, float above, bool inCab)
    {
        var t = Tuning;
        var s = Settings.Current;
        var rainTarget = s.Weather == "Rain" ? Math.Clamp(s.WeatherAmount, 0f, 1f) : 0f;
        // (Already at the weather it starts with, as the picture is; a change after plays out over a few seconds.)
        _rain = _settled ? _rain + Math.Clamp(rainTarget - _rain, -dt * 0.2f, dt * 0.2f) : rainTarget;
        _settled = true;

        var day = Daylight(s, latitude, longitude);
        var night = 1f - Math.Clamp(day * 1.6f, 0f, 1f);
        var summer = s.SkyMonth is >= 6 and <= 8 ? 1f : s.SkyMonth is 5 or 9 ? 0.4f : 0f;
        var ground = 1f - MathEx.SmoothStep(t.HighFade * 0.25f, t.HighFade, above);
        var outside = inCab ? t.CabMuffle : 1f;
        var calm = 1f - MathF.Min(_rain * 1.5f, 1f);
        var wildlife = t.Master * ground * outside * calm;

        Keep(_background, t.Master * t.Background * outside * (0.6f + 0.4f * ground));
        Keep(_cicadas, wildlife * t.Cicadas * summer * MathEx.SmoothStep(0.5f, 1f, day));
        Keep(_crickets, wildlife * t.Crickets * night);
        // (In a cab the rain's its own recording, on the roof and glass, not the outside's softened.)
        Keep(_rainOut, t.Master * t.Rain * _rain * (inCab ? 0f : 1f));
        Keep(_rainIn, t.Master * t.Rain * _rain * (inCab ? 1f : 0f));

        if (Every(ref _nextBird, dt, t.BirdEveryMin, t.BirdEveryMax) && day > 0.3f)
            Call(Birds, wildlife * t.Birds * day, 15f, 70f);
        if (Every(ref _nextCrow, dt, t.CrowEveryMin, t.CrowEveryMax) && day > 0.3f)
            Call(Crows, wildlife * t.Crows * day, 30f, 120f);
        if (Every(ref _nextChirp, dt, 2f, 7f) && night > 0.5f)
            Call(Chirps, wildlife * t.Crickets * night, 4f, 20f);
        if (Every(ref _nextThunder, dt, t.ThunderEveryMin, t.ThunderEveryMax) && _rain >= t.ThunderAbove)
            AudioEngine.Play(Load(Thunder[Random.Shared.Next(Thunder.Length)]), AudioCategory.Effects,
                t.Master * t.Thunder * (inCab ? 0.7f : 1f) * (0.55f + 0.45f * Random.Shared.NextSingle()), 0.9f + 0.15f * Random.Shared.NextSingle());
    }

    /// Counts down to the next of something that comes now and then; true when it's due (and the next one's set).
    private static bool Every(ref float next, float dt, float min, float max)
    {
        next -= dt;
        if (next > 0f)
            return false;
        next = min + (MathF.Max(max, min) - min) * Random.Shared.NextSingle();
        return true;
    }

    /// One of a set of calls from somewhere round the camera, between near and far metres off, a little higher or lower each time.
    private static void Call(string[] files, float level, float near, float far)
    {
        if (level < 0.002f)
            return;
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var away = near + (far - near) * Random.Shared.NextSingle();
        var at = new float3(MathF.Cos(angle) * away, 2f + 8f * Random.Shared.NextSingle(), MathF.Sin(angle) * away);
        AudioEngine.Play(Load(files[Random.Shared.Next(files.Length)], mono: true), AudioCategory.Effects, level, 0.93f + 0.14f * Random.Shared.NextSingle(),
            new AudioEngine.Placement(at, default, 10f, 400f));
    }

    private static string Load(string file, bool mono = false)
    {
        var name = "env:" + file;
        if (!Loaded.Contains(name))
        {
            if (AudioEngine.Load(name, file, mono))
                Loaded.Add(name);
            else
                Warn(file);
        }

        if (Trace)
            RailWerkz.Core.Logging.Log.Info($"Environment: {file}");
        return name;
    }

    /// HB_ENVLOG set: every sound started is logged, for testing.
    private static readonly bool Trace = System.Environment.GetEnvironmentVariable("HB_ENVLOG") is not null;

    private static readonly HashSet<string> Warned = [];

    private static void Warn(string file)
    {
        if (Warned.Add(file))
            RailWerkz.Core.Logging.Log.Warn($"Environment audio: couldn't load {file}");
    }

    /// A looped bed at a level: started once it's heard, stopped once it isn't.
    private static void Keep(Bed bed, float level)
    {
        if (level > 0.002f)
        {
            if (bed.Handle is null)
            {
                if (!Loaded.Contains(bed.Name) && AudioEngine.LoadSeamless(bed.Name, bed.File))
                    Loaded.Add(bed.Name);
                if (!Loaded.Contains(bed.Name))
                {
                    Warn(bed.File);
                    return;
                }

                bed.Handle = AudioEngine.PlayLoop(bed.Name, AudioCategory.Effects, level);
                if (Trace)
                    RailWerkz.Core.Logging.Log.Info($"Environment: {bed.File} looping at {level:0.00}");
            }

            AudioEngine.SetLoopVolume(bed.Handle.Value, level);
        }
        else if (bed.Handle is { } handle)
        {
            AudioEngine.FadeLoop(handle, 1f);
            bed.Handle = null;
        }
    }

    /// For the Audio Editor: one of a kind of sound at full volume, as tuned.
    public static void Preview(string kind)
    {
        var t = Tuning;
        var (files, level) = kind switch
        {
            "bird" => (Birds, t.Birds),
            "crow" => (Crows, t.Crows),
            "crickets" => (Chirps, t.Crickets),
            "thunder" => (Thunder, t.Thunder),
            _ => (Birds, 0f),
        };
        AudioEngine.Play(Load(files[Random.Shared.Next(files.Length)], mono: kind != "thunder"), AudioCategory.Effects, t.Master * level);
    }

    public void Dispose()
    {
        foreach (var bed in (Bed[])[_background, _cicadas, _crickets, _rainOut, _rainIn])
            if (bed.Handle is { } handle)
            {
                AudioEngine.StopLoop(handle);
                bed.Handle = null;
            }
    }
}
