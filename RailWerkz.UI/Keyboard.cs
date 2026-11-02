using System.Text;
using Brutal.ImGuiApi;

namespace RailWerkz.UI;

/// Typed characters, fed by the app from the window's character events, for the deck's text fields.
public static class Keyboard
{
    private static readonly Queue<char> Typed = new();

    public static void Feed(uint codepoint)
    {
        if (codepoint is >= 32 and < 127)
            Typed.Enqueue((char)codepoint);
    }

    /// Set by the app while something above the deck (a dev window) has the pointer: the deck's menus ignore it.
    public static bool PointerBlocked { get; set; }

    /// Set while a text field above the deck has the keyboard: the deck ignores keys and typing.
    public static bool KeysBlocked { get; set; }

    /// Drops what nobody read this frame.
    public static void EndFrame() => Typed.Clear();

    public static bool Pressed(ImGuiKey key, bool repeat = false) => !KeysBlocked && ImGui.IsKeyPressed(key, repeat);

    /// A left click meant for the deck.
    public static bool Clicked() => !PointerBlocked && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

    /// Edits text with what was typed; true when Return is pressed.
    public static bool Edit(ref string text, int maxLength, Func<char, bool>? allow = null)
    {
        if (KeysBlocked)
        {
            Typed.Clear();
            return false;
        }

        var builder = new StringBuilder(text);
        var changed = false;

        while (Typed.Count > 0)
        {
            var c = Typed.Dequeue();
            if (builder.Length < maxLength && (allow is null || allow(c)))
            {
                builder.Append(c);
                changed = true;
                Sfx.Tick();
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, true) && builder.Length > 0)
        {
            builder.Length--;
            changed = true;
            Sfx.Tick();
        }

        if (changed)
            text = builder.ToString();

        return ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
    }
}
