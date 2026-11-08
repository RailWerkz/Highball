using Brutal.ImGuiApi;

namespace RailWerkz.UI;

/// Keeps floating windows wholly on the screen: never bigger than the space for them, and pushed back inside when they'd
/// hang off an edge (dragged there, opened there, or left there when the display shrank). A screen with a header or a footer
/// sets Top and Bottom for the frame, so windows stay between them, and calls Reset after.
public static class WindowBounds
{
    public static float Top;
    public static float Bottom = float.MaxValue;

    public static void Reset()
    {
        Top = 0f;
        Bottom = float.MaxValue;
    }

    /// The space windows may use this frame.
    public static (float2 Min, float2 Max) Area
    {
        get
        {
            var display = ImGui.GetIO().DisplaySize;
            var top = Math.Clamp(Top, 0f, display.Y);
            return (new float2(0f, top), new float2(display.X, MathF.Max(top + 1f, MathF.Min(display.Y, Bottom))));
        }
    }

    /// Before Begin: the window may grow no bigger than the space (auto-resizing ones scroll past that).
    public static void Limit()
    {
        var (min, max) = Area;
        var most = max - min;
        ImGui.SetNextWindowSizeConstraints(new float2(MathF.Min(120f, most.X), MathF.Min(40f, most.Y)), most);
    }

    /// After Begin: moves the window back inside the space if any of it is outside.
    public static void Keep()
    {
        var (min, max) = Area;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var x = Math.Clamp(pos.X, min.X, MathF.Max(min.X, max.X - size.X));
        var y = Math.Clamp(pos.Y, min.Y, MathF.Max(min.Y, max.Y - size.Y));
        if (x != pos.X || y != pos.Y)
            ImGui.SetWindowPos(new float2(x, y));
    }

    /// Begin with both: Limit before it and Keep after.
    public static bool Begin(ImString name, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        Limit();
        var shown = ImGui.Begin(name, ref open, flags);
        Keep();
        return shown;
    }
}
