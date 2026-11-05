#version 450
// Darkens the finished world by its occlusion. Occlusion only takes light from the sky, so each pixel works out how much of its light
// is the sky's and how much the sun's, and the sunlit ground keeps its brightness save for a little in the deepest corners.
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D occlusionMap;
layout(set = 0, binding = 1) uniform sampler2D sceneDepth;

#define OCCLUSION_SET 0
#define OCCLUSION_BINDING 2
#include "Occlusion/OcclusionData.glsl"

float brightness(vec3 color)
{
    return dot(color, vec3(0.2126, 0.7152, 0.0722));
}

void main()
{
    ivec2 screen = textureSize(sceneDepth, 0);
    ivec2 half2 = textureSize(occlusionMap, 0);
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float depth = texelFetch(sceneDepth, pixel, 0).r;
    if (depth <= 0.0)
    {
        outColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float here = alongView(depth);
    vec2 place = (vec2(pixel) + 0.5) * 0.5 - 0.5;
    ivec2 corner = ivec2(floor(place));
    vec2 f = place - vec2(corner);
    float sum = 0.0;
    float total = 0.0;
    for (int y = 0; y <= 1; y++)
    {
        for (int x = 0; x <= 1; x++)
        {
            ivec2 at = clamp(corner + ivec2(x, y), ivec2(0), half2 - 1);
            float along = alongView(texelFetch(sceneDepth, min(at * 2, screen - 1), 0).r);
            float weight = (x == 1 ? f.x : 1.0 - f.x) * (y == 1 ? f.y : 1.0 - f.y) * (abs(along - here) / min(along, here) < 0.03 ? 1.0 : 0.002) + 1e-5;
            sum += texelFetch(occlusionMap, at, 0).r * weight;
            total += weight;
        }
    }

    float open = sum / total;
    if (open > 0.995)
    {
        outColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    vec3 relative = placeAt((vec2(pixel) + 0.5) / vec2(screen), depth);
    vec3 n = normalAt(sceneDepth, pixel, screen, relative);
    vec3 sun = sky.sunDirection.xyz;
    float direct = brightness(SunColor) * max(dot(n, sun), 0.0) * sunShadow(relative, n, sun);
    float ambient = brightness(skyIrradiance(n));
    float kept = (direct + ambient * open) / max(direct + ambient, 1e-5);

    // The blend multiplies what's there by the alpha.
    outColor = vec4(0.0, 0.0, 0.0, min(kept, mix(1.0, open, 0.2)));
}
