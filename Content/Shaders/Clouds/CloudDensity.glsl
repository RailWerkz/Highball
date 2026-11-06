// Cloud shape, adapted from Kitten Space Agency's CloudFunctions. It samples no painted maps: where the clouds are comes from the
// noise itself, at a much larger scale, and how they're shaped with height from a curve, both set for each type of cloud.

#define PI 3.14159265
#define saturate(x) clamp(x, 0.0, 1.0)
#include "Clouds/ScatteringFunctions.glsl"

layout(set = 0, binding = 2) uniform sampler3D noiseTexture;

const float EarthRadius = 6371000.0;

vec3 PlanetCentre()
{
    return vec3(0.0, -(EarthRadius + clouds.offset.w), 0.0);
}

// Distances along a ray to the near and far sides of a sphere round the planet's centre; negative when behind the ray's start.
vec2 SphereHits(vec3 origin, vec3 direction, float radius)
{
    vec3 o = origin - PlanetCentre();
    float b = dot(o, direction);
    float c = dot(o, o) - radius * radius;
    float d = b * b - c;
    if (d < 0.0)
        return vec2(-1.0);
    d = sqrt(d);
    return vec2(-b - d, -b + d);
}

// Density per metre of a layer at a point relative to the camera. dimensional is how deep inside the cloud's rough shape it is,
// noiseDepth how deep inside the carved one, and h how far up the layer, 0 to 1.
float CloudDensity(int index, vec3 relative, float lod, out float dimensional, out float noiseDepth, out float h)
{
    dimensional = 0.0;
    noiseDepth = 0.0;

    vec4 heights = clouds.layers[index].heights;
    vec4 tiles = clouds.layers[index].tiles;
    vec4 profile = clouds.layers[index].profile;
    vec4 look = clouds.layers[index].look;
    vec4 look2 = clouds.layers[index].look2;

    float height = length(relative - PlanetCentre()) - EarthRadius;
    float shell = (height - heights.x) / (heights.y - heights.x);
    h = shell;
    if (shell <= 0.0 || shell >= 1.0)
        return 0.0;

    vec3 p = relative + clouds.offset.xyz;

    // Lean the map over with height, and let broad noise push it about, or every cloud is a straight-sided column. Only smooth
    // noise will do for this: anything sharper, pulled this far, folds the map over itself into ripples and swirls.
    float anvil = profile.w * smoothstep(0.6, 0.85, shell);
    float shear = look2.z * h + anvil * 5000.0;
    float push = textureLod(noiseTexture, p / tiles.z, 2.0).b;
    float pushOther = textureLod(noiseTexture, p / tiles.z + vec3(0.37, 0.11, 0.73), 2.0).b;
    vec2 map = p.xz + vec2(shear, shear * 0.35) + (vec2(push, pushOther) - 0.5) * tiles.z * 0.5;

    // The layer's height is only how far clouds can reach. Each place has its own base and top, rising and falling in broad swells and
    // smaller rolls, so a deck has no flat top or underside, and nothing touches the layer's limits except here and there.
    float swell = textureLod(noiseTexture, vec3(map.x / (tiles.x * 2.0), 0.59, map.y / (tiles.y * 2.0)), 1.0).b;
    float roll = textureLod(noiseTexture, vec3(map.x / (tiles.x * 0.5), 0.77, map.y / (tiles.y * 0.5)), 1.0).b;
    float swellLow = textureLod(noiseTexture, vec3(map.y / (tiles.y * 2.0), 0.23, map.x / (tiles.x * 2.0)), 1.0).b;
    float rollLow = textureLod(noiseTexture, vec3(map.y / (tiles.y * 0.5), 0.91, map.x / (tiles.x * 0.5)), 1.0).b;
    float raised = clamp(((swell * 0.6 + roll * 0.4) - 0.5) * 2.2 + 0.5, 0.0, 1.0);
    float lowered = clamp(((swellLow * 0.6 + rollLow * 0.4) - 0.5) * 2.2 + 0.5, 0.0, 1.0);
    float localBase = clouds.layers[index].relief.x * lowered;
    float localTop = 1.0 - clouds.layers[index].relief.y * (1.0 - raised);
    h = (shell - localBase) / max(localTop - localBase, 0.02);
    if (h <= 0.0 || h >= 1.0)
        return 0.0;

    // Where clouds are: smooth blobs, or a honeycomb of cells, thresholded by how much of the sky should be covered.
    vec3 uv = vec3(map.x / tiles.x, 0.17, map.y / tiles.y);
    float spread = textureLod(noiseTexture, uv, 1.0).b;
    if (look.x > 0.0)
        spread = mix(spread, textureLod(noiseTexture, uv, 0.5).r, look.x);

    float banks = textureLod(noiseTexture, vec3(map.y / (tiles.y * 5.0), 0.37, map.x / (tiles.x * 5.0)), 1.0).b;
    // Bitten by noise in three dimensions too, or clouds are the same outline all the way up, with straight sides.
    spread = clamp((mix(spread, banks, look2.y) - 0.5) * 1.7 + 0.5 + (pushOther - 0.5) * 0.35, 0.0, 1.0);

    // A storm's top spreads out only round the storm: a broad blurred version of where clouds are says how far.
    if (anvil > 0.0)
    {
        float round = textureLod(noiseTexture, uv, 3.5).b;
        spread = max(spread, ((round - 0.5) * 5.0 + 0.5) * anvil * (1.0 - smoothstep(0.92, 1.0, shell)));
    }

    float threshold = 1.0 - heights.z;
    float coverage = smoothstep(threshold - 0.04, threshold + look.y, spread);
    if (coverage < 0.01)
        return 0.0;

    // Bases and rounded tops, reaching higher where there's more cloud.
    float topFalls = profile.y + profile.z * coverage;
    float shapeGradient = smoothstep(0.0, profile.x, h) * (1.0 - smoothstep(min(topFalls, 0.8), min(topFalls + 0.15 + 0.3 * coverage, 1.0), h));
    float densityGradient = mix(0.55, 1.0, smoothstep(0.0, 0.5, h));

    dimensional = coverage + shapeGradient - 1.0;
    if (dimensional < 0.01)
        return 0.0;

    float detail = textureLod(noiseTexture, p / tiles.w, min(lod, 3.0)).g;
    float noise = mix(textureLod(noiseTexture, p / tiles.z, min(lod, 2.0)).r, detail, 0.45);

    return ApplyErosion(noise, dimensional, look.z, look.w, noiseDepth) * heights.w * densityGradient;
}

// How much a thin ice cloud at a point in its sheet covers, 0 to 1. Noise is stretched along x, so its strands run with the high wind.
float HighCloudCover(vec3 relative, float lod)
{
    vec2 p = relative.xz + clouds.offset.xz;
    float patches = textureLod(noiseTexture, vec3(p.y / 180000.0, 0.31, p.x / 180000.0), 1.0).b;
    float kind = clouds.high.z;
    float thin = clouds.high.w;
    float cover;

    if (kind < 0.5)
    {
        // Cirrus: separate soft tufts, mostly a thin veil, with short strands in it rather than lines ruled across the sky. Broad noise
        // turns the strands' direction from one part of the sky to another.
        vec2 turn = vec2(textureLod(noiseTexture, vec3(p.x / 90000.0, 0.63, p.y / 90000.0), 1.5).b, textureLod(noiseTexture, vec3(p.y / 90000.0, 0.13, p.x / 90000.0), 1.5).b) - 0.5;
        vec2 q = p + vec2(turn.x * 12000.0, turn.y * 14000.0);
        float tuft = textureLod(noiseTexture, vec3(p.x / 36000.0, 0.41, p.y / 24000.0), 1.0).b;
        float soft = textureLod(noiseTexture, vec3(q.x / 12600.0, 0.57, q.y / 7200.0), lod).b;
        float wisp = textureLod(noiseTexture, vec3(q.x / 14400.0, 0.47, q.y / 1800.0), lod).b;
        float strand = textureLod(noiseTexture, vec3(q.x / 5040.0, 0.83, q.y / 630.0), lod).b;
        float where = smoothstep(0.5 + thin, 0.76 + thin, patches * 0.3 + tuft * 0.7 + (soft - 0.5) * 0.25);
        cover = where * (0.5 + 0.5 * smoothstep(0.38, 0.75, wisp * 0.6 + strand * 0.4));
    }
    else if (kind < 1.5)
    {
        // Cirrostratus: a veil, barely varying.
        float veil = textureLod(noiseTexture, vec3(p.x / 60000.0, 0.11, p.y / 24000.0), max(lod, 1.0)).b;
        cover = (0.8 + 0.35 * (veil - 0.5)) * smoothstep(-0.05 + thin, 0.25 + thin, patches);
    }
    else
    {
        // Cirrocumulus: tiny flakes in rows, in patches.
        float flakes = textureLod(noiseTexture, vec3(p.x / 2400.0, 0.71, p.y / 1600.0), lod).g;
        float rows = textureLod(noiseTexture, vec3(p.x / 24000.0, 0.27, p.y / 6000.0), max(lod, 1.0)).b;
        cover = smoothstep(0.52, 0.7, flakes) * smoothstep(0.4 + thin, 0.62 + thin, patches * 0.6 + rows * 0.4);
    }

    return clamp(cover, 0.0, 1.0) * clouds.high.y;
}
