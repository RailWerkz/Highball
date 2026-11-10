using Brutal.ImGuiApi;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// One reading in a Kit.Stats grid: a small spaced caption over its value.
public readonly record struct Stat(string Label, string Value, Rgba? Color = null);

/// The deck's look (the consist editor's: enamel plates with cut corners, spaced capitals, square lamps, hairline frames,
/// Mono readings) for a floating ImGui window, so it can still be dragged, scrolled and typed into. Everything's laid out
/// in ImGui's flow: each piece reserves its space and draws itself there, its clicks through an invisible button.
///
/// Text is drawn on the draw list, never through ImGui.Text (which takes its string as a format).
public static class Kit
{
    private static ImDrawListPtr Draw => ImGui.GetWindowDrawList();

    private static readonly Stack<(float2 Start, float Width)> Cards = new();

    private static float CardPad => S(12f);

    /// Captions over controls and readings: dimmer than what they label, but readable.
    private static Rgba Label => Rgba.Mix(TextFaint, TextDim, 0.7f);

    /// The width there is to lay out in: a card's inside, or the window's.
    public static float Width => Cards.Count > 0 ? Cards.Peek().Width - CardPad * 2f : ImGui.GetContentRegionAvail().X;

    // --- The window. ---

    /// A plate of a fixed width, its height following what's in it (scrolling past the room there is), kept on the
    /// screen. Its header: a lamp and the title, a status on the right, the subtitle under, and a close box.
    public static bool Begin(string name, ref bool open, float width)
    {
        var (min, max) = WindowBounds.Area;
        ImGui.SetNextWindowSizeConstraints(new float2(width, S(80f)), new float2(width, MathF.Max(S(80f), max.Y - min.Y)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new float4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new float4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(S(16f), S(14f)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, S(6f));
        var shown = ImGui.Begin(name, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse);
        WindowBounds.Keep();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(S(8f), S(6f)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 0f);

        // The plate, behind everything, over the whole window whatever it's scrolled to.
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var draw = Draw;
        draw.PushClipRect(pos, pos + size, false);
        Canvas.Plate(draw, pos, pos + size);
        draw.PopClipRect();
        return shown;
    }

    public static void End()
    {
        ImGui.PopStyleVar(3);
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    /// The title band; returns true when its close box is clicked.
    public static bool Header(string title, string subtitle, string status, Rgba statusColor)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Width;
        Canvas.Lamp(draw, at + new float2(S(4f), S(8f)), S(4f), Accent);
        Canvas.Spaced(draw, Bold, S(12f), at + new float2(S(18f), S(1f)), Text, title, 0.24f);
        Canvas.Text(draw, Bold, S(17f), at + new float2(0f, S(22f)), Text, Ellipsize(subtitle, Bold, S(17f), width - S(40f)));

        // The close box, top right; the status to its left.
        var close = new float2(at.X + width - S(18f), at.Y - S(2f));
        ImGui.SetCursorScreenPos(close);
        var clicked = ImGui.InvisibleButton("##close", new float2(S(18f), S(18f)));
        var hover = ImGui.IsItemHovered();
        var c = close + new float2(S(9f), S(9f));
        var arm = S(5f);
        var ink = hover ? Text : TextDim;
        draw.AddLine(c - new float2(arm, arm), c + new float2(arm, arm), ink, S(1.5f));
        draw.AddLine(c + new float2(-arm, arm), c + new float2(arm, -arm), ink, S(1.5f));
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var statusWidth = Canvas.SpacedWidth(Bold, S(10f), status, 0.16f);
        var statusX = close.X - S(12f) - statusWidth;
        Canvas.Lamp(draw, new float2(statusX - S(10f), at.Y + S(8f)), S(3f), statusColor);
        Canvas.Spaced(draw, Bold, S(10f), new float2(statusX, at.Y + S(2f)), Rgba.Mix(Text, statusColor, 0.5f), status, 0.16f);

        // The double rule under it.
        var rule = at.Y + S(50f);
        var pos = ImGui.GetWindowPos();
        var right = pos.X + ImGui.GetWindowSize().X;
        draw.AddLine(new float2(pos.X, rule), new float2(right, rule), StrokeStrong, 1f);
        draw.AddLine(new float2(pos.X, rule + S(3f)), new float2(right, rule + S(3f)), Stroke, 1f);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(width, S(62f)));
        return clicked;
    }

    // --- Tabs and cards. ---

    /// A bar of tabs, the picked one filled with the accent. Returns the one picked (clicked, or as it was).
    public static int Tabs(string id, ReadOnlySpan<string> labels, int current)
    {
        var picked = current;
        var gap = S(4f);
        var width = (Width - gap * (labels.Length - 1)) / labels.Length;
        var height = S(30f);
        var at = ImGui.GetCursorScreenPos();
        for (var i = 0; i < labels.Length; i++)
        {
            var min = at + new float2(i * (width + gap), 0f);
            if (Cell($"{id}.{i}", labels[i], min, new float2(width, height), i == current, true, null, out _))
                picked = i;
        }

        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, height));
        ImGui.Dummy(new float2(1f, S(2f)));
        return picked;
    }

    /// Starts a card: a framed panel with a spaced title (a lamp before it if given, a note on the right). Cards don't nest.
    public static void BeginCard(string title, string? note = null, Rgba? lamp = null)
    {
        var draw = Draw;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        Cards.Push((start, width));
        ImGui.SetCursorScreenPos(start + new float2(CardPad, CardPad));
        ImGui.BeginGroup();
        ImGui.PushItemWidth(Width);

        var at = ImGui.GetCursorScreenPos();
        var x = at.X;
        if (lamp is { } color)
        {
            Canvas.Lamp(draw, new float2(x + S(3f), at.Y + S(7f)), S(3f), color);
            x += S(14f);
        }

        Canvas.Spaced(draw, Bold, S(12.5f), new float2(x, at.Y), Text, title.ToUpperInvariant(), 0.2f);
        if (note is not null)
            Canvas.Text(draw, Mono, S(13.5f), new float2(at.X + Width, at.Y - S(1f)), TextDim, note, Align.Right);
        ImGui.Dummy(new float2(Width, S(21f)));
    }

    public static void EndCard()
    {
        ImGui.PopItemWidth();
        ImGui.EndGroup();
        var (start, width) = Cards.Pop();
        var bottom = ImGui.GetItemRectMax().Y + CardPad;
        var draw = Draw;
        draw.ChannelsSetCurrent(0);
        var max = new float2(start.X + width, bottom);
        draw.AddRectFilled(start, max, Raised.WithAlpha(0.42f));
        draw.AddRect(start, max, Stroke, 0f, ImDrawFlags.None, 1f);
        draw.AddRectFilled(start, new float2(start.X + S(2f), start.Y + S(24f)), Accent.WithAlpha(0.8f));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new float2(start.X, bottom));
        ImGui.Dummy(new float2(width, S(8f)));
    }

    // --- Text. ---

    /// A small spaced caption, over a control.
    public static void Caption(string text)
    {
        var at = ImGui.GetCursorScreenPos();
        Canvas.Spaced(Draw, Medium, S(12f), at, Label, text.ToUpperInvariant(), 0.18f);
        ImGui.Dummy(new float2(Width, S(16f)));
    }

    /// A line or more of plain words, wrapped to the width.
    public static void Note(string text, Rgba? color = null)
    {
        var draw = Draw;
        var size = S(14.5f);
        foreach (var line in Canvas.Wrap(text, Regular, size, Width))
        {
            var at = ImGui.GetCursorScreenPos();
            Canvas.Text(draw, Regular, size, at, color ?? TextDim, line);
            ImGui.Dummy(new float2(Width, S(19f)));
        }
    }

    /// A lamp and a line beside it, in its colour.
    public static void Status(Rgba color, string text)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        Canvas.Lamp(draw, at + new float2(S(4f), S(9f)), S(3f), color);
        var size = S(15f);
        var lines = Canvas.Wrap(text, Medium, size, Width - S(16f));
        for (var i = 0; i < lines.Count; i++)
            Canvas.Text(draw, Medium, size, at + new float2(S(16f), i * S(19f)), Rgba.Mix(Text, color, 0.35f), lines[i]);
        ImGui.Dummy(new float2(Width, MathF.Max(1, lines.Count) * S(19f)));
    }

    /// Readings in a grid: captions over Mono values, columns across.
    public static void Stats(ReadOnlySpan<Stat> stats, int columns = 3, bool big = false)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Width / columns;
        var rowHeight = big ? S(44f) : S(37f);
        var rows = (stats.Length + columns - 1) / columns;
        for (var i = 0; i < stats.Length; i++)
        {
            var cell = at + new float2(i % columns * width, i / columns * rowHeight);
            var stat = stats[i];
            var label = stat.Label.ToUpperInvariant();
            while (label.Length > 1 && Canvas.SpacedWidth(Medium, S(11.5f), label, 0.18f) > width - S(8f))
                label = label[..^2] + "…";
            Canvas.Spaced(draw, Medium, S(11.5f), cell, Label, label, 0.18f);
            var font = big ? Bold : Mono;
            var size = big ? S(20f) : S(15f);
            Canvas.Text(draw, font, size, cell + new float2(0f, S(15f)), stat.Color ?? Text, Ellipsize(stat.Value, font, size, width - S(8f)));
        }

        ImGui.Dummy(new float2(Width, rows * rowHeight));
    }

    // --- Controls. ---

    /// A plate button across the width (or as wide as asked). Primary is filled with the accent; a tint fills it with that.
    public static bool Button(string id, string label, float width = 0f, ButtonKind kind = ButtonKind.Secondary, bool enabled = true, Rgba? tint = null)
        => Button(id, label, out _, width, kind, enabled, tint);

    /// The same, saying whether it's held down (for a horn).
    public static bool Button(string id, string label, out bool held, float width = 0f, ButtonKind kind = ButtonKind.Secondary, bool enabled = true, Rgba? tint = null)
    {
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(width > 0f ? width : Width, S(31f));
        var clicked = Cell(id, label, at, size, kind == ButtonKind.Primary, enabled, tint, out held);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(size);
        return clicked;
    }

    /// A row of buttons sharing the width. Returns the one clicked, or -1.
    public static int Buttons(string id, ReadOnlySpan<string> labels, ReadOnlySpan<bool> enabled = default)
    {
        var gap = S(6f);
        var width = (Width - gap * (labels.Length - 1)) / labels.Length;
        var at = ImGui.GetCursorScreenPos();
        var hit = -1;
        for (var i = 0; i < labels.Length; i++)
            if (Cell($"{id}.{i}", labels[i], at + new float2(i * (width + gap), 0f), new float2(width, S(31f)), false, enabled.IsEmpty || enabled[i], null, out _))
                hit = i;
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, S(31f)));
        return hit;
    }

    /// One of several, the picked one lit. Returns the one picked. Across the width, or as wide as asked.
    public static int Segmented(string id, ReadOnlySpan<string> labels, int current, bool enabled = true, float across = 0f)
    {
        var gap = S(3f);
        var width = ((across > 0f ? across : Width) - gap * (labels.Length - 1)) / labels.Length;
        var at = ImGui.GetCursorScreenPos();
        var picked = current;
        for (var i = 0; i < labels.Length; i++)
            if (Cell($"{id}.{i}", labels[i], at + new float2(i * (width + gap), 0f), new float2(width, S(29f)), i == current, enabled, null, out _))
                picked = i;
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, S(29f)));
        return picked;
    }

    /// A switch: a square lamp, lit when it's on, and its name. Returns true when it's flipped. Sits on the current line,
    /// so several can go across with SameLine.
    public static bool Toggle(string id, string label, ref bool on, bool enabled = true)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = S(15f);
        var text = Canvas.TextSize(Medium, size, label);
        var box = S(15f);
        var whole = new float2(box + S(8f) + text.X, MathF.Max(box, text.Y));
        var clicked = ImGui.InvisibleButton(id, whole) && enabled;
        var hover = ImGui.IsItemHovered() && enabled;
        var lit = Tween.Float(id, "on", on ? 1f : 0f, 0.12f);
        var alpha = enabled ? 1f : 0.4f;
        var boxMin = at + new float2(0f, (whole.Y - box) * 0.5f);
        draw.AddRectFilled(boxMin, boxMin + new float2(box, box), Surface.Fade(alpha));
        draw.AddRect(boxMin, boxMin + new float2(box, box), Rgba.Mix(StrokeStrong, Accent, MathF.Max(lit, hover ? 0.5f : 0f)).Fade(alpha), 0f, ImDrawFlags.None, 1f);
        if (lit > 0.01f)
        {
            var inset = S(3f);
            draw.AddRectFilled(boxMin + new float2(inset, inset), boxMin + new float2(box - inset, box - inset), Accent.Fade(lit * alpha));
        }

        Canvas.Text(draw, Medium, size, at + new float2(box + S(8f), (whole.Y - text.Y) * 0.5f), (on ? Text : TextDim).Fade(alpha), label);
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
        {
            on = !on;
            Sfx.Tick();
        }

        return clicked;
    }

    /// A slider over 0 to 1 across the width, dragged with the pointer (a click jumps to it). Returns true when the value
    /// changed; released is true on the frame it's let go, for saving once a drag rather than every frame of it.
    public static bool Slider(string id, ref float value, out bool released, bool enabled = true)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Width, S(22f));
        ImGui.InvisibleButton(id, size);
        var active = ImGui.IsItemActive() && enabled;
        released = ImGui.IsItemDeactivated() && enabled;
        var hover = ImGui.IsItemHovered() && enabled;

        var pad = S(7f);
        var left = at.X + pad;
        var span = MathF.Max(size.X - pad * 2f, 1f);
        var was = value;
        if (active)
            value = Math.Clamp((ImGui.GetIO().MousePos.X - left) / span, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);

        var lit = Tween.Float(id, "lit", active || hover ? 1f : 0f, 0.12f);
        var alpha = enabled ? 1f : 0.4f;
        var midY = at.Y + size.Y * 0.5f;
        var knobX = left + span * value;
        var track = S(2f);
        draw.AddRectFilled(new float2(left, midY - track), new float2(left + span, midY + track), StrokeStrong.Fade(alpha));
        if (enabled)
            draw.AddRectFilled(new float2(left, midY - track), new float2(knobX, midY + track), Accent.WithAlpha(0.85f).Fade(alpha));
        var knob = new float2(S(4.5f) + lit * S(1f), S(8f));
        draw.AddRectFilled(new float2(knobX - knob.X, midY - knob.Y), new float2(knobX + knob.X, midY + knob.Y),
            (enabled ? Rgba.Mix(Text, Accent, 0.35f + 0.65f * lit) : TextFaint).Fade(alpha));

        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (released)
            Sfx.Tick();
        return active && value != was;
    }

    /// A segmented gauge; its readings go beside or below it, never over it.
    public static void Gauge(float value, Rgba color)
    {
        var at = ImGui.GetCursorScreenPos();
        var max = at + new float2(Width, S(18f));
        Canvas.SegmentBar(Draw, at, max, value, color.WithAlpha(0.75f), 30);
        ImGui.Dummy(new float2(Width, S(18f)));
    }

    /// A list row: its number, its name, a word on the right; an accent bar on the picked one. Returns true when clicked.
    public static bool Row(string id, string number, string title, string right, bool picked, float width)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(width, S(31f));
        var clicked = ImGui.InvisibleButton(id, size);
        var hover = ImGui.IsItemHovered();
        var lit = Tween.Float(id, "lit", picked ? 1f : hover ? 0.5f : 0f, 0.15f);
        if (lit > 0.01f)
            draw.AddRectFilled(at, at + size, Accent.WithAlpha(0.14f * lit));
        if (picked)
            draw.AddRectFilled(at, new float2(at.X + S(3f), at.Y + size.Y), Accent);
        Canvas.Text(draw, Mono, S(14f), at + new float2(S(12f), S(7.5f)), TextFaint, number);
        Canvas.Text(draw, picked ? Bold : Medium, S(15.5f), at + new float2(S(44f), S(6f)), picked ? Text : TextDim, Ellipsize(title, Medium, S(15.5f), width - S(130f)));
        var tagWidth = Canvas.SpacedWidth(Medium, S(11.5f), right, 0.16f);
        Canvas.Spaced(draw, Medium, S(11.5f), new float2(at.X + width - S(10f) - tagWidth, at.Y + S(9.5f)), TextFaint, right, 0.16f);
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A button or tab cell: framed, lit when picked, the accent on hover.
    private static bool Cell(string id, string label, float2 min, float2 size, bool lit, bool enabled, Rgba? tint, out bool held)
    {
        var draw = Draw;
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, size) && enabled;
        var hovered = ImGui.IsItemHovered() && enabled;
        held = ImGui.IsItemActive() && enabled;
        var hover = Tween.Float(id, "hover", hovered ? 1f : 0f, 0.12f);
        var max = min + size;
        var alpha = enabled ? 1f : 0.35f;
        Rgba ink;
        if (tint is { } fill)
        {
            draw.AddRectFilled(min, max, Rgba.Mix(fill, Rgba.White, (held ? 0.25f : 0.1f) * hover).Fade(alpha));
            ink = Rgba.White.Fade(alpha);
        }
        else if (lit)
        {
            draw.AddRectFilled(min, max, Rgba.Mix(Accent.WithAlpha(0.85f), Rgba.White, (held ? 0.25f : 0.1f) * hover).Fade(alpha));
            draw.AddRect(min, max, Accent.Fade(alpha), 0f, ImDrawFlags.None, 1f);
            ink = OnAccent.Fade(alpha);
        }
        else
        {
            draw.AddRectFilled(min, max, Rgba.Lerp(Surface, Accent.WithAlpha(0.15f), held ? 1f : hover * 0.6f).Fade(alpha));
            draw.AddRect(min, max, Rgba.Mix(StrokeStrong, Accent, hover).Fade(alpha), 0f, ImDrawFlags.None, 1f);
            ink = Text.Fade(alpha);
        }

        var text = label.ToUpperInvariant();
        // A label too long for its cell keeps some room either side: it's drawn closer set, then a size smaller, and only
        // then cut short.
        var room = size.X - S(16f);
        var (fontSize, tracking) = (S(12.5f), 0.12f);
        var width = Canvas.SpacedWidth(Bold, fontSize, text, tracking);
        if (width > room)
            (tracking, width) = (0.04f, Canvas.SpacedWidth(Bold, fontSize, text, 0.04f));
        if (width > room)
            (fontSize, width) = (S(11f), Canvas.SpacedWidth(Bold, S(11f), text, tracking));
        if (width > room)
            (text, width) = (Ellipsize(text, Bold, fontSize, room), Canvas.SpacedWidth(Bold, fontSize, Ellipsize(text, Bold, fontSize, room), tracking));
        Canvas.Spaced(draw, Bold, fontSize, new float2((min.X + max.X - width) * 0.5f, (min.Y + max.Y) * 0.5f - fontSize * 0.6f), ink, text, tracking);
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
            Sfx.Select();
        return clicked;
    }

    /// Cut short with an ellipsis to fit a width.
    public static string Ellipsize(string text, ImFontPtr font, float size, float width)
    {
        if (Canvas.TextSize(font, size, text).X <= width || text.Length < 2)
            return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var cut = text[..n].TrimEnd() + "…";
            if (Canvas.TextSize(font, size, cut).X <= width)
                return cut;
        }

        return text[..1];
    }
}
