#version 450
// One frame of a tree's impostor (ImpostorBaker): its colour with full coverage where it's cut out, or its normal (model space)
// and depth (-1 to 1 over the sphere, towards the viewer). Unlit: impostor.frag lights it as plant.frag lights the model.
#include "plant_alpha.glsl"

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in float vDepth;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    vec4 right;
    vec4 up;
    vec4 view;
    vec4 centre;
    vec4 cut;
} pc;

void main()
{
    vec4 map = texture(albedoMap, vUv);
    float alpha = pc.cut.x > 1.5 ? 1.0 : pc.cut.x > 0.5 ? holdCoverage(texture(opacityMap, vUv).r, opacityMap, vUv) : holdCoverage(map.a, albedoMap, vUv);
    if (alpha < 0.3)
        discard;

    if (pc.centre.w < 0.5)
        outColor = vec4(map.rgb, 1.0);
    else
        outColor = vec4(normalize(vNormal) * 0.5 + 0.5, clamp(vDepth * 0.5 + 0.5, 0.0, 1.0));
}
