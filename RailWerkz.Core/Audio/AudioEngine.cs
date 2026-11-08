using System.Runtime.InteropServices;
using Brutal.OpenAlApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace RailWerkz.Core.Audio;

public enum AudioCategory { Effects, Voice, Interface, Music }

/// Sound over OpenAL (Brutal.OpenAl): named sounds, a pool of voices for one-shots, loops, delayed sounds, sounds placed
/// in the world (heard from where the listener is, fading with distance) and the music, streamed from the files in Content/Audio/Music.
public static class AudioEngine
{
    private sealed record Loaded(AlBuffer Buffer, float Duration);

    private struct Voice
    {
        public AlSource Source;
        public AudioCategory Category;
        public float Volume;
        public bool Looping;
        public int Generation;
        /// Turning down by this much a second until silent, then stopped (0 while not fading).
        public float Fade;
        /// Placed with a range: how far that is, and how far from the listener it is now (both 0 when it has none).
        public float Range;
        public float Distance;
    }

    private readonly record struct Pending(double At, string Name, AudioCategory Category, float Volume, float Pitch);

    /// Where a sound is in the world, relative to the listener's origin: it's at full volume within ReferenceDistance
    /// metres and falls off as reference / distance beyond, panned by where it is and pitched by Doppler from Velocity
    /// (metres a second). With a Range (metres) it's also turned down by (1 - distance / range)^RangeCurve all the way
    /// out, so it's silent at the range: the inverse law alone flattens out far off and never gets to silence. Placed
    /// sounds lose their highs to the air with distance too. Only mono sounds are placed: OpenAL plays stereo ones as they are.
    public readonly record struct Placement(float3 Position, float3 Velocity, float ReferenceDistance, float Range = 0f);

    /// How steeply a placed sound fades across its range on top of the inverse law: 1 is a straight line to silence,
    /// 2 drops sooner (-6 dB by half the range, -12 dB by three quarters).
    private const float RangeCurve = 2f;

    /// How much high end placed sounds lose to the air: 1 is OpenAL's real-air figure (about -0.05 dB a metre at 5 kHz).
    private const float AirAbsorption = 1f;

    private static float3 _listener;

    /// Refers to a playing sound (a loop, or a one-shot until its voice is reused) so it can be stopped, moved or turned up and down.
    public readonly record struct LoopHandle(int Index, int Generation)
    {
        public static readonly LoopHandle None = new(-1, 0);
        public bool IsValid => Index >= 0;
    }

    // Enough for a lash-up's engines, compressors and blowers, the wheels near the listener, the horn, bell and interface.
    private const int VoiceCount = 64;
    private const int StreamBuffers = 4;
    private const int StreamFrames = 8192;

    /// How long a sustained sound's loop seam and its let-go crossfade last, in seconds.
    private const float SustainFade = 0.08f;

    private static double _lastUpdate = -1.0;

    private static Device? _device;
    private static Context? _context;
    private static readonly Dictionary<string, Loaded> Sounds = [];
    private static readonly Voice[] Voices = new Voice[VoiceCount];
    private static readonly List<Pending> Queue = [];

    private static AlSource _music;
    private static readonly AlBuffer[] MusicBuffers = new AlBuffer[StreamBuffers];
    private static readonly short[] MusicScratch = new short[StreamFrames * 2];
    private static readonly MusicStream MusicPlayer = new();

    public static bool Available { get; private set; }

    public static bool Init()
    {
        try
        {
            _device = Alc.OpenDevice();
            _context = _device.CreateContext();
            _context.MakeCurrent();
        }
        catch (Exception e)
        {
            Log.Warn($"No audio device: {e.Message}");
            return false;
        }

        for (var i = 0; i < VoiceCount; i++)
            Voices[i].Source = Al.GenSource();

        // Placed sounds fall off as reference / distance, the way sound does in open air.
        Al.DistanceModel = AlDistanceModel.InverseDistanceClamped;

        _music = Al.GenSource();
        _music.SourceRelative = true;

        for (var i = 0; i < StreamBuffers; i++)
            MusicBuffers[i] = Al.GenBuffer();

        StartStream();
        Available = true;
        Log.Info($"Audio: {_device.Specifier}");
        return true;
    }

    private static Loaded Upload(Sound sound)
    {
        var buffer = Al.GenBuffer();
        buffer.SetData(sound.Channels == 2 ? AlFormat.Stereo16 : AlFormat.Mono16, MemoryMarshal.AsBytes(sound.Samples.AsSpan()), sound.SampleRate);
        return new Loaded(buffer, sound.Duration);
    }

    /// Loads a WAV from Content/Audio under a name, folded down to mono to be placed in the world if mono. False (and a
    /// warning) if it can't.
    public static bool Load(string name, string relativePath, bool mono = false)
    {
        if (!Available)
            return false;

        try
        {
            var sound = Wav.Load(ContentPath.Audio(relativePath));
            Sounds[name] = Upload(mono ? sound.ToMono() : sound);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Sound {name} ({relativePath}): {e.Message}");
            return false;
        }
    }

    /// Loads a WAV that's played as an attack, a sustain and a release (a horn): it plays from the top into the stretch
    /// between loopStart seconds and endLength seconds before its end, and loops that while it's held. Its release is
    /// registered as name + ".end", for ReleaseSustained. False (and a warning) if it can't.
    public static bool LoadSustained(string name, string relativePath, float loopStart, float endLength, bool mono = false)
    {
        if (!Available)
            return false;

        try
        {
            var sound = Wav.Load(ContentPath.Audio(relativePath));
            if (mono)
                sound = sound.ToMono();
            var (main, end, a, b) = SplitSustained(sound, loopStart, endLength);
            var loaded = Upload(main);
            // AL_SOFT_loop_points: once playing gets to b it goes back to a. Set before any source uses the buffer.
            var buffer = loaded.Buffer;
            buffer.LoopPoints = new int2(a, b);
            Sounds[name] = loaded;
            Sounds[name + ".end"] = Upload(end);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Sound {name} ({relativePath}): {e.Message}");
            return false;
        }
    }

    /// Loads a WAV to loop as a whole, its end crossfaded into its start so it goes round without a click (recorded
    /// loops seldom meet exactly). False (and a warning) if it can't.
    public static bool LoadSeamless(string name, string relativePath, bool mono = false)
    {
        if (!Available)
            return false;

        try
        {
            var sound = Wav.Load(ContentPath.Audio(relativePath));
            if (mono)
                sound = sound.ToMono();
            // Looping from just after the top over to the very end: the attack is the first SustainFade, which the
            // end's crossfade leads back into.
            var (main, _, a, b) = SplitSustained(sound, SustainFade, 0f);
            var loaded = Upload(main);
            var buffer = loaded.Buffer;
            buffer.LoopPoints = new int2(a, b);
            Sounds[name] = loaded;
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Sound {name} ({relativePath}): {e.Message}");
            return false;
        }
    }

    /// The sound up to its loop's end, with the loop's last SustainFade crossfaded into what leads up to its start so it
    /// goes round without a click; and its release from SustainFade before the loop's end, faded in, to crossfade to.
    private static (Sound Main, Sound End, int LoopStart, int LoopEnd) SplitSustained(Sound sound, float loopStart, float endLength)
    {
        var ch = sound.Channels;
        var frames = sound.Samples.Length / ch;
        var fade = (int)(SustainFade * sound.SampleRate);
        var a = Math.Clamp((int)(loopStart * sound.SampleRate), fade, frames);
        var b = Math.Clamp(frames - (int)(endLength * sound.SampleRate), 0, frames);
        if (b - a < fade * 3)
            throw new InvalidDataException($"too short to loop between {loopStart:0.##} s and {endLength:0.##} s from the end");

        var main = new short[b * ch];
        Array.Copy(sound.Samples, main, main.Length);
        for (var i = 0; i < fade; i++)
        {
            // Equal power: the two stretches aren't the same wave, so it's their loudness that adds up.
            var w = (i + 0.5f) / fade * MathF.PI * 0.5f;
            var (into, from) = (MathF.Sin(w), MathF.Cos(w));
            for (var c = 0; c < ch; c++)
            {
                var at = (b - fade + i) * ch + c;
                var lead = (a - fade + i) * ch + c;
                main[at] = (short)Math.Clamp(sound.Samples[at] * from + sound.Samples[lead] * into, -32768f, 32767f);
            }
        }

        var endStart = b - fade;
        var end = new short[(frames - endStart) * ch];
        Array.Copy(sound.Samples, endStart * ch, end, 0, end.Length);
        for (var i = 0; i < fade; i++)
        {
            var into = MathF.Sin((i + 0.5f) / fade * MathF.PI * 0.5f);
            for (var c = 0; c < ch; c++)
                end[i * ch + c] = (short)(end[i * ch + c] * into);
        }

        return (new Sound(main, ch, sound.SampleRate), new Sound(end, ch, sound.SampleRate), a, b);
    }

    /// Registers a generated sound under a name.
    public static void Add(string name, Sound sound)
    {
        if (Available)
            Sounds[name] = Upload(sound);
    }

    public static bool Has(string name) => Sounds.ContainsKey(name);

    public static float Duration(string name) => Sounds.TryGetValue(name, out var s) ? s.Duration : 0f;

    private static float CategoryGain(AudioCategory category)
    {
        var s = Settings.Settings.Current;
        return category switch
        {
            AudioCategory.Effects => s.EffectsVolume,
            AudioCategory.Voice => s.VoiceVolume,
            AudioCategory.Interface => s.EffectsVolume * 0.8f,
            _ => s.MusicVolume,
        };
    }

    private static int FindVoice()
    {
        // A free voice, else the oldest one-shot.
        for (var i = 0; i < VoiceCount; i++)
        {
            if (!Voices[i].Looping && Voices[i].Source.State != AlSourceState.Playing)
                return i;
        }

        for (var i = 0; i < VoiceCount; i++)
        {
            if (!Voices[i].Looping && Voices[i].Category != AudioCategory.Voice)
                return i;
        }

        return -1;
    }

    /// Puts a voice where a sound's placed, or on the listener if it isn't.
    private static void Place(ref Voice voice, Placement? at)
    {
        if (at is { } p)
        {
            voice.Source.SourceRelative = false;
            voice.Source.Position = p.Position;
            voice.Source.Velocity = p.Velocity;
            voice.Source.ReferenceDistance = p.ReferenceDistance;
            voice.Source.RolloffFactor = 1f;
            voice.Source.MaxDistance = p.Range > 0f ? p.Range : 20000f;
            voice.Source.AirAbsorptionFactor = AirAbsorption;
            var d = p.Position - _listener;
            voice.Range = p.Range;
            voice.Distance = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        }
        else
        {
            voice.Source.SourceRelative = true;
            voice.Source.Position = default;
            voice.Source.Velocity = default;
            voice.Source.AirAbsorptionFactor = 0f;
            voice.Range = voice.Distance = 0f;
        }
    }

    /// What a voice's gain should be: its volume, its category's, and how far into its range's fade-out it is.
    private static float Gain(in Voice voice)
    {
        var gain = voice.Volume * CategoryGain(voice.Category);
        if (voice.Range <= 0f)
            return gain;

        var t = Math.Clamp(1f - voice.Distance / voice.Range, 0f, 1f);
        return gain * MathF.Pow(t, RangeCurve);
    }

    /// Plays a sound once, where it's placed if it is.
    public static LoopHandle Play(string name, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f, Placement? at = null)
    {
        if (!Available || !Sounds.TryGetValue(name, out var sound))
            return LoopHandle.None;

        var index = FindVoice();
        if (index < 0)
            return LoopHandle.None;

        ref var voice = ref Voices[index];
        voice.Source.Stop();
        voice.Source.Buffer = sound.Buffer;
        voice.Source.Looping = false;
        Place(ref voice, at);
        voice.Source.Pitch = pitch;
        voice.Category = category;
        voice.Volume = volume;
        voice.Fade = 0f;
        voice.Generation++;
        voice.Source.Gain = Gain(voice);
        voice.Source.Play();
        return new LoopHandle(index, voice.Generation);
    }

    /// Plays a sound once at a point in the world. It's at full volume within referenceDistance metres and falls off
    /// as reference / distance beyond, panned by where it is from the listener.
    public static LoopHandle PlayAt(string name, float3 position, float referenceDistance, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f) =>
        Play(name, category, volume, pitch, new Placement(position, default, referenceDistance));

    /// Where placed sounds are heard from: a position, which way is forward and up, and how fast it's moving (for Doppler).
    public static void SetListener(float3 position, float3 forward, float3 up, float3 velocity = default)
    {
        if (!Available)
            return;

        Al.Listener.Position = position;
        _listener = position;
        Al.Listener.Orientation = (forward, up);
        Al.Listener.Velocity = velocity;
    }

    /// Moves a playing sound to a new place (one that moves with something).
    public static void Move(LoopHandle handle, Placement at)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return;

        Place(ref Voices[handle.Index], at);
    }

    /// Plays a sound once after a delay in seconds (sound arriving after the flash).
    public static void PlayDelayed(string name, float delay, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f)
    {
        if (Available)
            Queue.Add(new Pending(Time.Time.Now + delay, name, category, volume, pitch));
    }

    public static LoopHandle PlayLoop(string name, AudioCategory category = AudioCategory.Effects, float volume = 1f, Placement? at = null)
    {
        if (!Available || !Sounds.TryGetValue(name, out var sound))
            return LoopHandle.None;

        var index = FindVoice();
        if (index < 0)
            return LoopHandle.None;

        ref var voice = ref Voices[index];
        voice.Source.Stop();
        voice.Source.Buffer = sound.Buffer;
        voice.Source.Looping = true;
        Place(ref voice, at);
        voice.Source.Pitch = 1f;
        voice.Category = category;
        voice.Volume = volume;
        voice.Looping = true;
        voice.Fade = 0f;
        voice.Generation++;
        voice.Source.Gain = Gain(voice);
        voice.Source.Play();
        return new LoopHandle(index, voice.Generation);
    }

    public static void SetLoopVolume(LoopHandle handle, float volume, float pitch = 1f)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return;

        Voices[handle.Index].Volume = volume;
        Voices[handle.Index].Source.Pitch = pitch;
    }

    public static void StopLoop(LoopHandle handle)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return;

        ref var voice = ref Voices[handle.Index];
        voice.Source.Stop();
        voice.Looping = false;
        voice.Generation++;
    }

    /// Lets a loop play out to the end of the time round it's on and stop there (a bell finishing its swing). The handle
    /// still moves it until it's done.
    public static void EndLoop(LoopHandle handle)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return;

        ref var voice = ref Voices[handle.Index];
        voice.Source.Looping = false;
        voice.Looping = false;
    }

    /// Turns a loop down to silence over some seconds, then stops it.
    public static void FadeLoop(LoopHandle handle, float seconds)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return;

        ref var voice = ref Voices[handle.Index];
        voice.Fade = MathF.Max(voice.Volume, 0.001f) / MathF.Max(seconds, 0.001f);
        voice.Generation++;
    }

    /// Starts a sound loaded with LoadSustained: its attack, then its sustain over and over until ReleaseSustained.
    public static LoopHandle PlaySustained(string name, AudioCategory category = AudioCategory.Effects, float volume = 1f, Placement? at = null) =>
        PlayLoop(name, category, volume, at);

    /// Lets go of a sustained sound: crossfades from wherever it's got to into its release, which it returns (to move it).
    public static LoopHandle ReleaseSustained(LoopHandle handle, string name, AudioCategory category = AudioCategory.Effects, float volume = 1f, Placement? at = null)
    {
        if (!handle.IsValid || Voices[handle.Index].Generation != handle.Generation)
            return LoopHandle.None;

        FadeLoop(handle, SustainFade);
        return Play(name + ".end", category, volume, 1f, at);
    }

    /// Stops everything but the music (leaving a mission).
    public static void StopAll()
    {
        if (!Available)
            return;

        Queue.Clear();
        for (var i = 0; i < VoiceCount; i++)
        {
            Voices[i].Source.Stop();
            Voices[i].Looping = false;
            Voices[i].Fade = 0f;
            Voices[i].Generation++;
        }
    }

    /// Fades to a track (None for silence). Asking for the one already playing carries on with it.
    public static void PlayMusic(MusicTrack track) => MusicPlayer.Play(track);

    public static MusicTrack CurrentMusic => MusicPlayer.Track;

    private static void StartStream()
    {
        foreach (var buffer in MusicBuffers)
            FillMusic(buffer);

        _music.Looping = false;
        _music.QueueBuffers(MusicBuffers);
        _music.Play();
    }

    private static void FillMusic(AlBuffer buffer)
    {
        // A block can come back empty where one file ends and the next opens; try again, then fall back to silence.
        var frames = 0;
        for (var tries = 0; frames == 0 && tries < 4; tries++)
            frames = MusicPlayer.Render(MusicScratch);

        if (frames == 0)
        {
            frames = StreamFrames;
            Array.Clear(MusicScratch);
        }

        buffer.SetData(AlFormat.Stereo16, MemoryMarshal.AsBytes(MusicScratch.AsSpan(0, frames * 2)), MusicPlayer.SampleRate);
    }

    /// Every frame: feeds the music, starts delayed sounds and applies the volume settings.
    public static void Update()
    {
        if (!Available)
            return;

        var settings = Settings.Settings.Current;
        Al.Listener.Gain = settings.MasterVolume;

        var now = Time.Time.Now;
        var dt = _lastUpdate < 0.0 ? 0f : (float)Math.Min(now - _lastUpdate, 0.1);
        _lastUpdate = now;

        for (var i = 0; i < VoiceCount; i++)
        {
            ref var voice = ref Voices[i];
            if (voice.Fade > 0f)
            {
                voice.Volume -= voice.Fade * dt;
                if (voice.Volume <= 0f)
                {
                    voice.Source.Stop();
                    voice.Looping = false;
                    voice.Fade = 0f;
                    voice.Volume = 0f;
                }
            }

            if (voice.Looping || voice.Source.State == AlSourceState.Playing)
                voice.Source.Gain = Gain(voice);
        }

        for (var i = Queue.Count - 1; i >= 0; i--)
        {
            if (Queue[i].At > now)
                continue;

            var p = Queue[i];
            Queue.RemoveAt(i);
            Play(p.Name, p.Category, p.Volume, p.Pitch);
        }

        UpdateMusic(settings.MusicVolume);
    }

    private static void UpdateMusic(float volume)
    {
        _music.Gain = volume;
        var processed = _music.BuffersProcessed;
        Span<AlBuffer> one = stackalloc AlBuffer[1];

        while (processed-- > 0)
        {
            _music.UnqueueBuffers(one);
            FillMusic(one[0]);
            _music.QueueBuffers(one);
        }

        // Starved (a long load): start again.
        if (_music.State != AlSourceState.Playing)
            _music.Play();
    }

    public static void Shutdown()
    {
        if (!Available)
            return;

        StopAll();
        _music.Stop();
        Al.DeleteSources([_music]);
        Al.DeleteSources(Voices.Select(v => v.Source).ToArray());
        Al.DeleteBuffers(MusicBuffers);
        Al.DeleteBuffers(Sounds.Values.Select(s => s.Buffer).ToArray());
        Sounds.Clear();
        MusicPlayer.Dispose();
        Context.MakeCurrent(null);
        _context?.Dispose();
        _device?.Dispose();
        Available = false;
    }
}
