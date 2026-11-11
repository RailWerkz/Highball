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

/// Locomotives and rolling stock on the track, in the 3D Editor: as many cuts as are put down (up to MaxVehicles vehicles
/// in all), each placed from a saved consist with a click on the track. One cut is looked at in this window (the focus,
/// picked by clicking a vehicle) and one is driven (the controlled one): control is taken, and given up, from the window.
/// Cuts that run into each other couple under CoupleSpeed; faster, both are taken off. A cut uncoupled and drawn away
/// from the rest of its train becomes a cut of its own.
public sealed partial class TrainPanel : IDisposable
{
    /// The most vehicles there can be on the track at once.
    public const int MaxVehicles = Railway.MaxVehicles;

    /// Run together faster than this (mph, closing) and both cuts are taken off: there's no crash yet to show.
    public const double CoupleSpeed = Railway.CoupleSpeed;

    /// The stock on the track and what happens between it: this window drives it and draws it.
    public readonly Railway Railway = new();

    /// A cut on the track, with what it takes to draw it smoothly from one frame to the next.
    private sealed class Cut(Train train) : Railway.Cut(train)
    {
        /// The consist it was made from, to keep the vehicles' names and loads in (null once cuts have been joined).
        public Consist? From;
        public readonly List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> Pose = [];
        public readonly List<(float4 Rotation, double3 At, float4 BodyRotation, double3 BodyAt)> Previous = [];
        public readonly List<Suspension.Roll> Rolls = [];
    }

    private List<Cut> _cuts => [.. Railway.Cuts.Cast<Cut>()];
    private Cut? _focus;

    private Cut? _controlled
    {
        get => (Cut?)Railway.Controlled;
        set => Railway.Controlled = value;
    }

    public TrainPanel()
    {
        Railway.NewCut = (train, from) => new Cut(train) { From = (from as Cut)?.From };
        Railway.Notice += what => _notice = what;
        Railway.Removed += cut =>
        {
            if (ReferenceEquals(_focus, cut))
                Focus(_cuts.LastOrDefault());
        };
        Railway.Replaced += (old, now) =>
        {
            if (ReferenceEquals(_focus, old) || _focus is null || !Railway.Cuts.Contains(_focus))
                Focus((Cut)now);
        };
    }

    private readonly Dictionary<string, VehicleModel?> _models = [];
    private readonly List<ModelDraw> _draws = [];
    private List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> _pose = [];
    private List<Consist> _consists = Consist.All();
    private int _picked;
    private RailGraph? _graph;

    /// The cut looked at in the window: the last one picked or placed.
    public Train? Train => _focus?.Train;

    /// The cut being driven, if any.
    public Train? Controlled => _controlled?.Train;

    /// How many vehicles are on the track, and how many cuts.
    public int VehicleCount => _cuts.Sum(c => c.Train.Cars.Count);
    public int CutCount => _cuts.Count;

    private void Focus(Cut? cut)
    {
        if (!ReferenceEquals(_focus, cut))
        {
            Selected = -1;
            SelectedCoupling = null;
        }
        _focus = cut;
        if (cut is not null)
            cut.Still = 0.0;
    }

    /// The consist the looked-at cut was made from, to keep the vehicles' names in.
    private Consist? _placedFrom => _focus?.From;

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

    /// The track has a new graph (it changed): the train is put back on it where it was.
    public void Use(RailGraph graph)
    {
        _graph = graph;
        Railway.Use(graph);
    }

    /// Takes a cut off the track (not another driver's).
    private void Remove(Cut cut)
    {
        if (cut.Remote)
        {
            _notice = "That's another driver's train";
            return;
        }

        Railway.Remove(cut);
    }

    /// A notice for the crew from outside the window (multiplayer).
    public void Notify(string text) => _notice = text;

    /// Takes control of the looked-at cut, giving up whichever's driven now (its brakes set as it's left). False, with a
    /// notice, if it has no locomotive.
    public bool TakeControl()
    {
        if (_focus is not { } cut)
            return false;
        if (cut.Remote)
        {
            _notice = "That's another driver's train";
            return false;
        }

        if (!cut.Train.Cars.Any(c => c.Def.Kind == VehicleKind.Locomotive))
        {
            _notice = "There's no locomotive in this cut to take control of";
            return false;
        }

        if (!ReferenceEquals(_controlled, cut))
            Release();
        _controlled = cut;
        cut.Still = 0.0;
        return true;
    }

    /// Gives up the driven cut, leaving it as a train's left standing: throttle off, brakes on.
    public void Release()
    {
        if (_controlled is not { } cut)
            return;
        cut.Train.Secure();
        cut.Still = 0.0;
        _controlled = null;
        Sounds.Stop();
        _hornButton = false;
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

        if (_toPlace is null && (_picked < 0 || _picked >= _consists.Count))
        {
            _notice = "Make up a consist on the Build tab first";
            return;
        }

        if (graph.Nearest(x, z, 25.0) is not { } near)
        {
            _notice = "No laid track within 25 m of there";
            return;
        }

        // What the Build tab asked for (a consist made up there, or one vehicle), or the one picked on the Place tab.
        var consist = _toPlace ?? _consists[_picked];
        var fromLibrary = _toPlace is null || _consists.Any(c => c.Name == consist.Name && c.SavedAt is not null && !_buildChanged);
        _toPlace = null;
        if (VehicleCount + consist.Cars.Count > MaxVehicles)
        {
            _notice = $"That would make more than {MaxVehicles:#,0} vehicles on the track";
            return;
        }

        var (tx, tz) = graph.FacingOf(near);
        var head = tx * fx + tz * fz >= 0.0 ? near : near.Reversed;
        var train = Trains.Train.Place(graph, consist, head, out var why, _placeAs);
        if (train is null)
        {
            _notice = why;
            return;
        }

        if (Railway.Add(train, null, out var refused) is not Cut cut)
        {
            _notice = refused;
            return;
        }

        // Names and loads go back into the consist it came from only if that's a saved one.
        cut.From = fromLibrary ? _consists.FirstOrDefault(c => c.Name == consist.Name) ?? consist : null;
        Focus(cut);
        // The first engine put down is driven; after that, control's taken from the window.
        if (_controlled is null && train.Cars.Any(c => c.Def.Kind == VehicleKind.Locomotive))
            _controlled = cut;
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
        if (hit < 0)
        {
            Select(-1);
            return false;
        }

        // The cut the vehicle's in becomes the one looked at.
        var car = _placed[hit].Car;
        if (_cuts.FirstOrDefault(c => c.Train.Cars.Contains(car)) is { } cut)
        {
            Focus(cut);
            Select(cut.Train.Cars.IndexOf(car));
        }
        return true;
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
        // The engine driven, outlined green whether or not it's looked at.
        if (_controlled?.Train is { } driven && driven.Body.Brakes.Control is var lead and >= 0)
            foreach (var placed in _placed)
                if (placed.Car == driven.Cars[lead])
                    _overlay.Outline(draw, placed, project, clipMin, clipMax, Style.Clear);
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

    /// How full the picked car is loaded: its weight changes at once (and it settles on its springs), and it's saved into
    /// the consist the train was placed from when the slider's let go, as a rename is.
    private void LoadControl(Train train, int index, TrainCar car)
    {
        ImGui.Dummy(new float2(1f, Style.S(4f)));
        Kit.Caption("Load");
        var fill = (float)car.Fill;
        if (Kit.Slider("vehicle.load", ref fill, out var released))
        {
            // Snap to the ends, so empty and full are easy to hit.
            var value = fill < 0.02f ? 0.0 : fill > 0.98f ? 1.0 : Math.Round(fill, 2);
            train.SetFill(index, value);
        }

        if (released)
            SaveLoad(car);

        // What it's seen carrying, once it carries anything (the first unless another's picked).
        if (car.Def.Cargoes is { Count: > 0 } cargoes && car.Fill > 0.0)
        {
            Kit.Caption("Cargo");
            var labels = cargoes.Select(c => c.Name).ToArray();
            var current = Math.Max(0, cargoes.ToList().FindIndex(c => c.Id == car.Cargo));
            var picked = Kit.Segmented("vehicle.cargo", labels, current);
            if (picked != current)
            {
                car.Cargo = cargoes[picked].Id;
                Sfx.Tick();
                SaveLoad(car);
            }
        }

        var where = _placedFrom is { } consist ? $"saved to {consist.Name}" : "not saved: not from a consist";
        Kit.Note($"{car.Fill * 100.0:0}%  ·  {UnitText.Mass(car.Mass)} of {UnitText.Mass(car.Def.MassLoaded)}  ·  {where}", Style.TextFaint);
    }

    /// Keeps a car's load in the consist it came from.
    private void SaveLoad(TrainCar car)
    {
        if (_placedFrom is not { } consist || car.ConsistIndex < 0 || car.ConsistIndex >= consist.Cars.Count)
            return;
        consist.Cars[car.ConsistIndex] = consist.Cars[car.ConsistIndex].WithFill(car.Fill) with { Cargo = car.Cargo };
        consist.Save();
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
        var step = Paced(dt);
        // Every cut's stepped, but one left standing a while sleeps until it's driven, looked at or run into.
        Railway.Step(step, _focus);
        Graphs.Record(Train, dt);
        _draws.Clear();
        _placed.Clear();
        _lamps.Clear();
        if (_controlled?.Train is { } driven)
        {
            var keys = !Keyboard.KeysBlocked;
            if (keys && Input.KeyDown(GlfwKey.B))
                Sounds.SetBell(!Sounds.BellRinging);
            if (keys && Input.KeyDown(GlfwKey.X))
                driven.Sand = !driven.Sand;
            Sounds.SetHorn((keys && Input.Key(GlfwKey.Space)) || _hornButton);
            // The ditch lights flash with the horn.
            foreach (var systems in driven.Body.Systems)
                if (systems is not null)
                    systems.Horn = Sounds.HornBlowing;
        }
        else
        {
            Sounds.Stop();
            _hornButton = false;
        }

        if (_cuts.Count == 0)
        {
            StopEngines();
            _pose = [];
            return;
        }

        foreach (var cut in _cuts)
            DrawCut(cut, dt, step);
        _pose = (_focus ?? _cuts[0]).Pose;
        _rolls = (_focus ?? _cuts[0]).Rolls;
        _previous = (_focus ?? _cuts[0]).Previous;
        if (Train is { } looked)
        {
            PlaceCouplings(looked);
            Smoothness(dt, looked);
        }
    }

    /// One cut's vehicles drawn where they are this frame (between physics steps, so it runs smoothly at any frame rate,
    /// see Train.Pose), its bodies on their springs, and its engines' lamps.
    private void DrawCut(Cut cut, float dt, double step)
    {
        var train = cut.Train;
        (_pose, _previous, _rolls) = (cut.Pose, cut.Previous, cut.Rolls);
        // Last frame's places count only for the same cars.
        if (_previous.Count != train.Cars.Count)
            _previous.Clear();
        if (_rolls.Count != train.Cars.Count)
        {
            _rolls.Clear();
            _rolls.AddRange(new Suspension.Roll[train.Cars.Count]);
        }

        train.Dp.Ground = Ground;
        train.Pose(_pose);
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
            var (bodyRotation, bodyAt) = Sprung(train, i, rotation, at, step);
            var (wasRotation, wasAt, wasBodyRotation, wasBodyAt) = hadPrevious ? _previous[i] : (rotation, at, bodyRotation, bodyAt);
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
                AddLamps(car, systems, rotation, at);
            if (hadPrevious)
                _previous[i] = (rotation, at, bodyRotation, bodyAt);
            else
                _previous.Add((rotation, at, bodyRotation, bodyAt));
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

    /// The cut being drawn's cars as drawn last frame, and their bodies on the springs, for their motion.
    private List<(float4 Rotation, double3 At, float4 BodyRotation, double3 BodyAt)> _previous = [];

    /// The cut being drawn's car bodies' roll on their springs.
    private List<Suspension.Roll> _rolls = [];

    /// Where car i's body is drawn, given where the car stands on its trucks: lowered by its load, and rolled about its
    /// centre plates by the track and by leaning out on curves. Moves its roll on by dt.
    private (float4 Rotation, double3 At) Sprung(Train train, int i, float4 rotation, double3 at, double dt)
    {
        var car = train.Cars[i];

        // Which way is the inside of the curve: the track's heading turns from the car behind to the car ahead, towards it.
        (double X, double Y, double Z) Heading(int k)
        {
            var (f, r) = (_pose[k].Front, _pose[k].Rear);
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
        var roll = _rolls[i];
        Suspension.Step(ref roll, dt, train.Speed, car.Curvature, inward, load, at.X, at.Z);
        _rolls[i] = roll;

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
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Train traction: {Math.Abs(train.Traction) / 1000.0:0} kN, slip {string.Join(" ", train.Body.WheelSlip.Where((_, k) => train.Body.Systems[k] is not null).Select(w => w.ToString("0.00", CultureInfo.InvariantCulture)))}, sand {train.Sand}, fuel {string.Join(" ", train.Body.Systems.Where(s => s is not null).Select(s => s!.FuelRate.ToString("0", CultureInfo.InvariantCulture)))} L/h, mass {train.Mass / 1000.0:0} t"));
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

    private VehicleModel? Model(string? path)
    {
        if (path is null)
            return null;
        if (_models.TryGetValue(path, out var model))
            return model;
        try
        {
            model = VehicleModel.Load(path);
        }
        catch (Exception e)
        {
            Log.Warn($"Trains: model {path} unreadable ({e.Message})");
            model = null;
        }

        return _models[path] = model;
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

        // The horn, bell and engines heard are the driven cut's, or the looked-at one's when nothing's driven.
        if ((_controlled ?? _focus) is not { } heard || heard.Pose.Count != heard.Train.Cars.Count)
        {
            StopEngines();
            return;
        }

        var train = heard.Train;
        _pose = heard.Pose;
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
        _pose = (_focus ?? heard).Pose;
    }

    // How high a prime mover sits over the rail tops, in metres: about the middle of the long hood.
    private const double EngineHeight = 3.2;

    /// The most locomotives heard at once: each engine takes about five of the 64 voices, and the horn, bell, wheels and
    /// couplers need theirs.
    private const int MaxEngineSounds = 8;

    /// Each locomotive heard now, by the vehicle (so its sound carries on when cuts couple or part).
    private readonly Dictionary<TrainCar, EngineSound> _engines = [];
    private readonly RunningGear _gear = new();
    private readonly SlackSound _slack = new();

    /// The engines of the locomotives nearest the camera, whichever cut they're in (up to MaxEngineSounds), each heard from
    /// the middle of its own body: its train's throttle sets the revs, and how hard that train's pulling (its tractive
    /// effort, or its power once it's moving) is the load. The wheels and couplers are the heard cut's.
    private void Engines(Train train, Camera camera, float dt)
    {
        // The wheels, motors and couplers, whether or not there are engine sounds.
        _gear.Update(dt, train.Body, _pose, (camera.X, camera.Y, camera.Z));
        _slack.Update(dt, train.Body, _pose, (camera.X, camera.Y, camera.Z));

        var near = new List<(Cut Cut, int Car, double Away)>();
        foreach (var c in _cuts)
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
                _engines[vehicle] = sound = new EngineSound(vehicle.Def.EngineSound!, 1f + 0.012f * (_engines.Count % 3 - 1) + 0.004f * (_engines.Count / 3));
            EngineVoice(t, cut.Pose, car, sound, camera, dt);
        }
    }

    /// One locomotive's engine sound this frame, from its train's controls and how hard it's pulling.
    private static void EngineVoice(Train train, List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> pose, int car, EngineSound sound,
        Camera camera, float dt)
    {
        // With the reverser centred the throttle doesn't load the engine, and it stays at idle, as on the real thing.
        var throttle = train.Reverser != 0 && train.Dynamic <= 0.0 ? train.Notch / (float)Trains.Train.Notches : 0f;
        var effort = train.Cars.Where(c => c.Def.Kind == VehicleKind.Locomotive).Sum(c => c.Def.StartingTractiveEffort);
        var traction = Math.Abs(train.Traction);
        var load = (float)Math.Max(effort > 0.0 ? traction / effort : 0.0, train.Power > 0.0 ? traction * Math.Abs(train.Speed) / train.Power : 0.0);
        {
            var _pose = pose;
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
        foreach (var sound in _engines.Values)
            sound.Stop();
        _engines.Clear();
        _gear.Stop();
    }

    public void CancelPlacing()
    {
        Placing = false;
        _toPlace = null;
    }

    /// Picks a saved consist by name; false if there's none called that.
    public bool Pick(string name)
    {
        _consists = Consist.All();
        _toPlace = null;
        _picked = _consists.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return _picked >= 0;
    }

    /// Takes every cut off the track.
    public void Clear()
    {
        Release();
        // Only this player's: another driver's cut is theirs to take off.
        foreach (var cut in _cuts.Where(c => !c.Remote))
            Railway.Remove(cut);
        Focus(_cuts.LastOrDefault());
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
