using Brutal.ImGuiApi;
using Highball.Routes;
using Highball.Signalling;
using Highball.World;
using RailWerkz.Core.Logging;

namespace Highball.Builder;

/// Route > Lay Out Signals: the route's signals laid out at its track circuits' joints (SignalLayout), and Route > Remove Laid-Out
/// Signals to take them away again. Either is one undo; before laying out, Objects.json is copied to Objects.before-signals.json in
/// the route's folder, so the route as it was can always be had back by hand too.
public sealed partial class EditorView
{
    private const string BeforeSignals = "Objects.before-signals.json";

    private void MenuSignals()
    {
        var source = _terrain?.Source;
        var ready = source?.Lines is not null && _network is not null;
        if (ImGui.MenuItem("Lay Out Signals", "", false, ready))
            LayOutSignals();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Control points at every switch on main and branch lines, automatic signals at every block joint between.\nReplaces signals laid out before; keeps those placed by hand. Ctrl+Z undoes it.");
        var laid = source?.Objects.Count(o => o.Laid) ?? 0;
        if (ImGui.MenuItem(laid > 0 ? $"Remove Laid-Out Signals ({laid})" : "Remove Laid-Out Signals", "", false, ready && laid > 0))
            RemoveLaidSignals();
    }

    /// Lays the route's signals out afresh: those laid out before go, the new ones come, as one undo.
    private void LayOutSignals()
    {
        if (_terrain?.Source is not { } source || source.Lines is not { } lines || _network is not { } network)
            return;

        // The circuits as the Circuits tool has them; if it's not built them yet, the joints it would first lay out.
        TrackGraph graph;
        List<(int Edge, double S)> joints;
        if (_circuitEditor?.Circuits is { } circuits)
        {
            graph = circuits.Graph;
            joints = [.. circuits.Joints.Select(j => (j.Edge, j.S))];
        }
        else
        {
            graph = TrackGraph.Build([.. source.Network.Ways.Values.Where(w => !RouteNetwork.IsGuide(w))], _route.Projection!);
            var data = RouteSignalling.Load(_route);
            var generated = TrackCircuits.Generate(graph, data.WorldJoints(_route.Projection!), data.BlockLength);
            var next = data.Next;
            joints = [.. TrackCircuits.Build(graph, generated, [], ref next).Joints.Select(j => (j.Edge, j.S))];
        }

        // The route as it was, kept beside it.
        try
        {
            var objects = Path.Combine(_route.Folder, "Objects.json");
            if (File.Exists(objects))
                File.Copy(objects, Path.Combine(_route.Folder, BeforeSignals), overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warn($"Signal layout: couldn't keep a copy of the objects: {e.Message}");
        }

        var kept = source.Objects.Where(o => !o.Laid).ToList();
        var id = DateTime.UtcNow.Ticks;
        var plan = SignalLayout.Lay(graph, joints, lines, kept, network.SpanPoints,
            (kind, x, z, heading, node) => _objects.Placed(kind, x, z, heading, node, id: id++), _objects.PlacedAtWorld);

        var edits = source.Objects.Where(o => o.Laid).Select(o => new PlacedEdit(o, null))
            .Concat(plan.Signals.Select(o => new PlacedEdit(null, o))).ToList();
        if (edits.Count == 0)
        {
            Notify("No signals to lay out: no main or branch line with circuit joints");
            return;
        }

        ApplyPlaced(edits);
        Record(new PlacedRunEdit(edits));
        var text = $"Laid out {plan.Signals.Count} signals ({plan.Bridges} on bridges): {plan.ControlPoints} control point and {plan.Automatics} automatic positions" +
                   (plan.Skipped > 0 ? $", {plan.Skipped} already signalled or with no track" : "") + ". Ctrl+Z undoes it.";
        Notify(text);
        Log.Info($"Signal layout: {text} Copy of the objects before: {BeforeSignals}");
    }

    /// Takes away every signal and signal bridge Lay Out Signals put there, as one undo.
    private void RemoveLaidSignals()
    {
        if (_terrain?.Source is not { } source)
            return;
        var edits = source.Objects.Where(o => o.Laid).Select(o => new PlacedEdit(o, null)).ToList();
        if (edits.Count == 0)
            return;
        ApplyPlaced(edits);
        Record(new PlacedRunEdit(edits));
        Notify($"Removed {edits.Count} laid-out signals. Ctrl+Z puts them back.");
        Log.Info($"Signal layout: removed {edits.Count}");
    }
}
