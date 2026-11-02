using Brutal.ImGuiApi;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace RailWerkz.UI;

public enum ButtonKind
{
    /// Filled with the accent: the one thing to do on a page.
    Primary,
    /// A framed plate: everything else.
    Secondary,
}

/// Draw-list widgets for the deck. Each takes an id (for its animation state and ImGui's) and draws itself where
/// it's told; interaction goes through an invisible ImGui button, so a current window is needed (the deck's).
public static class Widgets
{
    private static readonly HashSet<string> Hovered = [];

    public static bool Button(ImDrawListPtr draw, string id, string label, float2 min, float2 size, ButtonKind kind = ButtonKind.Secondary, bool enabled = true)
    {
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, size) && enabled && !Keyboard.PointerBlocked;
        var hovered = ImGui.IsItemHovered() && enabled && !Keyboard.PointerBlocked;
        var held = ImGui.IsItemActive() && enabled;

        if (hovered && !Hovered.Contains(id))
            Sfx.Hover();
        if (hovered) Hovered.Add(id); else Hovered.Remove(id);

        var hover = Tween.Float(id, "hover", hovered ? 1f : 0f, 0.12f);
        var max = min + size;
        var alpha = enabled ? 1f : 0.4f;
        Rgba textColor;

        if (kind == ButtonKind.Primary)
        {
            draw.AddRectFilled(min, max, Rgba.Mix(Accent, Rgba.White, (held ? 0.25f : 0.1f) * hover).Fade(alpha));
            textColor = OnAccent.Fade(alpha);
        }
        else
        {
            draw.AddRectFilled(min, max, Rgba.Lerp(Surface, Accent.WithAlpha(0.15f), held ? 1f : hover * 0.6f).Fade(alpha));
            draw.AddRect(min, max, Rgba.Mix(StrokeStrong, Accent, hover).Fade(alpha), 0f, ImDrawFlags.None, 1f);
            textColor = Style.Text.Fade(alpha);
        }

        var text = label.ToUpperInvariant();
        var width = Canvas.SpacedWidth(Bold, S(13f), text, 0.12f);
        Canvas.Spaced(draw, Bold, S(13f), new float2((min.X + max.X - width) * 0.5f, (min.Y + max.Y) * 0.5f - S(8f)), textColor, text, 0.12f);

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (clicked)
            Sfx.Select();

        return clicked;
    }

    /// A one-line text field fed by Keyboard.Edit while focused. Returns true when Return is pressed in it.
    public static bool TextField(ImDrawListPtr draw, string id, string label, ref string text, float2 min, float width, bool focused, int maxLength = 64, Func<char, bool>? allow = null)
    {
        var height = S(48f);
        var max = min + new float2(width, height);
        var focus = Tween.Float(id, "focus", focused ? 1f : 0f, 0.15f);
        var submitted = focused && Keyboard.Edit(ref text, maxLength, allow);

        draw.AddRectFilled(min, max, Surface);
        draw.AddRect(min, max, Rgba.Mix(StrokeStrong, Accent, focus), 0f, ImDrawFlags.None, 1f);
        Canvas.Spaced(draw, Medium, S(11f), min + new float2(S(12f), S(7f)), Rgba.Mix(TextDim, Accent, focus), label.ToUpperInvariant(), 0.12f);
        Canvas.Text(draw, Mono, S(16f), min + new float2(S(12f), S(22f)), Style.Text, text);

        if (focused && Procedural.Pulse(1.2f) > 0.5f)
        {
            var caretX = min.X + S(12f) + Canvas.TextSize(Mono, S(16f), text).X + S(2f);
            draw.AddRectFilled(new float2(caretX, min.Y + S(23f)), new float2(caretX + S(9f), min.Y + S(40f)), Accent);
        }

        return submitted;
    }
}
