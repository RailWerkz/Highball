// The shade clouds cast on the world (set 3). The clouds themselves are drawn over everything after it: cloud_composite.frag.

#define CLOUD_DATA_SET 3
#define CLOUD_DATA_BINDING 0
#include "Clouds/CloudData.glsl"

layout(set = 3, binding = 1) uniform sampler2D cloudShadowMap;

// How much of the sun's light gets through the clouds to a point relative to the camera.
float cloudSunShadow(vec3 relative)
{
    if (clouds.frame.y < 0.5)
        return 1.0;

    // The map is of the ground: a point higher up is under the same cloud as a point further towards the sun's far side.
    vec3 sun = clouds.sun.xyz;
    vec2 at = relative.xz - clouds.ground.zw - sun.xz / max(sun.y, 0.15) * (relative.y - clouds.ground.x);
    vec2 place = at / clouds.frame.w;

    // Past its edge there's no knowing: fade to clear rather than smear its last row out to the horizon.
    float shade = texture(cloudShadowMap, place * 0.5 + 0.5).r;
    return mix(shade, 1.0, smoothstep(0.8, 1.0, max(abs(place.x), abs(place.y))));
}
