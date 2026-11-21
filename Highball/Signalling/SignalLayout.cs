using Highball.Builder;
using Highball.Routes;
using Highball.World;

namespace Highball.Signalling;

/// Lays out a route's signals as a signal engineer might first draw them, at its track circuits' joints (TrackCircuits), with
/// heads as CSX arranges them (its standard installations: an absolute signal always shows two heads, Stop being red over red;
/// an automatic one a single head, Restricted Proceed red with a number plate):
///
/// - a control point at every switch or crossing that a main or branch line runs through: on each leg, at its joint (just past the
///   fouling point), a signal facing trains coming in. Facing the points it can send a train either way, so it's three-light over
///   three-light (top head the normal route, the lower one the diverging); on a trailing leg three-light over a searchlight. A
///   siding or yard track coming out onto the line gets the same, governing movements out. Track between two switches too short
///   for a joint of its own (a crossover) is inside the interlocking and has none.
/// - automatic signals along plain main and branch line, one each way at every joint between control points, single three-light.
/// - yards, sidings and spurs otherwise unsignalled, as they are.
///
/// Each stands on the right of the track for the trains it governs. Where another signalled track runs alongside (closer than a
/// mast could stand between them) the signals for one direction share a signal bridge across the tracks instead. Signals
/// already there (by hand, or laid before and kept) aren't doubled.
public static class SignalLayout
{
    /// How far from a switch a joint still counts as its control point's (TrackCircuits.Generate lays them 35 m out).
    private const double ControlReach = 60.0;

    /// How close one signal may be to another governing the same way before it's left out as a duplicate.
    private const double Duplicate = 30.0;

    /// Another signalled track within this of a signal's track, beside it: the signals go on a bridge.
    private const double AlongsideTrack = 8.0;

    /// Signals for the same direction on tracks side by side share one bridge if they're within this of each other along the
    /// line; a bridge spanning more tracks than this is left as masts (it's run out over a yard).
    private const double BridgeGather = 80.0;
    private const int MostBridgeTracks = 6;

    /// Bridges facing opposite ways within this of each other become one, back to back.
    private const double BackToBack = 40.0;

    private static readonly IReadOnlyList<SignalHeadType> Automatic = [SignalHeadType.Triple];
    private static readonly IReadOnlyList<SignalHeadType> Facing = [SignalHeadType.Triple, SignalHeadType.Triple];
    private static readonly IReadOnlyList<SignalHeadType> Trailing = [SignalHeadType.Triple, SignalHeadType.Searchlight];

    public sealed record Plan(List<PlacedObject> Signals, int ControlPoints, int Automatics, int Bridges, int Skipped);

    /// A signal to be: where on its track it governs from, the way trains it governs run there (unit), and its heads.
    private sealed record Want(double X, double Z, double Tx, double Tz, IReadOnlyList<SignalHeadType> Heads, bool Control);

    /// Works the layout out. joints: the circuits' joints as placed on graph (edge, distance along); spans: where the tracks are
    /// under a signal bridge put somewhere (NetworkMeshes.SpanPoints); make: a placed object from a kind, place and heading.
    public static Plan Lay(TrackGraph graph, IEnumerable<(int Edge, double S)> joints, NetworkLines lines, IReadOnlyList<PlacedObject> existing,
        Func<PlacedObject, (double X, double Z)[]> spans, Func<PlacedKind, double, double, double, long?, PlacedObject> make, Func<PlacedObject, (double X, double Z)> where)
    {
        // Which edges are signalled line: a main or branch line laid there (the line nearest the edge's middle).
        var signalled = new bool[graph.Edges.Count];
        var known = new bool[graph.Edges.Count];
        foreach (var edge in graph.Edges)
        {
            var (x, z, _, _) = graph.PointAt(edge.Index, edge.Length * 0.5);
            if (PlaceTool.NearestRail(lines, x, z, 15.0) is not { } on || on.Line.Disused)
                continue;
            known[edge.Index] = true;
            signalled[edge.Index] = (RailClass)on.Line.Class is RailClass.Main or RailClass.Branch;
        }

        bool ControlPoint(int vertex) => graph.Vertices[vertex].Junction && graph.Vertices[vertex].Ends.Any(e => signalled[e.Edge]);

        var wants = new List<Want>();
        var (controls, automatics) = (0, 0);
        foreach (var (edgeIndex, s) in joints)
        {
            if (!known[edgeIndex])
                continue;
            var edge = graph.Edges[edgeIndex];
            var nearFrom = s <= ControlReach && ControlPoint(edge.From);
            var nearTo = edge.Length - s <= ControlReach && ControlPoint(edge.To);
            var (x, z, dx, dz) = graph.PointAt(edgeIndex, s);
            if (nearFrom)
            {
                wants.Add(new Want(x, z, -dx, -dz, IsToe(graph, edge.From, edgeIndex) ? Facing : Trailing, true));
                controls++;
            }

            if (nearTo)
            {
                wants.Add(new Want(x, z, dx, dz, IsToe(graph, edge.To, edgeIndex) ? Facing : Trailing, true));
                controls++;
            }

            if (!nearFrom && !nearTo && signalled[edgeIndex])
            {
                wants.Add(new Want(x, z, dx, dz, Automatic, false));
                wants.Add(new Want(x, z, -dx, -dz, Automatic, false));
                automatics += 2;
            }
        }

        // What's there already governs too: hand-placed signals, and bridges.
        var taken = new List<(double X, double Z, double Fx, double Fz)>();
        foreach (var o in existing.Where(o => o.Kind is PlacedKind.Signal or PlacedKind.SignalBridge))
        {
            var (x, z) = where(o);
            var a = o.Heading * Math.PI / 180.0;
            taken.Add((x, z, Math.Cos(a), Math.Sin(a)));
        }

        bool Taken(double x, double z, double fx, double fz) =>
            taken.Any(t => (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z) < Duplicate * Duplicate && t.Fx * fx + t.Fz * fz > 0.8);

        var made = new List<PlacedObject>();
        var skipped = 0;
        var forBridges = new List<(Want Want, PlacedObject Bridge)>();
        foreach (var want in wants)
        {
            // The signal faces the trains it governs: against the way they run.
            var (fx, fz) = (-want.Tx, -want.Tz);
            if (Taken(want.X, want.Z, fx, fz))
            {
                skipped++;
                continue;
            }

            // On the right of the track for the trains it governs (x east, z south: right of (tx, tz) is (-tz, tx)).
            var (rx, rz) = (-want.Tz, want.Tx);
            if (PlaceTool.SignalAt(lines, want.X + rx * 2.0, want.Z + rz * 2.0) is not { } at
                || Math.Cos(at.Heading * Math.PI / 180.0) * fx + Math.Sin(at.Heading * Math.PI / 180.0) * fz < 0.5)
            {
                skipped++;
                continue;
            }

            // Another signalled track alongside: on a bridge with it.
            if (Alongside(lines, want, rx, rz) && PlaceTool.SignalBridgeAt(lines, want.X + rx * 1.0, want.Z + rz * 1.0) is { } spot)
            {
                var bridge = make(PlacedKind.SignalBridge, spot.X, spot.Z, spot.Heading, spot.Node) with { Toward = spot.Toward, Laid = true };
                forBridges.Add((want, bridge));
                taken.Add((want.X, want.Z, fx, fz));
                continue;
            }

            made.Add(make(PlacedKind.Signal, at.X, at.Z, at.Heading, at.Node) with { Toward = at.Toward, Heads = want.Heads, Laid = true });
            taken.Add((want.X, want.Z, fx, fz));
        }

        // Signals for one way on tracks side by side, near each other along the line, go on one bridge: the first one's, with each
        // one's heads over its own track. (A cantilever over one or two tracks, a bridge over more.)
        var bridges = 0;
        var built = new List<(PlacedObject Bridge, (double X, double Z)[] Tracks)>();
        var used = new bool[forBridges.Count];
        for (var i = 0; i < forBridges.Count; i++)
        {
            if (used[i])
                continue;
            var (first, bridge) = forBridges[i];
            var group = new List<Want> { first };
            for (var j = i + 1; j < forBridges.Count; j++)
            {
                var other = forBridges[j].Want;
                var (ox, oz) = (other.X - first.X, other.Z - first.Z);
                if (!used[j] && other.Tx * first.Tx + other.Tz * first.Tz > 0.9 && Math.Abs(ox * first.Tx + oz * first.Tz) < BridgeGather
                    && Math.Abs(ox * -first.Tz + oz * first.Tx) < AlongsideTrack * MostBridgeTracks)
                {
                    used[j] = true;
                    group.Add(other);
                }
            }

            var tracks = spans(bridge);
            if (tracks.Length is < 2 or > MostBridgeTracks || (group.Count == 1 && tracks.Length > 2))
            {
                // Not worth a bridge (one signal under a wide span, or a span out over a yard): each on its own mast, on the left of
                // its track, clear of the one on its right (as CSX brackets a signal to the left where it must).
                foreach (var want in group)
                    if (PlaceTool.SignalAt(lines, want.X - want.Tz * 2.0, want.Z + want.Tx * 2.0) is { } at)
                    {
                        var shift = 2.0 * PlaceTool.SignalOff;
                        made.Add(make(PlacedKind.Signal, at.X + want.Tz * shift, at.Z - want.Tx * shift, at.Heading, at.Node) with { Toward = at.Toward, Heads = want.Heads, Laid = true });
                    }

                continue;
            }

            // Each track under it: the heads of the signal on that track (its want projected across the bridge), or none.
            var (ax, az) = where(bridge);
            var a = bridge.Heading * Math.PI / 180.0;
            var (bfx, bfz) = (Math.Cos(a), Math.Sin(a));
            var (brx, brz) = (bfz, -bfx);
            var stacks = tracks.Select(track =>
            {
                var t = (track.X - ax) * brx + (track.Z - az) * brz;
                var mine = group.FirstOrDefault(w => Math.Abs((w.X - ax) * brx + (w.Z - az) * brz - t) < 2.5);
                return mine?.Heads ?? [];
            }).ToList();
            while (stacks.Count > 0 && stacks[^1].Count == 0)
                stacks.RemoveAt(stacks.Count - 1);
            built.Add((bridge with { Stacks = stacks, Style = (int)SignalHeads.StyleFor(tracks.Length) }, tracks));
        }

        // Bridges facing opposite ways at much the same place are one structure, its back's heads the other's: each of those over the
        // track its own was over (by where the tracks lie across the first).
        var merged = new bool[built.Count];
        for (var i = 0; i < built.Count; i++)
        {
            if (merged[i])
                continue;
            var (bridge, tracks) = built[i];
            var (ax, az) = where(bridge);
            var a = bridge.Heading * Math.PI / 180.0;
            var (brx, brz) = (Math.Sin(a), -Math.Cos(a));
            for (var j = i + 1; j < built.Count; j++)
            {
                var (other, otherTracks) = built[j];
                var (bx, bz) = where(other);
                if (merged[j] || Math.Cos((other.Heading - bridge.Heading) * Math.PI / 180.0) > -0.9 || (bx - ax) * (bx - ax) + (bz - az) * (bz - az) > BackToBack * BackToBack)
                    continue;
                var back = tracks.Select(track =>
                {
                    var t = (track.X - ax) * brx + (track.Z - az) * brz;
                    var m = Array.FindIndex(otherTracks, o => Math.Abs((o.X - ax) * brx + (o.Z - az) * brz - t) < 2.5);
                    return m >= 0 ? other.StackOver(m) : [];
                }).ToList();
                // (Only if every one of its heads finds its track under this one.)
                if (back.Sum(s => s.Count) != (other.Stacks ?? []).Sum(s => s.Count))
                    continue;
                while (back.Count > 0 && back[^1].Count == 0)
                    back.RemoveAt(back.Count - 1);
                bridge = bridge with { BackStacks = back };
                merged[j] = true;
                break;
            }

            made.Add(bridge);
            bridges++;
        }

        return new Plan(made, controls, automatics, bridges, skipped);
    }

    /// Whether a junction's edge is its toe: the leg a train takes the switch from facing its points, the one the others both
    /// leave opposite to. (At a crossing, or anything with more legs, none is.)
    private static bool IsToe(TrackGraph graph, int vertex, int edge)
    {
        var v = graph.Vertices[vertex];
        if (v.Degree != 3)
            return false;
        (double X, double Z) Away((int Edge, bool AtStart) end)
        {
            var e = graph.Edges[end.Edge];
            var s = Math.Min(15.0, e.Length * 0.5);
            var (x, z, _, _) = graph.PointAt(end.Edge, end.AtStart ? s : e.Length - s);
            var (dx, dz) = (x - v.X, z - v.Z);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
            return (dx / length, dz / length);
        }

        var dirs = v.Ends.Select(e => (e.Edge, Dir: Away(e))).ToList();
        var toe = dirs.MaxBy(d => dirs.Where(o => o != d).Min(o => -(o.Dir.X * d.Dir.X + o.Dir.Z * d.Dir.Z)));
        return toe.Edge == edge;
    }

    /// Whether another main or branch line runs alongside a signal's track on its right, where its mast would stand, too close for
    /// one to stand between. (On its left the mast's clear of it.)
    private static bool Alongside(NetworkLines lines, Want want, double rx, double rz)
    {
        foreach (var side in (ReadOnlySpan<double>)[1.0])
            for (var off = 3.0; off <= AlongsideTrack; off += 1.0)
            {
                var (x, z) = (want.X + rx * side * off, want.Z + rz * side * off);
                if (PlaceTool.NearestRail(lines, x, z, 1.2) is { } on && (RailClass)on.Line.Class is RailClass.Main or RailClass.Branch && on.Away < 1.2)
                {
                    // (Running alongside, not crossing.)
                    var (lx, lz, _, fx, fz, _, _) = new Leg(on.Line, 0, 1).At(on.S);
                    if (Math.Abs(fx * want.Tx + fz * want.Tz) > 0.9 && (lx - want.X) * (lx - want.X) + (lz - want.Z) * (lz - want.Z) > 4.0)
                        return true;
                }
            }

        return false;
    }
}
