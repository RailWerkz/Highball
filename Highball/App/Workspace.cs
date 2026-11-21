using Brutal.ImGuiApi;
using Highball.Builder;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.App;

/// The layout every working screen (Route Builder, Consist Editor, Drive) shares, in the editor's look:
///
///   top bar      the menus, the tool's modes, and the view switch at the right
///   rail         tool icons down the left edge
///   properties   the tool's settings docked at the right, only while it has any
///   dock         an optional strip along the bottom (the train's controls)
///   status bar   a reading, the keys for what's in hand, a notice
///
/// A screen opens the top bar (BeginBar), sets the frame's edges (Begin), then draws the parts it has.
public static class Workspace
{
    public static float RailWidth => S(42f);
    public static float PropertiesWidth => S(300f);
    public static float StatusHeight => S(24f);
    public static float DockHeight => S(52f);

    /// Under the top bar.
    public static float Top { get; private set; }

    /// Over the status bar.
    public static float StatusTop { get; private set; }

    /// Over the dock, or the status bar when there's no dock: the bottom of the rail, properties and viewport.
    public static float Bottom { get; private set; }

    public static float2 Display => ImGui.GetIO().DisplaySize;

    private const ImGuiWindowFlags Fixed = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;

    /// The frame's edges: top is under the top bar; dock is whether the bottom strip is shown.
    public static void Begin(float top, bool dock)
    {
        Top = top;
        StatusTop = Display.Y - StatusHeight;
        Bottom = dock ? StatusTop - DockHeight : StatusTop;
        WindowBounds.Top = Top;
        WindowBounds.Bottom = Bottom;
    }

    public static void End() => WindowBounds.Reset();

    // --- Top bar. ---

    /// Opens the top bar (ImGui's main menu bar, a little taller). Returns its height, or 0 if it isn't shown.
    public static bool BeginBar(out float height)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new float2(S(10f), S(8f)));
        var open = ImGui.BeginMainMenuBar();
        ImGui.PopStyleVar();
        height = open ? ImGui.GetWindowSize().Y : 0f;
        if (open)
        {
            var draw = ImGui.GetWindowDrawList();
            draw.AddLine(new float2(0f, height - 1f), new float2(Display.X, height - 1f), Stroke, 1f);
        }

        return open;
    }

    public static void EndBar() => ImGui.EndMainMenuBar();

    /// The tool's modes in the top bar after the menus: a joined strip of words. Returns the one picked.
    public static int Modes(string id, ReadOnlySpan<string> labels, int current)
    {
        if (labels.Length < 2)
            return current;
        var height = ImGui.GetWindowSize().Y;
        var draw = ImGui.GetWindowDrawList();
        ImGui.SameLine(0f, S(24f));
        var start = ImGui.GetCursorScreenPos().X;
        draw.AddLine(new float2(start - S(12f), height * 0.25f), new float2(start - S(12f), height * 0.75f), StrokeStrong, 1f);
        var picked = current;
        var x = start;
        var cellHeight = height - S(12f);
        for (var i = 0; i < labels.Length; i++)
        {
            var width = Canvas.TextSize(Medium, S(13.5f), labels[i]).X + S(20f);
            var min = new float2(x, S(6f));
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"{id}.{i}", new float2(width, cellHeight)) && i != current)
            {
                picked = i;
                Sfx.Tick();
            }

            var hover = ImGui.IsItemHovered();
            var on = i == current;
            if (on)
                draw.AddRectFilled(min, min + new float2(width, cellHeight), Raised, S(3f));
            else if (hover)
                draw.AddRectFilled(min, min + new float2(width, cellHeight), Raised.WithAlpha(0.5f), S(3f));
            Canvas.Text(draw, Medium, S(13.5f), min + new float2(width * 0.5f, (cellHeight - S(13.5f)) * 0.5f - S(1f)), on ? Style.Text : hover ? Style.Text : TextDim, labels[i], Align.Center);
            if (on)
                draw.AddRectFilled(new float2(min.X + S(6f), min.Y + cellHeight - S(2f)), new float2(min.X + width - S(6f), min.Y + cellHeight), Accent);
            x += width + S(2f);
        }

        ImGui.SetCursorScreenPos(new float2(x, 0f));
        ImGui.Dummy(new float2(1f, 1f));
        return picked;
    }

    /// The view switch at the right end of the top bar, a dot before it while there's something unsaved. Returns the
    /// view picked.
    public static int Views(string id, ReadOnlySpan<string> labels, int current, ReadOnlySpan<bool> enabled = default, bool unsaved = false, string? unsavedTip = null)
    {
        var draw = ImGui.GetWindowDrawList();
        var height = ImGui.GetWindowSize().Y;
        var cell = S(40f);
        var cellHeight = height - S(14f);
        var right = Display.X - S(10f);
        var at = new float2(right - cell * labels.Length, S(7f));
        draw.AddRectFilled(at, at + new float2(cell * labels.Length, cellHeight), Raised, S(3f));
        var picked = current;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = at + new float2(i * cell, 0f);
            var open = enabled.IsEmpty || enabled[i];
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"{id}.{i}", new float2(cell, cellHeight)) && open && i != current)
            {
                picked = i;
                Sfx.Tick();
            }

            var hover = ImGui.IsItemHovered() && open;
            if (i == current)
                draw.AddRectFilled(min + new float2(S(2f), S(2f)), min + new float2(cell - S(2f), cellHeight - S(2f)), Accent, S(3f));
            else if (hover)
                draw.AddRectFilled(min + new float2(S(2f), S(2f)), min + new float2(cell - S(2f), cellHeight - S(2f)), RaisedHover, S(3f));
            var ink = i == current ? OnAccent : !open ? TextFaint : hover ? Style.Text : TextDim;
            Canvas.Text(draw, Bold, S(12.5f), min + new float2(cell * 0.5f, (cellHeight - S(12.5f)) * 0.5f - S(1f)), ink, labels[i], Align.Center);
        }

        if (unsaved)
        {
            var c = new float2(at.X - S(16f), height * 0.5f);
            draw.AddCircleFilled(c, S(4f), Caution);
            ImGui.SetCursorScreenPos(c - new float2(S(8f), S(8f)));
            ImGui.InvisibleButton($"{id}.unsaved", new float2(S(16f), S(16f)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(unsavedTip ?? "Unsaved changes (Ctrl+S)");
        }

        return picked;
    }

    // --- Rail. ---

    private static float RailButton => S(34f);

    public static bool BeginRail()
    {
        ImGui.SetNextWindowPos(new float2(0f, Top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(RailWidth, MathF.Max(S(100f), Bottom - Top)), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, S(6f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0f, S(2f)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Style.Background.Float4);
        var shown = ImGui.Begin("##rail"u8, Fixed | ImGuiWindowFlags.NoScrollbar);
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(new float2(RailWidth - 1f, Top), new float2(RailWidth - 1f, Bottom), Stroke, 1f);
        return shown;
    }

    public static void EndRail()
    {
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    /// One tool: its icon, on a lit square while it's in hand; its name and key as a tooltip.
    public static bool RailItem(string id, ToolIcon icon, string tip, bool on, bool enabled = true)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = new float2((RailWidth - RailButton) * 0.5f, ImGui.GetCursorScreenPos().Y);
        var size = new float2(RailButton, RailButton);
        ImGui.SetCursorScreenPos(at);
        var clicked = ImGui.InvisibleButton(id, size) && enabled;
        var hover = ImGui.IsItemHovered();
        if (on)
            draw.AddRectFilled(at, at + size, Accent.WithAlpha(0.22f), S(4f));
        else if (hover && enabled)
            draw.AddRectFilled(at, at + size, Raised, S(4f));
        var ink = !enabled ? TextFaint : on ? Rgba.Mix(Accent, Rgba.White, 0.35f) : hover ? Style.Text : TextDim;
        ToolIcons.Draw(draw, icon, at + size * 0.5f, S(8.5f), ink, S(1.4f));
        if (hover)
            ImGui.SetTooltip(enabled ? tip : $"{tip}: build tiles first");
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A little space between groups of tools.
    public static void RailGap()
    {
        var draw = ImGui.GetWindowDrawList();
        var y = ImGui.GetCursorScreenPos().Y + S(4f);
        draw.AddLine(new float2(S(12f), y), new float2(RailWidth - S(12f), y), Stroke, 1f);
        ImGui.Dummy(new float2(1f, S(9f)));
    }

    /// Undo and redo at the foot of the rail.
    public static (bool Undo, bool Redo) RailHistory(bool canUndo, bool canRedo)
    {
        var size = RailButton;
        var y = MathF.Max(ImGui.GetCursorScreenPos().Y + S(8f), Bottom - size * 2f - S(8f));
        ImGui.SetCursorScreenPos(new float2(0f, y));
        var undo = RailItem("##undo", ToolIcon.Undo, "Undo (Ctrl+Z)", false, canUndo);
        var redo = RailItem("##redo", ToolIcon.Redo, "Redo (Ctrl+Y)", false, canRedo);
        return (undo, redo);
    }

    // --- Properties. ---

    /// The properties panel, docked at the right edge (or at left, on a screen that wants it there).
    public static bool BeginProperties(string id, float? left = null)
    {
        var x = left ?? Display.X - PropertiesWidth;
        ImGui.SetNextWindowPos(new float2(x, Top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(PropertiesWidth, MathF.Max(S(100f), Bottom - Top)), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(S(10f), S(10f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, S(6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var shown = ImGui.Begin(id, Fixed);
        var draw = ImGui.GetWindowDrawList();
        var edge = left is null ? x : x + PropertiesWidth - 1f;
        draw.PushClipRect(new float2(x - 1f, Top), new float2(x + PropertiesWidth + 1f, Bottom), false);
        draw.AddLine(new float2(edge, Top), new float2(edge, Bottom), Stroke, 1f);
        draw.PopClipRect();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(S(6f), S(4f)));
        return shown;
    }

    public static void EndProperties()
    {
        ImGui.PopStyleVar();
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    /// A panel drawn on the draw list rather than as a window (the consist editor's), flush to an edge: a flat fill, a
    /// hairline on its inner side, and a small heading. Returns where the content starts.
    public static float2 Panel(ImDrawListPtr draw, float2 min, float2 max, string title)
    {
        draw.AddRectFilled(min, max, Surface);
        if (min.X > 0f)
            draw.AddLine(new float2(min.X, min.Y), new float2(min.X, max.Y), Stroke, 1f);
        if (max.X < Display.X)
            draw.AddLine(new float2(max.X, min.Y), new float2(max.X, max.Y), Stroke, 1f);
        if (title.Length == 0)
            return new float2(min.X + S(24f), min.Y + S(26f));
        Canvas.Text(draw, Bold, S(13.5f), new float2(min.X + S(14f), min.Y + S(10f)), TextDim, title);
        return new float2(min.X + S(24f), min.Y + S(48f));
    }

    // --- Dock. ---

    /// The bottom strip over the status bar, from left to the window's right edge.
    public static bool BeginDock(string id, float left)
    {
        ImGui.SetNextWindowPos(new float2(left, Bottom), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(Display.X - left, DockHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(S(12f), S(6f)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var shown = ImGui.Begin(id, Fixed | ImGuiWindowFlags.NoScrollbar);
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(new float2(left, Bottom), new float2(Display.X, Bottom), Stroke, 1f);
        return shown;
    }

    public static void EndDock()
    {
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    // --- Status bar. ---

    /// The status bar: a reading on the left, the keys for what's in hand, and a notice (or credit) on the right.
    public static void Status(ImDrawListPtr draw, string? status, (string Key, string Action)[] hints, string? right = null, bool rightBright = false)
    {
        var size = Display;
        var top = StatusTop;
        var height = StatusHeight;
        draw.AddRectFilled(new float2(0f, top), size, Style.Background);
        draw.AddLine(new float2(0f, top), new float2(size.X, top), Stroke, 1f);
        var y = top + (height - S(12.5f)) * 0.5f - S(1f);
        var x = S(10f);
        if (status is { Length: > 0 })
        {
            Canvas.Text(draw, Mono, S(12.5f), new float2(x, y + S(0.5f)), TextDim, status);
            x += Canvas.TextSize(Mono, S(12.5f), status).X + S(24f);
        }

        var limit = size.X - S(16f) - (right is { Length: > 0 } ? Canvas.TextSize(Regular, S(12.5f), right).X + S(24f) : 0f);
        foreach (var (key, action) in hints)
        {
            var name = Kit.Sentence(action);
            var keyWidth = Canvas.TextSize(Bold, S(12.5f), key).X;
            var width = keyWidth + S(6f) + Canvas.TextSize(Regular, S(12.5f), name).X;
            if (x + width > limit)
                break;
            Canvas.Text(draw, Bold, S(12.5f), new float2(x, y), Style.Text, key);
            Canvas.Text(draw, Regular, S(12.5f), new float2(x + keyWidth + S(6f), y), TextDim, name);
            x += width + S(18f);
        }

        if (right is { Length: > 0 })
            Canvas.Text(draw, Regular, S(12.5f), new float2(size.X - S(10f), y), rightBright ? Style.Text : TextFaint, right, Align.Right);
    }
}
