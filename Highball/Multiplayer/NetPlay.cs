using System.Text.Json;
using Highball.Trains;
using RailWerkz.Core.Logging;
using RailWerkz.Network;
using RailWerkz.Server;

namespace Highball.Multiplayer;

/// This game's side of a multiplayer session. It joins a world server (hosting one first, on a thread of its own, if
/// this player's the host), learns which world and region to open, and once that pack's track is laid out checks it's
/// the same as the server's. From then on it tells the server about this player's cuts (put on, their state 20 times
/// a second, taken off) and switches, and puts every other player's cuts on the track to be shown where they say they
/// are (Railway.AddRemote, Train.Mirror), carried on between their updates at the speed they last had.
public sealed class NetPlay : IDisposable
{
    public enum Stage { Connecting, Welcomed, Playing, Failed }

    private sealed class Remote
    {
        public required Consist Consist;
        public Railway.Cut? Cut;
        public TrackPosition Head;
        public float[] Extension = [];
        public float[] Velocity = [];
        public double At;
        public double Logged;
    }

    private const double SendEvery = 0.05;

    private readonly Session _session = new();
    private readonly NetWriter _writer = new();
    private readonly List<Session.Event> _events = [];
    private readonly string _name;
    private readonly Dictionary<Railway.Cut, ulong> _mine = [];
    private readonly Dictionary<ulong, Remote> _remote = [];
    private uint _nextCut = 1;
    private double _now, _sentAt;
    private bool _applyingSwitch;
    private Railway? _railway;
    private SwitchStates? _switches;
    private RailGraph? _graph;
    private int _serverChecksum;

    private WorldServer? _server;
    private Thread? _serverThread;

    public Stage State { get; private set; } = Stage.Connecting;
    public string? Problem { get; private set; }
    public int Number { get; private set; }
    public string World { get; private set; } = "";
    public string Region { get; private set; } = "";
    public bool Hosting => _server is not null;

    /// Something to tell the player.
    public event Action<string>? Notice;

    private NetPlay(string name) => _name = name;

    public static NetPlay Join(string host, int port, string name)
    {
        var net = new NetPlay(name);
        if (!net._session.Join(host, port, out var error))
            net.Fail($"Couldn't connect to {host}:{port}: {error}");
        else
            Log.Info($"Multiplayer: connecting to {host}:{port}");
        return net;
    }

    /// Hosts a server for an installed world or region (its .hbserver) on this machine, then joins it.
    public static NetPlay Host(string serverFile, string world, int port, string name)
    {
        var server = new WorldServer(serverFile, world, port);
        var thread = new Thread(server.Run) { IsBackground = true, Name = "World server" };
        thread.Start();
        var net = Join("127.0.0.1", port, name);
        net._server = server;
        net._serverThread = thread;
        return net;
    }

    private void Fail(string why)
    {
        State = Stage.Failed;
        Problem = why;
        Log.Warn($"Multiplayer: {why}");
        Notice?.Invoke(why);
    }

    /// The pack's track is laid out: checks it against the server's, and starts sharing cuts and switches.
    public void Attach(Railway railway, SwitchStates switches, RailGraph graph)
    {
        if (State == Stage.Failed)
            return;
        var first = _railway is null;
        _railway = railway;
        _graph = graph;
        if (!ReferenceEquals(_switches, switches))
        {
            if (_switches is not null)
                _switches.Changed -= SwitchChanged;
            _switches = switches;
            _switches.Changed += SwitchChanged;
        }

        var mine = graph.Checksum();
        if (mine != _serverChecksum)
        {
            Fail("Track differs from the server's pack");
            _session.Close();
            return;
        }

        if (first)
        {
            _session.SendToHost(_writer.Begin(Protocol.Id(Protocol.Ready)).I32(mine).Span, Delivery.Reliable);
            State = Stage.Playing;
            Log.Info($"Multiplayer: playing {World}/{Region} as player {Number}");
        }
    }

    /// Every frame: messages in, this player's cuts out.
    public void Update(double dt)
    {
        _now += dt;
        Poll();
        if (State != Stage.Playing || _railway is not { } railway)
            return;

        // This player's cuts: new ones announced, gone ones taken off, every one's state now and then.
        foreach (var cut in railway.Cuts)
            if (!cut.Remote && !_mine.ContainsKey(cut))
            {
                var id = ((ulong)(uint)Number << 32) | _nextCut++;
                _mine[cut] = id;
                var consist = new Consist { Name = cut.Train.Name, Cars = [.. cut.Train.Cars.Select(c => new ConsistCar(c.Def.Id, c.Livery?.Id, c.Reversed, c.Fill > 0.0, c.Tag, c.Fill, c.Cargo))] };
                _session.SendToHost(_writer.Begin(Protocol.Id(Protocol.CutAdded)).U64(id).LongStr(JsonSerializer.Serialize(consist)).Span, Delivery.Reliable);
                SendState(id, cut.Train);
            }

        foreach (var (cut, id) in _mine.Where(m => !railway.Cuts.Contains(m.Key)).ToList())
        {
            _mine.Remove(cut);
            _session.SendToHost(_writer.Begin(Protocol.Id(Protocol.CutRemoved)).U64(id).Span, Delivery.Reliable);
        }

        if (_now - _sentAt >= SendEvery)
        {
            _sentAt = _now;
            foreach (var (cut, id) in _mine)
                SendState(id, cut.Train);
        }

        // Everyone else's: carried on from where they last said, at the speed they had (no more than a third of a
        // second on, so a late packet can't fling one far).
        foreach (var remote in _remote.Values)
        {
            if (remote.Cut is not { } cut || _graph is not { } graph)
                continue;
            var head = remote.Head;
            var ahead = remote.Velocity.Length > 0 ? remote.Velocity[0] * Math.Min(_now - remote.At, 0.3) : 0.0;
            if (Math.Abs(ahead) > 1e-6)
                graph.Move(ref head, ahead);
            cut.Train.Mirror(head, remote.Extension, remote.Velocity);
        }
    }

    private void SendState(ulong id, Train train)
    {
        var w = _writer.Begin(Protocol.Id(Protocol.CutState)).U64(id).I32(train.Head.Edge).F32((float)train.Head.S).I32(train.Head.Direction);
        var body = train.Body;
        w.U16((ushort)body.Velocity.Length);
        for (var i = 0; i < body.Velocity.Length - 1; i++)
            w.F32((float)(i < body.Extension.Length ? body.Extension[i] : 0.0));
        for (var i = 0; i < body.Velocity.Length; i++)
            w.F32((float)body.Velocity[i]);
        _session.SendToHost(w.Span, Delivery.Unreliable);
    }

    private void SwitchChanged(long node, bool reversed)
    {
        if (_applyingSwitch || State != Stage.Playing)
            return;
        _session.SendToHost(_writer.Begin(Protocol.Id(Protocol.Switch)).I64(node).Bool(reversed).Span, Delivery.Reliable);
    }

    /// Messages in: before playing (the welcome) as well as during.
    public void Poll()
    {
        _events.Clear();
        _session.Poll(_events);
        foreach (var e in _events)
        {
            switch (e.Kind)
            {
                case Session.EventKind.Connected:
                    Log.Info("Multiplayer: connected, saying hello");
                    _session.SendToHost(_writer.Begin(Protocol.Id(Protocol.Hello)).Str(_name).I32(Protocol.Version).Span, Delivery.Reliable);
                    break;
                case Session.EventKind.Failed:
                    Fail($"Couldn't connect ({e.Reason ?? "no answer"})");
                    break;
                case Session.EventKind.Disconnected:
                    if (State != Stage.Failed)
                        Fail($"Disconnected from the server{(e.Reason is null ? "" : $" ({e.Reason})")}");
                    RemoveRemotes();
                    break;
                case Session.EventKind.Message when e.Data is { } data:
                    try
                    {
                        Handle(data);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Multiplayer: bad message ({ex.Message})");
                    }

                    break;
            }
        }
    }

    private void Handle(byte[] data)
    {
        var r = new NetReader(data);
        switch (r.MessageId)
        {
            case Protocol.Welcome:
                Number = r.I32();
                World = r.Str();
                Region = r.Str();
                r.I32();
                _serverChecksum = r.I32();
                State = Stage.Welcomed;
                Log.Info($"Multiplayer: welcomed as player {Number} to {World}/{Region}");
                break;
            case Protocol.Refused:
                Fail(r.Str());
                _session.Close();
                break;
            case Protocol.CutAdded:
            {
                var id = r.U64();
                var consist = JsonSerializer.Deserialize<Consist>(r.LongStr()) ?? new Consist();
                _remote[id] = new Remote { Consist = consist };
                Notice?.Invoke($"Another driver put {consist.Name} on the track");
                break;
            }
            case Protocol.CutState:
            {
                var id = r.U64();
                if (!_remote.TryGetValue(id, out var remote) || _graph is not { } graph || _railway is not { } railway)
                    return;
                var head = new TrackPosition(r.I32(), r.F32(), r.I32());
                var cars = r.U16();
                var extension = new float[Math.Max(0, cars - 1)];
                for (var i = 0; i < extension.Length; i++)
                    extension[i] = r.F32();
                var velocity = new float[cars];
                for (var i = 0; i < cars; i++)
                    velocity[i] = r.F32();
                if (r.Overrun || head.Edge < 0 || head.Edge >= graph.Edges.Count)
                    return;
                (remote.Head, remote.Extension, remote.Velocity, remote.At) = (head, extension, velocity, _now);
                if (remote.Cut is null)
                {
                    var train = Train.Place(graph, remote.Consist, head, out var why, PlacedAs.Running);
                    if (train is null)
                    {
                        Log.Warn($"Multiplayer: couldn't show {remote.Consist.Name} ({why})");
                        return;
                    }

                    remote.Cut = railway.AddRemote(train);
                    Log.Info($"Multiplayer: showing {remote.Consist.Name} ({cars} vehicles) for player {Protocol.Owner(id)} on edge {head.Edge}");
                }
                else if (_now - remote.Logged > 10.0)
                {
                    remote.Logged = _now;
                    var (x, _, z) = graph.PositionOf(head);
                    Log.Info($"Multiplayer: {remote.Consist.Name} at edge {head.Edge}, {velocity[0] * 2.2369363:0} mph, x {x:0} z {z:0}");
                }

                break;
            }
            case Protocol.CutRemoved:
            {
                var id = r.U64();
                if (_remote.Remove(id, out var remote) && remote.Cut is { } cut)
                    _railway?.Remove(cut);
                break;
            }
            case Protocol.Switch:
            {
                var node = r.I64();
                var reversed = r.Bool();
                _applyingSwitch = true;
                _switches?.Set(node, reversed);
                _applyingSwitch = false;
                break;
            }
        }
    }

    private void RemoveRemotes()
    {
        foreach (var remote in _remote.Values)
            if (remote.Cut is { } cut)
                _railway?.Remove(cut);
        _remote.Clear();
    }

    public void Dispose()
    {
        if (_switches is not null)
            _switches.Changed -= SwitchChanged;
        _session.Dispose();
        if (_server is not null)
        {
            _server.Stop();
            _serverThread?.Join(1000);
            _server.Dispose();
        }
    }
}
