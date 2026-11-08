namespace Highball.Trains;

/// What a car's control valve is doing.
public enum ValveState
{
    /// Brake pipe above the auxiliary reservoir: the cylinder exhausts and the reservoirs charge from the pipe.
    Release,
    /// Brake pipe below the auxiliary reservoir: air goes from the reservoir into the cylinder.
    Service,
    /// Applied and holding: the reservoir has come down to the pipe, so nothing flows.
    Lap,
    /// The pipe fell fast: the valve vents its own pipe (passing it on down the train) and dumps both reservoirs into the cylinder.
    Emergency,
}

/// A brake valve away from the lead: a distributed power remote's, worked by radio from the lead. Cut in, it keeps the pipe
/// at its own car where Target says (the lead's equalizing reservoir, as last heard), and vents it in emergency.
public sealed class BrakeValve
{
    public int Car { get; init; }
    public bool CutIn { get; set; }
    public double Target { get; set; } = AirBrakes.Feed;
    public bool Emergency { get; set; }
    public double Equalizing { get; set; } = AirBrakes.Feed;

    /// Air it's putting into the pipe, cubic feet a minute (its own flow meter).
    public double Flow { get; set; }
}

/// A train's air brakes as an AAR freight train has them, worked from the lead locomotive.
///
/// Between each car and the next the pipe goes through an angle cock on each car's end and the hose joining them. Air only
/// passes with the hose connected and both cocks open (a cock part open throttles it). A cock open on an end with no hose
/// on it lets the pipe out to the air, fast enough to put the brakes in emergency: as uncoupling with the hoses still joined
/// does, when they pull apart. Distributed power remotes' brake valves (Remotes) work the pipe at their own cars.
///
/// The brake pipe runs the length of the train. The brake valve on the controlling locomotive charges it from the main
/// reservoir to what the equalizing reservoir says (Feed when released), or lets it down below that for a service
/// application. The pipe is one length of hose and pipe per car, and air flows from each to the next as a diffusion
/// (Diffusivity), so a reduction or a release takes time to reach the rear. A long train recharges slowly too, since every
/// car's reservoirs draw on the pipe as they fill. Each car's control valve compares its pipe with its auxiliary reservoir.
/// Below it, the valve feeds the reservoir into the brake cylinder; their volumes make the cylinder rise 2.5 psi for every
/// psi the pipe comes down, until the two meet at about 64 psi on a full service (26 psi off 90). Above it, the valve
/// releases. A pipe falling faster than EmergencyRate trips emergency. The valve then vents its own pipe, which passes
/// the emergency on down the train, and puts its emergency reservoir in with the auxiliary for about 77 psi.
///
/// Pressures are gauge psi, as the gauges in the cab read; volumes are cubic inches, and the air in a volume is its
/// pressure times its volume (good enough at these pressures).
public sealed class AirBrakes
{
    /// What the brake pipe's charged to, released.
    public const double Feed = 90.0;
    /// The first notch of service, and full service: equalizing reservoir reductions from Feed.
    public const double MinimumReduction = 6.0;
    public const double FullServiceReduction = 26.0;
    /// The compressor governor: cuts in below, out above.
    public const double MainCutIn = 130.0;
    public const double MainCutOut = 140.0;
    /// The independent brake's full application on the locomotives' cylinders.
    public const double IndependentFull = 72.0;
    /// A cylinder below this (its piston's return spring) puts no shoes on the wheels.
    public const double CylinderThreshold = 3.0;
    /// A full service's cylinder pressure: the braking force a car's rated for.
    public const double FullServiceCylinder = 64.0;

    private const double PipeArea = 1.5;              // 1-1/4 in pipe, square inches
    private const double InchesPerMetre = 39.3701;
    private const double AuxVolume = 2500.0;          // ABDX-type car: auxiliary and emergency reservoirs, cylinder
    private const double EmergencyVolume = 3500.0;
    private const double CylinderVolume = 1000.0;
    private const double MainVolumePerUnit = 50000.0; // each locomotive's main reservoirs
    private const double CompressorRate = 2.1;        // psi a second into its own main reservoirs, running
    private const double Diffusivity = 50000.0;       // m²/s along the pipe
    private const double Leakage = 0.0001;            // of each car's pipe air a second
    private const double EqualizingService = 3.5;     // psi a second the equalizing reservoir comes down at, service
    private const double EqualizingRelease = 15.0;
    private const double EmergencyRate = 12.0;        // psi a second falling that trips a valve into emergency...
    private const double EmergencyPickup = 0.45;      // ...held this long (its vent piston's travel): what sets how fast it runs down the train
    private const double QuickService = 1.5;          // psi a valve vents locally when it starts to apply, over QuickServiceTime
    private const double QuickServiceTime = 0.3;
    private const double VentTime = 10.0;             // seconds a valve's emergency vent stays open, before the pipe can recharge
    private const double ReleaseTime = 6.0;           // seconds for the cylinder to blow down (time constant)
    private const double SensitivityApply = 1.0;      // psi the pipe must be under the reservoir to apply, and over it to release
    private const double SensitivityRelease = 1.0;
    private const double IndependentApply = 1.5;      // time constants, seconds
    private const double IndependentRelease = 2.5;

    // How fast air goes through each path, cubic inches a second for each psi across it.
    private const double AuxCharge = 60.0;
    private const double EmergencyCharge = 45.0;
    private const double ServiceFeed = 1250.0;
    private const double RelayFeed = 8000.0;          // the brake valve's relay, charging the pipe or letting it down
    private const double EmergencyFeed = 4000.0;
    private const double CockVent = 3000.0;           // an open angle cock with nothing on it

    /// An angle cock part open passes this much of the air an open one does.
    public const double Partial = 0.2;

    private readonly double[] _volume, _link, _last, _rate, _quick, _vent, _pickup;
    private readonly bool[] _loco;
    private readonly double _mainVolume;

    public int Count { get; }

    /// Brake pipe, auxiliary reservoir, emergency reservoir and brake cylinder pressures, car by car from the front.
    public double[] Pipe { get; }
    public double[] Aux { get; }
    public double[] Reserve { get; }
    public double[] Cylinder { get; }
    public ValveState[] Valve { get; }

    /// Each coupling's air hose joined, and its two angle cocks (0 closed, 1 open): the car ahead's on its rear, the car
    /// behind's on its front. FrontCock and RearCock are the train's own ends, closed on a made-up train.
    public bool[] Hose { get; }
    public double[] CockAhead { get; }
    public double[] CockBehind { get; }
    public double FrontCock { get; set; }
    public double RearCock { get; set; }

    /// The distributed power remotes' brake valves.
    public List<BrakeValve> Remotes { get; } = [];

    /// The car whose brake valve works the train: the first locomotive, or -1 if there's none (the pipe's left to itself).
    public int Control { get; }

    public double Main { get; private set; }
    public double Equalizing { get; private set; }
    /// The independent brake's pressure on every locomotive's cylinders.
    public double Independent { get; private set; }
    public bool Compressing { get; private set; }

    /// The air the brake valve's putting into the pipe, in cubic feet of free air a minute: what the cab's flow meter shows.
    /// A train that's charged and tight shows little; a big reading released means it's still charging, or there's a leak.
    public double Flow { get; private set; }

    /// The share of the locomotives whose engines are running to drive their compressors, 0 to 1.
    public double CompressorShare { get; set; } = 1.0;

    // The controls.
    /// Automatic brake handle: 0 release, then from the first service notch (just above 0) to full service at 1.
    public double Handle { get; set; }
    public bool EmergencyHandle { get; set; }
    /// Independent brake handle, 0 release to 1 full.
    public double IndependentHandle { get; set; }

    /// The longest step the pipe stays stable at; Step splits a longer one up.
    public double MaxStep { get; }

    public AirBrakes(IReadOnlyList<TrainCar> cars)
    {
        Count = cars.Count;
        Pipe = new double[Count];
        Aux = new double[Count];
        Reserve = new double[Count];
        Cylinder = new double[Count];
        Valve = new ValveState[Count];
        _volume = new double[Count];
        _link = new double[Math.Max(Count - 1, 0)];
        _last = new double[Count];
        _rate = new double[Count];
        _quick = new double[Count];
        _vent = new double[Count];
        _pickup = new double[Count];
        _loco = new bool[Count];
        Hose = new bool[_link.Length];
        CockAhead = new double[_link.Length];
        CockBehind = new double[_link.Length];
        Array.Fill(Hose, true);
        Array.Fill(CockAhead, 1.0);
        Array.Fill(CockBehind, 1.0);

        var units = 0;
        Control = -1;
        for (var i = 0; i < Count; i++)
        {
            _loco[i] = cars[i].Def.Kind == VehicleKind.Locomotive;
            if (_loco[i])
            {
                units++;
                if (Control < 0)
                    Control = i;
            }

            _volume[i] = PipeArea * Math.Max(cars[i].Def.Length, 3.0) * InchesPerMetre;
            Pipe[i] = Aux[i] = Reserve[i] = _last[i] = Feed;
        }

        for (var j = 0; j < _link.Length; j++)
        {
            // Diffusivity D along the pipe: a link's conductance makes dP/dt = D d²P/dx² with each car one cell of the pipe.
            var between = Math.Max((cars[j].Def.Length + cars[j + 1].Def.Length) * 0.5, 3.0);
            _link[j] = Diffusivity * PipeArea * InchesPerMetre / between;
        }

        _mainVolume = Math.Max(units, 1) * MainVolumePerUnit;
        Main = units > 0 ? MainCutOut : 0.0;
        Equalizing = Feed;

        // Fastest anything empties a cell, per second: its links, and the brake valve and emergency vents at the head.
        var fastest = 8.0;
        for (var i = 0; i < Count; i++)
        {
            var links = (i > 0 ? _link[i - 1] : 0.0) + (i < _link.Length ? _link[i] : 0.0);
            fastest = Math.Max(fastest, (links + RelayFeed + EmergencyFeed) / _volume[i]);
        }

        MaxStep = 0.4 / fastest;
    }

    /// The pressure on a car's cylinder, the independent brake included on a locomotive.
    public double CylinderOf(int car) => _loco[car] ? Math.Max(Cylinder[car], Independent) : Cylinder[car];

    /// How much of its full service braking a car's getting, 0 up (a little over 1 in emergency).
    public double Braking(int car) => Math.Max(0.0, CylinderOf(car) - CylinderThreshold) / (FullServiceCylinder - CylinderThreshold);

    /// What the automatic brake handle asks the equalizing reservoir for.
    public double Target => EmergencyHandle ? 0.0 : Handle <= 0.001 ? Feed : Feed - (MinimumReduction + (FullServiceReduction - MinimumReduction) * Math.Clamp(Handle, 0.0, 1.0));

    public void Step(double h)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(h / MaxStep));
        var flow = 0.0;
        for (var s = 0; s < steps; s++)
            flow += Substep(h / steps);
        // q in psi·in³/s is free air at 14.7 psi; to cubic feet a minute.
        Flow = flow / steps / 14.7 / 1728.0 * 60.0;
    }

    private double Substep(double h)
    {
        // Compressors and their governor.
        if (Control >= 0)
        {
            if (Main < MainCutIn)
                Compressing = true;
            else if (Main >= MainCutOut)
                Compressing = false;
            if (CompressorShare <= 0.0)
                Compressing = false;
            if (Compressing)
                Main = Math.Min(MainCutOut + 0.5, Main + CompressorRate * CompressorShare * h);
        }

        // The equalizing reservoir goes to what the handle asks, at the service rate down.
        var target = Target;
        Equalizing = target < Equalizing
            ? Math.Max(target, Equalizing - (EmergencyHandle ? 200.0 : EqualizingService) * h)
            : Math.Min(target, Equalizing + EqualizingRelease * h);

        // The brake valve's relay keeps the pipe at the head where the equalizing reservoir is, charging from the main
        // reservoirs and exhausting to the air; in emergency it opens the pipe wide.
        var charged = Control >= 0 ? Relay(Control, Equalizing, EmergencyHandle, h) : 0.0;

        // The remotes' brake valves, each with its own equalizing reservoir following what it's told.
        foreach (var remote in Remotes)
        {
            if (!remote.CutIn || remote.Car < 0 || remote.Car >= Count)
            {
                remote.Flow = 0.0;
                continue;
            }

            var want = remote.Emergency ? 0.0 : remote.Target;
            remote.Equalizing = want < remote.Equalizing
                ? Math.Max(want, remote.Equalizing - (remote.Emergency ? 200.0 : EqualizingService) * h)
                : Math.Min(want, remote.Equalizing + EqualizingRelease * h);
            var q = Relay(remote.Car, remote.Equalizing, remote.Emergency, h);
            remote.Flow += (q / 14.7 / 1728.0 * 60.0 - remote.Flow) * Math.Min(1.0, h / 0.5);
        }

        // Down the pipe, car to car through the hoses and cocks, and out through its leaks.
        for (var j = 0; j < _link.Length; j++)
        {
            var open = Hose[j] ? Math.Min(CockAhead[j], CockBehind[j]) : 0.0;
            if (open <= 0.0)
                continue;
            var q = _link[j] * open * (Pipe[j] - Pipe[j + 1]) * h;
            Pipe[j] -= q / _volume[j];
            Pipe[j + 1] += q / _volume[j + 1];
        }

        // Out of any cock open with no hose on it.
        Vent(0, FrontCock, h);
        Vent(Count - 1, RearCock, h);
        for (var j = 0; j < _link.Length; j++)
        {
            if (Hose[j])
                continue;
            Vent(j, CockAhead[j], h);
            Vent(j + 1, CockBehind[j], h);
        }

        for (var i = 0; i < Count; i++)
        {
            Pipe[i] -= Pipe[i] * Leakage * h;
            Valve1(i, h);
            Pipe[i] = Math.Max(Pipe[i], 0.0);
        }

        // The independent brake, straight to the locomotives' cylinders.
        var independent = IndependentFull * Math.Clamp(IndependentHandle, 0.0, 1.0);
        Independent += (independent - Independent) * Math.Min(1.0, h / (independent > Independent ? IndependentApply : IndependentRelease));
        return charged;
    }

    /// A brake valve's relay at a car: the pipe there charged to the equalizing reservoir from the main reservoirs, or let
    /// down to it, or opened wide in emergency. Returns what it charged with (psi·in³/s).
    private double Relay(int c, double equalizing, bool emergency, double h)
    {
        var v = _volume[c];
        if (emergency)
        {
            Pipe[c] -= Math.Min(Pipe[c], EmergencyFeed / v * Pipe[c] * h);
            return 0.0;
        }

        if (equalizing > Pipe[c])
        {
            var room = Math.Max(0.0, Math.Min(equalizing, Main) - Pipe[c]);
            var charged = RelayFeed * room;
            Pipe[c] += charged / v * h;
            Main -= charged / _mainVolume * h;
            return charged;
        }

        Pipe[c] -= RelayFeed * (Pipe[c] - equalizing) / v * h;
        return 0.0;
    }

    private void Vent(int car, double open, double h)
    {
        if (open <= 0.0 || car < 0 || car >= Count)
            return;
        Pipe[car] -= Math.Min(Pipe[car], CockVent * open / _volume[car] * Pipe[car] * h);
    }

    /// Pulls a car's bleed rod: its auxiliary reservoir let down to the pipe and its cylinder exhausted, so a car with no air
    /// on it (or cut off from the brake valve) can be moved. A car charged from the pipe again releases as usual.
    public void Bleed(int car)
    {
        if (car < 0 || car >= Count)
            return;
        Aux[car] = Math.Min(Aux[car], Pipe[car]);
        Cylinder[car] = 0.0;
        Valve[car] = ValveState.Release;
        _vent[car] = 0.0;
        _pickup[car] = 0.0;
    }

    /// One car's control valve for a step.
    private void Valve1(int i, double h)
    {
        var pipe = Pipe[i];
        var raw = (pipe - _last[i]) / h;
        _last[i] = pipe;
        _rate[i] += (raw - _rate[i]) * Math.Min(1.0, h / 0.05);

        var state = Valve[i];
        _pickup[i] = _rate[i] < -EmergencyRate && pipe > 5.0 ? _pickup[i] + h : 0.0;
        if (state != ValveState.Emergency && _pickup[i] >= EmergencyPickup)
        {
            state = ValveState.Emergency;
            _vent[i] = VentTime;
        }
        else if (state == ValveState.Release && pipe < Aux[i] - SensitivityApply)
        {
            state = ValveState.Service;
            _quick[i] = QuickServiceTime;
        }
        else if (state != ValveState.Release && pipe > Aux[i] + SensitivityRelease)
            state = ValveState.Release;

        var v = _volume[i];
        switch (state)
        {
            case ValveState.Release:
            {
                // Charging: the reservoirs fill from the pipe, through their chokes; the cylinder blows down.
                if (pipe > Aux[i])
                {
                    var q = AuxCharge * (pipe - Aux[i]) * h;
                    Aux[i] += q / AuxVolume;
                    Pipe[i] -= q / v;
                }

                if (Pipe[i] > Reserve[i])
                {
                    var q = EmergencyCharge * (Pipe[i] - Reserve[i]) * h;
                    Reserve[i] += q / EmergencyVolume;
                    Pipe[i] -= q / v;
                }

                Cylinder[i] -= Cylinder[i] * Math.Min(1.0, h / ReleaseTime);
                break;
            }
            case ValveState.Service:
            case ValveState.Lap:
            {
                if (_quick[i] > 0.0)
                {
                    // Quick service: a puff of the pipe vented right here, so the reduction carries on down faster.
                    _quick[i] -= h;
                    Pipe[i] -= QuickService / QuickServiceTime * h;
                }

                // The reservoir into the cylinder until it's down to the pipe, or the two have met.
                var room = Math.Min(Aux[i] - Pipe[i], Aux[i] - Cylinder[i]);
                if (room > 0.05)
                {
                    var q = Math.Min(ServiceFeed * (Aux[i] - Pipe[i]) * h, room / (1.0 / AuxVolume + 1.0 / CylinderVolume));
                    Aux[i] -= q / AuxVolume;
                    Cylinder[i] += q / CylinderVolume;
                    state = ValveState.Service;
                }
                else
                    state = ValveState.Lap;

                break;
            }
            case ValveState.Emergency:
            {
                // The pipe vented here until the vent valve closes, and both reservoirs into the cylinder. It stays applied
                // until the pipe's recharged over the auxiliary reservoir.
                if (_vent[i] > 0.0)
                {
                    _vent[i] -= h;
                    Pipe[i] -= Math.Min(Pipe[i], EmergencyFeed / v * Pipe[i] * h);
                }

                Dump(ref Aux[i], AuxVolume, ref Cylinder[i], h);
                Dump(ref Reserve[i], EmergencyVolume, ref Cylinder[i], h);
                break;
            }
        }

        Valve[i] = state;
    }

    /// Emergency: a reservoir straight into the cylinder until they're level.
    private static void Dump(ref double from, double size, ref double cylinder, double h)
    {
        if (from <= cylinder)
            return;
        var q = Math.Min(EmergencyFeed * (from - cylinder) * h, (from - cylinder) / (1.0 / size + 1.0 / CylinderVolume));
        from -= q / size;
        cylinder += q / CylinderVolume;
    }
}
