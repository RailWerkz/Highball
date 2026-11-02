using System.Text;
using Brutal.RakNetApi;
using RailWerkz.Core.Logging;

namespace RailWerkz.Network;

/// A peer on the other end, by RakNet GUID.
public readonly record struct PeerId(ulong Value)
{
    public static readonly PeerId None = new(0);
    public override string ToString() => Value.ToString("X");
}

public enum Delivery
{
    /// Positions and snapshots: newest wins, losses are fine.
    Unreliable,
    /// Events that must arrive, in order: fire, kills, lobby state.
    Reliable,
}

/// A RakNet peer (Brutal.RakNet) as either a host that others join or a client joined to one. Messages are byte
/// arrays whose first byte is a game message id at or above RakNet's user range; Poll hands them out with who sent them.
public sealed unsafe class Session : IDisposable
{
    public enum Role { None, Host, Client }

    public enum EventKind { Connected, Disconnected, Failed, Message }

    public readonly record struct Event(EventKind Kind, PeerId Peer, byte[]? Data, string? Reason = null);

    /// The first byte of every game message is UserBase + its own id.
    public const byte UserBase = (byte)DefaultMessageIDTypes.UserPacketEnum;

    private RakPeerInstance _peer;
    private SystemAddress _server;
    private readonly Dictionary<ulong, RakNetGUID> _peers = [];

    public Role Mode { get; private set; }
    public bool Active => Mode != Role.None;
    public bool Connected => Mode == Role.Host || _connected;
    public PeerId Self { get; private set; }
    public PeerId HostPeer { get; private set; }
    public IEnumerable<PeerId> Peers => _peers.Keys.Select(k => new PeerId(k));
    public int PeerCount => _peers.Count;

    private bool _connected;

    /// Starts listening for up to maxClients players on the port.
    public bool Host(int port, int maxClients, out string error)
    {
        Close();
        _peer = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor((ushort)port);
        var result = _peer.Startup((uint)maxClients, new Span<SocketDescriptor>(ref socket));

        if (result != StartupResult.RaknetStarted)
        {
            error = $"Could not open UDP {port}: {result}";
            _peer.Dispose();
            _peer = default;
            return false;
        }

        _peer.SetMaximumIncomingConnections((ushort)maxClients);
        _peer.SetOccasionalPing(true);
        Mode = Role.Host;
        Self = new PeerId(_peer.GetMyGUID().G);
        HostPeer = Self;
        error = string.Empty;
        Log.Info($"Hosting on UDP {port} for {maxClients} players");
        return true;
    }

    /// Starts connecting; a Connected or Failed event follows from Poll.
    public bool Join(string host, int port, out string error)
    {
        Close();
        _peer = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor(0);
        var result = _peer.Startup(1, new Span<SocketDescriptor>(ref socket));

        if (result != StartupResult.RaknetStarted)
        {
            error = $"Network unavailable: {result}";
            _peer.Dispose();
            _peer = default;
            return false;
        }

        _peer.SetOccasionalPing(true);
        var hostBytes = Encoding.ASCII.GetBytes(host + "\0");
        var attempt = _peer.Connect(hostBytes, (ushort)port, default, null, 0, 8, 500, 0);

        if (attempt != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            error = $"Could not reach {host}:{port}: {attempt}";
            Close();
            return false;
        }

        Mode = Role.Client;
        Self = new PeerId(_peer.GetMyGUID().G);
        error = string.Empty;
        Log.Info($"Connecting to {host}:{port}");
        return true;
    }

    public void Send(PeerId to, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (!Active || !_peers.TryGetValue(to.Value, out var guid))
            return;

        _peer.Send(data, PacketPriority.HighPriority, Reliability(delivery), Channel(delivery), new AddressOrGUID(guid), false);
    }

    /// To the host, from a client.
    public void SendToHost(ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (Mode != Role.Client || !_connected)
            return;

        _peer.Send(data, PacketPriority.HighPriority, Reliability(delivery), Channel(delivery), new AddressOrGUID(_server), false);
    }

    /// To every connected peer, optionally skipping one (the sender being relayed).
    public void Broadcast(ReadOnlySpan<byte> data, Delivery delivery, PeerId except = default)
    {
        if (!Active)
            return;

        if (except.Value != 0 && _peers.TryGetValue(except.Value, out var skip))
            _peer.Send(data, PacketPriority.HighPriority, Reliability(delivery), Channel(delivery), new AddressOrGUID(skip), true);
        else
            _peer.Send(data, PacketPriority.HighPriority, Reliability(delivery), Channel(delivery), AddressOrGUID.UnassignedGuid, true);
    }

    private static PacketReliability Reliability(Delivery delivery) =>
        delivery == Delivery.Reliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableSequenced;

    private static byte Channel(Delivery delivery) => delivery == Delivery.Reliable ? (byte)0 : (byte)1;

    public int Ping(PeerId peer)
    {
        if (!Active)
            return 0;

        if (Mode == Role.Client)
            return _connected ? _peer.GetAveragePing(new AddressOrGUID(_server)) : 0;

        return _peers.TryGetValue(peer.Value, out var guid) ? _peer.GetAveragePing(new AddressOrGUID(guid)) : 0;
    }

    public void Kick(PeerId peer)
    {
        if (Mode == Role.Host && _peers.TryGetValue(peer.Value, out var guid))
        {
            _peer.CloseConnection(new AddressOrGUID(guid), true);
            _peers.Remove(peer.Value);
        }
    }

    /// Everything that arrived since the last call.
    public void Poll(List<Event> events)
    {
        if (!Active)
            return;

        for (var packet = _peer.Receive(); packet != null; packet = _peer.Receive())
        {
            var id = packet->Length > 0 ? packet->Data[0] : (byte)0;
            var from = new PeerId(packet->Guid.G);

            switch ((DefaultMessageIDTypes)id)
            {
                case DefaultMessageIDTypes.ConnectionRequestAccepted:
                    _server = packet->SystemAddress;
                    _connected = true;
                    _peers[packet->Guid.G] = packet->Guid;
                    HostPeer = from;
                    events.Add(new Event(EventKind.Connected, from, null));
                    break;

                case DefaultMessageIDTypes.NewIncomingConnection:
                    _peers[packet->Guid.G] = packet->Guid;
                    events.Add(new Event(EventKind.Connected, from, null));
                    break;

                case DefaultMessageIDTypes.DisconnectionNotification:
                case DefaultMessageIDTypes.ConnectionLost:
                    _peers.Remove(packet->Guid.G);
                    if (Mode == Role.Client)
                        _connected = false;
                    events.Add(new Event(EventKind.Disconnected, from, null,
                        (DefaultMessageIDTypes)id == DefaultMessageIDTypes.ConnectionLost ? "LINK LOST" : "SIGNED OFF"));
                    break;

                case DefaultMessageIDTypes.ConnectionAttemptFailed:
                    events.Add(new Event(EventKind.Failed, from, null, "NO ANSWER"));
                    break;

                case DefaultMessageIDTypes.NoFreeIncomingConnections:
                    events.Add(new Event(EventKind.Failed, from, null, "SERVER FULL"));
                    break;

                case DefaultMessageIDTypes.ConnectionBanned:
                    events.Add(new Event(EventKind.Failed, from, null, "BANNED"));
                    break;

                case DefaultMessageIDTypes.InvalidPassword:
                case DefaultMessageIDTypes.IncompatibleProtocolVersion:
                    events.Add(new Event(EventKind.Failed, from, null, "VERSION MISMATCH"));
                    break;

                default:
                    if (id >= UserBase)
                        events.Add(new Event(EventKind.Message, from, new ReadOnlySpan<byte>(packet->Data, (int)packet->Length).ToArray()));
                    break;
            }

            _peer.DeallocatePacket(packet);
        }
    }

    public void Close()
    {
        if (_peer.IsNull())
        {
            Mode = Role.None;
            return;
        }

        _peer.Shutdown(200);
        _peer.Dispose();
        _peer = default;
        _peers.Clear();
        _connected = false;
        Mode = Role.None;
        Self = PeerId.None;
        HostPeer = PeerId.None;
    }

    public void Dispose() => Close();
}
