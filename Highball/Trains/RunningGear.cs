using RailWerkz.Core.Audio;

namespace Highball.Trains;

/// The sound of the running gear, from Content/Audio/Trucks/Set0.
///
/// Every locomotive's traction motors whine with its speed, louder working (or braking dynamically). The wheels aren't
/// heard car by car: a few emitters ride on whichever trucks are nearest the listener, each with its own rolling loops,
/// chosen at random, as it takes up a truck: slow rolling at walking pace, crossfading to axle roll at speed. Each
/// also puts in flange squeal on a curve, brake squeal stopping, and a clank now and then from a rail joint.
public sealed class RunningGear
{
    private const string Folder = "Trucks/Set0";
    private const int Emitters = 3;
    private const float WheelReference = 6f;
    private const float WheelRange = 400f;
    private const float MotorReference = 6f;
    private const float MotorRange = 900f;
    private const double WheelHeight = 0.6;
    private const double MotorHeight = 0.9;
    /// Every running gear sound's level is scaled by this: a touch under 1, so it sits under the rest of the mix.
    private const float Level = 0.9f;

    private static readonly string[] Slow = SoundFiles.Matching(Folder, "slowRolling");
    private static readonly string[] Roll = SoundFiles.Matching(Folder, "axleRoll");
    private static readonly string[] Flange = SoundFiles.Matching(Folder, "flange");
    private static readonly string[] Squeal = SoundFiles.Matching(Folder, "brakeSqueal");
    private static readonly string[] Clank = SoundFiles.Matching(Folder, "axleClank");
    private const string MotorLow = Folder + "/tractionMotorLow.wav";
    private const string MotorHigh = Folder + "/tractionMotorHi.wav";
    private const string WheelSlipSound = Folder + "/wheelSlip.wav";

    private sealed class Wheel
    {
        public int Car = -1;
        public bool Front;
        public AudioEngine.LoopHandle Slow = AudioEngine.LoopHandle.None, Roll = AudioEngine.LoopHandle.None;
        public float Pitch = 1f;
        public double ToClank;
        public float Gain;
    }

    private sealed class Motor
    {
        public int Car;
        public AudioEngine.LoopHandle Low = AudioEngine.LoopHandle.None, High = AudioEngine.LoopHandle.None, Slip = AudioEngine.LoopHandle.None;
        public float Effort;
    }

    private readonly Random _random = new();
    private readonly Wheel[] _wheels = Enumerable.Range(0, Emitters).Select(_ => new Wheel()).ToArray();
    private readonly List<Motor> _motors = [];
    private readonly List<(double Distance, int Car, bool Front)> _nearest = [];
    private TrainBody? _for;

    private string? Pick(string[] from) => from.Length == 0 ? null : from[_random.Next(from.Length)];

    /// Every frame, after the train's drawn: each car's truck centres where they're drawn, and the listener's position
    /// (world metres), which the sounds are placed relative to.
    public void Update(float dt, TrainBody body, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        if (!ReferenceEquals(body, _for))
        {
            Stop();
            _for = body;
            for (var i = 0; i < body.Count; i++)
                if (body.Cars[i].Def is { Kind: VehicleKind.Locomotive, Power: > 0.0 })
                    _motors.Add(new Motor { Car = i });
        }

        if (trucks.Count != body.Count)
            return;

        foreach (var motor in _motors)
            UpdateMotor(dt, motor, body, trucks, listener);
        Assign(body, trucks, listener);
        foreach (var wheel in _wheels)
            UpdateWheel(dt, wheel, body, trucks, listener);
    }

    private static AudioEngine.Placement At(TrainBody body, int car, (double X, double Y, double Z) p, ((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear) pair, double up,
        (double X, double Y, double Z) listener, float reference, float range)
    {
        var (dx, dy, dz) = (pair.Front.X - pair.Rear.X, pair.Front.Y - pair.Rear.Y, pair.Front.Z - pair.Rear.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var v = body.Velocity[car];
        return new AudioEngine.Placement(new float3((float)(p.X - listener.X), (float)(p.Y + up - listener.Y), (float)(p.Z - listener.Z)),
            new float3((float)(dx / length * v), (float)(dy / length * v), (float)(dz / length * v)), reference, range);
    }

    /// The motors whine up with speed: the low loop at walking pace, the high one at line speed, pitched along with it.
    private void UpdateMotor(float dt, Motor motor, TrainBody body, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        var i = motor.Car;
        var pair = trucks[i];
        var middle = ((pair.Front.X + pair.Rear.X) * 0.5, (pair.Front.Y + pair.Rear.Y) * 0.5, (pair.Front.Z + pair.Rear.Z) * 0.5);
        var at = At(body, i, middle, pair, MotorHeight, listener, MotorReference, MotorRange);
        if (!motor.Low.IsValid && SoundFiles.Loop(MotorLow) is { } low)
            motor.Low = AudioEngine.PlayLoop(low, volume: 0f, at: at);
        if (!motor.High.IsValid && SoundFiles.Loop(MotorHigh) is { } high)
            motor.High = AudioEngine.PlayLoop(high, volume: 0f, at: at);

        var def = body.Cars[i].Def;
        var speed = (float)Math.Abs(body.Velocity[i]);
        var working = def.StartingTractiveEffort > 0.0
            ? (float)Math.Clamp((Math.Abs(body.Traction[i]) + body.DynamicBraking[i]) / def.StartingTractiveEffort * 2.5, 0.0, 1.0)
            : 0f;
        motor.Effort += (working - motor.Effort) * MathF.Min(1f, dt / 0.5f);
        var moving = Math.Clamp(speed / 2f, 0f, 1f);
        var level = moving * (0.25f + 0.75f * motor.Effort);
        var w = Math.Clamp(speed / 25f, 0f, 1f) * MathF.PI * 0.5f;
        AudioEngine.SetLoopVolume(motor.Low, MathF.Cos(w) * level * 0.8f * Level, Math.Clamp(0.55f + speed / 14f, 0.55f, 1.8f));
        AudioEngine.SetLoopVolume(motor.High, MathF.Sin(w) * level * 0.8f * Level, Math.Clamp(0.6f + speed / 30f, 0.6f, 1.6f));
        AudioEngine.Move(motor.Low, at);
        AudioEngine.Move(motor.High, at);

        // Its wheels spinning: the slip loop, up and higher the harder they spin, only while they do.
        var slip = (float)body.WheelSlip[i];
        if (slip > 0.02f && !motor.Slip.IsValid && SoundFiles.Loop(WheelSlipSound) is { } spinning)
            motor.Slip = AudioEngine.PlayLoop(spinning, volume: 0f, at: at);
        if (motor.Slip.IsValid)
        {
            var wheelAt = At(body, i, middle, pair, WheelHeight, listener, WheelReference, WheelRange);
            AudioEngine.SetLoopVolume(motor.Slip, Math.Clamp(slip * 1.4f, 0f, 1f) * Level, 0.85f + 0.35f * slip + Math.Clamp(speed / 40f, 0f, 0.3f));
            AudioEngine.Move(motor.Slip, wheelAt);
            if (slip <= 0.01f)
            {
                AudioEngine.FadeLoop(motor.Slip, 0.3f);
                motor.Slip = AudioEngine.LoopHandle.None;
            }
        }
    }

    /// Keeps the emitters on the nearest trucks. One moves only once its truck's dropped well out of the nearest few, so
    /// they don't hop about between trucks the same distance off.
    private void Assign(TrainBody body, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        _nearest.Clear();
        for (var i = 0; i < trucks.Count; i++)
        {
            _nearest.Add((Apart(trucks[i].Front, listener), i, true));
            _nearest.Add((Apart(trucks[i].Rear, listener), i, false));
        }

        _nearest.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var keep = Math.Min(_nearest.Count, Emitters * 2);
        foreach (var wheel in _wheels)
        {
            var still = false;
            for (var k = 0; k < keep && !still; k++)
                still = _nearest[k].Car == wheel.Car && _nearest[k].Front == wheel.Front;
            if (!still)
                Release(wheel);
        }

        foreach (var wheel in _wheels.Where(w => w.Car < 0))
        {
            for (var k = 0; k < Math.Min(_nearest.Count, Emitters); k++)
            {
                var (_, car, front) = _nearest[k];
                if (_wheels.Any(w => w.Car == car && w.Front == front))
                    continue;
                wheel.Car = car;
                wheel.Front = front;
                // Each truck rolls a little differently; the clanks come at random, every 15 to 45 m.
                wheel.Pitch = 0.94f + (float)_random.NextDouble() * 0.12f;
                wheel.ToClank = 15.0 + _random.NextDouble() * 30.0;
                wheel.Gain = 0f;
                break;
            }
        }
    }

    private static double Apart((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static void Release(Wheel wheel)
    {
        AudioEngine.FadeLoop(wheel.Slow, 0.4f);
        AudioEngine.FadeLoop(wheel.Roll, 0.4f);
        wheel.Slow = wheel.Roll = AudioEngine.LoopHandle.None;
        wheel.Car = -1;
    }

    private void UpdateWheel(float dt, Wheel wheel, TrainBody body, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        if (wheel.Car < 0 || wheel.Car >= body.Count)
            return;
        var i = wheel.Car;
        var pair = trucks[i];
        var at = At(body, i, wheel.Front ? pair.Front : pair.Rear, pair, WheelHeight, listener, WheelReference, WheelRange);
        var speed = (float)Math.Abs(body.Velocity[i]);

        // Slow rolling comes in from a crawl and gives way to axle roll between about 13 and 22 mph.
        var slow = Smooth(0.2f, 2f, speed) * (1f - Smooth(6f, 10f, speed));
        var fast = Smooth(6f, 10f, speed) * (0.65f + 0.35f * Math.Clamp(speed / 30f, 0f, 1f));
        if (slow > 0.001f && !wheel.Slow.IsValid && SoundFiles.Loop(Pick(Slow)) is { } s)
            wheel.Slow = AudioEngine.PlayLoop(s, volume: 0f, at: at);
        if (fast > 0.001f && !wheel.Roll.IsValid && SoundFiles.Loop(Pick(Roll)) is { } r)
            wheel.Roll = AudioEngine.PlayLoop(r, volume: 0f, at: at);
        // A new emitter comes in gently rather than cutting in.
        wheel.Gain = MathF.Min(1f, wheel.Gain + dt / 0.5f);
        AudioEngine.SetLoopVolume(wheel.Slow, slow * 0.7f * wheel.Gain * Level, Math.Clamp(0.8f + speed / 10f, 0.8f, 1.3f) * wheel.Pitch);
        AudioEngine.SetLoopVolume(wheel.Roll, fast * wheel.Gain * Level, Math.Clamp(0.75f + speed / 40f, 0.75f, 1.45f) * wheel.Pitch);
        AudioEngine.Move(wheel.Slow, at);
        AudioEngine.Move(wheel.Roll, at);

        // Now and then: a joint, the flanges on a curve, the shoes biting as it slows.
        var car = body.Cars[i];
        wheel.ToClank -= speed * dt;
        if (wheel.ToClank <= 0.0)
        {
            wheel.ToClank = 15.0 + _random.NextDouble() * 30.0;
            if (speed > 1f && SoundFiles.Shot(Pick(Clank)) is { } clank)
                AudioEngine.Play(clank, volume: Math.Clamp(0.3f + speed / 40f, 0.3f, 0.8f) * Level, pitch: wheel.Pitch, at: at);
        }

        var sharp = (float)Math.Clamp(car.Curvature * 500.0, 0.0, 1.0); // full at a 500 m radius and tighter
        if (sharp > 0.15f && speed > 2f && _random.NextDouble() < 0.7 * sharp * dt && SoundFiles.Shot(Pick(Flange)) is { } flange)
            AudioEngine.Play(flange, volume: (0.35f + 0.4f * sharp) * Level, pitch: wheel.Pitch, at: at);

        if (body.Braking[i] > 2000.0 && speed is > 0.3f and < 7f && _random.NextDouble() < 0.5 * dt && SoundFiles.Shot(Pick(Squeal)) is { } squeal)
            AudioEngine.Play(squeal, volume: 0.6f * Level, pitch: wheel.Pitch, at: at);
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Stop()
    {
        foreach (var wheel in _wheels)
            Release(wheel);
        foreach (var motor in _motors)
        {
            AudioEngine.FadeLoop(motor.Low, 0.3f);
            AudioEngine.FadeLoop(motor.High, 0.3f);
            AudioEngine.FadeLoop(motor.Slip, 0.3f);
        }

        _motors.Clear();
        _for = null;
    }
}
