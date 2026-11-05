// The clouds for world shaders (set 3): what the sky shows through them, and the shade they cast on the ground.

#define CLOUD_DATA_SET 3
#define CLOUD_DATA_BINDING 0
#include "Clouds/CloudData.glsl"

layout(set = 3, binding = 1) uniform sampler2D cloudColor;
layout(set = 3, binding = 2) uniform sampler2D cloudShadowMap;

// The sky behind a direction, with the clouds in front. ndc: where it is on screen, +y up.
vec3 applyClouds(vec3 sky, vec2 ndc)
{
    if (clouds.frame.y < 0.5)
        return sky;

    vec4 c = texture(cloudColor, vec2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5));
    return sky * c.a + c.rgb;
}

// How much of the sun's light gets through the clouds to a point relative to the camera.
float cloudSunShadow(vec3 relative)
{
    if (clouds.frame.y < 0.5)
        return 1.0;

    vec2 uv = relative.xz / clouds.frame.w * 0.5 + 0.5;
    return texture(cloudShadowMap, uv).r;
}
