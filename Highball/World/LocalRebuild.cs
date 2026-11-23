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
public sealed record LocalRebuild(NetworkLines Previous, HashSet<long> Near, HashSet<long> Halo, List<(double X, double Z)> Places);
