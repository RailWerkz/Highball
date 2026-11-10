using System.Globalization;
using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Train window: a plate in the deck's look, its tabs each a stack of cards.
///   Drive     the controls, the air brakes, horn and bell, and how it's running
///   Power     the engines all together, and the distributed power
///   Vehicle   the train's vehicles, and the one picked: its name, what it is, its air, and a locomotive's own switches
///   Coupling  the coupling picked: its knuckles, its hose and angle cocks
///   Train     which consist, how it's put on the track, and what's shown
public sealed partial class TrainPanel
{
    private enum Tab
    {
        Drive,
        Power,
        Vehicle,
        Coupling,
        Build,
        Train,
    }

    private static readonly string[] TabNames = ["Drive", "Power", "Vehicles", "Coupling", "Build", "Place"];

    /// With nothing on the track, only these two mean anything.
    private static readonly string[] EmptyTabNames = ["Build", "Place"];

    private Tab _tab = Tab.Drive;

    private static float PanelWidth => S(420f);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// Opens the window on a tab, by its name.
    public void ShowTab(string name)
    {
        if (Enum.TryParse<Tab>(name, true, out var tab))
            _tab = tab;
        Open = true;
    }

    public void Window(float top)
    {
        if (!Open)
        {
            Graphs.Window(Train);
            return;
        }

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new float2(display.X - PanelWidth - S(12f), top + S(12f)), ImGuiCond.FirstUseEver);
        if (_showVehicleTab)
            _tab = Tab.Vehicle;
        if (_showCouplingTab)
            _tab = Tab.Coupling;
        _showVehicleTab = _showCouplingTab = false;

        if (Kit.Begin("Consist##panel", ref Open, PanelWidth))
            Contents();
        Kit.End();
        Graphs.Window(Train);
    }

    private void Contents()
    {
        var train = Train;
        var (status, statusColor) = train is null ? ("NO TRAIN", TextFaint)
            : train.Stopped is not null ? ("STOPPED", Caution)
            : Math.Abs(train.Speed) < 0.05 ? ("STANDING", TextDim)
            : (UnitText.Speed(Math.Abs(train.Speed)).ToUpperInvariant(), Style.Clear);
        var subtitle = train is null ? "Place a consist on the track" : $"{train.Name}  ·  {train.Cars.Count} vehicles";
        if (Kit.Header("CONSIST", subtitle, status, statusColor))
            Open = false;

        if (_notice is not null)
        {
            Kit.Status(Caution, _notice);
            if (ImGui.IsItemClicked())
                _notice = null;
        }

        if (train is null)
        {
            var empty = Kit.Tabs("train.emptytabs", EmptyTabNames, _tab == Tab.Build ? 0 : 1);
            _tab = empty == 0 ? Tab.Build : Tab.Train;
            if (_tab == Tab.Build)
                BuildTab();
            else
                Setup(null);
            return;
        }

        // The readings that matter at a glance, whatever the tab.
        var air = train.Body.Brakes;
        var head = Math.Max(air.Control, 0);
        Kit.Stats([
            new Stat("Speed", UnitText.Speed(Math.Abs(train.Speed))),
            new Stat("Throttle", train.Dynamic > 0.0 ? Inv($"DB {train.Dynamic * 100.0:0}%") : train.Notch == 0 ? "Idle" : Inv($"N{train.Notch}")),
            new Stat("Brake pipe", UnitText.Pressure(air.Pipe[head]), air.Valve[head] == ValveState.Emergency ? Stop : null),
            new Stat("Main res", UnitText.Pressure(air.Main)),
        ], 4, big: true);

        _tab = (Tab)Kit.Tabs("train.tabs", TabNames, (int)_tab);
        switch (_tab)
        {
            case Tab.Drive:
                if (ReferenceEquals(train, Controlled))
                    Drive(train);
                else
                    NotDriven(train);
                break;
            case Tab.Power:
                PowerTab(train);
                break;
            case Tab.Vehicle:
                VehicleTab(train);
                break;
            case Tab.Coupling:
                CouplingTab(train);
                break;
            case Tab.Build:
                BuildTab();
                break;
            default:
                Setup(train);
                break;
        }
    }

    /// The looked-at cut isn't the one driven: control can be taken of it if it has an engine.
    private void NotDriven(Train train)
    {
        var engines = train.Cars.Count(c => c.Def.Kind == VehicleKind.Locomotive);
        Kit.BeginCard("Not in control", Controlled is { } driven ? $"driving {driven.Name}" : "nothing driven", Caution);
        if (engines == 0)
            Kit.Note("No locomotive in this cut: it can't be driven. Its handbrakes are on the Vehicles tab.");
        else if (Kit.Button("train.take", Controlled is null ? "Take control" : "Take control (releases the other)", kind: ButtonKind.Primary))
            TakeControl();
        Handbrakes(train);
        Kit.EndCard();
    }

    /// A locomotive picked: whether it's the lead unit, and a way to make it one if it's at the far end of the train.
    private void LeadControl(Train train, int car)
    {
        if (train.Cars[car].Def.Kind != VehicleKind.Locomotive || train.Body.Systems[car] is null)
            return;
        var lead = train.LeadUnit;
        var (group, remote) = train.Dp.GroupOf(car);
        string role = car == lead ? "Lead unit" : group == train.Dp.Lead ? "Trailing unit, with the lead" : remote is not null ? "DP remote" : "Not in use";
        Kit.BeginCard("Role", role, car == lead ? Style.Clear : remote is not null ? Caution : TextDim);
        // Only a locomotive at an end of the train can lead: the far end's turns the whole train round.
        var atRear = false;
        for (var i = train.Cars.Count - 1; i >= 0 && train.Body.Systems[i] is not null; i--)
            if (i == car)
                atRear = true;
        if (car != lead && group != train.Dp.Lead)
        {
            if (atRear)
            {
                if (Kit.Button("vehicle.lead", "Make lead unit · turn the train round", kind: ButtonKind.Primary))
                    MakeLead(train);
            }
            else
                Kit.Note("Only a locomotive at an end of the train can lead.");
        }

        Kit.Note("Units coupled to the lead work with it; any other locomotive is a DP remote.");
        Kit.EndCard();
    }

    /// The looked-at cut turned end for end, so the locomotive at its rear leads. Standing only: it's a crew change.
    private void MakeLead(Train train)
    {
        if (Math.Abs(train.Speed) > 0.1)
        {
            _notice = "Stop the train first to change ends";
            return;
        }

        if (_focus is not { } cut || !ReferenceEquals(cut.Train, train) || train.Turned() is not { } turned)
            return;
        Hook(turned);
        cut.Train = turned;
        cut.Pose.Clear();
        cut.Previous.Clear();
        cut.Rolls.Clear();
        Selected = turned.LeadUnit;
        _notice = $"{turned.Cars[turned.LeadUnit].Label} is the lead unit now";
    }

    /// Every handbrake in the cut wound on or off at once.
    private static void Handbrakes(Train train)
    {
        var on = train.Body.HandbrakeOn.Count(h => h);
        Kit.Caption($"Handbrakes · {on} of {train.Cars.Count} on");
        var hit = Kit.Buttons("train.handbrakes", ["All on", "All off"], [on < train.Cars.Count, on > 0]);
        if (hit >= 0)
            Array.Fill(train.Body.HandbrakeOn, hit == 0);
    }

    // --- Place: the consist, placing it, what's on the track, and what's shown. ---

    private static readonly string[] PlacedNames = ["Running", "Starting up", "Cold"];

    private void Setup(Train? train)
    {
        Kit.BeginCard(train is null ? "Place a train" : "Consist", _consists.Count == 1 ? "1 saved" : $"{_consists.Count} saved");
        Kit.Caption("Consist");
        var shown = _picked >= 0 && _picked < _consists.Count ? _consists[_picked].Name : "(none saved)";
        ImGui.PushItemWidth(Kit.Width - S(88f));
        if (ImGui.BeginCombo("##consist", shown))
        {
            for (var i = 0; i < _consists.Count; i++)
                if (ImGui.Selectable(_consists[i].Name, i == _picked))
                    _picked = i;
            ImGui.EndCombo();
        }

        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (Kit.Button("train.reload", "Reload", S(80f)))
        {
            var name = _picked >= 0 && _picked < _consists.Count ? _consists[_picked].Name : null;
            _consists = Consist.All();
            _picked = Math.Max(0, _consists.FindIndex(c => c.Name == name));
        }

        Kit.Caption("Put on the track");
        _placeAs = (PlacedAs)Kit.Segmented("train.placed", PlacedNames, (int)_placeAs);
        var noTrack = _graph is null || _graph.Edges.Count == 0;
        var canPlace = _consists.Count > 0 && !noTrack;
        ImGui.Dummy(new float2(1f, S(2f)));
        if (Kit.Button("train.place", Placing && _toPlace is null ? "Click the track · Esc cancels" : "Place on track", kind: ButtonKind.Primary, enabled: canPlace))
        {
            _toPlace = null;
            Placing = !Placing;
        }

        if (noTrack)
            Kit.Note("No laid track yet: lay some first.");
        else
            Kit.Note($"Its front goes where you click, facing away from the camera. {VehicleCount:#,0} of {MaxVehicles:#,0} vehicles on the track.");
        Kit.EndCard();

        OnTheTrack();
        if (train is null)
            return;
        Kit.BeginCard("View");
        Kit.Toggle("train.follow", "Camera follows", ref Follow);
        ImGui.SameLine(0f, S(18f));
        Kit.Toggle("train.tags", "Name tags", ref ShowTags);
        ImGui.SameLine(0f, S(18f));
        Kit.Toggle("train.graphs", "Air graphs", ref Graphs.Open);
        Kit.EndCard();
    }

    /// Every cut on the track: looked at with a click, driven, or taken off.
    private void OnTheTrack()
    {
        if (_cuts.Count == 0)
            return;
        Kit.BeginCard("On the track", _cuts.Count == 1 ? "1 cut" : $"{_cuts.Count} cuts");
        Cut? remove = null;
        for (var i = 0; i < _cuts.Count; i++)
        {
            var cut = _cuts[i];
            var t = cut.Train;
            var engines = t.Cars.Count(c => c.Def.Kind == VehicleKind.Locomotive);
            var badge = ReferenceEquals(cut, _controlled) ? "DRIVING" : Math.Abs(t.Speed) > 0.05 ? UnitText.Speed(Math.Abs(t.Speed)).ToUpperInvariant() : null;
            if (ToolPanel.Item($"cut{i}", null, $"{t.Name} · {t.Cars.Count} vehicles{(engines > 0 ? $", {engines} engines" : "")}", badge, ReferenceEquals(cut, _focus),
                    swatch: ReferenceEquals(cut, _controlled) ? Style.Clear : engines > 0 ? Caution : TextFaint))
                Focus(cut);
        }

        ImGui.Dummy(new float2(1f, S(4f)));
        var focus = _focus;
        var hasEngine = focus?.Train.Cars.Any(c => c.Def.Kind == VehicleKind.Locomotive) ?? false;
        var driving = focus is not null && ReferenceEquals(focus, _controlled);
        var hit = Kit.Buttons("train.cutactions", [driving ? "Release control" : "Take control", "Remove", "Remove all"],
            [focus is not null && (driving || hasEngine), focus is not null, true]);
        if (hit == 0)
        {
            if (driving)
                Release();
            else
                TakeControl();
        }
        else if (hit == 1)
            remove = focus;
        else if (hit == 2)
            Clear();
        if (remove is not null)
            Remove(remove);
        Kit.EndCard();
    }

    // --- Drive. ---

    private static readonly string[] ReverserNames = ["Reverse", "Neutral", "Forward"];

    private void Drive(Train train)
    {
        Kit.BeginCard("Controls");
        Kit.Caption("Reverser");
        // As the lead unit's cab has it: forward is the way the cab faces.
        train.CabReverser = Kit.Segmented("drive.reverser", ReverserNames, train.CabReverser + 1) - 1;
        Kit.Caption("Throttle");
        var notch = train.Notch;
        if (ImGui.SliderInt("##throttle", ref notch, 0, Trains.Train.Notches, notch == 0 ? "Idle"u8 : "Notch %d"u8, ImGuiSliderFlags.AlwaysClamp))
        {
            // Power and dynamic braking are either-or.
            train.Notch = notch;
            if (notch > 0)
                train.Dynamic = 0.0;
        }

        Kit.Caption("Dynamic brake");
        var dynamic = (float)train.Dynamic;
        if (ImGui.SliderFloat("##dynamic", ref dynamic, 0f, 1f, dynamic <= 0.001f ? "Off" : Inv($"{dynamic * 100f:0}%%"), ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoRoundToFormat))
        {
            train.Dynamic = dynamic;
            if (dynamic > 0f)
                train.Notch = 0;
        }

        ImGui.Dummy(new float2(1f, S(2f)));
        var sand = train.Sand;
        if (Kit.Toggle("drive.sand", "Sand · X", ref sand))
            train.Sand = sand;
        var (running, onLine, engines) = train.Body.EngineCount;
        if (engines > 0 && onLine == 0)
            Kit.Status(Caution, running == 0 ? "No engine's running: start them on the Power tab" : "No engine's on line: put one in Run on the Power tab");
        if (train.WheelSlip > 0.05)
            Kit.Status(Stop, train.Sand ? "Wheel slip: ease the throttle back" : "Wheel slip: sand, or ease the throttle back");
        else if (train.Sand && !train.Body.Systems.Any(s => s?.Sanding == true))
            Kit.Status(Caution, "Out of sand");
        Kit.EndCard();

        var air = train.Body.Brakes;
        var head = Math.Max(air.Control, 0);
        Kit.BeginCard("Air brakes", Inv($"{air.Flow:0} cfm"));
        Kit.Caption("Automatic");
        // Release, then the service zone from the first notch (-6 psi) to full service (-26 psi).
        var brake = (float)train.Brake;
        var reduction = AirBrakes.Feed - air.Target;
        var handle = train.Emergency ? "Emergency" : brake <= 0.001f ? "Release" : brake >= 0.999f ? $"Full service -{UnitText.Pressure(reduction)}" : $"Service -{UnitText.Pressure(reduction)}";
        ImGui.BeginDisabled(train.Emergency);
        if (ImGui.SliderFloat("##automatic", ref brake, 0f, 1f, handle, ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoRoundToFormat))
            train.Brake = brake;
        ImGui.EndDisabled();
        Kit.Caption("Independent");
        var independent = (float)train.Independent;
        if (ImGui.SliderFloat("##independent", ref independent, 0f, 1f, independent <= 0.001f ? "Release" : UnitText.Pressure(AirBrakes.IndependentFull * independent),
                ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoRoundToFormat))
            train.Independent = independent;
        ImGui.Dummy(new float2(1f, S(2f)));
        if (Kit.Button("drive.emergency", train.Emergency ? "Emergency · click to release" : "Emergency", tint: train.Emergency ? Stop : null))
            train.Emergency = !train.Emergency;
        ImGui.Dummy(new float2(1f, S(2f)));
        Kit.Stats([
            new Stat("Equalizing", UnitText.Pressure(air.Equalizing)),
            new Stat("Rear pipe", UnitText.Pressure(air.Pipe[^1])),
            new Stat("Cylinder", UnitText.Pressure(air.CylinderOf(head))),
        ]);
        if (air.Valve[head] == ValveState.Emergency || air.Valve[^1] == ValveState.Emergency)
            Kit.Status(Stop, "In emergency: release, and let it recharge to move");
        Kit.EndCard();

        Kit.BeginCard("Handbrakes");
        Handbrakes(train);
        if (Kit.Button("train.release", "Release control · leave it braked"))
            Release();
        Kit.EndCard();

        Kit.BeginCard("Horn and bell");
        var half = (Kit.Width - S(8f)) * 0.5f;
        ImGui.PushItemWidth(half);
        Chooser("##horn", TrainSounds.Horns, TrainSounds.Horn, n => TrainSounds.Horn = n);
        ImGui.SameLine(0f, S(8f));
        Chooser("##bell", TrainSounds.Bells, TrainSounds.Bell, n =>
        {
            TrainSounds.Bell = n;
            if (Sounds.BellRinging)
                Sounds.SetBell(true);
        });
        ImGui.PopItemWidth();
        Kit.Button("drive.horn", Sounds.HornBlowing ? "Horn · blowing" : "Horn · hold, or Space", out _hornButton, half);
        ImGui.SameLine(0f, S(8f));
        if (Kit.Button("drive.bell", Sounds.BellRinging ? "Bell on · B" : "Bell off · B", half, Sounds.BellRinging ? ButtonKind.Primary : ButtonKind.Secondary))
            Sounds.SetBell(!Sounds.BellRinging);
        Kit.EndCard();

        Kit.BeginCard("Running", Inv($"{TimeSpan.FromSeconds(train.Time):m\\:ss}"));
        var forces = train.Body.CouplerForce;
        var lead = train.Cars[0];
        var direction = train.CabSpeed > 0.01 ? "forward" : train.CabSpeed < -0.01 ? "backward" : "standing";
        Kit.Stats([
            new Stat("Going", direction),
            new Stat("Acceleration", Inv($"{train.Acceleration:0.000} m/s²")),
            new Stat("Grade", Inv($"{lead.Grade * 100.0:0.00}%")),
            new Stat("Traction", UnitText.Force(Math.Abs(train.Traction))),
            new Stat("Brakes", UnitText.Force(train.Braking)),
            new Stat("Resistance", UnitText.Force(train.Resistance)),
            new Stat("Gravity", UnitText.Force(train.GravityForce)),
            new Stat("Dynamic", UnitText.Force(train.DynamicBraking)),
            new Stat("Draft / buff", forces.Length == 0 ? "-" : $"{UnitText.ForceK(Math.Max(forces.Max(), 0.0))} / {UnitText.ForceK(Math.Max(-forces.Min(), 0.0))}"),
            new Stat("Curve", lead.Curvature > 1e-6 ? UnitText.Length(1.0 / lead.Curvature) : "straight"),
            new Stat("Length", UnitText.Length(train.Length)),
            new Stat("Mass", UnitText.Mass(train.Mass)),
        ]);
        // Horsepower per ton (kW a tonne in metric): what the train can climb and how fast, on the mass as loaded now.
        var hptLabel = RailWerkz.Core.Settings.Settings.Current.Units == RailWerkz.Core.Settings.UnitSystem.Imperial ? "HPT" : "kW / t";
        Kit.Stats([new Stat("Run", UnitText.Length(train.Distance)), new Stat(hptLabel, UnitText.PowerToWeight(train.Power, train.Mass))], 3);
        if (train.Stopped is { } stopped)
            Kit.Status(Caution, stopped);
        Kit.EndCard();
    }

    /// A combo of sound names; picking one keeps it in the settings.
    private static void Chooser(string id, IReadOnlyList<string> names, string current, Action<string> pick)
    {
        if (!ImGui.BeginCombo(id, names.Count == 0 ? "(no sounds)" : current))
            return;
        foreach (var name in names)
        {
            if (ImGui.Selectable(name, name == current) && name != current)
            {
                pick(name);
                RailWerkz.Core.Settings.Settings.Save();
            }
        }

        ImGui.EndCombo();
    }

    // --- Power: the engines together, and distributed power. ---

    private static readonly string[] ModeNames = ["Synchronous", "Independent"];

    private static void PowerTab(Train train)
    {
        var (running, onLine, engines) = train.Body.EngineCount;
        var starting = train.Body.Starting.Max();
        Kit.BeginCard("Engines", $"{running} of {engines} running", running == engines && engines > 0 ? Style.Clear : Caution);
        Kit.Stats([
            new Stat("Running", $"{running} / {engines}"),
            new Stat("On line", $"{onLine} / {engines}", onLine == 0 && engines > 0 ? Caution : null),
            new Stat("Starting", starting > 0.0 ? Inv($"{starting:0} s") : "-"),
        ]);
        switch (Kit.Buttons("power.engines", ["Start up all", "All on line", "Shut down all"]))
        {
            case 0:
                train.Body.StartEngines();
                break;
            case 1:
                foreach (var systems in train.Body.Systems)
                    if (systems is not null)
                        systems.Isolated = false;
                break;
            case 2:
                train.Body.ShutDownEngines();
                break;
        }

        if (Kit.Button("power.lights", "Usual lights"))
            train.LightUp();
        Kit.Note("The lead's headlight bright with its ditch lights, a trailing engine's dim at the rear. Each engine's own switches are on the Vehicle tab.", TextFaint);
        Kit.EndCard();

        // Fuel, oil, water and sand across the engines: the lowest of each, and all of them filled at once.
        var stocked = train.Body.Systems.Where(s => s?.Capacity is not null).Select(s => s!).ToList();
        if (stocked.Count > 0)
        {
            var lowest = stocked.Min(s => Math.Min(Math.Min(s.FuelShare, s.OilShare), Math.Min(s.WaterShare, s.SandShare)));
            Kit.BeginCard("Supplies", Inv($"lowest {lowest * 100.0:0}%"), lowest < 0.15 ? Stop : lowest < 0.35 ? Caution : Style.Clear);
            Kit.Stats([
                new Stat("Fuel", Inv($"{stocked.Min(s => s.FuelShare) * 100.0:0}%"), stocked.Min(s => s.FuelShare) < 0.15 ? Caution : null),
                new Stat("Oil", Inv($"{stocked.Min(s => s.OilShare) * 100.0:0}%"), stocked.Min(s => s.OilShare) < LocoSystems.LowOil + 0.1 ? Caution : null),
                new Stat("Water", Inv($"{stocked.Min(s => s.WaterShare) * 100.0:0}%"), stocked.Min(s => s.WaterShare) < LocoSystems.LowWater + 0.1 ? Caution : null),
                new Stat("Sand", Inv($"{stocked.Min(s => s.SandShare) * 100.0:0}%"), stocked.Min(s => s.SandShare) < 0.15 ? Caution : null),
                new Stat("Burning", UnitText.FuelRate(stocked.Sum(s => s.FuelRate))),
                new Stat("Engines", $"{stocked.Count}"),
            ]);
            if (Kit.Button("power.fill", "Fill up all · fuel, oil, water, sand"))
                foreach (var s in stocked)
                    s.FillUp();
            Kit.Note("Each engine's own tanks are on the Vehicle tab. Short of oil or water its protection stops the engine; out of fuel, it stops.", TextFaint);
            Kit.EndCard();
        }

        var dp = train.Dp;
        Kit.BeginCard("Distributed power", dp.Remotes.Count == 1 ? "1 remote" : $"{dp.Remotes.Count} remotes");
        if (dp.Remotes.Count == 0)
        {
            Kit.Note("Every locomotive's in the lead consist: there's nothing to work by radio.");
            Kit.EndCard();
            return;
        }

        Kit.Caption("Mode");
        dp.Mode = (DpMode)Kit.Segmented("power.mode", ModeNames, (int)dp.Mode);
        if (dp.Mode == DpMode.Independent)
        {
            Kit.Caption("Remote throttle");
            var notch = dp.RemoteNotch;
            if (ImGui.SliderInt("##remotethrottle", ref notch, 0, Trains.Train.Notches, notch == 0 ? "Idle"u8 : "Notch %d"u8, ImGuiSliderFlags.AlwaysClamp))
            {
                dp.RemoteNotch = notch;
                if (notch > 0)
                    dp.RemoteDynamic = 0.0;
            }

            Kit.Caption("Remote dynamic brake");
            var dynamic = (float)dp.RemoteDynamic;
            if (ImGui.SliderFloat("##remotedynamic", ref dynamic, 0f, 1f, dynamic <= 0.001f ? "Off" : Inv($"{dynamic * 100f:0}%%"), ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoRoundToFormat))
            {
                dp.RemoteDynamic = dynamic;
                if (dynamic > 0f)
                    dp.RemoteNotch = 0;
            }
        }

        ImGui.Dummy(new float2(1f, S(2f)));
        switch (Kit.Buttons("power.link", ["Link", "Unlink"]))
        {
            case 0:
                dp.LinkAll();
                break;
            case 1:
                dp.UnlinkAll();
                break;
        }

        foreach (var remote in dp.Remotes)
            RemoteEntry(train, remote);
        Kit.EndCard();
    }

    /// One remote: its link and comm, the radio path to it, and what it's doing.
    private static void RemoteEntry(Train train, DistributedPower.Remote remote)
    {
        ImGui.Dummy(new float2(1f, S(4f)));
        var draw = ImGui.GetWindowDrawList();
        var top = ImGui.GetCursorScreenPos();
        var head = remote.Group.Head;
        var units = remote.Group.Units.Length;
        var comm = DistributedPower.CommText(remote, train.Time);
        var color = remote.Link != DpLink.Linked ? Caution : remote.CommLost || comm == "lost" ? Stop : comm == "OK" ? Style.Clear : Caution;
        var state = remote.Link switch
        {
            DpLink.NotSetUp => "not set up",
            DpLink.SetUp => "set up, not linked",
            DpLink.Linking => "linking...",
            _ => remote.CommLost ? "comm lost" : $"linked, comm {comm}",
        };
        ImGui.Indent(S(10f));
        Kit.Status(color, $"Car {head + 1}  {train.Cars[head].Label}{(units > 1 ? $" +{units - 1}" : "")}  ·  {state}");
        var power = remote.Applied.Dynamic > 0.0 ? Inv($"DB {remote.Applied.Dynamic * 100.0:0}%") : remote.Applied.Notch == 0 ? "Idle" : Inv($"N{remote.Applied.Notch}");
        Kit.Stats([
            new Stat("Distance", UnitText.Length(remote.Distance)),
            new Stat("Signal", Inv($"{remote.Signal * 100.0:0}%"), remote.Signal < 0.5 ? Caution : null),
            new Stat("Round trip", remote.RoundTrip > 0.0 ? Inv($"{remote.RoundTrip:0.0} s") : "-"),
            new Stat("Doing", remote.Link == DpLink.Linked ? power : "-"),
            new Stat("Its pipe", remote.Link == DpLink.Linked ? UnitText.Pressure(remote.PipeHeard) : "-"),
            new Stat("Brake valve", remote.Valve.CutIn ? Inv($"in, {remote.Valve.Flow:0} cfm") : "cut out"),
        ]);
        Kit.Note(char.ToUpperInvariant(DistributedPower.SightText(remote)[0]) + DistributedPower.SightText(remote)[1..], remote.Sight == Sight.Blocked ? Caution : TextFaint);
        ImGui.Unindent(S(10f));
        var bottom = ImGui.GetItemRectMax().Y;
        draw.AddRectFilled(top, new float2(top.X + S(2f), bottom), color.WithAlpha(0.7f));
    }

    // --- Vehicle. ---

    private static string KindText(VehicleDef def) => def.Kind == VehicleKind.Locomotive ? "LOCO" : def.Kind.ToString().ToUpperInvariant();

    private void VehicleTab(Train train)
    {
        var c = CultureInfo.InvariantCulture;
        Kit.BeginCard("Vehicles", $"{train.Cars.Count}");
        var rows = Math.Min(train.Cars.Count, 6);
        var width = Kit.Width;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new float4(0f, 0f, 0f, 0f));
        if (ImGui.BeginChild("##vehicles", new float2(width, rows * S(28f) + S(2f)), ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            var inner = ImGui.GetContentRegionAvail().X;
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0f, 0f));
            for (var i = 0; i < train.Cars.Count; i++)
                if (Kit.Row($"vehicle.{i}", (i + 1).ToString(c), train.Cars[i].Label, KindText(train.Cars[i].Def), i == Selected, inner))
                    Select(i == Selected ? -1 : i);
            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        Kit.Toggle("vehicle.tags", "Name tags in the view", ref ShowTags);
        Kit.EndCard();

        if (Selected < 0 || Selected >= train.Cars.Count)
        {
            Kit.Note("Pick one here, or click a vehicle in the view.", TextFaint);
            return;
        }

        var picked = train.Cars[Selected];
        var def = picked.Def;
        LeadControl(train, Selected);
        Kit.BeginCard(def.Name, $"{Selected + 1} of {train.Cars.Count}");
        if (!ReferenceEquals(_nameFor, picked))
        {
            _name = new ImInputString(48, picked.Tag ?? "");
            _nameFor = picked;
        }

        Kit.Caption("Name, shown on its tag");
        var entered = ImGui.InputText("##name", _name, ImGuiInputTextFlags.EnterReturnsTrue);
        if (entered || ImGui.IsItemDeactivatedAfterEdit())
            Rename(picked, _name.ToString());
        ImGui.Dummy(new float2(1f, S(2f)));
        // Its handbrake: wound on over a few seconds, it holds as a full service does.
        var hand = train.Body.HandbrakeOn[Selected];
        if (Kit.Toggle("vehicle.handbrake", train.Body.Handbrake[Selected] is var taken and > 0.0 and < 1.0
                ? $"Handbrake · winding {(hand ? "on" : "off")}" : "Handbrake", ref hand))
            train.Body.HandbrakeOn[Selected] = hand;
        ImGui.Dummy(new float2(1f, S(2f)));
        var type = string.Join("  ·  ", new[] { def.Builder, def.Type, def.Built }.Where(t => t.Length > 0));
        if (type.Length > 0)
            Kit.Note(type, TextFaint);
        var load = !def.Kind.IsRollingStock() || !def.CanLoad ? "" : picked.Fill <= 0.0 ? " empty" : picked.Fill >= 1.0 ? " loaded" : $" {picked.Fill * 100.0:0}% loaded";
        var stats = new List<Stat>
        {
            new("Length", UnitText.Length(def.Length)),
            new("Mass", UnitText.Mass(picked.Mass) + load),
            new("Axles", def.Axles.ToString(c)),
            new("Facing", picked.Reversed ? "turned" : "forward"),
            new("Livery", picked.Livery?.Name ?? "-"),
            new("Top speed", def.MaxSpeed > 0.0 ? UnitText.Speed(def.MaxSpeed) : "-"),
        };
        if (def.Kind == VehicleKind.Locomotive)
        {
            stats.Add(new("Power", UnitText.Power(def.Power)));
            stats.Add(new("Tractive", UnitText.Force(def.StartingTractiveEffort)));
            stats.Add(new("Dynamic", def.DynamicBrakeEffort > 0.0 ? UnitText.Force(def.DynamicBrakeEffort) : "-"));
        }

        Kit.Stats(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stats));
        if (def.CanLoad)
            LoadControl(train, Selected, picked);
        Kit.EndCard();

        // A locomotive's fuel, oil, water and sand, up where they're seen.
        if (train.Body.Systems[Selected] is { } stocked)
            Supplies(train, stocked, Selected);

        // Its own air, and the bleed rod.
        var air = train.Body.Brakes;
        var valve = air.Valve[Selected];
        Kit.BeginCard("Air", valve.ToString().ToLowerInvariant(), valve == ValveState.Emergency ? Stop : valve == ValveState.Release ? Style.Clear : Caution);
        Kit.Stats([
            new Stat("Brake pipe", UnitText.Pressure(air.Pipe[Selected])),
            new Stat("Reservoir", UnitText.Pressure(air.Aux[Selected])),
            new Stat("Cylinder", UnitText.Pressure(air.CylinderOf(Selected))),
        ]);
        if (Kit.Button("vehicle.bleed", "Bleed the brake"))
            air.Bleed(Selected);
        Kit.Note("The bleed rod lets its brake off, to move a car with no air on it.", TextFaint);
        Kit.EndCard();

        if (train.Body.Systems[Selected] is { } systems)
        {
            DpRole(train, Selected);
            LocoControls(systems, Selected);
        }
    }

    /// Where a locomotive stands in the distributed power: the lead, trailing a consist, or a remote (set up here).
    private static void DpRole(Train train, int car)
    {
        var (group, remote) = train.Dp.GroupOf(car);
        if (group is null)
            return;
        Kit.BeginCard("Distributed power");
        if (remote is null)
            Kit.Status(Style.Clear, car == group.Head ? "Lead unit: works the train" : $"Trailing the lead at car {group.Head + 1}, on MU cables");
        else if (car != group.Head)
            Kit.Status(TextDim, $"Trailing the remote at car {group.Head + 1}, on MU cables");
        else
        {
            var setUp = remote.SetUp;
            if (Kit.Toggle($"dp.setup.{car}", "Set up as a remote", ref setUp))
                remote.SetUp = setUp;
            Kit.Note(remote.Link switch
            {
                DpLink.NotSetUp => "Not powered: it has no MU cables to the lead. Set it up, then link it from the lead (Power tab).",
                DpLink.SetUp => "Set up: waiting for the lead to link it (Power tab).",
                DpLink.Linking => "Linking...",
                _ => remote.CommLost ? "Linked, but comm's lost: it's idling with its brake valve cut out." : "Linked: it does as the lead says, by radio.",
            }, TextFaint);
        }

        Kit.EndCard();
    }

    /// A locomotive's fuel, oil, water and sand: each a gauge, what it weighs with them aboard against dry and wet, what
    /// it's burning, and one button to fill the lot.
    private static void Supplies(Train train, LocoSystems s, int car)
    {
        if (s.Capacity is not { } full)
            return;
        var def = train.Cars[car].Def;
        var lowest = Math.Min(Math.Min(s.FuelShare, s.OilShare), Math.Min(s.WaterShare, s.SandShare));
        Kit.BeginCard("Supplies", s.Sanding ? "sanding" : Inv($"{lowest * 100.0:0}% lowest"), lowest < 0.15 ? Stop : lowest < 0.35 ? Caution : s.Sanding ? Accent : Style.Clear);
        void Row(string name, double share, string amount, string of, double low)
        {
            Kit.Caption($"{name}   {amount} of {of}");
            Kit.Gauge((float)Math.Clamp(share, 0.0, 1.0), share < low ? Stop : share < low + 0.15 ? Caution : Accent);
            ImGui.Dummy(new float2(1f, S(2f)));
        }

        Row("Fuel", s.FuelShare, UnitText.Volume(s.Fuel), UnitText.Volume(full.FuelL), 0.05);
        Row("Lube oil", s.OilShare, UnitText.Volume(s.Oil), UnitText.Volume(full.OilL), LocoSystems.LowOil);
        Row("Cooling water", s.WaterShare, UnitText.Volume(s.Water), UnitText.Volume(full.WaterL), LocoSystems.LowWater);
        Row("Sand", s.SandShare, UnitText.SandVolume(s.Sand), UnitText.SandVolume(full.SandM3), 0.05);
        Kit.Stats([
            new Stat("Weighs", UnitText.Mass(def.MassDry + s.SuppliesMass)),
            new Stat("Dry / wet", $"{UnitText.Mass(def.MassDry)} / {UnitText.Mass(def.MassDry + full.FullMass)}"),
            new Stat("Burning", UnitText.FuelRate(s.FuelRate)),
            new Stat("Adhesion", Inv($"{def.Adhesion * (s.Sanding ? TrainBody.SandGrip : 1.0) * 100.0:0}%{(s.Sanding ? " sanded" : "")}")),
            new Stat("Traction", def.AcTraction ? "AC" : "DC"),
            new Stat("Wheels", train.Body.WheelSlip[car] > 0.05 ? "slipping" : "gripping", train.Body.WheelSlip[car] > 0.05 ? Stop : null),
        ]);
        if (Kit.Button($"loco.fill.{car}", "Fill up · fuel, oil, water, sand"))
            s.FillUp();
        Kit.EndCard();
    }

    private static readonly string[] IsolationNames = ["Start / Stop", "Run"];
    private static readonly string[] HeadlightNames = ["Off", "Dim", "Bright"];

    /// A locomotive's own switches: battery and breakers, its engine's start, stop and isolation, its lights.
    private static void LocoControls(LocoSystems s, int car)
    {
        var id = car.ToString(CultureInfo.InvariantCulture);
        Kit.BeginCard("Electrical", s.Battery ? Inv($"{s.Volts:0.0} V") : "off", !s.Battery ? TextFaint : s.Volts < 60.0 ? Caution : Style.Clear);
        Kit.Toggle($"loco.battery.{id}", "Battery switch", ref s.Battery);
        ImGui.Dummy(new float2(1f, S(2f)));
        Kit.Gauge((float)Math.Clamp(s.Volts / 80.0, 0.0, 1.0), s.Volts < 60.0 ? Caution : Accent);
        ImGui.Dummy(new float2(1f, S(4f)));
        if (s.Battery)
            Kit.Stats([
                new Stat("Voltage", Inv($"{s.Volts:0.0} V"), s.Volts < 60.0 ? Caution : null),
                new Stat("Current", Inv($"{(s.BatteryAmps >= 0.0 ? "+" : "-")}{Math.Abs(s.BatteryAmps):0} A")),
                new Stat("Charge", Inv($"{s.Charge * 100.0:0}%")),
            ]);
        else
            Kit.Note("No supply", TextFaint);
        Kit.Caption("Breakers");
        Kit.Toggle($"loco.control.{id}", "Control", ref s.ControlBreaker);
        ImGui.SameLine(0f, S(18f));
        Kit.Toggle($"loco.fuel.{id}", "Fuel pump", ref s.FuelPumpBreaker);
        ImGui.SameLine(0f, S(18f));
        Kit.Toggle($"loco.lights.{id}", "Lights", ref s.LightsBreaker);
        ImGui.Dummy(new float2(1f, S(2f)));
        if (!s.ControlPowered)
            Kit.Status(TextFaint, "Computer off");
        else if (s.BootLeft > 0.0)
            Kit.Status(Caution, Inv($"Computer starting up, {s.BootLeft:0} s"));
        else
            Kit.Status(Style.Clear, "Computer ready");
        Kit.EndCard();

        var (state, color) = s.Engine switch
        {
            EngineState.Stopped => ("shut down", TextFaint),
            EngineState.Priming => (Inv($"priming {s.StateLeft:0} s"), Caution),
            EngineState.Cranking => (Inv($"cranking {s.StateLeft:0} s"), Caution),
            EngineState.Running => (s.Isolated ? "running, isolated" : "on line", Style.Clear),
            _ => (Inv($"stopping {s.StateLeft:0} s"), Caution),
        };
        Kit.BeginCard("Engine", state, color);
        Kit.Caption("Isolation switch");
        s.Isolated = Kit.Segmented($"loco.isolation.{id}", IsolationNames, s.Isolated ? 0 : 1) == 0;
        ImGui.Dummy(new float2(1f, S(2f)));
        var canStart = s.CanStart(out var why);
        switch (Kit.Buttons($"loco.engine.{id}", ["Start", "Stop"], [canStart, s.Turning]))
        {
            case 0:
                s.Start();
                break;
            case 1:
                s.Stop();
                break;
        }

        if (s.Notice is { } notice && !s.Turning)
            Kit.Status(Caution, notice);
        else if (s.Engine == EngineState.Stopped && why is not null)
            Kit.Status(TextFaint, why);
        if (s.Engine == EngineState.Stopped)
            Kit.Note("Battery on, control and fuel pump breakers on, wait for the computer, isolation in Start / Stop, then Start. Run takes power.", TextFaint);
        Kit.EndCard();

        Kit.BeginCard("Lights", s.LampBrightness > 0.0 ? s.Flashing ? "flashing" : "powered" : "no power", s.LampBrightness > 0.0 ? Style.Clear : TextFaint);
        Kit.Caption("Front headlight");
        s.FrontHeadlight = (HeadlightSetting)Kit.Segmented($"loco.front.{id}", HeadlightNames, (int)s.FrontHeadlight);
        Kit.Caption("Rear headlight");
        s.RearHeadlight = (HeadlightSetting)Kit.Segmented($"loco.rear.{id}", HeadlightNames, (int)s.RearHeadlight);
        ImGui.Dummy(new float2(1f, S(4f)));
        Kit.Toggle($"loco.ditch.{id}", "Ditch lights", ref s.DitchLights);
        Kit.Note(s.LampBrightness <= 0.0 ? "No power to the lights: they need the battery and the lights breaker."
            : $"At the {(s.DitchAtFront ? "front" : "rear")} end (where the headlight's brighter); they flash with the horn.", TextFaint);
        Kit.EndCard();
    }

    // --- Coupling. ---

    private static readonly string[] CockNames = ["Closed", "Partial", "Open"];

    private static int CockIndex(double value) => value <= 0.0 ? 0 : value >= 1.0 ? 2 : 1;

    private static double CockValue(int index) => index switch { 0 => 0.0, 1 => AirBrakes.Partial, _ => 1.0 };

    private void CouplingTab(Train train)
    {
        var count = train.Cars.Count;
        string Name(int k) => k < 0 ? "Front end" : k >= count - 1 ? "Rear end" : $"{k + 1}  |  {k + 2}";
        Kit.BeginCard("Coupling");
        if (ImGui.BeginCombo("##coupling", SelectedCoupling is { } shown ? Name(shown) : "(pick one)"))
        {
            for (var k = -1; k < count; k++)
                if (ImGui.Selectable($"{Name(k)}##k{k}", SelectedCoupling == k))
                    SelectCoupling(k);
            ImGui.EndCombo();
        }

        if (SelectedCoupling is null)
            Kit.Note("Or click one in the view: the small marks between the vehicles.", TextFaint);
        Kit.EndCard();
        if (SelectedCoupling is not { } at)
            return;

        var air = train.Body.Brakes;
        if (at < 0 || at >= count - 1)
        {
            // An end of the train: just its angle cock.
            var car = at < 0 ? 0 : count - 1;
            var cock = at < 0 ? air.FrontCock : air.RearCock;
            Kit.BeginCard($"{(at < 0 ? "Front" : "Rear")} of the train", train.Cars[car].Label, cock > 0.0 ? Stop : Style.Clear);
            Kit.Caption("Angle cock");
            var picked = CockValue(Kit.Segmented("coupling.end", CockNames, CockIndex(cock)));
            if (at < 0)
                air.FrontCock = picked;
            else
                air.RearCock = picked;
            ImGui.Dummy(new float2(1f, S(2f)));
            Kit.Stats([new Stat("Brake pipe", UnitText.Pressure(air.Pipe[car]))]);
            if (picked > 0.0)
                Kit.Status(Stop, "Open on the end of the train: the pipe's venting to the air");
            else
                Kit.Status(Style.Clear, "Closed, as the end of a train should be");
            Kit.EndCard();
            return;
        }

        var body = train.Body;
        var (ahead, behind) = (train.Cars[at], train.Cars[at + 1]);
        var coupled = body.Coupled[at];
        Kit.BeginCard("Knuckles", coupled ? "coupled" : "uncoupled", coupled ? Style.Clear : Caution);
        Kit.Stats([
            new Stat("Ahead", $"{at + 1}  {ahead.Label}"),
            new Stat("Behind", $"{at + 2}  {behind.Label}"),
        ], 2);
        if (coupled)
        {
            var force = body.CouplerForce[at];
            Kit.Stats([new Stat(force >= 0.0 ? "Pulling" : "Pushing", UnitText.ForceK(Math.Abs(force)))], 2);
            if (Kit.Button("coupling.uncouple", "Uncouple · lift the cut lever") && !body.Uncouple(at, out var why) && why is not null)
                _notice = why;
            if (air.Hose[at] && (air.CockAhead[at] > 0.0 || air.CockBehind[at] > 0.0))
                Kit.Status(Caution, "The air's on through here: close both angle cocks and part the hose first, or it'll pull apart and put the brakes in emergency.");
        }
        else
        {
            Kit.Stats([new Stat("Apart", UnitText.Length(body.Gap(at)))], 2);
            Kit.Note("Run them back together to couple (under 4 mph).", TextFaint);
        }

        Kit.EndCard();

        var gap = body.Gap(at);
        var venting = !air.Hose[at] && (air.CockAhead[at] > 0.0 || air.CockBehind[at] > 0.0);
        var through = air.Hose[at] && air.CockAhead[at] > 0.0 && air.CockBehind[at] > 0.0;
        Kit.BeginCard("Air hose", air.Hose[at] ? "joined" : "parted", venting ? Stop : through ? Style.Clear : Caution);
        if (air.Hose[at])
        {
            if (Kit.Button("coupling.part", "Part the hose"))
                air.Hose[at] = false;
        }
        else if (Kit.Button("coupling.join", gap > 0.3 ? "Too far apart to join" : "Join the hose", enabled: gap <= 0.3))
            air.Hose[at] = true;

        Kit.Caption($"Car {at + 1}, rear angle cock");
        air.CockAhead[at] = CockValue(Kit.Segmented("coupling.ahead", CockNames, CockIndex(air.CockAhead[at])));
        Kit.Caption($"Car {at + 2}, front angle cock");
        air.CockBehind[at] = CockValue(Kit.Segmented("coupling.behind", CockNames, CockIndex(air.CockBehind[at])));
        ImGui.Dummy(new float2(1f, S(2f)));
        Kit.Stats([
            new Stat("Pipe ahead", UnitText.Pressure(air.Pipe[at])),
            new Stat("Pipe behind", UnitText.Pressure(air.Pipe[at + 1])),
        ], 2);
        if (venting)
            Kit.Status(Stop, "A cock's open with no hose on it: the pipe's venting to the air");
        else if (!through)
            Kit.Status(Caution, "The air's cut off here: the cars behind don't get the brake pipe");
        else if (air.CockAhead[at] < 1.0 || air.CockBehind[at] < 1.0)
            Kit.Status(Caution, "A cock's part open: the air's throttled, so the brakes behind answer slowly");
        else
            Kit.Status(Style.Clear, "The air's through");
        Kit.EndCard();
    }
}
