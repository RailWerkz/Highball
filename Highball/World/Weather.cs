using RailWerkz.Core.Settings;
using RailWerkz.Core.Time;

namespace Highball.World;

/// What's falling, and what it's left on the ground: the ground takes a while to get wet and longer to dry, and snow builds up and melts.
/// A change made once the game's running plays out over time; what the settings start with is already so.
public sealed class Weather
{
    public static readonly (string Id, string Label, string? Clouds)[] Kinds =
    [
        ("Clear", "Dry", null),
        ("Rain", "Rain", "Nimbostratus"),
        ("Snow", "Snow", "Nimbostratus"),
    ];

    private bool _settled;

    /// 0 to 1: how wet the ground is.
    public float Wetness { get; private set; }

    /// 0 to 1: how much snow lies.
    public float SnowCover { get; private set; }

    /// 0 to 1: how hard it's raining and snowing now.
    public float Raining { get; private set; }
    public float Snowing { get; private set; }

    /// How much thicker the air is, per metre.
    public float ExtraHaze { get; private set; }

    /// Seconds since the game started, wrapped so the falling stays precise.
    public float FallTime { get; private set; }

    public void Update()
    {
        var s = Settings.Current;
        var amount = Math.Clamp(s.WeatherAmount, 0f, 1f);
        var rain = s.Weather == "Rain" ? amount : 0f;
        var snow = s.Weather == "Snow" ? amount : 0f;
        var dt = Time.RealDelta;
        FallTime = (float)(Time.Now % 7200.0);

        if (!_settled)
        {
            _settled = true;
            Wetness = rain > 0f ? MathF.Min(0.4f + rain, 1f) : 0f;
            SnowCover = snow > 0f ? MathF.Min(0.4f + snow, 1f) : 0f;
            (Raining, Snowing) = (rain, snow);
        }

        static float Toward(float value, float target, float rate) => value + Math.Clamp(target - value, -rate, rate);

        Raining = Toward(Raining, rain, dt * 0.2f);
        Snowing = Toward(Snowing, snow, dt * 0.2f);
        var wetTarget = rain > 0f ? MathF.Min(0.4f + rain, 1f) : 0f;
        Wetness = Toward(Wetness, wetTarget, dt * (wetTarget > Wetness ? 1f / 60f : 1f / 400f));
        var snowTarget = snow > 0f ? MathF.Min(0.4f + snow, 1f) : 0f;
        SnowCover = Toward(SnowCover, snowTarget, dt * (snowTarget > SnowCover ? 1f / 180f : 1f / 500f));
        ExtraHaze = Raining / 9000f + Snowing / 3500f;
    }
}
