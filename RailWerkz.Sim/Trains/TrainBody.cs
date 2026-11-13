namespace Highball.Trains;

/// What a locomotive's told to do: its reverser (+1 towards the train's front), throttle notch and dynamic brake (0 to 1;
/// while it's on the notch is ignored).
public readonly record struct LocoCommand(int Reverser, int Notch, double Dynamic);

/// A train's cars as separate masses along the track, joined by couplers with slack, and its air brakes: the dynamics,
/// apart from where on the track it all is (Train keeps that).
///
/// Each car has its own speed. The forces on it are its traction if it's a locomotive, gravity on its grade, rolling and
/// curve resistance (Davis, as AREMA gives it), its brake shoes (from its own brake cylinder), and the couplers at its
/// two ends. Each coupling is a pair of knuckles with FreeSlack of play between them, then two friction draft gears in
/// series that take up to GearTravel more. These push back hard as they close, much less as they open (Unloading),
/// which is where the energy of a run-in goes. Past the gear travel it's solid. So slack runs in and out as it would:
/// a train bunched by its brakes stretches one car at a time as the engines load up, and a heavy train's rear runs in
/// when the head brakes first.
///
/// The couplers are stiff and the cars light by comparison, so it's stepped finely: Substeps a physics step, more if
/// the air brakes need it.
public sealed class TrainBody
{
    /// Knuckle play each way from centred, metres (an inch in all).
    public const double FreeSlack = 0.0127;
    /// What the two draft gears of a coupling take up between them past the knuckle play, metres.
    public const double GearTravel = 0.165;
    /// Draft gear rate, newtons per metre: about 600 klbf on each gear at full travel, the two in series.
    public const double GearStiffness = 1.6e7;
    /// Gone solid past the gear travel.
    public const double SolidStiffness = 1.5e8;
    /// The gears' friction lets them give back only this much of the force as they open again.
    public const double Unloading = 0.45;
    /// Viscous damping in an engaged coupling, newton-seconds a metre: through the gear, and once it's gone solid (heavier,
    /// or car-to-car ringing off rough track never dies away).
    public const double Damping = 6e5;
    public const double SolidDamping = 2.5e6;

    private const double Gravity = 9.80665;
    private const double KgPerShortTon = 907.18474;
    private const double NewtonsPerPound = 4.4482216;
    private const double MphPerMetrePerSecond = 2.2369363;
    private const double FeetPerMetre = 3.2808399;
    private const int MinimumSubsteps = 10;

    private readonly double[] _mass, _inertia, _gravity, _next;
    private readonly int _substeps;

    public IReadOnlyList<TrainCar> Cars { get; }
    public AirBrakes Brakes { get; }
    public int Count { get; }

    /// Each car's speed along the rail, metres a second: positive towards the train's front.
    public double[] Velocity { get; }

    /// Each coupling's extension from centred, metres, front to rear (car i to i + 1): positive stretched (draft),
    /// negative bunched (buff).
    public double[] Extension { get; }

    /// What each coupling carries, newtons: positive pulling (draft), negative pushing (buff).
    public double[] CouplerForce { get; }

    /// Gravity along the rail on the whole train, newtons (negative holding it back).
    public double GravityForce { get; private set; }

    /// Every car's own forces this step, newtons along the rail (resistance and braking as magnitudes).
    public double[] Traction { get; }
    public double[] Resistance { get; }
    public double[] Braking { get; }
    /// What each locomotive's dynamic brake holds back with, newtons (a magnitude).
    public double[] DynamicBraking { get; }

    /// Seconds until each locomotive's engine is running: counting down once it's started, zero once it's running or
    /// while it's stopped (and always for anything without an engine).
    public double[] Starting { get; }

    /// Each locomotive's battery, breakers, engine and lights; null for anything without an engine.
    public LocoSystems?[] Systems { get; }

    /// What each locomotive's told to do this step (by the lead's controls, or a distributed power remote's radio).
    public LocoCommand[] Commands { get; }

    /// Each coupling's knuckles locked. Uncoupled, the two ends only push on each other when they meet; once they've drawn
    /// apart they couple again when they're run back together.
    public bool[] Coupled { get; }
    private readonly bool[] _readyToCouple;

    /// Each car's handbrake: wound on (HandbrakeOn), and how far it's taken up, 0 off to 1 hard on. It's wound on and off
    /// over HandbrakeTime, and holds as hard as a full service does: it slows a car moving as the air brake does, not at once.
    public bool[] HandbrakeOn { get; }
    public double[] Handbrake { get; }
    public const double HandbrakeTime = 4.0;

    /// What happened to the couplings (coupled up, a hose parted), for the crew to be told.
    public Action<string>? Happened { get; set; }

    /// How far past touching an uncoupled pair can draw apart before a hose still joined between them pulls apart.
    public const double HoseReach = 0.5;

    /// Pulling the cut lever: refused under a pull (the pin's locked under load), with why.
    public bool Uncouple(int link, out string? why)
    {
        why = null;
        if (link < 0 || link >= Coupled.Length || !Coupled[link])
            return false;
        if (CouplerForce[link] > 20000.0)
        {
            why = "The coupling's under a pull: bunch the slack to lift the pin";
            return false;
        }

        Coupled[link] = false;
        _readyToCouple[link] = false;
        return true;
    }

    /// The gap between an uncoupled pair's knuckles, metres (0 touching).
    public double Gap(int link) => link >= 0 && link < Extension.Length && !Coupled[link] ? Math.Max(0.0, Extension[link] + FreeSlack) : 0.0;

    public TrainBody(IReadOnlyList<TrainCar> cars)
    {
        Cars = cars;
        Count = cars.Count;
        Brakes = new AirBrakes(cars);
        Velocity = new double[Count];
        Extension = new double[Math.Max(Count - 1, 0)];
        CouplerForce = new double[Extension.Length];
        Traction = new double[Count];
        Resistance = new double[Count];
        Braking = new double[Count];
        DynamicBraking = new double[Count];
        Starting = new double[Count];
        Systems = new LocoSystems?[Count];
        Commands = new LocoCommand[Count];
        Coupled = new bool[Extension.Length];
        Array.Fill(Coupled, true);
        _readyToCouple = new bool[Extension.Length];
        HandbrakeOn = new bool[Count];
        Handbrake = new double[Count];
        for (var i = 0; i < Count; i++)
            if (cars[i].Def is { Kind: VehicleKind.Locomotive, Power: > 0.0 } loco)
                Systems[i] = new LocoSystems(loco.EngineSound?.StartupSeconds ?? 10.0, loco.Supplies)
                {
                    SpoolUp = loco.EngineSound?.SpoolUp ?? 8.0,
                    SpoolDown = loco.EngineSound?.SpoolDown ?? 5.0,
                    GearIdle = loco.EngineSound?.GearIdle ?? 0.08,
                    HasLowIdle = loco.EngineSound?.LowIdle is { Length: > 0 },
                };
        _mass = new double[Count];
        _inertia = new double[Count];
        _gravity = new double[Count];
        _next = new double[Count];
        WheelSlip = new double[Count];
        _slipping = new bool[Count];
        _slipCut = new double[Count];
        Array.Fill(_slipCut, 1.0);
        var lightest = double.MaxValue;
        for (var i = 0; i < Count; i++)
        {
            _mass[i] = cars[i].Mass;
            // A locomotive's traction motors and gears add about 15% to what it takes to speed it up; a car's wheelsets 5%.
            _inertia[i] = _mass[i] * (cars[i].Def.Kind == VehicleKind.Locomotive ? 1.15 : 1.05);
            lightest = Math.Min(lightest, _inertia[i]);
        }

        // Stable for the stiffest a car can be held (solid both ends) and its damping, with room to spare.
        var omega = Math.Sqrt(2.0 * SolidStiffness / Math.Max(lightest, 1000.0));
        _substeps = Math.Max(MinimumSubsteps, (int)Math.Ceiling(Train.TimeStep * omega / 0.12));
    }

    public double Mass => _mass.Sum();

    /// Sand's wanted under the locomotives' wheels (the lead's sander switch, trainlined and radioed to the remotes).
    public bool Sanding { get; set; }

    /// Each locomotive's wheels slipping, 0 gripping to 1 spinning hard, eased so it can be heard and shown.
    public double[] WheelSlip { get; private set; } = [];

    /// Each locomotive's wheels are slipping now (they've broken away and not yet got hold again), and how far its wheel
    /// slip control has cut its power back to get them to (1 none, falling as it cuts).
    private bool[] _slipping = [];
    private double[] _slipCut = [];

    /// Grip as the wheels slip rather than roll: a share of what they hold rolling. AC's control keeps the creep small.
    private const double SlidingGripDc = 0.75, SlidingGripAc = 0.90;
    /// Sand on the rail adds this much to what the wheels grip with.
    public const double SandGrip = 1.20;

    /// Adhesion falls with speed (Curtius and Kniffler), as a share of its value standing.
    public static double AdhesionAtSpeed(double metresPerSecond)
    {
        var kmh = Math.Abs(metresPerSecond) * 3.6;
        return (0.161 + 7.5 / (kmh + 44.0)) / (0.161 + 7.5 / 44.0);
    }

    /// The most a locomotive's wheels can pull with before they slip, newtons, at its speed now.
    public double AdhesionLimit(int i) => Cars[i].Def.Adhesion * AdhesionAtSpeed(Velocity[i]) * (Systems[i]?.Sanding == true ? SandGrip : 1.0) * _mass[i] * Gravity;

    /// Car i weighs this now (its load changed): its inertia goes with it, as worked out when the train was made up.
    public void SetMass(int i, double mass)
    {
        _inertia[i] = mass * (Cars[i].Def.Kind == VehicleKind.Locomotive ? 1.15 : 1.05);
        _mass[i] = mass;
    }

    /// Runs on for h seconds with each locomotive doing as Commands says. Returns how far the lead car went along the rail.
    public double Step(double h, bool powerCut)
    {
        var steps = Math.Max(_substeps, (int)Math.Ceiling(h / Brakes.MaxStep));
        var hs = h / steps;
        var lead = 0.0;

        for (var i = 0; i < Count; i++)
        {
            var g = Cars[i].Grade;
            _gravity[i] = -_mass[i] * Gravity * g / Math.Sqrt(1.0 + g * g);
        }

        GravityForce = _gravity.Sum();

        // The locomotives' own systems, their revs, and the compressors on the engines that are turning, pumping with the revs.
        var engines = 0;
        var pumping = 0.0;
        for (var i = 0; i < Count; i++)
        {
            if (Systems[i] is not { } systems)
                continue;
            systems.Update(h);
            Starting[i] = systems.SecondsToRunning;
            systems.Govern(h, Commands[i].Reverser, Commands[i].Dynamic > 0.0 ? 0 : Commands[i].Notch);
            engines++;
            pumping += systems.AirOutput;
        }

        Brakes.CompressorShare = engines > 0 ? pumping / engines : 1.0;

        // Fuel, oil and water burnt, sand put down; a locomotive weighs what it's carrying.
        for (var i = 0; i < Count; i++)
        {
            if (Systems[i] is not { } systems)
                continue;
            var def = Cars[i].Def;
            var notch = Commands[i].Dynamic > 0.0 ? 0 : Commands[i].Notch;
            var kilowatts = Math.Abs(Traction[i] * Velocity[i]) / 1000.0 + 40.0 * notch / Train.Notches;
            systems.Use(h, kilowatts, Sanding);
            if (def.Supplies is not null)
            {
                var mass = def.MassDry + systems.SuppliesMass;
                if (Math.Abs(mass - _mass[i]) > 1.0)
                    SetMass(i, mass);
            }
        }

        for (var i = 0; i < Count; i++)
        {
            var wound = HandbrakeOn[i] ? 1.0 : 0.0;
            Handbrake[i] = wound > Handbrake[i] ? Math.Min(wound, Handbrake[i] + h / HandbrakeTime) : Math.Max(wound, Handbrake[i] - h / HandbrakeTime);
        }

        for (var s = 0; s < steps; s++)
        {
            Brakes.Step(hs);
            Forces(powerCut, hs);

            for (var i = 0; i < Count; i++)
            {
                var v = Velocity[i];
                var active = Traction[i] + _gravity[i] + (i > 0 ? CouplerForce[i - 1] : 0.0) - (i < Extension.Length ? CouplerForce[i] : 0.0);
                var passive = Resistance[i] + Braking[i] + DynamicBraking[i];
                double next;
                if (Math.Abs(v) < 1e-6)
                {
                    // Standing: it only moves once what's pushing it beats everything holding it.
                    next = Math.Abs(active) <= passive ? 0.0 : (active - Math.Sign(active) * passive) / _inertia[i] * hs;
                }
                else
                {
                    next = v + (active - Math.Sign(v) * passive) / _inertia[i] * hs;
                    // What holds a car back can stop it, but never send it the other way.
                    if (Math.Sign(next) != Math.Sign(v) && Math.Abs(active) <= passive)
                        next = 0.0;
                }

                _next[i] = next;
            }

            for (var j = 0; j < Extension.Length; j++)
                Extension[j] += (_next[j] - _next[j + 1]) * hs;
            lead += _next[0] * hs;
            Array.Copy(_next, Velocity, Count);
        }

        Couplings();
        return lead;
    }

    /// Uncoupled pairs: ready to couple once they've drawn apart, and coupled when they meet again; a hose still joined
    /// across one pulls apart when they draw too far.
    private void Couplings()
    {
        for (var j = 0; j < Extension.Length; j++)
        {
            if (Coupled[j])
                continue;
            if (Extension[j] > FreeSlack + 0.3)
                _readyToCouple[j] = true;
            else if (_readyToCouple[j] && Extension[j] <= 0.0)
            {
                Coupled[j] = true;
                _readyToCouple[j] = false;
                var speed = Math.Abs(Velocity[j] - Velocity[j + 1]) * MphPerMetrePerSecond;
                Happened?.Invoke(speed > 4.0
                    ? $"Coupled {j + 1} to {j + 2} hard, at {speed:0.0} mph: keep it under 4"
                    : $"Coupled {j + 1} to {j + 2}: join the air hose and open the angle cocks to cut them in");
            }

            if (Brakes.Hose[j] && Extension[j] > FreeSlack + HoseReach)
            {
                Brakes.Hose[j] = false;
                var venting = Brakes.CockAhead[j] > 0.0 || Brakes.CockBehind[j] > 0.0;
                Happened?.Invoke(venting
                    ? $"The air hose between {j + 1} and {j + 2} pulled apart with the angle cocks open: the brakes went into emergency"
                    : $"The air hose between {j + 1} and {j + 2} pulled apart");
            }
        }
    }

    /// Car `to` takes car `from` of another body's state as it stands: its speed (negated, turned round), engine and
    /// handbrake.
    public void TakeCar(int to, TrainBody other, int from, bool turned)
    {
        Velocity[to] = turned ? -other.Velocity[from] : other.Velocity[from];
        Systems[to] = other.Systems[from];
        WheelSlip[to] = other.WheelSlip[from];
        _slipping[to] = other._slipping[from];
        _slipCut[to] = other._slipCut[from];
        SetMass(to, other._mass[from]);
        Starting[to] = other.Starting[from];
        HandbrakeOn[to] = other.HandbrakeOn[from];
        Handbrake[to] = other.Handbrake[from];
        Brakes.TakeCar(to, other.Brakes, from);
    }

    /// Coupling `to` takes coupling `from` of another body (car from to from + 1): its slack, knuckles and air hose, its
    /// cocks swapped if it's turned round.
    public void TakeLink(int to, TrainBody other, int from, bool turned)
    {
        Extension[to] = other.Extension[from];
        Coupled[to] = other.Coupled[from];
        _readyToCouple[to] = other._readyToCouple[from];
        Brakes.Hose[to] = other.Brakes.Hose[from];
        (Brakes.CockAhead[to], Brakes.CockBehind[to]) = turned
            ? (other.Brakes.CockBehind[from], other.Brakes.CockAhead[from])
            : (other.Brakes.CockAhead[from], other.Brakes.CockBehind[from]);
    }

    /// A coupling just made between two cuts run together: locked, but the hose not joined and both cocks shut (the crew
    /// joins them), its slack where the two met.
    public void Joined(int link, double extension)
    {
        Extension[link] = extension;
        Coupled[link] = true;
        _readyToCouple[link] = false;
        Brakes.Hose[link] = false;
        Brakes.CockAhead[link] = Brakes.CockBehind[link] = 0.0;
    }

    /// Stops the lead car dead (it's come up against the end of the track); the rest run in on it.
    public void HaltLead() => Velocity[0] = 0.0;

    /// Stops every car dead.
    public void Halt() => Array.Clear(Velocity);

    /// Switches every locomotive on and starts its engine, one after another a gap apart, each taking its own startup time
    /// (EngineSoundDef); none makes power until it's running and put on line.
    public void StartEngines(double gap = 3.0)
    {
        var next = 0.0;
        foreach (var systems in Systems)
        {
            if (systems is null)
                continue;
            systems.StartUp(next);
            next += gap;
        }
    }

    /// Stops every engine and switches everything off.
    public void ShutDownEngines()
    {
        foreach (var systems in Systems)
            systems?.ShutDown();
    }

    /// Every locomotive as it was left running, on line.
    public void LeaveEnginesRunning()
    {
        foreach (var systems in Systems)
            systems?.LeaveRunning();
    }

    /// How many of the locomotives are running, and of those how many are on line.
    public (int Running, int OnLine, int Engines) EngineCount =>
        (Systems.Count(s => s?.Running == true), Systems.Count(s => s?.MakesPower == true), Systems.Count(s => s is not null));

    /// The dynamic brake's grip at a speed, 0 to 1 of its rated effort: extended range, so flat from about 10 mph to
    /// 25 mph, fading out below as the motors' fields can't be built, and above held to the grids' power.
    public static double DynamicCurve(double speed)
    {
        var v = Math.Abs(speed);
        if (v < 1.0)
            return 0.0;
        return Math.Min(Math.Min(1.0, (v - 1.0) / 3.5), 11.2 / v);
    }

    private void Forces(bool powerCut, double hs)
    {
        // Couplers first: they decide the rest. An uncoupled pair only pushes, when they meet.
        for (var j = 0; j < Extension.Length; j++)
        {
            var force = Coupler(Extension[j], Velocity[j] - Velocity[j + 1]);
            CouplerForce[j] = Coupled[j] ? force : Math.Min(force, 0.0);
        }

        var mean = 0.0;
        for (var i = 0; i < Count; i++)
            mean += Velocity[i];
        var forward = mean >= 0.0;

        for (var i = 0; i < Count; i++)
        {
            var car = Cars[i];
            var v = Velocity[i];
            var def = car.Def;
            var loco = def.Kind == VehicleKind.Locomotive;

            // Traction: up to its adhesion at low speed, its power (as the notch allows) above.
            Traction[i] = 0.0;
            DynamicBraking[i] = 0.0;
            var running = Systems[i]?.MakesPower ?? Starting[i] <= 0.0;
            var (reverser, notch, dynamic) = (Commands[i].Reverser, Commands[i].Dynamic > 0.0 ? 0 : Commands[i].Notch, Commands[i].Dynamic);
            var share = (double)notch / Train.Notches;
            if (loco && running && dynamic > 0.0 && reverser != 0 && def.DynamicBrakeEffort > 0.0)
                // The traction motors as generators, their power burnt off in the grids.
                DynamicBraking[i] = Math.Clamp(dynamic, 0.0, 1.0) * def.DynamicBrakeEffort * DynamicCurve(v);
            else if (loco && running && def.Power > 0.0 && reverser != 0 && notch > 0 && !powerCut)
            {
                var pulling = Math.Max(v * reverser, 1e-3);
                Traction[i] = reverser * share * Math.Min(def.StartingTractiveEffort, def.Power / pulling);
            }

            if (loco)
                Traction[i] = Adhere(i, Traction[i], hs);

            // Rolling and curve resistance, Davis.
            var mph = Math.Abs(v) * MphPerMetrePerSecond;
            var tons = _mass[i] / KgPerShortTon;
            var perAxle = tons / Math.Max(def.Axles, 1);
            var leading = forward ? i == 0 : i == Count - 1;
            var area = loco ? 105.0 : 85.0; // frontal area, square feet
            var drag = (leading ? 0.0024 : 0.0005) * area;
            var pounds = tons * (1.3 + 29.0 / perAxle) + (loco ? 0.03 : 0.045) * tons * mph + drag * mph * mph;
            if (car.Curvature > 1e-9)
            {
                var degrees = 5729.58 / (1.0 / car.Curvature * FeetPerMetre); // degree of curve, on a 100 ft chord
                pounds += 0.8 * tons * degrees;
            }

            Resistance[i] = pounds * NewtonsPerPound;

            // Brake shoes press with a force set by the car's empty weight, so a loaded car stops slower: at a full
            // service, 10% of it on a car, 8% on a locomotive.
            Braking[i] = Math.Max(Brakes.Braking(i), Handbrake[i]) * (loco ? 0.08 : 0.10) * def.MassEmpty * Gravity;
        }
    }

    /// What a locomotive's wheels actually pull with, asked for `wanted`: all of it while they grip; once it's more than they
    /// can hold they break away and slip, pulling with less, and its wheel slip control cuts the power back until they
    /// take hold again, then lets it build back up. Sand lets them hold more.
    private double Adhere(int i, double wanted, double hs)
    {
        var def = Cars[i].Def;
        var ac = def.AcTraction;
        var asked = Math.Abs(wanted) * _slipCut[i];
        var limit = AdhesionLimit(i);
        if (!_slipping[i] && asked > limit && asked > 1000.0)
            _slipping[i] = true;
        // Once slipping they only take hold again with the pull well under what they'd grip with.
        else if (_slipping[i] && (asked < limit * (ac ? 0.97 : 0.85) || asked <= 1000.0))
            _slipping[i] = false;

        double pull, spin;
        if (_slipping[i])
        {
            pull = Math.Min(asked, limit * (ac ? SlidingGripAc : SlidingGripDc));
            spin = Math.Clamp(0.35 + (asked / limit - 1.0) * 3.0, 0.3, 1.0) * (ac ? 0.45 : 1.0);
            _slipCut[i] = Math.Max(0.2, _slipCut[i] - hs * (ac ? 3.0 : 0.9));
        }
        else
        {
            pull = asked;
            spin = 0.0;
            _slipCut[i] = Math.Min(1.0, _slipCut[i] + hs * (ac ? 0.6 : 0.18));
        }

        WheelSlip[i] += (spin - WheelSlip[i]) * Math.Min(1.0, hs * (spin > WheelSlip[i] ? 8.0 : 2.5));
        return Math.Sign(wanted) * pull;
    }

    /// What a coupling carries at an extension, opening (rate positive with it) or closing.
    public static double Coupler(double extension, double rate)
    {
        var engaged = Math.Abs(extension) - FreeSlack;
        if (engaged <= 0.0)
            return 0.0;

        var sign = Math.Sign(extension);
        var spring = engaged <= GearTravel
            ? GearStiffness * engaged
            : GearStiffness * GearTravel + SolidStiffness * (engaged - GearTravel);
        // Friction: full force taking up travel or holding still, Unloading of it as it relaxes back; eased over
        // a couple of cm/s so it doesn't chatter as it turns round.
        var holding = Math.Clamp(1.0 + rate * sign / 0.02, 0.0, 1.0);
        var damping = engaged <= GearTravel ? Damping : SolidDamping;
        var force = sign * spring * (Unloading + (1.0 - Unloading) * holding) + damping * rate;
        // A coupling in draft can only pull, in buff only push.
        return sign > 0 ? Math.Max(force, 0.0) : Math.Min(force, 0.0);
    }
}
