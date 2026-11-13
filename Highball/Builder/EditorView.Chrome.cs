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

    /// The Place tool's buttons on the strip, each with its own part of the palette: premade and fitted pieces, structures,
    /// signals, and what stands beside the line.
    private enum PlaceGroup { Assemblies, Structures, Signals, Lineside }

    private static PlaceGroup GroupOf(PlaceMode mode) => mode switch
    {
        PlaceMode.Track or PlaceMode.Diamond or PlaceMode.GradeCrossing => PlaceGroup.Assemblies,
        PlaceMode.Bridge or PlaceMode.RemoveBridge or PlaceMode.EditBridge => PlaceGroup.Structures,
        PlaceMode.Signal or PlaceMode.HandThrow => PlaceGroup.Signals,
        _ => PlaceGroup.Lineside,
    };

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
        if (GroupOf(_place.Mode) == group)
            return;
        _place.Mode = group switch
        {
            PlaceGroup.Assemblies => PlaceMode.Track,
            PlaceGroup.Structures => PlaceMode.EditBridge,
            PlaceGroup.Signals => PlaceMode.Signal,
            _ => PlaceMode.Pole,
        };
        (_place.Selected, _place.Stamp, _place.RunFrom, _place.RunFirst, _bridgeFrom, _switchRefused) = (null, null, null, null, null, null);
        _place.Pen.Clear();
    }

    private static readonly (Tool Tool, string Name)[] Brushes =
        [(Tool.Raise, "Raise"), (Tool.Lower, "Lower"), (Tool.Smooth, "Smooth"), (Tool.Flatten, "Flatten"), (Tool.Paint, "Paint")];


    /// The Objects palette's pole menus.
    private static readonly (string Menu, ToolIcon Icon, PlacedKind[] Kinds)[] PoleMenus =
    [
        ("Utility Poles", ToolIcon.UtilityPole, [PlacedKind.PoleTelegraph, PlacedKind.PoleDistribution, PlacedKind.PoleTelecom]),
        ("Light Poles", ToolIcon.LightPole, [PlacedKind.LightCobra, PlacedKind.LightYardMast, PlacedKind.LightPlatform]),
        ("Ties", ToolIcon.Ties, [PlacedKind.TieSingle, PlacedKind.TieSection, PlacedKind.TieSectionBallast]),
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

    private static readonly (StampGroup Group, string Name, int Columns)[] StampGroups =
    [
        (StampGroup.Switches, "Switches", 2), (StampGroup.ThreeWay, "Three-way Switches", 2), (StampGroup.Crossovers, "Crossovers", 2),
        (StampGroup.Scissors, "Scissors Crossovers", 3), (StampGroup.Slips, "Slips", 2), (StampGroup.Diamonds, "Diamonds", 3),
        (StampGroup.Ladders, "Ladders", 3), (StampGroup.Straights, "Straights", 3), (StampGroup.Curves, "Curves", 3),
    ];

    /// The palette, as much of it as belongs to the button it's on: every track piece by kind, drawn from its own
    /// track; the signals and crossings; or the poles, ties and bridges.
    private void PlacePalette(TerrainSource source)
    {
        switch (GroupOf(_place.Mode))
        {
            case PlaceGroup.Assemblies:
                AssembliesPalette(source);
                break;
            case PlaceGroup.Structures:
                StructuresPalette();
                break;
            case PlaceGroup.Signals:
                Kit.BeginCard("Place");
                if (PaletteRow(ToolIcon.Signal, "Signal", _place.Mode == PlaceMode.Signal, source.Objects.Count(o => o.Kind == PlacedKind.Signal)))
                    (_place.Mode, _switchRefused) = (PlaceMode.Signal, null);
                Kit.EndCard();
                break;
            default:
                LinesidePalette(source);
                break;
        }
    }

    /// What's fitted to where it lands (grade crossings, diamonds), then every premade track piece.
    private void AssembliesPalette(TerrainSource source)
    {
        Kit.BeginCard("Fitted");
        if (PaletteRow(ToolIcon.Crossing, "Grade crossing", _place.Mode == PlaceMode.GradeCrossing, source.Objects.Count(o => o.Kind == PlacedKind.GradeCrossing)))
            (_place.Mode, _switchRefused) = (PlaceMode.GradeCrossing, null);
        if (PaletteRow(ToolIcon.Diamond, "Diamond", _place.Mode == PlaceMode.Diamond))
            (_place.Mode, _switchRefused) = (PlaceMode.Diamond, null);
        Kit.EndCard();
        PiecesPalette();
    }

    private void PiecesPalette()
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        _place.Rebuild(imperial);
        Kit.BeginCard("Track pieces");
        {
            var factor = imperial ? FeetPerMeter : 1f;
            var spacing = _place.Spacing * factor;
            if (ToolPanel.Value("piecespacing", imperial ? "Spacing (ft)" : "Spacing (m)", ref spacing, YardTool.MinSpacing * factor, YardTool.MaxSpacing * factor, "0.0"))
                _place.Spacing = Math.Clamp(spacing / factor, YardTool.MinSpacing, YardTool.MaxSpacing);
        }

        foreach (var (group, name, columns) in StampGroups)
        {
            var stamps = _place.Catalogue.Where(s => s.Group == group).ToList();
            if (!ToolPanel.Section($"stamps{group}", name, stamps.Count.ToString(CultureInfo.InvariantCulture), group == StampGroup.Switches))
                continue;
            ImGui.Dummy(new float2(1f, S(2f)));
            var column = 0;
            var width = (Kit.Width - 6f * (columns - 1)) / columns;
            foreach (var stamp in stamps)
            {
                if (column % columns != 0)
                    ImGui.SameLine(0f, 6f);
                column++;
                if (StampTile(stamp, width))
                    (_place.Mode, _place.Stamp, _place.Selected, _switchRefused) = (PlaceMode.Track, stamp, null, null);
            }
        }

        Kit.EndCard();
    }

    private void LinesidePalette(TerrainSource source)
    {
        Kit.BeginCard("Place");
        foreach (var (menu, icon, kinds) in PoleMenus)
        {
            if (!ToolPanel.Section($"poles{menu}", menu, source.Objects.Count(o => kinds.Contains(o.Kind)).ToString(CultureInfo.InvariantCulture), true))
                continue;
            foreach (var kind in kinds)
                if (PaletteRow(icon, kind.Name(), _place.Mode == PlaceMode.Pole && _place.PoleKind == kind, source.Objects.Count(o => o.Kind == kind)))
                {
                    (_place.Mode, _place.PoleKind, _place.RunFrom, _place.RunFirst, _switchRefused) = (PlaceMode.Pole, kind, null, null, null);
                    // Ties are for laying along something, as a rule, not one by one.
                    if (kind.IsTie() && _place.Drawn == PoleDraw.One)
                        _place.Drawn = PoleDraw.Pen;
                }
        }

        Kit.EndCard();
    }

    private void StructuresPalette()
    {
        Kit.BeginCard("Bridges");
        if (PaletteRow(ToolIcon.Bridge, "Edit bridge", _place.Mode == PlaceMode.EditBridge))
            (_place.Mode, _bridgeFrom, _switchRefused) = (PlaceMode.EditBridge, null, null);
        if (PaletteRow(ToolIcon.Bridge, "Place bridge", _place.Mode == PlaceMode.Bridge))
            (_place.Mode, _bridgeFrom, _switchRefused) = (PlaceMode.Bridge, null, null);
        if (PaletteRow(ToolIcon.RemoveBridge, "Remove bridge", _place.Mode == PlaceMode.RemoveBridge))
            (_place.Mode, _bridgeFrom, _switchRefused) = (PlaceMode.RemoveBridge, null, null);
        Kit.EndCard();
        if (_place.Mode == PlaceMode.EditBridge)
            BridgePanel();
    }

    /// How the pole in hand goes down: what it snaps to, how far off that, and whether it's a run and at what gap.
    private void PolePanel()
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var factor = imperial ? FeetPerMeter : 1f;
        Kit.BeginCard(_place.PoleKind.Name(), null, Accent);

        Kit.Caption("Laid");
        var ways = new[] { PoleDraw.One, PoleDraw.Fence, PoleDraw.Pen };
        var drawn = Array.IndexOf(ways, _place.Drawn);
        var pickDrawn = ToolPanel.Mode("poledraw", ["One", "Fence", "Pen"], drawn, 3);
        if (pickDrawn != drawn && pickDrawn >= 0)
            (_place.Drawn, _place.RunFrom, _place.RunFirst) = (ways[pickDrawn], null, null);

        // (A pen's path goes where it's drawn.)
        if (_place.Drawn != PoleDraw.Pen)
        {
            Kit.Caption("Keeps to");
            var snaps = new[] { PoleSnap.Anywhere, PoleSnap.Road, PoleSnap.Track };
            var snap = Array.IndexOf(snaps, _place.Snap);
            var pickSnap = ToolPanel.Mode("polesnap", ["Anywhere", "Road", "Track"], snap, 3);
            if (pickSnap != snap && pickSnap >= 0)
                (_place.Snap, _place.RunFrom, _place.RunFirst) = (snaps[pickSnap], null, null);
            if (_place.Snap != PoleSnap.Anywhere)
            {
                var offset = _place.PoleOffset * factor;
                var label = _place.Snap == PoleSnap.Road ? (imperial ? "Off road edge (ft)" : "Off road edge (m)") : (imperial ? "Off track (ft)" : "Off track (m)");
                if (ToolPanel.Value("poleoffset", label, ref offset, 0f, PoleRuns.MaxOffset * factor, "0.0"))
                {
                    var metres = Math.Clamp(offset / factor, 0f, PoleRuns.MaxOffset);
                    if (_place.Snap == PoleSnap.Road)
                        _place.RoadOffset = metres;
                    else
                        _place.TrackOffset = metres;
                }
            }
        }

        if (_place.Drawn == PoleDraw.One)
        {
            Kit.EndCard();
            return;
        }

        var gap = _place.Gap * factor;
        if (ToolPanel.Value("polegap", imperial ? "Gap (ft)" : "Gap (m)", ref gap, PoleRuns.MinGap(_place.PoleKind) * factor, PoleRuns.MaxGap * factor, "0.00", log: true))
            _place.Gap = Math.Clamp(gap / factor, PoleRuns.MinGap(_place.PoleKind), PoleRuns.MaxGap);
        Kit.EndCard();
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
        // As Kit's cells: square, framed, lit with the accent when it's the one in hand.
        draw.AddRectFilled(at, at + size, on ? Accent.WithAlpha(0.85f) : hovered ? Rgba.Lerp(Surface, Accent.WithAlpha(0.15f), 0.6f) : Surface);
        draw.AddRect(at, at + size, on ? Accent : hovered ? Rgba.Mix(StrokeStrong, Accent, 1f) : StrokeStrong, 0f, ImDrawFlags.None, 1f);

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

    /// A row of a palette, as every list in the panels: its icon and name (and how many are placed), lit when it's in hand.
    private static bool PaletteRow(ToolIcon icon, string name, bool on, int? placed = null) =>
        ToolPanel.Item($"palette.{name}", icon, name, placed is { } n ? n.ToString(CultureInfo.InvariantCulture) : null, on);

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
