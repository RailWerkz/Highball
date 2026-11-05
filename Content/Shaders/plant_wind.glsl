// How far the wind pushes a plant's top. Gusts are broad waves that travel downwind across the ground, so neighbours move together
// and slowly; each plant only adds a small flutter of its own. Bark and leaves standing at one place move as one.

const vec2 WindDirection = vec2(0.943, 0.33); // east and a little south, as the clouds go

// where: the plant's place, wrapped to 1024 m so these whole-number waves meet themselves
// phases: x and y the two gusts' phases, z the flutter's
// lean: how far a top one unit up leans at the strongest, in units
vec2 windPush(vec2 where, float away, vec3 phases, float lean)
{
    const float toPhase = 6.2831853 / 1024.0;
    float broad = 0.5 + 0.5 * sin(dot(where, vec2(14.0, 5.0)) * toPhase - phases.x);
    float narrow = 0.5 + 0.5 * sin(dot(where, vec2(27.0, -4.0)) * toPhase - phases.y);
    float gust = broad * (0.35 + 0.65 * narrow);

    // No movement to see far away, where it would only shimmer.
    lean *= 1.0 - smoothstep(70.0, 180.0, away);

    float own = fract(sin(dot(where, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
    float flutter = sin(phases.z + own) * (0.05 + 0.15 * gust);
    return (WindDirection * (0.25 + 0.75 * gust) + vec2(-WindDirection.y, WindDirection.x) * flutter) * lean;
}
