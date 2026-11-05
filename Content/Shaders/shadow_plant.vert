#version 450
// A plant or rock in a shadow map: the same as plant.vert, so shadows move and fade with what casts them.

#include "plant_wind.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace;
layout(location = 4) in vec4 inTint;

layout(location = 0) out vec2 vUv;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset;
    vec4 shape;
    vec4 shift;
    vec4 wind;
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

    if (pc.shape.x > 0.0)
    {
        float up = max(inPosition.y + pc.shift.y, 0.0);
        turned.xz += windPush(pc.wind.xy + inPlace.xz, away, vec3(pc.wind.zw, pc.offset.w), up * up * pc.shape.x * size);
    }

    vUv = inUv;
    gl_Position = pc.light * vec4(standing + turned, 1.0);
}
