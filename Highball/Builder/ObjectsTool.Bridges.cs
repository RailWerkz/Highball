using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// Editing a bridge (Structures, Edit bridge): click one to pick it; its piers show as handles to drag along it; its card sets its
/// deck, its piers' style and where they stand, its room for more tracks either side, and each end's retaining walls. Each change is
/// saved to Bridges.json and undone as one.
internal sealed partial class ObjectsTool
{
    /// The bridge picked, the pier picked on it (for Remove), and the pier being dragged with where it's got to.
    private long? _bridgeKey;
    private int? _pierPick;
    private (int Index, float S)? _pierDrag;

    private BridgeGroup? PickedBridge => _bridgeKey is { } key ? _network?.Bridges.FirstOrDefault(b => b.Key == key) : null;

    /// Where a pier's handle is drawn: on the deck's underside at its place.
    private float2? PierHandle(BridgeGroup bridge, float s)
    {
        var (x, z, underside) = _network!.BridgePlace(bridge, s);
        return context.Project(x, underside, z);
    }

    private int? PierUnder(BridgeGroup bridge, float2 mouse)
    {
        var piers = _network!.PiersOf(bridge);
        for (var i = 0; i < piers.Count; i++)
            if (PierHandle(bridge, piers[i].S) is { } at && (at.X - mouse.X) * (at.X - mouse.X) + (at.Y - mouse.Y) * (at.Y - mouse.Y) < S(12f) * S(12f))
                return i;
        return null;
    }

    private void BridgeEditUpdate((double X, double Z)? ground, float2 mouse)
    {
        if (_network is null)
            return;
        var bridge = PickedBridge;
        if (Input.MouseDown(GlfwMouseButton.Left))
        {
            if (bridge is not null && PierUnder(bridge, mouse) is { } pier)
            {
                _pierPick = pier;
                _pierDrag = (pier, _network.PiersOf(bridge)[pier].S);
            }
            else if (ground is { } at)
            {
                var found = _network.BridgeAt(at.X, at.Z, 6.0);
                if (found?.Key != _bridgeKey)
                    _pierPick = null;
                _bridgeKey = found?.Key;
            }
        }

        if (_pierDrag is { } drag && bridge is not null)
        {
            if (Input.Mouse(GlfwMouseButton.Left) && ground is { } to)
                _pierDrag = (drag.Index, Math.Clamp(NetworkMeshes.AlongBridge(bridge, to.X, to.Z), 1f, bridge.Length - 1f));
            else if (!Input.Mouse(GlfwMouseButton.Left))
            {
                _pierDrag = null;
                var piers = _network.PiersOf(bridge).Select(p => p.S).ToArray();
                if (drag.Index < piers.Length && MathF.Abs(piers[drag.Index] - drag.S) > 0.05f)
                {
                    piers[drag.Index] = drag.S;
                    BridgeSet(bridge, EditOf(bridge) with { Piers = [.. piers.Order()] });
                }
            }
        }

        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Delete) && bridge is not null && _pierPick is { } doomed)
            RemovePier(bridge, doomed);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Escape))
            (_bridgeKey, _pierPick, _pierDrag) = (null, null, null);
    }

    private BridgeEdit EditOf(BridgeGroup bridge) => _terrain!.Source.Bridges.GetValueOrDefault(bridge.Key) ?? new BridgeEdit(bridge.Key);

    /// One bridge's edit set (as worked out again when it's back to nothing set), saved, shown, and pushed for undo.
    private void BridgeSet(BridgeGroup bridge, BridgeEdit after)
    {
        var before = _terrain!.Source.Bridges.GetValueOrDefault(bridge.Key);
        var plain = after == new BridgeEdit(bridge.Key) ? null : after;
        if (before == plain)
            return;
        ApplyBridge(bridge.Key, plain);
        context.Record(new BridgeChange(bridge.Key, before, plain));
    }

    public void ApplyBridge(long key, BridgeEdit? edit)
    {
        var source = _terrain!.Source;
        var was = source.Bridges.GetValueOrDefault(key);
        if (edit is null)
            source.Bridges.Remove(key);
        else
            source.Bridges[key] = edit;
        RouteBridges.Save(_route, source.Bridges.Values);
        _network?.BridgeChanged(key);

        // What an end stands on, how far back, and how long its wall runs shape the ground under it too: the track's laid out again
        // round the bridge, and its ground shaped again.
        static object? Ground(BridgeEdit? e) => e is null ? null
            : (e.StartKind, e.EndKind, e.StartSetback, e.EndSetback, e.StartWing, e.EndWing, e.StartExtend, e.EndExtend);
        if (!Equals(Ground(was), Ground(edit)) && _network?.Bridges.FirstOrDefault(b => b.Key == key) is { } bridge)
        {
            RailWerkz.Core.Logging.Log.Info($"Bridge {key}: ends set ({edit?.StartKind}/{edit?.EndKind}, lengthened {edit?.StartExtend:0}/{edit?.EndExtend:0} m, " +
                                            $"set back {edit?.StartSetback:0.0}/{edit?.EndSetback:0.0} m, nodes {edit?.StartNodes?.Length ?? 0}/{edit?.EndNodes?.Length ?? 0}): laying the track round it again");
            source.RebuildAround(bridge.X, bridge.Z);
            foreach (var p in (int[])[bridge.First, bridge.Last])
                source.RebuildAround(bridge.Reference.X[p], bridge.Reference.Z[p]);
            context.RebuildNetwork();
        }
    }

    /// The track's nodes at one end of a bridge (its start: the end nearer the reference's first point), each of its tracks'.
    private static long[] EndNodes(BridgeGroup bridge, bool start)
    {
        var (rx, rz) = (bridge.Reference.X[bridge.First], bridge.Reference.Z[bridge.First]);
        var nodes = new List<long>();
        foreach (var (line, first, last, _, _) in bridge.Members)
        {
            var nearFirst = Math.Pow(line.X[first] - rx, 2) + Math.Pow(line.Z[first] - rz, 2) < Math.Pow(line.X[last] - rx, 2) + Math.Pow(line.Z[last] - rz, 2);
            if (NetworkLines.EndNode(line, nearFirst == start ? first : last, nearFirst == start ? last : first) is { } node)
                nodes.Add(node);
        }

        return [.. nodes.Distinct()];
    }

    private void RemovePier(BridgeGroup bridge, int index)
    {
        var piers = _network!.PiersOf(bridge).Select(p => p.S).ToList();
        if (index >= piers.Count)
            return;
        piers.RemoveAt(index);
        _pierPick = null;
        BridgeSet(bridge, EditOf(bridge) with { Piers = [.. piers] });
    }

    /// The picked bridge outlined along its deck's edges, and its piers as numbered handles (the dragged one where it's got to).
    private void BridgeDraw(ImDrawListPtr draw)
    {
        if (_network is null)
            return;
        var mouse = Input.MousePosition;
        var hovered = !context.Blocked && context.Hit is { } at ? _network.BridgeAt(at.X, at.Z, 6.0) : null;
        foreach (var bridge in (BridgeGroup?[])[hovered, PickedBridge])
        {
            if (bridge is null)
                continue;
            var picked = bridge.Key == _bridgeKey;
            var color = picked ? Accent : Accent.WithAlpha(0.45f);
            var line = bridge.Reference;
            foreach (var u in (float[])[bridge.Left, bridge.Right])
            {
                float2? previous = null;
                for (var p = bridge.First; p <= bridge.Last; p++)
                {
                    var (x, z, y, _, _, rx, rz) = new Leg(line, 0, 1).At(line.S[p]);
                    var here = context.Project(x + rx * u, y + 0.5, z + rz * u);
                    if (previous is { } a && here is { } b)
                        draw.AddLine(a, b, color, S(picked ? 2.5f : 1.5f));
                    previous = here;
                }
            }

            if (!picked)
                continue;
            var piers = _network.PiersOf(bridge);
            for (var i = 0; i < piers.Count; i++)
            {
                var s = _pierDrag is { } drag && drag.Index == i ? drag.S : piers[i].S;
                if (PierHandle(bridge, s) is not { } handle)
                    continue;
                var lit = _pierPick == i || (_pierDrag?.Index == i);
                var hover = (handle.X - mouse.X) * (handle.X - mouse.X) + (handle.Y - mouse.Y) * (handle.Y - mouse.Y) < S(12f) * S(12f);
                draw.AddCircleFilled(handle, S(9f), lit ? Accent : hover ? Style.Text : Rgba.Black.WithAlpha(0.7f), 20);
                draw.AddCircle(handle, S(9f), lit ? Rgba.White : Accent, 20, S(1.5f));
                Canvas.Text(draw, Bold, S(11f), handle + new float2(0f, -S(6.5f)), lit ? OnAccent : hover ? Rgba.Black : Style.Text, (i + 1).ToString(CultureInfo.InvariantCulture), Align.Center);
            }
        }
    }

    /// How far a track laid onto a bridge runs on past each end, on the ground, for it to be joined to.
    private const float BridgeApproach = 20f;

    /// A track laid across a bridge beside the outermost track on that side (a track's spacing out from it): on the bridge, and on
    /// the ground for BridgeApproach past each end, its ends loose to join. The bridge widens to carry it.
    public void LayTrackOnBridge(BridgeGroup bridge, int side)
    {
        if (_tracks is not { } tracks)
            return;
        var one = NetworkLine.BallastShoulder + 0.75f;
        // The outermost track's middle on that side, then one spacing on.
        var offset = side > 0
            ? bridge.Members.Max(m => m.Offset + m.Half - one) + NetworkLine.TrackSpacing
            : bridge.Members.Min(m => m.Offset - m.Half + one) - NetworkLine.TrackSpacing;

        var line = bridge.Reference;
        var leg = new Leg(line, 0, 1);
        (double X, double Z) At(float s)
        {
            // Past either end, straight on from it.
            var clamped = Math.Clamp(s, 0f, line.S[^1]);
            var (x, z, _, fx, fz, rx, rz) = leg.At(clamped);
            var beyond = s - clamped;
            return (x + fx * beyond + rx * offset, z + fz * beyond + rz * offset);
        }

        List<LayPoint> Along(float from, float to)
        {
            var steps = Math.Max(1, (int)MathF.Ceiling((to - from) / 10f));
            return [.. Enumerable.Range(0, steps + 1).Select(k => { var (x, z) = At(from + (to - from) * k / steps); return new LayPoint(x, z); })];
        }

        var (start, end) = (bridge.From, bridge.To);
        List<PlannedWay> ways =
        [
            new(Along(start - BridgeApproach, start)),
        ];
        // Each joined to the last point of the one before.
        ways.Add(new(Along(start, end), Shared: [(0, 0, ways[0].Points.Count - 1)], Tags: new Dictionary<string, string> { ["bridge"] = "yes", ["layer"] = "1" }));
        ways.Add(new(Along(end, end + BridgeApproach), Shared: [(0, 1, ways[1].Points.Count - 1)]));

        var path = ways.SelectMany(w => w.Points).Select(p => (p.X, p.Z)).ToList();
        if (tracks.RunsInto(path, 2.0, false, false))
        {
            _bridgeProblem = "Runs into another track";
            return;
        }

        _bridgeProblem = null;
        if (tracks.LayWays(ways) is { } laid)
            context.Commit(laid);
    }

    private string? _bridgeProblem;

    /// Sliders' values while they're dragged, until they're let go (and saved as one edit).
    private readonly Dictionary<string, float> _bridgeDrafts = [];

    private void Draft(string id, float value)
    {
        if (ImGui.IsItemActive())
            _bridgeDrafts[id] = value;
        else
            _bridgeDrafts.Remove(id);
    }

    /// The picked bridge's card: what it is, its deck, its piers, its width, and its ends.
    private void BridgePanel()
    {
        if (PickedBridge is not { } bridge)
        {
            ToolPanel.Hint("Click a bridge.");
            return;
        }

        var edit = EditOf(bridge);
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var factor = imperial ? FeetPerMeter : 1f;
        var unit = imperial ? "ft" : "m";
        string Length(float metres) => string.Create(CultureInfo.InvariantCulture, $"{metres * factor:0} {unit}");
        var piers = _network!.PiersOf(bridge);

        Kit.BeginCard("Bridge", Length(bridge.Length), Accent);
        var carried = bridge.Members.Count;
        Kit.Stats(
        [
            new Stat("Carries", bridge.Rail ? $"{bridge.Members.Sum(m => m.Line.Tracks)} track{(bridge.Members.Sum(m => m.Line.Tracks) == 1 ? "" : "s")}" : $"{carried} road{(carried == 1 ? "" : "s")}"),
            new Stat("Spans", (piers.Count + 1).ToString(CultureInfo.InvariantCulture)),
        ], 2);
        Kit.Caption("Deck");
        var deck = ToolPanel.Mode("bridgedeck", ["Steel", "Concrete"], (int)edit.Deck, 2);
        if (deck != (int)edit.Deck)
            BridgeSet(bridge, edit with { Deck = (DeckKind)deck });
        Kit.EndCard();

        Kit.BeginCard("Piers", edit.Piers is null ? "Auto" : "Set by hand");
        var style = ToolPanel.Mode("pierstyle", ["Auto", "Wall", "Columns", "Column"], (int)edit.Style, 2);
        if (style != (int)edit.Style)
            BridgeSet(bridge, edit with { Style = (PierStyle)style });
        for (var i = 0; i < piers.Count; i++)
            if (ToolPanel.Item($"pier{i}", null, $"Pier {i + 1}", Length(piers[i].S), _pierPick == i))
                _pierPick = _pierPick == i ? null : i;
        switch (Kit.Buttons("pieractions", ["Add", "Remove", "Auto"], [true, _pierPick is not null, edit.Piers is not null]))
        {
            case 0:
                // In the middle of the longest span.
                var stops = new List<float> { 0f };
                stops.AddRange(piers.Select(p => p.S));
                stops.Add(bridge.Length);
                var widest = Enumerable.Range(0, stops.Count - 1).MaxBy(k => stops[k + 1] - stops[k]);
                BridgeSet(bridge, edit with { Piers = [.. stops.Skip(1).SkipLast(1).Append((stops[widest] + stops[widest + 1]) * 0.5f).Order()] });
                break;
            case 1 when _pierPick is { } doomed:
                RemovePier(bridge, doomed);
                break;
            case 2:
                BridgeSet(bridge, edit with { Piers = null });
                _pierPick = null;
                break;
        }

        Kit.EndCard();

        Kit.BeginCard("Width", bridge.Rail ? "Tracks" : "Extra lanes");
        if (bridge.Rail)
        {
            Kit.Caption("Add track");
            switch (Kit.Buttons("addtrack", ["+ Left", "+ Right"], [_tracks is not null, _tracks is not null]))
            {
                case 0:
                    LayTrackOnBridge(bridge, -1);
                    break;
                case 1:
                    LayTrackOnBridge(bridge, 1);
                    break;
            }

            if (_bridgeProblem is { } problem)
                ToolPanel.Problem(problem);
            Kit.Caption("Spare deck");
        }

        var left = edit.RoomLeft;
        if (ToolPanel.Count("roomleft", "Left", ref left, 0, 4))
            BridgeSet(bridge, edit with { RoomLeft = left });
        var right = edit.RoomRight;
        if (ToolPanel.Count("roomright", "Right", ref right, 0, 4))
            BridgeSet(bridge, edit with { RoomRight = right });
        Kit.EndCard();

        foreach (var start in (bool[])[true, false])
        {
            // What the end stands on, as worked out or as picked: a wall beside what's underneath, a slope down to it, or the bank.
            var shownKind = _network!.EndKindOf(bridge, start);
            Kit.BeginCard(start ? "Start abutment" : "End abutment", shownKind?.ToString());
            // (What the built bridge has at this end now: if the settings haven't reached it, this says so.)
            var built = _network.EndBuilt(bridge, start);
            ToolPanel.Hint(string.Create(CultureInfo.InvariantCulture,
                $"Built: {built.Kind?.ToString() ?? "none"}, deck +{built.Lengthened * factor:0} {unit}, set back {built.Setback * factor:0.0} {unit}"));
            var kinds = new BridgeEndKind?[] { null, BridgeEndKind.Wall, BridgeEndKind.Slope, BridgeEndKind.Bank };
            var setKind = start ? edit.StartKind : edit.EndKind;
            var kindPick = ToolPanel.Mode(start ? "startkind" : "endkind", ["Auto", "Wall", "Slope", "Bank"], Array.IndexOf(kinds, setKind), 4);
            if (kindPick >= 0 && kinds[kindPick] != setKind)
            {
                var nodes = EndNodes(bridge, start);
                BridgeSet(bridge, start ? edit with { StartKind = kinds[kindPick], StartNodes = nodes } : edit with { EndKind = kinds[kindPick], EndNodes = nodes });
                edit = EditOf(bridge);
            }

            // How much further the deck carries on over the approach, so its end (and the abutment) stands nearer the bank.
            var extendId = start ? "startextend" : "endextend";
            var extend = _bridgeDrafts.GetValueOrDefault(extendId, (start ? edit.StartExtend : edit.EndExtend) * factor);
            ToolPanel.Value(extendId, "Lengthen deck", ref extend, 0f, 60f * factor, $"0' {unit}';;'none'", out var extendLet);
            Draft(extendId, extend);
            if (extendLet)
            {
                var nodes = EndNodes(bridge, start);
                var value = extend < 0.25f ? 0f : extend / factor;
                BridgeSet(bridge, start ? edit with { StartExtend = value, StartNodes = nodes } : edit with { EndExtend = value, EndNodes = nodes });
                edit = EditOf(bridge);
            }

            // How far back from what's underneath its wall or slope stands, towards the bank (under the deck still).
            var setbackId = start ? "startsetback" : "endsetback";
            var setback = _bridgeDrafts.GetValueOrDefault(setbackId, (start ? edit.StartSetback : edit.EndSetback) * factor);
            ToolPanel.Value(setbackId, "Set back", ref setback, 0f, 40f * factor, $"0.0' {unit}'", out var setLet);
            Draft(setbackId, setback);
            if (setLet)
            {
                var nodes = EndNodes(bridge, start);
                var value = setback < 0.05f ? 0f : setback / factor;
                BridgeSet(bridge, start ? edit with { StartSetback = value, StartNodes = nodes } : edit with { EndSetback = value, EndNodes = nodes });
                edit = EditOf(bridge);
            }

            var wingId = start ? "startwing" : "endwing";
            var wing = _bridgeDrafts.GetValueOrDefault(wingId, ((start ? edit.StartWing : edit.EndWing) ?? 0f) * factor);
            Kit.Caption(shownKind == BridgeEndKind.Wall ? "Walls along what's underneath" : "Wing walls");
            ToolPanel.Value(wingId, "Length", ref wing, 0f, (shownKind == BridgeEndKind.Wall ? 60f : 20f) * factor, $"0.0' {unit}';;'auto'", out var let);
            Draft(wingId, wing);
            if (let)
            {
                var nodes = EndNodes(bridge, start);
                BridgeSet(bridge, start ? edit with { StartWing = wing < 0.25f ? null : wing / factor, StartNodes = nodes }
                    : edit with { EndWing = wing < 0.25f ? null : wing / factor, EndNodes = nodes });
            }
            // (A wall beside what runs underneath runs along it: no flare.)
            if (shownKind != BridgeEndKind.Wall)
            {
                var flareId = start ? "startflare" : "endflare";
                var flare = _bridgeDrafts.GetValueOrDefault(flareId, start ? edit.StartFlare : edit.EndFlare);
                ToolPanel.Value(flareId, "Flare", ref flare, -45f, 80f, "0'°'", out var flared);
                Draft(flareId, flare);
                if (flared)
                    BridgeSet(bridge, start ? edit with { StartFlare = MathF.Round(flare) } : edit with { EndFlare = MathF.Round(flare) });
            }

            Kit.EndCard();
        }
    }
}
