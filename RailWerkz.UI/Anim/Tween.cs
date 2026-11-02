namespace RailWerkz.UI.Anim;

/// What a running tween does when its target changes before it gets there.
public enum Policy
{
    /// Start again from where it is now, towards the new target (the smooth default).
    Crossfade,
    /// Jump straight to the new target.
    Cut,
    /// Finish the current run, then head for the newest target.
    Queue,
}

/// Immediate-mode tweens, after ImAnim: ask for a value with an id, a channel and a target every frame, and get back
/// where it is now. State lives in a table keyed by (id, channel) and is dropped when nobody has asked for a while,
/// so callers keep nothing. Call BeginFrame once a frame, after ImGui.NewFrame.
///
///     var lift = Tween.Float(id, "lift", hovered ? 1f : 0f, 0.18f, Ease.OutCubic);
public static class Tween
{
    private struct Track
    {
        public float4 From;
        public float4 To;
        public float4 Value;
        public float4 Queued;
        public bool HasQueued;
        public float Elapsed;
        public float Duration;
        public Ease Ease;
        public double TouchedAt;
        public bool Color;
    }

    private struct SpringState
    {
        public float4 Value;
        public float4 Velocity;
        public double TouchedAt;
    }

    private static readonly Dictionary<ulong, Track> Tracks = [];
    private static readonly Dictionary<ulong, SpringState> Springs = [];
    private static readonly Dictionary<ulong, (double At, double TouchedAt)> Triggers = [];
    private static readonly List<ulong> Stale = [];
    private static double _now;
    private static float _dt;
    private static double _lastSweep;

    /// Seconds since the program started, on the interface clock.
    public static double Now => _now;

    /// This frame's interface delta.
    public static float DeltaTime => _dt;

    /// Accessibility: every tween arrives at once and springs settle immediately.
    public static bool ReduceMotion { get; set; }

    /// Advances the clock and, every couple of seconds, forgets tracks that went unasked.
    public static void BeginFrame(float dt)
    {
        _dt = Math.Clamp(dt, 0f, 0.1f);
        _now += _dt;

        if (_now - _lastSweep < 2.0)
            return;

        _lastSweep = _now;
        Sweep(Tracks, t => t.TouchedAt);
        Sweep(Springs, s => s.TouchedAt);
        Sweep(Triggers, t => t.TouchedAt);
    }

    private static void Sweep<T>(Dictionary<ulong, T> table, Func<T, double> touched)
    {
        Stale.Clear();
        foreach (var (key, value) in table)
        {
            if (_now - touched(value) > 3.0)
                Stale.Add(key);
        }

        foreach (var key in Stale)
            table.Remove(key);
    }

    /// FNV-1a over the id and the channel, without allocating.
    public static ulong Key(ReadOnlySpan<char> id, ReadOnlySpan<char> channel)
    {
        var hash = 14695981039346656037ul;
        foreach (var c in id)
            hash = (hash ^ c) * 1099511628211ul;
        hash = (hash ^ '#') * 1099511628211ul;
        foreach (var c in channel)
            hash = (hash ^ c) * 1099511628211ul;
        return hash;
    }

    /// The same with a number mixed in (an item's index in a list).
    public static ulong Key(ReadOnlySpan<char> id, ReadOnlySpan<char> channel, int index) =>
        (Key(id, channel) ^ (uint)index) * 1099511628211ul;

    private static float4 Step(ulong key, float4 target, float duration, Ease ease, Policy policy, float4? initial, bool color)
    {
        if (!Tracks.TryGetValue(key, out var track))
        {
            var start = initial ?? target;
            track = new Track { From = start, To = target, Value = start, Duration = duration, Ease = ease, Color = color };
        }

        track.TouchedAt = _now;

        if (!Same(target, track.To))
        {
            switch (policy)
            {
                case Policy.Cut:
                    track.From = track.To = track.Value = target;
                    track.Elapsed = track.Duration;
                    break;
                case Policy.Queue when track.Elapsed < track.Duration:
                    track.Queued = target;
                    track.HasQueued = true;
                    break;
                default:
                    track.From = track.Value;
                    track.To = target;
                    track.Elapsed = 0f;
                    track.Duration = duration;
                    track.Ease = ease;
                    break;
            }
        }

        if (ReduceMotion || track.Duration <= 0f)
        {
            track.Elapsed = track.Duration;
        }
        else
        {
            track.Elapsed = MathF.Min(track.Duration, track.Elapsed + _dt);
        }

        var k = track.Duration <= 0f ? 1f : Easing.Apply(track.Ease, track.Elapsed / track.Duration);
        track.Value = track.Color ? MixColor(track.From, track.To, k) : track.From + (track.To - track.From) * k;

        if (track.Elapsed >= track.Duration && track.HasQueued)
        {
            track.HasQueued = false;
            track.From = track.Value;
            track.To = track.Queued;
            track.Elapsed = 0f;
        }

        Tracks[key] = track;
        return track.Value;
    }

    private static bool Same(float4 a, float4 b) =>
        MathF.Abs(a.X - b.X) < 1e-5f && MathF.Abs(a.Y - b.Y) < 1e-5f && MathF.Abs(a.Z - b.Z) < 1e-5f && MathF.Abs(a.W - b.W) < 1e-5f;

    private static float4 MixColor(float4 a, float4 b, float k) =>
        Rgba.Mix(new Rgba(a.X, a.Y, a.Z, a.W), new Rgba(b.X, b.Y, b.Z, b.W), k).Float4;

    public static float Float(string id, string channel, float target, float duration = 0.25f, Ease ease = Ease.OutCubic,
        Policy policy = Policy.Crossfade, float? initial = null) =>
        Step(Key(id, channel), new float4(target, 0f, 0f, 0f), duration, ease, policy, initial is { } i ? new float4(i, 0f, 0f, 0f) : null, false).X;

    public static float Float(ulong key, float target, float duration = 0.25f, Ease ease = Ease.OutCubic,
        Policy policy = Policy.Crossfade, float? initial = null) =>
        Step(key, new float4(target, 0f, 0f, 0f), duration, ease, policy, initial is { } i ? new float4(i, 0f, 0f, 0f) : null, false).X;

    public static float2 Vec2(string id, string channel, float2 target, float duration = 0.25f, Ease ease = Ease.OutCubic,
        Policy policy = Policy.Crossfade, float2? initial = null)
    {
        var v = Step(Key(id, channel), new float4(target.X, target.Y, 0f, 0f), duration, ease, policy,
            initial is { } i ? new float4(i.X, i.Y, 0f, 0f) : null, false);
        return new float2(v.X, v.Y);
    }

    /// A colour, blended perceptually.
    public static Rgba Color(string id, string channel, Rgba target, float duration = 0.25f, Ease ease = Ease.OutCubic,
        Policy policy = Policy.Crossfade, Rgba? initial = null)
    {
        var v = Step(Key(id, channel), target.Float4, duration, ease, policy, initial?.Float4, true);
        return new Rgba(v.X, v.Y, v.Z, v.W);
    }

    /// A damped spring towards target: no fixed duration, it keeps its velocity when the target moves, which is what
    /// makes a selection bar feel physical. Stiffness ~ how fast, damping ~ how little it wobbles (critical ≈
    /// 2·√stiffness).
    public static float Spring(ulong key, float target, float stiffness = 260f, float damping = 26f)
    {
        if (!Springs.TryGetValue(key, out var spring))
            spring = new SpringState { Value = new float4(target, 0f, 0f, 0f) };

        spring.TouchedAt = _now;

        if (ReduceMotion)
        {
            spring.Value = new float4(target, 0f, 0f, 0f);
            spring.Velocity = default;
        }
        else
        {
            // Semi-implicit Euler in a few substeps, so a long frame can't blow it up.
            var steps = Math.Max(1, (int)MathF.Ceiling(_dt / (1f / 240f)));
            var h = _dt / steps;
            for (var i = 0; i < steps; i++)
            {
                var force = (target - spring.Value.X) * stiffness - spring.Velocity.X * damping;
                spring.Velocity.X += force * h;
                spring.Value.X += spring.Velocity.X * h;
            }
        }

        Springs[key] = spring;
        return spring.Value.X;
    }

    public static float Spring(string id, string channel, float target, float stiffness = 260f, float damping = 26f) =>
        Spring(Key(id, channel), target, stiffness, damping);

    /// Seconds since the last time fire was true for this key (or since first asked); a cheap way to run one-shot
    /// animations like a click pulse: var t = Tween.Since(id, "pulse", clicked).
    public static float Since(string id, string channel, bool fire = false)
    {
        var key = Key(id, channel);
        var at = !fire && Triggers.TryGetValue(key, out var trigger) ? trigger.At : _now;
        Triggers[key] = (at, _now);
        return (float)(_now - at);
    }

    /// Forgets one track, so it starts afresh next time (from its initial value).
    public static void Reset(string id, string channel)
    {
        var key = Key(id, channel);
        Tracks.Remove(key);
        Springs.Remove(key);
        Triggers.Remove(key);
    }

    /// Forgets everything: a new screen's entrances play again.
    public static void ResetAll()
    {
        Tracks.Clear();
        Springs.Clear();
        Triggers.Clear();
    }

    /// For list entrances: 0 until index·delay has passed since start, then eases to 1 over duration.
    public static float Stagger(double start, int index, float delay = 0.045f, float duration = 0.4f, Ease ease = Ease.OutCubic)
    {
        if (ReduceMotion)
            return 1f;

        var t = (float)(_now - start) - index * delay;
        return Easing.Apply(ease, t / duration);
    }

    /// One-shot progress: 0 at start, 1 after duration.
    public static float Progress(double start, float duration, Ease ease = Ease.OutCubic, float delay = 0f) =>
        ReduceMotion ? 1f : Easing.Apply(ease, ((float)(_now - start) - delay) / duration);
}
