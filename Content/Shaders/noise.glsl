// Value noise and fbm for procedural surface detail.

// An integer hash of a lattice point, so it stays random however far from the origin the point is (a float hash
// runs out of precision a few kilometres out and turns into stripes and blocks).
float hash(ivec2 p)
{
    uvec2 v = uvec2(p) * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    return float(v.x ^ v.y) * (1.0 / 4294967296.0);
}

float noise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = p - cell;
    ivec2 i = ivec2(cell);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + ivec2(1, 0)), u.x), mix(hash(i + ivec2(0, 1)), hash(i + ivec2(1, 1)), u.x), u.y);
}

// Octaves fade out to their average as they shrink below a pixel, so distant ground doesn't shimmer and the detail thins out
// smoothly with distance.
float fbm(vec2 p, float footprint)
{
    float sum = 0.0;
    float amplitude = 0.5;
    float total = 0.0;
    for (int i = 0; i < 5; i++)
    {
        float fade = 1.0 - smoothstep(0.25, 0.5, footprint);
        if (fade <= 0.0)
        {
            // This octave and every finer one are below a pixel: all at their average, so they needn't be worked out.
            float rest = 2.0 * amplitude * (1.0 - exp2(float(i - 5)));
            sum += 0.5 * rest;
            total += rest;
            break;
        }
        sum += mix(0.5, noise(p), fade) * amplitude;
        total += amplitude;
        p = mat2(1.6, 1.2, -1.2, 1.6) * p + 17.1; // rotated each octave so the lattice doesn't line up
        footprint *= 2.0;
        amplitude *= 0.5;
    }
    return sum / total;
}
