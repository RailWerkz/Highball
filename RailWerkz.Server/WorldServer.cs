using System.Diagnostics;
using System.Text.Json;
using Highball.Trains;
using RailWerkz.Core.Logging;
using RailWerkz.Network;

namespace RailWerkz.Server;

/// A world or region's server. It loads only the server track (RailGraphFile: the rail graph with heights, switches and
/// signals; no terrain), and keeps what every player shares: how each switch is set, and every cut on the track with
/// its latest state, so a player joining late sees everything as it stands.
///
/// A cut is run either by its player's game (the server passes its states on) or by the server itself: those it puts on
/// its own Railway and steps at the simulation's rate (Train.TimeStep), driven by their players' Control messages, and
/// sends every player their state. Switches a server-run train forces over by trailing through are set for everyone.
/// The same class runs headless (Program) or on a thread inside the game of a player who hosts.
public sealed class WorldServer : IDisposable
{
    private sealed class Player
    {
        public required int Number;
        public required string Name;
        public bool Ready;
    }

    private sealed class SharedCut
    {
        public required ulong Id;
        public required PeerId Owner;
        public required byte[] Added;
        public byte[]? State;

        /// Run here (CutAdded's flag): its consist, and once its first state has placed it, its cut on the Railway.
        public bool ServerRuns;
        public Consist? Consist;
        public Railway.Cut? Run;

        /// The player number of whoever drives it (0: nobody).
        public int Driver;
    }

    private readonly Session _session = new();
    private readonly Dictionary<PeerId, Player> _players = [];
    private readonly SwitchStates _switches = new();
    private readonly Dictionary<ulong, SharedCut> _cuts = [];
    private readonly Dictionary<Railway.Cut, SharedCut> _run = [];
    /// Cuts the Railway made this step from ones run here (a couple, a split), and the cut each came from.
    private readonly Dictionary<Railway.Cut, SharedCut> _heirs = [];
    private uint _nextServerCut = 1;
    private double _gaugesAt;
    private readonly Railway _railway = new();
    private readonly List<Session.Event> _events = [];
    private readonly NetWriter _writer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _simulated;
    private double _sentAt;
    private bool _applyingSwitch;
    private int _nextNumber = 1;
    private volatile bool _stop;

    public RailGraphFile.Contents Track { get; }
    public string World { get; }
    public string Region { get; }
    public int Port { get; }
    public int Checksum { get; }

    public int PlayerCount => _players.Count;

    /// How many cuts the server itself runs.
    public int RunCount => _run.Count;

    public WorldServer(string serverFile, string world, int port)
    {
        Track = RailGraphFile.Read(serverFile);
        World = world;
        Region = Track.Name;
        Port = port;
        Checksum = Track.Graph.Checksum();

        Track.Graph.Reversed = _switches.Reversed;
        _railway.Use(Track.Graph);
        _railway.Switches = _switches;
        _switches.Changed += SwitchChanged;
        _railway.Removed += cut => Lost(cut, "taken off");
        _railway.Replaced += (old, now) =>
        {
            if (_run.TryGetValue(old, out var shared))
                _heirs.TryAdd(now, shared);
            Lost(old, "coupled or split");
        };

        if (!_session.Host(port, 64, out var error))
            throw new InvalidOperationException($"couldn't host on port {port}: {error}");
        Log.Info($"Server: {world}/{Region} on port {port}: {Track.Graph.Edges.Count} edges, {Track.Graph.Switches.Count} switches, " +
                 $"{Track.Graph.TotalLength / 1000.0:0.00} km, {Track.Signals.Count} signals, checksum {Checksum:X8}");
    }

    /// Runs until Stop, on whichever thread calls it.
    public void Run()
    {
        while (!_stop)
        {
            Poll();
            Simulate();
            Thread.Sleep(2);
        }
    }

    public void Stop() => _stop = true;

    /// Steps the trains run here up to now, in the simulation's fixed steps (at most a quarter of a second at once, so a stall
    /// doesn't make it race), and sends their states every Protocol.StateEvery.
    public void Simulate()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _simulated > 0.25)
            _simulated = now - 0.25;
        while (_simulated + Train.TimeStep <= now)
        {
            // Driven cuts stay awake while there's power or brake to answer.
            foreach (var cut in _run.Keys)
                if (cut.Train.Notch > 0 || cut.Train.Dynamic > 0.0)
                    cut.Still = 0.0;
            _railway.Step(Train.TimeStep);
            _simulated += Train.TimeStep;
            Adopt();
        }

        if (now - _gaugesAt >= 0.1)
        {
            _gaugesAt = now;
            foreach (var (cut, shared) in _run)
                if (shared.Driver != 0 && PeerOf(shared.Driver) is { } driver)
                    _session.Send(driver, WriteGauges(shared.Id, cut.Train), Delivery.Unreliable);
        }

        if (now - _sentAt < Protocol.StateEvery || _run.Count == 0)
            return;
        _sentAt = now;
        foreach (var (cut, shared) in _run)
        {
            var state = WriteState(_writer, shared.Id, cut.Train).ToArray();
            shared.State = state;
            foreach (var (peer, player) in _players)
                if (player.Ready)
                    _session.Send(peer, state, Delivery.Unreliable);
        }
    }

    /// Cuts on the Railway that nobody's told of: what a couple or split of cuts run here made. Each is run here under a new id,
    /// driven by whoever drove the cut it came from (the half of a split that kept the controls; the other, nobody), and every
    /// player's told.
    private void Adopt()
    {
        foreach (var cut in _railway.Cuts)
        {
            if (cut.Remote || _run.ContainsKey(cut))
                continue;
            var driver = _heirs.TryGetValue(cut, out var from) && cut.Train.LeadUnit >= 0 ? from.Driver : 0;
            var id = ((ulong)(uint)driver << 32) | (Protocol.ServerCut | _nextServerCut++);
            var consist = new Consist
            {
                Name = cut.Train.Name,
                Cars = [.. cut.Train.Cars.Select(c => new ConsistCar(c.Def.Id, c.Livery?.Id, c.Reversed, c.Fill > 0.0, c.Tag, c.Fill, c.Cargo))],
            };
            var added = _writer.Begin(Protocol.Id(Protocol.CutAdded)).U64(id).LongStr(JsonSerializer.Serialize(consist)).Bool(true).I32(driver).Span.ToArray();
            var shared = new SharedCut
            {
                Id = id, Owner = PeerOf(driver) ?? default, Added = added, ServerRuns = true, Consist = consist, Run = cut, Driver = driver,
            };
            _cuts[id] = shared;
            _run[cut] = shared;
            foreach (var (peer, player) in _players)
                if (player.Ready)
                    _session.Send(peer, added, Delivery.Reliable);
            Log.Info($"Server: {consist.Name} ({cut.Train.Cars.Count} vehicles) run here as {id:X}, driven by {(driver == 0 ? "nobody" : $"player {driver}")}");
        }

        _heirs.Clear();
    }

    private PeerId? PeerOf(int number)
    {
        foreach (var (peer, player) in _players)
            if (player.Number == number)
                return peer;
        return null;
    }

    /// A cut's brakes as the cab shows them (Gauges).
    private byte[] WriteGauges(ulong id, Train train)
    {
        var brakes = train.Body.Brakes;
        var w = _writer.Begin(Protocol.Id(Protocol.Gauges)).U64(id).F32((float)brakes.Main).F32((float)brakes.Equalizing).F32((float)brakes.Independent)
            .U16((ushort)brakes.Count);
        for (var i = 0; i < brakes.Count; i++)
            w.F32((float)brakes.Pipe[i]);
        for (var i = 0; i < brakes.Count; i++)
            w.F32((float)brakes.Cylinder[i]);
        return w.Span.ToArray();
    }

    /// A cut's state as CutState carries it: its front, each coupler's extension and each car's speed.
    public static ReadOnlySpan<byte> WriteState(NetWriter writer, ulong id, Train train)
    {
        var w = writer.Begin(Protocol.Id(Protocol.CutState)).U64(id).I32(train.Head.Edge).F32((float)train.Head.S).I32(train.Head.Direction);
        var body = train.Body;
        w.U16((ushort)body.Velocity.Length);
        for (var i = 0; i < body.Velocity.Length - 1; i++)
            w.F32((float)(i < body.Extension.Length ? body.Extension[i] : 0.0));
        for (var i = 0; i < body.Velocity.Length; i++)
            w.F32((float)body.Velocity[i]);
        return w.Span;
    }

    public void Poll()
    {
        _events.Clear();
        _session.Poll(_events);
        foreach (var e in _events)
        {
            switch (e.Kind)
            {
                case Session.EventKind.Connected:
                    Log.Info($"Server: {e.Peer} connected");
                    break;
                case Session.EventKind.Disconnected:
                case Session.EventKind.Failed:
                    Left(e.Peer, e.Reason);
                    break;
                case Session.EventKind.Message when e.Data is { } data:
                    try
                    {
                        Handle(e.Peer, data);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Server: bad message from {e.Peer} ({ex.Message})");
                    }

                    break;
            }
        }
    }

    private void Handle(PeerId from, byte[] data)
    {
        var r = new NetReader(data);
        var id = r.MessageId;
        if (id == Protocol.Hello)
        {
            var name = r.Str();
            var version = r.I32();
            if (version != Protocol.Version)
            {
                Refuse(from, $"The server runs version {Protocol.Version} of the game's protocol, you {version}");
                return;
            }

            var player = new Player { Number = _nextNumber++, Name = name };
            _players[from] = player;
            _session.Send(from, _writer.Begin(Protocol.Id(Protocol.Welcome)).I32(player.Number).Str(World).Str(Region).I32(Track.Zone).I32(Checksum).Span, Delivery.Reliable);
            Log.Info($"Server: {name} joined as player {player.Number}");
            return;
        }

        if (!_players.TryGetValue(from, out var who))
            return;

        switch (id)
        {
            case Protocol.Ready:
            {
                var theirs = r.I32();
                if (theirs != Checksum)
                {
                    Refuse(from, "Your copy of this world's track isn't the server's: install the same version of its pack");
                    return;
                }

                who.Ready = true;
                foreach (var (node, reversed) in _switches.All)
                    _session.Send(from, _writer.Begin(Protocol.Id(Protocol.Switch)).I64(node).Bool(reversed).Span, Delivery.Reliable);
                foreach (var cut in _cuts.Values)
                {
                    _session.Send(from, cut.Added, Delivery.Reliable);
                    if (cut.State is { } state)
                        _session.Send(from, state, Delivery.Reliable);
                }

                Log.Info($"Server: {who.Name} ready, sent {_switches.All.Count()} switches and {_cuts.Count} cuts");
                break;
            }
            case Protocol.CutAdded:
            {
                var cut = r.U64();
                if (Protocol.Owner(cut) != who.Number)
                    return;
                var json = r.LongStr();
                var serverRuns = r.Bool();
                if (r.Overrun)
                    serverRuns = false;
                var shared = new SharedCut { Id = cut, Owner = from, Added = data, ServerRuns = serverRuns, Driver = who.Number };
                if (serverRuns)
                    shared.Consist = JsonSerializer.Deserialize<Consist>(json);
                _cuts[cut] = shared;
                Pass(from, data, Delivery.Reliable);
                Log.Info($"Server: {who.Name} put cut {cut:X} on the track{(serverRuns ? ", run here" : "")}");
                break;
            }
            case Protocol.CutState:
            {
                var cut = r.U64();
                if (!_cuts.TryGetValue(cut, out var shared) || shared.Owner != from)
                    return;
                if (shared.ServerRuns)
                {
                    // Its first state places it; from then on it's run here and its player's states are ignored.
                    if (shared.Run is null)
                        Place(shared, r, who);
                    return;
                }

                shared.State = data;
                Pass(from, data, Delivery.Unreliable);
                break;
            }
            case Protocol.Control:
            {
                var cut = r.U64();
                if (!_cuts.TryGetValue(cut, out var shared) || shared.Owner != from || shared.Run is not { } run)
                    return;
                var train = run.Train;
                train.Reverser = Math.Clamp(r.U8() - 1, -1, 1);
                train.Notch = Math.Clamp((int)r.U8(), 0, Train.Notches);
                train.Brake = Math.Clamp(r.F32(), 0f, 1f);
                train.Independent = Math.Clamp(r.F32(), 0f, 1f);
                // Sent as the handle's share of full (notch / Notches), so whole notches only.
                train.DynamicNotch = (int)MathF.Round(Math.Clamp(r.F32(), 0f, 1f) * Train.Notches);
                train.Emergency = r.Bool();
                train.Sand = r.Bool();
                if (r.Overrun)
                    return;
                run.Still = 0.0;
                break;
            }
            case Protocol.CutRemoved:
            {
                var cut = r.U64();
                if (_cuts.TryGetValue(cut, out var shared) && shared.Owner == from)
                {
                    _cuts.Remove(cut);
                    if (shared.Run is { } run)
                    {
                        _run.Remove(run);
                        _railway.Remove(run);
                    }

                    Pass(from, data, Delivery.Reliable);
                    Log.Info($"Server: {who.Name} took cut {cut:X} off");
                }

                break;
            }
            case Protocol.Switch:
            {
                var node = r.I64();
                var reversed = r.Bool();
                _applyingSwitch = true;
                _switches.Set(node, reversed);
                _applyingSwitch = false;
                Pass(from, data, Delivery.Reliable);
                break;
            }
        }
    }

    /// A server-run cut's first state: its train put where it says, moving as it says, on the Railway.
    private void Place(SharedCut shared, NetReader r, Player who)
    {
        var head = new TrackPosition(r.I32(), r.F32(), r.I32());
        var cars = r.U16();
        var extension = new float[Math.Max(0, cars - 1)];
        for (var i = 0; i < extension.Length; i++)
            extension[i] = r.F32();
        var velocity = new float[cars];
        for (var i = 0; i < cars; i++)
            velocity[i] = r.F32();
        if (r.Overrun || head.Edge < 0 || head.Edge >= Track.Graph.Edges.Count || shared.Consist is null)
            return;

        var train = Train.Place(Track.Graph, shared.Consist, head, out var why, PlacedAs.Running);
        if (train is null)
        {
            Log.Warn($"Server: couldn't run {who.Name}'s {shared.Consist.Name} ({why})");
            return;
        }

        if (train.Cars.Count == cars)
            train.Mirror(head, extension, velocity);
        var cut = _railway.Add(train, null, out why);
        if (cut is null)
        {
            Log.Warn($"Server: couldn't run {who.Name}'s {shared.Consist.Name} ({why})");
            return;
        }

        shared.Run = cut;
        _run[cut] = shared;
        Log.Info($"Server: running {shared.Consist.Name} ({train.Cars.Count} vehicles) for {who.Name} on edge {head.Edge}");
    }

    /// A cut run here gone from the Railway on its own (hit, coupled, drawn apart): taken off for everyone.
    private void Lost(Railway.Cut cut, string how)
    {
        if (!_run.Remove(cut, out var shared))
            return;
        shared.Run = null;
        _cuts.Remove(shared.Id);
        var message = _writer.Begin(Protocol.Id(Protocol.CutRemoved)).U64(shared.Id).Span.ToArray();
        foreach (var (peer, player) in _players)
            if (player.Ready)
                _session.Send(peer, message, Delivery.Reliable);
        Log.Info($"Server: {shared.Consist?.Name} {how}, taken off");
    }

    /// A switch a train run here forced over: set for every player.
    private void SwitchChanged(long node, bool reversed)
    {
        if (_applyingSwitch)
            return;
        var message = _writer.Begin(Protocol.Id(Protocol.Switch)).I64(node).Bool(reversed).Span.ToArray();
        foreach (var (peer, player) in _players)
            if (player.Ready)
                _session.Send(peer, message, Delivery.Reliable);
    }

    /// A message from one player passed on to every other player who's ready.
    private void Pass(PeerId from, byte[] data, Delivery delivery)
    {
        foreach (var (peer, player) in _players)
            if (peer != from && player.Ready)
                _session.Send(peer, data, delivery);
    }

    private void Refuse(PeerId to, string why)
    {
        _session.Send(to, _writer.Begin(Protocol.Id(Protocol.Refused)).Str(why).Span, Delivery.Reliable);
        Log.Info($"Server: refused {to}: {why}");
    }

    /// A player gone: their cuts come off the track for everyone else.
    private void Left(PeerId peer, string? why)
    {
        if (!_players.Remove(peer, out var player))
            return;
        foreach (var shared in _cuts.Values.Where(c => c.Owner == peer).ToList())
        {
            _cuts.Remove(shared.Id);
            if (shared.Run is { } run)
            {
                _run.Remove(run);
                _railway.Remove(run);
            }

            Pass(peer, _writer.Begin(Protocol.Id(Protocol.CutRemoved)).U64(shared.Id).Span.ToArray(), Delivery.Reliable);
        }

        Log.Info($"Server: {player.Name} left{(why is null ? "" : $" ({why})")}");
    }

    public void Dispose() => _session.Dispose();
}
