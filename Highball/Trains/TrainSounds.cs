using RailWerkz.Core.Audio;
using RailWerkz.Core.IO;

namespace Highball.Trains;

/// A train's bell and horn, picked from the WAVs in Content/Audio/Bells and Content/Audio/Horns, and heard from where
/// they are on the lead locomotive (folded to mono, so OpenAL places them). The bell is one swing, looped while it's on,
/// and finishes its swing when turned off. A horn file is an attack (its first HornAttack seconds), a sustain looped
/// while it's held, and a release (its last HornRelease seconds) when it's let go.
public sealed class TrainSounds
{
    private const float HornAttack = 1f;
    private const float HornRelease = 1f;

    /// How much the horns' presence (above 1.5 kHz) is turned up. The recordings have nearly all their sound at 300-1000 Hz
    /// and little of the 2-5 kHz bite a horn has close to, where the ear hears most: played as they are they sound far off and
    /// muffled, and quieter than they are.
    private const float HornBrighten = 10f;

    /// And how hard it's then pushed into a soft limiter: the recordings already peak at full scale, so being louder means
    /// rounding their peaks off (about 7 dB louder to the ear than the bells, as a horn should be, without clipping).
    private const float HornDrive = 8f;

    /// Within these many metres each is at full volume; beyond, it halves (-6 dB) every time the distance doubles.
    /// A horn is far louder than the engine under it (about 110 dB at 30 m against 85-90), so it holds full volume out to 40 m;
    /// the bell to 8 m, so it's heard over the engine idling near the cab.
    public const float HornReference = 80f;
    public const float BellReference = 8f;

    /// How far off each can be heard at all, in metres: it's turned down more and more all the way out to this, and is silent beyond.
    public const float HornRange = 2414f; // 1.5 miles

    public const float BellRange = 400f;

    private AudioEngine.LoopHandle _bell = AudioEngine.LoopHandle.None, _bellTail = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _horn = AudioEngine.LoopHandle.None, _hornEnd = AudioEngine.LoopHandle.None;
    private string? _bellSound, _hornSound;
    private static readonly HashSet<string> Loaded = [];

    // Where each is this frame, relative to the listener's origin.
    private AudioEngine.Placement? _hornAt, _bellAt;

    /// The horns and bells there are, by file name without .wav.
    public static IReadOnlyList<string> Horns { get; } = Find("Horns");
    public static IReadOnlyList<string> Bells { get; } = Find("Bells");

    private static List<string> Find(string folder)
    {
        var path = ContentPath.Audio(folder);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.wav").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    /// The picks, kept in the settings; an unknown one falls back to the first there is.
    public static string Horn
    {
        get => Pick(Horns, RailWerkz.Core.Settings.Settings.Current.TrainHorn);
        set => RailWerkz.Core.Settings.Settings.Current.TrainHorn = value;
    }

    public static string Bell
    {
        get => Pick(Bells, RailWerkz.Core.Settings.Settings.Current.TrainBell);
        set => RailWerkz.Core.Settings.Settings.Current.TrainBell = value;
    }

    private static string Pick(IReadOnlyList<string> all, string wanted) =>
        all.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? (all.Count > 0 ? all[0] : "");

    public bool BellRinging => _bell.IsValid;
    public bool HornBlowing => _horn.IsValid;

    /// Where the horn and bell are this frame (positions relative to the listener's origin, velocities in m/s); everything
    /// they're playing goes along.
    public void Move(AudioEngine.Placement horn, AudioEngine.Placement bell)
    {
        _hornAt = horn;
        _bellAt = bell;
        AudioEngine.Move(_horn, horn);
        AudioEngine.Move(_hornEnd, horn);
        AudioEngine.Move(_bell, bell);
        AudioEngine.Move(_bellTail, bell);
    }

    /// Rings the bell or stops it.
    public void SetBell(bool on)
    {
        // A different bell picked while it's ringing: change over to it.
        if (on && _bell.IsValid && _bellSound != Bell)
            SetBell(false);

        if (on == _bell.IsValid)
            return;

        if (!on)
        {
            AudioEngine.EndLoop(_bell);
            _bellTail = _bell;
            _bell = AudioEngine.LoopHandle.None;
            return;
        }

        var name = "bell:" + Bell;
        if (Loaded.Add(name) && !AudioEngine.Load(name, Path.Combine("Bells", Bell + ".wav"), mono: true))
            return;
        _bellSound = Bell;
        _bell = AudioEngine.PlayLoop(name, at: _bellAt);
    }

    /// Blows the horn while held is true; lets it go when it turns false.
    public void SetHorn(bool held)
    {
        if (held == _horn.IsValid)
            return;

        if (!held)
        {
            _hornEnd = AudioEngine.ReleaseSustained(_horn, "horn:" + _hornSound, at: _hornAt);
            _horn = AudioEngine.LoopHandle.None;
            return;
        }

        var name = "horn:" + Horn;
        if (Loaded.Add(name) && !AudioEngine.LoadSustained(name, Path.Combine("Horns", Horn + ".wav"), HornAttack, HornRelease, mono: true, brighten: HornBrighten, drive: HornDrive))
            return;
        _hornSound = Horn;
        _horn = AudioEngine.PlaySustained(name, at: _hornAt);
    }

    /// Silences both at once (the train's gone).
    public void Stop()
    {
        AudioEngine.StopLoop(_bell);
        AudioEngine.StopLoop(_bellTail);
        AudioEngine.FadeLoop(_horn, 0.05f);
        AudioEngine.StopLoop(_hornEnd);
        _bell = _bellTail = _horn = _hornEnd = AudioEngine.LoopHandle.None;
    }
}
