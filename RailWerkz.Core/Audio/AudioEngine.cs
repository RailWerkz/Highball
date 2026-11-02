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
    }

    private readonly record struct Pending(double At, string Name, AudioCategory Category, float Volume, float Pitch);

    /// Refers to a looping sound so it can be stopped or turned up and down.
    public readonly record struct LoopHandle(int Index, int Generation)
    {
        public static readonly LoopHandle None = new(-1, 0);
        public bool IsValid => Index >= 0;
    }

    private const int VoiceCount = 40;
    private const int StreamBuffers = 4;
    private const int StreamFrames = 8192;

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

    /// Loads a WAV from Content/Audio under a name. False (and a warning) if it can't.
    public static bool Load(string name, string relativePath)
    {
        if (!Available)
            return false;

        try
        {
            Sounds[name] = Upload(Wav.Load(ContentPath.Audio(relativePath)));
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Sound {name} ({relativePath}): {e.Message}");
            return false;
        }
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

    /// Plays a sound once.
    public static void Play(string name, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f)
    {
        if (!Available || !Sounds.TryGetValue(name, out var sound))
            return;

        var index = FindVoice();
        if (index < 0)
            return;

        ref var voice = ref Voices[index];
        voice.Source.Stop();
        voice.Source.Buffer = sound.Buffer;
        voice.Source.Looping = false;
        voice.Source.SourceRelative = true;
        voice.Source.Position = default;
        voice.Source.Pitch = pitch;
        voice.Category = category;
        voice.Volume = volume;
        voice.Source.Gain = volume * CategoryGain(category);
        voice.Source.Play();
    }

    /// Plays a sound once at a point in the world. It's at full volume within referenceDistance metres and falls off
    /// as reference / distance beyond, panned by where it is from the listener.
    public static void PlayAt(string name, float3 position, float referenceDistance, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f)
    {
        if (!Available || !Sounds.TryGetValue(name, out var sound))
            return;

        var index = FindVoice();
        if (index < 0)
            return;

        ref var voice = ref Voices[index];
        voice.Source.Stop();
        voice.Source.Buffer = sound.Buffer;
        voice.Source.Looping = false;
        voice.Source.SourceRelative = false;
        voice.Source.Position = position;
        voice.Source.ReferenceDistance = referenceDistance;
        voice.Source.RolloffFactor = 1f;
        voice.Source.MaxDistance = 20000f;
        voice.Source.Pitch = pitch;
        voice.Category = category;
        voice.Volume = volume;
        voice.Source.Gain = volume * CategoryGain(category);
        voice.Source.Play();
    }

    /// Where placed sounds are heard from: a position and which way is forward and up.
    public static void SetListener(float3 position, float3 forward, float3 up)
    {
        if (!Available)
            return;

        Al.Listener.Position = position;
        Al.Listener.Orientation = (forward, up);
    }

    /// Plays a sound once after a delay in seconds (sound arriving after the flash).
    public static void PlayDelayed(string name, float delay, AudioCategory category = AudioCategory.Effects, float volume = 1f, float pitch = 1f)
    {
        if (Available)
            Queue.Add(new Pending(Time.Time.Now + delay, name, category, volume, pitch));
    }

    public static LoopHandle PlayLoop(string name, AudioCategory category = AudioCategory.Effects, float volume = 1f)
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
        voice.Source.SourceRelative = true;
        voice.Source.Position = default;
        voice.Source.Pitch = 1f;
        voice.Category = category;
        voice.Volume = volume;
        voice.Looping = true;
        voice.Generation++;
        voice.Source.Gain = volume * CategoryGain(category);
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

        for (var i = 0; i < VoiceCount; i++)
        {
            ref var voice = ref Voices[i];
            if (voice.Looping || voice.Source.State == AlSourceState.Playing)
                voice.Source.Gain = voice.Volume * CategoryGain(voice.Category);
        }

        var now = Time.Time.Now;
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
