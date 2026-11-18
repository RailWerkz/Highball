namespace RailWerkz.Server;

/// The messages between players and the world server, each a byte array starting with Session.UserBase + its id.
///   Hello      player to server: name, protocol version
///   Welcome    server to player: the player's number, the world and region (which pack to open), its zone, and the
///              server track's checksum (RailGraph.Checksum)
///   Ready      player to server: its own track's checksum, once its pack's laid out; the server answers with every
///              switch and cut so far, or Refused if the tracks differ
///   Refused    server to player: why
///   CutAdded   a cut put on the track: its id (the player's number in the top 32 bits) and its consist as JSON
///   CutState   a cut's front (edge, distance, direction), each coupler's slack and each car's speed, 20 times a second
///   CutRemoved a cut gone (taken off, coupled into another, split)
///   Switch     a switch set: its node, and whether for its reverse route
///   Control    player to server: a cut the server runs, driven: reverser, notch, automatic, independent and dynamic
///              brakes, emergency, sand
///   Gauges     server to a cut's driver: a cut it runs, its brakes as the cab shows them (main and equalizing reservoirs,
///              independent cylinder, each car's brake pipe and cylinder), 10 times a second
///
/// A server-run cut that couples to another or draws apart becomes new cuts with ids the server makes (ServerCut set in the
/// low half): CutRemoved for the old, CutAdded for the new with its driver's number (0: nobody's), whose game drives it.
///
/// A cut is run either by its player's game, which sends its state (CutState) for the server to pass on, or by the server
/// (CutAdded's last flag): its player sends one CutState to place it and then only Control, and the server steps it with
/// everything else it runs and sends every player (its own too) its state.
public static class Protocol
{
    /// Bumped whenever a message changes shape. 2: a cut's consist as a long string (it was cut off at 255 bytes).
    /// 3: CutAdded says whether the server runs the cut, and who drives it; Control; Gauges.
    public const int Version = 3;

    /// Set in the low half of the ids of cuts the server made (a couple, a split), so they never meet a player's own.
    public const uint ServerCut = 0x8000_0000;

    /// The port a server listens on unless told otherwise.
    public const int DefaultPort = 24180;

    public const byte Hello = 1, Welcome = 2, Ready = 3, Refused = 4, CutAdded = 5, CutState = 6, CutRemoved = 7, Switch = 8, Control = 9, Gauges = 10;

    /// How often states are sent, seconds.
    public const double StateEvery = 0.05;

    /// What NetWriter.Begin takes for a message (it adds Session.UserBase itself, and NetReader.MessageId takes it off).
    public static byte Id(byte message) => message;

    /// The player a cut belongs to, by its id.
    public static int Owner(ulong cut) => (int)(cut >> 32);
}
