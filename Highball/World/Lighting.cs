using Highball.App;
using RailWerkz.Core.Settings;

namespace Highball.World;

/// Puts the Settings screen's sky, time of day and exposure onto a world renderer. The sun comes from the date and time and the
/// route's place on Earth. A skybox brings its own sun, so they only apply to the procedural sky.
public sealed class Lighting
{
    private readonly WorldRenderer _renderer;
    private readonly double _latitude;
    private readonly double _longitude;
    /// How far true north is turned from the world's north (-z), degrees clockwise: the sun, moon and stars turn with it.
    private readonly double _gridNorth;
    private (string Sky, int Version, int Year, int Month, int Day, float Hour, float Overcast) _applied = ("", -1, 0, 0, 0, 0f, 0f);

    private string _skySetting = Settings.Current.Skybox;

    /// The named times of a day.
    public static readonly (string Id, string Label)[] Presets = [("dawn", "Dawn"), ("noon", "Noon"), ("golden", "Golden hour"), ("dusk", "Dusk"), ("night", "Night")];

    /// Barstow, for a scene with no route of its own.
    public Lighting(WorldRenderer renderer, double latitude = 34.9, double longitude = -117.09, double gridNorth = 0.0)
    {
        _renderer = renderer;
        _latitude = latitude;
        _longitude = longitude;
        _gridNorth = gridNorth;

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

        if (DevTools.TemporalAA)
        {
            DevTools.TemporalAA = false;
            Settings.Current.TemporalAA = true;
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

    /// The route's longitude, for its time zone.
    public double Longitude => _longitude;

    /// The sky has a time of day (the procedural one; a skybox brings its own sun).
    public bool HasClock => _renderer.Sky.Name == Sky.Procedural;

    /// Where the sun is at the Settings' date and time.
    public SunPosition Sun()
    {
        var s = Settings.Current;
        return SunPosition.At(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour);
    }

    /// The moon from true east and south to the world's, turned by _gridNorth.
    private MoonPosition ToGrid(MoonPosition moon)
    {
        var (sin, cos) = Math.SinCos(_gridNorth * Math.PI / 180.0);
        var (east, north) = ((double)moon.East, -(double)moon.South);
        return new MoonPosition((float)(east * cos + north * sin), moon.Up, (float)-(north * cos - east * sin));
    }

    /// The star turn's east and south columns swapped for the world's east and south, turned by _gridNorth.
    private float[] ToGrid(float[] turn)
    {
        var (sin, cos) = MathF.SinCos((float)(_gridNorth * Math.PI / 180.0));
        var result = (float[])turn.Clone();
        for (var k = 0; k < 3; k++)
        {
            var (east, south) = (turn[k], turn[6 + k]);
            result[k] = east * cos - south * sin;
            result[6 + k] = east * sin + south * cos;
        }

        return result;
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
                sky.SetSun(sun.Elevation, (float)(sun.Azimuth + _gridNorth),
                    ToGrid(MoonPosition.At(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour)),
                    ToGrid(MoonPosition.StarTurn(_latitude, _longitude, s.SkyYear, s.SkyMonth, s.SkyDay, s.SkyHour)),
                    overcast);
                _applied = key;
            }
        }

        _renderer.Exposure = s.Exposure;
        _renderer.MeasureExposure = s.AutoExposure;
    }

    public static string Clock(double hour)
    {
        var minutes = (int)Math.Round(((hour % 24.0 + 24.0) % 24.0) * 60.0) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
