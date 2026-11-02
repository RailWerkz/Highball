using System.Diagnostics;

namespace RailWerkz.Core.Time;

/// The frame clock. Game code reads DeltaTime (scaled, 0 while paused); interface code reads RealDelta.
public static class Time
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static double _last;

    /// Seconds since the last frame, scaled and clamped so a hitch can't tunnel rounds through the ground.
    public static float DeltaTime { get; private set; }

    /// Unscaled seconds since the last frame.
    public static float RealDelta { get; private set; }

    /// Seconds since start, unscaled.
    public static double Now => Watch.Elapsed.TotalSeconds;

    /// Seconds of game time (scaled).
    public static double GameTime { get; private set; }

    public static long Frame { get; private set; }

    /// 0 pauses the game; interface animation keeps running on RealDelta.
    public static float Scale { get; set; } = 1f;

    public static void Tick()
    {
        var now = Watch.Elapsed.TotalSeconds;
        RealDelta = (float)Math.Clamp(now - _last, 0.0, 0.1);
        DeltaTime = RealDelta * Scale;
        GameTime += DeltaTime;
        _last = now;
        Frame++;
    }
}
