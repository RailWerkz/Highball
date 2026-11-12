namespace Highball.World;

/// What a bridge's end stands on.
/// Bank: the ground as it comes (a bridge over a river or a valley, with nothing mapped under its ends).
/// Slope: a spill-through end, the bank running down under the deck at SpillRun to one, paved, with the deck on a seat at its top.
/// Wall: a full-height abutment down to what's underneath, its wing walls in line with it holding the bank back, where there's no
/// room for a slope before the road or track below.
/// Joined: the deck carries straight on into another line's bridge (lanes merging or parting on it): no end at all.
public enum BridgeEndKind { Bank, Slope, Wall, Joined }

/// One end of a line's bridged stretch: its point, which end it is, what it stands on, how far it is along the deck (metres, from the
/// end) to the edge of what runs underneath, the height of that, and how far out each side its wing walls reach.
/// Under: the road or track it stands beside (a slope's toe and a wall's face run along it), how far along it the deck crosses, and
/// which side of it the end is (+1 its right).
/// Footprint: how far along what's underneath the deck reaches either side of where its middle crosses.
public readonly record struct BridgeEnd(int Point, bool Start, BridgeEndKind Kind, float Run, float Floor, float Wing, NetworkLine? Under = null, float UnderS = 0f, int Side = 0,
    float Footprint = 0f)
{
    /// Where the deck crosses what's underneath (world x, z), its way there, and its right.
    public (double X, double Z, float Fx, float Fz, float Rx, float Rz) UnderFrame()
    {
        var u = Under!;
        var i = Array.BinarySearch(u.S, UnderS);
        i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, u.Count - 2);
        var t = Math.Clamp((UnderS - u.S[i]) / MathF.Max(u.S[i + 1] - u.S[i], 1e-4f), 0f, 1f);
        var (dx, dz) = ((float)(u.X[i + 1] - u.X[i]), (float)(u.Z[i + 1] - u.Z[i]));
        var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-5f);
        (dx, dz) = (dx / length, dz / length);
        return (u.X[i] + (u.X[i + 1] - u.X[i]) * t, u.Z[i] + (u.Z[i + 1] - u.Z[i]) * t, dx, dz, -dz, dx);
    }

    /// How far a place is from what's underneath, square across it, on this end's side (negative: the other side).
    public float FromUnder(double x, double z)
    {
        var (ux, uz, _, _, rx, rz) = UnderFrame();
        return (float)((x - ux) * rx + (z - uz) * rz) * Side;
    }
}

/// Choosing each bridge end's kind, from what crosses under the deck nearest that end: a slope where the bank has room to come down to
/// what's underneath at SpillRun to one past a SeatWidth seat (and a shoulder's width short of it), a wall where it hasn't.
public sealed partial class NetworkLines
{
    /// A paved slope's run per metre of fall (as the fill slopes are).
    public const float SpillRun = 1.5f;
    /// How far under the deck the seat at a slope's top reaches, before the slope falls away.
    public const float SeatWidth = 1.5f;
    /// How far into the gap a wall's face stands from the deck's end.
    public const float WallFace = 1.5f;
    /// Room kept between an end's slope or wall and the edge of a road or track running underneath.
    private const float UnderShoulder = 1.0f;
    public const float UnderShoulderOf = UnderShoulder;

    /// A wall end's wall: its face this far past the paving of what runs underneath, this thick, and running at most so far along it either
    /// way from the deck.
    public const float WallOff = 0.8f;
    public const float WallThick = 0.6f;
    public const float WallMost = 80f;

    /// How thick a slope's paving slab is: the bank's cut this far under its top.
    public const float SlabDepth = 0.35f;

    /// The top of a slope end's paving at a place under it: a plane along what's underneath, rising from a shoulder's width off its edge
    /// at SpillRun to one, up to the seat just under the deck's beams.
    public float SlopeAt(NetworkLine line, BridgeEnd end, double x, double z)
    {
        var off = end.FromUnder(x, z) - end.Under!.Formation - UnderShoulder;
        var top = line.Y[end.Point] - BridgePlanner.DeckDepth(line) - BridgeClearance;
        return MathF.Min(top, end.Floor - RoadClearance + MathF.Max(off, 0f) / SpillRun);
    }

    private Dictionary<NetworkLine, List<BridgeEnd>>? _bridgeEnds;
    private Dictionary<NetworkLine, List<BridgeEnd>>? _wallsBeside;

    /// Behind a wall's face, how deep its body is: its own thickness and, over that, the strip let down behind it (WallTrench samples),
    /// which it covers, so the terrain's triangles rise behind it and never through its face (they're too coarse to start at its back).
    private const float WallTrench = 1.5f;
    public float WallBody => WallThick + WallTrench * _spacing;

    /// The wall ends standing beside each line (that line their Under).
    private Dictionary<NetworkLine, List<BridgeEnd>> WallsBeside()
    {
        if (_wallsBeside is null)
        {
            BridgeEndsOf(_lines[0]);
            lock (_bridgeEndsLock)
                _wallsBeside ??= _bridgeEnds!.Values.SelectMany(e => e).Where(e => e.Kind == BridgeEndKind.Wall && e.Under is not null)
                    .GroupBy(e => e.Under!).ToDictionary(g => g.Key, g => g.ToList());
        }

        return _wallsBeside;
    }

    private readonly object _bridgeEndsLock = new();

    /// Lets down the strip each wall's body covers, beside each line in the tile, to the road's level.
    private void TrenchWalls(List<int> list, float[] heights, int samples, int gx0, int gz0)
    {
        if (_lines.Count == 0)
            return;
        var walls = WallsBeside();
        foreach (var index in list)
        {
            var line = _lines[index];
            if (!walls.TryGetValue(line, out var ends))
                continue;
            foreach (var end in ends)
            {
                var most = MathF.Min(WallMost, end.Footprint + end.Wing + 4f);
                var (from, to) = (end.UnderS - most, end.UnderS + most);
                for (var p = 0; p + 1 < line.Count; p++)
                {
                    if (line.S[p + 1] < from || line.S[p] > to || line.Structures[p] != Structure.Ground)
                        continue;
                    var face = (line.Rail ? line.Formation : PavedHalf(line, p)) + WallOff;
                    var reach = face + WallBody;
                    var r = reach / _spacing;
                    var (ax, az, bx, bz) = (line.X[p] / _spacing - gx0, line.Z[p] / _spacing - gz0, line.X[p + 1] / _spacing - gx0, line.Z[p + 1] / _spacing - gz0);
                    var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                    var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                    var (dx, dz) = (bx - ax, bz - az);
                    var length2 = dx * dx + dz * dz;
                    if (length2 <= 0)
                        continue;
                    var length = Math.Sqrt(length2);
                    for (var z = z0; z <= z1; z++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0);
                            // To the line's right is (-dz, dx): the wall's side only.
                            var side = ((x - ax) * -dz + (z - az) * dx) / length * end.Side;
                            var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                            var d = (float)Math.Sqrt(ex * ex + ez * ez) * _spacing;
                            var along = line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t;
                            if (side <= 0 || d > reach || d < face - WallOff || along < from || along > to)
                                continue;
                            var y = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t - RoadClearance;
                            var sample = z * samples + x;
                            if (heights[sample] > y)
                                heights[sample] = y;
                        }
                }
            }
        }
    }

    /// Every end of a line's bridged stretches.
    public IReadOnlyList<BridgeEnd> BridgeEndsOf(NetworkLine line)
    {
        if (_bridgeEnds is null)
            lock (_bridgeEndsLock)
                _bridgeEnds ??= FindBridgeEnds();
        return _bridgeEnds.TryGetValue(line, out var ends) ? ends : [];
    }

    /// A line's bridge end at a point, if it has one there.
    public BridgeEnd? BridgeEndAt(NetworkLine line, int point)
    {
        foreach (var end in BridgeEndsOf(line))
            if (end.Point == point)
                return end;
        return null;
    }

    private Dictionary<NetworkLine, List<BridgeEnd>> FindBridgeEnds()
    {
        var atNode = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                if (!atNode.TryGetValue(node, out var list))
                    atNode[node] = list = [];
                list.Add((line, point));
            }

        static bool Bridged(NetworkLine line, int point) =>
            (point > 0 && line.Structures[point - 1] == Structure.Bridge) || (point < line.Structures.Length && line.Structures[point] == Structure.Bridge);

        var found = new System.Collections.Concurrent.ConcurrentDictionary<NetworkLine, List<BridgeEnd>>();
        var others = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        Parallel.ForEach(_lines.Where(l => l.Structures.Contains(Structure.Bridge)), line =>
        {
            var ends = new List<BridgeEnd>();
            var depth = BridgePlanner.DeckDepth(line);
            var deckHalf = BridgePlanner.DeckHalf(line);
            var shared = line.Nodes.Select(n => n.Node).ToHashSet();
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;

                // What crosses under the stretch, square enough to be under it: where along the deck, how far it reaches along the deck
                // either side (its width at the angle, and the deck's), and its height there.
                var under = new List<(float S, float Reach, float Y, NetworkLine Line, float LineS, float Footprint)>();
                if (!line.Mark)
                {
                    var (bx0, bx1, bz0, bz1) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
                    for (var q = first; q <= last; q++)
                        (bx0, bx1, bz0, bz1) = (Math.Min(bx0, line.X[q]), Math.Max(bx1, line.X[q]), Math.Min(bz0, line.Z[q]), Math.Max(bz1, line.Z[q]));
                    foreach (var other in others)
                    {
                        if (other == line || other.MaxX < bx0 || other.MinX > bx1 || other.MaxZ < bz0 || other.MinZ > bz1 || other.Nodes.Any(n => shared.Contains(n.Node)))
                            continue;
                        for (var i = 0; i + 1 < other.Count; i++)
                        {
                            if (other.Structures[i] != Structure.Ground)
                                continue;
                            for (var q = first; q < last; q++)
                            {
                                if (Crossing(other, i, line, q) is not { } hit || hit.Sine < 0.3f)
                                    continue;
                                var s = line.S[q] + (line.S[q + 1] - line.S[q]) * hit.T;
                                var ot = (float)OtherT(other, i, line, q);
                                var y = other.Y[i] + (other.Y[i + 1] - other.Y[i]) * ot;
                                var reach = (other.Formation + UnderShoulder) / hit.Sine + deckHalf * hit.Cosine / hit.Sine;
                                under.Add((s, reach, y, other, other.S[i] + (other.S[i + 1] - other.S[i]) * ot, deckHalf / hit.Sine + 25f * hit.Cosine));
                            }
                        }
                    }
                }

                foreach (var (point, start) in (ReadOnlySpan<(int, bool)>)[(first, true), (last, false)])
                {
                    var joined = line.Nodes.Where(n => n.Point == point).Any(n => atNode.TryGetValue(n.Node, out var users) && users.Any(u => u.Line != line && Bridged(u.Line, u.Point)));
                    if (joined)
                    {
                        ends.Add(new BridgeEnd(point, start, BridgeEndKind.Joined, 0f, line.Y[point], 0f));
                        continue;
                    }

                    if (under.Count == 0)
                    {
                        ends.Add(new BridgeEnd(point, start, BridgeEndKind.Bank, 0f, line.Ground[point], 0f));
                        continue;
                    }

                    // The nearest edge of what's under it, from this end; and how far the end is from it, square across it.
                    var nearest = start ? under.MinBy(u => u.S - u.Reach) : under.MaxBy(u => u.S + u.Reach);
                    var run = start ? nearest.S - nearest.Reach - line.S[first] : line.S[last] - (nearest.S + nearest.Reach);
                    var floor = nearest.Y;
                    var probe = new BridgeEnd(point, start, BridgeEndKind.Wall, 0f, floor, 0f, nearest.Line, nearest.LineS, 1);
                    var away = probe.FromUnder(line.X[point], line.Z[point]);
                    var side = away >= 0f ? 1 : -1;
                    var room = MathF.Abs(away) - nearest.Line.Formation - UnderShoulder;
                    var fall = line.Y[point] - depth - floor;
                    var kind = room >= fall * SpillRun + 1f ? BridgeEndKind.Slope : BridgeEndKind.Wall;
                    var wing = Math.Clamp((line.Y[point] - floor) * SpillRun, 1.5f, 25f);
                    ends.Add(new BridgeEnd(point, start, kind, MathF.Max(run, 0f), floor, wing, nearest.Line, nearest.LineS, side, nearest.Footprint));
                }
            }

            found[line] = ends;
        });

        var counts = found.Values.SelectMany(e => e).GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        RailWerkz.Core.Logging.Log.Info($"Bridge ends: {string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {c.Key.ToString().ToLowerInvariant()}"))}");
        return new Dictionary<NetworkLine, List<BridgeEnd>>(found);
    }
}
