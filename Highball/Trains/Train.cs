using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// One vehicle of a train on the track: what it is, and where it stands this step. Every place faces the way the train
/// does (towards its front).
public sealed class TrainCar
{
    public required VehicleDef Def { get; init; }
    public Livery? Livery { get; init; }
    /// Turned round in the consist: its own front faces the train's rear.
    public bool Reversed { get; init; }
    public bool Loaded { get; init; }

    /// Its name, given by the player and shown on its tag; null for none.
    public string? Tag { get; set; }

    /// Which car of the consist it was made from, to save its name back there.
    public int ConsistIndex { get; init; }

    /// Its name, or its type's when it hasn't one.
    public string Label => string.IsNullOrWhiteSpace(Tag) ? Def.Name : Tag;

    public double Mass => Def.Mass(Loaded);

    public double TruckCentres => Def.Centres;

    /// From the end towards the train's front to the truck nearer it, and from the other end: its own front's unless it's
    /// turned round.
    public double LeadingOverhang => Reversed ? Def.RearOverhang : Def.FrontOverhang;
    public double TrailingOverhang => Reversed ? Def.FrontOverhang : Def.RearOverhang;

    public TrackPosition Front, FrontTruck, RearTruck, Rear;

    /// The truck centres on the rail tops, in world metres.
    public (double X, double Y, double Z) FrontAt, RearAt;

    /// Rise over run towards the train's front, and how sharply the track bends (1 / radius), under the car.
    public double Grade, Curvature;
}

/// A train on the rail graph: its cars from the front, where its front stands, and how fast it's going. Moved by a
/// fixed step of TimeStep seconds, in doubles, and kept on the track as an edge and a distance along it, so nothing drifts
/// however long it runs.
///
/// The dynamics are Body's: every car its own mass on its own grade and curve, joined by couplers with slack, braked by
/// its own cylinder off the train's air brakes. Here the lead car's front is moved along the track by what Body says it
/// went, and each car behind is placed back from it by the cars' lengths and how far each coupling's stretched or bunched.
public sealed class Train
{
    public const double TimeStep = 1.0 / 120.0;
    private const int MaxStepsPerFrame = 30;

    public const int Notches = 8;

    private readonly SwitchMemory _memory = new();
    private double _pending;

    /// How far the front moved on the plan in the last step (negative backing up): what drawing goes back over.
    private double _lastPlan;

    public RailGraph Graph { get; private set; }
    public string Name { get; }
    public List<TrainCar> Cars { get; }

    /// The front of the leading car's coupler, facing the way the train's front faces.
    public TrackPosition Head { get; private set; }

    /// The cars as masses, their couplers and the air brakes.
    public TrainBody Body { get; }

    /// The locomotives as consists, the lead's and the distributed power remotes', and the radio between them.
    public DistributedPower Dp { get; }

    /// Something happened worth telling the crew: a coupling made, a hose parted, a DP link made or lost.
    public Action<string>? Happened { get; set; }

    /// The lead car's speed, metres a second along the rail: positive towards the train's front.
    public double Speed => Body.Velocity[0];

    // The controls, as worked from the leading cab: the lead's consist answers them, and the distributed power remotes as
    // the radio tells them.
    /// +1 to pull towards the train's front, -1 towards its rear, 0 neutral.
    public int Reverser { get; set; }
    /// Throttle notch, 0 (idle) to Notches.
    public int Notch { get; set; }
    /// Automatic (train) brake, 0 release, then the service zone from the first notch to full service at 1.
    public double Brake
    {
        get => Body.Brakes.Handle;
        set => Body.Brakes.Handle = value;
    }

    /// Automatic brake in emergency.
    public bool Emergency
    {
        get => Body.Brakes.EmergencyHandle;
        set => Body.Brakes.EmergencyHandle = value;
    }

    /// Dynamic brake, 0 off to 1 full. Taking power and dynamic braking are either-or: while it's on, the throttle's ignored.
    public double Dynamic { get; set; }

    /// What the dynamic brakes held back with in the last step, newtons.
    public double DynamicBraking { get; private set; }

    /// Independent (locomotive) brake, 0 release to 1 full.
    public double Independent
    {
        get => Body.Brakes.IndependentHandle;
        set => Body.Brakes.IndependentHandle = value;
    }

    /// Runs on the switch nodes a train forces over by trailing through them set against it, as a real one does.
    public Action<long>? RunThrough { get; set; }

    // What the last step worked out, for the gauges.
    public double Traction { get; private set; }
    public double GravityForce { get; private set; }
    public double Resistance { get; private set; }
    public double Braking { get; private set; }
    public double Acceleration { get; private set; }
    public double Distance { get; private set; }
    public double Time { get; private set; }

    /// Why it stopped dead, if it did: the end of the track ahead or behind.
    public string? Stopped { get; private set; }

    public double Mass { get; }
    public double Length { get; }
    public double Power { get; }

    /// The couplings' extensions before the last step, to put back if the step couldn't be placed.
    private readonly double[] _extensionBefore;

    private Train(RailGraph graph, string name, List<TrainCar> cars)
    {
        Graph = graph;
        Name = name;
        Cars = cars;
        Mass = cars.Sum(c => c.Mass);
        Length = cars.Sum(c => c.Def.Length);
        Power = cars.Sum(c => c.Def.Power);
        Body = new TrainBody(cars);
        _extensionBefore = new double[Body.Extension.Length];
        Dp = new DistributedPower(this);
        Body.Happened = what => Happened?.Invoke(what);
        Dp.Happened = what => Happened?.Invoke(what);
    }

    /// The consist on the track with its front at head, its cars behind it. Null, with why, if it can't be: nothing in it
    /// the catalog knows, or not enough track behind (or in front, to move it up onto).
    public static Train? Place(RailGraph graph, Consist consist, TrackPosition head, out string? why, PlacedAs state = PlacedAs.StartingUp)
    {
        var cars = consist.Cars.Select((c, i) => (Car: c, Index: i)).Where(c => c.Car.Def is not null)
            .Select(c => new TrainCar { Def = c.Car.Def!, Livery = c.Car.Def!.FindLivery(c.Car.Livery), Reversed = c.Car.Reversed, Loaded = c.Car.Loaded, Tag = c.Car.Tag, ConsistIndex = c.Index })
            .ToList();
        if (cars.Count == 0)
        {
            why = "Nothing in the consist the catalog knows";
            return null;
        }

        var train = new Train(graph, consist.Name, cars);
        if (!train.TryPlace(head, out var short1))
        {
            // Not enough track behind: move the front up by what's missing and try again.
            var moved = head;
            graph.Move(ref moved, short1 + 0.01);
            if (!train.TryPlace(moved, out _))
            {
                why = $"Not enough track for a train {UnitText.Length(train.Length)} long here";
                return null;
            }
        }

        // Placed as a parked engine stands: independent brake on, the engines as asked. The lead engine's headlight is on
        // bright with its ditch lights, at the train's front, and a trailing engine at the rear shows a dim light behind.
        train.Independent = 1.0;
        switch (state)
        {
            case PlacedAs.Running:
                train.Body.LeaveEnginesRunning();
                break;
            case PlacedAs.StartingUp:
                train.Body.StartEngines();
                break;
            default:
                train.Body.ShutDownEngines();
                break;
        }

        if (state != PlacedAs.ShutDown)
        {
            train.LightUp();
            // Made up and tested: the remotes set up and linked.
            train.Dp.LinkAllNow();
        }
        why = null;
        Log.Info($"Train {train.Name}: {cars.Count} vehicles, {train.Length:0.0} m, {train.Mass / 1000.0:0} t, placed on edge {train.Head.Edge}");
        return train;
    }

    /// The usual lights for a train: the lead engine's headlight bright towards the front with its ditch lights, and if the
    /// last vehicle's an engine, its headlight dim towards the rear. Every other lamp off.
    public void LightUp()
    {
        var systems = Body.Systems;
        foreach (var s in systems)
        {
            if (s is null)
                continue;
            s.FrontHeadlight = s.RearHeadlight = HeadlightSetting.Off;
            s.DitchLights = false;
        }

        var lead = Array.FindIndex(systems, s => s is not null);
        if (lead < 0)
            return;
        var front = systems[lead]!;
        if (Cars[lead].Reversed)
            front.RearHeadlight = HeadlightSetting.Bright;
        else
            front.FrontHeadlight = HeadlightSetting.Bright;
        front.DitchLights = true;
        var last = systems.Length - 1;
        if (last != lead && systems[last] is { } rear)
        {
            if (Cars[last].Reversed)
                rear.FrontHeadlight = HeadlightSetting.Dim;
            else
                rear.RearHeadlight = HeadlightSetting.Dim;
        }
    }

    /// The train on a graph built again (after the track changed): its front put on the nearest track to where it was,
    /// facing as near the same way. False if there's no track there for it now.
    public bool MoveTo(RailGraph graph)
    {
        var (x, _, z) = Graph.PositionOf(Head);
        var (fx, fz) = Graph.FacingOf(Head);
        if (graph.Nearest(x, z, 5.0) is not { } near)
            return false;
        var (nx, nz) = graph.FacingOf(near);
        var head = nx * fx + nz * fz >= 0.0 ? near : near.Reversed;
        var old = Graph;
        Graph = graph;
        _memory.Clear();
        if (TryPlace(head, out _))
            return true;
        Graph = old;
        return false;
    }

    /// Puts each car behind the front, walking back along the rail: its overhangs and truck centres, and between one car
    /// and the next whatever its coupling's stretched (or bunched, less). False, with how much track was missing, if it ran out.
    private bool TryPlace(TrackPosition head, out double missing)
    {
        _memory.Begin();
        var at = head;
        missing = 0.0;
        var carry = 0.0;
        for (var i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            // From the last car's rear truck: its trailing overhang and the coupling's gap, always a few metres.
            if (carry != 0.0 && !Back(ref at, carry, ref missing))
                return false;
            if (i > 0)
                Cars[i - 1].Rear = at;
            car.Front = at;
            if (!Back(ref at, car.LeadingOverhang, ref missing))
                return false;
            car.FrontTruck = at;
            if (!Back(ref at, car.TruckCentres, ref missing))
                return false;
            car.RearTruck = at;
            carry = car.TrailingOverhang + (i < Body.Extension.Length ? Body.Extension[i] : 0.0);
        }

        if (!Back(ref at, carry, ref missing))
            return false;
        Cars[^1].Rear = at;

        _memory.Forget();
        Head = head;
        foreach (var car in Cars)
        {
            car.FrontAt = Graph.PositionOf(car.FrontTruck);
            car.RearAt = Graph.PositionOf(car.RearTruck);
            car.Grade = (Graph.GradeOf(car.FrontTruck) + Graph.GradeOf(car.RearTruck)) * 0.5;
            car.Curvature = (Math.Abs(Graph.CurvatureOf(car.FrontTruck)) + Math.Abs(Graph.CurvatureOf(car.RearTruck))) * 0.5;
        }

        return true;
    }

    /// Steps a place back along the rail by a length measured along it (so a little less on the plan, on a grade).
    private bool Back(ref TrackPosition at, double alongRail, ref double missing)
    {
        var grade = Graph.GradeOf(at);
        var plan = alongRail / Math.Sqrt(1.0 + grade * grade);
        var move = Graph.Move(ref at, -plan, _memory);
        if (move.Stop == TrackStop.None)
            return true;
        missing = plan + move.Moved; // Moved is negative
        return false;
    }

    /// Runs the physics on for a frame's worth of time, in fixed steps.
    public void Update(double dt)
    {
        _pending = Math.Min(_pending + dt, MaxStepsPerFrame * TimeStep);
        while (_pending >= TimeStep)
        {
            Tick(TimeStep);
            _pending -= TimeStep;
        }
    }

    /// How far the clock is into the next step, 0 to 1.
    public double Alpha => Math.Clamp(_pending / TimeStep, 0.0, 1.0);

    /// Where to draw each car's truck centres this frame: between the last step and the one before it, as far as the clock
    /// has gone into the next step, so the train moves smoothly whatever the frame rate. Found on the track (the front put
    /// back by the part of the last step not yet due, then each car walked back from it as the steps place them), so the
    /// trucks are always on the rails; at a step's end it's exactly where the step put them. A frame behind the physics.
    public void Pose(List<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> into)
    {
        into.Clear();
        var memory = _memory.Copy();
        var at = Head;
        var back = (1.0 - Alpha) * _lastPlan;
        if (back != 0.0)
            Graph.Move(ref at, -back, memory);

        for (var i = 0; i < Cars.Count; i++)
        {
            var car = Cars[i];
            Walk(ref at, car.LeadingOverhang, memory);
            var front = Graph.PositionOf(at);
            Walk(ref at, car.TruckCentres, memory);
            into.Add((front, Graph.PositionOf(at)));
            Walk(ref at, car.TrailingOverhang + (i < Body.Extension.Length ? Body.Extension[i] : 0.0), memory);
        }
    }

    /// Back along the rail by a length measured along it, as Back does, with no account kept of the track running out.
    private void Walk(ref TrackPosition at, double alongRail, SwitchMemory memory)
    {
        var grade = Graph.GradeOf(at);
        Graph.Move(ref at, -alongRail / Math.Sqrt(1.0 + grade * grade), memory);
    }

    private void Tick(double h)
    {
        var before = Body.Velocity.Average();
        Array.Copy(Body.Extension, _extensionBefore, _extensionBefore.Length);
        // The power's cut in emergency, by the handle or the lead unit's valve tripping (its PC switch).
        var brakes = Body.Brakes;
        var cut = Emergency || (brakes.Control >= 0 && brakes.Valve[brakes.Control] == ValveState.Emergency);
        // Each locomotive's told what to do: the lead's consist straight off the controls, the remotes by radio.
        Dp.Update(Time);
        var along = Body.Step(h, cut);

        Traction = Body.Traction.Sum();
        GravityForce = Body.GravityForce;
        Resistance = Body.Resistance.Sum();
        Braking = Body.Braking.Sum();
        DynamicBraking = Body.DynamicBraking.Sum();
        Time += h;
        _lastPlan = 0.0;

        // Along the rail to along the plan, at the front.
        var head = Head;
        var stop = TrackStop.None;
        var moved = 0.0;
        if (along != 0.0)
        {
            var grade = Graph.GradeOf(Head);
            var move = Graph.Move(ref head, along / Math.Sqrt(1.0 + grade * grade), _memory);
            foreach (var node in move.Trailed)
                RunThrough?.Invoke(node);
            stop = move.Stop;
            moved = move.Moved;
        }

        if (stop != TrackStop.None)
        {
            Stopped = "The end of the track ahead";
            Body.HaltLead();
        }

        // The cars behind are placed even when the lead didn't move: the slack between them may have.
        if (!TryPlace(head, out _))
        {
            // The rear would run off the end: it all stays where it was, stopped.
            Stopped = "The end of the track behind";
            Array.Copy(_extensionBefore, Body.Extension, _extensionBefore.Length);
            Body.Halt();
            Acceleration = 0.0;
            TryPlace(Head, out _);
            return;
        }

        if (stop == TrackStop.None && along != 0.0)
            Stopped = null;
        _lastPlan = moved;
        Distance += Math.Abs(moved);
        Acceleration = (Body.Velocity.Average() - before) / h;
    }
}
