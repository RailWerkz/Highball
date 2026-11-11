using Highball.Trains;
using RailWerkz.Core.Logging;
using RailWerkz.Network;

namespace RailWerkz.Server;

/// A world or region's server. It loads only the server track (RailGraphFile: the rail graph with heights, switches and
/// signals; no terrain), and keeps what every player shares: how each switch is set, and every cut on the track with
/// its latest state, so a player joining late sees everything as it stands. In this first version each player's game
/// runs its own trains and the server passes their states on; running every train here (the Railway in RailWerkz.Sim)
/// comes next.
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
        public required PeerId Owner;
        public required byte[] Added;
        public byte[]? State;
    }

    private readonly Session _session = new();
    private readonly Dictionary<PeerId, Player> _players = [];
    private readonly Dictionary<long, bool> _switches = [];
    private readonly Dictionary<ulong, SharedCut> _cuts = [];
    private readonly List<Session.Event> _events = [];
    private readonly NetWriter _writer = new();
    private int _nextNumber = 1;
    private volatile bool _stop;

    public RailGraphFile.Contents Track { get; }
    public string World { get; }
    public string Region { get; }
    public int Port { get; }
    public int Checksum { get; }

    public int PlayerCount => _players.Count;

    public WorldServer(string serverFile, string world, int port)
    {
        Track = RailGraphFile.Read(serverFile);
        World = world;
        Region = Track.Name;
        Port = port;
        Checksum = Track.Graph.Checksum();
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
            Thread.Sleep(5);
        }
    }

    public void Stop() => _stop = true;

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
                foreach (var (node, reversed) in _switches)
                    _session.Send(from, _writer.Begin(Protocol.Id(Protocol.Switch)).I64(node).Bool(reversed).Span, Delivery.Reliable);
                foreach (var cut in _cuts.Values)
                {
                    _session.Send(from, cut.Added, Delivery.Reliable);
                    if (cut.State is { } state)
                        _session.Send(from, state, Delivery.Reliable);
                }

                Log.Info($"Server: {who.Name} ready, sent {_switches.Count} switches and {_cuts.Count} cuts");
                break;
            }
            case Protocol.CutAdded:
            {
                var cut = r.U64();
                if (Protocol.Owner(cut) != who.Number)
                    return;
                _cuts[cut] = new SharedCut { Owner = from, Added = data };
                Pass(from, data, Delivery.Reliable);
                Log.Info($"Server: {who.Name} put cut {cut:X} on the track");
                break;
            }
            case Protocol.CutState:
            {
                var cut = r.U64();
                if (!_cuts.TryGetValue(cut, out var shared) || shared.Owner != from)
                    return;
                shared.State = data;
                Pass(from, data, Delivery.Unreliable);
                break;
            }
            case Protocol.CutRemoved:
            {
                var cut = r.U64();
                if (_cuts.TryGetValue(cut, out var shared) && shared.Owner == from)
                {
                    _cuts.Remove(cut);
                    Pass(from, data, Delivery.Reliable);
                    Log.Info($"Server: {who.Name} took cut {cut:X} off");
                }

                break;
            }
            case Protocol.Switch:
            {
                var node = r.I64();
                _switches[node] = r.Bool();
                Pass(from, data, Delivery.Reliable);
                break;
            }
        }
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
        foreach (var id in _cuts.Where(c => c.Value.Owner == peer).Select(c => c.Key).ToList())
        {
            _cuts.Remove(id);
            Pass(peer, _writer.Begin(Protocol.Id(Protocol.CutRemoved)).U64(id).Span.ToArray(), Delivery.Reliable);
        }

        Log.Info($"Server: {player.Name} left{(why is null ? "" : $" ({why})")}");
    }

    public void Dispose() => _session.Dispose();
}
