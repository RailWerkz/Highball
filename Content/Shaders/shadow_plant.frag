#version 450
// Cuts the leaves and blades out of a plant's shadow.

#include "plant_alpha.glsl"

layout(location = 0) in vec2 vUv;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 shape;
    vec4 band;
} pc;

void main()
{
    if (pc.shape.w > 1.5)
        return;

    float alpha = pc.shape.w > 0.5 ? holdCoverage(texture(opacityMap, vUv).r, opacityMap, vUv) : holdCoverage(texture(albedoMap, vUv).a, albedoMap, vUv);
    if (alpha < 0.4)
        discard;
}
