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
    public const float StripWidth = 76f;
    private const float DockPanelWidth = 320f;
    private const float StripButton = 58f;

    private static readonly (Tool Tool, ToolIcon Icon, string Name, string Key, bool Gap)[] Strip =
    [
        (Tool.Look, ToolIcon.Look, "Free Look", "1", true),
        (Tool.Track, ToolIcon.Edit, "Edit", "7", false),
        (Tool.Pen, ToolIcon.Pen, "Pen", "8", false),
        (Tool.Shape, ToolIcon.Shape, "Shape", "-", false),
        (Tool.Follow, ToolIcon.Lay, "Lay", "=", false),
        (Tool.Parallel, ToolIcon.Parallel, "Parallel", "P", false),
        (Tool.Switches, ToolIcon.Switches, "Switches", "0", true),
        (Tool.Place, ToolIcon.Place, "Objects", "O", false),
        (Tool.Circuits, ToolIcon.Circuits, "Circuits", "9", true),
        (Tool.Raise, ToolIcon.Terrain, "Terrain", "2-6", false),
    ];

    private static readonly (Tool Tool, string Name)[] Brushes =
        [(Tool.Raise, "Raise"), (Tool.Lower, "Lower"), (Tool.Smooth, "Smooth"), (Tool.Flatten, "Flatten"), (Tool.Paint, "Paint")];

    /// The Objects palette's lineside menus: each one's title, what it places, and what's counted beside it.
    private static readonly (string Menu, PlaceMode Mode, ToolIcon Icon, string Name, PlacedKind Kind)[] Lineside =
    [
        ("Signals", PlaceMode.Signal, ToolIcon.Signal, "Signal", PlacedKind.Signal),
        ("Hand Throws", PlaceMode.HandThrow, ToolIcon.HandThrow, "Hand throw", PlacedKind.HandThrow),
        ("Grade Crossings", PlaceMode.GradeCrossing, ToolIcon.Crossing, "Grade crossing", PlacedKind.GradeCrossing),
    ];

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
                ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:#,0} ft")
                : string.Create(CultureInfo.InvariantCulture, $"{metres:#,0} m");

            var above = Height(_camera.Y - terrain.Source.HeightAt(_camera.X, _camera.Z));
            var camera = _topDown
                ? $"view {above}"
                : imperial
                    ? string.Create(CultureInfo.InvariantCulture, $"{above} up, {_speed * 2.23694f:0} mph")
                    : string.Create(CultureInfo.InvariantCulture, $"{above} up, {_speed * 3.6f:0} km/h");
            if (_pointerAt is not { } at)
                return camera;
            var geo = _route.Projection!.Inverse(new RoutePoint(at.X, -at.Z));
            return string.Create(CultureInfo.InvariantCulture, $"{geo.Lat:0.00000}, {geo.Lon:0.00000}   {Height(at.Y)}   {camera}");
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
            foreach (var (tool, icon, name, key, gap) in Strip)
            {
                var on = tool == Tool.Raise ? IsBrush(_tool) : _tool == tool;
                var at = ImGui.GetCursorScreenPos() + new float2(4f, 0f);
                var size = new float2(StripWidth - 8f, StripButton);
                ImGui.SetCursorScreenPos(at);
                if (ImGui.InvisibleButton(name, size))
                    _tool = tool == Tool.Raise ? _brush : tool;
                var hovered = ImGui.IsItemHovered();
                if (on)
                    draw.AddRectFilled(at, at + size, Accent, 5f);
                else if (hovered)
                    draw.AddRectFilled(at, at + size, StrokeStrong, 5f);

                var color = on ? OnAccent : hovered ? Style.Text : TextDim;
                ToolIcons.Draw(draw, icon, at + new float2(size.X * 0.5f, 20f), 11f, color, 1.6f);
                Canvas.Text(draw, Medium, 12f, at + new float2(size.X * 0.5f, 38f), color, name, Align.Center);
                if (hovered)
                    ImGui.SetTooltip($"{name} ({key})");

                if (gap)
                {
                    var y = ImGui.GetCursorScreenPos().Y + 2f;
                    draw.AddLine(new float2(at.X + 8f, y), new float2(at.X + size.X - 8f, y), StrokeStrong, 1f);
                    ImGui.Dummy(new float2(1f, 5f));
                }
            }

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

    private static readonly (StampGroup Group, string Name, int Columns)[] StampGroups =
    [
        (StampGroup.Switches, "Switches", 2), (StampGroup.ThreeWay, "Three-way Switches", 2), (StampGroup.Crossovers, "Crossovers", 2),
        (StampGroup.Scissors, "Scissors Crossovers", 3), (StampGroup.Slips, "Slips", 2), (StampGroup.Diamonds, "Diamonds", 3),
        (StampGroup.Ladders, "Ladders", 3), (StampGroup.Straights, "Straights", 3), (StampGroup.Curves, "Curves", 3),
    ];

    /// The palette: every track piece by kind, drawn from its own track, then what goes beside the track.
    private void PlacePalette(TerrainSource source)
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        _place.Rebuild(imperial);

        var spacing = imperial ? _place.Spacing * FeetPerMeter : _place.Spacing;
        ImGui.SliderFloat(imperial ? "Spacing (ft)"u8 : "Spacing (m)"u8, ref spacing, imperial ? YardTool.MinSpacing * FeetPerMeter : YardTool.MinSpacing, imperial ? YardTool.MaxSpacing * FeetPerMeter : YardTool.MaxSpacing, "%.1f"u8, ImGuiSliderFlags.None);
        _place.Spacing = Math.Clamp(imperial ? spacing / FeetPerMeter : spacing, YardTool.MinSpacing, YardTool.MaxSpacing);

        foreach (var (group, name, columns) in StampGroups)
        {
            if (!ImGui.CollapsingHeader(name, group == StampGroup.Switches ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                continue;
            if (group == StampGroup.Diamonds && PaletteRow(ToolIcon.Diamond, "Join crossing tracks", _place.Mode == PlaceMode.Diamond))
                (_place.Mode, _switchRefused) = (PlaceMode.Diamond, null);
            var column = 0;
            var width = (ImGui.GetContentRegionAvail().X - 6f * (columns - 1)) / columns;
            foreach (var stamp in _place.Catalogue.Where(s => s.Group == group))
            {
                if (column % columns != 0)
                    ImGui.SameLine(0f, 6f);
                column++;
                if (StampTile(stamp, width))
                    (_place.Mode, _place.Stamp, _place.Selected, _switchRefused) = (PlaceMode.Track, stamp, null, null);
            }
        }

        foreach (var (menu, mode, icon, name, kind) in Lineside)
        {
            if (!ImGui.CollapsingHeader($"{menu} ({source.Objects.Count(o => o.Kind == kind)})###{menu}", ImGuiTreeNodeFlags.None))
                continue;
            if (PaletteRow(icon, name, _place.Mode == mode))
                (_place.Mode, _switchRefused) = (mode, null);
        }
    }

    /// A piece on the palette: its plan, its name, lit when it's in hand.
    private bool StampTile(TrackStamp stamp, float width)
    {
        const float height = 62f;
        const float caption = 20f;
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(width, height);
        var on = _place.Mode == PlaceMode.Track && _place.Stamp?.Id == stamp.Id;
        var clicked = ImGui.InvisibleButton(stamp.Id, size) && stamp.Problem is null;
        var hovered = ImGui.IsItemHovered();
        draw.AddRectFilled(at, at + size, on ? Accent : hovered ? RaisedHover : Raised, 4f);

        var color = on ? OnAccent : stamp.Problem is not null ? TextFaint : hovered ? Style.Text : TextDim;
        var sign = _place.Mirror ? -1.0 : 1.0;
        var (minA, maxA, minO, maxO) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
        foreach (var way in stamp.Ways)
            foreach (var (along, off) in way.Points)
                (minA, maxA, minO, maxO) = (Math.Min(minA, along), Math.Max(maxA, along), Math.Min(minO, off * sign), Math.Max(maxO, off * sign));

        var inner = new float2(width - 14f, height - caption - 10f);
        var scale = (float)Math.Min(inner.X / Math.Max(maxA - minA, 1.0), inner.Y / Math.Max(maxO - minO, 1.0));
        var middle = at + new float2(width * 0.5f, (height - caption) * 0.5f + 1f);
        float2 Plot(double along, double off) => middle + new float2((float)(along - (minA + maxA) * 0.5) * scale, (float)(off * sign - (minO + maxO) * 0.5) * scale);
        foreach (var way in stamp.Ways)
        {
            var points = way.Points;
            var skip = Math.Max(1, points.Count / 24);
            for (var i = 0; i < points.Count - 1; i += skip)
            {
                var j = Math.Min(i + skip, points.Count - 1);
                draw.AddLine(Plot(points[i].Along, points[i].Off), Plot(points[j].Along, points[j].Off), color, 1.5f);
            }
        }

        Canvas.Text(draw, Medium, 12f, at + new float2(width * 0.5f, height - caption + 3f), on ? OnAccent : stamp.Problem is not null ? TextFaint : Style.Text, stamp.Label, Align.Center);
        if (hovered)
            ImGui.SetTooltip(stamp.Problem ?? $"{stamp.Name}, {PanelLength(stamp.Length)} of track");
        return clicked;
    }

    /// A row of the Place palette: its icon and name, lit when it's the one in hand.
    private bool PaletteRow(ToolIcon icon, string name, bool on)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(ImGui.GetContentRegionAvail().X, 30f);
        var clicked = ImGui.InvisibleButton(name, size);
        var hovered = ImGui.IsItemHovered();
        if (on)
            draw.AddRectFilled(at, at + size, Accent, 4f);
        else if (hovered)
            draw.AddRectFilled(at, at + size, StrokeStrong, 4f);
        var color = on ? OnAccent : Style.Text;
        ToolIcons.Draw(draw, icon, at + new float2(16f, 15f), 8f, color, 1.4f);
        Canvas.Text(draw, Medium, 14f, at + new float2(34f, 7f), color, name);
        return clicked;
    }

    /// The terrain brushes, their size, and what to paint.
    private void BrushPanel(bool imperial)
    {
        foreach (var (tool, name) in Brushes)
        {
            if (tool is not (Tool.Raise or Tool.Flatten))
                ImGui.SameLine(0f, -1f);
            if (ImGui.RadioButton(name, _tool == tool))
                _tool = tool;
        }

        var radius = imperial ? _radius * FeetPerMeter : _radius;
        if (ImGui.SliderFloat(imperial ? "Radius (ft)"u8 : "Radius (m)"u8, ref radius, imperial ? MinRadius * FeetPerMeter : MinRadius, imperial ? MaxRadius * FeetPerMeter : MaxRadius, "%.0f"u8, ImGuiSliderFlags.Logarithmic))
            _radius = Math.Clamp(imperial ? radius / FeetPerMeter : radius, MinRadius, MaxRadius);

        if (_tool != Tool.Paint)
            return;
        for (var i = 0; i < PaintCovers.Length; i++)
        {
            if (i % 3 != 0)
                ImGui.SameLine(0f, -1f);
            if (ImGui.RadioButton(PaintCovers[i].Name.ToLowerInvariant(), _paint == i))
                _paint = i;
        }
    }

    /// How many tracks to lay beside a track, and how far apart. They go on whichever side of it the pointer is.
    private void ParallelPanel()
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var tracks = _parallel.ParallelTracks;
        if (ImGui.InputInt("Tracks"u8, ref tracks))
            _parallel.ParallelTracks = Math.Clamp(tracks, 1, YardTool.MaxParallel);
        var spacing = imperial ? _parallel.Spacing * FeetPerMeter : _parallel.Spacing;
        if (ImGui.SliderFloat(imperial ? "Spacing (ft)"u8 : "Spacing (m)"u8, ref spacing, imperial ? YardTool.MinSpacing * FeetPerMeter : YardTool.MinSpacing, imperial ? YardTool.MaxSpacing * FeetPerMeter : YardTool.MaxSpacing, "%.1f"u8, ImGuiSliderFlags.None))
            _parallel.Spacing = Math.Clamp(imperial ? spacing / FeetPerMeter : spacing, YardTool.MinSpacing, YardTool.MaxSpacing);

        if (_parallel.Plan is not { } plan)
        {
            ImGui.TextDisabled(_parallel.Anchor is null ? "Click a track where they start" : "Click level with where they end");
            return;
        }

        if (plan.Problem is { } problem)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Stop);
            ImGui.TextWrapped(problem);
            ImGui.PopStyleColor();
        }
        else
            ImGui.TextWrapped(plan.Summary);
    }
}
