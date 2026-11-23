using System.Globalization;
using Brutal.ImGuiApi;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The editor's docked windows: the Outliner (everything in the route, by kind, picked with a click) and the Inspector (the
/// tool in hand, its modes and settings; or the train, vehicle or coupling picked), and what the View and Window menus add.
public sealed partial class EditorView
{
    /// The most of one kind the Outliner lists before saying how many more there are (the search narrows it).
    private const int OutlinerLimit = 300;

    private readonly ImInputString _outlinerFilter = new(64, ""u8);

    /// The scenery kinds in the Outliner, grouped as the Place tool's palettes have them.
    private static readonly (string Name, PlacedKind[] Kinds)[] OutlinerGroups =
    [
        ("Signals", [PlacedKind.Signal, PlacedKind.SignalBridge, PlacedKind.HandThrow]),
        ("Crossings", [PlacedKind.GradeCrossing]),
        ("Utility poles", [PlacedKind.PoleTelegraph, PlacedKind.PoleDistribution, PlacedKind.PoleTelecom]),
        ("Light poles", [PlacedKind.LightCobra, PlacedKind.LightYardMast, PlacedKind.LightPlatform]),
        ("Ties", [PlacedKind.TieSingle, PlacedKind.TieSection, PlacedKind.TieSectionBallast]),
    ];

    /// The Inspector: the train while the Train tool's in hand, else the tool's modes and settings.
    public void Inspector()
    {
        if (_terrain is null)
        {
            Kit.Note(Failed ? "The terrain couldn't be opened." : "Loading terrain...");
            return;
        }

        if (_trains.Open)
        {
            ToolHeading("Train", "C");
            _trains.BuilderInspector();
            return;
        }

        var (index, mode) = Current;
        var rail = Rail[index];
        ToolHeading(rail.Name, rail.Key);
        if (rail.Modes.Length > 1)
        {
            var pick = ToolPanel.Mode($"mode.{rail.Name}", rail.Modes.Select(m => m.Name).ToArray(), mode, 6);
            if (pick != mode && pick >= 0)
            {
                Use(rail.Modes[pick]);
                _lastMode[index] = pick;
            }

            ImGui.Dummy(new float2(1f, S(4f)));
        }

        if (_tool == Tool.Look)
        {
            Kit.Note("Nothing in hand. Pick a tool on the left, or something in the Outliner to go to it.");
            return;
        }

        Panel();
    }

    /// The tool's name and key over its settings.
    private static void ToolHeading(string name, string? key)
    {
        ImGui.PushFont(Bold, 0f);
        ImGui.Text(name);
        ImGui.PopFont();
        if (key is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(key);
        }

        ImGui.Separator();
        ImGui.Dummy(new float2(1f, S(2f)));
    }

    /// The Outliner: the route's track, switches, scenery and trains, as a tree. A click on something goes to it and picks
    /// it with the tool it belongs to.
    public void Outliner()
    {
        if (_terrain is not { } terrain)
        {
            ImGui.TextDisabled("Loading terrain...");
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##outliner.filter"u8, "Search"u8, _outlinerFilter, ImGuiInputTextFlags.None, null, IntPtr.Zero);
        var filter = _outlinerFilter.ToString().Trim();
        ImGui.Separator();

        if (!ImGui.BeginChild("##outliner.tree"u8, new float2(0f, 0f), ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        var source = terrain.Source;
        var open = filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

        // Track: how much is laid, and its switches.
        if (source.Lines is { } lines)
        {
            var rails = lines.Lines.Count(l => l.Rail);
            Leaf($"Track##outliner.track", $"{rails:#,0} lines");
            var turnouts = lines.Turnouts;
            if (Branch("Switches##outliner.switches", turnouts.Count, open))
            {
                var shown = 0;
                foreach (var t in turnouts)
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node}");
                    if (!Matches(name, filter))
                        continue;
                    if (++shown > OutlinerLimit)
                        break;
                    if (Item(name, $"outliner.sw{t.Node}", _tool == Tool.Switches && _switch == t.Node))
                        GoToSwitch(t);
                }

                More(shown);
                ImGui.TreePop();
            }
        }

        // Scenery, by palette.
        foreach (var (group, kinds) in OutlinerGroups)
        {
            var count = source.Objects.Count(o => kinds.Contains(o.Kind));
            if (!Branch($"{group}##outliner.{group}", count, open | (count == 0 ? ImGuiTreeNodeFlags.Leaf : 0)))
                continue;
            foreach (var kind in kinds)
            {
                var ofKind = source.Objects.Where(o => o.Kind == kind).ToList();
                if (ofKind.Count == 0)
                    continue;
                if (kinds.Length > 1 && !Branch($"{kind.Name()}##outliner.k{kind}", ofKind.Count, open))
                    continue;
                var shown = 0;
                foreach (var o in ofKind)
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"{kind.Name()} {o.Id}");
                    if (!Matches(name, filter))
                        continue;
                    if (++shown > OutlinerLimit)
                        break;
                    if (Item(name, $"outliner.o{o.Id}", _tool == Tool.Place && _place.Selected == o.Id))
                        GoToObject(o);
                }

                More(shown);
                if (kinds.Length > 1)
                    ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        // Trains: each cut, and its vehicles.
        var cuts = _trains.Cuts.ToList();
        if (Branch("Trains##outliner.trains", cuts.Count, open | (cuts.Count == 0 ? ImGuiTreeNodeFlags.Leaf : 0)))
        {
            for (var i = 0; i < cuts.Count; i++)
            {
                var (train, driving, focused) = cuts[i];
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | (focused && _trains.Open && _trains.Selected < 0 ? ImGuiTreeNodeFlags.Selected : 0);
                var label = driving ? $"{train.Name}  (driving)##outliner.cut{i}" : $"{train.Name}##outliner.cut{i}";
                var expanded = ImGui.TreeNodeEx(label, flags);
                if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
                    PickTrain(i, -1);
                if (!expanded)
                    continue;
                for (var car = 0; car < train.Cars.Count; car++)
                {
                    var name = train.Cars[car].Label;
                    if (!Matches(name, filter))
                        continue;
                    if (Item(name, $"outliner.cut{i}.car{car}", focused && _trains.Open && _trains.Selected == car))
                        PickTrain(i, car);
                }

                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        ImGui.EndChild();
    }

    private static bool Matches(string name, string filter) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// A row with nothing under it: a name, and a reading at the right.
    private static void Leaf(string label, string right)
    {
        ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
        RightText(right);
    }

    /// A group: its name, and how many are in it at the right. Returns whether it's open (TreePop after).
    private static bool Branch(string label, int count, ImGuiTreeNodeFlags flags)
    {
        var open = ImGui.TreeNodeEx(label, flags | ImGuiTreeNodeFlags.SpanAvailWidth);
        RightText(count.ToString("#,0", CultureInfo.InvariantCulture));
        return open;
    }

    private static void RightText(string text)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.TextDisabled(text);
    }

    /// One thing in a group. Returns true when it's clicked.
    private static bool Item(string name, string id, bool selected)
    {
        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth | (selected ? ImGuiTreeNodeFlags.Selected : 0);
        ImGui.TreeNodeEx($"{name}##{id}", flags);
        return ImGui.IsItemClicked();
    }

    private static void More(int shown)
    {
        if (shown > OutlinerLimit)
            ImGui.TextDisabled($"More than {OutlinerLimit}; search to narrow them");
    }

    /// Picks a switch with the Switches tool and goes to it.
    private void GoToSwitch(Turnout turnout)
    {
        UseTool(Tool.Switches, default);
        _switch = turnout.Node;
        MoveTo(new RoutePoint(turnout.X, -turnout.Z));
    }

    /// Picks a placed object with the Place tool's palette it belongs to, and goes to it.
    private void GoToObject(PlacedObject o)
    {
        var group = o.Kind switch
        {
            PlacedKind.Signal or PlacedKind.SignalBridge or PlacedKind.HandThrow => PlaceGroup.Signals,
            PlacedKind.GradeCrossing => PlaceGroup.Assemblies,
            _ => PlaceGroup.Lineside,
        };
        UseTool(Tool.Place, group);
        _place.Selected = o.Id;
        MoveTo(_route.Projection!.Forward(new GeoPoint(o.Lat, o.Lon)));
    }

    /// Looks at a cut (car -1) or picks one of its vehicles, with the Train tool in hand.
    private void PickTrain(int cut, int car)
    {
        _tool = Tool.Look;
        if (car < 0)
            _trains.LookAtCut(cut);
        else
            _trains.PickVehicle(cut, car);
    }

    /// Puts a tool in hand by its mode on the rail, remembering it as that rail tool's last.
    private void UseTool(Tool tool, PlaceGroup group)
    {
        for (var t = 0; t < Rail.Length; t++)
            for (var m = 0; m < Rail[t].Modes.Length; m++)
                if (Rail[t].Modes[m].Tool == tool && (tool != Tool.Place || Rail[t].Modes[m].Group == group))
                {
                    Use(Rail[t].Modes[m]);
                    _lastMode[t] = m;
                    return;
                }
    }

    /// The View menu's train items: the camera riding along, the name tags.
    public void MenuViewTrains()
    {
        if (ImGui.MenuItem("Camera Follows Train", "", _trains.Follow, _trains.Train is not null))
            _trains.Follow = !_trains.Follow;
        if (ImGui.MenuItem("Vehicle Name Tags", "", _trains.ShowTags, true))
            _trains.ShowTags = !_trains.ShowTags;
    }

    /// Where the editor's own windows (Drive, Consist Builder) dock when they're first opened.
    public void DockWindowsAt(ImGuiID node) => _trains.DockAt = node;

    /// The Window menu's editor windows.
    public void MenuWindow()
    {
        if (ImGui.MenuItem("Drive (debug)", "", _trains.DriveOpen, true))
            _trains.DriveOpen = !_trains.DriveOpen;
        if (ImGui.MenuItem("Consist Builder", "", _trains.ConsistBuilderOpen, true))
            _trains.ConsistBuilderOpen = !_trains.ConsistBuilderOpen;
        if (ImGui.MenuItem("Air Graphs", "", _trains.Graphs.Open, true))
            _trains.Graphs.Open = !_trains.Graphs.Open;
        if (ImGui.MenuItem("Audio Editor", "", _audioEditor.Open, true))
            _audioEditor.Open = !_audioEditor.Open;
    }
}
