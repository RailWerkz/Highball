#version 450
// Cuts the leaves and blades out of a plant's shadow.

layout(location = 0) in vec2 vUv;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset;
    vec4 shape;
    vec4 shift;
} pc;

void main()
{
    if (pc.shape.w > 1.5)
        return;

    float alpha = pc.shape.w > 0.5 ? texture(opacityMap, vUv).r : texture(albedoMap, vUv).a;
    if (alpha < 0.4)
        discard;
}
