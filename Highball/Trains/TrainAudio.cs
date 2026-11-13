using Highball.Builder;
using Highball.World;

namespace Highball.Trains;

/// The trains as they're heard: the listener at the camera, the horn and bell placed on the heard train's lead locomotive,
/// the engines of the locomotives nearest the camera, and the heard train's wheels and couplers. Only reads the trains and
/// how they're drawn this frame (TrainVisuals.CutState.Pose).
public sealed class TrainAudio
{
    /// A cut as drawn this frame: its train and each car's trucks.
    public readonly record struct Heard(Train Train, List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> Pose);

    // How high the horn (on the cab roof) and the bell (by the pilot) stand over the rail tops, in metres.
    private const double HornHeight = 4.6;
    private const double BellHeight = 1.2;

    // How high a prime mover sits over the rail tops, in metres: about the middle of the long hood.
    private const double EngineHeight = 3.2;

    /// The most locomotives heard at once: each engine takes about five of the 64 voices, and the horn, bell, wheels and
    /// couplers need theirs.
    private const int MaxEngineSounds = 8;

    private (double X, double Y, double Z)? _lastEar;
    private float3 _earVelocity;

    /// Each locomotive heard now, by the vehicle (so its sound carries on when cuts couple or part).
    private readonly Dictionary<TrainCar, EngineSound> _engines = [];
    private readonly RunningGear _gear = new();
    private readonly SlackSound _slack = new();

    /// After the camera's moved this frame: hears from the camera, and puts sounds' horn and bell on heard's lead
    /// locomotive (the driven cut, or the looked-at one; none, and the engines stop). The engines heard are the nearest of
    /// any of cuts. The listener is kept at the origin and the sounds placed relative to it, so it stays exact however far
    /// out the route goes.
    public void Hear(Camera camera, float dt, Heard? heard, IReadOnlyList<Heard> cuts, TrainSounds sounds)
    {
        // The camera's own speed, for Doppler: smoothed, and a jump (a teleport, a hitch) counted as standing still.
        var ear = (camera.X, camera.Y, camera.Z);
        if (_lastEar is { } was && dt > 0f)
        {
            var moved = new float3((float)(ear.X - was.X), (float)(ear.Y - was.Y), (float)(ear.Z - was.Z)) / dt;
            if (MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y + moved.Z * moved.Z) > 120f)
                moved = default;
            _earVelocity += (moved - _earVelocity) * MathF.Min(1f, dt / 0.15f);
        }

        _lastEar = ear;
        RailWerkz.Core.Audio.AudioEngine.SetListener(default, camera.Forward, camera.Up, _earVelocity);
        // The bus compressor over the world's sounds, as the mix has it (AudioTuning, set in the Audio Editor).
        var bus = AudioTuning.Current.Mix;
        RailWerkz.Core.Audio.AudioEngine.BusCompressor = new(bus.Compressor, bus.ThresholdDb, bus.Ratio, bus.AttackMs, bus.ReleaseMs, bus.MakeupDb);

        if (heard is not { } h || h.Pose.Count != h.Train.Cars.Count)
        {
            Stop();
            return;
        }

        var (train, pose) = (h.Train, h.Pose);
        var lead = Math.Max(0, train.Cars.FindIndex(c => c.Def.Kind == VehicleKind.Locomotive));
        var (front, rear) = (pose[lead].Front, pose[lead].Rear);
        // Moving along the track at the train's speed: towards its front, or back.
        var (dx, dy, dz) = (front.X - rear.X, front.Y - rear.Y, front.Z - rear.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var velocity = new float3((float)(dx / length * train.Speed), (float)(dy / length * train.Speed), (float)(dz / length * train.Speed));
        // Where its horn and bell are on its body (SoundPosition: as tuned, or as the model has them), turned with the body as
        // it's drawn, and how loud, as tuned.
        var car = train.Cars[lead];
        var (bx, by, bz) = car.Reversed ? (-dx, -dy, -dz) : (dx, dy, dz);
        var rotation = TrainVisuals.Facing(bx, by, bz);
        var middle = ((front.X + rear.X) * 0.5, (front.Y + rear.Y) * 0.5, (front.Z + rear.Z) * 0.5);
        float3 At(SoundSpot spot)
        {
            var p = VehicleOverlay.Rotate(rotation, SoundPosition(car, spot));
            return new((float)(middle.Item1 + p.X - camera.X), (float)(middle.Item2 + p.Y - camera.Y), (float)(middle.Item3 + p.Z - camera.Z));
        }

        var tuned = AudioTuning.Current.For(car.Def.Id);
        var mix = AudioTuning.Current.Mix;
        sounds.HornLevel = tuned.Horn * mix.Horn * mix.Master;
        sounds.BellLevel = tuned.Bell * mix.Bell * mix.Master;
        sounds.Move(new(At(SoundSpot.Horn), velocity, TrainSounds.HornReference, TrainSounds.HornRange),
            new(At(SoundSpot.Bell), velocity, TrainSounds.BellReference, TrainSounds.BellRange));
        Engines(train, pose, cuts, camera, dt);
    }

    /// Where a sound comes from on a locomotive, in its model's axes (x across, y up from the rail, z towards its front): as
    /// tuned in the Audio Editor, or else the game's own place for it. The engine, its air compressor and brake blowers in the
    /// middle of the long hood; the horn and bell as the model has them (the fixtures cut out of it), or over the front truck,
    /// the horn on the cab roof and the bell low.
    public static float3 SoundPosition(TrainCar car, SoundSpot spot)
    {
        var tuned = AudioTuning.Current.For(car.Def.Id);
        var engine = new float3(0f, (float)EngineHeight, 0f);
        var front = (float)(car.Def.Centres * 0.5);
        return spot switch
        {
            SoundSpot.Engine => AudioTuning.Point(tuned.EngineAt) ?? engine,
            SoundSpot.AirCompressor => AudioTuning.Point(tuned.AirCompressorAt) ?? AudioTuning.Point(tuned.EngineAt) ?? engine,
            SoundSpot.BrakeFan => AudioTuning.Point(tuned.BrakeFanAt) ?? AudioTuning.Point(tuned.EngineAt) ?? engine,
            SoundSpot.Horn => AudioTuning.Point(tuned.HornAt) ?? car.Def.Horn ?? new float3(0f, (float)HornHeight, front),
            _ => AudioTuning.Point(tuned.BellAt) ?? car.Def.Bell ?? new float3(0f, (float)BellHeight, front),
        };
    }

    /// The engines of the locomotives nearest the camera, whichever cut they're in (up to MaxEngineSounds), each heard from
    /// the middle of its own body: its train's throttle sets the revs, and how hard that train's pulling (its tractive
    /// effort, or its power once it's moving) is the load. The wheels and couplers are the heard cut's.
    private void Engines(Train train, List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> pose, IReadOnlyList<Heard> cuts,
        Camera camera, float dt)
    {
        // The wheels, motors and couplers, whether or not there are engine sounds.
        _gear.Update(dt, train.Body, pose, (camera.X, camera.Y, camera.Z));
        _slack.Update(dt, train.Body, pose, (camera.X, camera.Y, camera.Z));

        var near = new List<(Heard Cut, int Car, double Away)>();
        foreach (var c in cuts)
        {
            if (c.Pose.Count != c.Train.Cars.Count)
                continue;
            for (var i = 0; i < c.Train.Cars.Count; i++)
            {
                if (c.Train.Cars[i].Def.EngineSound is null)
                    continue;
                var (f, r) = (c.Pose[i].Front, c.Pose[i].Rear);
                var (dx, dz) = ((f.X + r.X) * 0.5 - camera.X, (f.Z + r.Z) * 0.5 - camera.Z);
                near.Add((c, i, dx * dx + dz * dz));
            }
        }

        var heard = near.OrderBy(n => n.Away).Take(MaxEngineSounds).ToList();
        foreach (var gone in _engines.Keys.Where(k => !heard.Any(h => ReferenceEquals(h.Cut.Train.Cars[h.Car], k))).ToList())
        {
            _engines[gone].Stop();
            _engines.Remove(gone);
        }

        foreach (var (cut, car, _) in heard)
        {
            var t = cut.Train;
            var vehicle = t.Cars[car];
            if (!_engines.TryGetValue(vehicle, out var sound))
                // Each a little off the others, so a lash-up of the same engines doesn't phase into one.
                _engines[vehicle] = sound = new EngineSound(vehicle.Def.EngineSound!, 1f + 0.012f * (_engines.Count % 3 - 1) + 0.004f * (_engines.Count / 3), vehicle.Def.Id);
            EngineVoice(t, cut.Pose, car, sound, camera, dt);
        }
    }

    /// One locomotive's engine sound this frame, from its train's controls and how hard it's pulling.
    private static void EngineVoice(Train train, List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> pose, int car, EngineSound sound,
        Camera camera, float dt)
    {
        // The revs come from the engine's governor in the simulation (they follow the notch in neutral too, and sit a little
        // above idle in gear); this throttle's only for an engine without one. With the reverser centred nothing loads it.
        var throttle = train.Reverser != 0 && train.Dynamic <= 0.0 ? train.Notch / (float)Train.Notches : 0f;
        var effort = train.Cars.Where(c => c.Def.Kind == VehicleKind.Locomotive).Sum(c => c.Def.StartingTractiveEffort);
        var traction = Math.Abs(train.Traction);
        var load = (float)Math.Max(effort > 0.0 ? traction / effort : 0.0, train.Power > 0.0 ? traction * Math.Abs(train.Speed) / train.Power : 0.0);
        var systems = train.Body.Systems[car];
        var onLine = systems?.MakesPower ?? true;
        var (front, rear) = (pose[car].Front, pose[car].Rear);
        var (dx, dy, dz) = (front.X - rear.X, front.Y - rear.Y, front.Z - rear.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var velocity = new float3((float)(dx / length * train.Speed), (float)(dy / length * train.Speed), (float)(dz / length * train.Speed));
        // The engine, air compressor and blowers where they are on its body (SoundPosition), turned as it's drawn.
        var vehicle = train.Cars[car];
        var (bx, by, bz) = vehicle.Reversed ? (-dx, -dy, -dz) : (dx, dy, dz);
        var rotation = TrainVisuals.Facing(bx, by, bz);
        float3 On(SoundSpot spot)
        {
            var p = VehicleOverlay.Rotate(rotation, SoundPosition(vehicle, spot));
            return new((float)((front.X + rear.X) * 0.5 + p.X - camera.X), (float)((front.Y + rear.Y) * 0.5 + p.Y - camera.Y),
                (float)((front.Z + rear.Z) * 0.5 + p.Z - camera.Z));
        }

        var at = On(SoundSpot.Engine);
        var inputs = new EngineInputs(onLine ? throttle : 0f, onLine ? load : 0f, (float)train.Body.Starting[car], train.Body.Brakes.Compressing,
            onLine && train.Reverser != 0 ? (float)train.Dynamic : 0f, systems?.Turning ?? true, (float)(systems?.RunDown ?? 0.0),
            systems?.Isolated ?? false, (float)(systems?.Revs ?? -1.0));
        sound.Update(dt, inputs, new(at, velocity, EngineSound.Reference, EngineSound.Range),
            new(On(SoundSpot.AirCompressor), velocity, EngineSound.Reference, EngineSound.Range),
            new(On(SoundSpot.BrakeFan), velocity, EngineSound.Reference, EngineSound.Range));
    }

    /// Silences the engines and the wheels.
    public void Stop()
    {
        foreach (var sound in _engines.Values)
            sound.Stop();
        _engines.Clear();
        _gear.Stop();
    }
}
