using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Settings;

namespace Highball.World;

/// Puts the Settings screen's sky, time of day and exposure onto a world renderer. The sun comes from the date and time and the
/// route's place on Earth. A skybox brings its own sun, so they only apply to the procedural sky.
public sealed class LightingPanel
{
    private readonly WorldRenderer _renderer;
    private readonly double _latitude;
    private readonly double _longitude;
    private (string Sky, int Version, int Year, int Month, int Day, float Hour, float Overcast) _applied = ("", -1, 0, 0, 0, 0f, 0f);

    private string _skySetting = Settings.Current.Skybox;

    public bool Open;

    /// The named times of a day.
    public static readonly (string Id, string Label)[] Presets = [("dawn", "Dawn"), ("noon", "Noon"), ("golden", "Golden hour"), ("dusk", "Dusk"), ("night", "Night")];

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

        if (DevTools.Weather is { } weather)
        {
            DevTools.Weather = null;
            Settings.Current.Weather = weather;
        }

        if (DevTools.NoTemporalAA)
        {
            DevTools.NoTemporalAA = false;
            Settings.Current.TemporalAA = false;
        }

        if (DevTools.NoBloom)
        {
            DevTools.NoBloom = false;
            Settings.Current.Bloom = false;
        }

        if (DevTools.NoGodRays)
        {
            DevTools.NoGodRays = false;
            Settings.Current.GodRays = false;
        }

        if (DevTools.ColorGrade is { } grade)
        {
            DevTools.ColorGrade = null;
            Settings.Current.ColorGrade = grade;
        }

        if (DevTools.NoOcclusion)
        {
            DevTools.NoOcclusion = false;
            RailWerkz.Core.Settings.Settings.Current.AmbientOcclusion = false;
        }

        if (DevTools.NoVegetation)
        {
            DevTools.NoVegetation = false;
            Settings.Current.Vegetation = false;
        }

        if (DevTools.CloudType is { } type)
        {
            DevTools.CloudType = null;
            Settings.Current.CloudType = type;
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
    public void Preset(string name) => Settings.Current.SkyHour = (float)PresetHour(name, _latitude, _longitude);

    /// When that named time is on the current day, there.
    public static double PresetHour(string name, double latitude, double longitude)
    {
        var s = Settings.Current;
        var sun = SunPosition.At(latitude, longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
        var rise = double.IsNaN(sun.Sunrise) ? sun.SolarNoon - 6.0 : sun.Sunrise;
        var set = double.IsNaN(sun.Sunset) ? sun.SolarNoon + 6.0 : sun.Sunset;
        var hour = name.ToLowerInvariant() switch
        {
            "dawn" => rise + 0.1,
            "noon" => sun.SolarNoon,
            "golden" => set - 0.75,
            "dusk" => set + 0.2,
            "night" => set + 3.0,
            _ => (double)s.SkyHour,
        };
        return (hour % 24.0 + 24.0) % 24.0;
    }

    /// Brings the sky and the exposure up to date. Call before the frame's world is recorded.
    public void Apply()
    {
        var s = Settings.Current;
        var sky = _renderer.Sky;
        if (s.Skybox != _skySetting)
        {
            _skySetting = s.Skybox;
            sky.Use(s.Skybox);
        }

        _renderer.Shadows = s.SunShadows && !DevTools.NoShadows;
        var type = CloudType.Find(s.CloudType);
        _renderer.Clouds.Type = type;
        _renderer.Clouds.Amount = s.CloudCoverage;
        if (sky.Name == Sky.Procedural)
        {
            var overcast = type.OvercastAt(s.CloudCoverage);
            var key = (sky.Name, sky.Version, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour, overcast);
            if (key != _applied)
            {
                var sun = Sun();
                sky.SetSun(sun.Elevation, sun.Azimuth,
                    MoonPosition.At(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour),
                    MoonPosition.StarTurn(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour),
                    overcast);
                _applied = key;
            }
        }

        _renderer.Exposure = s.Exposure;
        _renderer.MeasureExposure = s.AutoExposure;
    }

    /// The test scene's window: the named times, where the sun is, and anything else the screen wants in it. The rest is in Settings.
    public void Draw(Action? extra = null)
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(90f, 44f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Lighting"u8, ref Open))
        {
            if (_renderer.Sky.Name == Sky.Procedural)
                DrawClock(Settings.Current);
            else
                ImGui.TextDisabled("A skybox has its own sun. Pick Procedural in Settings for time of day."u8);

            extra?.Invoke();
        }

        ImGui.End();
    }

    private void DrawClock(Settings s)
    {
        foreach (var (id, label) in Presets)
        {
            if (ImGui.Button(label))
                Preset(id);
            ImGui.SameLine();
        }

        ImGui.NewLine();

        var zone = SunPosition.TimeZone(_longitude);
        var sun = Sun();
        ImGui.Text($"{Clock(s.SkyHour)} on {s.SkyYear}-{s.SkyMonth:00}-{s.SkyDay:00}, UTC{zone:+0;-0;+0}");
        ImGui.Text($"Sun {sun.Elevation:0.0} degrees up, bearing {sun.Azimuth:0}");
        if (double.IsNaN(sun.Sunrise))
            ImGui.Text(sun.Elevation > 0f ? "No sunset today"u8 : "No sunrise today"u8);
        else
            ImGui.Text($"Sunrise {Clock(sun.Sunrise)}, noon {Clock(sun.SolarNoon)}, sunset {Clock(sun.Sunset)}");
    }

    public static string Clock(double hour)
    {
        var minutes = (int)Math.Round(((hour % 24.0 + 24.0) % 24.0) * 60.0) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
