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
public static class Protocol
{
    /// Bumped whenever a message changes shape. 2: a cut's consist as a long string (it was cut off at 255 bytes).
    public const int Version = 2;

    /// The port a server listens on unless told otherwise.
    public const int DefaultPort = 24180;

    public const byte Hello = 1, Welcome = 2, Ready = 3, Refused = 4, CutAdded = 5, CutState = 6, CutRemoved = 7, Switch = 8;

    /// What NetWriter.Begin takes for a message (it adds Session.UserBase itself, and NetReader.MessageId takes it off).
    public static byte Id(byte message) => message;

    /// The player a cut belongs to, by its id.
    public static int Owner(ulong cut) => (int)(cut >> 32);
}
