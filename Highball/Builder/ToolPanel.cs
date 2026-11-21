using System.Globalization;
using Brutal.ImGuiApi;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// How every tool's properties are laid out: modes as one joined strip, sections of compact rows, value bars with their
/// label inside, list rows, and plain hints and problems. Nothing names what's already on screen: the rail shows the tool.
public static class ToolPanel
{
    private static ImDrawListPtr Draw => ImGui.GetWindowDrawList();

    /// Which folding sections are open, by id.
    private static readonly Dictionary<string, bool> Open = [];

    /// What the tool's doing, when it says something the screen doesn't already: one dim line. The title and key aren't
    /// drawn (the rail and its tooltip have them).
    public static void Header(string title, string? key, string? subtitle = null)
    {
        if (subtitle is not { Length: > 0 })
            return;
        var at = ImGui.GetCursorScreenPos();
        Canvas.Text(Draw, Regular, S(13f), at, TextDim, Kit.Ellipsize(subtitle, Regular, S(13f), Kit.Width));
        ImGui.Dummy(new float2(Kit.Width, S(20f)));
    }

    /// One of several, as a joined strip (more than `columns` wrap onto further strips). Returns the one picked; current
    /// may be -1 for none lit.
    public static int Mode(string id, ReadOnlySpan<string> labels, int current, int columns = 4, bool enabled = true)
    {
        var picked = current;
        var total = labels.Length;
        var widest = 0f;
        foreach (var label in labels)
            widest = MathF.Max(widest, Canvas.TextSize(Medium, S(13f), label).X);
        // One strip if they all fit in it; else rows of as many as were asked for.
        columns = Kit.Width / total - S(14f) >= widest ? total : Math.Max(columns, 1);
        while (columns > 1 && Kit.Width / Math.Min(columns, total) - S(14f) < widest)
            columns--;
        var cell = Kit.Width / Math.Min(columns, total);
        for (var start = 0; start < total; start += columns)
        {
            var count = Math.Min(columns, total - start);
            var local = current - start;
            var row = Kit.Segmented($"{id}.{start}", labels.Slice(start, count), local >= 0 && local < count ? local : -1, enabled, cell * count);
            if (row >= 0 && row != local)
                picked = start + row;
            if (start + columns < total)
                ImGui.Dummy(new float2(1f, S(1f)));
        }

        return picked;
    }

    /// A value from min to max as a bar with its caption inside at the left and the reading at the right (log: evenly in
    /// ratio, for sizes). Returns true while it's changed; released is true on the frame it's let go.
    public static bool Value(string id, string caption, ref float value, float min, float max, string format, out bool released, bool log = false, bool enabled = true)
    {
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Width, Kit.RowHeight);
        var t = log ? MathF.Log(MathF.Max(value, min) / min) / MathF.Log(max / min) : (value - min) / MathF.Max(max - min, 1e-6f);
        var changed = Kit.Bar(id, ref t, at, size, Kit.Sentence(caption), value.ToString(format, CultureInfo.InvariantCulture), enabled, out released);
        if (changed)
            value = Math.Clamp(log ? min * MathF.Pow(max / min, t) : min + (max - min) * t, min, max);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(size);
        return changed;
    }

    public static bool Value(string id, string caption, ref float value, float min, float max, string format, bool log = false, bool enabled = true) =>
        Value(id, caption, ref value, min, max, format, out _, log, enabled);

    /// A whole number between min and max: its caption at the left, then minus, the number and plus at the right.
    public static bool Count(string id, string caption, ref int value, int min, int max)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Kit.Width;
        var height = Kit.RowHeight;
        Canvas.Text(draw, Regular, S(13f), at + new float2(0f, (height - S(13f)) * 0.5f - S(1f)), TextDim, Kit.Sentence(caption));
        var button = height;
        var field = S(44f);
        var left = at.X + width - button * 2f - field;
        var was = value;
        ImGui.SetCursorScreenPos(new float2(left, at.Y));
        if (Kit.Button($"{id}.less", "-", button, ButtonKind.Secondary, value > min))
            value--;
        ImGui.SetCursorScreenPos(new float2(left + button + field, at.Y));
        if (Kit.Button($"{id}.more", "+", button, ButtonKind.Secondary, value < max))
            value++;
        Canvas.Text(draw, Mono, S(13.5f), new float2(left + button + field * 0.5f, at.Y + (height - S(13.5f)) * 0.5f), Text, value.ToString(CultureInfo.InvariantCulture), Align.Center);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(width, height));
        value = Math.Clamp(value, min, max);
        return value != was;
    }

    /// A list or palette row: an icon or a colour swatch (if either), its name, a word on the right; lit when it's in hand.
    public static bool Item(string id, ToolIcon? icon, string name, string? right, bool on, bool enabled = true, Rgba? swatch = null)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Width, Kit.RowHeight);
        var clicked = ImGui.InvisibleButton(id, size) && enabled;
        var hover = ImGui.IsItemHovered() && enabled;
        if (on)
            draw.AddRectFilled(at, at + size, Accent.WithAlpha(0.3f), S(3f));
        else if (hover)
            draw.AddRectFilled(at, at + size, RaisedHover.WithAlpha(0.6f), S(3f));
        var ink = !enabled ? TextFaint : on || hover ? Text : TextDim;
        var x = S(8f);
        if (icon is { } shape)
        {
            ToolIcons.Draw(draw, shape, at + new float2(S(16f), size.Y * 0.5f), S(7f), ink, S(1.3f));
            x = S(30f);
        }
        else if (swatch is { } fill)
        {
            var c = at + new float2(S(14f), size.Y * 0.5f);
            draw.AddRectFilled(c - new float2(S(5f), S(5f)), c + new float2(S(5f), S(5f)), fill, S(2f));
            x = S(28f);
        }

        var tagWidth = right is null ? 0f : Canvas.TextSize(Regular, S(12.5f), Kit.Sentence(right)).X + S(12f);
        var y = at.Y + (size.Y - S(13.5f)) * 0.5f - S(1f);
        Canvas.Text(draw, Medium, S(13.5f), new float2(at.X + x, y), ink, Kit.Ellipsize(name, Medium, S(13.5f), size.X - x - tagWidth - S(4f)));
        if (right is not null)
            Canvas.Text(draw, Regular, S(12.5f), new float2(at.X + size.X - S(8f), y + S(0.5f)), TextFaint, Kit.Sentence(right), Align.Right);
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A section that folds: a chevron and its title, a count on the right. Returns true while it's open.
    public static bool Section(string id, string title, string? right = null, bool openAtFirst = false)
    {
        if (!Open.TryGetValue(id, out var open))
            Open[id] = open = openAtFirst;
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Width, S(24f));
        if (ImGui.InvisibleButton(id, size))
            Open[id] = open = !open;
        var hover = ImGui.IsItemHovered();
        draw.AddRectFilled(at, at + size, (hover ? RaisedHover : Raised).WithAlpha(0.55f), S(3f));
        var ink = hover || open ? Text : TextDim;
        var c = at + new float2(S(12f), size.Y * 0.5f);
        var arm = S(3f);
        if (open)
        {
            draw.AddLine(c + new float2(-arm, -arm * 0.5f), c + new float2(0f, arm * 0.5f), ink, S(1.4f));
            draw.AddLine(c + new float2(0f, arm * 0.5f), c + new float2(arm, -arm * 0.5f), ink, S(1.4f));
        }
        else
        {
            draw.AddLine(c + new float2(-arm * 0.5f, -arm), c + new float2(arm * 0.5f, 0f), ink, S(1.4f));
            draw.AddLine(c + new float2(arm * 0.5f, 0f), c + new float2(-arm * 0.5f, arm), ink, S(1.4f));
        }

        Canvas.Text(draw, Bold, S(13.5f), at + new float2(S(24f), (size.Y - S(13.5f)) * 0.5f - S(1f)), ink, Kit.Sentence(title));
        if (right is not null)
            Canvas.Text(draw, Regular, S(13f), new float2(at.X + size.X - S(8f), at.Y + (size.Y - S(13f)) * 0.5f - S(1f)), TextFaint, right, Align.Right);
        ImGui.Dummy(new float2(1f, S(2f)));
        return open;
    }

    /// Plain guidance: what a click does, what's wanted next.
    public static void Hint(string text) => Kit.Note(text);

    /// A reading in plain words, brighter than a hint.
    public static void Line(string text) => Kit.Note(text, Text);

    /// What's wrong, or why something was refused.
    public static void Problem(string text) => Kit.Status(Stop, text);
}
