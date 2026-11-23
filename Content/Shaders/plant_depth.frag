#version 450
// Plants' depth alone, before they're shaded (plant.frag): the cut-out decides which samples they cover, and the shaded pass then
// tests for equal depth, so the full lighting runs once a pixel instead of once for every blade of grass stacked in front of it.
// The cut-out, the hand-over dither and the alpha given to the samples must match plant.frag's.

#include "plant_fade.glsl"
#include "plant_alpha.glsl"

layout(location = 2) in vec2 vUv;
layout(location = 6) flat in vec2 vFade;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;
    vec4 band;
    vec4 globals;
} pc;

void main()
{
    float alpha = plantAlpha(albedoMap, opacityMap, vUv, texture(albedoMap, vUv).a, pc.shape.w);
    if (fadedOut(vFade, gl_FragCoord.xy, pc.globals.y) || alpha < 0.3)
        discard;
    outColor = vec4(0.0, 0.0, 0.0, clamp((alpha - 0.3) / 0.25 + 0.3, 0.0, 1.0));
}
