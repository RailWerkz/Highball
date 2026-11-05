// What the cloud passes and everything lit by them share, written once a frame by Clouds.cs.

#ifndef CLOUD_DATA_SET
#define CLOUD_DATA_SET 0
#define CLOUD_DATA_BINDING 3
#endif

layout(set = CLOUD_DATA_SET, binding = CLOUD_DATA_BINDING) uniform CloudUniforms
{
    mat4 previousViewProjection;
    vec4 right;    // camera right * tan(half fov x)
    vec4 up;       // camera up * tan(half fov y)
    vec4 forward;
    vec4 sun;
    vec4 sunColor;
    vec4 zenith;
    vec4 horizon;
    vec4 offset;   // xyz: where the camera is in the noise, wind included; w: its height in metres
    vec4 layer;    // x, y: bottom and top of the clouds, as heights in metres; z: coverage; w: density
    vec4 frame;    // x: frame number; y: 1 when there are clouds; z: 1 when last frame's are usable; w: half the shadow map's width in metres
} clouds;
