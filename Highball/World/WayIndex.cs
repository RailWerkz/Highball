using Highball.Routes;

namespace Highball.World;

/// The network's ways as a rebuild looks them up: the cells (tiles) each one passes through, and the ways each node is on and ends
/// at. Kept up to date edit by edit: an edit replaces ways rather than changing them, so only the ways that aren't the objects
/// indexed are looked at again (Update). Background thread only (one rebuild at a time).
internal sealed class WayIndex(RouteProjection projection, double size)
{
    private readonly Dictionary<long, NetworkWay> _ways = [];
    private readonly Dictionary<long, TileId[]> _cells = [];
    private readonly Dictionary<TileId, HashSet<long>> _byCell = [];
    private readonly Dictionary<long, List<long>> _onNode = [];
    private readonly Dictionary<long, List<long>> _endsAt = [];

    /// How far apart a long segment is looked along for the cells it crosses, metres.
    private const double Step = 100.0;

    public int Count => _ways.Count;

    /// The ways as they are now: those replaced, added or gone since the last time are indexed again. Returns how many were.
    public int Update(IReadOnlyCollection<NetworkWay> ways)
    {
        var changed = 0;
        var now = new HashSet<long>();
        foreach (var way in ways)
        {
            now.Add(way.Id);
            if (_ways.TryGetValue(way.Id, out var had) && ReferenceEquals(had, way))
                continue;
            if (had is not null)
                Remove(had);
            Add(way);
            changed++;
        }

        if (now.Count != _ways.Count)
            foreach (var gone in _ways.Values.Where(w => !now.Contains(w.Id)).ToList())
            {
                Remove(gone);
                changed++;
            }

        return changed;
    }

    public NetworkWay? Way(long id) => _ways.GetValueOrDefault(id);

    /// The cells a way passes through.
    public IReadOnlyList<TileId> CellsOf(long way) => _cells.TryGetValue(way, out var cells) ? cells : [];

    /// The ways passing through a cell.
    public IReadOnlyCollection<long> In(TileId cell) => _byCell.TryGetValue(cell, out var ways) ? ways : [];

    /// The ways a node is on (once for each time it's on one).
    public IReadOnlyList<long> On(long node) => _onNode.TryGetValue(node, out var ways) ? ways : [];

    /// The ways ending at a node (a way ending there at both ends twice).
    public IReadOnlyList<long> EndingAt(long node) => _endsAt.TryGetValue(node, out var ways) ? ways : [];

    /// The cells a way passes through, worked out from its points (and along its long segments).
    public List<TileId> CellsOf(NetworkWay way)
    {
        var cells = new List<TileId>();
        (double X, double Z)? last = null;
        for (var i = 0; i < way.Lat.Length; i++)
        {
            var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
            var (x, z) = (p.East, -p.North);
            if (last is { } l)
            {
                var steps = (int)(Math.Sqrt((x - l.X) * (x - l.X) + (z - l.Z) * (z - l.Z)) / Step);
                for (var k = 1; k <= steps; k++)
                    Note(cells, l.X + (x - l.X) * k / (steps + 1), l.Z + (z - l.Z) * k / (steps + 1));
            }

            Note(cells, x, z);
            last = (x, z);
        }

        return cells;
    }

    private void Note(List<TileId> cells, double x, double z)
    {
        var id = new TileId((int)Math.Floor(x / size), (int)Math.Floor(-z / size));
        if (!cells.Contains(id))
            cells.Add(id);
    }

    private void Add(NetworkWay way)
    {
        _ways[way.Id] = way;
        var cells = CellsOf(way).ToArray();
        _cells[way.Id] = cells;
        foreach (var c in cells)
        {
            if (!_byCell.TryGetValue(c, out var set))
                _byCell[c] = set = [];
            set.Add(way.Id);
        }

        if (way.Nodes.Length == 0)
            return;
        foreach (var node in way.Nodes)
            Push(_onNode, node, way.Id);
        Push(_endsAt, way.Nodes[0], way.Id);
        Push(_endsAt, way.Nodes[^1], way.Id);
    }

    private void Remove(NetworkWay way)
    {
        _ways.Remove(way.Id);
        if (_cells.Remove(way.Id, out var cells))
            foreach (var c in cells)
                if (_byCell.TryGetValue(c, out var set))
                    set.Remove(way.Id);
        if (way.Nodes.Length == 0)
            return;
        foreach (var node in way.Nodes)
            Pull(_onNode, node, way.Id);
        Pull(_endsAt, way.Nodes[0], way.Id);
        Pull(_endsAt, way.Nodes[^1], way.Id);
    }

    private static void Push(Dictionary<long, List<long>> map, long node, long way)
    {
        if (!map.TryGetValue(node, out var list))
            map[node] = list = [];
        list.Add(way);
    }

    private static void Pull(Dictionary<long, List<long>> map, long node, long way)
    {
        if (map.TryGetValue(node, out var list) && list.Remove(way) && list.Count == 0)
            map.Remove(node);
    }

    /// The ways a rebuild of these cells is handed: those passing through them, each made whole (the ways it runs on into at a
    /// node only two ways meet at, end to end, as a chain does: NetworkLines.Chains), and the ways meeting any of those at a node
    /// (so every chain through the cells is made as it is from the whole network: its junctions, switches and crossovers with
    /// every track there), made whole too.
    public HashSet<long> Handed(IEnumerable<TileId> cells)
    {
        var handed = new HashSet<long>();
        var queue = new Queue<long>();
        void Take(long id)
        {
            if (_ways.ContainsKey(id) && handed.Add(id))
                queue.Enqueue(id);
        }

        void Whole()
        {
            while (queue.TryDequeue(out var id))
            {
                var way = _ways[id];
                if (way.Nodes.Length == 0)
                    continue;
                foreach (var node in (ReadOnlySpan<long>)[way.Nodes[0], way.Nodes[^1]])
                    if (On(node).Count == 2 && EndingAt(node).Count == 2)
                        foreach (var other in EndingAt(node))
                            Take(other);
            }
        }

        foreach (var cell in cells)
            foreach (var id in In(cell))
                Take(id);
        Whole();
        foreach (var id in handed.ToList())
            foreach (var node in _ways[id].Nodes)
                foreach (var other in On(node))
                    Take(other);
        Whole();
        return handed;
    }
}
