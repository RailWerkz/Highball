using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// A hash that comes out the same in every run of the game (.NET's own is seeded afresh each time): FNV-1a, 64 bit.
internal struct StableHash
{
    public ulong Value { get; private set; } = 14695981039346656037UL;

    public StableHash()
    {
    }

    public void Add(long value)
    {
        for (var i = 0; i < 8; i++)
        {
            Value = (Value ^ ((ulong)value >> (i * 8) & 0xFF)) * 1099511628211UL;
        }
    }

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    public void Add(string value)
    {
        foreach (var c in value)
            Add((long)c);
        Add(-1L);
    }
}

/// The solved track the route used to keep (Track.bin.gz, and the parts of its stamp in Track.stamp.bin): replaced by the cells
/// (CellStore), which keep the lines finished and are written as each edit's saved, where the solved track was only ever good
/// until the network's next edit. Only taken away now, where one's left.
public sealed partial class NetworkLines
{
    /// Takes away the solved track a route kept before the cells (and does no harm where there's none).
    public static void ForgetSolved(RouteInfo route)
    {
        foreach (var name in (ReadOnlySpan<string>)["Track.bin.gz", "Track.stamp.bin"])
        {
            try
            {
                File.Delete(Path.Combine(route.Folder, name));
            }
            catch (IOException e)
            {
                Log.Warn($"Track: couldn't remove the old {name}: {e.Message}");
            }
        }
    }
}
