using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The editor's tools on the workspace rail, each a group of modes picked at the top of its inspector, and what the menu
/// bar and status line say about it.
public sealed partial class EditorView
{
    /// One mode of a rail tool: the tool it puts in hand (the Place tool on one of its groups).
    private readonly record struct ToolMode(Tool Tool, PlaceGroup Group, string Name);

    /// A tool on the rail: its icon, its name and key for the tooltip and inspector, and its modes.
    private sealed record RailTool(string Name, string? Key, ToolIcon Icon, ToolMode[] Modes, bool GapAfter = false);

    private static readonly RailTool[] Rail =
    [
        new("Look", "1", ToolIcon.Look, [new(Tool.Look, default, "Look")], GapAfter: true),
        new("Draw", "8", ToolIcon.Draw, [new(Tool.Draw, default, "Track"), new(Tool.Pen, default, "Pen")]),
        new("Edit", "7", ToolIcon.Edit, [new(Tool.Track, default, "Points"), new(Tool.Shape, default, "Shape"), new(Tool.Join, default, "Join")]),
        new("Yard", "=", ToolIcon.Lay, [new(Tool.Follow, default, "Lay"), new(Tool.Parallel, default, "Parallel"), new(Tool.Siding, default, "Siding")]),
        new("Switches", "0", ToolIcon.Switches, [new(Tool.Switches, default, "Switches")], GapAfter: true),
        new("Place", "\\", ToolIcon.Pieces, [new(Tool.Place, PlaceGroup.Assemblies, "Pieces"), new(Tool.Place, PlaceGroup.Structures, "Bridges"), new(Tool.Place, PlaceGroup.Lineside, "Lineside")]),
        new("Signals", "9", ToolIcon.Signal, [new(Tool.Place, PlaceGroup.Signals, "Signals"), new(Tool.Circuits, default, "Circuits")]),
        new("Roads", ";", ToolIcon.Road, [new(Tool.Road, default, "Roads")]),
        new("Terrain", "2-6", ToolIcon.Terrain,
        [
            new(Tool.Raise, default, "Raise"), new(Tool.Lower, default, "Lower"), new(Tool.Smooth, default, "Smooth"),
            new(Tool.Flatten, default, "Flatten"), new(Tool.Paint, default, "Paint"), new(Tool.ClearBallast, default, "Ballast"),
        ], GapAfter: true),
    ];

    /// The mode last used in each rail tool, which its button goes back to.
    private readonly int[] _lastMode = new int[Rail.Length];

    /// Whether a mode is the one in hand.
    private bool InHand(ToolMode mode) => mode.Tool == _tool && (mode.Tool != Tool.Place || GroupOf(_place.Mode) == mode.Group);

    /// The rail tool in hand, and its mode's index in it.
    private (int Tool, int Mode) Current
    {
        get
        {
            for (var t = 0; t < Rail.Length; t++)
                for (var m = 0; m < Rail[t].Modes.Length; m++)
                    if (InHand(Rail[t].Modes[m]))
                        return (t, m);
            return (0, 0);
        }
    }

    private void Use(ToolMode mode)
    {
        if (mode.Tool == Tool.Place)
            _objects.Open(mode.Group);
        _tool = mode.Tool;
        _trains.Open = false;
    }

    /// The rail tool's modes in the top bar.
    public void ToolModes()
    {
        if (_terrain is null || _trains.Open)
            return;
        var (index, mode) = Current;
        var rail = Rail[index];
        var pick = Workspace.Modes($"mode.{rail.Name}", rail.Modes.Select(m => m.Name).ToArray(), mode);
        if (pick != mode && pick >= 0)
        {
            Use(rail.Modes[pick]);
            _lastMode[index] = pick;
        }
    }

    /// Whether the Train inspector is showing in place of the tool's.
    public bool TrainInspector => _trains.Open;

    /// The editor's tools on the rail (inside Workspace.BeginRail), then the train, then undo and redo at its foot.
    public void RailItems()
    {
        var (current, _) = Current;
        for (var t = 0; t < Rail.Length; t++)
        {
            var tool = Rail[t];
            var tip = tool.Key is null ? tool.Name : $"{tool.Name} ({tool.Key})";
            if (Workspace.RailItem($"rail.{tool.Name}", tool.Icon, tip, t == current && !_trains.Open, _terrain is not null))
                Use(tool.Modes[Math.Clamp(_lastMode[t], 0, tool.Modes.Length - 1)]);
            if (tool.GapAfter)
                Workspace.RailGap();
        }

        if (Workspace.RailItem("rail.train", ToolIcon.Consist, "Train (C)", _trains.Open, _terrain is not null))
        {
            _trains.Open = !_trains.Open;
            if (_trains.Open)
                _tool = Tool.Look;
        }

        var (undo, redo) = Workspace.RailHistory(CanUndo, CanRedo);
        if (undo)
            UndoLast();
        if (redo)
            RedoLast();
    }

    private YardTool ActiveYard => _tool == Tool.Parallel ? _parallel : _yard;

    private static bool IsBrush(Tool tool) => tool is Tool.Raise or Tool.Lower or Tool.Smooth or Tool.Flatten or Tool.Paint or Tool.ClearBallast;

    public bool CanUndo => _undo.Count > 0 && _terrain is not null;
    public bool CanRedo => _redo.Count > 0 && _terrain is not null;

    public void UndoLast()
    {
        if (_terrain is not null && _undo.TryPop(out var edit))
        {
            Redo(edit, undo: true);
            _redo.Push(edit);
        }
    }

    public void RedoLast()
    {
        if (_terrain is not null && _redo.TryPop(out var edit))
        {
            Redo(edit, undo: false);
            _undo.Push(edit);
        }
    }

    /// Whether anything's unsaved, for the top bar's dot.
    public bool Unsaved => _terrain?.Source is { } source && (source.Unsaved || source.Network.Unsaved || _circuitEditor?.Unsaved == true);

    /// What's unsaved, for the dot's tooltip.
    public string SaveState
    {
        get
        {
            if (_terrain is not { } terrain)
                return "Opening";
            var source = terrain.Source;
            var state = source.Unsaved || source.Network.Unsaved || _circuitEditor?.Unsaved == true ? "Unsaved changes" : "Saved";
            return _rebuild is not null ? $"{state}, updating" : state;
        }
    }

    /// Where the pointer is on the ground and how the camera stands, for the status line.
    public string Status
    {
        get
        {
            if (_terrain is not { } terrain)
                return "";
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            string Height(double metres) => imperial
                ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:#,0} FT")
                : string.Create(CultureInfo.InvariantCulture, $"{metres:#,0} M");

            // The pointer's ground over sea level (MSL), the camera over the ground under it (AGL), and how fast it's flying.
            var camera = $"AGL {Height(_camera.Y - terrain.Source.HeightAt(_camera.X, _camera.Z))}";
            if (!_topDown)
                camera += imperial
                    ? string.Create(CultureInfo.InvariantCulture, $"   {_eye.Speed * 2.23694f:0} MPH")
                    : string.Create(CultureInfo.InvariantCulture, $"   {_eye.Speed * 3.6f:0} KM/H");
            if (_pointerAt is not { } at)
                return camera;
            var geo = _route.Projection!.Inverse(new RoutePoint(at.X, -at.Z));
            return string.Create(CultureInfo.InvariantCulture, $"{geo.Lat:0.00000}, {geo.Lon:0.00000}   MSL {Height(at.Y)}   {camera}");
        }
    }

    private static readonly (string? Id, string Name)[] Beds =
    [
        (null, "Automatic"), (nameof(TrackBed.Mainline), "Mainline"), (nameof(TrackBed.Low), "Low"), (nameof(TrackBed.Sunken), "Sunken"), (nameof(TrackBed.Street), "Street"),
    ];

    private bool _bedHeightSet;
    private float _bedHeight = NetworkLine.BallastHeight;

    /// What track's laid on: for what's laid next, and for what's selected in the Edit tool.
    private void BedPanel()
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var tracks = _tracks!;
        Kit.BeginCard("Track bed");
        var current = Array.FindIndex(Beds, b => b.Id == tracks.NewBed);
        var pick = ToolPanel.Mode("bed", Beds.Select(b => b.Id is null ? "Auto" : b.Id == nameof(TrackBed.Mainline) ? "Main" : b.Name).ToArray(), current, 3);
        if (pick != current && pick >= 0)
        {
            tracks.NewBed = Beds[pick].Id;
            _bedHeightSet = false;
            if (Enum.TryParse<TrackBed>(Beds[pick].Id, out var chosen))
                _bedHeight = NetworkLine.BedHeightOf(chosen);
        }

        var street = tracks.NewBed == nameof(TrackBed.Street);
        var height = imperial ? _bedHeight * FeetPerMeter * 12f : _bedHeight * 100f;
        if (ToolPanel.Value("bedheight", imperial ? "Height (in)" : "Height (cm)", ref height, imperial ? 1f : 2f, imperial ? 59f : 150f, "0", enabled: !street))
        {
            _bedHeight = imperial ? height / 12f / FeetPerMeter : height / 100f;
            _bedHeightSet = true;
        }

        tracks.NewBedHeight = _bedHeightSet && !street ? _bedHeight : null;

        if (_tool == Tool.Track && Kit.Button("applybed", "Apply to Selection", 0f, ButtonKind.Secondary, _selection.Count > 0))
            Committed(tracks.SetBed(_selection, tracks.NewBed, tracks.NewBedHeight));
        Kit.EndCard();
    }

    /// What class track's laid as: for what's laid next, and for what's selected in the Edit tool. Auto leaves it to the
    /// tool (drawn track takes the class of the track it leaves; track along a guide is yard).
    private void ClassPanel()
    {
        var tracks = _tracks!;
        Kit.BeginCard("Track class");
        var classes = TrackEditor.Settable;
        var current = tracks.NewClass is { } chosen ? Array.IndexOf(classes, chosen) + 1 : 0;
        var labels = new[] { "Auto" }.Concat(classes.Select(c => c == RailClass.Industrial ? "Indust" : RailColours.Name((int)c))).ToArray();
        var pick = ToolPanel.Mode("railclass", labels, current, 3);
        if (pick != current && pick >= 0)
            tracks.NewClass = pick == 0 ? null : classes[pick - 1];

        if (_tool == Tool.Track && Kit.Button("applyclass", "Apply to Selection", 0f, ButtonKind.Secondary, _selection.Count > 0 && tracks.NewClass is not null))
            Committed(tracks.SetClass(_selection, tracks.NewClass!.Value));
        Kit.EndCard();
    }


    /// The terrain brush's size and what it paints (the inspector's mode row picks the brush).
    private void BrushPanel(bool imperial) => SyncBrush().Panel(imperial);

}
