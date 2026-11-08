using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Trains;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;

namespace Highball.Builder;

/// A train to try the track with, in the 3D Editor: pick a saved consist, click the track to put it there (its front at
/// the click, facing away from the camera), then drive it from this window. The window is a test bench, not a cab.
public sealed partial class TrainPanel : IDisposable
{
    private readonly Dictionary<string, RenderModel?> _models = [];
    private readonly List<ModelDraw> _draws = [];
    private readonly List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> _pose = [];
    private List<Consist> _consists = Consist.All();
    private int _picked;
    private RailGraph? _graph;

    public Train? Train
    {
        get => _train;
        private set
        {
            if (!ReferenceEquals(_train, value))
            {
                Selected = -1;
                SelectedCoupling = null;
            }
            _train = value;
        }
    }

    private Train? _train;

    /// The consist the train was made from, to keep the vehicles' names in.
    private Consist? _placedFrom;

    /// The vehicle picked (an index into the train's cars), shown in the Vehicle tab and outlined; -1 for none.
    public int Selected { get; private set; } = -1;

    /// Show the vehicles' name tags over them: the named ones, and the picked one whatever it's called.
    public bool ShowTags = true;

    /// The Vehicle tab is to be brought forward next frame (a vehicle was just picked on the screen).
    private bool _showVehicleTab;

    /// The coupling picked: k joins car k to car k + 1 (from 0); -1 is the train's front end, the last car's index its rear.
    /// Null for none.
    public int? SelectedCoupling { get; private set; }
    private bool _showCouplingTab;

    /// Where each coupling is this frame, from the front end (-1) to the rear (the last car's index), in world metres.
    private readonly List<double3> _couplings = [];

    /// The ground's height on the plan (x, z), for the DP radio's line of sight.
    public Func<double, double, double>? Ground { get; set; }

    /// How the next train's put on the track: running, starting up, or shut down.
    private PlacedAs _placeAs = PlacedAs.StartingUp;

    /// The locomotives' lit lamps this frame, for the renderer.
    private readonly List<LocalLight> _lamps = [];
    public IReadOnlyList<LocalLight> Lamps => _lamps;

    private readonly VehicleOverlay _overlay = new();
    private readonly List<VehicleOverlay.Placed> _placed = [];
    private ImInputString _name = new(48, ""u8);
    private TrainCar? _nameFor;

    /// Waiting for a click on the track to place the train.
    public bool Placing { get; private set; }

    /// The camera rides along with the train.
    public bool Follow;

    public bool Open;

    /// The train's bell and horn: B rings the bell or stops it, Space blows the horn while it's held.
    public readonly TrainSounds Sounds = new();

    /// The horn button in the window is held down.
    private bool _hornButton;

    /// The air brakes and couplers, graphed.
    public readonly TrainGraphs Graphs = new();

    private string? _notice;

    /// The last thing worth telling about placing or running the train.
    public string? Notice => _notice;

    /// The cars, to draw.
    public IReadOnlyList<ModelDraw> Draws => _draws;

    /// Sets a switch for one route (the editor's meshes own how the switches lie).
    public Action<long, bool>? SetSwitch { get; set; }

    /// The track has a new graph (it changed): the train is put back on it where it was.
    public void Use(RailGraph graph)
    {
        _graph = graph;
        if (Train is { } train && !train.MoveTo(graph))
        {
            Train = null;
            _notice = "The track under the train changed: it was taken off";
        }
    }

    /// Puts the picked consist on the track nearest (x, z), its front there, facing as near (fx, fz) as the track allows.
    public void PlaceAt(double x, double z, double fx, double fz)
    {
        Placing = false;
        if (_graph is not { } graph)
        {
            _notice = "No track to put it on";
            return;
        }

        if (_picked < 0 || _picked >= _consists.Count)
        {
            _notice = "Make a consist in the Consist Editor first";
            return;
        }

        if (graph.Nearest(x, z, 25.0) is not { } near)
        {
            _notice = "No laid track within 25 m of there";
            return;
        }

        var (tx, tz) = graph.FacingOf(near);
        var head = tx * fx + tz * fz >= 0.0 ? near : near.Reversed;
        var train = Trains.Train.Place(graph, _consists[_picked], head, out var why, _placeAs);
        if (train is null)
        {
            _notice = why;
            return;
        }

        train.Happened = what =>
        {
            Log.Info($"Train {train.Name}: {what}");
            _notice = what;
        };
        train.RunThrough = node =>
        {
            Log.Info($"Train {train.Name}: ran through switch {node}, set against it");
            _notice = "Ran through a switch set against the train: it's been forced over";
            SetSwitch?.Invoke(node, !graph.Reversed(node));
        };
        Train = train;
        _placedFrom = _consists[_picked];
        _notice = null;
    }

    /// Picks the vehicle under the pointer, opening this window on it; clicking where there's none lets go of it. True
    /// if a vehicle was hit.
    public bool SelectAt(float2 pointer, VehicleOverlay.Projector project, double3 camera)
    {
        // A coupling under the pointer first: they're small, and sit between the vehicles.
        if (CouplingAt(pointer, project, camera) is { } coupling)
        {
            SelectCoupling(coupling);
            return true;
        }

        var hit = _overlay.Pick(_placed, pointer, project, camera);
        Select(hit);
        return hit >= 0;
    }

    public void Select(int car)
    {
        Selected = Train is { } train && car >= 0 && car < train.Cars.Count ? car : -1;
        if (Selected >= 0)
        {
            SelectedCoupling = null;
            Open = true;
            _showVehicleTab = true;
        }
    }

    public void SelectCoupling(int? coupling)
    {
        SelectedCoupling = Train is { } train && coupling is { } k && k >= -1 && k < train.Cars.Count ? k : null;
        if (SelectedCoupling is not null)
        {
            Selected = -1;
            Open = true;
            _showCouplingTab = true;
        }
    }

    // How near the pointer has to be to a coupling to pick it, and how far off they're shown.
    private const float CouplingReach = 16f;
    private const double CouplingShown = 250.0;

    /// The coupling nearest the pointer within reach on the screen, if there is one.
    private int? CouplingAt(float2 pointer, VehicleOverlay.Projector project, double3 camera)
    {
        int? best = null;
        var nearest = Style.S(CouplingReach);
        for (var k = 0; k < _couplings.Count; k++)
        {
            var c = _couplings[k];
            if (Far(c, camera) || project(c.X, c.Y, c.Z) is not { } s)
                continue;
            var d = MathF.Sqrt((s.X - pointer.X) * (s.X - pointer.X) + (s.Y - pointer.Y) * (s.Y - pointer.Y));
            if (d < nearest)
                (best, nearest) = (k - 1, d);
        }

        return best;
    }

    private static bool Far(double3 at, double3 camera)
    {
        var (dx, dy, dz) = (at.X - camera.X, at.Y - camera.Y, at.Z - camera.Z);
        return dx * dx + dy * dy + dz * dz > CouplingShown * CouplingShown;
    }

    /// The picked vehicle's outline and the name tags, over the 3D view between clipMin and clipMax.
    public void Overlay(ImDrawListPtr draw, VehicleOverlay.Projector project, double3 camera, float2 clipMin, float2 clipMax)
    {
        if (Train is not { } train)
            return;
        var picked = Selected >= 0 && Selected < train.Cars.Count ? train.Cars[Selected] : null;
        foreach (var placed in _placed)
            if (placed.Car == picked)
                _overlay.Outline(draw, placed, project, clipMin, clipMax, Style.Accent);
        Couplings(draw, project, camera, clipMin, clipMax);
        if (!ShowTags)
            return;
        foreach (var placed in _placed)
            if (placed.Car == picked || !string.IsNullOrWhiteSpace(placed.Car.Tag))
                _overlay.Tag(draw, placed, project, camera, clipMin, clipMax, placed.Car == picked);
    }

    /// A small mark on each coupling near the camera while this window's open (brighter under the pointer), and a ring on
    /// the picked one: green coupled with the air through, amber uncoupled or the air shut off, red venting.
    private void Couplings(ImDrawListPtr draw, VehicleOverlay.Projector project, double3 camera, float2 clipMin, float2 clipMax)
    {
        if (Train is not { } train || (!Open && SelectedCoupling is null))
            return;
        var hovered = CouplingAt(Input.MousePosition, project, camera);
        draw.PushClipRect(clipMin, clipMax, true);
        for (var k = 0; k < _couplings.Count; k++)
        {
            var c = _couplings[k];
            var index = k - 1;
            if (Far(c, camera) || project(c.X, c.Y, c.Z) is not { } s)
                continue;
            var color = CouplingColor(train, index);
            var picked = SelectedCoupling == index;
            var r = Style.S(picked ? 4.5f : 3f);
            var alpha = picked || hovered == index ? 0.95f : 0.45f;
            draw.AddQuadFilled(s + new float2(0f, -r), s + new float2(r, 0f), s + new float2(0f, r), s + new float2(-r, 0f), color.WithAlpha(alpha));
            if (picked)
            {
                var ring = 9f + 1.5f * MathF.Sin((float)ImGui.GetTime() * 3f);
                draw.AddCircle(s, Style.S(ring), color.WithAlpha(0.8f), 24, Style.S(1.5f));
            }
        }

        draw.PopClipRect();
    }

    private static Rgba CouplingColor(Train train, int k)
    {
        var air = train.Body.Brakes;
        var last = train.Cars.Count - 1;
        if (k < 0 || k >= last)
            return (k < 0 ? air.FrontCock : air.RearCock) > 0.0 ? Style.Stop : Style.Clear;
        if (!air.Hose[k] && (air.CockAhead[k] > 0.0 || air.CockBehind[k] > 0.0))
            return Style.Stop;
        var through = air.Hose[k] && air.CockAhead[k] >= 1.0 && air.CockBehind[k] >= 1.0;
        return train.Body.Coupled[k] && through ? Style.Clear : Style.Caution;
    }

    /// Gives a vehicle a name (blank takes it away), and keeps it in the consist it came from.
    private void Rename(TrainCar car, string name)
    {
        car.Tag = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (_placedFrom is not { } consist || car.ConsistIndex < 0 || car.ConsistIndex >= consist.Cars.Count)
            return;
        consist.Cars[car.ConsistIndex] = consist.Cars[car.ConsistIndex] with { Tag = car.Tag };
        consist.Save();
    }

    public void Update(float dt)
    {
        Train?.Update(Paced(dt));
        Graphs.Record(Train, dt);
        _draws.Clear();
        _placed.Clear();
        _lamps.Clear();
        if (Train is not { } train)
        {
            Sounds.Stop();
            StopEngines();
            _hornButton = false;
            _previous.Clear();
            return;
        }

        var keys = !Keyboard.KeysBlocked;
        if (keys && Input.KeyDown(GlfwKey.B))
            Sounds.SetBell(!Sounds.BellRinging);
        Sounds.SetHorn((keys && Input.Key(GlfwKey.Space)) || _hornButton);
        // The ditch lights flash with the horn.
        foreach (var systems in train.Body.Systems)
            if (systems is not null)
                systems.Horn = Sounds.HornBlowing;

        // Last frame's places count only for the same train on the same track.
        if (!ReferenceEquals(train, _previousTrain) || !ReferenceEquals(train.Graph, _previousGraph) || _previous.Count != train.Cars.Count)
            _previous.Clear();
        _previousTrain = train;
        _previousGraph = train.Graph;

        train.Dp.Ground = Ground;

        // Drawn between physics steps, so it runs smoothly at any frame rate (see Train.Pose).
        train.Pose(_pose);
        PlaceCouplings(train);
        Smoothness(dt, train);
        var hadPrevious = _previous.Count > 0;
        for (var i = 0; i < train.Cars.Count; i++)
        {
            var car = train.Cars[i];
            var (fx, fy, fz) = _pose[i].Front;
            var (rx, ry, rz) = _pose[i].Rear;
            // The body hangs between its truck centres, along the line joining them; its own front is the train's unless
            // it's been turned.
            var (dx, dy, dz) = (fx - rx, fy - ry, fz - rz);
            if (car.Reversed)
                (dx, dy, dz) = (-dx, -dy, -dz);
            var rotation = Facing(dx, dy, dz);
            var at = new double3((fx + rx) * 0.5, (fy + ry) * 0.5, (fz + rz) * 0.5);
            var (wasRotation, wasAt) = hadPrevious ? _previous[i] : (rotation, at);
            if (Model(car.Livery) is { } model)
            {
                // Where it was last frame too, so temporal anti-aliasing follows it (see MotionVectors).
                _draws.Add(new ModelDraw(model, default, Rotation: rotation, At: at, PreviousRotation: wasRotation, PreviousAt: wasAt));
                _placed.Add(new VehicleOverlay.Placed(car, car.Livery!.Model, rotation, at));
            }

            if (train.Body.Systems[i] is { } systems)
                AddLamps(car, systems, rotation, at);
            if (hadPrevious)
                _previous[i] = (rotation, at);
            else
                _previous.Add((rotation, at));
        }
    }

    // How high a coupler's knuckle stands over the rail tops.
    private const double CouplerHeight = 0.85;

    /// Each coupling's place, from the cars as drawn: each car's ends past its trucks by its overhangs, and between two cars
    /// halfway from one's end to the next's.
    private void PlaceCouplings(Train train)
    {
        _couplings.Clear();
        double3 End(int i, bool rear)
        {
            var (f, r) = (_pose[i].Front, _pose[i].Rear);
            var (dx, dy, dz) = (r.X - f.X, r.Y - f.Y, r.Z - f.Z);
            var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
            var car = train.Cars[i];
            return rear
                ? new double3(r.X + dx / length * car.TrailingOverhang, r.Y + dy / length * car.TrailingOverhang + CouplerHeight, r.Z + dz / length * car.TrailingOverhang)
                : new double3(f.X - dx / length * car.LeadingOverhang, f.Y - dy / length * car.LeadingOverhang + CouplerHeight, f.Z - dz / length * car.LeadingOverhang);
        }

        var count = train.Cars.Count;
        _couplings.Add(End(0, false));
        for (var j = 0; j + 1 < count; j++)
        {
            // Drawn apart, it's marked on the knuckle of the car ahead.
            var (a, b) = (End(j, true), End(j + 1, false));
            _couplings.Add(train.Body.Gap(j) > 0.5 ? a : new double3((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5));
        }

        _couplings.Add(End(count - 1, true));
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
            level *= brightness;
            // Aimed a little down the track.
            var aim = MathEx.Normalize(lamp.Facing + new float3(0f, lamp.Ditch ? -0.08f : -0.04f, 0f));
            var p = VehicleOverlay.Rotate(rotation, lamp.Position);
            var f = VehicleOverlay.Rotate(rotation, aim);
            var (x, y, z) = (at.X + p.X, at.Y + p.Y, at.Z + p.Z);
            if (lamp.Ditch)
                _lamps.Add(new LocalLight(x, y, z, 120f, new float3(350f, 320f, 265f) * level, f, 0.97f, 0.9f, 0.2f, new float3(4f, 3.6f, 2.8f) * level));
            else
            {
                var share = 1f / Math.Max(1, car.Def.Lamps.Count(l => !l.Ditch && l.Front == lamp.Front));
                _lamps.Add(new LocalLight(x, y, z, 350f, new float3(2000f, 1800f, 1500f) * (level * share), f, 0.985f, 0.95f, 0.3f, new float3(5f, 4.4f, 3.4f) * level));
            }
        }
    }

    /// Each car's place as drawn last frame, for its motion.
    private readonly List<(float4 Rotation, double3 At)> _previous = [];
    private Train? _previousTrain;
    private RailGraph? _previousGraph;

    // Frame times as measured wobble from one frame to the next by more than the screen's do: the picture is shown at the
    // display's own beat, so running the train on by each frame's own time makes it move unevenly on the screen. It's run on
    // by a running average instead, and whatever that gets ahead of or behind real time is paid back a little each frame,
    // so it never drifts from the clock. A hitch past a quarter of a second is let through at once.
    private double _paceAverage = -1.0;
    private double _paceOwed;

    private double Paced(double dt)
    {
        if (dt <= 0.0)
            return 0.0;
        _paceAverage = _paceAverage < 0.0 ? dt : _paceAverage + (dt - _paceAverage) * 0.1;
        _paceOwed += dt - _paceAverage;
        if (Math.Abs(_paceOwed) > 0.25)
        {
            var hitch = _paceOwed;
            _paceOwed = 0.0;
            return Math.Max(0.0, _paceAverage + hitch);
        }

        var repay = _paceOwed * 0.05;
        _paceOwed -= repay;
        return Math.Max(0.0, _paceAverage + repay);
    }

    // With --train: how evenly the lead truck moves on the screen from frame to frame, drawn as it is and as the physics
    // steps alone would put it, logged each second.
    private (double X, double Y, double Z)? _lastDrawn, _lastStepped;
    private double _logAt;
    private readonly List<double> _drawnSpeeds = [], _steppedSpeeds = [], _frameTimes = [];

    private void Smoothness(float dt, Train train)
    {
        if (!App.DevTools.TrainLog || dt <= 0f)
            return;
        static double Apart((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        var drawn = _pose[0].Front;
        var stepped = train.Cars[0].FrontAt;
        if (_lastDrawn is { } d && _lastStepped is { } s)
        {
            _drawnSpeeds.Add(Apart(drawn, d));
            _steppedSpeeds.Add(Apart(stepped, s));
            _frameTimes.Add(dt);
        }

        (_lastDrawn, _lastStepped) = (drawn, stepped);
        _logAt += dt;
        if (_logAt < 1.0 || _drawnSpeeds.Count < 2)
            return;
        static string Spread(List<double> v, double scale, string unit)
        {
            var mean = v.Average();
            var sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
            return string.Create(CultureInfo.InvariantCulture, $"{v.Min() * scale:0.0}-{v.Max() * scale:0.0} {unit}, wobble {sd / Math.Max(mean, 1e-9) * 100.0:0.0}%");
        }

        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Train smoothness: {Math.Abs(train.Speed):0.00} m/s, {_drawnSpeeds.Count} frames ({Spread(_frameTimes, 1000.0, "ms")}); moved a frame: drawn {Spread(_drawnSpeeds, 100.0, "cm")}; steps alone {Spread(_steppedSpeeds, 100.0, "cm")}"));
        _drawnSpeeds.Clear();
        _steppedSpeeds.Clear();
        _frameTimes.Clear();
        _logAt = 0.0;
    }

    /// The rotation that turns a model's front (+z, up +y) to face (dx, dy, dz): a turn about the vertical, then a tilt
    /// up or down.
    private static float4 Facing(double dx, double dy, double dz)
    {
        var flat = Math.Sqrt(dx * dx + dz * dz);
        var yaw = Math.Atan2(dx, dz);
        var pitch = -Math.Atan2(dy, flat);
        var (sy, cy) = Math.SinCos(yaw * 0.5);
        var (sx, cx) = Math.SinCos(pitch * 0.5);
        return new float4((float)(cy * sx), (float)(sy * cx), (float)(-sy * sx), (float)(cy * cx));
    }

    private RenderModel? Model(Livery? livery)
    {
        if (livery is null)
            return null;
        if (_models.TryGetValue(livery.Model, out var model))
            return model;
        try
        {
            model = new RenderModel(RailWerkz.Vulkan.Model.Load(livery.Model));
        }
        catch (Exception e)
        {
            Log.Warn($"Trains: model {livery.Model} unreadable ({e.Message})");
            model = null;
        }

        return _models[livery.Model] = model;
    }

    /// Where the camera should move this frame to keep up with the train: how far its front moved.
    public (double X, double Y, double Z) FollowBy(ref (double X, double Y, double Z)? last)
    {
        if (Train is not { } train || !Follow)
        {
            last = null;
            return default;
        }

        var now = _pose.Count > 0 ? _pose[0].Front : train.Cars[0].FrontAt;
        var by = last is { } was ? (now.X - was.X, now.Y - was.Y, now.Z - was.Z) : default;
        last = now;
        return by;
    }

    // How high the horn (on the cab roof) and the bell (by the pilot) stand over the rail tops, in metres.
    private const double HornHeight = 4.6;
    private const double BellHeight = 1.2;

    private (double X, double Y, double Z)? _lastEar;
    private float3 _earVelocity;

    /// After the camera's moved this frame: hears from the camera, and puts the bell and horn on the lead locomotive. The
    /// listener is kept at the origin and the sounds placed relative to it, so it stays exact however far out the route goes.
    public void Hear(Camera camera, float dt)
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

        if (Train is not { } train || _pose.Count != train.Cars.Count)
            return;

        var lead = Math.Max(0, train.Cars.FindIndex(c => c.Def.Kind == VehicleKind.Locomotive));
        var (front, rear) = (_pose[lead].Front, _pose[lead].Rear);
        // Moving along the track at the train's speed: towards its front, or back.
        var (dx, dy, dz) = (front.X - rear.X, front.Y - rear.Y, front.Z - rear.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var velocity = new float3((float)(dx / length * train.Speed), (float)(dy / length * train.Speed), (float)(dz / length * train.Speed));
        // Where its horn and bell are on its body, as the model has them (the fixtures cut out of it), turned with the body as
        // it's drawn; an engine that doesn't say has them over its front truck, the horn on the cab roof and the bell low.
        var car = train.Cars[lead];
        var (bx, by, bz) = car.Reversed ? (-dx, -dy, -dz) : (dx, dy, dz);
        var rotation = Facing(bx, by, bz);
        var middle = ((front.X + rear.X) * 0.5, (front.Y + rear.Y) * 0.5, (front.Z + rear.Z) * 0.5);
        float3 At(float3? fixture, double up)
        {
            if (fixture is not { } on)
                return new((float)(front.X - camera.X), (float)(front.Y + up - camera.Y), (float)(front.Z - camera.Z));
            var p = VehicleOverlay.Rotate(rotation, on);
            return new((float)(middle.Item1 + p.X - camera.X), (float)(middle.Item2 + p.Y - camera.Y), (float)(middle.Item3 + p.Z - camera.Z));
        }

        Sounds.Move(new(At(car.Def.Horn, HornHeight), velocity, TrainSounds.HornReference, TrainSounds.HornRange),
            new(At(car.Def.Bell, BellHeight), velocity, TrainSounds.BellReference, TrainSounds.BellRange));
        Engines(train, camera, dt);
    }

    // How high a prime mover sits over the rail tops, in metres: about the middle of the long hood.
    private const double EngineHeight = 3.2;

    private readonly List<(int Car, EngineSound Sound)> _engines = [];
    private readonly RunningGear _gear = new();
    private readonly SlackSound _slack = new();
    private Train? _enginesFor;

    /// Every locomotive with an engine sound runs it, heard from the middle of its own body: the throttle sets the revs,
    /// and how hard the train's pulling (its tractive effort, or its power once it's moving) is the load.
    private void Engines(Train train, Camera camera, float dt)
    {
        if (!ReferenceEquals(train, _enginesFor))
        {
            StopEngines();
            _enginesFor = train;
            for (var i = 0; i < train.Cars.Count; i++)
                if (train.Cars[i].Def.EngineSound is { } def)
                    // Each a little off the others, so a lash-up of the same engines doesn't phase into one.
                    _engines.Add((i, new EngineSound(def, 1f + 0.012f * (_engines.Count % 3 - 1) + 0.004f * (_engines.Count / 3))));
        }

        // The wheels, motors and couplers, whether or not there are engine sounds.
        _gear.Update(dt, train.Body, _pose, (camera.X, camera.Y, camera.Z));
        _slack.Update(dt, train.Body, _pose, (camera.X, camera.Y, camera.Z));
        if (_engines.Count == 0)
            return;

        // With the reverser centred the throttle doesn't load the engine, and it stays at idle, as on the real thing.
        var throttle = train.Reverser != 0 && train.Dynamic <= 0.0 ? train.Notch / (float)Trains.Train.Notches : 0f;
        var effort = train.Cars.Where(c => c.Def.Kind == VehicleKind.Locomotive).Sum(c => c.Def.StartingTractiveEffort);
        var traction = Math.Abs(train.Traction);
        var load = (float)Math.Max(effort > 0.0 ? traction / effort : 0.0, train.Power > 0.0 ? traction * Math.Abs(train.Speed) / train.Power : 0.0);

        foreach (var (car, sound) in _engines)
        {
            var systems = train.Body.Systems[car];
            var onLine = systems?.MakesPower ?? true;
            var (front, rear) = (_pose[car].Front, _pose[car].Rear);
            var (dx, dy, dz) = (front.X - rear.X, front.Y - rear.Y, front.Z - rear.Z);
            var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
            var velocity = new float3((float)(dx / length * train.Speed), (float)(dy / length * train.Speed), (float)(dz / length * train.Speed));
            var at = new float3((float)((front.X + rear.X) * 0.5 - camera.X), (float)((front.Y + rear.Y) * 0.5 + EngineHeight - camera.Y),
                (float)((front.Z + rear.Z) * 0.5 - camera.Z));
            var inputs = new EngineInputs(onLine ? throttle : 0f, onLine ? load : 0f, (float)train.Body.Starting[car], train.Body.Brakes.Compressing,
                onLine && train.Reverser != 0 ? (float)train.Dynamic : 0f, systems?.Turning ?? true, (float)(systems?.RunDown ?? 0.0));
            sound.Update(dt, inputs, new(at, velocity, EngineSound.Reference, EngineSound.Range));
        }
    }

    private void StopEngines()
    {
        foreach (var (_, sound) in _engines)
            sound.Stop();
        _engines.Clear();
        _enginesFor = null;
        _gear.Stop();
    }

    public void CancelPlacing() => Placing = false;

    /// Picks a saved consist by name; false if there's none called that.
    public bool Pick(string name)
    {
        _consists = Consist.All();
        _picked = _consists.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return _picked >= 0;
    }

    public void Dispose()
    {
        Sounds.Stop();
        StopEngines();
        foreach (var model in _models.Values)
            model?.Dispose();
        _models.Clear();
        _draws.Clear();
    }
}
