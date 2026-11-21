using Brutal.ImGuiApi;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// One reading in a Kit.Stats grid: a label and its value.
public readonly record struct Stat(string Label, string Value, Rgba? Color = null);

/// The editor's controls, laid out in ImGui's flow inside a panel or window: compact rows in sentence case, flat greys,
/// the accent only on what's on or picked. Each piece reserves its space and draws itself there, its clicks through an
/// invisible button. Text is drawn on the draw list, never through ImGui.Text (which takes its string as a format).
public static class Kit
{
    private static ImDrawListPtr Draw => ImGui.GetWindowDrawList();

    /// Section starts, for the width inside one.
    private static readonly Stack<float> Sections = new();

    public static float RowHeight => S(24f);
    public static float FontSize => S(14f);
    private static float Rounding => S(3f);

    /// The width there is to lay out in.
    public static float Width => ImGui.GetContentRegionAvail().X;

    // --- A floating window. ---

    /// A floating window of a fixed width, its height following what's in it, kept on the screen; flat, with a hairline.
    public static bool Begin(string name, ref bool open, float width)
    {
        var (min, max) = WindowBounds.Area;
        ImGui.SetNextWindowSizeConstraints(new float2(width, S(60f)), new float2(width, MathF.Max(S(60f), max.Y - min.Y)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(S(10f), S(8f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, S(6f));
        var shown = ImGui.Begin(name, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse);
        WindowBounds.Keep();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(S(6f), S(4f)));
        return shown;
    }

    public static void End()
    {
        ImGui.PopStyleVar();
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// A floating window's title row: its title, a status at the right, a close box. Returns true when the box is clicked.
    public static bool Header(string title, string subtitle, string status, Rgba statusColor)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Width;
        var name = subtitle.Length > 0 ? subtitle : Sentence(title);
        Canvas.Text(draw, Bold, FontSize, at + new float2(0f, S(2f)), Text, Ellipsize(name, Bold, FontSize, width - S(140f)));

        var close = new float2(at.X + width - S(18f), at.Y);
        ImGui.SetCursorScreenPos(close);
        var clicked = ImGui.InvisibleButton("##close", new float2(S(18f), S(18f)));
        var hover = ImGui.IsItemHovered();
        var c = close + new float2(S(9f), S(10f));
        var arm = S(4f);
        draw.AddLine(c - new float2(arm, arm), c + new float2(arm, arm), hover ? Text : TextDim, S(1.3f));
        draw.AddLine(c + new float2(-arm, arm), c + new float2(arm, -arm), hover ? Text : TextDim, S(1.3f));
        Canvas.Text(draw, Regular, S(13f), new float2(close.X - S(8f), at.Y + S(2.5f)), statusColor == TextFaint ? TextDim : statusColor, Sentence(status), Align.Right);

        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(width, S(22f)));
        Separator();
        return clicked;
    }

    // --- Sections. ---

    /// Tabs: text across the width, the picked one underlined. Returns the one picked.
    public static int Tabs(string id, ReadOnlySpan<string> labels, int current)
    {
        var draw = Draw;
        var picked = current;
        var width = Width / labels.Length;
        var height = S(26f);
        var at = ImGui.GetCursorScreenPos();
        for (var i = 0; i < labels.Length; i++)
        {
            var min = at + new float2(i * width, 0f);
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"{id}.{i}", new float2(width, height)) && i != current)
            {
                picked = i;
                Sfx.Tick();
            }

            var hover = ImGui.IsItemHovered();
            var on = i == current;
            Canvas.Text(draw, on ? Bold : Medium, FontSize, min + new float2(width * 0.5f, (height - FontSize) * 0.5f - S(1f)), on || hover ? Text : TextDim, labels[i], Align.Center);
            if (on)
                draw.AddRectFilled(new float2(min.X, at.Y + height - S(2f)), new float2(min.X + width, at.Y + height), Accent);
        }

        draw.AddLine(new float2(at.X, at.Y + height), new float2(at.X + Width, at.Y + height), Stroke, 1f);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, height + S(4f)));
        return picked;
    }

    /// Starts a section: a header row with its title (a status dot if given, a note at the right), then its rows. Sections
    /// don't nest.
    public static void BeginCard(string title, string? note = null, Rgba? lamp = null)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var width = Width;
        var height = S(24f);
        draw.AddRectFilled(at, at + new float2(width, height), Raised.WithAlpha(0.55f), Rounding);
        var x = at.X + S(8f);
        if (lamp is { } color)
        {
            draw.AddCircleFilled(new float2(x + S(3f), at.Y + height * 0.5f), S(3f), color);
            x += S(12f);
        }

        Canvas.Text(draw, Bold, S(13.5f), new float2(x, at.Y + (height - S(13.5f)) * 0.5f - S(1f)), Text, Sentence(title));
        if (note is not null)
            Canvas.Text(draw, Regular, S(13f), new float2(at.X + width - S(8f), at.Y + (height - S(13f)) * 0.5f - S(1f)), TextDim, note, Align.Right);
        ImGui.Dummy(new float2(width, height + S(2f)));
        Sections.Push(at.Y);
        ImGui.Indent(S(4f));
        ImGui.PushItemWidth(Width - S(4f));
    }

    public static void EndCard()
    {
        ImGui.PopItemWidth();
        ImGui.Unindent(S(4f));
        Sections.Pop();
        ImGui.Dummy(new float2(1f, S(8f)));
    }

    public static void Separator()
    {
        var at = ImGui.GetCursorScreenPos();
        Draw.AddLine(new float2(at.X, at.Y + S(2f)), new float2(at.X + Width, at.Y + S(2f)), Stroke, 1f);
        ImGui.Dummy(new float2(Width, S(5f)));
    }

    // --- Text. ---

    /// A label over a control.
    public static void Caption(string text)
    {
        var at = ImGui.GetCursorScreenPos();
        Canvas.Text(Draw, Regular, S(13f), at, TextDim, Sentence(text));
        ImGui.Dummy(new float2(Width, S(16f)));
    }

    /// A line or more of plain words, wrapped to the width.
    public static void Note(string text, Rgba? color = null)
    {
        var draw = Draw;
        var size = S(13f);
        foreach (var line in Canvas.Wrap(text, Regular, size, Width))
        {
            var at = ImGui.GetCursorScreenPos();
            Canvas.Text(draw, Regular, size, at, color ?? TextDim, line);
            ImGui.Dummy(new float2(Width, S(17f)));
        }
    }

    /// A dot and a line beside it, in its colour.
    public static void Status(Rgba color, string text)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        draw.AddCircleFilled(at + new float2(S(4f), S(9f)), S(3f), color);
        var size = S(13.5f);
        var lines = Canvas.Wrap(text, Medium, size, Width - S(14f));
        for (var i = 0; i < lines.Count; i++)
            Canvas.Text(draw, Medium, size, at + new float2(S(14f), i * S(18f)), Rgba.Mix(Text, color, 0.4f), lines[i]);
        ImGui.Dummy(new float2(Width, MathF.Max(1, lines.Count) * S(18f)));
    }

    /// Readings as property rows: the label at the left, the value at the right; two to a row where there's room.
    public static void Stats(ReadOnlySpan<Stat> stats, int columns = 3, bool big = false)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var across = Math.Clamp(columns, 1, 2);
        var gap = S(14f);
        var width = (Width - gap * (across - 1)) / across;
        var row = S(20f);
        var rows = (stats.Length + across - 1) / across;
        for (var i = 0; i < stats.Length; i++)
        {
            var cell = at + new float2(i % across * (width + gap), i / across * row);
            var stat = stats[i];
            var value = Ellipsize(stat.Value, Mono, S(13f), width * 0.6f);
            Canvas.Text(draw, Mono, S(13f), cell + new float2(width, S(2f)), stat.Color ?? Text, value, Align.Right);
            var room = width - Canvas.TextSize(Mono, S(13f), value).X - S(8f);
            Canvas.Text(draw, Regular, S(13f), cell + new float2(0f, S(1f)), TextDim, Ellipsize(stat.Label, Regular, S(13f), room));
        }

        ImGui.Dummy(new float2(Width, rows * row));
    }

    // --- Controls. ---

    /// A button across the width (or as wide as asked). Primary is filled with the accent; a tint fills it with that.
    public static bool Button(string id, string label, float width = 0f, ButtonKind kind = ButtonKind.Secondary, bool enabled = true, Rgba? tint = null)
        => Button(id, label, out _, width, kind, enabled, tint);

    /// The same, saying whether it's held down (for a horn).
    public static bool Button(string id, string label, out bool held, float width = 0f, ButtonKind kind = ButtonKind.Secondary, bool enabled = true, Rgba? tint = null)
    {
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(width > 0f ? width : Width, RowHeight);
        var clicked = Cell(id, label, at, size, kind == ButtonKind.Primary, enabled, tint, out held);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(size);
        return clicked;
    }

    /// A row of buttons sharing the width (evenly, or by weights). Returns the one clicked, or -1.
    public static int Buttons(string id, ReadOnlySpan<string> labels, ReadOnlySpan<bool> enabled = default, ReadOnlySpan<float> weights = default)
    {
        var gap = S(4f);
        var share = Width - gap * (labels.Length - 1);
        var sum = 0f;
        for (var i = 0; i < labels.Length; i++)
            sum += weights.IsEmpty ? 1f : weights[i];
        var at = ImGui.GetCursorScreenPos();
        var hit = -1;
        var x = 0f;
        for (var i = 0; i < labels.Length; i++)
        {
            var width = share * (weights.IsEmpty ? 1f : weights[i]) / sum;
            if (Cell($"{id}.{i}", labels[i], at + new float2(x, 0f), new float2(width, RowHeight), false, enabled.IsEmpty || enabled[i], null, out _))
                hit = i;
            x += width + gap;
        }

        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, RowHeight));
        return hit;
    }

    /// One of several in a joined strip, the picked one lit. Returns the one picked. Across the width, or as wide as asked.
    public static int Segmented(string id, ReadOnlySpan<string> labels, int current, bool enabled = true, float across = 0f)
    {
        var draw = Draw;
        var total = across > 0f ? across : Width;
        var width = total / labels.Length;
        var at = ImGui.GetCursorScreenPos();
        var height = RowHeight;
        draw.AddRectFilled(at, at + new float2(total, height), Raised, Rounding);
        var picked = current;
        for (var i = 0; i < labels.Length; i++)
        {
            var min = at + new float2(i * width, 0f);
            ImGui.SetCursorScreenPos(min);
            var clicked = ImGui.InvisibleButton($"{id}.{i}", new float2(width, height)) && enabled;
            var hover = ImGui.IsItemHovered() && enabled;
            var on = i == current;
            if (on)
                draw.AddRectFilled(min + new float2(S(2f), S(2f)), min + new float2(width - S(2f), height - S(2f)), Accent.WithAlpha(enabled ? 0.9f : 0.4f), Rounding);
            else if (hover)
                draw.AddRectFilled(min + new float2(S(2f), S(2f)), min + new float2(width - S(2f), height - S(2f)), RaisedHover, Rounding);
            else if (i > 0 && i - 1 != current)
                draw.AddLine(new float2(min.X, min.Y + S(6f)), new float2(min.X, min.Y + height - S(6f)), Stroke, 1f);
            var ink = !enabled ? TextFaint : on ? OnAccent : hover ? Text : TextDim;
            var text = Ellipsize(labels[i], Medium, S(13f), width - S(8f));
            Canvas.Text(draw, Medium, S(13f), min + new float2(width * 0.5f, (height - S(13f)) * 0.5f - S(1f)), ink, text, Align.Center);
            if (clicked && i != current)
            {
                picked = i;
                Sfx.Tick();
            }
        }

        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(total, height));
        return picked;
    }

    /// A checkbox and its name. Returns true when it's flipped. Sits on the current line, so several go across with SameLine.
    public static bool Toggle(string id, string label, ref bool on, bool enabled = true)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var text = Canvas.TextSize(Regular, S(13.5f), label);
        var box = S(14f);
        var whole = new float2(box + S(7f) + text.X, MathF.Max(box, S(18f)));
        var clicked = ImGui.InvisibleButton(id, whole) && enabled;
        var hover = ImGui.IsItemHovered() && enabled;
        var alpha = enabled ? 1f : 0.4f;
        var boxMin = at + new float2(0f, (whole.Y - box) * 0.5f);
        draw.AddRectFilled(boxMin, boxMin + new float2(box, box), (on ? Accent : hover ? RaisedHover : Raised).Fade(alpha), Rounding);
        if (on)
        {
            var c = boxMin + new float2(box, box) * 0.5f;
            draw.AddLine(c + new float2(-S(3.5f), 0f), c + new float2(-S(1f), S(2.5f)), OnAccent.Fade(alpha), S(1.6f));
            draw.AddLine(c + new float2(-S(1f), S(2.5f)), c + new float2(S(3.5f), -S(3f)), OnAccent.Fade(alpha), S(1.6f));
        }

        Canvas.Text(draw, Regular, S(13.5f), at + new float2(box + S(7f), (whole.Y - S(13.5f)) * 0.5f - S(1f)), (on || hover ? Text : TextDim).Fade(alpha), label);
        if (clicked)
        {
            on = !on;
            Sfx.Tick();
        }

        return clicked;
    }

    /// A slider over 0 to 1 across the width: a filled bar, dragged (a click jumps to it). Returns true when the value
    /// changed; released is true on the frame it's let go.
    public static bool Slider(string id, ref float value, out bool released, bool enabled = true)
    {
        var at = ImGui.GetCursorScreenPos();
        var changed = Bar(id, ref value, at, new float2(Width, RowHeight), null, null, enabled, out released);
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new float2(Width, RowHeight));
        return changed;
    }

    /// A value bar: filled to the value in a dim accent, a label inside at the left and the reading at the right.
    public static bool Bar(string id, ref float value, float2 at, float2 size, string? label, string? reading, bool enabled, out bool released)
    {
        var draw = Draw;
        ImGui.SetCursorScreenPos(at);
        ImGui.InvisibleButton(id, size);
        var active = ImGui.IsItemActive() && enabled;
        released = ImGui.IsItemDeactivated() && enabled;
        var hover = ImGui.IsItemHovered() && enabled;
        var was = value;
        if (active)
            value = Math.Clamp((ImGui.GetIO().MousePos.X - at.X) / MathF.Max(size.X, 1f), 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        var alpha = enabled ? 1f : 0.45f;
        draw.AddRectFilled(at, at + size, (hover || active ? RaisedHover : Raised).Fade(alpha), Rounding);
        if (value > 0f)
            draw.AddRectFilled(at, at + new float2(size.X * value, size.Y), Accent.WithAlpha(active ? 0.55f : 0.4f).Fade(alpha), Rounding);
        var y = at.Y + (size.Y - S(13f)) * 0.5f - S(1f);
        if (label is not null)
            Canvas.Text(draw, Regular, S(13f), new float2(at.X + S(8f), y), (enabled ? TextDim : TextFaint), label);
        if (reading is not null)
            Canvas.Text(draw, Mono, S(13f), new float2(at.X + size.X - S(8f), y + S(0.5f)), enabled ? Text : TextFaint, reading, Align.Right);
        if (hover)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        return active && value != was;
    }

    /// A progress bar.
    public static void Gauge(float value, Rgba color)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(Width, S(6f));
        draw.AddRectFilled(at, at + size, Raised, S(3f));
        draw.AddRectFilled(at, at + new float2(size.X * Math.Clamp(value, 0f, 1f), size.Y), color, S(3f));
        ImGui.Dummy(new float2(Width, S(10f)));
    }

    /// A list row: its number, its name, a word on the right; lit when picked. Returns true when clicked.
    public static bool Row(string id, string number, string title, string right, bool picked, float width)
    {
        var draw = Draw;
        var at = ImGui.GetCursorScreenPos();
        var size = new float2(width, RowHeight);
        var clicked = ImGui.InvisibleButton(id, size);
        var hover = ImGui.IsItemHovered();
        if (picked)
            draw.AddRectFilled(at, at + size, Accent.WithAlpha(0.3f), Rounding);
        else if (hover)
            draw.AddRectFilled(at, at + size, RaisedHover.WithAlpha(0.6f), Rounding);
        var y = at.Y + (size.Y - S(13.5f)) * 0.5f - S(1f);
        Canvas.Text(draw, Mono, S(12.5f), new float2(at.X + S(8f), y + S(1f)), TextFaint, number);
        Canvas.Text(draw, Medium, S(13.5f), new float2(at.X + S(36f), y), picked || hover ? Text : TextDim, Ellipsize(title, Medium, S(13.5f), width - S(120f)));
        Canvas.Text(draw, Regular, S(12.5f), new float2(at.X + width - S(8f), y + S(1f)), TextFaint, right, Align.Right);
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A button: flat, lit when picked, brighter on hover.
    private static bool Cell(string id, string label, float2 min, float2 size, bool lit, bool enabled, Rgba? tint, out bool held)
    {
        var draw = Draw;
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, size) && enabled;
        var hovered = ImGui.IsItemHovered() && enabled;
        held = ImGui.IsItemActive() && enabled;
        var max = min + size;
        var alpha = enabled ? 1f : 0.4f;
        Rgba fill, ink;
        if (tint is { } t)
            (fill, ink) = (Rgba.Mix(t, Rgba.White, held ? 0.2f : hovered ? 0.1f : 0f), Rgba.White);
        else if (lit)
            (fill, ink) = (Rgba.Mix(Accent, Rgba.White, held ? 0.2f : hovered ? 0.1f : 0f), OnAccent);
        else
            (fill, ink) = (held ? Rgba.Mix(RaisedHover, Rgba.White, 0.06f) : hovered ? RaisedHover : Raised, Text);
        draw.AddRectFilled(min, max, fill.Fade(alpha), Rounding);
        var text = Ellipsize(Sentence(label), Medium, S(13.5f), size.X - S(12f));
        Canvas.Text(draw, Medium, S(13.5f), new float2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f - S(8f)), ink.Fade(alpha), text, Align.Center);
        if (clicked)
            Sfx.Tick();
        return clicked;
    }

    /// A label as the editor writes it: an all-capitals one (from the deck's days) in sentence case.
    public static string Sentence(string text) => Style.Case(text);

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
