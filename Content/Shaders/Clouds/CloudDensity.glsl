// Cloud shape, adapted from Kitten Space Agency's CloudFunctions. It samples no painted maps: where the clouds are comes from
// the noise itself, at a much larger scale, and how they're shaped with height from a simple curve.

#define PI 3.14159265
#define saturate(x) clamp(x, 0.0, 1.0)
#include "Clouds/ScatteringFunctions.glsl"

layout(set = 0, binding = 2) uniform sampler3D noiseTexture;

const float EarthRadius = 6371000.0;
const float CoverageTile = 40000.0; // these all divide 120 km, which the camera's place in the noise wraps at
const float ShapeTile = 8000.0;
const float DetailTile = 1500.0;
const float CloudErosionDepth = 0.9;
const float CloudEdgeSharpness = 0.35;

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

// Density per metre at a point relative to the camera. dimensional is how deep inside the cloud's rough shape it is, and
// noiseDepth how deep inside the carved one: both stand in for how much cloud surrounds it, when it's lit.
float CloudDensity(vec3 relative, float lod, out float dimensional, out float noiseDepth)
{
    dimensional = 0.0;
    noiseDepth = 0.0;

    float height = length(relative - PlanetCentre()) - EarthRadius;
    float h = (height - clouds.layer.x) / (clouds.layer.y - clouds.layer.x);
    if (h <= 0.0 || h >= 1.0)
        return 0.0;

    vec3 p = relative + clouds.offset.xyz;

    // Shear the map with height, and let the shape noise push it about, or every cloud is a straight-sided column.
    float billow = textureLod(noiseTexture, p / ShapeTile, min(lod, 2.0)).r;
    vec2 map = p.xz + vec2(h * 1400.0, h * 500.0) + (billow - 0.5) * 1100.0;

    // Banks of cloud and the gaps between them, thresholded by how much of the sky should be covered.
    float banks = textureLod(noiseTexture, vec3(map.x, 0.0, map.y) / CoverageTile, 0.0).r;
    float patches = textureLod(noiseTexture, vec3(map.y, 0.3 * CoverageTile, map.x) / (CoverageTile * 0.25), 0.0).r;
    float spread = banks * 0.45 + patches * 0.55;
    float coverage = smoothstep(1.0 - clouds.layer.z - 0.06, 1.0 - clouds.layer.z + 0.14, spread);
    if (coverage < 0.01)
        return 0.0;

    // Flat bases and rounded tops, reaching higher where there's more cloud.
    float shapeGradient = smoothstep(0.0, 0.05, h) * (1.0 - smoothstep(0.1 + 0.55 * coverage, 0.25 + 0.75 * coverage, h));
    float densityGradient = mix(0.55, 1.0, smoothstep(0.0, 0.5, h));

    dimensional = coverage + shapeGradient - 1.0;
    if (dimensional < 0.01)
        return 0.0;

    float detail = textureLod(noiseTexture, p / DetailTile, min(lod, 3.0)).g;
    float noise = mix(billow, detail, 0.35);

    return ApplyErosion(noise, dimensional, CloudErosionDepth, CloudEdgeSharpness, noiseDepth) * clouds.layer.w * densityGradient;
}
