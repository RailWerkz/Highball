using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Settings, in tabs: Display, Graphics, Audio, Simulation. Q/E or a click switches tab; every change applies at
/// once and is saved on the way out.
public sealed class OptionsScreen : MenuScreen
{
    private readonly (string Name, Menu Menu)[] _tabs;
    private int _tab;
    private double _tabChangedAt;
    private int _tabDirection = 1;

    private static readonly string[] Qualities = ["LOW", "MEDIUM", "HIGH", "ULTRA"];
    private static readonly float[] Scales = [0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.5f];

    public OptionsScreen() : base("OPTIONS")
    {
        var s = Settings.Current;
        var app = HighballApp.Instance;

        var display = NewTab("Display")
            .Add(new MenuItem { Label = "Fullscreen", Value = () => OnOff(s.Fullscreen), OnChange = _ => { s.Fullscreen = !s.Fullscreen; app.SetFullscreen(s.Fullscreen); } })
            .Add(new MenuItem { Label = "Vertical sync", Value = () => OnOff(s.VSync), OnChange = _ => { s.VSync = !s.VSync; app.ApplyVSync(); } })
            .Add(new MenuItem { Label = "Interface scale", Value = () => $"{s.UiScale * 100f:0}%", OnChange = d => s.UiScale = Step(Scales, s.UiScale, d) })
            .Add(new MenuItem { Label = "Accent", Value = () => Accents[FindAccent(s.Accent)].Name, OnChange = d => { s.Accent = Accents[(FindAccent(s.Accent) + d + Accents.Length) % Accents.Length].Name; app.ApplyTheme(); } })
            .Add(new MenuItem { Label = "Reduce motion", Value = () => OnOff(s.ReduceMotion), OnChange = _ => { s.ReduceMotion = !s.ReduceMotion; app.ApplyTheme(); } });

        var graphics = NewTab("Graphics")
            .Add(new MenuItem { Label = "Quality", Value = () => Qualities[Math.Clamp(s.Quality, 0, 3)], OnChange = d => s.Quality = Math.Clamp(s.Quality + d, 0, 3) })
            .Add(new MenuItem { Label = "View distance", Value = () => Distance(s.ViewDistance), Meter = () => (s.ViewDistance - MetersPerMile) / (20000f - MetersPerMile), OnChange = d => s.ViewDistance = StepDistance(s.ViewDistance, d) })
            .Add(new MenuItem { Label = "Field of view", Value = () => $"{s.FieldOfView:0}°", Meter = () => (s.FieldOfView - 40f) / 50f, OnChange = d => s.FieldOfView = Math.Clamp(s.FieldOfView + d * 5f, 40f, 90f) });

        var audio = NewTab("Audio")
            .Add(Volume("Master", () => s.MasterVolume, v => s.MasterVolume = v))
            .Add(Volume("Music", () => s.MusicVolume, v => s.MusicVolume = v))
            .Add(Volume("Effects", () => s.EffectsVolume, v => s.EffectsVolume = v))
            .Add(Volume("Voice", () => s.VoiceVolume, v => s.VoiceVolume = v, separatorAfter: true));

        // Each change loads the take and plays it.
        foreach (var slot in Sfx.Slots)
            audio.Add(new MenuItem { Label = slot.Label, Value = () => slot.Current, OnChange = slot.Cycle, Silent = true });

        var simulation = NewTab("Simulation")
            .Add(new MenuItem { Label = "Units", Value = () => s.Units == UnitSystem.Metric ? "METRIC" : "IMPERIAL", OnChange = _ => s.Units = s.Units == UnitSystem.Metric ? UnitSystem.Imperial : UnitSystem.Metric })
            .Add(new MenuItem { Label = "Assisted braking", Value = () => OnOff(s.AssistedBraking), OnChange = _ => s.AssistedBraking = !s.AssistedBraking })
            .Add(new MenuItem { Label = "Mouse sensitivity", Value = () => $"{s.Sensitivity:0.0}x", Meter = () => (s.Sensitivity - 0.2f) / 2.8f, OnChange = d => s.Sensitivity = MathF.Round(Math.Clamp(s.Sensitivity + d * 0.1f, 0.2f, 3f), 1) })
            .Add(new MenuItem { Label = "Invert look", Value = () => OnOff(s.InvertY), OnChange = _ => s.InvertY = !s.InvertY });

        _tabs = [("DISPLAY", display), ("GRAPHICS", graphics), ("AUDIO", audio), ("SIMULATION", simulation)];
    }

    private Menu NewTab(string name) => new(name) { Look = MenuLook.Rows, RowWidth = 680f, OnBack = Back, ShowHeader = false };

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static float Step(float[] steps, float value, int direction)
    {
        var index = Array.FindIndex(steps, v => MathF.Abs(v - value) < 0.01f);
        if (index < 0)
            index = Array.IndexOf(steps, 1f);
        return steps[Math.Clamp(index + direction, 0, steps.Length - 1)];
    }

    private static MenuItem Volume(string label, Func<float> get, Action<float> set, bool separatorAfter = false) => new()
    {
        Label = label,
        Value = () => $"{get() * 100f:0}",
        Meter = get,
        OnChange = d => set(MathF.Round(Math.Clamp(get() + d * 0.05f, 0f, 1f), 2)),
        SeparatorAfter = separatorAfter,
    };

    private const float MetersPerMile = 1609.344f;

    private static bool Imperial => Settings.Current.Units == UnitSystem.Imperial;

    private static string Distance(float meters) => Imperial ? $"{meters / MetersPerMile:0} MI" : $"{meters / 1000f:0} KM";

    /// Whole miles (1 to 12) or whole kilometres (2 to 20), in the player's units.
    private static float StepDistance(float meters, int direction) => Imperial
        ? Math.Clamp(MathF.Round(meters / MetersPerMile) + direction, 1f, 12f) * MetersPerMile
        : Math.Clamp(MathF.Round(meters / 1000f) + direction, 2f, 20f) * 1000f;

    private Menu Tab => _tabs[_tab].Menu;

    public override void Enter()
    {
        if (DevTools.Tab is { } tab)
        {
            _tab = Math.Max(0, Array.FindIndex(_tabs, t => t.Name.Equals(tab, StringComparison.OrdinalIgnoreCase)));
            DevTools.Tab = null;
        }

        base.Enter();
        _tabChangedAt = Tween.Now;
        Tab.Open();
    }

    public override void Resume()
    {
        base.Resume();
        Tab.Open();
    }

    protected override void Back()
    {
        Settings.Save();
        base.Back();
    }

    private void SwitchTab(int index)
    {
        index = (index + _tabs.Length) % _tabs.Length;
        if (index == _tab)
            return;

        _tabDirection = index > _tab ? 1 : -1;
        _tab = index;
        _tabChangedAt = Tween.Now;
        Tab.Open();
        Sfx.Hover();
    }

    public override void Update(float dt)
    {
        if (Keyboard.Pressed(ImGuiKey.Q) || Keyboard.Pressed(ImGuiKey.PageUp))
            SwitchTab(_tab - 1);
        if (Keyboard.Pressed(ImGuiKey.E) || Keyboard.Pressed(ImGuiKey.PageDown) || Keyboard.Pressed(ImGuiKey.Tab))
            SwitchTab(_tab + 1);

        Tab.Update();
    }

    protected override (string Key, string Action)[] HintKeys() => [("Q / E", "Tab"), ("← →", "Change"), ("ESC", "Back")];

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        DrawFrame(draw, size);
        var left = S(110f);
        var top = MenuTop(size);
        Canvas.Text(draw, Heavy, S(34f), new float2(left, top), Style.Text, Menu.Title);

        // The tab strip, its underline sprung between tabs.
        var tabY = top + S(70f);
        var x = left;
        var mouse = ImGui.GetIO().MousePos;
        var underlineX = 0f;
        var underlineW = 0f;

        for (var i = 0; i < _tabs.Length; i++)
        {
            var name = _tabs[i].Name;
            var textSize = Canvas.TextSize(Bold, S(17f), name);
            var selected = i == _tab;
            var hovered = !Keyboard.PointerBlocked && mouse.X >= x - S(6f) && mouse.X <= x + textSize.X + S(6f) && mouse.Y >= tabY - S(6f) && mouse.Y <= tabY + textSize.Y + S(14f);

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (Keyboard.Clicked())
                    SwitchTab(i);
            }

            var color = Tween.Color("options.tab", name, selected ? Style.Text : hovered ? Rgba.Mix(TextDim, Style.Text, 0.5f) : TextDim, 0.15f);
            Canvas.Text(draw, Bold, S(17f), new float2(x, tabY), color, name);

            if (selected)
            {
                underlineX = x;
                underlineW = textSize.X;
            }

            x += textSize.X + S(36f);
        }

        var ux = Tween.Spring("options", "underlineX", underlineX, 420f, 36f);
        var uw = Tween.Spring("options", "underlineW", underlineW, 420f, 36f);
        var lineY = tabY + S(30f);
        draw.AddRectFilled(new float2(left, lineY + S(1f)), new float2(left + S(680f), lineY + S(2f)), Stroke);
        draw.AddRectFilled(new float2(ux, lineY), new float2(ux + uw, lineY + S(3f)), Accent);

        // The tab's rows, sliding in from the side the tab came from.
        var slide = (1f - Tween.Progress(_tabChangedAt, 0.35f, Ease.OutQuint)) * S(40f) * _tabDirection;
        Tab.Draw(draw, left + slide, lineY + S(40f));

        Canvas.Hints(draw, new float2(S(110f), size.Y - S(40f)), HintKeys());
    }
}
