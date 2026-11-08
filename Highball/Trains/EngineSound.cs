using RailWerkz.Core.Audio;

namespace Highball.Trains;

/// What an engine's sound follows each frame.
/// Throttle 0 idle to 1 notch 8; Load how hard it's working, 0 to 1; Starting seconds until it's running (0 running);
/// Compressing its air compressor's running; Dynamic the dynamic brake, 0 to 1. Turning: it's been started (or is running);
/// RunDown, while it's shut off and running down, 1 at the cut falling to 0 at a stop.
public readonly record struct EngineInputs(float Throttle, float Load, float Starting, bool Compressing, float Dynamic, bool Turning = true, float RunDown = 0f);

/// One locomotive's prime mover and what it drives, heard from its body.
///
/// It starts from cold as the train's placed (the startup recording, with the idle loop brought in as it ends). The
/// revs follow the throttle at the engine's own pace (it takes SpoolUp seconds to wind up from idle to notch 8). The
/// idle loop is pitched up and the full-throttle loop pitched up to its own as they crossfade between. The exhaust chug
/// comes in with the load, heaviest when it's labouring at low revs. Its air compressor cuts in and out with the main
/// reservoir's governor, and the dynamic brake's grid blowers spin up and down with the brake.
public sealed class EngineSound(EngineSoundDef def, float detune)
{
    /// Within this many metres it's at full volume, and out to this range it can be heard at all. Kept close, so that a
    /// few metres off the engine already sits under the horn and bell, which carry much further (see TrainSounds).
    public const float Reference = 6f;
    public const float Range = 2500f;
    private const float AuxReference = 4f;
    private const float AuxRange = 800f;

    /// Seconds before it's running over which the idle comes in under the end of the startup.
    private const float IdleIn = 3f;

    private AudioEngine.LoopHandle _idle = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _run = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _chug = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _startup = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _compressor = AudioEngine.LoopHandle.None;
    private AudioEngine.LoopHandle _fan = AudioEngine.LoopHandle.None;
    private bool _started, _startupPlayed, _compressing;
    private float _revs, _load, _fanSpeed, _compressorIn = -1f;

    /// How much of the engine's turning to be heard: 1 running or starting, down to 0 as it runs down to a stop.
    private float _alive = 1f;

    /// 0 at idle to 1 at full revs, as the engine has got to.
    public float Revs => _revs;

    public void Update(float dt, EngineInputs input, AudioEngine.Placement at)
    {
        var aux = at with { ReferenceDistance = AuxReference, Range = AuxRange };
        if (!_started)
        {
            _started = true;
            // Each loop starts silent; the gains below bring them in this same frame.
            if (SoundFiles.Loop(def.Idle) is { } idle)
                _idle = AudioEngine.PlayLoop(idle, volume: 0f, at: at);
            if (SoundFiles.Loop(def.Run) is { } run)
                _run = AudioEngine.PlayLoop(run, volume: 0f, at: at);
            if (SoundFiles.Loop(def.Chug) is { } chug)
                _chug = AudioEngine.PlayLoop(chug, volume: 0f, at: at);
        }

        // Shut off: it runs down, lower and quieter, to silence; and the next start's recording is played afresh.
        if (!input.Turning)
        {
            _alive = MathF.Min(_alive, Math.Clamp(input.RunDown, 0f, 1f));
            if (_startup.IsValid && input.RunDown > 0f)
            {
                AudioEngine.FadeLoop(_startup, 0.6f);
                _startup = AudioEngine.LoopHandle.None;
            }
        }
        else
            _alive = 1f;

        // Starting: the recording once its turn comes (each unit's a few seconds after the last), the idle coming in under
        // its end. Nothing at all before then.
        var running = _alive;
        if (!input.Turning)
            _startupPlayed = false;
        else if (input.Starting > 0f)
        {
            running = Math.Clamp(1f - input.Starting / IdleIn, 0f, 1f);
            if (!_startupPlayed && input.Starting <= def.StartupSeconds)
            {
                _startupPlayed = true;
                if (SoundFiles.Shot(def.Startup) is { } startup)
                    _startup = AudioEngine.Play(startup, volume: def.Volume, at: at);
            }
        }
        else
            _startupPlayed = true;

        // The governor winds the revs up and down at a steady rate, easing in at the end.
        var target = input.Starting > 0f ? 0f : Math.Clamp(input.Throttle, 0f, 1f);
        var rate = target > _revs ? 1f / MathF.Max(def.SpoolUp, 0.1f) : 1f / MathF.Max(def.SpoolDown, 0.1f);
        var step = rate * dt * MathF.Min(1f, 0.25f + 4f * MathF.Abs(target - _revs));
        _revs = target > _revs ? MathF.Min(target, _revs + step) : MathF.Max(target, _revs - step);
        _load += (Math.Clamp(input.Load, 0f, 1f) - _load) * MathF.Min(1f, dt / 0.6f);

        // Equal-power crossfade, pitched so the two meet in the middle.
        var w = _revs * MathF.PI * 0.5f;
        var sag = input.Turning ? 1f : 0.55f + 0.45f * _alive;
        var idlePitch = (1f + _revs * (def.IdlePitchTop - 1f)) * detune * sag;
        var runPitch = (def.RunPitchBottom + _revs * (1f - def.RunPitchBottom)) * detune;
        AudioEngine.SetLoopVolume(_idle, MathF.Cos(w) * def.Volume * running, idlePitch);
        AudioEngine.SetLoopVolume(_run, MathF.Sin(w) * def.Volume * (0.85f + 0.15f * _load) * running, runPitch);
        AudioEngine.SetLoopVolume(_chug, _load * (0.35f + 0.45f * (1f - _revs)) * def.Volume * running, (0.85f + 0.3f * _revs) * detune);

        Compressor(dt, input.Compressing && input.Starting <= 0f && input.Turning, aux);
        Fan(dt, input.Dynamic, aux);

        AudioEngine.Move(_idle, at);
        AudioEngine.Move(_run, at);
        AudioEngine.Move(_chug, at);
        AudioEngine.Move(_startup, at);
    }

    /// Cutting in: the start, then the loop once the start's done. Cutting out: the loop off and the unloader's blast.
    private void Compressor(float dt, bool on, AudioEngine.Placement at)
    {
        if (on && !_compressing)
        {
            _compressing = true;
            if (SoundFiles.Shot(def.CompressorStart) is { } start)
            {
                AudioEngine.Play(start, volume: 0.8f * def.Volume, at: at);
                _compressorIn = MathF.Max(0.05f, AudioEngine.Duration(start) - 0.1f);
            }
            else
                _compressorIn = 0f;
        }
        else if (!on && _compressing)
        {
            _compressing = false;
            _compressorIn = -1f;
            AudioEngine.FadeLoop(_compressor, 0.1f);
            _compressor = AudioEngine.LoopHandle.None;
            if (SoundFiles.Shot(def.CompressorStop) is { } stop)
                AudioEngine.Play(stop, volume: 0.9f * def.Volume, at: at);
        }

        if (_compressorIn >= 0f)
        {
            _compressorIn -= dt;
            if (_compressorIn < 0f && SoundFiles.Loop(def.CompressorLoop) is { } loop)
                _compressor = AudioEngine.PlayLoop(loop, volume: 0.7f * def.Volume, at: at);
        }

        AudioEngine.Move(_compressor, at);
    }

    /// The blowers spin up over a couple of seconds and run down slower, louder and higher the harder the brake.
    private void Fan(float dt, float dynamic, AudioEngine.Placement at)
    {
        var target = Math.Clamp(dynamic, 0f, 1f);
        _fanSpeed += (target - _fanSpeed) * MathF.Min(1f, dt / (target > _fanSpeed ? 1.5f : 3f));
        if (_fanSpeed > 0.01f && !_fan.IsValid && SoundFiles.Loop(def.DynamicFan) is { } fan)
            _fan = AudioEngine.PlayLoop(fan, volume: 0f, at: at);
        else if (_fanSpeed <= 0.005f && _fan.IsValid)
        {
            AudioEngine.StopLoop(_fan);
            _fan = AudioEngine.LoopHandle.None;
        }

        AudioEngine.SetLoopVolume(_fan, MathF.Pow(_fanSpeed, 0.6f) * def.Volume, 0.6f + 0.45f * _fanSpeed);
        AudioEngine.Move(_fan, at);
    }

    public void Stop()
    {
        foreach (var handle in (ReadOnlySpan<AudioEngine.LoopHandle>)[_idle, _run, _chug, _compressor, _fan])
            AudioEngine.FadeLoop(handle, 0.3f);
        AudioEngine.StopLoop(_startup);
        _idle = _run = _chug = _compressor = _fan = _startup = AudioEngine.LoopHandle.None;
        _started = false;
    }
}
