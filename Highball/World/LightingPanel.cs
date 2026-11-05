using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Settings;

namespace Highball.World;

/// The sky, the time of day and the exposure for a world renderer, as a window. The date and time are kept in the
/// settings, and the sun comes from them and the route's place on Earth. A skybox brings its own sun, so they only apply to
/// the procedural sky.
public sealed class LightingPanel
{
    private readonly WorldRenderer _renderer;
    private readonly double _latitude;
    private readonly double _longitude;
    private (string Sky, int Version, int Year, int Month, int Day, float Hour) _applied = ("", -1, 0, 0, 0, 0f);

    public bool Open;

    /// The most auto exposure opens up: it should still look like night at night.
    private const float MaxAdaptation = 4f;

    /// Barstow, for a scene with no route of its own.
    public LightingPanel(WorldRenderer renderer, double latitude = 34.9, double longitude = -117.09)
    {
        _renderer = renderer;
        _latitude = latitude;
        _longitude = longitude;

        if (DevTools.CloudCoverage is { } coverage)
        {
            DevTools.CloudCoverage = null;
            Settings.Current.CloudCoverage = coverage;
        }

        if (DevTools.SkyTime is { } time)
        {
            DevTools.SkyTime = null;
            (Settings.Current.SkyYear, Settings.Current.SkyMonth, Settings.Current.SkyDay, Settings.Current.SkyHour) = (time.Year, time.Month, time.Day, time.Hour);
        }
    }

    private SunPosition Sun()
    {
        var s = Settings.Current;
        return SunPosition.At(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
    }

    /// Moves the clock to a time of the current day, by name: dawn, noon, golden, dusk or night.
    public void Preset(string name)
    {
        var sun = Sun();
        var rise = double.IsNaN(sun.Sunrise) ? sun.SolarNoon - 6.0 : sun.Sunrise;
        var set = double.IsNaN(sun.Sunset) ? sun.SolarNoon + 6.0 : sun.Sunset;
        var hour = name.ToLowerInvariant() switch
        {
            "dawn" => rise + 0.1,
            "noon" => sun.SolarNoon,
            "golden" => set - 0.75,
            "dusk" => set + 0.2,
            "night" => set + 3.0,
            _ => (double)Settings.Current.SkyHour,
        };
        Settings.Current.SkyHour = (float)((hour % 24.0 + 24.0) % 24.0);
    }

    /// Brings the sky and the exposure up to date. Call before the frame's world is recorded.
    public void Apply()
    {
        var s = Settings.Current;
        var sky = _renderer.Sky;
        _renderer.Clouds.Coverage = s.CloudCoverage;
        if (sky.Name == Sky.Procedural)
        {
            var key = (sky.Name, sky.Version, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
            if (key != _applied)
            {
                var sun = Sun();
                sky.SetSun(sun.Elevation, sun.Azimuth);
                _applied = key;
            }
        }

        // Partly, not all the way: dusk should still look darker than noon.
        var adaptation = s.AutoExposure ? MathF.Min(MathF.Pow(1f / MathF.Max(sky.Brightness, 1e-4f), 0.42f), MaxAdaptation) : 1f;
        _renderer.Exposure = s.Exposure * adaptation;
    }

    /// The window, if it's open, with anything else the screen wants in it.
    public void Draw(Action? extra = null)
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(90f, 44f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Lighting"u8, ref Open))
        {
            var s = Settings.Current;
            var sky = _renderer.Sky;

            if (ImGui.BeginCombo("Sky"u8, Sky.Label(sky.Name)))
            {
                foreach (var name in Sky.Available)
                {
                    if (ImGui.Selectable(Sky.Label(name), name == sky.Name))
                    {
                        sky.Use(name);
                        s.Skybox = name;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.Separator();
            if (sky.Name == Sky.Procedural)
                DrawClock(s);
            else
                ImGui.TextDisabled("A skybox has its own sun. Pick Procedural for time of day."u8);

            ImGui.Separator();
            var exposure = s.Exposure;
            if (ImGui.SliderFloat("Exposure"u8, ref exposure, 0.1f, 4f))
                s.Exposure = exposure;

            var auto = s.AutoExposure;
            if (ImGui.Checkbox("Auto exposure"u8, ref auto))
                s.AutoExposure = auto;

            var shadows = _renderer.Shadows;
            if (ImGui.Checkbox("Sun shadows"u8, ref shadows))
                _renderer.Shadows = shadows;

            extra?.Invoke();
        }

        ImGui.End();
    }

    private void DrawClock(Settings s)
    {
        foreach (var (label, id) in ((string, string)[])[("Dawn", "dawn"), ("Noon", "noon"), ("Golden hour", "golden"), ("Dusk", "dusk"), ("Night", "night")])
        {
            if (ImGui.Button(label))
                Preset(id);
            ImGui.SameLine();
        }

        ImGui.NewLine();

        var coverage = s.CloudCoverage;
        if (ImGui.SliderFloat("Clouds"u8, ref coverage, 0f, 1f))
            s.CloudCoverage = coverage;

        var hour = s.SkyHour;
        if (ImGui.SliderFloat("Time"u8, ref hour, 0f, 23.99f))
            s.SkyHour = hour;

        var month = s.SkyMonth;
        if (ImGui.SliderInt("Month"u8, ref month, 1, 12))
            s.SkyMonth = month;

        s.SkyDay = Math.Clamp(s.SkyDay, 1, DateTime.DaysInMonth(s.SkyYear, s.SkyMonth));
        var day = s.SkyDay;
        if (ImGui.SliderInt("Day"u8, ref day, 1, DateTime.DaysInMonth(s.SkyYear, s.SkyMonth)))
            s.SkyDay = day;

        var year = s.SkyYear;
        if (ImGui.SliderInt("Year"u8, ref year, 1900, 2100))
            s.SkyYear = year;

        var zone = SunPosition.TimeZone(_longitude);
        var sun = Sun();
        ImGui.Text($"{Clock(s.SkyHour)} on {s.SkyYear}-{s.SkyMonth:00}-{s.SkyDay:00}, UTC{zone:+0;-0;+0}");
        ImGui.Text($"Sun {sun.Elevation:0.0} degrees up, bearing {sun.Azimuth:0}");
        if (double.IsNaN(sun.Sunrise))
            ImGui.Text(sun.Elevation > 0f ? "No sunset today"u8 : "No sunrise today"u8);
        else
            ImGui.Text($"Sunrise {Clock(sun.Sunrise)}, noon {Clock(sun.SolarNoon)}, sunset {Clock(sun.Sunset)}");
    }

    private static string Clock(double hour)
    {
        var minutes = (int)Math.Round(((hour % 24.0 + 24.0) % 24.0) * 60.0) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
