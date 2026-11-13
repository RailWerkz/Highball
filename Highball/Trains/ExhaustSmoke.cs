namespace Highball.Trains;

/// One piece of an exhaust plume as it's drawn: a capsule from one point the smoke left the stack at to the next, its
/// radius at each end (World/Smoke.cs marches the whole chain at once, as Kitten Space Agency's trails are). Density: how
/// thick it is at its heart, per metre; Soot: 0 for dark grey haze, 1 for pure black; Age: seconds since it left the stack;
/// Billow: 0 for exhaust frayed into wisps, 1 for the round billows of an engine catching.
public readonly record struct SmokeSegment(double AX, double AY, double AZ, float RadiusA, double BX, double BY, double BZ, float RadiusB,
    float Density, float Soot, float Age, float Billow);

/// Heat shimmering over a running engine's stack: its mouth in the world, how wide, how strong (0 to 1), and how far its
/// top's pushed over (metres, east and north in the world's x and z) by the air going past, as the smoke is.
public readonly record struct HazeSource(double X, double Y, double Z, float Width, float Strength, float LeanX = 0f, float LeanZ = 0f);

/// What a locomotive's exhaust is doing this frame, from its engine and its motion. Mouth: the stack's mouth in the world;
/// Across, Along: unit directions across and along the engine there; Velocity: the engine's, metres a second; Opening: the
/// mouth's size across and along; Load: how hard it's working, 0 to 1; Revs: 0 idle to 1 notch 8; Starting: seconds until
/// it's running (0 running); Running: turning under its own power.
public readonly record struct ExhaustState(
    (double X, double Y, double Z) Mouth, float3 Across, float3 Along, float3 Velocity, float2 Opening, float Load, float Revs, float Starting, bool Running);

/// The exhaust from every locomotive: smoke when there is any, and the heat haze over the stack.
///
/// A diesel smokes black when there's more fuel going in than air to burn it, and otherwise runs nearly clean. So here:
///  - starting, it coughs and then billows black as it catches, clearing over the next few seconds as it settles;
///  - opening the throttle, the governor's fuel is there at once but the turbo takes seconds to spool up and bring the air,
///    and until it does it smokes black (the GE's turbo-lag smoke), harder the further and faster it's opened from low revs;
///  - lugging in a high notch at a crawl, before the boost has built, it smokes a lighter grey-black;
///  - running steady, even in notch 8, there's only a thin haze and the shimmer of hot exhaust.
/// The smoke leaves as a stream of small overlapping puffs, each carrying its share of the plume's thickness so they run
/// together, spreading fast, frayed by the air and pushed about by gusts so no plume or moment is quite like another.
public sealed class ExhaustSmoke
{
    /// The most puffs at once, all engines together: the oldest go first beyond it.
    public const int MaxPuffs = 4000;

    private struct Puff
    {
        public double X, Y, Z;
        public float3 Velocity, Drift;
        public float Radius0, Growth, Density0, Soot, Seed, Age, Life, Lift, Billow;
        public int Chain;           // which engine's plume it's a point of, in the order they left
        public float Radius, Density;
    }

    private sealed class Emitter
    {
        public float Owed;          // puffs due but not yet put out
        public float Boost;         // the turbo's air, as a share of what full fuel needs, lagging the fuel
        public float SinceRunning = 99f;
        public float Heat;          // the haze, 0 to 1
        public float Clock;
        public float Phase;         // where this engine's gusts are, so engines differ
        public float LeanX, LeanZ;  // how far the haze's top is pushed over, settling towards the air's push
        public HazeSource Haze;
        public int Chain;
        public bool Seen;
    }

    private readonly List<Puff> _puffs = [];
    private readonly List<SmokeSegment> _drawn = [];
    private readonly Dictionary<int, int> _last = [];
    private int _chains;
    private readonly List<HazeSource> _haze = [];
    private readonly Dictionary<object, Emitter> _emitters = new(ReferenceEqualityComparer.Instance);
    private readonly Random _random = new(7);

    public IReadOnlyList<SmokeSegment> Segments => _drawn;
    public IReadOnlyList<HazeSource> Haze => _haze;

    /// Starts a frame: engines not given again this frame stop smoking.
    public void Begin()
    {
        foreach (var e in _emitters.Values)
            e.Seen = false;
    }

    /// One engine's exhaust this frame. Key: anything that stays the same for that engine (its car).
    public void Emit(object key, in ExhaustState state, float dt)
    {
        if (!_emitters.TryGetValue(key, out var e))
            _emitters[key] = e = new Emitter { Phase = (float)_random.NextDouble() * 100f, SinceRunning = state.Running ? 99f : 0f, Heat = state.Running ? 0.7f : 0f, Chain = ++_chains };
        e.Seen = true;
        if (dt <= 0f)
            return;
        e.Clock += dt;
        var speed = Length(state.Velocity);
        var revs = Math.Clamp(state.Revs, 0f, 1f);
        var load = Math.Clamp(state.Load, 0f, 1f);

        // The air round it isn't still: gusts that wander, different for each engine.
        var t = e.Clock + e.Phase;
        var gust = new float3(MathF.Sin(t * 0.37f) * 0.9f + MathF.Sin(t * 1.31f + 1.7f) * 0.45f, MathF.Sin(t * 0.83f + 0.4f) * 0.25f,
            MathF.Cos(t * 0.29f + 2.1f) * 0.9f + MathF.Sin(t * 1.07f + 0.9f) * 0.45f);

        // Heat: building as it fires and starts, steady while it runs (more, revved up), dying away slowly once it's stopped.
        // The hot air rises a couple of metres a second, and the air going past the stack (the gusts, less the engine's own
        // motion) pushes it over meanwhile, as it does the smoke: under way it lies back behind.
        var heat = state.Starting > 0f ? 0.8f : state.Running ? 0.6f + 0.4f * revs : 0f;
        e.Heat += (heat - e.Heat) * MathF.Min(1f, dt / (heat > e.Heat ? 3f : 25f));
        var (pushX, pushZ) = ((gust.X - state.Velocity.X) * 1.8f, (gust.Z - state.Velocity.Z) * 1.8f);
        var pushed = MathF.Sqrt(pushX * pushX + pushZ * pushZ);
        if (pushed > 6f)
            (pushX, pushZ) = (pushX * 6f / pushed, pushZ * 6f / pushed);
        var settle = MathF.Min(1f, dt / 0.8f);
        e.LeanX += (pushX - e.LeanX) * settle;
        e.LeanZ += (pushZ - e.LeanZ) * settle;
        var widest = MathF.Max(state.Opening.X, state.Opening.Y);
        e.Haze = new HazeSource(state.Mouth.X, state.Mouth.Y, state.Mouth.Z, widest, e.Heat, e.LeanX, e.LeanZ);

        // The fuel the governor's giving, and the air the turbo's bringing for it: the boost comes up behind the fuel over a
        // few seconds, and falls away faster. While fuel's ahead of air it smokes.
        var fuel = state.Running ? revs * (0.35f + 0.65f * load) : 0f;
        e.Boost += (fuel - e.Boost) * MathF.Min(1f, dt / (fuel > e.Boost ? 2.8f : 0.7f));
        var rich = Math.Clamp((fuel - e.Boost) * 3.5f, 0f, 1f);
        // Lugging: a high notch at a crawl or stalled (wheels slipping or not: slip cuts the traction, not the fuel the
        // governor's giving), the boost never builds against the load and it pours black.
        var lugging = revs * revs * Math.Clamp(1f - speed / 10f, 0f, 1f) * (0.75f + 0.25f * load);
        e.SinceRunning = state.Running ? e.SinceRunning + dt : 0f;
        var catching = (state.Running ? MathF.Exp(-e.SinceRunning / 3.5f) : state.Starting is > 0f and < 4f ? 1f : 0f) * 0.6f;
        var black = MathF.Max(MathF.Max(rich, lugging), catching);

        // Clean running: next to nothing, a little more working hard.
        var clean = state.Running ? 0.05f + 0.1f * revs * load : 0f;
        var plume = (clean + 16f * MathF.Pow(black, 1.1f)) / (1f + speed / 14f);
        if (plume < 0.03f)
        {
            e.Owed = 0f;
            return;
        }

        var soot = Math.Clamp(0.3f + 0.7f * black / MathF.Max(black + clean, 1e-3f), 0f, 1f);
        var billow = MathF.Max(MathF.Min(1f, catching / 0.6f), 0.35f * rich);
        var exit = 1.5f + 3f * revs + 4f * black;
        var lift = 0.3f + 1.2f * black + 0.4f * revs;
        // Short-lived: an engine's smoke thins and is gone in a few seconds, not a rocket's trail.
        var life = 1.6f + 2.8f * black;
        var growth = 0.6f + 0.9f * black + 0.03f * speed + 0.4f * billow;

        // Enough of them that each is well inside the one before as it leaves: the faster it goes (out of the stack, or past
        // in the air under way), the more there are.
        var away = MathF.Max(exit, speed);
        // The capsules join them, so they needn't be close: a lash-up of ten mustn't run out of room for its plumes.
        var rate = Math.Clamp(away * 2f, 8f, 25f);
        var half = state.Opening * 0.5f;
        var r0 = MathF.Max(0.28f, 0.45f * MathF.Max(half.X, half.Y)) * (1f + 0.8f * billow + 0.4f * black);
        e.Owed += rate * dt;
        var up = new float3(0f, 1f, 0f);
        while (e.Owed >= 1f)
        {
            e.Owed -= 1f;
            // Somewhere over the mouth, a little way up it already by the time this frame's done, so they're evenly spaced.
            var lag = (float)_random.NextDouble() * dt;
            var a = ((float)_random.NextDouble() * 2f - 1f) * half.X * 0.6f;
            var b = ((float)_random.NextDouble() * 2f - 1f) * half.Y * 0.6f;
            var start = state.Across * a + state.Along * b + up * (exit * lag + 0.05f);
            var jitter = new float3((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f) * 0.7f;
            if (_puffs.Count >= MaxPuffs)
                _puffs.RemoveAt(0);
            _puffs.Add(new Puff
            {
                X = state.Mouth.X + start.X, Y = state.Mouth.Y + start.Y, Z = state.Mouth.Z + start.Z,
                Velocity = state.Velocity + up * exit + jitter,
                Drift = gust,
                Radius0 = r0 * (0.8f + 0.4f * (float)_random.NextDouble()),
                Growth = growth * (0.8f + 0.4f * (float)_random.NextDouble()),
                Density0 = plume * (0.7f + 0.6f * (float)_random.NextDouble()),
                Soot = soot,
                Seed = (float)_random.NextDouble() * 64f,
                Age = lag,
                Life = life * (0.75f + 0.5f * (float)_random.NextDouble()),
                Lift = lift,
                Billow = billow,
                Chain = e.Chain,
            });
        }
    }

    /// Moves every puff on by dt, drops the spent ones, and lists what's left to draw, and the haze over each engine.
    public void Update(float dt)
    {
        foreach (var key in _emitters.Where(p => !p.Value.Seen).Select(p => p.Key).ToList())
            _emitters.Remove(key);
        _haze.Clear();
        foreach (var e in _emitters.Values)
            if (e.Haze.Strength > 0.01f)
                _haze.Add(e.Haze);

        for (var i = _puffs.Count - 1; i >= 0; i--)
        {
            var p = _puffs[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _puffs.RemoveAt(i);
                continue;
            }

            var radius = p.Radius0 + p.Growth * MathF.Pow(p.Age, 0.75f);
            // The air takes it: a small puff is slowed to it quickly, a big one more slowly, and the faster it's going through
            // the air the harder (drag grows with speed), so under way the plume's knocked over flat behind. It rises while
            // it's hot, and drifts with the gusts.
            var still = p.Drift + new float3(0f, p.Lift * MathF.Exp(-p.Age / 3f) + 0.1f, 0f);
            var through = still - p.Velocity;
            var drag = 1.8f / (1f + radius * 0.6f) * (1f + Length(through) / 5f);
            p.Velocity += through * MathF.Min(1f, drag * dt);
            p.X += p.Velocity.X * dt;
            p.Y += p.Velocity.Y * dt;
            p.Z += p.Velocity.Z * dt;

            // Spread thinner as it grows; in over its first moments, out over its last.
            var thin = p.Radius0 / radius;
            var fade = Math.Clamp(p.Age / 0.1f, 0f, 1f) * (1f - Smooth(p.Life * 0.3f, p.Life, p.Age));
            p.Radius = radius;
            p.Density = p.Density0 * thin * thin * fade;
            _puffs[i] = p;
        }

        // Each engine's points joined in the order they left the stack into capsules, one plume of them; a point too far
        // from the one before (the plume's broken) stands as a ball on its own.
        _drawn.Clear();
        _last.Clear();
        for (var i = 0; i < _puffs.Count; i++)
        {
            var p = _puffs[i];
            var joined = false;
            if (_last.TryGetValue(p.Chain, out var j))
            {
                var q = _puffs[j];
                var (dx, dy, dz) = (p.X - q.X, p.Y - q.Y, p.Z - q.Z);
                var reach = 2.5 * (p.Radius + q.Radius);
                if (dx * dx + dy * dy + dz * dz < reach * reach && (p.Density > 0.002f || q.Density > 0.002f))
                {
                    _drawn.Add(new SmokeSegment(q.X, q.Y, q.Z, q.Radius, p.X, p.Y, p.Z, p.Radius, 0.5f * (p.Density + q.Density),
                        0.5f * (p.Soot + q.Soot), 0.5f * (p.Age + q.Age), 0.5f * (p.Billow + q.Billow)));
                    joined = true;
                }
            }

            if (!joined && p.Density > 0.002f)
                _drawn.Add(new SmokeSegment(p.X, p.Y, p.Z, p.Radius, p.X, p.Y, p.Z, p.Radius, p.Density, p.Soot, p.Age, p.Billow));
            _last[p.Chain] = i;
        }
    }

    public void Clear()
    {
        _puffs.Clear();
        _drawn.Clear();
        _haze.Clear();
        _emitters.Clear();
    }

    private static float Length(float3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
