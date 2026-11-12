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
        var covered = group.Members.Where(m => !m.Line.Mark).Select(m => (From: m.Offset - m.Half, To: m.Offset + m.Half)).OrderBy(c => c.From).ToList();
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

        // Railings along the outer edges only.
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
            Abutment(builder, group, s, inward, left, right, depth, edge, start ? edit.StartWing : edit.EndWing, start ? edit.StartFlare : edit.EndFlare);
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

    /// An abutment under one end of the deck: a seat for the beams, a wall behind them up to the deck, and a retaining wall out from
    /// each side holding the bank back, as long as asked (or as the bank needs) and flared back from square as asked, its top falling
    /// from the deck's edge to the ground.
    private void Abutment(Builder builder, BridgeGroup group, float s, float inward, float left, float right, float depth, float edge, float? wing, float flare)
    {
        var line = group.Reference;
        var (center, r) = FrameAt(line, s, builder.Origin);
        // Into the gap, from this end.
        var forward = new float3(r.Z, 0f, -r.X) * inward;
        var world = (X: center.X + builder.Origin.X, Z: center.Z + builder.Origin.Z);
        var ground = MathF.Min(_source.HeightAt(world.X, world.Z), _source.HeightAt(world.X + forward.X * 4f, world.Z + forward.Z * 4f));
        var top = center.Y - depth;
        ground = MathF.Min(ground, top - 0.5f);
        var middle = center + r * ((left + right) * 0.5f);
        var half = (right - left) * 0.5f + 0.1f;
        var data = new float4(0f, 0f, Code(LineMaterial.Concrete), 0f);
        Solid(builder, middle + forward * 0.6f, r * half, forward * 0.9f, ground - 1.5f, top, data);
        Solid(builder, middle - forward * 0.35f, r * half, forward * 0.35f, ground - 1.5f, center.Y - 0.05f, data);

        var reach = wing ?? (group.Rail
            ? MathF.Max(line.BallastHalfTop + MathF.Max(0.35f, line.BedHeight * NetworkLine.BallastSlope * NetworkLine.BedRun) + NetworkLine.BedFade - (right - left) * 0.5f, 1f) + 1f
            : Math.Clamp((center.Y - ground) * 1.2f, 1.5f, 6f));
        var settled = center.Y + (group.Rail ? 0f : RoadRise) + 0.1f;
        var turn = Math.Clamp(flare, -45f, 80f) * MathF.PI / 180f;
        foreach (var side in (float[])[-1f, 1f])
        {
            var a = middle - forward * 0.1f + r * (side * half);
            // Out to the side, turned back into the bank by the flare.
            var outward = r * (side * MathF.Cos(turn)) - forward * MathF.Sin(turn);
            var b = a + outward * reach;
            var low = MathF.Min(ground, _source.HeightAt(b.X + builder.Origin.X, b.Z + builder.Origin.Z)) - 1.5f;
            Wing(builder, a, b, 0.4f, low, center.Y + edge, settled, data);
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
