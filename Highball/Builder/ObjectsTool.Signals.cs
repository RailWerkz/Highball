using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// A picked signal's or signal bridge's heads: each stack set top to bottom (a head's type, moved up or down, taken off, or another
/// added at the foot), and each head's lamps lit by hand to test them.
internal sealed partial class ObjectsTool
{
    private static readonly SignalHeadType[] HeadTypes = Enum.GetValues<SignalHeadType>();

    private void SignalPanel(PlacedObject picked)
    {
        if (picked.Kind == PlacedKind.Signal)
        {
            Kit.Caption("Front, top to bottom");
            if (HeadStack("mast", picked, picked.MastHeads, 0, removable: picked.MastHeads.Count > 1) is { } heads)
                PlacedChange(picked, picked with { Heads = heads });
            // Back to back with the front's, facing trains the other way.
            Kit.Caption("Back, top to bottom");
            if (HeadStack("mastback", picked, picked.MastBackHeads, picked.MastHeads.Count, removable: true) is { } back)
                PlacedChange(picked, picked with { BackHeads = back.Count > 0 ? back : null });
        }
        else
        {
            // A truss on two legs, or a cantilever's arm out from one, on the tracks' right or left (as a driver coming at its front
            // sees them).
            Kit.Caption("Structure");
            var style = ToolPanel.Mode("bridgestyle", ["Bridge", "Cantilever"], picked.Cantilever ? 1 : 0, 2);
            if (style != (picked.Cantilever ? 1 : 0))
            {
                PlacedChange(picked, picked with { Style = style });
                return;
            }

            if (picked.Cantilever)
            {
                var side = ToolPanel.Mode("legside", ["Leg Right", "Leg Left"], picked.Opposite ? 1 : 0, 2);
                if (side != (picked.Opposite ? 1 : 0))
                {
                    PlacedChange(picked, picked with { Opposite = side == 1 });
                    return;
                }
            }

            // Over each track it spans, left to right as a driver coming at its front sees them, its front's heads and its back's;
            // a track with no heads on a side has no mast stub there.
            var tracks = _network!.SpanTracks(picked.Id);
            Kit.Note(tracks == 1 ? "Spans 1 track" : $"Spans {tracks} tracks");
            var count = Math.Max(tracks, Math.Max(picked.Stacks?.Count ?? 0, picked.BackStacks?.Count ?? 0));
            var front = Padded(picked.Stacks, count);
            var rear = Padded(picked.BackStacks, count);
            var first = 0;
            var firstBack = front.Sum(s => s.Count);
            for (var track = 0; track < count; track++)
            {
                var place = track >= tracks ? " (no track now)" : count == 1 ? "" : track == 0 ? " (left)" : track == tracks - 1 ? " (right)" : "";
                Kit.Caption($"Track {track + 1}{place}: front, top to bottom");
                if (HeadStack($"track{track}", picked, front[track], first, removable: true) is { } heads)
                {
                    PlacedChange(picked, picked with { Stacks = Trimmed(front, track, heads) });
                    break;
                }

                Kit.Caption($"Track {track + 1}{place}: back, top to bottom");
                if (HeadStack($"back{track}", picked, rear[track], firstBack, removable: true) is { } back)
                {
                    PlacedChange(picked, picked with { BackStacks = Trimmed(rear, track, back) });
                    break;
                }

                first += front[track].Count;
                firstBack += rear[track].Count;
            }
        }

        // By hand for now, until there are signalling rules to set them; not saved with the route.
        var all = SignalHeads.All(picked).ToList();
        switch (Kit.Buttons("lamps", ["All Lamps", "No Lamps"]))
        {
            case 0:
                for (var head = 0; head < all.Count; head++)
                    _network!.SetLamps(picked, head, all[head].Lenses());
                break;
            case 1:
                for (var head = 0; head < all.Count; head++)
                    _network!.SetLamps(picked, head, SignalLamps.None);
                break;
        }
    }

    /// A signal bridge's stacks on one side, one for each of tracks at least (empty past those it has).
    private static List<IReadOnlyList<SignalHeadType>> Padded(IReadOnlyList<IReadOnlyList<SignalHeadType>>? stacks, int tracks)
    {
        var padded = (stacks ?? []).ToList();
        while (padded.Count < tracks)
            padded.Add([]);
        return padded;
    }

    /// A signal bridge turned round where it stands: its heads face the other way, its tracks run right to left across it now, so
    /// each side's stacks go the other way round to stay over their tracks, and a cantilever's leg is on its other side to stay put.
    private PlacedObject TurnedRound(PlacedObject o)
    {
        var count = Math.Max(_network?.SpanTracks(o.Id) ?? 0, Math.Max(o.Stacks?.Count ?? 0, o.BackStacks?.Count ?? 0));
        List<IReadOnlyList<SignalHeadType>>? Reversed(IReadOnlyList<IReadOnlyList<SignalHeadType>>? stacks) =>
            stacks is null ? null : Padded(stacks, count).AsEnumerable().Reverse().ToList();
        return o with { Heading = o.Heading + 180.0, Stacks = Reversed(o.Stacks), BackStacks = Reversed(o.BackStacks), Opposite = o.Cantilever ? !o.Opposite : o.Opposite };
    }

    /// One side's stacks with one track's changed, and the empty ones past the last with heads dropped (null if none has any).
    private static List<IReadOnlyList<SignalHeadType>>? Trimmed(List<IReadOnlyList<SignalHeadType>> stacks, int track, IReadOnlyList<SignalHeadType> heads)
    {
        var changed = stacks.ToList();
        changed[track] = heads;
        while (changed.Count > 0 && changed[^1].Count == 0)
            changed.RemoveAt(changed.Count - 1);
        return changed.Count > 0 ? changed : null;
    }

    /// One stack of heads to edit, top to bottom; the first's lamps are numbered firstLamp (NetworkMeshes.LampsOf). The stack as
    /// changed, or null if it wasn't. Removable: whether its last head can go (a mast signal keeps one).
    private IReadOnlyList<SignalHeadType>? HeadStack(string id, PlacedObject picked, IReadOnlyList<SignalHeadType> heads, int firstLamp, bool removable)
    {
        var names = HeadTypes.Select(t => t.Name()).ToArray();
        for (var i = 0; i < heads.Count; i++)
        {
            var type = Array.IndexOf(HeadTypes, heads[i]);
            var pick = ToolPanel.Mode($"{id}type{i}", names, type, 3);
            if (pick != type)
                return [.. heads.Select((h, k) => k == i ? HeadTypes[pick] : h)];
            LampToggles($"{id}lamp{i}", picked, firstLamp + i, heads[i]);
            switch (Kit.Buttons($"{id}order{i}", ["Up", "Down", "Remove"], [i > 0, i + 1 < heads.Count, removable || heads.Count > 1]))
            {
                case 0:
                    return [.. heads.Select((h, k) => k == i - 1 ? heads[i] : k == i ? heads[i - 1] : h)];
                case 1:
                    return [.. heads.Select((h, k) => k == i + 1 ? heads[i] : k == i ? heads[i + 1] : h)];
                case 2:
                    return [.. heads.Where((_, k) => k != i)];
            }

            if (i + 1 < heads.Count)
                ImGui.Separator();
        }

        if (heads.Count < SignalHeads.MaxStack)
        {
            var add = Kit.Buttons($"{id}add", HeadTypes.Select(t => "+ " + t.Name()).ToArray());
            if (add >= 0)
                return [.. heads, HeadTypes[add]];
        }

        return null;
    }

    /// A head's lamps to light by hand: a colour-light head's three lenses, any of them at once; a searchlight's aspects, one at a time.
    private void LampToggles(string id, PlacedObject picked, int head, SignalHeadType type)
    {
        var network = _network!;
        var lit = network.LampsOf(picked.Id, head);
        var shown = type.Shown(lit);
        var first = true;
        foreach (var (lamp, _, _) in NetworkMeshes.Lamps)
        {
            if (!type.Lenses().HasFlag(lamp))
                continue;
            if (!first)
                ImGui.SameLine(0f, S(10f));
            first = false;
            var on = shown.HasFlag(lamp);
            if (Kit.Toggle($"{id}{lamp}", lamp.ToString(), ref on))
                network.SetLamps(picked, head, type == SignalHeadType.Searchlight ? (on ? lamp : SignalLamps.None) : on ? shown | lamp : shown & ~lamp);
        }
    }
}
