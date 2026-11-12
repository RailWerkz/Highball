using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Bridges as one structure each (see BridgeGroup): a deck across everything carried side by side, girders or a concrete beam the
/// full width under it, railings along its outer edges only, piers where they're clear of what runs underneath (or where they've
/// been put), and an abutment each end with retaining walls set as asked. Built whole in the chunk its middle's in.
public sealed partial class NetworkMeshes
{
    private List<BridgeGroup> _bridges = [];
    private readonly Dictionary<ChunkKey, List<BridgeGroup>> _bridgesByChunk = [];

    /// The kit's detail parts (columns, caps, pier walls, bearings), if it's been built (Tools/Blender/bridges.py).
    private Dictionary<string, Model.Part>? _bridgeKit;

    public IReadOnlyList<BridgeGroup> Bridges => _bridges;

    private BridgeEdit EditOf(BridgeGroup group) => _source.Bridges.GetValueOrDefault(group.Key) ?? new BridgeEdit(group.Key);

    private void FindBridges()
    {
        _bridges = BridgePlanner.Find(_lines);
        _bridgesByChunk.Clear();
        foreach (var group in _bridges)
        {
            var key = KeyAt(group.X, group.Z);
            if (!_bridgesByChunk.TryGetValue(key, out var list))
                _bridgesByChunk[key] = list = [];
            list.Add(group);
        }
    }

    /// One bridge's edit changed: its piers are worked out again, and its chunk built again.
    public void BridgeChanged(long key)
    {
        foreach (var group in _bridges.Where(g => g.Key == key))
        {
            group.AutoPiers = null;
            var chunk = KeyAt(group.X, group.Z);
            _stale.Add((chunk, false));
        }
    }

    /// The bridge whose middle line passes nearest (x, z), within so many metres.
    public BridgeGroup? BridgeAt(double x, double z, double within)
    {
        (BridgeGroup?, double) best = (null, within * within);
        foreach (var group in _bridges)
        {
            var line = group.Reference;
            for (var p = group.First; p < group.Last; p++)
            {
                var (ax, az, bx, bz) = (line.X[p], line.Z[p], line.X[p + 1], line.Z[p + 1]);
                var (dx, dz) = (bx - ax, bz - az);
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                var away = ex * ex + ez * ez;
                // (Anywhere across its deck counts.)
                var reach = Math.Max(0.0, Math.Sqrt(away) - Math.Max(-group.Left, group.Right));
                if (reach * reach < best.Item2)
                    best = (group, reach * reach);
            }
        }

        return best.Item1;
    }

    /// The square-across direction at s along a bridge.
    private static (float Ax, float Az) AcrossAt(BridgeGroup group, float s)
    {
        var (_, _, _, _, _, rx, rz) = new Leg(group.Reference, 0, 1).At(group.From + s);
        return (rx, rz);
    }

    /// Where a bridge's piers stand: as put by hand, or placed automatically.
    public List<BridgePier> PiersOf(BridgeGroup group)
    {
        var edit = EditOf(group);
        if (edit.Piers is { } placed)
            return [.. placed.Where(s => s > 0.5f && s < group.Length - 0.5f).Order().Select(s =>
            {
                var (ax, az) = BridgePlanner.Direction(group, s, t => AcrossAt(group, t));
                return new BridgePier(s, ax, az);
            })];
        return group.AutoPiers ??= BridgePlanner.AutoPiers(group, edit.Deck, s => AcrossAt(group, s));
    }

    /// A place on a bridge's middle line, s metres along it: world x, z, and the deck's underside there.
    public (double X, double Z, float Underside) BridgePlace(BridgeGroup group, float s)
    {
        var (x, z, y, _, _, _, _) = new Leg(group.Reference, 0, 1).At(group.From + s);
        return (x, z, y - BridgePlanner.DeckDepth(group.Reference));
    }

    /// How far along a bridge's middle line the place nearest (x, z) is.
    public static float AlongBridge(BridgeGroup group, double x, double z)
    {
        var line = group.Reference;
        var (best, at) = (double.MaxValue, 0f);
        for (var p = group.First; p < group.Last; p++)
        {
            var (ax, az, bx, bz) = (line.X[p], line.Z[p], line.X[p + 1], line.Z[p + 1]);
            var (dx, dz) = (bx - ax, bz - az);
            var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
            var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
            if (ex * ex + ez * ez < best)
                (best, at) = (ex * ex + ez * ez, line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t - group.From);
        }

        return at;
    }

    /// The deck's reach across, room and all.
    private (float Left, float Right) DeckAcross(BridgeGroup group, BridgeEdit edit)
    {
        var unit = group.Rail ? NetworkLine.TrackSpacing : 3.65f;
        return (group.Left - edit.RoomLeft * unit, group.Right + edit.RoomRight * unit);
    }

    private void BuildBridge(Builder builder, BridgeGroup group)
    {
        var edit = EditOf(group);
        var line = group.Reference;
        var (first, last) = (group.First, group.Last);
        var (left, right) = DeckAcross(group, edit);
        var depth = BridgePlanner.DeckDepth(line);
        var edge = group.Rail ? NetworkLine.BallastHeight + 0.1f : 0.3f;
        var surface = group.Rail ? 0.05f : RoadRise - 0.01f;
        const float deckBottom = -0.25f;
        var steel = Code(LineMaterial.Steel);
        var galvanised = Code(LineMaterial.Steel, 1);
        var concrete = Code(LineMaterial.Concrete);
        var concreteData = new float4(0f, 0f, concrete, 0f);

        // What carries the deck: plate girders evenly across, or a concrete beam the full width, its sides tapering in underneath.
        var girders = new List<float>();
        if (edit.Deck == DeckKind.Steel)
        {
            var count = Math.Max(2, (int)MathF.Ceiling((right - left - 0.5f) / GirderGap) + 1);
            for (var g = 0; g < count; g++)
                girders.Add(left + 0.25f + (right - left - 0.5f) * g / (count - 1));
            foreach (var u in girders)
                Sweep(builder, line, first, last, IBeam(u, deckBottom, -depth), _ => steel, (_, _, _) => 0f, capped: true);
        }
        else
        {
            var taper = MathF.Min(0.9f, (right - left) * 0.15f);
            Sweep(builder, line, first, last,
            [
                (left, deckBottom), (left, -depth * 0.45f), (left + taper, -depth), (right - taper, -depth), (right, -depth * 0.45f), (right, deckBottom), (left, deckBottom),
            ], _ => concrete, (_, _, _) => 0f, capped: true);
        }

        // The deck between and beside what it carries (each carries its own surface, but for a guide's strip): a slab, its top just under theirs.
        var covered = (group.Covered ?? group.Members.Where(m => !m.Line.Mark).Select(m => (From: m.Offset - m.Half, To: m.Offset + m.Half)).ToList()).OrderBy(c => c.From).ToList();
        var gaps = new List<(float From, float To)>();
        var at = left;
        foreach (var (from, to) in covered)
        {
            if (from > at + 0.05f)
                gaps.Add((at, from));
            at = MathF.Max(at, to);
        }

        if (right > at + 0.05f)
            gaps.Add((at, right));
        foreach (var (from, to) in gaps)
            Sweep(builder, line, first, last, [(from, deckBottom), (from, surface), (to, surface), (to, deckBottom), (from, deckBottom)], _ => concrete, (_, _, _) => 0f, capped: true);

        // Along the outer edges only: on a railway, railings; on a road, a concrete parapet with a rail along its top.
        var road = !group.Rail;
        if (road)
        {
            Sweep(builder, line, first, last,
                [(left, deckBottom - 0.15f), (left, edge + ParapetHeight), (left + 0.25f, edge + ParapetHeight), (left + 0.3f, edge + 0.25f), (left + 0.45f, edge), (left + 0.45f, deckBottom), (left, deckBottom - 0.15f)],
                _ => concrete, (_, _, _) => 0f, capped: true);
            Sweep(builder, line, first, last,
                [(right - 0.45f, deckBottom), (right - 0.45f, edge), (right - 0.3f, edge + 0.25f), (right - 0.25f, edge + ParapetHeight), (right, edge + ParapetHeight), (right, deckBottom - 0.15f), (right - 0.45f, deckBottom)],
                _ => concrete, (_, _, _) => 0f, capped: true);
            foreach (var u in (float[])[left + 0.12f, right - 0.12f])
                Sweep(builder, line, first, last, Tube(u, edge + ParapetHeight + ParapetRail, 0.045f), _ => galvanised, (_, _, _) => 0f);
        }
        else
            foreach (var u in (float[])[left + 0.08f, right - 0.08f])
                foreach (var height in (float[])[0.4f, 0.72f, RailingHeight])
                    Sweep(builder, line, first, last, Tube(u, edge + height, 0.04f), _ => galvanised, (_, _, _) => 0f);

        void Along(float gap, Action<float3, float3, float3> place)
        {
            for (var s = MathF.Ceiling(line.S[first] / gap) * gap; s < line.S[last]; s += gap)
            {
                var (center, r) = FrameAt(line, s, builder.Origin);
                place(center, r, new float3(r.Z, 0f, -r.X));
            }
        }

        var bright = new float4(0f, 0f, galvanised, 0f);
        var steelData = new float4(0f, 0f, steel, 0f);
        void Post(float3 center, float3 r, float3 forward)
        {
            if (road)
                foreach (var u in (float[])[left + 0.12f, right - 0.12f])
                    Solid(builder, center + r * u, r * 0.035f, forward * 0.05f, center.Y + edge + ParapetHeight, center.Y + edge + ParapetHeight + ParapetRail + 0.04f, bright);
            else
                foreach (var u in (float[])[left + 0.08f, right - 0.08f])
                    Solid(builder, center + r * u, r * 0.05f, forward * 0.05f, center.Y + edge, center.Y + edge + RailingHeight + 0.05f, bright);
        }

        Along(RailPostGap, Post);
        if (edit.Deck == DeckKind.Steel)
        {
            var (outerLeft, outerRight) = (girders[0], girders[^1]);
            Along(StiffenerGap, (center, r, forward) =>
            {
                foreach (var u in (float[])[outerLeft - 0.11f, outerLeft + 0.11f, outerRight - 0.11f, outerRight + 0.11f])
                    Solid(builder, center + r * u, r * 0.1f, forward * 0.018f, center.Y - depth + 0.03f, center.Y + deckBottom - 0.03f, steelData);
            });
            Along(CrossFrameGap, (center, r, forward) =>
            {
                for (var g = 0; g + 1 < girders.Count; g++)
                {
                    var middle = (girders[g] + girders[g + 1]) * 0.5f;
                    Solid(builder, center + r * middle, r * ((girders[g + 1] - girders[g]) * 0.5f), forward * 0.05f, center.Y - depth * 0.75f, center.Y - depth * 0.35f, steelData);
                }
            });
        }

        // Each end: the deck closed with a plate, a post at each railing's end, and the abutment.
        foreach (var (s, inward) in new[] { (line.S[first], 1f), (line.S[last], -1f) })
        {
            // Where the deck carries on into another line's bridge, a pier under the joint, and no end.
            if (inward > 0f ? group.StartJoined : group.EndJoined)
            {
                var joint = Math.Clamp(s - group.From + inward * 0.6f, 0f, group.Length);
                var (ax, az) = BridgePlanner.Direction(group, joint, t => AcrossAt(group, t));
                Pier(builder, group, new BridgePier(joint, ax, az), edit.Style != PierStyle.Auto ? edit.Style : group.Rail ? PierStyle.Wall : PierStyle.Columns, left, right, depth, girders);
                continue;
            }

            var (center, r) = FrameAt(line, s, builder.Origin);
            var forward = new float3(r.Z, 0f, -r.X);
            Post(center, r, forward);
            Solid(builder, center + r * ((left + right) * 0.5f), r * ((right - left) * 0.5f), forward * 0.04f, center.Y + deckBottom, center.Y + surface - 0.02f, concreteData);
            var start = inward > 0f;
            Abutment(builder, group, s, inward, left, right, depth, edge, start ? edit.StartWing : edit.EndWing, start ? edit.StartFlare : edit.EndFlare,
                _lines.BridgeEndAt(line, start ? first : last));
        }

        // The piers.
        var style = edit.Style != PierStyle.Auto ? edit.Style : group.Rail ? PierStyle.Wall : PierStyle.Columns;
        foreach (var pier in PiersOf(group))
            Pier(builder, group, pier, style, left, right, depth, girders);
    }

    /// One pier: down from just under the deck to below the ground, across the deck's width the way it runs.
    private void Pier(Builder builder, BridgeGroup group, BridgePier pier, PierStyle style, float left, float right, float depth, List<float> girders)
    {
        var (center, r) = FrameAt(group.Reference, group.From + pier.S, builder.Origin);
        var along = new float3(pier.Ax, 0f, pier.Az);
        // Turned to run along what it's beside, it has to reach further to span the deck.
        var square = MathF.Max(MathF.Abs(along.X * r.X + along.Z * r.Z), 0.6f);
        var forward = new float3(-along.Z, 0f, along.X);
        if (forward.X * r.Z - forward.Z * r.X < 0f)
            forward *= -1f;
        var middle = center + r * ((left + right) * 0.5f);
        var halfLength = (right - left) * 0.5f / square;
        var top = center.Y - depth;

        // The ground under it: the lowest along its length, so it never stops short of it.
        var ground = float.MaxValue;
        foreach (var f in (float[])[-1f, -0.5f, 0f, 0.5f, 1f])
        {
            var p = middle + along * (halfLength * f);
            ground = MathF.Min(ground, _source.HeightAt(p.X + builder.Origin.X, p.Z + builder.Origin.Z));
        }

        if (top - ground < 0.6f)
            return;
        var bottom = ground - 1f;
        const float capDepth = 0.8f;
        var capTop = top;
        var capBottom = top - capDepth;
        var concrete = Code(LineMaterial.Concrete);
        var data = new float4(0f, 0f, concrete, 0f);
        var up = new float3(0f, 1f, 0f);

        switch (style)
        {
            case PierStyle.Wall:
                if (!Kit(builder, "Wall_Concrete", middle with { Y = bottom }, along, up, forward, new float3(halfLength * 2f - 0.8f, capBottom - bottom, 1.3f)))
                    Solid(builder, middle, along * (halfLength - 0.4f), forward * 0.65f, bottom, capBottom, data);
                break;
            case PierStyle.Column:
                if (!Kit(builder, "Column_Concrete", middle with { Y = bottom }, along, up, forward, new float3(2.2f, capBottom - bottom, 2.2f)))
                    Solid(builder, middle, along * 1.1f, forward * 1.1f, bottom, capBottom, data);
                break;
            default:
                var columns = Math.Max(2, (int)MathF.Ceiling(halfLength * 2f / 7f));
                for (var c = 0; c < columns; c++)
                {
                    var u = -halfLength + 1.2f + (halfLength * 2f - 2.4f) * c / (columns - 1);
                    var foot = middle + along * u;
                    if (!Kit(builder, "Column_Concrete", foot with { Y = bottom }, along, up, forward, new float3(1.3f, capBottom - bottom, 1.3f)))
                        Solid(builder, foot, along * 0.65f, forward * 0.65f, bottom, capBottom, data);
                }

                break;
        }

        // The cap across it, the full width, and a bearing under each girder.
        var capLength = style == PierStyle.Column ? halfLength * 2f : halfLength * 2f + 0.2f;
        if (!Kit(builder, "Cap_Concrete", middle with { Y = capBottom }, along, up, forward, new float3(capLength, capDepth, 1.6f)))
            Solid(builder, middle, along * (capLength * 0.5f), forward * 0.8f, capBottom, capTop, data);
        foreach (var u in girders)
        {
            var seat = center + r * u;
            if (!Kit(builder, "Bearing_Black", seat with { Y = capTop }, along, up, forward, new float3(0.5f, 0.12f, 0.4f)))
                Solid(builder, seat, along * 0.25f, forward * 0.2f, capTop, capTop + 0.12f, new float4(0f, 0f, PaintOf("Fixed_Black"), 0f));
        }
    }

    /// A road bridge's parapet: how high it stands over the deck's edge, and its rail over that.
    private const float ParapetHeight = 0.85f;
    private const float ParapetRail = 0.3f;

    /// An abutment under one end of the deck, as the end's kind has it (see BridgeEndKind).
    /// Wall: a full-height wall from below what runs underneath up to the beams' seat, a back wall over that up to the deck, and wing
    /// walls in line with its face out each side, their tops falling with the bank behind them to the ground.
    /// Slope: a seat beam at the top of the bank, a back wall up to the deck, and the slope under the deck paved down to its toe.
    /// Bank (or no kind worked out): a seat and wall down to the ground there, and short wings flared as asked.
    private void Abutment(Builder builder, BridgeGroup group, float s, float inward, float left, float right, float depth, float edge, float? wing, float flare, BridgeEnd? end)
    {
        var line = group.Reference;
        var (center, r) = FrameAt(line, s, builder.Origin);
        // Into the gap, from this end.
        var forward = new float3(r.Z, 0f, -r.X) * inward;
        var world = (X: center.X + builder.Origin.X, Z: center.Z + builder.Origin.Z);
        var top = center.Y - depth;
        var middle = center + r * ((left + right) * 0.5f);
        var half = (right - left) * 0.5f + 0.1f;
        var data = new float4(0f, 0f, Code(LineMaterial.Concrete), 0f);
        var settled = center.Y + (group.Rail ? 0f : RoadRise) + 0.1f;
        var kind = end?.Kind ?? BridgeEndKind.Bank;

        // The back wall, up to the deck, behind the beams' ends.
        void BackWall(float bottom) => Solid(builder, middle - forward * 0.35f, r * half, forward * 0.35f, bottom, center.Y - 0.05f, data);

        if (kind is BridgeEndKind.Wall or BridgeEndKind.Slope)
        {
            // The seat: a beam across under the beams' ends, and the back wall up to the deck behind them.
            var seatBottom = top - 1.4f;
            Solid(builder, middle + forward * 0.55f, r * half, forward * 0.55f, seatBottom, top, data);
            BackWall(seatBottom);
            // (A railway's slope is left as levelled earth, unpaved.)
            if (kind == BridgeEndKind.Wall)
                RetainingWall(builder, group, end!.Value, data);
            else if (!group.Rail)
                Paving(builder, group, middle, r, forward, half + 0.4f, 1.1f, end!.Value, data);
            return;
        }

        var ground = MathF.Min(_source.HeightAt(world.X, world.Z), _source.HeightAt(world.X + forward.X * 4f, world.Z + forward.Z * 4f));
        ground = MathF.Min(ground, top - 0.5f);
        Solid(builder, middle + forward * 0.6f, r * half, forward * 0.9f, ground - 1.5f, top, data);
        BackWall(ground - 1.5f);

        var length = wing ?? (group.Rail
            ? MathF.Max(line.BallastHalfTop + MathF.Max(0.35f, line.BedHeight * NetworkLine.BallastSlope * NetworkLine.BedRun) + NetworkLine.BedFade - (right - left) * 0.5f, 1f) + 1f
            : Math.Clamp((center.Y - ground) * 1.2f, 1.5f, 6f));
        var turn = Math.Clamp(flare, -45f, 80f) * MathF.PI / 180f;
        foreach (var side in (float[])[-1f, 1f])
        {
            var a = middle - forward * 0.1f + r * (side * half);
            // Out to the side, turned back into the bank by the flare.
            var outward = r * (side * MathF.Cos(turn)) - forward * MathF.Sin(turn);
            var b = a + outward * length;
            var low = MathF.Min(ground, _source.HeightAt(b.X + builder.Origin.X, b.Z + builder.Origin.Z)) - 1.5f;
            Wing(builder, a, b, 0.4f, low, center.Y + edge, settled, data);
        }
    }

    private const float WallOff = NetworkLines.WallOff;
    private const float WallThick = NetworkLines.WallThick;
    private const float WallStep = 2f;
    private const float WallMost = NetworkLines.WallMost;
    /// How far behind a wall's back the bank it holds is looked at (more than the terrain's sample spacing and a half).
    private const float WallLooks = 3.5f;
    /// How far past a deck's end a wall still rises to its seat (the wedge a skewed crossing leaves).
    private const float WallWedge = 25f;

    /// A wall end's wall: along the edge of what runs underneath, its face WallOff past its formation, under the deck up to the seat
    /// and out either side as a retaining wall, its top following the bank behind it until the bank's come down to the road (or
    /// halfway to the next bridge's wall along the same side, so two don't stand in each other).
    private void RetainingWall(Builder builder, BridgeGroup group, BridgeEnd end, float4 data)
    {
        var under = end.Under!;
        var origin = builder.Origin;
        var deck = group.Reference;
        var depth = BridgePlanner.DeckDepth(deck);
        var (left, right) = DeckAcross(group, EditOf(group));
        var endY = deck.Y[end.Point];

        // The deck's underside over a place, if it's over it.
        float? DeckOver(double x, double z)
        {
            var (best, at, y) = (double.MaxValue, 0.0, 0f);
            for (var p = group.First; p < group.Last; p++)
            {
                var (ax, az, dx, dz) = (deck.X[p], deck.Z[p], deck.X[p + 1] - deck.X[p], deck.Z[p + 1] - deck.Z[p]);
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                if (ex * ex + ez * ez < best)
                {
                    // (Across to the deck's right: the right of (dx, dz) is (-dz, dx).)
                    var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
                    (best, at, y) = (ex * ex + ez * ez, ((x - ax) * -dz + (z - az) * dx) / length, deck.Y[p] + (deck.Y[p + 1] - deck.Y[p]) * (float)t);
                }
            }

            // (Past the deck's end too, within its width: on a skew, the wedge between the square end and the wall along the road is the
            // abutment's, up to the seat.)
            return at >= left - 0.3 && at <= right + 0.3 && best < Math.Pow(Math.Max(-left, right) + WallWedge, 2) ? y - depth : null;
        }

        // As far each way as halfway to the next wall on this side of the same road.
        var (lowest, highest) = (end.UnderS - WallMost, end.UnderS + WallMost);
        foreach (var other in _bridges)
        {
            if (other == group)
                continue;
            foreach (var point in (int[])[other.First, other.Last])
                if (_lines.BridgeEndAt(other.Reference, point) is { Kind: BridgeEndKind.Wall } e && e.Under == under && e.Side == end.Side)
                {
                    if (e.UnderS < end.UnderS)
                        lowest = MathF.Max(lowest, (e.UnderS + end.UnderS) * 0.5f);
                    else if (e.UnderS > end.UnderS)
                        highest = MathF.Min(highest, (e.UnderS + end.UnderS) * 0.5f);
                }
        }

        float? lastCovered = null;
        (float3 Face, float3 Back, float Top, float Bottom)? At(float s, out bool stop, out bool covered)
        {
            stop = false;
            covered = false;
            var i = Array.BinarySearch(under.S, s);
            i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, under.Count - 2);
            var t = Math.Clamp((s - under.S[i]) / MathF.Max(under.S[i + 1] - under.S[i], 1e-4f), 0f, 1f);
            var (x, z) = (under.X[i] + (under.X[i + 1] - under.X[i]) * t, under.Z[i] + (under.Z[i + 1] - under.Z[i]) * t);
            var floor = under.Y[i] + (under.Y[i + 1] - under.Y[i]) * t;
            var (dx, dz) = ((float)(under.X[i + 1] - under.X[i]), (float)(under.Z[i + 1] - under.Z[i]));
            var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-5f);
            var (rx, rz) = (-dz / length * end.Side, dx / length * end.Side);
            var off = (under.Rail ? under.Formation : NetworkLines.PavedHalf(under, i)) + WallOff;
            var face = (X: x + rx * off, Z: z + rz * off);
            // (Its body covers the strip let down behind it: its back where the ground's left as it is.)
            var body = _lines.WallBody;
            var back = (X: face.X + rx * body, Z: face.Z + rz * body);
            float top;
            if (DeckOver(face.X, face.Z) is { } underside)
            {
                (top, covered) = (underside, true);
                lastCovered = s;
            }
            else
            {
                // (Past the deck, the approach's bank falls away at the slope's run: a cutting's own high ground beyond is left as it is.)
                var beyond = MathF.Abs(s - (lastCovered ?? end.UnderS));
                var bank = endY + 0.3f - beyond / NetworkLines.SpillRun;
                // (The highest of the ground just behind it: the terrain's triangles from the road up to that cross the wall's plane no
                // higher, so the wall hides them, and the bank meets its top.)
                var behind = float.MinValue;
                for (var look = 0.3f; look <= WallLooks; look += 0.8f)
                    behind = MathF.Max(behind, _source.HeightAt(back.X + rx * look, back.Z + rz * look));
                top = MathF.Min(behind + 0.15f, bank);
                if (top < floor + 0.6f)
                {
                    stop = true;
                    top = floor + 0.6f;
                }
            }

            return (new float3((float)(face.X - origin.X), 0f, (float)(face.Z - origin.Z)), new float3((float)(back.X - origin.X), 0f, (float)(back.Z - origin.Z)), top, floor - 1.2f);
        }

        var points = new List<(float3 Face, float3 Back, float Top, float Bottom)>();
        foreach (var way in (int[])[-1, 1])
        {
            lastCovered = null;
            var run = new List<(float3, float3, float, float)>();
            for (var k = way < 0 ? 0 : 1; k * WallStep <= WallMost; k++)
            {
                var s = end.UnderS + way * k * WallStep;
                if (s < 0f || s > under.S[^1])
                    break;
                if (At(s, out var stop, out var covered) is not { } place)
                    break;
                // (Past halfway to the next wall only while still under its own deck.)
                if ((s < lowest || s > highest) && !covered)
                    break;
                run.Add(place);
                if (stop)
                    break;
            }

            if (way < 0)
                run.Reverse();
            points.AddRange(run);
        }

        if (points.Count < 2)
            return;
        // Its top evened out along it (the ground behind is only as smooth as the terrain's samples), never under where it was.
        var tops = points.Select(p => p.Top).ToArray();
        for (var k = 1; k + 1 < points.Count; k++)
            points[k] = points[k] with { Top = MathF.Max(tops[k], (tops[k - 1] + tops[k] * 2f + tops[k + 1]) * 0.25f) };
        var up = new float3(0f, 1f, 0f);
        for (var k = 0; k + 1 < points.Count; k++)
        {
            var (f0, b0, t0, d0) = points[k];
            var (f1, b1, t1, d1) = points[k + 1];
            var outward = MathEx.Normalize(f0 - b0);
            var along = MathEx.Normalize(f1 - f0);
            void Quad(float3 p, float3 q, float pBottom, float qBottom, float pTop, float qTop, float3 normal)
            {
                var v0 = builder.Add(p with { Y = pBottom }, normal, data);
                var v1 = builder.Add(q with { Y = qBottom }, normal, data);
                var v2 = builder.Add(p with { Y = pTop }, normal, data);
                var v3 = builder.Add(q with { Y = qTop }, normal, data);
                builder.Indices.AddRange([v0, v1, v2, v1, v3, v2, v0, v2, v1, v1, v2, v3]);
            }

            Quad(f0, f1, d0, d1, t0, t1, outward);
            Quad(b0, b1, d0, d1, t0, t1, outward * -1f);
            // The top.
            var a0 = builder.Add(f0 with { Y = t0 }, up, data);
            var a1 = builder.Add(f1 with { Y = t1 }, up, data);
            var a2 = builder.Add(b0 with { Y = t0 }, up, data);
            var a3 = builder.Add(b1 with { Y = t1 }, up, data);
            builder.Indices.AddRange([a0, a1, a2, a1, a3, a2, a0, a2, a1, a1, a2, a3]);
            if (k == 0)
                Quad(f0, b0, d0, d0, t0, t0, along * -1f);
            if (k + 2 == points.Count)
                Quad(f1, b1, d1, d1, t1, t1, along);
        }
    }

    /// The paving on a slope under a deck's end: a concrete slab SlabDepth thick across the deck's width (half either side of its middle),
    /// from so far into the gap down to its toe a shoulder's width off what's underneath, where a low kerb runs along it. Its top is the
    /// slope's own plane (SlopeAt), flat, with the bank cut out under it to fit, and its edges show.
    private void Paving(Builder builder, BridgeGroup group, float3 middle, float3 r, float3 forward, float half, float from, BridgeEnd end, float4 data)
    {
        var origin = builder.Origin;
        var line = group.Reference;
        var limit = end.Under!.Formation + NetworkLines.UnderShoulderOf;
        float Off(float3 p) => end.FromUnder(p.X + origin.X, p.Z + origin.Z) - limit;
        float Top(float3 p) => _lines.SlopeAt(line, end, p.X + origin.X, p.Z + origin.Z);

        // Each column across: from the seat down to its toe.
        var columns = Math.Max(2, (int)MathF.Ceiling(half));
        var toes = new float[columns + 1];
        for (var j = 0; j <= columns; j++)
        {
            var bottom = middle + r * (-half + half * 2f * j / columns);
            var (o0, o1) = (Off(bottom + forward * from), Off(bottom + forward * (from + 1f)));
            // (Off falls as it goes into the gap: to nothing at the toe.)
            toes[j] = o0 - o1 > 1e-3f ? from + o0 / (o0 - o1) : from;
        }

        if (toes.Max() - from < 1f)
            return;
        // (Rows half a metre apart, so the fold where the slope meets the level top under the seat stays sharp.)
        var rows = Math.Max(2, (int)MathF.Ceiling((toes.Max() - from) * 2f));
        var grid = new float3[rows + 1, columns + 1];
        for (var i = 0; i <= rows; i++)
            for (var j = 0; j <= columns; j++)
            {
                var into = from + MathF.Max(toes[j] - from, 0f) * i / rows;
                var p = middle + r * (-half + half * 2f * j / columns) + forward * into;
                grid[i, j] = p with { Y = Top(p) };
            }

        var start = (uint)builder.Vertices.Count;
        for (var i = 0; i <= rows; i++)
            for (var j = 0; j <= columns; j++)
            {
                var (i0, i1, j0, j1) = (Math.Max(i - 1, 0), Math.Min(i + 1, rows), Math.Max(j - 1, 0), Math.Min(j + 1, columns));
                var (u, v) = (grid[i, j1] - grid[i, j0], grid[i1, j] - grid[i0, j]);
                var normal = MathEx.Normalize(new float3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X));
                if (normal.Y < 0f)
                    normal *= -1f;
                builder.Add(grid[i, j], normal, data);
            }

        var stride = (uint)(columns + 1);
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < columns; j++)
            {
                var a = start + (uint)i * stride + (uint)j;
                var c = a + stride;
                // (Both ways round: seen from above whichever way the frame turns.)
                builder.Indices.AddRange([a, a + 1, c, a + 1, c + 1, c, a, c, a + 1, a + 1, c, c + 1]);
            }

        // Its two side edges, down through the slab and a little into the bank.
        const float below = NetworkLines.SlabDepth + 0.2f;
        foreach (var (j, side) in (ReadOnlySpan<(int, float)>)[(0, -1f), (columns, 1f)])
            for (var i = 0; i < rows; i++)
            {
                var (p, q) = (grid[i, j], grid[i + 1, j]);
                var normal = r * side;
                var v0 = builder.Add(p, normal, data);
                var v1 = builder.Add(q, normal, data);
                var v2 = builder.Add(p - new float3(0f, below, 0f), normal, data);
                var v3 = builder.Add(q - new float3(0f, below, 0f), normal, data);
                builder.Indices.AddRange([v0, v1, v2, v1, v3, v2, v0, v2, v1, v1, v2, v3]);
            }

        // The kerb along the toe.
        for (var j = 0; j < columns; j++)
        {
            var (p, q) = (grid[rows, j], grid[rows, j + 1]);
            Wing(builder, p, q, 0.3f, MathF.Min(p.Y, q.Y) - below, p.Y + 0.12f, q.Y + 0.12f, data);
        }
    }

    /// A part of the bridge kit stood at a place, stretched to size along across (model x), up (model y) and forward (model z).
    /// False if there's no kit, or no such part in it, for the caller to build its own.
    private bool Kit(Builder builder, string name, float3 at, float3 across, float3 up, float3 forward, float3 size)
    {
        if (_bridgeKit is null || !_bridgeKit.TryGetValue(name, out var part))
            return false;
        // Concrete as the rest of the bridge's (textured by where it is, as Face does); any other paint as the kit has it.
        var concrete = name.EndsWith("_Concrete", StringComparison.Ordinal);
        var code = concrete ? Code(LineMaterial.Concrete) : PaintOf(name);
        // A wall's rounded noses (past x = +-0.5) keep their shape: they're scaled by its depth, and only the middle by its length.
        var noses = name.StartsWith("Wall", StringComparison.Ordinal);
        var core = noses ? MathF.Max(size.X - size.Z, 0.01f) : size.X;
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var (p, n) = (vertex.Position, vertex.Normal);
            var beyond = noses && MathF.Abs(p.X) > 0.5f + 1e-4f;
            var x = beyond ? MathF.Sign(p.X) * (core * 0.5f + (MathF.Abs(p.X) - 0.5f) * size.Z) : p.X * core;
            var sx = beyond ? size.Z : core;
            var normal = MathEx.Normalize(across * (n.X / sx) + up * (n.Y / size.Y) + forward * (n.Z / size.Z));
            var position = at + across * x + up * (p.Y * size.Y) + forward * (p.Z * size.Z);
            builder.Add(position, normal, concrete ? new float4(position.X, position.X + position.Z, code, 0f) : new float4(vertex.Uv.X, vertex.Uv.Y, code, 0f));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
        return true;
    }
}
