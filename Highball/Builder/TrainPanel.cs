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

/// The places on a locomotive its sounds come from, as the Audio Editor moves them.
public enum SoundSpot { Engine, AirCompressor, BrakeFan, Horn, Bell }


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

    /// The stock on the track and what happens between it (the route's, RouteWorld.Railway): this window drives it.
    public readonly Railway Railway;

    /// A cut on the track, with what it takes to draw it smoothly from one frame to the next.
    private sealed class Cut(Train train) : Railway.Cut(train)
    {
        /// The consist it was made from, to keep the vehicles' names and loads in (null once cuts have been joined).
        public Consist? From;

        /// What it takes to draw it smoothly (TrainVisuals).
        public readonly TrainVisuals.CutState Drawn = new();

        /// Each car's trucks as drawn this frame.
        public List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> Pose => Drawn.Pose;
    }

    private List<Cut> _cuts => [.. Railway.Cuts.Cast<Cut>()];
    private Cut? _focus;

    private Cut? _controlled
    {
        get => (Cut?)Railway.Controlled;
        set => Railway.Controlled = value;
    }

    public TrainPanel(Railway railway)
    {
        Railway = railway;
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

    /// The trains as drawn: models, lamps, smoke, what's picked and outlined.
    private readonly TrainVisuals _visuals = new();
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
    public IReadOnlyList<LocalLight> Lamps => _visuals.Lamps;

    /// The locomotives' exhaust smoke, for the world to draw (FrameScene.Smoke).
    public IReadOnlyList<SmokeSegment> Smoke => _visuals.Smoke;
    public IReadOnlyList<HazeSource> Haze => _visuals.Haze;

    private VehicleOverlay _overlay => _visuals.Overlay;
    private IReadOnlyList<VehicleOverlay.Placed> _placed => _visuals.Placed;

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
    public IReadOnlyList<ModelDraw> Draws => _visuals.Draws;

    /// The track has a new graph (it changed): the train is put back on it where it was.
    public void Use(RailGraph graph)
    {
        _graph = graph;
        Railway.Use(graph);
    }

    /// The furthest a truck can turn from its car's body, in degrees, before it's off the rail.
    private const double MaxTruckSwivel = 10.0;

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
            _notice = "No locomotive in this cut";
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
            _notice = "No consist made";
            return;
        }

        if (graph.Nearest(x, z, 25.0) is not { } near)
        {
            _notice = "No track within 25 m";
            return;
        }

        // What the Build tab asked for (a consist made up there, or one vehicle), or the one picked on the Place tab.
        var consist = _toPlace ?? _consists[_picked];
        var fromLibrary = _toPlace is null || _consists.Any(c => c.Name == consist.Name && c.SavedAt is not null && !_buildChanged);
        _toPlace = null;
        if (VehicleCount + consist.Cars.Count > MaxVehicles)
        {
            _notice = $"Over {MaxVehicles:#,0} vehicles";
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

    /// What the renderer outlines this frame (VehicleOutline): the engine driven, whether or not it's looked at, and the
    /// picked vehicle.
    public IReadOnlyList<Outlined> Outlines
    {
        get
        {
            var driven = _controlled?.Train is { } train && train.Body.Brakes.Control is var lead and >= 0 ? train.Cars[lead] : null;
            var picked = Train is { } focus && Selected >= 0 && Selected < focus.Cars.Count ? focus.Cars[Selected] : null;
            return _visuals.Outlines(driven, picked);
        }
    }

    /// The name tags and coupling marks, over the 3D view between clipMin and clipMax.
    public void Overlay(ImDrawListPtr draw, VehicleOverlay.Projector project, double3 camera, float2 clipMin, float2 clipMax)
    {
        if (Train is not { } train)
            return;
        var picked = Selected >= 0 && Selected < train.Cars.Count ? train.Cars[Selected] : null;
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

    /// The cut looked at, which the world keeps awake as it steps (RouteWorld.Step).
    public Railway.Cut? FocusCut => _focus;

    /// After the world's stepped the trains on by step (paced: see RouteWorld.Step): the controls, and the trains drawn.
    public void Update(float dt, double step)
    {
        Graphs.Record(Train, dt);
        _visuals.Begin();
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
            _visuals.End(dt);
            return;
        }

        // A truck turned further than it can from its body has left the rail: the whole cut comes off (only this player's own).
        var derailed = new List<(Cut Cut, double Degrees)>();
        foreach (var cut in _cuts)
            if (_visuals.Draw(cut.Train, cut.Drawn, dt, step, Ground) is var swivel and > MaxTruckSwivel && !cut.Remote)
                derailed.Add((cut, swivel));
        foreach (var (cut, degrees) in derailed)
        {
            Railway.Remove(cut);
            _notice = $"{cut.Train.Name} derailed: track too sharp for its trucks";
            RailWerkz.Core.Logging.Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Derailed: {cut.Train.Name}, a truck turned {degrees:0.0} degrees from its body"));
        }

        if (derailed.Count > 0 && _cuts.Count == 0)
        {
            StopEngines();
            _pose = [];
            _visuals.End(dt);
            return;
        }
        _visuals.End(dt);
        _pose = (_focus ?? _cuts[0]).Pose;
        if (Train is { } looked)
        {
            PlaceCouplings(looked);
            Smoothness(dt, looked);
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

    /// Where the camera should move this frame to keep up with the train: how far its front moved.
    /// Where a car of the looked-at train is drawn this frame (its trucks, between physics steps: what Follow tracks, so
    /// a camera on it doesn't shake), or where the physics has it if it hasn't been drawn yet.
    public ((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)? Drawn(int car)
    {
        if (Train is not { } train || car < 0 || car >= train.Cars.Count)
            return null;
        return _pose.Count == train.Cars.Count ? _pose[car] : (train.Cars[car].FrontAt, train.Cars[car].RearAt);
    }

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

    /// The trains as heard: engines, wheels, couplers, and where the horn and bell are.
    private readonly TrainAudio _audio = new();

    /// After the camera's moved this frame: hears from the camera (TrainAudio). The horn, bell and engines heard are the
    /// driven cut's, or the looked-at one's when nothing's driven.
    public void Hear(Camera camera, float dt)
    {
        var heard = _controlled ?? _focus;
        _audio.Hear(camera, dt, heard is null ? null : new TrainAudio.Heard(heard.Train, heard.Pose), [.. _cuts.Select(c => new TrainAudio.Heard(c.Train, c.Pose))], Sounds);
        if (heard is not null && heard.Pose.Count == heard.Train.Cars.Count)
            _pose = (_focus ?? heard).Pose;
    }

    /// The locomotive the Audio Editor works on, and how it's placed: the picked vehicle if it's a locomotive, else the lead
    /// one of the train looked at. Its middle (between its trucks, on the rail) and its turn, as SoundPosition's are drawn.
    public bool SoundAnchor(out TrainCar car, out float4 rotation, out double3 middle)
    {
        car = null!;
        rotation = default;
        middle = default;
        if (_focus is not { } cut || cut.Pose.Count != cut.Train.Cars.Count)
            return false;
        var train = cut.Train;
        var i = Selected >= 0 && Selected < train.Cars.Count && train.Cars[Selected].Def.Kind == VehicleKind.Locomotive
            ? Selected
            : train.Cars.FindIndex(c => c.Def.Kind == VehicleKind.Locomotive);
        if (i < 0)
            return false;
        var (f, r) = (cut.Pose[i].Front, cut.Pose[i].Rear);
        car = train.Cars[i];
        var (dx, dy, dz) = (f.X - r.X, f.Y - r.Y, f.Z - r.Z);
        if (car.Reversed)
            (dx, dy, dz) = (-dx, -dy, -dz);
        rotation = TrainVisuals.Facing(dx, dy, dz);
        middle = new double3((f.X + r.X) * 0.5, (f.Y + r.Y) * 0.5, (f.Z + r.Z) * 0.5);
        return true;
    }

    private void StopEngines() => _audio.Stop();

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
        _visuals.Dispose();
        Sounds.Stop();
        StopEngines();
    }
}
