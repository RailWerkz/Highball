using System.Globalization;
using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Objects tool's palettes in the side panel, each belonging to one of its strip buttons.
internal sealed partial class ObjectsTool
{
    public static PlaceGroup GroupOf(PlaceMode mode) => mode switch
    {
        PlaceMode.Track or PlaceMode.Diamond or PlaceMode.GradeCrossing => PlaceGroup.Assemblies,
        PlaceMode.Bridge or PlaceMode.RemoveBridge or PlaceMode.EditBridge => PlaceGroup.Structures,
        PlaceMode.Signal or PlaceMode.HandThrow => PlaceGroup.Signals,
        _ => PlaceGroup.Lineside,
    };

    /// The Place tool, on one of its buttons: what's in hand is let go if it belongs to another.
    public void Open(PlaceGroup group)
    {
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

    /// The Objects palette's pole menus.
    private static readonly (string Menu, ToolIcon Icon, PlacedKind[] Kinds)[] PoleMenus =
    [
        ("Utility Poles", ToolIcon.UtilityPole, [PlacedKind.PoleTelegraph, PlacedKind.PoleDistribution, PlacedKind.PoleTelecom]),
        ("Light Poles", ToolIcon.LightPole, [PlacedKind.LightCobra, PlacedKind.LightYardMast, PlacedKind.LightPlatform]),
        ("Ties", ToolIcon.Ties, [PlacedKind.TieSingle, PlacedKind.TieSection, PlacedKind.TieSectionBallast]),
    ];

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
            ImGui.SetTooltip(stamp.Problem ?? $"{stamp.Name}, {YardLaying.Length(stamp.Length)} of track");
        return clicked;
    }

    /// A row of a palette, as every list in the panels: its icon and name (and how many are placed), lit when it's in hand.
    private static bool PaletteRow(ToolIcon icon, string name, bool on, int? placed = null) =>
        ToolPanel.Item($"palette.{name}", icon, name, placed is { } n ? n.ToString(CultureInfo.InvariantCulture) : null, on);
}
