using RailWerkz.Core.Audio;

namespace Highball.Trains;

/// What an engine's sound follows each frame.
/// Throttle 0 idle to 1 notch 8; Load how hard it's working, 0 to 1; Starting seconds until it's running (0 running);
/// Compressing its air compressor's running; Dynamic the dynamic brake, 0 to 1. Turning: it's been started (or is running);
/// RunDown, while it's shut off and running down, 1 at the cut falling to 0 at a stop. Isolated: the isolation switch is in
/// Start/Stop, and it idles at low idle where it has one. Revs, 0 idle to 1 notch 8, the simulation's governor's (which
/// the compressor pumps with); negative to have the sound wind its own towards the throttle.
public readonly record struct EngineInputs(float Throttle, float Load, float Starting, bool Compressing, float Dynamic, bool Turning = true, float RunDown = 0f,
    bool Isolated = false, float Revs = -1f);

/// One locomotive's prime mover and what it drives, heard from its body.
///
/// Isolated (the isolation switch in Start/Stop), an engine with a low idle settles down to it, and back up to idle once
/// it's put on line, the two idle loops crossfaded over LowIdleSeconds.
///
/// It starts from cold as the train's placed (the startup recording, with the idle loop brought in as it ends). The
/// revs follow the throttle at the engine's own pace (it takes SpoolUp seconds to wind up from idle to notch 8). The
/// idle loop is pitched up and the full-throttle loop pitched up to its own as they crossfade between; or, for an engine
/// whose rpm are given, the revs are turned into rpm and each of its recordings pitched to them from its own, the two
/// either side crossfaded, so every notch is heard from the recording nearest it. The exhaust chug
/// comes in with the load, heaviest when it's labouring at low revs. Its air compressor cuts in and out with the main
/// reservoir's governor, and the dynamic brake's grid blowers spin up and down with the brake.
public sealed class EngineSound(EngineSoundDef def, float detune, string vehicle = "")
{
    /// This locomotive's tuning and the mix's, read every frame so the Audio Editor's changes are heard at once.
    private AudioTuning.VehicleTuning Tuned => AudioTuning.Current.For(vehicle);
    private static AudioTuning.MixTuning Mix => AudioTuning.Current.Mix;
    private float EngineLevel => def.Volume * Tuned.Engine * Mix.Engines * Mix.Master;
    private float EnginePitch => detune * Tuned.EnginePitch;
    private float ChugLevel => def.Volume * Tuned.Chug * Mix.Engines * Mix.Master;
    private float TurboLevel => def.Volume * Tuned.Turbo * Mix.Engines * Mix.Master;
    private float StartupLevel => def.Volume * Tuned.Startup * Mix.Engines * Mix.Master;
    private float AirLevel => def.Volume * Tuned.AirCompressor * Mix.AirCompressor * Mix.Master;
    private float FanLevel => def.Volume * Tuned.BrakeFan * Mix.Engines * Mix.Master;

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
    private AudioEngine.LoopHandle _lowIdle = AudioEngine.LoopHandle.None;

    /// How far it's settled to low idle: 1 there, 0 at idle. Starts there, as it's started isolated.
    private float _low = 1f;

    /// The turbocharger's whine, and how fast it's turning, 0 stopped to 1 flat out.
    private AudioEngine.LoopHandle _turbo = AudioEngine.LoopHandle.None;
    private float _turboSpeed;

    /// Its recordings by rpm, lowest first, and each one's loop; empty for an engine sounded by the idle and run loops.
    private readonly IReadOnlyList<EngineSoundLoop> _ladder = def.Ladder;
    private readonly AudioEngine.LoopHandle[] _rungs = new AudioEngine.LoopHandle[def.Ladder.Count];
    private bool _started, _startupPlayed, _compressing;
    private float _revs, _load, _fanSpeed, _compressorIn = -1f;

    /// How much of the engine's turning to be heard: 1 running or starting, down to 0 as it runs down to a stop.
    private float _alive = 1f;

    /// 0 at idle to 1 at full revs, as the engine has got to.
    public float Revs => _revs;

    /// compressorAt, fanAt: where the air compressor and the brake blowers are, if not where the engine is.
    public void Update(float dt, EngineInputs input, AudioEngine.Placement at, AudioEngine.Placement? compressorAt = null, AudioEngine.Placement? fanAt = null)
    {
        var aux = (compressorAt ?? at) with { ReferenceDistance = AuxReference, Range = AuxRange };
        var fanAux = (fanAt ?? at) with { ReferenceDistance = AuxReference, Range = AuxRange };
        if (!_started)
        {
            _started = true;
            // Each loop starts silent; the gains below bring them in this same frame.
            for (var i = 0; i < _rungs.Length; i++)
                _rungs[i] = SoundFiles.Loop(_ladder[i].Path, def.EngineGainDb) is { } rung ? AudioEngine.PlayLoop(rung, volume: 0f, at: at) : AudioEngine.LoopHandle.None;
            if (_rungs.Length == 0 && SoundFiles.Loop(def.Idle, def.EngineGainDb) is { } idle)
                _idle = AudioEngine.PlayLoop(idle, volume: 0f, at: at);
            if (_rungs.Length == 0 && SoundFiles.Loop(def.Run, def.EngineGainDb) is { } run)
                _run = AudioEngine.PlayLoop(run, volume: 0f, at: at);
            if (SoundFiles.Loop(def.Chug, def.ChugGainDb) is { } chug)
                _chug = AudioEngine.PlayLoop(chug, volume: 0f, at: at);
            if (SoundFiles.Loop(def.LowIdle, def.EngineGainDb) is { } lowLoop)
                _lowIdle = AudioEngine.PlayLoop(lowLoop, volume: 0f, at: at);
            if (SoundFiles.Loop(def.Turbo, def.TurboGainDb) is { } turbo)
                _turbo = AudioEngine.PlayLoop(turbo, volume: 0f, at: at);
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
                    _startup = AudioEngine.Play(startup, volume: StartupLevel, at: at);
            }
        }
        else
            _startupPlayed = true;

        // The revs: the simulation's governor's, or (given none) wound up and down here at a steady rate, easing in at the end.
        if (input.Revs >= 0f)
            _revs = Math.Clamp(input.Revs, 0f, 1f);
        else
        {
            var target = input.Starting > 0f ? 0f : Math.Clamp(input.Throttle, 0f, 1f);
            var rate = target > _revs ? 1f / MathF.Max(def.SpoolUp, 0.1f) : 1f / MathF.Max(def.SpoolDown, 0.1f);
            var step = rate * dt * MathF.Min(1f, 0.25f + 4f * MathF.Abs(target - _revs));
            _revs = target > _revs ? MathF.Min(target, _revs + step) : MathF.Max(target, _revs - step);
        }
        _load += (Math.Clamp(input.Load, 0f, 1f) - _load) * MathF.Min(1f, dt / 0.6f);

        var sag = input.Turning ? 1f : 0.55f + 0.45f * _alive;

        // Low idle while isolated, eased between at a steady rate; the idle loop gives way to it, equal-power. Should the
        // revs rise while it's still settling, it goes as they do.
        var low = _lowIdle.IsValid && input.Isolated ? 1f : 0f;
        var settle = dt / MathF.Max(def.LowIdleSeconds, 0.1f);
        _low = low > _low ? MathF.Min(low, _low + settle) : MathF.Max(low, _low - settle);
        var lowGain = MathF.Sin(_low * MathF.PI * 0.5f) * MathF.Max(0f, 1f - 4f * _revs);
        var idleShare = _lowIdle.IsValid ? MathF.Cos(_low * MathF.PI * 0.5f) : 1f;
        AudioEngine.SetLoopVolume(_lowIdle, lowGain * EngineLevel * running, def.LowIdlePitch * EnginePitch * sag);

        if (_rungs.Length > 0)
            Rungs(running * idleShare, running, sag);
        else
        {
            // Equal-power crossfade, pitched so the two meet in the middle.
            var w = _revs * MathF.PI * 0.5f;
            var idlePitch = (1f + _revs * (def.IdlePitchTop - 1f)) * EnginePitch * sag;
            var runPitch = (def.RunPitchBottom + _revs * (1f - def.RunPitchBottom)) * EnginePitch;
            AudioEngine.SetLoopVolume(_idle, MathF.Cos(w) * EngineLevel * running * idleShare, idlePitch);
            AudioEngine.SetLoopVolume(_run, MathF.Sin(w) * EngineLevel * (0.85f + 0.15f * _load) * running, runPitch);
        }
        AudioEngine.SetLoopVolume(_chug, _load * (0.35f + 0.45f * (1f - _revs)) * ChugLevel * running, (0.85f + 0.3f * _revs) * detune);

        Turbo(dt, input);

        Compressor(dt, input.Compressing && input.Starting <= 0f && input.Turning, aux);
        Fan(dt, input.Dynamic, fanAux);

        AudioEngine.Move(_idle, at);
        AudioEngine.Move(_run, at);
        foreach (var rung in _rungs)
            AudioEngine.Move(rung, at);
        AudioEngine.Move(_chug, at);
        AudioEngine.Move(_lowIdle, at);
        AudioEngine.Move(_turbo, at);
        AudioEngine.Move(_startup, at);
    }

    /// The revs as rpm, heard from the two recordings either side of them, equal-power crossfaded and each pitched from the
    /// rpm it was made at to the engine's (below the lowest or above the highest, that one alone). Those above idle swell
    /// a little with the load, as the run loop does.
    private void Rungs(float idleRunning, float running, float sag)
    {
        var rpm = def.IdleRpm + _revs * (def.MaxRpm - def.IdleRpm);
        var below = 0;
        while (below < _ladder.Count - 1 && _ladder[below + 1].Rpm <= rpm)
            below++;
        var above = Math.Min(below + 1, _ladder.Count - 1);
        var t = above == below ? 0f : Math.Clamp((rpm - _ladder[below].Rpm) / (_ladder[above].Rpm - _ladder[below].Rpm), 0f, 1f);
        for (var i = 0; i < _rungs.Length; i++)
        {
            var gain = i == below ? MathF.Cos(t * MathF.PI * 0.5f) : i == above ? MathF.Sin(t * MathF.PI * 0.5f) : 0f;
            var labour = i == 0 ? 1f : 0.85f + 0.15f * _load;
            AudioEngine.SetLoopVolume(_rungs[i], gain * labour * EngineLevel * (i == 0 ? idleRunning : running), rpm / _ladder[i].Rpm * EnginePitch * sag);
        }
    }

    /// The turbocharger: driven by the exhaust, so it follows the revs and more so the load, lagging behind as it spools up
    /// and coasting down slower still, on past a shutdown (it's not cut short with the engine's sound). Barely heard at
    /// idle, it climbs in pitch and swells as it comes up to speed.
    private void Turbo(float dt, EngineInputs input)
    {
        if (!_turbo.IsValid)
            return;
        var target = input.Turning && input.Starting <= 0f ? 0.12f + 0.88f * _revs * (0.55f + 0.45f * _load) : 0f;
        _turboSpeed += (target - _turboSpeed) * MathF.Min(1f, dt / (target > _turboSpeed ? 2.5f : 5f));
        AudioEngine.SetLoopVolume(_turbo, MathF.Pow(_turboSpeed, 1.5f) * def.TurboVolume * TurboLevel, (0.6f + 0.7f * _turboSpeed) * detune);
    }

    /// Cutting in: the start, then the loop once the start's done. Cutting out: the loop off and the unloader's blast.
    private void Compressor(float dt, bool on, AudioEngine.Placement at)
    {
        if (on && !_compressing)
        {
            _compressing = true;
            if (SoundFiles.Shot(def.CompressorStart) is { } start)
            {
                AudioEngine.Play(start, volume: 0.8f * AirLevel, at: at);
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
                AudioEngine.Play(stop, volume: 0.9f * AirLevel, at: at);
        }

        if (_compressorIn >= 0f)
        {
            _compressorIn -= dt;
            if (_compressorIn < 0f && SoundFiles.Loop(def.CompressorLoop) is { } loop)
                _compressor = AudioEngine.PlayLoop(loop, volume: 0.7f * AirLevel, at: at);
        }

        AudioEngine.SetLoopVolume(_compressor, 0.7f * AirLevel); // live, as it's tuned
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

        AudioEngine.SetLoopVolume(_fan, MathF.Pow(_fanSpeed, 0.6f) * FanLevel, 0.6f + 0.45f * _fanSpeed);
        AudioEngine.Move(_fan, at);
    }

    public void Stop()
    {
        foreach (var handle in (ReadOnlySpan<AudioEngine.LoopHandle>)[_idle, _run, _chug, _compressor, _fan, _lowIdle, _turbo, .. _rungs])
            AudioEngine.FadeLoop(handle, 0.3f);
        Array.Fill(_rungs, AudioEngine.LoopHandle.None);
        AudioEngine.StopLoop(_startup);
        _idle = _run = _chug = _compressor = _fan = _startup = _lowIdle = _turbo = AudioEngine.LoopHandle.None;
        _turboSpeed = 0f;
        _low = 1f;
        _started = false;
    }
}
