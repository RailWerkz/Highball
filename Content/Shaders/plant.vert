#version 460
#extension GL_EXT_buffer_reference : require
// Plants and rocks, one mesh drawn many times. Each shrinks away with distance, its own random number saying how soon, so a far
// hillside thins out without any of them popping.

#include "plant_wind.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace; // relative to its cell, and its size
layout(location = 4) in vec4 inTint;  // its colour, and its random number

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec3 vTint;
layout(location = 4) out float vHeight;

// Each indirect draw's cell (Vegetation): xy where it is relative to the camera, zw its place on the ground, wrapped.
layout(buffer_reference, std430, buffer_reference_align = 16) readonly buffer Cells
{
    vec4 cell[];
};

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;   // how much the wind moves it, how much its normal leans up, how far away it's gone, where its cut-out comes from
    vec4 band;    // xy: from and to how far away it's drawn, each instance's own a little nearer or further (its detail levels)
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

    // Outside its detail level's distances it collapses to nothing, the next level drawn there instead. Each instance moves the
    // change by up to a tenth, the same for every level of it, so they don't all change at once.
    float stagger = 0.9 + 0.2 * fract(random * 13.1);
    if (away < pc.band.x * stagger || away >= pc.band.y * stagger)
        size = 0.0;

    float turn = fract(random * 37.7) * 6.2831853;
    float c = cos(turn);
    float s = sin(turn);
    vec3 model = inPosition * size;
    vec3 turned = vec3(model.x * c - model.z * s, model.y, model.x * s + model.z * c);
    vec3 normal = vec3(inNormal.x * c - inNormal.z * s, inNormal.y, inNormal.x * s + inNormal.z * c);

    // Bends more the higher up it is.
    if (pc.shape.x > 0.0)
    {
        float up = max(inPosition.y, 0.0);
        turned.xz += windPush(cell.zw + inPlace.xz, away, vec3(pc.globals.zw, pc.globals.y), up * up * pc.shape.x * size);
    }

    vec3 relative = standing + turned;
    vRelative = relative;
    // (Nudged up, so a normal pointing straight down, leaned halfway up, doesn't come to nothing.)
    vNormal = normalize(mix(normal, vec3(0.0, 1.0, 0.0), pc.shape.y) + vec3(0.0, 1e-3, 0.0));
    vUv = inUv;
    vTint = inTint.rgb;
    vHeight = inPosition.y;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
