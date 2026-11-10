using System.Globalization;
using Brutal.ImGuiApi;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The one way every tool's side panel is laid out, in Kit's look: a header (the tool and its key), the tool's mode as one
/// segmented row, then cards (Kit.BeginCard) of captioned controls, a card for what's picked when something is, and plain
/// hints and problems in their own colours. Every control a panel needs is here or in Kit, so no panel reaches for ImGui's
/// own radio buttons, combos or sliders.
public static class ToolPanel
{
    private static ImDrawListPtr Draw => ImGui.GetWindowDrawList();

    /// Which collapsible sections are open, by id.
    private static readonly Dictionary<string, bool> Open = [];

    /// The tool's name in spaced capitals with a lamp before it, its key on the right, what it's doing under it, and a rule.
    public static void Header(string title, string? key, string? subtitle = null)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Kit.Width;
        Canvas.Lamp(draw, at + new float2(S(4f), S(9f)), S(4f), Accent);
        Canvas.Spaced(draw, Bold, S(14f), at + new float2(S(18f), S(1f)), Text, title.ToUpperInvariant(), 0.24f);
        if (key is { Length: > 0 })
        {
            var keyWidth = Canvas.TextSize(Mono, S(13f), key).X + S(12f);
            var min = new float2(at.X + width - keyWidth, at.Y - S(1f));
            draw.AddRect(min, min + new float2(keyWidth, S(20f)), StrokeStrong, 0f, ImDrawFlags.None, 1f);
            Canvas.Text(draw, Mono, S(13f), min + new float2(keyWidth * 0.5f, S(2.5f)), TextDim, key, Align.Center);
        }

        var height = S(27f);
        if (subtitle is { Length: > 0 })
        {
            Canvas.Text(draw, Regular, S(14.5f), at + new float2(0f, S(25f)), TextDim, Kit.Ellipsize(subtitle, Regular, S(14.5f), width));
            height += S(20f);
        }

        draw.AddLine(new float2(at.X, at.Y + height), new float2(at.X + width, at.Y + height), StrokeStrong, 1f);
        ImGui.Dummy(new float2(width, height + S(8f)));
    }

    /// One of several, as a segmented row (more than `columns` wrap onto further rows). Returns the one picked; current may be
    /// -1 for none lit.
    public static int Mode(string id, ReadOnlySpan<string> labels, int current, int columns = 4, bool enabled = true)
    {
        var picked = current;
        // Wrapped rows keep one cell width: a short last row doesn't stretch.
        var gap = S(3f);
        var cell = (Kit.Width - gap * (Math.Min(columns, labels.Length) - 1)) / Math.Min(columns, labels.Length);
        for (var start = 0; start < labels.Length; start += columns)
        {
            var count = Math.Min(columns, labels.Length - start);
            var row = Kit.Segmented($"{id}.{start}", labels.Slice(start, count), current - start is var local && local >= 0 && local < count ? local : -1, enabled, cell * count + gap * (count - 1));
            if (row >= 0 && row != current - start)
                picked = start + row;
        }

        return picked;
    }

    /// A caption with the value read out on its right, over a slider from min to max (log: evenly in ratio, for sizes).
    /// Returns true while it's changed; released is true on the frame it's let go, for an edit that's made once.
    public static bool Value(string id, string caption, ref float value, float min, float max, string format, out bool released, bool log = false, bool enabled = true)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Kit.Width;
        Canvas.Spaced(draw, Medium, S(12f), at, Rgba.Mix(TextFaint, TextDim, 0.7f), caption.ToUpperInvariant(), 0.18f);
        Canvas.Text(draw, Mono, S(14f), new float2(at.X + width, at.Y - S(1f)), enabled ? Text : TextFaint, value.ToString(format, CultureInfo.InvariantCulture), Align.Right);
        ImGui.Dummy(new float2(width, S(16f)));

        var t = log ? MathF.Log(MathF.Max(value, min) / min) / MathF.Log(max / min) : (value - min) / MathF.Max(max - min, 1e-6f);
        var changed = Kit.Slider(id, ref t, out released, enabled);
        if (changed)
            value = Math.Clamp(log ? min * MathF.Pow(max / min, t) : min + (max - min) * t, min, max);
        return changed;
    }

    public static bool Value(string id, string caption, ref float value, float min, float max, string format, bool log = false, bool enabled = true) =>
        Value(id, caption, ref value, min, max, format, out _, log, enabled);

    /// A whole number between min and max: a caption, then a minus, the number and a plus. Returns true when it changed.
    public static bool Count(string id, string caption, ref int value, int min, int max)
    {
        Kit.Caption(caption);
        var width = Kit.Width;
        var button = S(36f);
        var at = ImGui.GetCursorScreenPos();
        var was = value;
        if (Kit.Button($"{id}.less", "-", button, ButtonKind.Secondary, value > min))
            value--;
        ImGui.SetCursorScreenPos(at + new float2(width - button, 0f));
        if (Kit.Button($"{id}.more", "+", button, ButtonKind.Secondary, value < max))
            value++;
        Canvas.Text(Draw, Mono, S(16f), at + new float2(width * 0.5f, S(6.5f)), Text, value.ToString(CultureInfo.InvariantCulture), Align.Center);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(width, S(31f)));
        value = Math.Clamp(value, min, max);
        return value != was;
    }

    /// A row of a list or palette: an icon or a colour swatch (if either), its name, a word on the right, the accent bar when
    /// it's the one in hand.
    public static bool Item(string id, ToolIcon? icon, string name, string? right, bool on, bool enabled = true, Rgba? swatch = null)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Width, S(31f));
        var clicked = ImGui.InvisibleButton(id, size) && enabled;
        var hover = ImGui.IsItemHovered() && enabled;
        var lit = Tween.Float(id, "lit", on ? 1f : hover ? 0.5f : 0f, 0.15f);
        if (lit > 0.01f)
            draw.AddRectFilled(at, at + size, Accent.WithAlpha(0.14f * lit));
        if (on)
            draw.AddRectFilled(at, new float2(at.X + S(3f), at.Y + size.Y), Accent);
        var ink = !enabled ? TextFaint : on ? Text : hover ? Text : TextDim;
        var x = S(12f);
        if (icon is { } shape)
        {
            ToolIcons.Draw(draw, shape, at + new float2(S(20f), size.Y * 0.5f), S(8.5f), ink, 1.4f);
            x = S(36f);
        }
        else if (swatch is { } fill)
        {
            var c = at + new float2(S(20f), size.Y * 0.5f);
            draw.AddRectFilled(c - new float2(S(6f), S(6f)), c + new float2(S(6f), S(6f)), fill);
            x = S(36f);
        }

        var tagWidth = right is null ? 0f : Canvas.SpacedWidth(Medium, S(11.5f), right, 0.16f) + S(14f);
        Canvas.Text(draw, on ? Bold : Medium, S(15.5f), at + new float2(x, S(6f)), ink, Kit.Ellipsize(name, Medium, S(15.5f), size.X - x - tagWidth - S(6f)));
        if (right is not null)
            Canvas.Spaced(draw, Medium, S(11.5f), new float2(at.X + size.X - tagWidth + S(4f), at.Y + S(9.5f)), TextFaint, right, 0.16f);
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A section that folds: a chevron and its title in spaced capitals, a count on the right. Returns true while it's open.
    public static bool Section(string id, string title, string? right = null, bool openAtFirst = false)
    {
        if (!Open.TryGetValue(id, out var open))
            Open[id] = open = openAtFirst;
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Kit.Width, S(27f));
        if (ImGui.InvisibleButton(id, size))
            Open[id] = open = !open;
        var hover = ImGui.IsItemHovered();
        var ink = hover ? Text : TextDim;
        var c = at + new float2(S(6f), size.Y * 0.5f);
        var arm = S(3.5f);
        if (open)
        {
            draw.AddLine(c + new float2(-arm, -arm * 0.5f), c + new float2(0f, arm * 0.5f), ink, S(1.5f));
            draw.AddLine(c + new float2(0f, arm * 0.5f), c + new float2(arm, -arm * 0.5f), ink, S(1.5f));
        }
        else
        {
            draw.AddLine(c + new float2(-arm * 0.5f, -arm), c + new float2(arm * 0.5f, 0f), ink, S(1.5f));
            draw.AddLine(c + new float2(arm * 0.5f, 0f), c + new float2(-arm * 0.5f, arm), ink, S(1.5f));
        }

        Canvas.Spaced(draw, Medium, S(12f), at + new float2(S(18f), S(7f)), ink, title.ToUpperInvariant(), 0.18f);
        if (right is not null)
            Canvas.Text(draw, Mono, S(13.5f), new float2(at.X + size.X, at.Y + S(5.5f)), TextFaint, right, Align.Right);
        draw.AddLine(new float2(at.X, at.Y + size.Y), new float2(at.X + size.X, at.Y + size.Y), Stroke, 1f);
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return open;
    }

    /// Plain guidance: what a click does, what's wanted next.
    public static void Hint(string text) => Kit.Note(text);

    /// A reading in plain words, brighter than a hint.
    public static void Line(string text) => Kit.Note(text, Text);

    /// What's wrong, or why something was refused.
    public static void Problem(string text) => Kit.Status(Stop, text);
}
