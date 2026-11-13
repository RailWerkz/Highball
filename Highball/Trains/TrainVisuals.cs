using Highball.Builder;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Utils;

namespace Highball.Trains;

/// The trains as the world draws them this frame: each vehicle's model where it stands between physics steps (so it runs
/// smoothly at any frame rate, see Train.Pose), its body on its springs, its locomotives' lit lamps and exhaust smoke, and
/// what can be picked and outlined. Only reads the trains.
public sealed class TrainVisuals : IDisposable
{
    /// What it takes to draw one cut smoothly from one frame to the next, kept with the cut.
    public sealed class CutState
    {
        /// Each car's trucks as drawn this frame.
        public readonly List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> Pose = [];

        /// Each car and its body as drawn last frame, for their motion.
        public readonly List<(float4 Rotation, double3 At, float4 BodyRotation, double3 BodyAt)> Previous = [];

        /// Each car body's roll on its springs.
        public readonly List<Suspension.Roll> Rolls = [];

        public void Clear()
        {
            Pose.Clear();
            Previous.Clear();
            Rolls.Clear();
        }
    }

    private readonly VehicleModels _models = new("Trains");
    private readonly List<ModelDraw> _draws = [];
    private readonly List<LocalLight> _lamps = [];
    private readonly ExhaustSmoke _smoke = new();
    private readonly VehicleOverlay _overlay = new();
    private readonly List<VehicleOverlay.Placed> _placed = [];
    private readonly List<Outlined> _outlined = [];

    /// The cars' models, to draw.
    public IReadOnlyList<ModelDraw> Draws => _draws;

    /// The locomotives' lit lamps.
    public IReadOnlyList<LocalLight> Lamps => _lamps;

    /// The locomotives' exhaust smoke, and the heat haze over it.
    public IReadOnlyList<SmokeSegment> Smoke => _smoke.Segments;
    public IReadOnlyList<HazeSource> Haze => _smoke.Haze;

    /// Each vehicle drawn, for picking and name tags.
    public IReadOnlyList<VehicleOverlay.Placed> Placed => _placed;

    /// Picking, tags and outline shapes.
    public VehicleOverlay Overlay => _overlay;

    /// Starts the frame: nothing drawn yet.
    public void Begin()
    {
        _draws.Clear();
        _placed.Clear();
        _lamps.Clear();
        _smoke.Begin();
    }

    /// Ends the frame: the smoke moves on.
    public void End(float dt) => _smoke.Update(dt);

    /// One cut's vehicles drawn where they are this frame, its bodies on their springs (moved on by step), and its engines'
    /// lamps and smoke. ground, if given, is the height of the ground at a place, for distributed power's radio.
    public void Draw(Train train, CutState state, float dt, double step, Func<double, double, double>? ground)
    {
        var (pose, previous, rolls) = (state.Pose, state.Previous, state.Rolls);
        // Last frame's places count only for the same cars.
        if (previous.Count != train.Cars.Count)
            previous.Clear();
        if (rolls.Count != train.Cars.Count)
        {
            rolls.Clear();
            rolls.AddRange(new Suspension.Roll[train.Cars.Count]);
        }

        train.Dp.Ground = ground;
        train.Pose(pose);
        var hadPrevious = previous.Count > 0;
        for (var i = 0; i < train.Cars.Count; i++)
        {
            var car = train.Cars[i];
            var (fx, fy, fz) = pose[i].Front;
            var (rx, ry, rz) = pose[i].Rear;
            // The body hangs between its truck centres, along the line joining them; its own front is the train's unless
            // it's been turned.
            var (dx, dy, dz) = (fx - rx, fy - ry, fz - rz);
            if (car.Reversed)
                (dx, dy, dz) = (-dx, -dy, -dz);
            var rotation = Facing(dx, dy, dz);
            var at = new double3((fx + rx) * 0.5, (fy + ry) * 0.5, (fz + rz) * 0.5);
            var (bodyRotation, bodyAt) = Sprung(train, state, i, rotation, at, step);
            var (wasRotation, wasAt, wasBodyRotation, wasBodyAt) = hadPrevious ? previous[i] : (rotation, at, bodyRotation, bodyAt);
            if (Model(car.ModelPath) is { } model)
            {
                // Where it was last frame too, so temporal anti-aliasing follows it (see MotionVectors). The trucks stay on
                // the rail; the body above them settles under its load and rolls a little on the springs.
                if (model.Trucks is { } trucks)
                {
                    _draws.Add(new ModelDraw(trucks, default, Rotation: rotation, At: at, PreviousRotation: wasRotation, PreviousAt: wasAt));
                    _draws.Add(new ModelDraw(model.Body, default, Rotation: bodyRotation, At: bodyAt, PreviousRotation: wasBodyRotation, PreviousAt: wasBodyAt));
                }
                else
                {
                    _draws.Add(new ModelDraw(model.Body, default, Rotation: rotation, At: at, PreviousRotation: wasRotation, PreviousAt: wasAt));
                }

                _placed.Add(new VehicleOverlay.Placed(car, car.ModelPath!, rotation, at));
            }

            if (train.Body.Systems[i] is { } systems)
            {
                AddLamps(car, systems, rotation, at);
                if (car.Def.Exhaust is { } exhaust)
                    Exhaust(train, pose, i, systems, exhaust, bodyRotation, bodyAt, dt);
            }
            if (hadPrevious)
                previous[i] = (rotation, at, bodyRotation, bodyAt);
            else
                previous.Add((rotation, at, bodyRotation, bodyAt));
        }
    }

    /// What the renderer outlines this frame (VehicleOutline): the driven vehicle and the picked one.
    public IReadOnlyList<Outlined> Outlines(TrainCar? driven, TrainCar? picked)
    {
        _outlined.Clear();
        foreach (var placed in _placed)
        {
            if (placed.Car != driven && placed.Car != picked)
                continue;
            if (_overlay.OutlineMesh(placed.Model) is { } shape)
                _outlined.Add(new Outlined(shape, placed.Rotation, placed.At, Driven: placed.Car == driven && placed.Car != picked));
        }

        return _outlined;
    }

    /// A locomotive's exhaust this frame: its stack's mouth on the body as it's drawn (rocking on the springs with it), the way
    /// it's moving, and how hard its engine's working, for the smoke.
    private void Exhaust(Train train, List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> pose, int i, LocoSystems systems,
        float3 exhaust, float4 bodyRotation, double3 bodyAt, float dt)
    {
        var mouth = VehicleOverlay.Rotate(bodyRotation, exhaust);
        var (f, r) = (pose[i].Front, pose[i].Rear);
        var (dx, dy, dz) = (f.X - r.X, f.Y - r.Y, f.Z - r.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var speed = train.Speed;
        var velocity = new float3((float)(dx / length * speed), (float)(dy / length * speed), (float)(dz / length * speed));
        // As hard as the engine sound has it working: tractive effort against what the lash-up can give, or power against its rating.
        var effort = train.Cars.Where(c => c.Def.Kind == VehicleKind.Locomotive).Sum(c => c.Def.StartingTractiveEffort);
        var traction = Math.Abs(train.Traction);
        var load = systems.MakesPower ? (float)Math.Max(effort > 0.0 ? traction / effort : 0.0, train.Power > 0.0 ? traction * Math.Abs(speed) / train.Power : 0.0) : 0f;
        var car = train.Cars[i];
        _smoke.Emit(car, new ExhaustState(
            (bodyAt.X + mouth.X, bodyAt.Y + mouth.Y, bodyAt.Z + mouth.Z),
            VehicleOverlay.Rotate(bodyRotation, new float3(1f, 0f, 0f)), VehicleOverlay.Rotate(bodyRotation, new float3(0f, 0f, 1f)),
            velocity, car.Def.ExhaustOpening, load, (float)systems.Revs, (float)train.Body.Starting[i], systems.Running), dt);
    }

    /// A locomotive's lit lamps as lights in the world: each headlight's beam shared between its lamps, dimmed by its switch
    /// and by the supply's volts (they sag while it cranks), the ditch lights steady or taking turns.
    private void AddLamps(TrainCar car, LocoSystems systems, float4 rotation, double3 at)
    {
        var brightness = (float)systems.LampBrightness;
        if (brightness <= 0f)
            return;
        foreach (var lamp in car.Def.Lamps)
        {
            var level = lamp.Ditch
                ? systems.DitchLit(lamp.Front, lamp.Side) ? 1f : 0f
                : (lamp.Front ? systems.FrontHeadlight : systems.RearHeadlight) switch { HeadlightSetting.Bright => 1f, HeadlightSetting.Dim => 0.22f, _ => 0f };
            if (level <= 0f)
                continue;
            _lamps.Add(LocoLamps.Light(lamp, LocoLamps.Heads(car.Def, lamp.Front), level * brightness, rotation, at));
        }
    }

    /// Where car i's body is drawn, given where the car stands on its trucks: lowered by its load, and rolled about its
    /// centre plates by the track and by leaning out on curves. Moves its roll on by dt.
    private static (float4 Rotation, double3 At) Sprung(Train train, CutState state, int i, float4 rotation, double3 at, double dt)
    {
        var car = train.Cars[i];
        var pose = state.Pose;

        // Which way is the inside of the curve: the track's heading turns from the car behind to the car ahead, towards it.
        (double X, double Y, double Z) Heading(int k)
        {
            var (f, r) = (pose[k].Front, pose[k].Rear);
            var (hx, hy, hz) = (f.X - r.X, f.Y - r.Y, f.Z - r.Z);
            var length = Math.Max(Math.Sqrt(hx * hx + hy * hy + hz * hz), 1e-9);
            return (hx / length, hy / length, hz / length);
        }

        var ahead = Heading(Math.Max(0, i - 1));
        var behind = Heading(Math.Min(train.Cars.Count - 1, i + 1));
        var (tx, tz) = (ahead.X - behind.X, ahead.Z - behind.Z);
        var turn = Math.Sqrt(tx * tx + tz * tz);
        var side = VehicleOverlay.Rotate(rotation, new float3(1f, 0f, 0f));
        var inward = turn > 1e-6 ? (side.X * tx + side.Z * tz) / turn : 0.0;

        // A locomotive rolls as the heavy thing it is, heavier with its tanks full, and sits lower for what it carries.
        var supplies = train.Body.Systems[i] is { } systems && car.Def.Supplies is not null ? systems.SuppliesShare : (double?)null;
        var load = car.Def.Kind == VehicleKind.Locomotive ? Suspension.LocoLoad(supplies ?? 1.0) : car.Fill;
        var sag = supplies is { } share ? Suspension.FullSuppliesSag * share : Suspension.Sag(car.Def, car.Fill);
        var roll = state.Rolls[i];
        Suspension.Step(ref roll, dt, train.Speed, car.Curvature, inward, load, at.X, at.Z);
        state.Rolls[i] = roll;

        // Rolled about a point RollCentre over the rail and lowered by the sag, in the car's own axes, then turned with it.
        var (sin, cos) = Math.SinCos(roll.Angle);
        var h = Suspension.RollCentre;
        var local = new float3((float)(h * sin), (float)(h * (1.0 - cos) - sag), 0f);
        var offset = VehicleOverlay.Rotate(rotation, local);
        var (hs, hc) = MathF.SinCos((float)roll.Angle * 0.5f);
        return (Multiply(rotation, new float4(0f, 0f, hs, hc)), new double3(at.X + offset.X, at.Y + offset.Y, at.Z + offset.Z));
    }

    /// q then p, as unit quaternions (x y z w): p turns first, in q's frame.
    private static float4 Multiply(float4 q, float4 p) => new(
        q.W * p.X + q.X * p.W + q.Y * p.Z - q.Z * p.Y,
        q.W * p.Y - q.X * p.Z + q.Y * p.W + q.Z * p.X,
        q.W * p.Z + q.X * p.Y - q.Y * p.X + q.Z * p.W,
        q.W * p.W - q.X * p.X - q.Y * p.Y - q.Z * p.Z);

    /// The rotation that turns a model's front (+z, up +y) to face (dx, dy, dz): a turn about the vertical, then a tilt
    /// up or down.
    public static float4 Facing(double dx, double dy, double dz)
    {
        var flat = Math.Sqrt(dx * dx + dz * dz);
        var yaw = Math.Atan2(dx, dz);
        var pitch = -Math.Atan2(dy, flat);
        var (sy, cy) = Math.SinCos(yaw * 0.5);
        var (sx, cx) = Math.SinCos(pitch * 0.5);
        return new float4((float)(cy * sx), (float)(sy * cx), (float)(-sy * sx), (float)(cy * cx));
    }

    /// A vehicle's model, loaded once.
    public VehicleModel? Model(string? path) => _models.Get(path);

    public void Dispose()
    {
        _overlay.Dispose();
        _models.Dispose();
        _draws.Clear();
    }
}
