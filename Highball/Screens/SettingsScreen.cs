using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Settings, in tabs: Display, Graphics, Lighting, Audio, Simulation. Q/E or a click switches tab; every change applies at
/// once and is saved on the way out. Opened from over a world, that world keeps drawing behind it, so changes can be seen.
public sealed class SettingsScreen : MenuScreen, IWorldScreen
{
    private readonly Screen? _under;
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly (string Name, Menu Menu)[] _tabs;
    private int _tab;
    private double _tabChangedAt;
    private int _tabDirection = 1;

    private static readonly string[] Qualities = ["LOW", "MEDIUM", "HIGH", "ULTRA"];
    private static readonly string[] TerrainDetails = ["FULL", "HALF", "QUARTER"];
    private static readonly float[] Scales = [0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.5f];

    public override WorldView View => _under?.View ?? WorldView.None;

    /// under: the screen it was opened over, if any. The place is where the sun's worked out for: Barstow, for no route of its own.
    public SettingsScreen(Screen? under = null, double latitude = 34.9, double longitude = -117.09) : base("SETTINGS")
    {
        _under = under;
        _latitude = latitude;
        _longitude = longitude;
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
            .Add(new MenuItem { Label = "Track textures", Value = () => s.TrackTextureSize >= 2048 ? "2K" : "1K", OnChange = _ => s.TrackTextureSize = s.TrackTextureSize >= 2048 ? 1024 : 2048 })
            .Add(new MenuItem { Label = "Terrain textures", Value = () => TerrainDetails[Math.Clamp(s.TerrainTextureDetail, 0, 2)], OnChange = d => s.TerrainTextureDetail = (Math.Clamp(s.TerrainTextureDetail, 0, 2) + d + 3) % 3 })
            .Add(new MenuItem { Label = "Anti-aliasing (MSAA)", Value = () => s.Msaa <= 1 ? "OFF" : $"{s.Msaa}x", OnChange = d => s.Msaa = StepMsaa(s.Msaa, d) })
            .Add(new MenuItem { Label = "View distance", Value = () => Distance(s.ViewDistance), Meter = () => (s.ViewDistance - MetersPerMile) / (20000f - MetersPerMile), OnChange = d => s.ViewDistance = StepDistance(s.ViewDistance, d) })
            .Add(new MenuItem { Label = "Field of view", Value = () => $"{s.FieldOfView:0}°", Meter = () => (s.FieldOfView - 40f) / 50f, OnChange = d => s.FieldOfView = Math.Clamp(s.FieldOfView + d * 5f, 40f, 90f), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Temporal anti-aliasing", Value = () => OnOff(s.TemporalAA), OnChange = _ => s.TemporalAA = !s.TemporalAA })
            .Add(new MenuItem { Label = "Ambient occlusion", Value = () => OnOff(s.AmbientOcclusion), OnChange = _ => s.AmbientOcclusion = !s.AmbientOcclusion })
            .Add(new MenuItem { Label = "Sun shadows", Value = () => OnOff(s.SunShadows), OnChange = _ => s.SunShadows = !s.SunShadows })
            .Add(new MenuItem { Label = "Plants and rocks", Value = () => OnOff(s.Vegetation), OnChange = _ => s.Vegetation = !s.Vegetation })
            .Add(new MenuItem { Label = "Bloom", Value = () => OnOff(s.Bloom), OnChange = _ => s.Bloom = !s.Bloom })
            .Add(new MenuItem { Label = "God rays and glare", Value = () => OnOff(s.GodRays), OnChange = _ => s.GodRays = !s.GodRays })
            .Add(new MenuItem { Label = "Look", Value = () => ColorGrade.Find(s.ColorGrade).Label.ToUpperInvariant(), OnChange = d => s.ColorGrade = Next(ColorGrade.All, ColorGrade.Find(s.ColorGrade), d).Id });

        // A skybox brings its own sun, so the clock and the clouds are the procedural sky's alone.
        Func<bool> procedural = () => s.Skybox == Sky.Procedural;
        var lighting = NewTab("Lighting")
            .Add(new MenuItem { Label = "Sky", Value = () => Sky.Label(s.Skybox), OnChange = d => s.Skybox = Next(Sky.Available, s.Skybox, d), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Jump to", Value = PresetName, OnChange = StepPreset, Enabled = procedural })
            .Add(new MenuItem { Label = "Time", Value = () => Clock(s.SkyHour), Meter = () => s.SkyHour / 24f, OnChange = d => s.SkyHour = StepHour(s.SkyHour, d), Enabled = procedural })
            .Add(new MenuItem { Label = "Month", Value = () => $"{s.SkyMonth}", OnChange = d => { s.SkyMonth = (s.SkyMonth - 1 + d + 12) % 12 + 1; ClampDay(s); }, Enabled = procedural })
            .Add(new MenuItem { Label = "Day", Value = () => $"{s.SkyDay}", OnChange = d => { var days = DateTime.DaysInMonth(s.SkyYear, s.SkyMonth); s.SkyDay = (s.SkyDay - 1 + d + days) % days + 1; }, Enabled = procedural })
            .Add(new MenuItem { Label = "Year", Value = () => $"{s.SkyYear}", OnChange = d => { s.SkyYear = Math.Clamp(s.SkyYear + d, 1900, 2100); ClampDay(s); }, Enabled = procedural })
            .Add(new MenuItem { Label = "Sun", Value = SunNow, Enabled = () => false })
            .Add(new MenuItem { Label = "Sunrise and sunset", Value = SunTimes, Enabled = () => false, SeparatorAfter = true })
            .Add(new MenuItem { Label = "Clouds", Value = () => CloudType.Find(s.CloudType).Label.Split(" (")[0].ToUpperInvariant(), OnChange = d => s.CloudType = Next(CloudType.All, CloudType.Find(s.CloudType), d).Id, Enabled = procedural })
            .Add(new MenuItem { Label = "Cloud amount", Value = () => $"{s.CloudCoverage * 100f:0}", Meter = () => s.CloudCoverage, OnChange = d => s.CloudCoverage = MathF.Round(Math.Clamp(s.CloudCoverage + d * 0.05f, 0f, 1f), 2), Enabled = procedural })
            .Add(new MenuItem { Label = "Weather", Value = () => Array.Find(Weather.Kinds, k => k.Id == s.Weather).Label?.ToUpperInvariant() ?? "DRY", OnChange = d => StepWeather(s, d) })
            .Add(new MenuItem { Label = "Weather strength", Value = () => $"{s.WeatherAmount * 100f:0}", Meter = () => s.WeatherAmount, OnChange = d => s.WeatherAmount = MathF.Round(Math.Clamp(s.WeatherAmount + d * 0.05f, 0f, 1f), 2), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Auto exposure", Value = () => OnOff(s.AutoExposure), OnChange = _ => s.AutoExposure = !s.AutoExposure })
            .Add(new MenuItem { Label = "Exposure", Value = () => $"{s.Exposure:0.00}", Meter = () => (s.Exposure - 0.1f) / 3.9f, OnChange = d => s.Exposure = MathF.Round(Math.Clamp(s.Exposure + d * 0.05f, 0.1f, 4f), 2) });

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

        _tabs = [("DISPLAY", display), ("GRAPHICS", graphics), ("LIGHTING", lighting), ("AUDIO", audio), ("SIMULATION", simulation)];
    }

    private const float RowWidth = 680f;

    private Menu NewTab(string name) => new(name) { Look = MenuLook.Rows, RowWidth = RowWidth, OnBack = Back, ShowHeader = false };

    /// The next sample count the GPU can do.
    private static int StepMsaa(int value, int direction)
    {
        var steps = ((int[])[1, 2, 4, 8]).Where(RailWerkz.Vulkan.RenderTarget.Supported).ToArray();
        var at = Math.Max(Array.IndexOf(steps, value), 0);
        return steps[(at + direction + steps.Length) % steps.Length];
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static T Next<T>(IReadOnlyList<T> all, T current, int direction)
    {
        var at = 0;
        for (var i = 0; i < all.Count; i++)
            if (EqualityComparer<T>.Default.Equals(all[i], current))
                at = i;
        return all[(at + direction + all.Count) % all.Count];
    }

    private static string Clock(double hour) => LightingPanel.Clock(hour);

    /// To the quarter hour, round the clock.
    private static float StepHour(float hour, int direction) => ((MathF.Round(hour * 4f) + direction + 96f) % 96f) / 4f;

    private static void ClampDay(Settings s) => s.SkyDay = Math.Clamp(s.SkyDay, 1, DateTime.DaysInMonth(s.SkyYear, s.SkyMonth));

    private static void StepWeather(Settings s, int direction)
    {
        var kind = Next(Weather.Kinds, Array.Find(Weather.Kinds, k => k.Id == s.Weather), direction);
        s.Weather = kind.Id;
        if (kind.Clouds is not null)
            s.CloudType = kind.Clouds;
    }

    private SunPosition Sun()
    {
        var s = Settings.Current;
        return SunPosition.At(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
    }

    private string SunNow()
    {
        var sun = Sun();
        return $"{sun.Elevation:0}° UP, BEARING {sun.Azimuth:0}°";
    }

    private string SunTimes()
    {
        var sun = Sun();
        return double.IsNaN(sun.Sunrise) ? (sun.Elevation > 0f ? "NO SUNSET" : "NO SUNRISE") : $"{Clock(sun.Sunrise)} - {Clock(sun.Sunset)}";
    }

    /// Which of the named times the clock is at, if any.
    private int PresetAt()
    {
        var hour = Settings.Current.SkyHour;
        return Array.FindIndex(LightingPanel.Presets, p => MathF.Abs((float)LightingPanel.PresetHour(p.Id, _latitude, _longitude) - hour) < 0.01f);
    }

    private string PresetName()
    {
        var at = PresetAt();
        return at < 0 ? "CUSTOM" : LightingPanel.Presets[at].Label.ToUpperInvariant();
    }

    private void StepPreset(int direction)
    {
        var count = LightingPanel.Presets.Length;
        var at = PresetAt();
        var next = at < 0 ? (direction > 0 ? 0 : count - 1) : (at + direction + count) % count;
        Settings.Current.SkyHour = (float)LightingPanel.PresetHour(LightingPanel.Presets[next].Id, _latitude, _longitude);
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height) => (_under as IWorldScreen)?.RenderWorld(cmd, width, height);

    public void CompositeWorld(CommandBuffer cmd) => (_under as IWorldScreen)?.CompositeWorld(cmd);

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

        // Over something else, whatever's playing carries on.
        if (_under is null)
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
        _under?.UpdateBehind();

        if (Keyboard.Pressed(ImGuiKey.Q) || Keyboard.Pressed(ImGuiKey.PageUp))
            SwitchTab(_tab - 1);
        if (Keyboard.Pressed(ImGuiKey.E) || Keyboard.Pressed(ImGuiKey.PageDown) || Keyboard.Pressed(ImGuiKey.Tab))
            SwitchTab(_tab + 1);

        Tab.Update();
    }

    protected override (string Key, string Action)[] HintKeys() => [("Q / E", "Tab"), ("← →", "Change"), ("ESC", "Back")];

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        var left = S(110f);
        var top = MenuTop(size);

        // Over a world, a shade to read by that leaves most of the picture to judge changes on.
        if (View == WorldView.World)
        {
            var edge = left + S(RowWidth) + S(120f);
            draw.AddRectFilled(new float2(0f, 0f), new float2(edge, size.Y), Style.Background.WithAlpha(0.82f));
            Canvas.HGradient(draw, new float2(edge, 0f), new float2(edge + S(160f), size.Y), Style.Background.WithAlpha(0.82f), Style.Background.WithAlpha(0f));
        }

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

            var color = Tween.Color("settings.tab", name, selected ? Style.Text : hovered ? Rgba.Mix(TextDim, Style.Text, 0.5f) : TextDim, 0.15f);
            Canvas.Text(draw, Bold, S(17f), new float2(x, tabY), color, name);

            if (selected)
            {
                underlineX = x;
                underlineW = textSize.X;
            }

            x += textSize.X + S(36f);
        }

        var ux = Tween.Spring("settings", "underlineX", underlineX, 420f, 36f);
        var uw = Tween.Spring("settings", "underlineW", underlineW, 420f, 36f);
        var lineY = tabY + S(30f);
        draw.AddRectFilled(new float2(left, lineY + S(1f)), new float2(left + S(RowWidth), lineY + S(2f)), Stroke);
        draw.AddRectFilled(new float2(ux, lineY), new float2(ux + uw, lineY + S(3f)), Accent);

        // The tab's rows, sliding in from the side the tab came from.
        var slide = (1f - Tween.Progress(_tabChangedAt, 0.35f, Ease.OutQuint)) * S(40f) * _tabDirection;
        Tab.MaxListHeight = size.Y - S(100f) - (lineY + S(40f));
        Tab.Draw(draw, left + slide, lineY + S(40f));

        Canvas.Hints(draw, new float2(S(110f), size.Y - S(40f)), HintKeys());
    }
}
