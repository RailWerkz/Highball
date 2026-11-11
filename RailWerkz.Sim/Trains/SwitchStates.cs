namespace Highball.Trains;

/// Which way every switch is set: normal unless set for its reverse route. The simulation's own record, which trains
/// read as they run through and the renderer swings the blades to; nothing here knows how a switch is drawn.
public sealed class SwitchStates
{
    private readonly Dictionary<long, bool> _reversed = [];

    /// Bumped on every change.
    public int Version { get; private set; }

    /// Whether a switch is set for its reverse route.
    public bool Reversed(long node) => _reversed.GetValueOrDefault(node);

    public void Set(long node, bool reversed)
    {
        if (Reversed(node) == reversed)
            return;
        _reversed[node] = reversed;
        Version++;
        Changed?.Invoke(node, reversed);
    }

    /// Sets a switch for the other route.
    public void Throw(long node) => Set(node, !Reversed(node));

    /// Every switch that's ever been set, and how.
    public IEnumerable<KeyValuePair<long, bool>> All => _reversed;

    /// A switch was set: its node, and whether for its reverse route.
    public event Action<long, bool>? Changed;
}
