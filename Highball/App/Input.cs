using Brutal.GlfwApi;

namespace Highball.App;

/// Keyboard and mouse state for the game, from the window's events: what's held, what went down or up this frame,
/// how far the mouse moved and the wheel.
public static class Input
{
    private static readonly HashSet<GlfwKey> Held = [];
    private static readonly HashSet<GlfwKey> Down = [];
    private static readonly HashSet<GlfwKey> Up = [];
    private static readonly bool[] Buttons = new bool[8];
    private static readonly bool[] ButtonsDown = new bool[8];
    private static readonly bool[] ButtonsUp = new bool[8];

    private static double2 _lastCursor;
    private static bool _haveCursor;
    private static bool _captured;

    public static float2 MouseDelta { get; private set; }
    public static float Scroll { get; private set; }
    public static float2 MousePosition { get; private set; }

    private static float2 _accumulatedDelta;
    private static float _accumulatedScroll;

    public static void Attach(GlfwWindow window)
    {
        window.OnKey += (_, key, _, action, _) =>
        {
            if (action == GlfwKeyAction.Press)
            {
                Held.Add(key);
                Down.Add(key);
            }
            else if (action == GlfwKeyAction.Release)
            {
                Held.Remove(key);
                Up.Add(key);
            }
        };

        window.OnMouseButton += (_, button, action, _) =>
        {
            var index = (int)button;
            if (index is < 0 or >= 8)
                return;

            if (action == GlfwButtonAction.Press)
            {
                Buttons[index] = true;
                ButtonsDown[index] = true;
            }
            else
            {
                Buttons[index] = false;
                ButtonsUp[index] = true;
            }
        };

        window.OnCursorPos += (_, pos) =>
        {
            if (_haveCursor)
                _accumulatedDelta += new float2((float)(pos.X - _lastCursor.X), (float)(pos.Y - _lastCursor.Y));

            _lastCursor = pos;
            _haveCursor = true;
            if (!_captured)
                MousePosition = new float2((float)pos.X, (float)pos.Y);
        };

        window.OnScroll += (_, offset) => _accumulatedScroll += (float)offset.Y;
        window.OnChar += (_, codepoint) => RailWerkz.UI.Keyboard.Feed(codepoint);

        window.OnFocus += (_, focus) =>
        {
            if (focus != GlfwFocusEvent.GainFocus)
                ReleaseAll();
        };
    }

    /// When the window takes or gives back the cursor. While it's taken the position reported runs on without limit, so MousePosition stays
    /// where the pointer was; and giving it back puts the pointer there again, a jump that isn't movement, so that isn't counted.
    public static void SetCaptured(bool captured)
    {
        _captured = captured;
        _haveCursor = false;
        _accumulatedDelta = default;
    }

    /// Call after polling events, before the frame reads input.
    public static void BeginFrame()
    {
        MouseDelta = _accumulatedDelta;
        Scroll = _accumulatedScroll;
        _accumulatedDelta = default;
        _accumulatedScroll = 0f;
    }

    /// Call at the end of the frame: this frame's presses and releases are forgotten.
    public static void EndFrame()
    {
        Down.Clear();
        Up.Clear();
        Array.Clear(ButtonsDown);
        Array.Clear(ButtonsUp);
        RailWerkz.UI.Keyboard.EndFrame();
    }

    public static void ReleaseAll()
    {
        foreach (var key in Held)
            Up.Add(key);

        Held.Clear();

        for (var i = 0; i < Buttons.Length; i++)
        {
            if (Buttons[i])
                ButtonsUp[i] = true;
            Buttons[i] = false;
        }
    }

    public static bool Key(GlfwKey key) => Held.Contains(key);
    public static bool KeyDown(GlfwKey key) => Down.Contains(key);
    public static bool KeyUp(GlfwKey key) => Up.Contains(key);
    public static bool Mouse(GlfwMouseButton button) => Buttons[(int)button];
    public static bool MouseDown(GlfwMouseButton button) => ButtonsDown[(int)button];
    public static bool MouseUp(GlfwMouseButton button) => ButtonsUp[(int)button];
}
