using Highball.Routes;

namespace Highball.World;

/// Takes apart fans, before the ways become lines: one track splitting into three or more at a single node, as OpenStreetMap often
/// draws them. No switch does that. Real track parts one route at a time, so here the switch stays at the node for one route, and
/// the others leave a little further along the straightest route, by a switch of their own at a new node there; and so on until
/// every node is an ordinary switch. Only the ways as fitted change, not the network's own.
public static class FanFitting
{
    /// How far a route may lie off directly opposite the track coming in, and still be part of the fan (degrees).
    private const double FanAngle = 35.0;

    /// How far along the straightest route to put the next switch, best first; and what it needs beyond that.
    private static readonly double[] Distances = [40.0, 30.0, 20.0, 15.0];
    private const double RoomOnThrough = 12.0;
    private const double RoomOnBranch = 15.0;

    /// How far along a leg its heading's measured.
    private const double HeadingReach = 15.0;

    /// How close past the new switch the moved route's first point may be.
    private const double FirstPointPast = 6.0;

    private const int MaxPasses = 32;

    /// One way out of a node along a way: the way, the node's place in it, and which way along it.
    private readonly record struct Leg(NetworkWay Way, int At, int Direction);

    public static List<NetworkWay> Apply(IEnumerable<NetworkWay> network, RouteInfo route)
    {
        var projection = route.Projection!;
        var ways = network.ToDictionary(w => w.Id);
        (double X, double Z) P(NetworkWay way, int i)
        {
            var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
            return (p.East, p.North);
        }

        static bool Usable(NetworkWay w) => w.Rail && w.Class != (int)RailClass.Disused && w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length;

        (double X, double Z)? Heading(Leg leg)
        {
            var (from, walked) = (P(leg.Way, leg.At), 0.0);
            var origin = from;
            for (var i = leg.At + leg.Direction; i >= 0 && i < leg.Way.Nodes.Length; i += leg.Direction)
            {
                var to = P(leg.Way, i);
                walked += Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
                from = to;
                if (walked >= HeadingReach)
                    break;
            }

            var (dx, dz) = (from.X - origin.X, from.Z - origin.Z);
            var length = Math.Sqrt(dx * dx + dz * dz);
            return length < 1e-6 ? null : (dx / length, dz / length);
        }

        // Keeps at it till nothing's left to take apart: each pass moves routes off one node.
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var uses = new Dictionary<long, List<Leg>>();
            foreach (var way in ways.Values.Where(Usable).OrderBy(w => w.Id))
                for (var i = 0; i < way.Nodes.Length; i++)
                {
                    if (!uses.TryGetValue(way.Nodes[i], out var list))
                        uses[way.Nodes[i]] = list = [];
                    if (i > 0)
                        list.Add(new Leg(way, i, -1));
                    if (i + 1 < way.Nodes.Length)
                        list.Add(new Leg(way, i, 1));
                }

            var changed = false;
            foreach (var (node, legs) in uses.OrderBy(u => u.Key))
                if (legs.Count >= 4 && TakeApart(node, legs, ways, uses, P, Heading))
                {
                    changed = true;
                    break;
                }

            if (!changed)
                break;
        }

        return [.. ways.Values];
    }

    private static bool TakeApart(long node, List<Leg> legs, Dictionary<long, NetworkWay> ways, Dictionary<long, List<Leg>> uses,
        Func<NetworkWay, int, (double X, double Z)> place, Func<Leg, (double X, double Z)?> heading)
    {
        var headings = legs.Select(heading).ToList();
        if (headings.Any(h => h is null))
            return false;

        // The track coming in: the leg all the others leave directly away from.
        var cos = Math.Cos(FanAngle * Math.PI / 180.0);
        int? toe = null;
        for (var t = 0; t < legs.Count; t++)
        {
            var at = t;
            if (Enumerable.Range(0, legs.Count).Where(o => o != at).All(o => -(headings[at]!.Value.X * headings[o]!.Value.X + headings[at]!.Value.Z * headings[o]!.Value.Z) >= cos))
            {
                toe = t;
                break;
            }
        }

        if (toe is not { } toeIndex)
            return false;

        var ahead = (X: -headings[toeIndex]!.Value.X, Z: -headings[toeIndex]!.Value.Z);
        var routes = Enumerable.Range(0, legs.Count).Where(o => o != toeIndex).Select(o => (Leg: legs[o], Turn: Math.Atan2(ahead.X * headings[o]!.Value.Z - ahead.Z * headings[o]!.Value.X, ahead.X * headings[o]!.Value.X + ahead.Z * headings[o]!.Value.Z))).ToList();

        // The straightest carries on; the rest but one go further along it, the one staying being the one on the outside if they
        // all lie one side (so what's moved doesn't cross it), and otherwise the shortest (what's moved needs the room).
        var through = routes.MinBy(r => Math.Abs(r.Turn));
        var others = routes.Where(r => r.Leg != through.Leg).ToList();
        var oneSide = others.All(r => r.Turn >= 0.0) || others.All(r => r.Turn <= 0.0);
        var staying = oneSide ? others.MaxBy(r => Math.Abs(r.Turn)) : others.MinBy(r => LegLength(r.Leg, place));
        var moving = others.Where(r => r.Leg != staying.Leg).Select(r => r.Leg).ToList();

        // Along the straightest route, as far as its way or the next junction.
        var path = new List<(int Index, (double X, double Z) At, double S)> { (through.Leg.At, place(through.Leg.Way, through.Leg.At), 0.0) };
        for (var i = through.Leg.At + through.Leg.Direction; i >= 0 && i < through.Leg.Way.Nodes.Length; i += through.Leg.Direction)
        {
            var at = place(through.Leg.Way, i);
            var last = path[^1];
            path.Add((i, at, last.S + Math.Sqrt((at.X - last.At.X) * (at.X - last.At.X) + (at.Z - last.At.Z) * (at.Z - last.At.Z))));
            if (uses[through.Leg.Way.Nodes[i]].Count > 2)
                break;
        }

        foreach (var distance in Distances)
        {
            if (path[^1].S < distance + RoomOnThrough)
                continue;
            if (moving.Any(m => LegLength(m, place) < distance + RoomOnBranch))
                continue;

            var after = path.FindIndex(p => p.S >= distance);
            var (before, past) = (path[after - 1], path[after]);
            var t = (distance - before.S) / Math.Max(past.S - before.S, 1e-6);
            var way = through.Leg.Way;
            var (lat, lon) = (way.Lat[before.Index] + (way.Lat[past.Index] - way.Lat[before.Index]) * t, way.Lon[before.Index] + (way.Lon[past.Index] - way.Lon[before.Index]) * t);
            var split = (node >= 0 ? 6_000_000_000_000_000L : -6_000_000_000_000_000L) + node * 16 + moving.Count;
            var (sx, sz) = (before.At.X + (past.At.X - before.At.X) * t, before.At.Z + (past.At.Z - before.At.Z) * t);

            // The new node in the straightest route's way.
            var insert = Math.Max(before.Index, past.Index);
            ways[way.Id] = Rebuilt(way, way.Id, [.. way.Nodes[..insert], split, .. way.Nodes[insert..]], [.. way.Lat[..insert], lat, .. way.Lat[insert..]], [.. way.Lon[..insert], lon, .. way.Lon[insert..]]);

            foreach (var leg in moving)
            {
                // (As it is now, if it's the same way as the one just changed.)
                var current = ways[leg.Way.Id];
                var at = Array.IndexOf(current.Nodes, node);
                var keep = new List<int>();
                var (dx, dz) = (past.At.X - before.At.X, past.At.Z - before.At.Z);
                var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
                for (var i = at + leg.Direction; i >= 0 && i < current.Nodes.Length; i += leg.Direction)
                {
                    var p = place(current, i);
                    if (keep.Count > 0 || ((p.X - sx) * dx + (p.Z - sz) * dz) / length >= FirstPointPast || i == 0 || i == current.Nodes.Length - 1)
                        keep.Add(i);
                }

                var nodes = new List<long> { split };
                var (la, lo) = (new List<double> { lat }, new List<double> { lon });
                foreach (var i in keep)
                {
                    nodes.Add(current.Nodes[i]);
                    la.Add(current.Lat[i]);
                    lo.Add(current.Lon[i]);
                }

                // A way running on through the node keeps what's behind it; what's moved is a new way.
                var hasBehind = at - leg.Direction >= 0 && at - leg.Direction < current.Nodes.Length;
                if (hasBehind)
                {
                    var (from, to) = leg.Direction > 0 ? (0, at) : (at, current.Nodes.Length - 1);
                    ways[current.Id] = Rebuilt(current, current.Id, current.Nodes[from..(to + 1)], current.Lat[from..(to + 1)], current.Lon[from..(to + 1)]);
                    var id = (current.Id >= 0 ? 7_000_000_000_000_000L : -7_000_000_000_000_000L) + current.Id * 16 + moving.Count;
                    ways[id] = Rebuilt(current, id, [.. nodes], [.. la], [.. lo]);
                }
                else
                    ways[current.Id] = Rebuilt(current, current.Id, [.. nodes], [.. la], [.. lo]);
            }

            return true;
        }

        return false;
    }

    private static double LegLength(Leg leg, Func<NetworkWay, int, (double X, double Z)> place)
    {
        var walked = 0.0;
        for (var i = leg.At; i + leg.Direction >= 0 && i + leg.Direction < leg.Way.Nodes.Length; i += leg.Direction)
        {
            var (a, b) = (place(leg.Way, i), place(leg.Way, i + leg.Direction));
            walked += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        }

        return walked;
    }

    private static NetworkWay Rebuilt(NetworkWay way, long id, long[] nodes, double[] lat, double[] lon) =>
        new() { Id = id, Rail = way.Rail, Class = way.Class, Nodes = nodes, Lat = lat, Lon = lon, Tags = way.Tags };
}
