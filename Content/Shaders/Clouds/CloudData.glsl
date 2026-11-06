// What the cloud passes and everything lit by them share, written once a frame by Clouds.cs.

#ifndef CLOUD_DATA_SET
#define CLOUD_DATA_SET 0
#define CLOUD_DATA_BINDING 3
#endif

struct CloudLayer
{
    vec4 heights; // bottom and top as heights in metres, coverage, density per metre
    vec4 tiles;   // the noise's size in metres: where clouds are in x and z, their shape, their detail
    vec4 profile; // base fade, top start, top reach, anvil
    vec4 look;    // cells, ramp, erosion, sharpness
    vec4 look2;   // skylight, banks, shear, sun
    vec4 relief;  // how far the base rises and the top falls from place to place, as parts of the layer's height
};

layout(set = CLOUD_DATA_SET, binding = CLOUD_DATA_BINDING) uniform CloudUniforms
{
    mat4 previousViewProjection;
    vec4 right;    // camera right * tan(half fov x)
    vec4 up;       // camera up * tan(half fov y)
    vec4 forward;  // w: the camera's near plane
    vec4 sun;
    vec4 sunColor;
    vec4 zenith;
    vec4 horizon;
    vec4 offset;   // xyz: where the camera is in the noise, wind included; w: its height in metres
    vec4 frame;    // x: frame number; y: 1 when there are clouds; z: 1 when last frame's are usable; w: half the shade map's width in metres
    vec4 ground;   // x: the ground under the shade map, relative to the camera; y: how many volumes; zw: the map's middle in x, z, relative to the camera
    vec4 motion;   // xyz: what to add to a point relative to the camera to get where it was relative to last frame's
    vec4 high;     // height, opacity, kind (below 0 for none), how much to thin it by
    CloudLayer layers[2];
} clouds;
