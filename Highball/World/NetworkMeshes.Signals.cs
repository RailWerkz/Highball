using Highball.Routes;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Signals and signal bridges: their heads stacked top to bottom on a mast or over each track on a bridge, built from the kit
/// Tools/Blender/signals.py makes (Meshes/Track/Signals), each lens lit as its head's lamps say.
public sealed partial class NetworkMeshes
{
    /// Each head type's model, the mast kit and the bridge kit.
    private readonly Dictionary<SignalHeadType, Dictionary<string, Model.Part>> _heads = [];
    private Dictionary<string, Model.Part>? _mastKit;
    private Dictionary<string, Model.Part>? _bridgeParts;

    /// Each head's height (its foot's at its origin), and where each of its lenses' light goes: the middle of the lens, a little out
    /// in front of its dome (the model's axes: x right, y up, z out its front). Read off the models.
    private readonly Dictionary<SignalHeadType, float> _headHeights = [];
    private readonly Dictionary<(SignalHeadType, string), float3> _lensAt = [];

    /// A mast signal's lowest head's foot over the ground, as the old signal's (its platform just under it), and the gap between heads.
    private const float MastFoot = 3.8f;
    private const float HeadGap = 0.15f;
    private const float MastBottom = 0.6f;
    private const float LadderStep = 0.3f;

    /// A signal bridge: how far its legs stand out from the tracks' centres beyond the outermost (8 ft 6 in clear, and half the tower);
    /// how high its truss's underside is over the highest rail (23 ft); the truss's depth front to back and its height; a bay's
    /// length; and how far in front of the truss the heads' mast stubs stand.
    private const float LegClear = 3.0f;
    private const float BridgeClear = 7.0f;
    private const float TrussHalf = 0.45f;
    private const float TrussHigh = 1.0f;
    private const float Bay = 1.5f;
    private const float StubOut = 0.72f;

    /// The back's mast stubs stand out past the walkway and its railing; a cantilever's arm reaches this far past its farthest track.
    private const float BackOut = 1.45f;
    private const float CantileverTip = 1.2f;

    /// A bridge put where there's no track under it stands over this much of nothing, so it can still be seen and picked.
    private const float LoneSpan = 4.6f;

    /// The tracks under a signal bridge, as fitted when it was last built: where each crosses it (left to right as a driver coming
    /// at it sees them) with its rail top's height, where its legs stand with the ground's height there, its truss's underside,
    /// and the way its heads face and its span runs (right).
    private sealed record SignalSpan(float3 Face, float3 Right, (double X, double Z, float Rail)[] Tracks, (double X, double Z, float Ground) Left,
        (double X, double Z, float Ground) Far, float Deck);

    private void LoadSignalModels()
    {
        try
        {
            _heads[SignalHeadType.Triple] = Model.LoadParts("Track/Signals/head_triple.glb");
            _heads[SignalHeadType.Hooded] = Model.LoadParts("Track/Signals/head_hooded.glb");
            _heads[SignalHeadType.Searchlight] = Model.LoadParts("Track/Signals/head_search.glb");
            _mastKit = Model.LoadParts("Track/Signals/mast.glb");
            _bridgeParts = Model.LoadParts("Track/Signals/bridge.glb");
        }
        catch (Exception e)
        {
            RailWerkz.Core.Logging.Log.Warn($"Signals: {e.Message}; signals without a model aren't drawn");
        }
    }

    private float HeadHeight(SignalHeadType type)
    {
        if (_headHeights.TryGetValue(type, out var known))
            return known;
        var top = 0f;
        foreach (var part in (IEnumerable<Model.Part>?)_heads.GetValueOrDefault(type)?.Values ?? [])
            foreach (var v in part.Vertices)
                top = MathF.Max(top, v.Position.Y);
        return _headHeights[type] = top > 0f ? top : 1.9f;
    }

    private float3 LensAt(SignalHeadType type, string part)
    {
        if (_lensAt.TryGetValue((type, part), out var known))
            return known;
        var found = new float3(0f, HeadHeight(type) * 0.5f, 0.15f);
        if (_heads.GetValueOrDefault(type)?.GetValueOrDefault(part) is { Vertices.Length: > 0 } lens)
        {
            var (lo, hi) = (new float3(float.MaxValue), new float3(float.MinValue));
            foreach (var v in lens.Vertices)
            {
                lo = new float3(MathF.Min(lo.X, v.Position.X), MathF.Min(lo.Y, v.Position.Y), MathF.Min(lo.Z, v.Position.Z));
                hi = new float3(MathF.Max(hi.X, v.Position.X), MathF.Max(hi.Y, v.Position.Y), MathF.Max(hi.Z, v.Position.Z));
            }

            found = new float3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, hi.Z + 0.03f);
        }

        return _lensAt[(type, part)] = found;
    }

    /// The lens part a lamp lights on a head: a colour-light head's own lens for each, a searchlight's one lens for all.
    private static string LensPart(SignalHeadType type, SignalLamps lamp) =>
        type == SignalHeadType.Searchlight ? SearchLens : Array.Find(Lamps, l => l.Lamp == lamp).Part ?? "";

    private const string SearchLens = "Fixed_LensSearch";

    private static readonly SignalLamps[] LampOrder = [SignalLamps.Red, SignalLamps.Yellow, SignalLamps.Green, SignalLamps.White];

    /// How high a stack of heads is, top of the top one to the foot of the bottom one.
    private float StackHeight(IReadOnlyList<SignalHeadType> heads)
    {
        var high = 0f;
        for (var i = 0; i < heads.Count; i++)
            high += HeadHeight(heads[i]) + (i > 0 ? HeadGap : 0f);
        return high;
    }

    /// Every head on a placed signal or signal bridge: its type, its number (as LampsOf counts them), where its foot is on its mast's
    /// axis (world metres) and the way it faces.
    private IEnumerable<(SignalHeadType Type, int Index, double X, double Y, double Z, float3 Face)> HeadsOn(PlacedAt placed)
    {
        var o = placed.Object;
        var index = 0;
        if (o.Kind == PlacedKind.Signal)
        {
            // The front's heads, then the back's, back to back on the mast, each stack from the same foot up.
            var face = FaceOf(o.Heading);
            foreach (var (heads, way) in MastSides(o))
            {
                var foot = placed.Y + MastFoot + StackHeight(heads);
                foreach (var type in heads)
                {
                    foot -= HeadHeight(type);
                    yield return (type, index++, placed.X, foot, placed.Z, face * way);
                    foot -= HeadGap;
                }
            }

            yield break;
        }

        if (placed.Span is not { } span)
            yield break;
        // The front's stacks over each track, then the back's, on stubs out past the walkway behind.
        foreach (var (stacks, way, reach) in BridgeSides(o))
            for (var track = 0; track < (stacks?.Count ?? 0); track++)
            {
                var heads = stacks![track];
                // Heads over a track that's no longer under the bridge aren't shown, but keep their numbers.
                if (track >= span.Tracks.Length)
                {
                    index += heads.Count;
                    continue;
                }

                var (x, z, _) = span.Tracks[track];
                var (sx, sz) = (x + span.Face.X * reach * way, z + span.Face.Z * reach * way);
                var foot = span.Deck + 0.05f + StackHeight(heads);
                foreach (var type in heads)
                {
                    foot -= HeadHeight(type);
                    yield return (type, index++, sx, foot, sz, span.Face * way);
                    foot -= HeadGap;
                }
            }
    }

    /// A mast's two sides: its front's heads facing its way (1), its back's the other (-1).
    private static (IReadOnlyList<SignalHeadType> Heads, float Way)[] MastSides(PlacedObject o) => [(o.MastHeads, 1f), (o.MastBackHeads, -1f)];

    /// A bridge's two sides: its front's stacks facing its way, on stubs StubOut in front; its back's the other way, BackOut behind.
    private static (IReadOnlyList<IReadOnlyList<SignalHeadType>>? Stacks, float Way, float Reach)[] BridgeSides(PlacedObject o) =>
        [(o.Stacks, 1f, StubOut), (o.BackStacks, -1f, BackOut)];

    private static float3 FaceOf(double heading)
    {
        var angle = heading * Math.PI / 180.0;
        return new float3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
    }

    /// A part placed with its axes stretched by scale and moved by offset (both in the model's axes: x right, y up, z along face).
    private static void Stretched(Builder builder, Model.Part part, float3 at, float3 face, float code, float3 scale, float3 offset)
    {
        var right = new float3(face.Z, 0f, -face.X);
        var up = new float3(0f, 1f, 0f);
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var (p, n) = (vertex.Position, vertex.Normal);
            var position = at + right * (p.X * scale.X + offset.X) + up * (p.Y * scale.Y + offset.Y) + face * (p.Z * scale.Z + offset.Z);
            var normal = right * (n.X / scale.X) + up * (n.Y / scale.Y) + face * (n.Z / scale.Z);
            var length = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            builder.Add(position, length > 1e-6f ? normal / length : up, new float4(vertex.Uv.X, vertex.Uv.Y, code, 0f));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    /// The kit's parts whose names start with role (Pole_, Leg_...), each with its paint.
    private static IEnumerable<(Model.Part Part, float Paint)> Kit(Dictionary<string, Model.Part>? kit, string role)
    {
        foreach (var (name, part) in kit ?? [])
            if (name.StartsWith(role, StringComparison.Ordinal))
                yield return (part, PaintOf(name));
    }

    /// A signal or signal bridge, built: its mast or bridge, then each head with its lenses lit as its lamps say.
    private void SignalModel(Builder builder, PlacedAt placed)
    {
        var o = placed.Object;
        float3 Local(double x, double y, double z) => new((float)(x - builder.Origin.X), (float)y, (float)(z - builder.Origin.Z));
        var one = new float3(1f);
        if (o.Kind == PlacedKind.Signal)
        {
            var face = FaceOf(o.Heading);
            var at = Local(placed.X, placed.Y, placed.Z);
            var back = o.MastBackHeads.Count > 0;
            var top = MastFoot + MathF.Max(StackHeight(o.MastHeads), StackHeight(o.MastBackHeads)) + 0.1f;
            var platform = MastFoot - 0.1f;
            foreach (var (part, paint) in Kit(_mastKit, "Base_"))
                Stretched(builder, part, at, face, paint, one, default);
            foreach (var (part, paint) in Kit(_mastKit, "Pole_"))
                Stretched(builder, part, at, face, paint, new float3(1f, top - MastBottom, 1f), new float3(0f, MastBottom, 0f));
            // A platform under each side's heads; with heads on the back, the ladder goes up the mast's side, clear of them.
            foreach (var (_, way) in back ? MastSides(o) : MastSides(o)[..1])
                foreach (var (part, paint) in Kit(_mastKit, "Platform_"))
                    Stretched(builder, part, at, face * way, paint, one, new float3(0f, platform, 0f));
            var ladder = back ? new float3(-face.Z, 0f, face.X) : face;
            for (var y = MastBottom; y + LadderStep <= platform + 0.01f; y += LadderStep)
                foreach (var (part, paint) in Kit(_mastKit, "Ladder_"))
                    Stretched(builder, part, at, ladder, paint, one, new float3(0f, y, 0f));
        }
        else if (placed.Span is { } span)
            BridgeModel(builder, o, span, Local(0.0, 0.0, 0.0));

        foreach (var (type, index, x, y, z, face) in HeadsOn(placed))
        {
            if (_heads.GetValueOrDefault(type) is not { } parts)
                continue;
            var shown = type.Shown(LampsOf(o.Id, index));
            var at = Local(x, y, z);
            foreach (var (name, part) in parts)
                Stretched(builder, part, at, face, LensPaintOf(type, name, shown), one, default);
        }
    }

    /// A head's part's paint: a lens lit if its lamp's shown (a searchlight's one lens in whichever colour it's showing), dark glass
    /// otherwise; anything else as its name says.
    private static float LensPaintOf(SignalHeadType type, string name, SignalLamps shown)
    {
        if (name == SearchLens)
        {
            var paint = shown == SignalLamps.None ? "LensRed" : "Lens" + shown;
            return Code(LineMaterial.Painted, Array.IndexOf(Paints, paint) + (shown == SignalLamps.None ? 0 : LitLens));
        }

        if (Array.FindIndex(Lamps, l => l.Part == name) is var lamp and >= 0 && shown.HasFlag(Lamps[lamp].Lamp))
            return Code(LineMaterial.Painted, Array.IndexOf(Paints, Lamps[lamp].Paint) + LitLens);
        return PaintOf(name);
    }

    /// A signal bridge's legs (a cantilever's one), its truss across over them in bays (a cantilever's out from its leg to past the
    /// farthest track), its walkway behind, and the brackets and mast stubs its heads stand on in front, and on the back past the
    /// walkway, over each track that has heads.
    private void BridgeModel(Builder builder, PlacedObject o, SignalSpan span, float3 origin)
    {
        float3 Local(double x, double y, double z) => new((float)x + origin.X, (float)y, (float)z + origin.Z);
        var face = span.Face;
        var one = new float3(1f);
        var cantilever = o.Cantilever;
        (double, double, float)[] legs = !cantilever ? [span.Left, span.Far] : o.Opposite ? [span.Left] : [span.Far];
        foreach (var (x, z, ground) in legs)
        {
            var at = Local(x, ground, z);
            foreach (var (part, paint) in Kit(_bridgeParts, "Base_"))
                Stretched(builder, part, at, face, paint, one, default);
            foreach (var (part, paint) in Kit(_bridgeParts, "Leg_"))
                Stretched(builder, part, at, face, paint, new float3(1f, span.Deck + TrussHigh - ground, 1f), default);
        }

        // The truss runs over the legs' towers to their outer faces, in whole bays stretched to fit; a cantilever's from its leg's
        // outer face out to CantileverTip past the farthest track, closed at its tip.
        var (dx, dz) = (span.Far.X - span.Left.X, span.Far.Z - span.Left.Z);
        var between = Math.Sqrt(dx * dx + dz * dz);
        var (ux, uz) = (dx / Math.Max(between, 1e-6), dz / Math.Max(between, 1e-6));
        var tip = LegClear - CantileverTip;
        var (from, to) = !cantilever ? (-0.25, between + 0.25) : o.Opposite ? (-0.25, between - tip) : (tip, between + 0.25);
        var length = (float)(to - from);
        var bays = Math.Max(1, (int)MathF.Round(length / Bay));
        var stretch = length / (bays * Bay);
        var start = Local(span.Left.X + ux * from, span.Deck, span.Left.Z + uz * from);
        for (var k = 0; k < bays; k++)
            foreach (var role in (ReadOnlySpan<string>)["Bay_", "Walk_"])
                foreach (var (part, paint) in Kit(_bridgeParts, role))
                    Stretched(builder, part, start, face, paint, new float3(stretch, 1f, 1f), new float3(k * Bay * stretch, 0f, 0f));
        foreach (var (part, paint) in Kit(_bridgeParts, "End_"))
            Stretched(builder, part, start, face, paint, one, new float3(length, 0f, 0f));

        // Over each track with heads: two arms off the truss's front (or its back, out past the walkway), and a stub of mast from the
        // bottom arm to the top head.
        foreach (var (stacks, way, reach) in BridgeSides(o))
            for (var track = 0; track < Math.Min(span.Tracks.Length, stacks?.Count ?? 0); track++)
            {
                var heads = stacks![track];
                if (heads.Count == 0)
                    continue;
                var (x, z, _) = span.Tracks[track];
                var at = Local(x, span.Deck, z);
                var top = 0.05f + StackHeight(heads) + 0.1f;
                foreach (var y in (ReadOnlySpan<float>)[0.05f, TrussHigh - 0.05f])
                    foreach (var (part, paint) in Kit(_mastKit, "Pole_"))
                        Stretched(builder, part, at + new float3(0f, y, 0f), face * way, paint, new float3(0.5f, reach - TrussHalf, 0.5f), new float3(0f, 0f, TrussHalf),
                            sideways: true);
                foreach (var (part, paint) in Kit(_mastKit, "Pole_"))
                    Stretched(builder, part, at, face * way, paint, new float3(1f, top + 0.05f, 1f), new float3(0f, -0.05f, reach));
            }
    }

    /// A part laid along face instead of up: its y (a pole's length) runs out of the bridge's front, from offset.Z on.
    private static void Stretched(Builder builder, Model.Part part, float3 at, float3 face, float code, float3 scale, float3 offset, bool sideways)
    {
        var right = new float3(face.Z, 0f, -face.X);
        var up = new float3(0f, 1f, 0f);
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var (p, n) = (vertex.Position, vertex.Normal);
            // Model y along face, model z down.
            var position = at + right * (p.X * scale.X) + face * (p.Y * scale.Y + offset.Z) - up * (p.Z * scale.Z);
            var normal = right * (n.X / scale.X) + face * (n.Y / scale.Y) - up * (n.Z / scale.Z);
            var length = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            builder.Add(position, length > 1e-6f ? normal / length : up, new float4(vertex.Uv.X, vertex.Uv.Y, code, 0f));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    /// The tracks under a signal bridge at (x, z) whose heads face heading: those crossing the line across it there, the nearest and
    /// every one within CrossingTrackGap of the next (each track of a line with several), with its legs out past the outermost.
    private SignalSpan Span(PlacedObject o, double x, double z)
    {
        var face = FaceOf(o.Heading);
        var right = new float3(face.Z, 0f, -face.X);
        var railTop = NetworkLine.BallastHeight + KitRailBase + RailTop;
        var tracks = new List<(double T, double X, double Z, float Rail)>();
        foreach (var (line, s, _) in TracksAcross(x, z, right.X, right.Z))
        {
            var (px, pz, y, _, _, rx, rz) = new Leg(line, 0, 1).At(s);
            for (var k = 0; k < line.Tracks; k++)
            {
                var off = line.TrackOffset(k);
                var t = (px + rx * off - x) * right.X + (pz + rz * off - z) * right.Z;
                tracks.Add((t, x + right.X * t, z + right.Z * t, y + railTop));
            }
        }

        if (tracks.Count == 0)
            tracks.Add((0.0, x, z, _source.HeightAt(x, z) + railTop));
        tracks.Sort((a, b) => a.T.CompareTo(b.T));
        var (low, high) = (tracks[0].T - LegClear, tracks[^1].T + LegClear);
        if (high - low < LoneSpan)
            (low, high) = ((low + high - LoneSpan) * 0.5, (low + high + LoneSpan) * 0.5);
        (double, double, float) LegAt(double t)
        {
            var (lx, lz) = (x + right.X * t, z + right.Z * t);
            return (lx, lz, _source.HeightAt(lx, lz));
        }

        return new SignalSpan(face, right, [.. tracks.Select(t => (t.X, t.Z, t.Rail))], LegAt(low), LegAt(high), tracks.Max(t => t.Rail) + BridgeClear);
    }

    /// How many tracks a signal bridge spans now (0 if it isn't built yet).
    public int SpanTracks(long bridge)
    {
        foreach (var list in _objectsByChunk.Values)
            foreach (var placed in list)
                if (placed.Object.Id == bridge)
                    return placed.Span?.Tracks.Length ?? 0;
        return 0;
    }

    /// How many tracks a signal bridge would span where it is (before it's placed).
    public int SpanTracksOf(PlacedObject bridge)
    {
        if (_objectProjection is not { } projection)
            return 0;
        var p = projection.Forward(new GeoPoint(bridge.Lat, bridge.Lon));
        return Span(bridge, p.East, -p.North).Tracks.Length;
    }

    /// Where the tracks a signal bridge would span cross it, left to right (world metres), where it is (before it's placed).
    public (double X, double Z)[] SpanPoints(PlacedObject bridge)
    {
        if (_objectProjection is not { } projection)
            return [];
        var p = projection.Forward(new GeoPoint(bridge.Lat, bridge.Lon));
        return [.. Span(bridge, p.East, -p.North).Tracks.Select(t => (t.X, t.Z))];
    }

    /// Every lit lamp on every signal and signal bridge: where its light goes (just in front of its lens), the way it shines, and
    /// which lamp it is (its colour).
    public IEnumerable<(double X, double Y, double Z, float3 Face, SignalLamps Lamp)> SignalLights()
    {
        foreach (var list in _objectsByChunk.Values)
            foreach (var placed in list)
            {
                if (placed.Object.Kind is not (PlacedKind.Signal or PlacedKind.SignalBridge))
                    continue;
                foreach (var (type, index, x, y, z, face) in HeadsOn(placed))
                {
                    var shown = type.Shown(LampsOf(placed.Object.Id, index));
                    if (shown == SignalLamps.None)
                        continue;
                    var right = new float3(face.Z, 0f, -face.X);
                    foreach (var lamp in LampOrder)
                    {
                        if (!shown.HasFlag(lamp))
                            continue;
                        var lens = LensAt(type, LensPart(type, lamp));
                        var at = right * lens.X + face * lens.Z;
                        yield return (x + at.X, y + lens.Y, z + at.Z, face, lamp);
                    }
                }
            }
    }
}
