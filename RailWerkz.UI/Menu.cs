using Brutal.ImGuiApi;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace RailWerkz.UI;

/// One line of a menu. An item either does something (OnSelect), or holds a value changed with left and right
/// (Value + OnChange), shown beside it; Meter draws the value as a bar.
public sealed class MenuItem
{
    public required string Label { get; init; }
    public Action? OnSelect { get; init; }
    public Func<string>? Value { get; init; }
    public Action<int>? OnChange { get; init; }
    public Func<float>? Meter { get; init; }
    public Func<bool>? Enabled { get; init; }
    public bool SeparatorAfter { get; init; }

    /// Changing the value plays no tick (the change makes its own sound).
    public bool Silent { get; init; }

    public bool IsEnabled => Enabled?.Invoke() ?? true;
}

public enum MenuLook
{
    /// Labels stacked and right-aligned on an anchor: the main menu and other front pages.
    Hero,
    /// Settings rows: a label on the left, its value on the right, in one glass card.
    Rows,
}

/// A vertical menu, driven by keyboard (arrows, W/S, Enter, Escape, left/right for values) and mouse alike. The
/// selection is a sprung accent bar that glides between rows; items slide in staggered when the menu opens.
public sealed class Menu
{
    private readonly string _id;
    private int _selected;
    private double _openedAt;
    private int _dragging = -1;

    /// Split-flap characters a meter row's readout always takes, so its arrow stays put as the value changes.
    private const int MeterChars = 6;

    public string Title { get; set; }
    public List<MenuItem> Items { get; } = [];
    public Action? OnBack { get; set; }
    public MenuLook Look { get; set; } = MenuLook.Hero;

    /// Off: no title or rule; the list starts at the top it's drawn at (for menus inside tabs).
    public bool ShowHeader { get; set; } = true;

    /// Rows layout: the card's width.
    public float RowWidth { get; set; } = 720f;

    public int Selected
    {
        get => _selected;
        set => _selected = Math.Clamp(value, 0, Math.Max(0, Items.Count - 1));
    }

    public MenuItem? Current => Items.Count > 0 ? Items[_selected] : null;

    public Menu(string title)
    {
        Title = title;
        _id = $"menu.{title}.{GetHashCode()}";
    }

    public Menu Add(MenuItem item)
    {
        Items.Add(item);
        return this;
    }

    /// Restarts the entrance (the items slide in again).
    public void Open()
    {
        _openedAt = Tween.Now;

        if (Items.Count > 0 && !Items[_selected].IsEnabled)
            Move(1);
    }

    private void Move(int direction)
    {
        if (Items.Count == 0)
            return;

        for (var i = 0; i < Items.Count; i++)
        {
            _selected = (_selected + direction + Items.Count) % Items.Count;
            if (Items[_selected].IsEnabled)
                break;
        }

        Sfx.Hover();
    }

    /// Reads input; call once a frame when this menu has focus.
    public void Update(bool focused = true)
    {
        if (!focused || Items.Count == 0)
            return;

        if (Keyboard.Pressed(ImGuiKey.UpArrow, true) || Keyboard.Pressed(ImGuiKey.W, true))
            Move(-1);

        if (Keyboard.Pressed(ImGuiKey.DownArrow, true) || Keyboard.Pressed(ImGuiKey.S, true))
            Move(1);

        var item = Items[_selected];

        if (item.OnChange is not null && item.IsEnabled)
        {
            if (Keyboard.Pressed(ImGuiKey.LeftArrow, true) || Keyboard.Pressed(ImGuiKey.A, true))
                Change(item, -1);

            if (Keyboard.Pressed(ImGuiKey.RightArrow, true) || Keyboard.Pressed(ImGuiKey.D, true))
                Change(item, 1);
        }

        if (Keyboard.Pressed(ImGuiKey.Enter) || Keyboard.Pressed(ImGuiKey.KeypadEnter) || Keyboard.Pressed(ImGuiKey.Space))
            Activate(item);

        if ((Keyboard.Pressed(ImGuiKey.Escape) || Keyboard.Pressed(ImGuiKey.Backspace)) && OnBack is not null)
        {
            Sfx.Back();
            OnBack();
        }
    }

    private void Change(MenuItem item, int direction)
    {
        item.OnChange!(direction);
        if (!item.Silent)
            Sfx.Hover();
    }

    /// Steps a meter's value toward a 0..1 target (a click or drag on its bar), stopping at the closest step.
    private static bool SeekMeter(MenuItem item, float target)
    {
        var changed = false;
        for (var n = 0; n < 200; n++)
        {
            var current = item.Meter!();
            var direction = target > current ? 1 : -1;
            item.OnChange!(direction);
            var next = item.Meter!();

            if (MathF.Abs(next - target) >= MathF.Abs(current - target))
            {
                if (next != current)
                    item.OnChange!(-direction);
                break;
            }

            changed = true;
        }

        return changed;
    }

    private void Activate(MenuItem item)
    {
        if (!item.IsEnabled)
            return;

        Tween.Since(_id, "press", true);

        if (item.OnSelect is not null)
        {
            Sfx.Select();
            item.OnSelect();
        }
        else if (item.OnChange is not null)
        {
            Change(item, 1);
        }
    }

    private float ItemHeight => Look == MenuLook.Hero ? S(44f) : S(52f);
    private float SeparatorGap => Look == MenuLook.Hero ? S(16f) : S(14f);

    private float RowTop(float listTop, int index)
    {
        var y = listTop;
        for (var i = 0; i < index; i++)
            y += ItemHeight + (Items[i].SeparatorAfter ? SeparatorGap : 0f);
        return y;
    }

    /// The height of the rows alone, without the header.
    public float ListHeight => RowTop(0f, Items.Count);

    /// Hero: labels right-aligned on x. Rows: the card's left edge at x. Returns the bottom of the list.
    public float Draw(ImDrawListPtr draw, float x, float top, bool focused = true) =>
        Look == MenuLook.Hero ? DrawHero(draw, x, top, focused) : DrawRows(draw, x, top, focused);

    private void DrawHeader(ImDrawListPtr draw, float left, float top, out float listTop)
    {
        if (!ShowHeader)
        {
            listTop = top;
            return;
        }

        var enter = Tween.Progress(_openedAt, 0.5f, Ease.OutQuint);
        Canvas.Text(draw, Heavy, S(34f), new float2(left + (1f - enter) * S(-30f), top), Style.Text.Fade(enter), Title);
        listTop = top + S(84f);
    }

    /// After the old shooters' stacked menus: the title and labels right-aligned on an anchor, the selection a lit
    /// bar reaching in from the left to the anchor, with a tick beside it.
    private float DrawHero(ImDrawListPtr draw, float anchor, float top, bool focused)
    {
        var mouse = ImGui.GetIO().MousePos;
        var mouseMoved = ImGui.GetIO().MouseDelta.X * ImGui.GetIO().MouseDelta.X + ImGui.GetIO().MouseDelta.Y * ImGui.GetIO().MouseDelta.Y > 0.5f;
        var clicked = focused && Keyboard.Clicked();
        var height = ItemHeight;
        var reach = S(440f);

        var listTop = top;
        if (ShowHeader)
        {
            var enter = Tween.Progress(_openedAt, 0.5f, Ease.OutQuint);
            Canvas.Text(draw, Heavy, S(34f), new float2(anchor + (1f - enter) * S(30f), top), Style.Text.Fade(enter), Title, Align.Right);
            var ruleY = top + S(54f);
            draw.AddRectFilled(new float2(anchor - S(340f) * enter, ruleY), new float2(anchor + S(12f), ruleY + 1f), StrokeStrong.Fade(enter));
            listTop = ruleY + S(18f);
        }

        var targetY = RowTop(listTop, _selected);
        var barY = Tween.Spring(Tween.Key(_id, "bar"), targetY, 380f, 34f);
        var shown = Tween.Float(_id, "barShown", focused && Items.Count > 0 ? 1f : 0f, 0.3f, Ease.OutCubic, initial: 0f);
        var press = Tween.Since(_id, "press");
        var flash = press < 0.3f ? 1f - press / 0.3f : 0f;

        if (shown > 0.01f)
        {
            var min = new float2(anchor - reach * shown, barY + S(4f));
            var max = new float2(anchor + S(14f), barY + height - S(4f));
            Canvas.HGradient(draw, min, max, Accent.WithAlpha(0f), Accent.WithAlpha((0.9f + 0.1f * flash) * shown));
            draw.AddRectFilled(new float2(max.X + S(6f), min.Y), new float2(max.X + S(10f), max.Y), Accent.Fade(shown));
        }

        var y = listTop;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var enabled = item.IsEnabled;
            var selected = focused && i == _selected;
            var enter = Tween.Stagger(_openedAt, i, 0.04f, 0.45f, Ease.OutQuint);
            var rowMin = new float2(anchor - reach, y);
            var rowMax = new float2(anchor + S(30f), y + height);

            if (focused && enabled && !Keyboard.PointerBlocked && mouse.X >= rowMin.X && mouse.X <= rowMax.X && mouse.Y >= rowMin.Y && mouse.Y <= rowMax.Y)
            {
                if (_selected != i && mouseMoved)
                {
                    _selected = i;
                    Sfx.Hover();
                }

                if (clicked)
                {
                    _selected = i;
                    Activate(item);
                }

                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            var color = Tween.Color($"{_id}.{i}", "color", !enabled ? TextFaint : selected ? OnAccent : TextDim, 0.15f);
            var labelSize = Canvas.TextSize(Bold, S(22f), item.Label);
            var x = anchor - (1f - enter) * S(40f);
            Canvas.Text(draw, Bold, S(22f), new float2(x, y + (height - labelSize.Y) * 0.5f), color.Fade(enter), item.Label, Align.Right);

            y += height;

            if (item.SeparatorAfter)
            {
                draw.AddRectFilled(new float2(anchor - S(340f), y + SeparatorGap * 0.5f), new float2(anchor + S(12f), y + SeparatorGap * 0.5f + 1f), Stroke.Fade(enter));
                y += SeparatorGap;
            }
        }

        return y;
    }

    /// Settings rows on one plate, like the switches of a control panel: a lamp per row (lit on the selection),
    /// the label engraved on the left, the value on a split-flap readout on the right.
    private float DrawRows(ImDrawListPtr draw, float left, float top, bool focused)
    {
        DrawHeader(draw, left, top, out var listTop);
        var mouse = ImGui.GetIO().MousePos;
        var mouseMoved = ImGui.GetIO().MouseDelta.X * ImGui.GetIO().MouseDelta.X + ImGui.GetIO().MouseDelta.Y * ImGui.GetIO().MouseDelta.Y > 0.5f;
        var clicked = focused && Keyboard.Clicked();
        var height = ItemHeight;
        var width = S(RowWidth);
        var pad = S(16f);
        var enter = Tween.Progress(_openedAt, 0.35f, Ease.OutCubic);

        Canvas.Plate(draw, new float2(left - pad, listTop - pad), new float2(left + width + pad, RowTop(listTop, Items.Count) + pad), enter);

        var barY = Tween.Spring(Tween.Key(_id, "bar"), RowTop(listTop, _selected), 420f, 40f);
        if (focused && Items.Count > 0)
            draw.AddRectFilled(new float2(left - S(4f), barY + S(4f)), new float2(left + width + S(4f), barY + height - S(4f)), Accent.WithAlpha(0.09f * enter));

        var y = listTop;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var enabled = item.IsEnabled;
            var selected = focused && i == _selected;
            var rowMin = new float2(left, y);
            var rowMax = new float2(left + width, y + height);
            var mid = y + height * 0.5f;

            // The value's layout first, so clicks can be aimed at its parts: [<] readout [bar] [>].
            var right = left + width - S(10f);
            var flapHeight = S(24f);
            var changeable = item.OnChange is not null && enabled;
            var value = item.Value?.Invoke() ?? "";
            var valueRight = changeable ? right - S(18f) : right;
            var barWidth = S(160f);
            var barMin = new float2(valueRight - barWidth, mid - S(6f));
            var flapRight = item.Meter is not null ? barMin.X - S(14f) : valueRight;
            var flapChars = item.Meter is not null ? Math.Max(MeterChars, value.Length) : value.Length;
            var leftArrowX = flapRight - SplitFlap.Width(flapChars, flapHeight) - S(14f);
            var rightArrowX = right - S(4f);
            var hit = S(14f);

            var hovered = focused && enabled && !Keyboard.PointerBlocked && mouse.X >= rowMin.X && mouse.X <= rowMax.X && mouse.Y >= rowMin.Y && mouse.Y <= rowMax.Y;
            var onLeftArrow = hovered && changeable && MathF.Abs(mouse.X - leftArrowX) <= hit;
            var onRightArrow = hovered && changeable && mouse.X >= rightArrowX - hit;
            var onBar = hovered && changeable && item.Meter is not null && mouse.X >= barMin.X && mouse.X <= barMin.X + barWidth;
            var onValue = hovered && changeable && item.Meter is null && mouse.X > leftArrowX + hit && mouse.X < rightArrowX - hit;

            if (hovered)
            {
                if (_selected != i && mouseMoved)
                {
                    _selected = i;
                    Sfx.Hover();
                }

                // Arrows step, and keep stepping while held; the readout steps forward; the bar seeks.
                var repeat = ImGui.IsMouseClicked(ImGuiMouseButton.Left, true);
                if (onLeftArrow && repeat)
                {
                    _selected = i;
                    Change(item, -1);
                }
                else if (onRightArrow && repeat)
                {
                    _selected = i;
                    Change(item, 1);
                }
                else if (onBar && clicked)
                {
                    _selected = i;
                    _dragging = i;
                }
                else if (onValue && clicked)
                {
                    _selected = i;
                    Change(item, 1);
                }
                else if (clicked)
                {
                    _selected = i;
                    if (item.OnSelect is not null)
                        Activate(item);
                }

                if (item.OnSelect is not null || onLeftArrow || onRightArrow || onBar || onValue)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (_dragging == i)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && item.Meter is not null)
                {
                    if (SeekMeter(item, Math.Clamp((mouse.X - barMin.X) / barWidth, 0f, 1f)) && !item.Silent)
                        Sfx.Hover();
                }
                else
                {
                    _dragging = -1;
                }
            }

            Canvas.Lamp(draw, new float2(left + S(10f), mid), S(3.5f), Accent, enter, selected);
            var labelColor = (!enabled ? TextFaint : selected ? Style.Text : TextDim).Fade(enter);
            var label = item.Label.ToUpperInvariant();
            Canvas.Spaced(draw, Medium, S(14f), new float2(left + S(32f), mid - S(8.5f)), labelColor, label, 0.08f);

            var arrowColor = (selected ? Accent : TextFaint).Fade(enter);
            var hotColor = Style.Text.Fade(enter);

            if (item.Value is not null)
            {
                if (item.Meter is not null)
                {
                    Canvas.SegmentBar(draw, barMin, barMin + new float2(barWidth, S(12f)), item.Meter(), selected ? Accent : TextDim, 16, enter);
                    SplitFlap.Draw(draw, $"{_id}.v{i}", value, new float2(flapRight, mid - flapHeight * 0.5f), flapHeight, Style.Text, MeterChars, Align.Right, enter);
                }
                else
                {
                    SplitFlap.Draw(draw, $"{_id}.v{i}", value, new float2(flapRight, mid - flapHeight * 0.5f), flapHeight, Style.Text, 0, Align.Right, enter * (enabled ? 1f : 0.5f));
                }

                if (changeable)
                {
                    Canvas.Arrow(draw, new float2(leftArrowX, mid), S(9f), onLeftArrow ? hotColor : arrowColor, true);
                    Canvas.Arrow(draw, new float2(rightArrowX, mid), S(9f), onRightArrow ? hotColor : arrowColor);
                }
            }
            else if (item.OnSelect is not null)
            {
                Canvas.Arrow(draw, new float2(rightArrowX, mid), S(9f), arrowColor);
            }

            y += height;

            if (i < Items.Count - 1)
            {
                var lineY = y + (item.SeparatorAfter ? SeparatorGap * 0.5f : 0f);
                draw.AddLine(new float2(left, lineY), new float2(left + width, lineY), (item.SeparatorAfter ? StrokeStrong : Stroke).Fade(enter), 1f);
            }

            if (item.SeparatorAfter)
                y += SeparatorGap;
        }

        return y;
    }
}
