using Highball.Routes;

namespace Highball.World;

/// A rebuild after an edit to track alone: the roads and marks (mapped tracks) the edit can't reach are taken finished from
/// Previous, and only the chains Near it are laid again, with the track. An edit's reach is short (a road eased onto a level
/// crossing, a bridge lifted clear of new track, a junction's height): beyond it, nothing it changed could change a line.
///
/// Halo: the chains meeting those laid again (sharing a node), laid again too so the lines near the edit are worked out against
/// them as each pass has them (as in a full rebuild), but then put back as they were: they're past the edit's reach.
///
/// Places: where the edit changed track (world metres, as it was and as it is). Rail too far from all of them is kept as it was,
/// heights and all (NetworkLines.HoldFarRail), so a grade worked out along a long line doesn't shift miles from the edit.
///
/// Dirty, Context (cells, docs/cell-routes.md "Edits"): the cells laid again, and the ring round them held as they are. Only the
/// ways through them (made whole: WayIndex.Handed) are handed to the passes, which lay the chains with any of their length in a
/// dirty cell and take every other line over the cells from Previous as it is (frozen), so every pass costs what's in the cells,
/// not the route. The lines laid then go into Previous's place in a copy of it (CellStore.Merge). Null: the whole route is handed.
public sealed record LocalRebuild(NetworkLines Previous, HashSet<long> Near, HashSet<long> Halo, List<(double X, double Z)> Places)
{
    public HashSet<TileId>? Dirty { get; init; }
    public HashSet<TileId>? Context { get; init; }

    /// The cells' size, metres.
    public double Size { get; init; }

    /// Previous's lines the passes may look at (those over the cells, and those of every chain laid again), once Build has found
    /// them; every line of Previous if the whole route's handed.
    public IReadOnlyList<NetworkLine> Seen { get; set; } = Previous.Lines;

    public TileId CellOf(double x, double z) => new((int)Math.Floor(x / Size), (int)Math.Floor(-z / Size));

    public bool InDirty(double x, double z) => Dirty is null || Dirty.Contains(CellOf(x, z));
}
