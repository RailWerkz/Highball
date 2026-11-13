#version 460
#extension GL_EXT_buffer_reference : require
// A plant or rock in a shadow map: the same as plant.vert, so shadows move and fade with what casts them.

#include "plant_wind.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace;
layout(location = 4) in vec4 inTint;

layout(location = 0) out vec2 vUv;

// Each indirect draw's cell (Vegetation): xy where it is relative to the camera, zw its place on the ground, wrapped.
layout(buffer_reference, std430, buffer_reference_align = 16) readonly buffer Cells
{
    vec4 cell[];
};

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 shape;   // how much the wind moves it, how much its normal leans up, how far away it's gone, where its cut-out comes from
    vec4 shift;   // moves the mesh to stand on its base; w: the camera's height
    vec4 globals; // x: the ground's origin below the camera (minus its height), y: the flutter's phase, zw: the gusts' phases
    Cells cells;
    uint base;    // the first of this call's cells
} pc;

void main()
{
    vec4 cell = pc.cells.cell[pc.base + gl_DrawID];
    vec3 offset = vec3(cell.x, pc.globals.x, cell.y);
    float size = inPlace.w;
    float random = inTint.w;

    vec3 standing = inPlace.xyz + offset;
    float away = length(standing);
    size *= 1.0 - smoothstep(0.75, 1.0, away / (pc.shape.z * mix(0.25, 1.0, random)));

    float turn = fract(random * 37.7) * 6.2831853;
    float c = cos(turn);
    float s = sin(turn);
    vec3 model = (inPosition + pc.shift.xyz) * size;
    vec3 turned = vec3(model.x * c - model.z * s, model.y, model.x * s + model.z * c);

    if (pc.shape.x > 0.0)
    {
        float up = max(inPosition.y + pc.shift.y, 0.0);
        turned.xz += windPush(cell.zw + inPlace.xz, away, vec3(pc.globals.zw, pc.globals.y), up * up * pc.shape.x * size);
    }

    vUv = inUv;
    gl_Position = pc.light * vec4(standing + turned, 1.0);
}
