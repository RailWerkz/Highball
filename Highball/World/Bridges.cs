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
    public float Left => Members.Min(m => m.Offset - m.Half);
    public float Right => Members.Max(m => m.Offset + m.Half);

    /// What runs underneath, found when the bridge is.
    public List<BridgeUnder> Under { get; } = [];

    /// The piers worked out for it (until something's changed).
    public List<BridgePier>? AutoPiers { get; set; }
}

/// Finding the route's bridges and placing their piers.
public static class BridgePlanner
{
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

            groups.Add(new BridgeGroup
            {
                Key = key, Reference = reference.Line, First = reference.First, Last = reference.Last,
                Members = [.. members.Select(m => (m.Line, m.First, m.Last, (float)((m.Mx - reference.Mx) * rx + (m.Mz - reference.Mz) * rz), m.Half))],
                Rail = members.Any(m => m.Line.Rail || m.Line.Mark), X = reference.Mx, Z = reference.Mz,
            });
        }

        foreach (var group in groups)
            FindUnder(group, lines);
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

    /// What runs under a bridge: every grounded road or track whose middle line crosses the reference's over the bridge.
    private static void FindUnder(BridgeGroup group, NetworkLines lines)
    {
        var reference = group.Reference;
        var carried = group.Members.Select(m => m.Line).ToHashSet();
        var (minX, maxX, minZ, maxZ) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
        for (var p = group.First; p <= group.Last; p++)
            (minX, maxX, minZ, maxZ) = (Math.Min(minX, reference.X[p]), Math.Max(maxX, reference.X[p]), Math.Min(minZ, reference.Z[p]), Math.Max(maxZ, reference.Z[p]));

        foreach (var line in lines.Lines)
        {
            if (carried.Contains(line) || line.MaxX < minX || line.MinX > maxX || line.MaxZ < minZ || line.MinZ > maxZ)
                continue;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                if (line.Structures[i] != Structure.Ground)
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
            if (s < 3f || s > length - 3f)
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
