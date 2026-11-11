namespace Highball.Trains;

/// Where a distributed power remote stands with the lead.
public enum DpLink
{
    /// Not set up as a remote: it takes no power and its brake valve's cut out.
    NotSetUp,

    /// Set up as a remote, waiting for the lead to link it.
    SetUp,

    /// The lead's asked it to link and is waiting for its answer.
    Linking,

    Linked,
}

/// Synchronous: every remote does what the lead does. Independent: the remotes take their own throttle or dynamic brake
/// (the "fence" moved to the lead's side of them), the brakes still as the lead's.
public enum DpMode
{
    Synchronous,
    Independent,
}

/// How the radio path between two antennas lies over the ground.
public enum Sight
{
    Clear,

    /// The line's clear, but the ground's inside the radio's Fresnel zone round it: some signal lost.
    Grazing,

    /// The ground's across the line itself: only what bends over the top gets through.
    Blocked,
}

/// Distributed power (Locotrol-style): locomotives spread through the train, worked from the lead by radio.
///
/// Locomotives coupled together are one consist, joined by MU cables: the units behind each consist's first answer it as
/// it is told. The consist with the train's first locomotive is the lead; every other is a remote, once it's been set up
/// as one (on its own lead unit) and the lead has linked it. The lead sends its controls as a radio message whenever they
/// change and once a second besides; each remote does as the last message it heard said, and answers with its own state.
///
/// The radio is a 450 MHz data link. A message takes a few tenths of a second, and gets through or not as the path allows:
/// well within a few kilometres in the clear, less and less beyond, and badly where the ground comes between the two
/// antennas (worked out over the terrain, with the earth's curve). A remote that hears nothing for 45 seconds takes it
/// the link's lost: it drops to idle and cuts out its brake valve, leaving the brakes to the lead through the pipe.
public sealed class DistributedPower
{
    public sealed class Group
    {
        public required int Head { get; init; }
        public required int[] Units { get; init; }
    }

    public readonly record struct Command(int Reverser, int Notch, double Dynamic, double BrakeTarget, bool Emergency);

    internal enum Kind
    {
        Command,
        Status,
        LinkRequest,
        LinkAnswer,
        Unlink,
    }

    internal readonly record struct Message(Kind Kind, Command Command, double SentAt, double Pipe);

    /// One remote consist and the radio path to it.
    public sealed class Remote
    {
        public required Group Group { get; init; }
        public required BrakeValve Valve { get; init; }

        public DpLink Link { get; internal set; } = DpLink.NotSetUp;

        /// Set up as a remote, on its own lead unit's controls (or not).
        public bool SetUp
        {
            get => Link != DpLink.NotSetUp;
            set
            {
                if (value && Link == DpLink.NotSetUp)
                    Link = DpLink.SetUp;
                else if (!value)
                    Link = DpLink.NotSetUp;
            }
        }

        /// The path to it, as last worked out: how far, how clear, and the share of messages getting through (0 to 1).
        public double Distance { get; internal set; }
        public Sight Sight { get; internal set; }
        public double Signal { get; internal set; } = 1.0;

        /// Seconds from the lead's message going to the remote's answer coming back, last time one did.
        public double RoundTrip { get; internal set; }

        /// What it's doing, as it last heard; and the brake pipe at it, as the lead last heard.
        public Command Applied { get; internal set; }
        public double PipeHeard { get; internal set; } = AirBrakes.Feed;

        /// When the lead last heard from it, and when it last heard from the lead (the train's clock).
        public double LastHeard { get; internal set; } = double.NegativeInfinity;
        public double LastCommand { get; internal set; } = double.NegativeInfinity;

        /// It's heard nothing for so long it's taken the link as lost, gone to idle and cut its brake valve out.
        public bool CommLost { get; internal set; }

        internal double LinkStarted, LinkAskedAt;
        internal readonly List<(double At, Message Message)> ToRemote = [], ToLead = [];
    }

    public const double CommandEvery = 1.0;
    public const double InterruptedAfter = 4.0;
    public const double LostAfter = 45.0;
    private const double AntennaHeight = 4.9;
    private const double Wavelength = 0.66;           // 450 MHz
    private const double ClearRange = 3500.0;         // metres it gets through near all the time in the clear...
    private const double FullRange = 14000.0;         // ...out to where it's lost altogether
    private const double EarthRadius = 6371000.0 * 4.0 / 3.0; // radio bends a little over the horizon
    private const double LinkTimeout = 20.0;
    private const double PathEvery = 0.25;

    private readonly Train _train;
    private readonly Random _random = new(4401);
    private Command _sent;
    private double _sentAt = double.NegativeInfinity;
    private double _pathAt = double.NegativeInfinity;

    public Group? Lead { get; }
    public IReadOnlyList<Remote> Remotes { get; }

    public DpMode Mode { get; set; }

    /// The remotes' own throttle notch and dynamic brake, in Independent.
    public int RemoteNotch { get; set; }
    public double RemoteDynamic { get; set; }

    /// The ground's height at a place on the plan (x, z), for the radio's line of sight; null takes it all as open.
    public Func<double, double, double>? Ground { get; set; }

    /// Something worth telling the crew (a link made or failed).
    public Action<string>? Happened { get; set; }

    public DistributedPower(Train train)
    {
        _train = train;
        var groups = new List<Group>();
        var systems = train.Body.Systems;
        for (var i = 0; i < systems.Length; i++)
        {
            if (systems[i] is null)
                continue;
            var units = new List<int> { i };
            while (i + 1 < systems.Length && systems[i + 1] is not null)
                units.Add(++i);
            groups.Add(new Group { Head = units[0], Units = [.. units] });
        }

        Lead = groups.Count > 0 ? groups[0] : null;
        var remotes = new List<Remote>();
        foreach (var group in groups.Skip(1))
        {
            var valve = new BrakeValve { Car = group.Head };
            train.Body.Brakes.Remotes.Add(valve);
            remotes.Add(new Remote { Group = group, Valve = valve });
        }

        Remotes = remotes;
    }

    /// Which consist a car's in, as its role: the lead's, a remote's, or none.
    public (Group? Group, Remote? Remote) GroupOf(int car)
    {
        if (Lead is { } lead && lead.Units.Contains(car))
            return (lead, null);
        foreach (var remote in Remotes)
            if (remote.Group.Units.Contains(car))
                return (remote.Group, remote);
        return (null, null);
    }

    private LocoSystems? SystemsOf(int car) => _train.Body.Systems[car];

    /// The DP computer on a unit: up when its control power's on and booted.
    private bool Computer(int car) => SystemsOf(car)?.ComputerReady ?? false;

    /// Every remote set up and linked at once, as a train made up and tested before it's handed over.
    public void LinkAllNow()
    {
        foreach (var remote in Remotes)
        {
            remote.Link = DpLink.Linked;
            remote.LastHeard = remote.LastCommand = _train.Time;
            remote.CommLost = false;
        }

        _sentAt = double.NegativeInfinity;
    }

    /// The lead's link button: asks every set-up remote to link.
    public void LinkAll()
    {
        if (Lead is null)
            return;
        if (!Computer(Lead.Head))
        {
            Happened?.Invoke("The lead's DP computer isn't up: its control power needs to be on");
            return;
        }

        foreach (var remote in Remotes.Where(r => r.Link == DpLink.SetUp))
        {
            remote.Link = DpLink.Linking;
            remote.LinkStarted = _train.Time;
            remote.LinkAskedAt = double.NegativeInfinity;
        }
    }

    public void UnlinkAll()
    {
        foreach (var remote in Remotes.Where(r => r.Link is DpLink.Linked or DpLink.Linking))
        {
            Send(remote.ToRemote, remote, new Message(Kind.Unlink, default, _train.Time, 0.0));
            remote.Link = DpLink.SetUp;
            remote.CommLost = false;
        }
    }

    /// Comm as the lead sees it: OK, interrupted (nothing heard for a few seconds), or lost.
    public static string CommText(Remote remote, double now)
    {
        if (remote.Link != DpLink.Linked)
            return "-";
        var quiet = now - remote.LastHeard;
        return quiet < InterruptedAfter ? "OK" : quiet < LostAfter ? $"interrupted {quiet:0} s" : "lost";
    }

    /// Runs the radio on to now, and sets each locomotive's command and each remote's brake valve for the next step.
    public void Update(double now)
    {
        var lead = Lead;
        var commands = _train.Body.Commands;
        Array.Clear(commands);
        if (lead is null)
            return;

        var train = _train;
        var brakes = train.Body.Brakes;
        var command = new Command(train.Reverser, train.Dynamic > 0.0 ? 0 : train.Notch, train.Dynamic, brakes.Target, train.Emergency);
        foreach (var unit in lead.Units)
            commands[unit] = new LocoCommand(command.Reverser, command.Notch, command.Dynamic);

        if (Remotes.Count == 0)
            return;

        if (now - _pathAt >= PathEvery)
        {
            _pathAt = now;
            foreach (var remote in Remotes)
                Path(remote, lead.Head);
        }

        // What the remotes are to do: the lead's, or in Independent their own power with the lead's brakes.
        var forRemotes = Mode == DpMode.Independent
            ? command with { Notch = RemoteDynamic > 0.0 ? 0 : RemoteNotch, Dynamic = RemoteDynamic }
            : command;
        var leadUp = Computer(lead.Head);
        var due = leadUp && (forRemotes != _sent || now - _sentAt >= CommandEvery);
        if (due)
        {
            _sent = forRemotes;
            _sentAt = now;
        }

        foreach (var remote in Remotes)
        {
            var remoteUp = Computer(remote.Group.Head);
            if (due && remote.Link == DpLink.Linked)
                Send(remote.ToRemote, remote, new Message(Kind.Command, forRemotes, now, 0.0));
            if (leadUp && remote.Link == DpLink.Linking && now - remote.LinkAskedAt >= 2.0)
            {
                remote.LinkAskedAt = now;
                Send(remote.ToRemote, remote, new Message(Kind.LinkRequest, forRemotes, now, 0.0));
            }

            if (remote.Link == DpLink.Linking && now - remote.LinkStarted > LinkTimeout)
            {
                remote.Link = DpLink.SetUp;
                Happened?.Invoke($"DP link to car {remote.Group.Head + 1} failed: no answer ({SightText(remote)}, {remote.Signal * 100.0:0}% signal)");
            }

            // What's arrived at the remote, and its answers.
            for (var k = 0; k < remote.ToRemote.Count; k++)
            {
                var (at, message) = remote.ToRemote[k];
                if (at > now)
                    continue;
                remote.ToRemote.RemoveAt(k--);
                if (!remoteUp)
                    continue;
                switch (message.Kind)
                {
                    case Kind.Command when remote.Link == DpLink.Linked:
                        remote.Applied = message.Command;
                        remote.LastCommand = now;
                        remote.CommLost = false;
                        Send(remote.ToLead, remote, new Message(Kind.Status, message.Command, message.SentAt, brakes.Pipe[remote.Group.Head]));
                        break;
                    case Kind.LinkRequest when remote.Link is DpLink.Linking or DpLink.SetUp or DpLink.Linked:
                        remote.Applied = message.Command;
                        remote.LastCommand = now;
                        Send(remote.ToLead, remote, new Message(Kind.LinkAnswer, message.Command, message.SentAt, brakes.Pipe[remote.Group.Head]));
                        break;
                }
            }

            // What's arrived back at the lead.
            for (var k = 0; k < remote.ToLead.Count; k++)
            {
                var (at, message) = remote.ToLead[k];
                if (at > now)
                    continue;
                remote.ToLead.RemoveAt(k--);
                if (!leadUp)
                    continue;
                remote.LastHeard = now;
                remote.RoundTrip = now - message.SentAt;
                remote.PipeHeard = message.Pipe;
                if (message.Kind == Kind.LinkAnswer && remote.Link == DpLink.Linking)
                {
                    remote.Link = DpLink.Linked;
                    remote.LastCommand = now;
                    Happened?.Invoke($"DP remote at car {remote.Group.Head + 1} linked ({remote.RoundTrip:0.0} s round trip)");
                }
            }

            // Too long without a word: it idles and cuts its brake valve out.
            if (remote.Link == DpLink.Linked && !remote.CommLost && now - remote.LastCommand > LostAfter)
            {
                remote.CommLost = true;
                Happened?.Invoke($"DP comm with car {remote.Group.Head + 1} lost: it's gone to idle and cut out its brake valve");
            }

            var working = remote.Link == DpLink.Linked && !remote.CommLost && remoteUp;
            var applied = remote.Applied;
            foreach (var unit in remote.Group.Units)
                commands[unit] = working ? new LocoCommand(applied.Reverser, applied.Notch, applied.Dynamic) : default;
            remote.Valve.CutIn = working;
            remote.Valve.Target = applied.BrakeTarget;
            remote.Valve.Emergency = working && applied.Emergency;
        }
    }

    /// A message on its way, if it gets through: a few tenths of a second, longer on a weak path (it's sent again).
    private void Send(List<(double At, Message Message)> queue, Remote remote, Message message)
    {
        if (_random.NextDouble() > remote.Signal)
            return;
        var delay = 0.25 + 0.25 * _random.NextDouble() + (1.0 - remote.Signal) * 0.8 * _random.NextDouble();
        queue.Add((_train.Time + delay, message));
    }

    public static string SightText(Remote remote) => remote.Sight switch
    {
        Sight.Clear => "line of sight clear",
        Sight.Grazing => "ground close under the path",
        _ => "terrain in the way",
    };

    /// Antenna to antenna, over the ground: the distance, the line of sight, and what share of messages get through.
    private void Path(Remote remote, int leadCar)
    {
        var a = Antenna(leadCar);
        var b = Antenna(remote.Group.Head);
        var (dx, dy, dz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var d = Math.Sqrt(dx * dx + dz * dz);
        remote.Distance = Math.Sqrt(d * d + dy * dy);

        // The worst clearance along the way, as a share of the first Fresnel zone's radius there.
        var worst = double.PositiveInfinity;
        if (Ground is { } ground && d > 1.0)
        {
            var samples = Math.Clamp((int)(d / 20.0), 8, 96);
            for (var k = 1; k < samples; k++)
            {
                var t = (double)k / samples;
                var (x, y, z) = (a.X + dx * t, a.Y + dy * t, a.Z + dz * t);
                var (d1, d2) = (d * t, d * (1.0 - t));
                var under = ground(x, z) + d1 * d2 / (2.0 * EarthRadius);
                var zone = Math.Sqrt(Wavelength * d1 * d2 / d);
                worst = Math.Min(worst, (y - under) / Math.Max(zone, 0.5));
            }
        }

        remote.Sight = worst >= 0.6 ? Sight.Clear : worst >= 0.0 ? Sight.Grazing : Sight.Blocked;
        var range = Math.Clamp(1.0 - (remote.Distance - ClearRange) / (FullRange - ClearRange), 0.0, 1.0);
        var obstruction = remote.Sight switch
        {
            Sight.Clear => 1.0,
            Sight.Grazing => 0.6 + 0.4 * worst / 0.6,
            // Over a ridge, by diffraction: worse the deeper it's cut.
            _ => Math.Clamp(0.3 + 0.15 * worst, 0.02, 0.3),
        };
        remote.Signal = Math.Clamp(range * obstruction, 0.0, 0.99);
    }

    private (double X, double Y, double Z) Antenna(int car)
    {
        var c = _train.Cars[car];
        return ((c.FrontAt.X + c.RearAt.X) * 0.5, (c.FrontAt.Y + c.RearAt.Y) * 0.5 + AntennaHeight, (c.FrontAt.Z + c.RearAt.Z) * 0.5);
    }
}
