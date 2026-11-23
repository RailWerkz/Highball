#version 450
// An impostor's depth alone, before it's shaded (impostor.frag), as plant_depth.frag is for the plants' own models: which pixels
// its tree covers must match impostor.frag's exactly.
#include "plant_fade.glsl"
#include "impostor.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) flat in vec3 vCentre;
layout(location = 2) flat in float vRadius;
layout(location = 4) flat in vec2 vTurn;
layout(location = 6) flat in vec2 vFade;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D depthMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;
    vec4 band;
    vec4 globals;
} pc;

void main()
{
    // (The footprint before anything's discarded, where every pixel still runs.)
    vec3 o = intoModel((vRelative - vCentre) / vRadius, vTurn);
    float footprint = max(length(dFdx(o)), length(dFdy(o)));
    if (fadedOut(vFade, gl_FragCoord.xy, pc.globals.y))
        discard;
    vec3 d = intoModel(normalize(vRelative), vTurn);
    vec2 randoms = vec2(pixelRandom(gl_FragCoord.xy + vec2(37.0, 17.0), pc.globals.y), pixelRandom(gl_FragCoord.xy + vec2(-23.0, 61.0), pc.globals.y));
    vec2 uv;
    vec3 hit;
    vec4 albedo;
    if (!impostorHit(albedoMap, depthMap, o, d, randoms, footprint, uv, hit, albedo))
        discard;
    outColor = vec4(0.0, 0.0, 0.0, clamp((albedo.a - 0.35) / 0.25 + 0.35, 0.0, 1.0));
}
