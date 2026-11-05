#version 450
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

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset; // the cell relative to the camera; w: the flutter's phase
    vec4 shape;  // how much the wind moves it, how much its normal leans up, how far away it's gone, where its cut-out comes from
    vec4 shift;  // moves the mesh to stand on its base; w: the camera's height
    vec4 wind;   // xy: the cell's place, wrapped; zw: the gusts' phases
} pc;

void main()
{
    float size = inPlace.w;
    float random = inTint.w;

    vec3 standing = inPlace.xyz + pc.offset.xyz;
    float away = length(standing);
    size *= 1.0 - smoothstep(0.75, 1.0, away / (pc.shape.z * mix(0.25, 1.0, random)));

    float turn = fract(random * 37.7) * 6.2831853;
    float c = cos(turn);
    float s = sin(turn);
    vec3 model = (inPosition + pc.shift.xyz) * size;
    vec3 turned = vec3(model.x * c - model.z * s, model.y, model.x * s + model.z * c);
    vec3 normal = vec3(inNormal.x * c - inNormal.z * s, inNormal.y, inNormal.x * s + inNormal.z * c);

    // Bends more the higher up it is.
    if (pc.shape.x > 0.0)
    {
        float up = max(inPosition.y + pc.shift.y, 0.0);
        turned.xz += windPush(pc.wind.xy + inPlace.xz, away, vec3(pc.wind.zw, pc.offset.w), up * up * pc.shape.x * size);
    }

    vec3 relative = standing + turned;
    vRelative = relative;
    vNormal = normalize(mix(normal, vec3(0.0, 1.0, 0.0), pc.shape.y));
    vUv = inUv;
    vTint = inTint.rgb;
    vHeight = inPosition.y;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
