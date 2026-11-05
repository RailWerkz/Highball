#version 450
// Lays the half-size clouds over the finished world. Each pixel prefers the cloud pixels that marched as far as it did, or a hill's
// edge would pick up the clouds from the sky behind it.

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D cloudColor;
layout(set = 0, binding = 1) uniform sampler2D sceneDepth;

#define CLOUD_DATA_SET 0
#define CLOUD_DATA_BINDING 2
#include "Clouds/CloudData.glsl"

float AlongView(ivec2 pixel)
{
    float depth = texelFetch(sceneDepth, pixel, 0).r;
    return clouds.forward.w / max(depth, 1e-8);
}

void main()
{
    ivec2 screen = textureSize(sceneDepth, 0);
    ivec2 cloudSize = textureSize(cloudColor, 0);
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float here = AlongView(pixel);

    vec2 place = (vec2(pixel) + 0.5) * 0.5 - 0.5;
    ivec2 corner = ivec2(floor(place));
    vec2 f = place - vec2(corner);

    vec4 sum = vec4(0.0);
    float total = 0.0;
    for (int y = 0; y <= 1; y++)
    {
        for (int x = 0; x <= 1; x++)
        {
            ivec2 at = clamp(corner + ivec2(x, y), ivec2(0), cloudSize - 1);
            float along = AlongView(min(at * 2, screen - 1));
            float near = abs(along - here) / min(along, here);
            float weight = (x == 1 ? f.x : 1.0 - f.x) * (y == 1 ? f.y : 1.0 - f.y) * (near < 0.03 ? 1.0 : 0.002) + 1e-5;
            sum += texelFetch(cloudColor, at, 0) * weight;
            total += weight;
        }
    }

    outColor = sum / total;
}
