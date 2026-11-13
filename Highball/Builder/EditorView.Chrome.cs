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
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The editor's tool strip down the left, the Place palette, and what the top bar and status line say about it.
public sealed partial class EditorView
{
    public const float StripWidth = 92f;
    private const float DockPanelWidth = 320f;
    private const float StripButton = 62f;

    /// The strip, by what each part is for: each section's title, then its buttons.
    private static readonly (string Section, (Tool Tool, PlaceGroup Group, ToolIcon Icon, string Name, string Tip)[] Items)[] Strip =
    [
        ("", [(Tool.Look, default, ToolIcon.Look, "Free Look", "Free Look (1)")]),
        ("TRACK",
        [
            (Tool.Follow, default, ToolIcon.Lay, "Lay", "Lay track along guides (=)"),
            (Tool.Pen, default, ToolIcon.Pen, "Pen", "Draw track (8)"),
            (Tool.Track, default, ToolIcon.Edit, "Edit", "Edit control points (7)"),
            (Tool.Shape, default, ToolIcon.Shape, "Shape", "Shape a line's curves (-)"),
            (Tool.Parallel, default, ToolIcon.Parallel, "Parallel", "Lay tracks beside a track (P)"),
            (Tool.Switches, default, ToolIcon.Switches, "Switches", "Switches (0)"),
        ]),
        ("BUILD",
        [
            (Tool.Place, PlaceGroup.Assemblies, ToolIcon.Pieces, "Assembly", "Track pieces and crossings"),
            (Tool.Place, PlaceGroup.Structures, ToolIcon.Bridge, "Structures", "Bridges"),
        ]),
        ("SIGNAL",
        [
            (Tool.Place, PlaceGroup.Signals, ToolIcon.Signal, "Signals", "Signals"),
            (Tool.Circuits, default, ToolIcon.Circuits, "Circuits", "Track circuits (9)"),
        ]),
        ("WORLD",
        [
            (Tool.Road, default, ToolIcon.Road, "Roads", "Roads (;)"),
            (Tool.Place, PlaceGroup.Lineside, ToolIcon.UtilityPole, "Lineside", "Poles, lights and loose ties"),
            (Tool.Raise, default, ToolIcon.Terrain, "Terrain", "Terrain (2-6)"),
        ]),
    ];

    /// The Place tool, on one of its buttons: what's in hand is let go if it belongs to another.
    private void OpenPlace(PlaceGroup group)
    {
        _tool = Tool.Place;
        _objects.Open(group);
    }

    private static readonly (Tool Tool, string Name)[] Brushes =
        [(Tool.Raise, "Raise"), (Tool.Lower, "Lower"), (Tool.Smooth, "Smooth"), (Tool.Flatten, "Flatten"), (Tool.Paint, "Paint")];


    /// The terrain brush last used, which the strip's Terrain button goes back to.
    private Tool _brush = Tool.Raise;

    private static bool IsBrush(Tool tool) => tool is Tool.Raise or Tool.Lower or Tool.Smooth or Tool.Flatten or Tool.Paint;

    private YardTool ActiveYard => _tool == Tool.Parallel ? _parallel : _yard;

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

    /// What's unsaved, for the top bar.
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

    private void ToolStrip()
    {
        if (_terrain is null)
            return;

        ImGui.SetNextWindowPos(new float2(0f, _headerBottom), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(StripWidth, Math.Max(100f, _footerTop - _headerBottom)), ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 6f));
        if (ImGui.Begin("Tools"u8, flags))
        {
            var draw = ImGui.GetWindowDrawList();
            foreach (var (section, items) in Strip)
            {
                StripSection(draw, section);
                foreach (var (tool, group, icon, name, tip) in items)
                {
                    var on = tool switch
                    {
                        Tool.Raise => IsBrush(_tool),
                        Tool.Place => _tool == Tool.Place && GroupOf(_place.Mode) == group,
                        _ => _tool == tool,
                    };
                    if (StripItem(draw, icon, name, tip, on))
                    {
                        if (tool == Tool.Place)
                            OpenPlace(group);
                        else
                            _tool = tool == Tool.Raise ? _brush : tool;
                    }
                }
            }

            // Not a tool: the train panel, open beside whatever tool is in hand.
            StripSection(draw, "");
            if (StripItem(draw, ToolIcon.Consist, "Consist", "Consist", _trains.Open))
                _trains.Open = !_trains.Open;

            // Undo and redo at the foot of the strip.
            var half = (StripWidth - 10f) * 0.5f;
            var foot = new float2(4f, MathF.Max(ImGui.GetCursorScreenPos().Y + 4f, _footerTop - half - 6f));
            if (HistoryButton(draw, "Undo", ToolIcon.Undo, foot, half, CanUndo))
                UndoLast();
            if (HistoryButton(draw, "Redo", ToolIcon.Redo, foot + new float2(half + 2f, 0f), half, CanRedo))
                RedoLast();
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// A button on the strip: its icon over its name, lit when it's on.
    private static bool StripItem(ImDrawListPtr draw, ToolIcon icon, string name, string tooltip, bool on)
    {
        var at = ImGui.GetCursorScreenPos() + new float2(4f, 0f);
        var size = new float2(StripWidth - 8f, StripButton);
        ImGui.SetCursorScreenPos(at);
        var clicked = ImGui.InvisibleButton(name, size);
        var hovered = ImGui.IsItemHovered();
        if (on)
            draw.AddRectFilled(at, at + size, Accent, 5f);
        else if (hovered)
            draw.AddRectFilled(at, at + size, StrokeStrong, 5f);

        var color = on ? OnAccent : hovered ? Style.Text : TextDim;
        ToolIcons.Draw(draw, icon, at + new float2(size.X * 0.5f, 21f), 11f, color, 1.6f);
        Canvas.Text(draw, Medium, 14f, at + new float2(size.X * 0.5f, 39f), color, name, Align.Center);
        if (hovered)
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// A section's start on the strip: a rule (but at the very top), and its title under it in small spaced capitals.
    private static void StripSection(ImDrawListPtr draw, string title)
    {
        if (ImGui.GetCursorScreenPos().Y > ImGui.GetWindowPos().Y + 10f)
            StripGap(draw);
        if (title.Length == 0)
            return;
        var at = ImGui.GetCursorScreenPos();
        Canvas.Text(draw, Medium, 11.5f, new float2(at.X + StripWidth * 0.5f, at.Y + 1f), TextDim, title, Align.Center);
        ImGui.Dummy(new float2(1f, 15f));
    }

    private static void StripGap(ImDrawListPtr draw)
    {
        var x = ImGui.GetCursorScreenPos().X + 4f;
        var y = ImGui.GetCursorScreenPos().Y + 2f;
        draw.AddLine(new float2(x + 8f, y), new float2(x + StripWidth - 16f, y), StrokeStrong, 1f);
        ImGui.Dummy(new float2(1f, 5f));
    }

    private static bool HistoryButton(ImDrawListPtr draw, string name, ToolIcon icon, float2 at, float size, bool enabled)
    {
        ImGui.SetCursorScreenPos(at);
        var clicked = ImGui.InvisibleButton(name, new float2(size, size)) && enabled;
        var hovered = ImGui.IsItemHovered();
        if (hovered && enabled)
            draw.AddRectFilled(at, at + new float2(size, size), StrokeStrong, 5f);
        ToolIcons.Draw(draw, icon, at + new float2(size, size) * 0.5f, 9f, enabled ? hovered ? Style.Text : TextDim : TextFaint, 1.6f);
        if (hovered)
            ImGui.SetTooltip($"{name} (Ctrl+{(icon == ToolIcon.Undo ? "Z" : "Y")})");
        return clicked;
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


    /// The terrain brushes, their size, and what to paint.
    private void BrushPanel(bool imperial)
    {
        var current = Array.FindIndex(Brushes, b => b.Tool == _tool);
        var pick = ToolPanel.Mode("brush", Brushes.Select(b => b.Name).ToArray(), current, 3);
        if (pick != current && pick >= 0)
            _tool = Brushes[pick].Tool;

        SyncBrush().Panel(imperial);
    }

}
