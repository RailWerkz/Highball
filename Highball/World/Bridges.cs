using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// What a bridge's piers are: a wall (under a railway, as a rule), a row of columns under a cap, or one column under a hammerhead.
public enum PierStyle { Auto, Wall, Columns, Column }

/// What carries a bridge's deck: steel plate girders, or a concrete beam.
public enum DeckKind { Steel, Concrete }

/// What's been set by hand on one bridge, by its key (Bridges.json). Piers: where they stand, metres along the bridge from its
/// start (null: placed automatically, clear of whatever runs underneath). Room: how many more tracks (or lanes) of deck either side
/// of what it carries now. Wings: each end's retaining walls, their length (null: as the bank needs) and how far they flare back
/// from square, degrees.
public sealed record BridgeEdit(long Key, float[]? Piers = null, PierStyle Style = PierStyle.Auto, DeckKind Deck = DeckKind.Steel,
    int RoomLeft = 0, int RoomRight = 0, float? StartWing = null, float? EndWing = null, float StartFlare = 0f, float EndFlare = 0f);

/// The route's hand-set bridges: Bridges.json in its folder.
public static class RouteBridges
{
    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Bridges.json");

    public static Dictionary<long, BridgeEdit> Load(RouteInfo route)
    {
        try
        {
            var path = PathIn(route);
            var list = File.Exists(path) ? JsonSerializer.Deserialize<List<BridgeEdit>>(File.ReadAllText(path)) ?? [] : [];
            return list.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception e)
        {
            Log.Warn($"Bridges: {e.Message}");
            return [];
        }
    }

    public static void Save(RouteInfo route, IEnumerable<BridgeEdit> edits)
    {
        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(edits.OrderBy(e => e.Key).ToList()));
    }
}

/// A hand edit to one bridge, for undo (null: as worked out).
public sealed record BridgeChange(long Key, BridgeEdit? Before, BridgeEdit? After);

/// Something a bridge passes over that its piers keep clear of: where it crosses the bridge's middle line (metres along the
/// bridge), how far it reaches each side of its own middle, its direction, and the sine of the angle it crosses at.
public readonly record struct BridgeUnder(float S, float Half, float Dx, float Dz, float Sine);

/// Where one pier stands: metres along the bridge, and the way its wall or row of columns runs across (a unit vector in the world).
public readonly record struct BridgePier(float S, float Ax, float Az);

/// One bridge: the bridged stretches of lines side by side over the same gap, carried by one deck on one set of piers.
/// Everything's measured along the reference (the longest of them): S from its start, across to its right.
public sealed class BridgeGroup
{
    public required long Key { get; init; }
    public required NetworkLine Reference { get; init; }
    /// The reference's bridged points, first to last.
    public required int First { get; init; }
    public required int Last { get; init; }
    /// The stretches it carries, with each one's middle's offset across from the reference's middle line and its deck's half width.
    public required List<(NetworkLine Line, int First, int Last, float Offset, float Half)> Members { get; init; }
    public required bool Rail { get; init; }
    public required double X { get; init; }
    public required double Z { get; init; }

    public float From => Reference.S[First];
    public float To => Reference.S[Last];
    public float Length => To - From;

    /// The deck's reach across, from the reference's middle line (negative: to its left), before any room's added.
    public float Left => Spread?.Left ?? Members.Min(m => m.Offset - m.Half);
    public float Right => Spread?.Right ?? Members.Max(m => m.Offset + m.Half);

    /// How far across it its members reach anywhere along it, measured point by point (members that splay apart, as a U-turn's two
    /// ways over one deck do, reach further than their middles' offsets say), and the stretches across that each covers all the way
    /// along.
    public (float Left, float Right)? Spread { get; set; }
    public List<(float From, float To)>? Covered { get; set; }

    /// Whether its deck carries straight on at each end into another line's bridge (lanes merging or parting on the deck, a slip road
    /// leaving it): no abutment there, but a pier under the joint.
    public bool StartJoined { get; set; }
    public bool EndJoined { get; set; }

    /// How far in from each end no pier may stand (metres along it): over a paved slope, which the seat at its top carries the deck
    /// across, or before a wall.
    public float StartClear { get; set; }
    public float EndClear { get; set; }

    /// What runs underneath, found when the bridge is.
    public List<BridgeUnder> Under { get; } = [];

    /// The piers worked out for it (until something's changed).
    public List<BridgePier>? AutoPiers { get; set; }
}

/// Finding the route's bridges and placing their piers.
public static class BridgePlanner
{
    /// How far past a paved slope's toe (or a wall) a pier keeps.
    private const float PierOffSlope = 1f;

    /// The longest span between supports a deck can have.
    public static float MaxSpan(DeckKind deck) => deck == DeckKind.Concrete ? 30f : 40f;

    /// How far a deck reaches either side of its line's middle, and how deep under it its beams go.
    /// (A guide's deck is made for the one track it'll carry.)
    public static float DeckHalf(NetworkLine line) => line.Rail ? line.BallastHalfTop + 0.75f : line.Mark ? NetworkLine.BallastShoulder + 0.75f : line.Width * 0.5f + 0.55f;
    public static float DeckDepth(NetworkLine line) => line.Rail || line.Mark ? 1.8f : 1.4f;

    /// How near a guide's middle built track has to run for its bridge to give way to the track's.
    private const double GuideCovered = 3.0;

    /// Every bridge on the lines: each line's bridged stretch, put together with any others beside it going the same way over the
    /// same gap at much the same height. A guide's bridge is one until track's laid across it, when the track's bridge takes its place.
    public static List<BridgeGroup> Find(NetworkLines lines)
    {
        var spans = new List<(NetworkLine Line, int First, int Last, double Mx, double Mz, double Fx, double Fz, float Y, float Half, float Length)>();
        foreach (var line in lines.Lines)
        {
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                if (line.Mark && Enumerable.Range(first, last - first + 1).All(q => lines.UnderTrack(line.X[q], line.Z[q], GuideCovered)))
                {
                    p = to;
                    continue;
                }

                var middle = (line.S[first] + line.S[last]) * 0.5f;
                var (x, z, y, fx, fz, _, _) = new Leg(line, 0, 1).At(middle);
                spans.Add((line, first, last, x, z, fx, fz, y, DeckHalf(line), line.S[last] - line.S[first]));
                p = to;
            }
        }

        // Side by side: much the same way, beside one another (decks within a few metres), overlapping along, at much the same height.
        var parent = Enumerable.Range(0, spans.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (var i = 0; i < spans.Count; i++)
            for (var j = i + 1; j < spans.Count; j++)
            {
                var (a, b) = (spans[i], spans[j]);
                // (A guide's height is only the ground's, so guides go together across more of a difference, and are levelled to the deck after.)
                if (Math.Abs(a.Fx * b.Fx + a.Fz * b.Fz) < 0.95 || MathF.Abs(a.Y - b.Y) > (a.Line.Mark || b.Line.Mark ? 4f : 1.2f))
                    continue;
                var (dx, dz) = (b.Mx - a.Mx, b.Mz - a.Mz);
                var across = Math.Abs(dx * -a.Fz + dz * a.Fx);
                var along = Math.Abs(dx * a.Fx + dz * a.Fz);
                if (across <= a.Half + b.Half + 4.0 && along <= (a.Length + b.Length) * 0.5 && SideBySide(a.Line, a.First, a.Last, b.Line, b.First, b.Last, a.Half + b.Half + 4.0))
                    parent[Root(i)] = Root(j);
                // Decks that overlap anywhere, going much the same way at much the same height, can only be one deck. (No part of a deck is
                // further from its middle than half its length and its half width, so decks whose middles are further apart than that
                // can't touch: only those near enough are looked at closely.)
                else if (!a.Line.Mark && !b.Line.Mark && Math.Abs(a.Fx * b.Fx + a.Fz * b.Fz) >= 0.8 &&
                         Math.Sqrt(dx * dx + dz * dz) <= (a.Length + b.Length) * 0.5 + a.Half + b.Half + 0.01 &&
                         Overlap(a.Line, a.First, a.Last, b.Line, b.First, b.Last, a.Half + b.Half))
                    parent[Root(i)] = Root(j);
            }

        var groups = new List<BridgeGroup>();
        foreach (var set in Enumerable.Range(0, spans.Count).GroupBy(Root))
        {
            var members = set.Select(i => spans[i]).ToList();
            var reference = members.OrderByDescending(m => m.Line.Rail).ThenByDescending(m => m.Line.Mark).ThenByDescending(m => m.Length).First();
            foreach (var m in members.Where(m => m.Line.Mark && m.Line != reference.Line))
                LevelTo(m.Line, m.First, m.Last, reference.Line, reference.First, reference.Last);
            var (rx, rz) = (-reference.Fz, reference.Fx);
            // Its key stays the same as track's added to it: a guide's place, if it's over one (guides have no nodes); otherwise the
            // lowest mapped node at its ends, or failing that the oldest made one (made ones count down from -1).
            long key;
            if (members.Where(m => m.Line.Mark).OrderByDescending(m => m.Length).ThenBy(m => m.Line.Index).FirstOrDefault() is { Line: not null } guide)
                key = (long)Math.Round(guide.Mx) * 1_000_003L + (long)Math.Round(guide.Mz);
            else
            {
                var ends = members.SelectMany(m => m.Line.Nodes.Where(n => n.Point == m.First || n.Point == m.Last).Select(n => n.Node)).ToList();
                key = ends.Any(n => n >= 0) ? ends.Where(n => n >= 0).Min()
                    : ends.Count > 0 ? ends.Max()
                    : (long)Math.Round(reference.Mx) * 1_000_003L + (long)Math.Round(reference.Mz);
            }

            var group = new BridgeGroup
            {
                Key = key, Reference = reference.Line, First = reference.First, Last = reference.Last,
                Members = [.. members.Select(m => (m.Line, m.First, m.Last, (float)((m.Mx - reference.Mx) * rx + (m.Mz - reference.Mz) * rz), m.Half))],
                Rail = members.Any(m => m.Line.Rail || m.Line.Mark), X = reference.Mx, Z = reference.Mz,
            };
            if (members.Count > 1)
            {
                // Each member's reach across the reference, point by point along it.
                var (lo, hi) = (float.MaxValue, float.MinValue);
                var covered = new List<(float From, float To)>();
                foreach (var m in members)
                {
                    var (near, far) = (float.MaxValue, float.MinValue);
                    for (var p = m.First; p <= m.Last; p++)
                    {
                        var across = AcrossOf(reference.Line, m.Line.X[p], m.Line.Z[p]);
                        (near, far) = (MathF.Min(near, across), MathF.Max(far, across));
                    }

                    (lo, hi) = (MathF.Min(lo, near - m.Half), MathF.Max(hi, far + m.Half));
                    if (!m.Line.Mark && far - m.Half < near + m.Half)
                        covered.Add((far - m.Half, near + m.Half));
                }

                group.Spread = (lo, hi);
                group.Covered = covered;
            }

            groups.Add(group);
        }

        // Which ends carry on into another line's bridge: a member's end at a node where some other line, not in the group, is bridged
        // on one side or the other of that node.
        var atNode = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines.Lines.Where(l => !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                if (!atNode.TryGetValue(node, out var list))
                    atNode[node] = list = [];
                list.Add((line, point));
            }

        static bool Bridged(NetworkLine line, int point) =>
            (point > 0 && line.Structures[point - 1] == Structure.Bridge) || (point < line.Structures.Length && line.Structures[point] == Structure.Bridge);
        foreach (var group in groups)
        {
            var carried = group.Members.Select(m => m.Line).ToHashSet();
            bool Joined(NetworkLine line, int point) =>
                line.Nodes.Where(n => n.Point == point).Any(n => atNode.TryGetValue(n.Node, out var others) && others.Any(o => !carried.Contains(o.Line) && Bridged(o.Line, o.Point)));
            // Each member's end nearer the reference's start, or its end.
            var (rx, rz) = (group.Reference.X[group.First], group.Reference.Z[group.First]);
            bool Side(bool start) => group.Members.Any(m =>
            {
                var nearFirst = Math.Pow(m.Line.X[m.First] - rx, 2) + Math.Pow(m.Line.Z[m.First] - rz, 2) < Math.Pow(m.Line.X[m.Last] - rx, 2) + Math.Pow(m.Line.Z[m.Last] - rz, 2);
                return Joined(m.Line, nearFirst == start ? m.First : m.Last);
            });
            (group.StartJoined, group.EndJoined) = (Side(true), Side(false));
            static float Kept(BridgeEnd? end) => end is { Kind: BridgeEndKind.Slope or BridgeEndKind.Wall } e ? e.Run + PierOffSlope : 0f;
            (group.StartClear, group.EndClear) = (Kept(lines.BridgeEndAt(group.Reference, group.First)), Kept(lines.BridgeEndAt(group.Reference, group.Last)));
        }

        // Every line's segments, filed by the grid cells they cross, so each bridge only looks at what's near it. (A long line's bounds
        // take in most of the route: going through all of its points for every bridge was most of finding them.)
        var order = new Dictionary<NetworkLine, int>();
        var cells = new Dictionary<(int X, int Z), List<(NetworkLine Line, int I)>>();
        foreach (var line in lines.Lines)
        {
            order[line] = order.Count;
            for (var i = 0; i + 1 < line.Count; i++)
                foreach (var cell in UnderCells(line.X[i], line.Z[i], line.X[i + 1], line.Z[i + 1]))
                {
                    if (!cells.TryGetValue(cell, out var list))
                        cells[cell] = list = [];
                    list.Add((line, i));
                }
        }

        foreach (var group in groups)
            FindUnder(group, cells, order);
        return groups;
    }

    /// Whether two bridged stretches stay beside each other the whole way along: every point of the shorter within reach of the
    /// other, and on the same level of an interchange.
    private static bool SideBySide(NetworkLine a, int a0, int a1, NetworkLine b, int b0, int b1, double reach)
    {
        if (a.S[a1] - a.S[a0] > b.S[b1] - b.S[b0])
            (a, a0, a1, b, b0, b1) = (b, b0, b1, a, a0, a1);
        if (a.Layers.Length > 0 && b.Layers.Length > 0 && a.Layers[Math.Min((a0 + a1) / 2, a.Layers.Length - 1)] != b.Layers[Math.Min((b0 + b1) / 2, b.Layers.Length - 1)])
            return false;
        for (var p = a0; p <= a1; p += Math.Max(1, (a1 - a0) / 12))
        {
            var best = double.MaxValue;
            for (var q = b0; q < b1; q++)
            {
                var (dx, dz) = (b.X[q + 1] - b.X[q], b.Z[q + 1] - b.Z[q]);
                var t = Math.Clamp(((a.X[p] - b.X[q]) * dx + (a.Z[p] - b.Z[q]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                best = Math.Min(best, Math.Pow(b.X[q] + dx * t - a.X[p], 2) + Math.Pow(b.Z[q] + dz * t - a.Z[p], 2));
            }

            if (best > reach * reach)
                return false;
        }

        return true;
    }

    /// Whether two bridged stretches come nearer each other anywhere than reach (their decks overlap).
    private static bool Overlap(NetworkLine a, int a0, int a1, NetworkLine b, int b0, int b1, double reach)
    {
        for (var p = a0; p <= a1; p++)
            for (var q = b0; q < b1; q++)
            {
                var (dx, dz) = (b.X[q + 1] - b.X[q], b.Z[q + 1] - b.Z[q]);
                var t = Math.Clamp(((a.X[p] - b.X[q]) * dx + (a.Z[p] - b.Z[q]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                if (Math.Pow(b.X[q] + dx * t - a.X[p], 2) + Math.Pow(b.Z[q] + dz * t - a.Z[p], 2) < reach * reach)
                    return true;
            }

        return false;
    }

    /// How far a place is across a line (to its right), from the nearest place on it.
    private static float AcrossOf(NetworkLine line, double x, double z)
    {
        var (best, across) = (double.MaxValue, 0.0);
        for (var q = 0; q + 1 < line.Count; q++)
        {
            var (dx, dz) = (line.X[q + 1] - line.X[q], line.Z[q + 1] - line.Z[q]);
            var length2 = Math.Max(dx * dx + dz * dz, 1e-9);
            var t = Math.Clamp(((x - line.X[q]) * dx + (z - line.Z[q]) * dz) / length2, 0.0, 1.0);
            var d = Math.Pow(line.X[q] + dx * t - x, 2) + Math.Pow(line.Z[q] + dz * t - z, 2);
            if (d < best)
                (best, across) = (d, ((x - line.X[q]) * -dz + (z - line.Z[q]) * dx) / Math.Sqrt(length2));
        }

        return (float)across;
    }

    /// A guide's bridged points set to the height of the bridge's reference line beside each.
    private static void LevelTo(NetworkLine line, int first, int last, NetworkLine reference, int from, int to)
    {
        for (var p = first; p <= last; p++)
        {
            var (best, height) = (double.MaxValue, line.Y[p]);
            for (var q = from; q <= to; q++)
            {
                var d = (reference.X[q] - line.X[p]) * (reference.X[q] - line.X[p]) + (reference.Z[q] - line.Z[p]) * (reference.Z[q] - line.Z[p]);
                if (d < best)
                    (best, height) = (d, reference.Y[q]);
            }

            line.Y[p] = height;
        }
    }

    /// The lowest the reference line's deck comes.
    private static float MinDeckOf(BridgeGroup group)
    {
        var low = float.MaxValue;
        for (var p = group.First; p <= group.Last; p++)
            low = MathF.Min(low, group.Reference.Y[p]);
        return low;
    }

    /// What runs under a bridge: every grounded road or track whose middle line crosses the reference's over the bridge.
    private static void FindUnder(BridgeGroup group, Dictionary<(int X, int Z), List<(NetworkLine Line, int I)>> cells, Dictionary<NetworkLine, int> order)
    {
        var reference = group.Reference;
        var carried = group.Members.Select(m => m.Line).ToHashSet();
        var (minX, maxX, minZ, maxZ) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
        for (var p = group.First; p <= group.Last; p++)
            (minX, maxX, minZ, maxZ) = (Math.Min(minX, reference.X[p]), Math.Max(maxX, reference.X[p]), Math.Min(minZ, reference.Z[p]), Math.Max(maxZ, reference.Z[p]));

        // What's near, in the order the lines and their segments come (so what's under is found in the same order as ever).
        var near = new HashSet<(NetworkLine Line, int I)>();
        foreach (var cell in UnderCells(minX, minZ, maxX, maxZ))
            if (cells.TryGetValue(cell, out var list))
                foreach (var segment in list)
                    if (!carried.Contains(segment.Line))
                        near.Add(segment);

        foreach (var (line, i) in near.OrderBy(n => order[n.Line]).ThenBy(n => n.I))
        {
            {
                // On the ground, or a deck below this one (a pier mustn't come down through it either).
                if (line.Structures[i] == Structure.Tunnel)
                    continue;
                if (line.Structures[i] == Structure.Bridge && MathF.Max(line.Y[i], line.Y[i + 1]) > MinDeckOf(group) - 3f)
                    continue;
                var (ax, az, bx, bz) = (line.X[i], line.Z[i], line.X[i + 1], line.Z[i + 1]);
                for (var p = group.First; p < group.Last; p++)
                {
                    var (cx, cz, ex, ez) = (reference.X[p], reference.Z[p], reference.X[p + 1], reference.Z[p + 1]);
                    var (ux, uz, vx, vz) = (bx - ax, bz - az, ex - cx, ez - cz);
                    var cross = ux * vz - uz * vx;
                    if (Math.Abs(cross) < 1e-9)
                        continue;
                    var (wx, wz) = (cx - ax, cz - az);
                    var t = (wx * vz - wz * vx) / cross;
                    var q = (wx * uz - wz * ux) / cross;
                    if (t < 0.0 || t > 1.0 || q < 0.0 || q > 1.0)
                        continue;
                    var (lu, lv) = (Math.Sqrt(ux * ux + uz * uz), Math.Sqrt(vx * vx + vz * vz));
                    var sine = (float)Math.Max(Math.Abs(cross) / Math.Max(lu * lv, 1e-9), 0.2);
                    var s = reference.S[p] + (reference.S[p + 1] - reference.S[p]) * (float)q - group.From;
                    // A road's own width (its shoulders included), not the flat ground it's laid on: the median beside it is for piers.
                    var half = line.Rail || line.Mark ? line.Formation : line.Width * 0.5f + (i < line.Shift.Length ? MathF.Abs(line.Shift[i]) : 0f);
                    group.Under.Add(new BridgeUnder(s, half, (float)(ux / lu), (float)(uz / lu), sine));
                }
            }
        }
    }

    /// The grid cells (UnderCell a side) a box covers, for FindUnder.
    private const double UnderCell = 64.0;

    private static IEnumerable<(int X, int Z)> UnderCells(double ax, double az, double bx, double bz)
    {
        var (x0, x1) = ((int)Math.Floor(Math.Min(ax, bx) / UnderCell), (int)Math.Floor(Math.Max(ax, bx) / UnderCell));
        var (z0, z1) = ((int)Math.Floor(Math.Min(az, bz) / UnderCell), (int)Math.Floor(Math.Max(az, bz) / UnderCell));
        for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
                yield return (x, z);
    }

    /// The piers for a bridge: as few as keep every span within what its deck can carry, each as near evenly spaced as it can be
    /// while standing clear of whatever runs underneath (with a metre to spare). A pier near what it stands beside runs along it,
    /// as one in a median does; otherwise square across the bridge.
    public static List<BridgePier> AutoPiers(BridgeGroup group, DeckKind deck, Func<float, (float Ax, float Az)> across)
    {
        var length = group.Length;
        var max = MaxSpan(deck);
        if (length <= max)
            return [];
        var half = MathF.Max(-group.Left, group.Right);

        bool Clear(float s)
        {
            if (s < MathF.Max(3f, group.StartClear) || s > length - MathF.Max(3f, group.EndClear))
                return false;
            var (ax, az) = Direction(group, s, across);
            foreach (var under in group.Under)
            {
                // How far along the bridge the pier's footprint reaches into this one's: by its own width, and by the deck's width
                // where the pier isn't running the same way.
                var along = MathF.Abs(ax * under.Dx + az * under.Dz);
                var skew = MathF.Sqrt(MathF.Max(0f, 1f - along * along));
                var reach = (under.Half + 1.6f) / under.Sine + half * skew / under.Sine;
                if (MathF.Abs(s - under.S) < reach)
                    return false;
            }

            return true;
        }

        for (var count = (int)MathF.Ceiling(length / max) - 1; count <= (int)MathF.Ceiling(length / max) * 3 + 2; count++)
        {
            var piers = new List<float>();
            var spacing = length / (count + 1);
            var ok = true;
            for (var k = 1; k <= count && ok; k++)
            {
                var ideal = spacing * k;
                float? found = null;
                for (var step = 0f; step <= spacing * 0.5f; step += 0.5f)
                {
                    if (Clear(ideal + step))
                        found = ideal + step;
                    else if (Clear(ideal - step))
                        found = ideal - step;
                    if (found is not null)
                        break;
                }

                if (found is not { } at || (piers.Count > 0 && at <= piers[^1] + 2f))
                    ok = false;
                else
                    piers.Add(at);
            }

            if (!ok)
                continue;
            var stops = new List<float> { 0f };
            stops.AddRange(piers);
            stops.Add(length);
            if (stops.Zip(stops.Skip(1)).All(p => p.Second - p.First <= max * 1.1f))
                return [.. piers.Select(s => { var (ax, az) = Direction(group, s, across); return new BridgePier(s, ax, az); })];
        }

        // Nowhere clear at an even spacing: piers only where they're clear (never in a road or on a track), as far apart as the deck
        // allows; where there's nowhere clear within that, the deck spans further to the next clear place.
        var clear = new List<float>();
        for (var s = 3f; s <= length - 3f; s += 0.5f)
            if (Clear(s))
                clear.Add(s);
        var chosen = new List<float>();
        var from = 0f;
        while (length - from > max)
        {
            var within = clear.Where(s => s > from + 2f && s <= from + max).ToList();
            var next = within.Count > 0 ? within.Max() : clear.Where(s => s > from + 2f).DefaultIfEmpty(float.NaN).Min();
            if (float.IsNaN(next))
                break;
            chosen.Add(next);
            from = next;
        }

        return [.. chosen.Select(s => { var (ax, az) = Direction(group, s, across); return new BridgePier(s, ax, az); })];
    }

    /// The way a pier at s runs: along what it's nearest beside (within 25 m, and not crossing the bridge at too steep a skew),
    /// else square across the bridge.
    public static (float Ax, float Az) Direction(BridgeGroup group, float s, Func<float, (float Ax, float Az)> across)
    {
        var (rx, rz) = across(s);
        BridgeUnder? near = null;
        foreach (var under in group.Under)
            if (MathF.Abs(under.S - s) < 25f && (near is null || MathF.Abs(under.S - s) < MathF.Abs(near.Value.S - s)))
                near = under;
        if (near is not { } u || u.Sine < 0.5f)
            return (rx, rz);
        var sign = u.Dx * rx + u.Dz * rz < 0f ? -1f : 1f;
        return (u.Dx * sign, u.Dz * sign);
    }
}
