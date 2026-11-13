namespace Highball.Trains;

/// Where a locomotive's prime mover is in its start or stop.
public enum EngineState
{
    /// Not turning.
    Stopped,

    /// The fuel pump building pressure before it cranks.
    Priming,

    /// Turned over by the starter (the main alternator run as a motor, off the battery) until it fires and comes up to idle.
    Cranking,

    Running,

    /// Fuel cut, running down to a stop.
    Stopping,
}

public enum HeadlightSetting
{
    Off,
    Dim,
    Bright,
}

/// How a locomotive's put on the track: as it was left running, starting up (the crew's just switched it all on and pressed
/// start), or cold, with everything off.
public enum PlacedAs
{
    Running,
    StartingUp,
    ShutDown,
}

/// One locomotive's own systems, as the crew works them: its battery and the low-voltage supply off it, the breakers on the
/// electrical cabinet, the computer they power, the isolation switch, its prime mover from start to stop, and its lights.
///
/// The battery is 32 lead-acid cells, about 64 V at rest. Everything runs off it until the engine's running, when the
/// auxiliary alternator holds the supply at 74 V and charges it again. Cranking draws over a thousand amps, so the volts sag
/// and the lamps dim while the engine turns over. It's never let run flat: it gets low, but it always has enough to start.
public sealed class LocoSystems(double startupSeconds, LocoSupplies? capacity = null)
{
    // --- Supplies: fuel, lube oil and cooling water burnt and lost as it runs, and sand put down to grip. ---

    /// What it holds full; null for an engine whose file doesn't say (it never runs short).
    public LocoSupplies? Capacity { get; } = capacity;

    /// What's aboard now: litres of fuel, oil and water, cubic metres of sand.
    public double Fuel = capacity?.FuelL ?? 0.0, Oil = capacity?.OilL ?? 0.0, Water = capacity?.WaterL ?? 0.0, Sand = capacity?.SandM3 ?? 0.0;

    /// Below these shares of full the engine's protection shuts it down (and won't let it start): low lube oil pressure,
    /// and the low water detector.
    public const double LowOil = 0.10, LowWater = 0.30;

    /// Litres of fuel an hour: idling, and for each kW it puts out (about 0.2 kg a kWh, through the generator).
    private const double IdleFuelPerHour = 13.0;
    private const double FuelPerKwHour = 0.23;
    /// Lube oil burnt for each litre of fuel; cooling water lost an hour, and for each litre of fuel.
    private const double OilPerFuel = 0.003;
    private const double WaterPerHour = 0.5, WaterPerFuel = 0.001;
    /// The sanders' flow, cubic metres a second (about 0.6 cu ft a minute).
    public const double SandFlow = 0.6 * 0.0283168 / 60.0;

    /// Litres of fuel an hour it's burning now.
    public double FuelRate { get; private set; }

    /// Sand going down under its wheels now.
    public bool Sanding { get; private set; }

    /// Every tank and the sand boxes filled up.
    public void FillUp()
    {
        if (Capacity is not { } c)
            return;
        (Fuel, Oil, Water, Sand) = (c.FuelL, c.OilL, c.WaterL, c.SandM3);
        if (Notice is not null && !Turning)
            Notice = null;
    }

    /// What its supplies weigh now.
    public double SuppliesMass => LocoSupplies.MassOf(Fuel, Oil, Water, Sand);

    /// Its supplies' weight as a share of full, 0 to 1.
    public double SuppliesShare => Capacity is { FullMass: > 0.0 } c ? Math.Clamp(SuppliesMass / c.FullMass, 0.0, 1.0) : 1.0;

    public double FuelShare => Capacity is { FuelL: > 0.0 } c ? Fuel / c.FuelL : 1.0;
    public double OilShare => Capacity is { OilL: > 0.0 } c ? Oil / c.OilL : 1.0;
    public double WaterShare => Capacity is { WaterL: > 0.0 } c ? Water / c.WaterL : 1.0;
    public double SandShare => Capacity is { SandM3: > 0.0 } c ? Sand / c.SandM3 : 1.0;

    /// What's short enough to stop the engine (or keep it from starting); null if nothing is.
    private string? Short => Capacity is null ? null
        : Fuel <= 0.0 ? "Out of fuel"
        : OilShare < LowOil ? "Low lube oil: the oil pressure protection stopped the engine"
        : WaterShare < LowWater ? "Low cooling water: the low water detector stopped the engine"
        : null;

    /// Burns and loses what it does over h seconds: fuel for its output (kW, at the rail and to run itself), oil and water
    /// with it, and sand if it's asked to sand and has any (and control power to work the sanders).
    public void Use(double h, double kilowatts, bool sand)
    {
        Sanding = sand && ControlPowered && (Capacity is null || Sand > 0.0);
        if (Capacity is null)
            return;
        FuelRate = Running ? IdleFuelPerHour + FuelPerKwHour * Math.Max(kilowatts, 0.0) : Turning ? IdleFuelPerHour : 0.0;
        var fuel = FuelRate * h / 3600.0;
        Fuel = Math.Max(0.0, Fuel - fuel);
        Oil = Math.Max(0.0, Oil - fuel * OilPerFuel);
        Water = Math.Max(0.0, Water - (Turning ? WaterPerHour * h / 3600.0 : 0.0) - fuel * WaterPerFuel);
        if (Sanding)
            Sand = Math.Max(0.0, Sand - SandFlow * h);
    }

    public const double ChargingVolts = 74.0;
    private const double CellsVolts = 58.0;           // open circuit, flat; plus 8 V more when full
    private const double InternalOhms = 0.012;
    private const double CapacityAmpSeconds = 450.0 * 3600.0;
    private const double ChargeOhms = 0.08;            // how hard the alternator pushes charge back in
    private const double MaxChargeAmps = 120.0;
    private const double LowestCharge = 0.35;           // it never runs flatter than this
    private const double CrankAmps = 1400.0;
    private const double CrankSeconds = 4.5;            // the starter's in for this much of the start, then it's fired
    private const double PrimeSeconds = 3.0;
    private const double BootSeconds = 6.0;              // the computer, from control power to ready
    private const double StopSeconds = 8.0;
    private const double FlashSeconds = 15.0;            // the ditch lights flash this long after the horn

    /// The switches. Isolated is the isolation switch in Start/Stop: the engine may be started and idles, but makes no
    /// power; in Run it answers the throttle.
    public bool Battery, ControlBreaker, FuelPumpBreaker, LightsBreaker;
    public bool Isolated = true;

    public HeadlightSetting FrontHeadlight, RearHeadlight;
    public bool DitchLights;

    /// The horn's blowing (the ditch lights flash with it, and for a while after).
    public bool Horn;

    public EngineState Engine { get; private set; }

    /// 0 flat (or as flat as it's let get) to 1 full.
    public double Charge { get; private set; } = 0.92;

    /// On the low-voltage supply, and into the battery (negative coming out of it).
    public double Volts { get; private set; }
    public double BatteryAmps { get; private set; }

    /// Why it last wouldn't start, or why it stopped by itself; null once it's going again.
    public string? Notice { get; private set; }

    private double _stateTime, _boot, _flash, _flashClock, _startIn = -1.0;

    /// Started as part of starting the whole lash-up: put on line (isolation to Run) as soon as it's running.
    private bool _onLineWhenRunning;

    public bool ControlPowered => Battery && ControlBreaker;
    public bool ComputerReady => ControlPowered && _boot >= BootSeconds;
    public double BootLeft => ControlPowered ? BootSeconds - _boot : 0.0;
    public bool Running => Engine == EngineState.Running;

    // --- The prime mover's revs, and the compressor it drives. ---

    /// 0 at idle to 1 at notch 8, as the governor's got them to. On line they follow the notch, the reverser in neutral
    /// or not (with it out of neutral never below GearIdle); isolated or stopped it sits at idle.
    public double Revs { get; private set; }

    /// Seconds to wind from idle to notch 8 and back down, and the revs with the reverser out of neutral (EngineSoundDef).
    public double SpoolUp = 8.0, SpoolDown = 5.0, GearIdle = 0.08;

    /// It drops to low idle isolated (its engine sound has one), turning slower.
    public bool HasLowIdle;
    private const double LowIdleShare = 0.75;

    /// How hard it drives its compressor, against running at idle: none stopped, more revved (notch 8 turns some 2.4
    /// times idle's revs), less at low idle.
    public double AirOutput => Running ? (Isolated && HasLowIdle ? LowIdleShare : 1.0) * (1.0 + 1.4 * Revs) : 0.0;

    /// The governor: winds the revs towards what the controls ask at a steady rate, easing in at the end.
    public void Govern(double h, int reverser, int notch)
    {
        var target = MakesPower ? Math.Max(Math.Clamp(notch / (double)Train.Notches, 0.0, 1.0), reverser != 0 ? GearIdle : 0.0) : 0.0;
        var rate = target > Revs ? 1.0 / Math.Max(SpoolUp, 0.1) : 1.0 / Math.Max(SpoolDown, 0.1);
        var step = rate * h * Math.Min(1.0, 0.25 + 4.0 * Math.Abs(target - Revs));
        Revs = target > Revs ? Math.Min(target, Revs + step) : Math.Max(target, Revs - step);
    }

    /// Running and on line: it answers the throttle and the dynamic brake.
    public bool MakesPower => Running && !Isolated;

    /// Turning (or about to), so heard and pumping air once it's running.
    public bool Turning => Engine is EngineState.Priming or EngineState.Cranking or EngineState.Running;

    /// Until it's running, from the start pressed; zero when it's running or stopped.
    public double SecondsToRunning => Engine switch
    {
        EngineState.Priming => PrimeSeconds - _stateTime + startupSeconds,
        EngineState.Cranking => Math.Max(0.0, startupSeconds - _stateTime),
        _ => _startIn > 0.0 ? _startIn + PrimeSeconds + startupSeconds : 0.0,
    };

    /// Seconds left in what it's doing now (priming, cranking, stopping).
    public double StateLeft => Engine switch
    {
        EngineState.Priming => PrimeSeconds - _stateTime,
        EngineState.Cranking => startupSeconds - _stateTime,
        EngineState.Stopping => StopSeconds - _stateTime,
        _ => 0.0,
    };

    /// 1 at a stop, falling to 0 as it runs down, for its sound.
    public double RunDown => Engine == EngineState.Stopping ? Math.Clamp(1.0 - _stateTime / StopSeconds, 0.0, 1.0) : 0.0;

    /// How bright an incandescent lamp burns on the supply, 1 at 74 V (brightness goes as the volts to the 3.4).
    public double LampBrightness => Battery && LightsBreaker ? Math.Pow(Math.Clamp(Volts / ChargingVolts, 0.0, 1.1), 3.4) : 0.0;

    /// The ditch lights are at the end the headlight's lit brighter at (the front, if neither's lit).
    public bool DitchAtFront => FrontHeadlight >= RearHeadlight;

    /// A ditch light lit (at that end; side 0 left, 1 right): steady, or taking turns while they flash.
    public bool DitchLit(bool front, int side) => DitchLights && front == DitchAtFront && LampBrightness > 0.0 && (_flash <= 0.0 || (int)(_flashClock * 2.0) % 2 == side);

    public bool Flashing => DitchLights && _flash > 0.0;

    /// Whether it can start now, and if not, why.
    public bool CanStart(out string? why)
    {
        why = Engine is EngineState.Priming or EngineState.Cranking or EngineState.Running ? "It's already running"
            : Engine == EngineState.Stopping ? "Wait for it to stop first"
            : !Battery ? "The battery switch is off"
            : !ControlBreaker ? "The control breaker is off"
            : !ComputerReady ? "The computer's still starting up"
            : !FuelPumpBreaker ? "The fuel pump breaker is off"
            : !Isolated ? "Put the isolation switch in Start/Stop to start it"
            : Short;
        return why is null;
    }

    /// The engine start button: primes, then cranks. False, with Notice saying why, if it can't.
    public bool Start()
    {
        if (!CanStart(out var why))
        {
            Notice = why;
            return false;
        }

        Notice = null;
        Set(EngineState.Priming);
        return true;
    }

    /// The engine stop button: cuts the fuel and lets it run down.
    public void Stop()
    {
        _startIn = -1.0;
        _onLineWhenRunning = false;
        if (Turning)
            Set(EngineState.Stopping);
    }

    /// Everything on, the isolation switch in Start/Stop, the engine started in delay seconds, and put on line once it's
    /// running.
    public void StartUp(double delay)
    {
        Battery = ControlBreaker = FuelPumpBreaker = LightsBreaker = true;
        Isolated = true;
        _boot = BootSeconds;
        _startIn = Math.Max(delay, 0.001);
        _onLineWhenRunning = true;
    }

    /// As left running: everything on, the engine running and on line.
    public void LeaveRunning()
    {
        Battery = ControlBreaker = FuelPumpBreaker = LightsBreaker = true;
        Isolated = false;
        _boot = BootSeconds;
        Set(EngineState.Running);
        Volts = ChargingVolts;
    }

    /// Stopped, every switch off and every lamp out.
    public void ShutDown()
    {
        Stop();
        Battery = ControlBreaker = FuelPumpBreaker = LightsBreaker = DitchLights = false;
        FrontHeadlight = RearHeadlight = HeadlightSetting.Off;
        Isolated = true;
    }

    private void Set(EngineState state)
    {
        Engine = state;
        _stateTime = 0.0;
    }

    public void Update(double h)
    {
        _boot = ControlPowered ? Math.Min(BootSeconds, _boot + h) : 0.0;

        // A start asked for in a moment (each of a lash-up a little after the last).
        if (_startIn > 0.0)
        {
            _startIn -= h;
            if (_startIn <= 0.0)
            {
                _startIn = -1.0;
                Start();
            }
        }

        // Without control power or fuel it can't keep going; short of oil or water, its protection stops it.
        if (Turning && (!ComputerReady || !FuelPumpBreaker || Short is not null))
        {
            Notice = !FuelPumpBreaker ? "The fuel pump was switched off: the engine's run out of fuel"
                : !ComputerReady ? "Control power was lost: the engine shut down"
                : Short;
            Set(EngineState.Stopping);
        }

        _stateTime += h;
        if (Engine == EngineState.Priming && _stateTime >= PrimeSeconds)
            Set(EngineState.Cranking);
        else if (Engine == EngineState.Cranking && _stateTime >= startupSeconds)
        {
            Set(EngineState.Running);
            if (_onLineWhenRunning)
                Isolated = false;
            _onLineWhenRunning = false;
        }
        else if (Engine == EngineState.Stopping && _stateTime >= StopSeconds)
            Set(EngineState.Stopped);

        // What's drawn off the supply, in amps.
        var load = 0.0;
        if (ControlPowered)
            load += 30.0;
        if (Battery && FuelPumpBreaker)
            load += 12.0;
        if (Battery && LightsBreaker)
        {
            load += FrontHeadlight switch { HeadlightSetting.Bright => 9.0, HeadlightSetting.Dim => 4.0, _ => 0.0 };
            load += RearHeadlight switch { HeadlightSetting.Bright => 9.0, HeadlightSetting.Dim => 4.0, _ => 0.0 };
            if (DitchLights)
                load += _flash > 0.0 ? 4.0 : 8.0;
        }

        if (Engine == EngineState.Cranking && _stateTime < CrankSeconds)
            // The inrush, easing as it gathers speed.
            load += CrankAmps * (1.0 - 0.45 * _stateTime / CrankSeconds);

        var rest = CellsVolts + 8.0 * Charge;
        if (!Battery)
        {
            Volts = 0.0;
            BatteryAmps = 0.0;
        }
        else if (Engine == EngineState.Running)
        {
            // The auxiliary alternator carries the load and charges the battery back up.
            Volts = ChargingVolts;
            BatteryAmps = Math.Clamp((ChargingVolts - rest) / ChargeOhms, 0.0, MaxChargeAmps);
        }
        else
        {
            Volts = rest - load * InternalOhms;
            BatteryAmps = -load;
        }

        Charge = Math.Clamp(Charge + BatteryAmps * h / CapacityAmpSeconds, LowestCharge, 1.0);

        _flash = Horn ? FlashSeconds : Math.Max(0.0, _flash - h);
        _flashClock = _flash > 0.0 ? _flashClock + h : 0.0;
    }
}
