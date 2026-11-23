#version 450
// A far tree as its impostor (impostor.glsl), shaded as plant.frag shades the tree's own model: its baked colour times its
// instance's tint, its baked normal leaned up as the leaves' is, the sun glowing through it seen against it.
//
// The baked normal is the average of the normals its texel covered, not made to length 1: where the leaves in a texel point
// many ways it's short (SmartImposter's normal spread). Lighting the averaged direction at full strength would light a clump
// of leaves as if they all faced it, brighter than the tree's own model; the sun's share is taken down by how far they disagree.
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"
#include "plant_fade.glsl"
#include "impostor.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) flat in vec3 vCentre;
layout(location = 2) flat in float vRadius;
layout(location = 3) flat in vec3 vTint;
layout(location = 4) flat in vec2 vTurn;
layout(location = 6) flat in vec2 vFade;
layout(early_fragment_tests) in;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D depthMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;   // y: how much its normal leans up
    vec4 band;    // z: its sphere's centre's height, w: its radius (model, at a size of 1)
    vec4 globals; // x: minus the camera's height, y: the flutter's phase
} pc;

void main()
{
    // (As impostor_depth.frag, exactly: this pass tests for its depth.)
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

    vec3 baked = textureGrad(depthMap, uv, vec2(footprint * 0.5 / Frames, 0.0), vec2(0.0, footprint * 0.5 / Frames)).rgb * 2.0 - 1.0;
    float agree = clamp(length(baked), 0.0, 1.0);
    vec3 n = outOfModel(baked / max(agree, 1e-3), vTurn);

    vec3 relative = vCentre + outOfModel(hit, vTurn) * vRadius;
    float dist = length(relative);
    vec3 away = relative / max(dist, 1e-3);
    n = normalize(mix(n, vec3(0.0, 1.0, 0.0), pc.shape.y) + vec3(0.0, 1e-3, 0.0));

    // Its height in the model (unscaled), as plant.frag's vHeight: darker low down.
    float height = pc.band.z + hit.y * pc.band.w;

    Surface s;
    s.albedo = albedo.rgb * vTint;
    s.metallic = 0.0;
    s.roughness = 0.85;
    s.normal = n;
    s.view = -away;
    s.occlusion = mix(0.55, 1.0, clamp(height * 1.4, 0.0, 1.0));
    s.specular = 0.3;
    weatherSurface(s, 0.0, 0.85);

    vec3 sun = sky.sunDirection.xyz;
    float shade = sunShadow(relative, n, sun) * mix(0.6, 1.0, agree);
    vec3 color = shadeLight(s, sun, SunColor * shade) + shadeSky(s, sun) + shadeLocalLights(s, relative);
    color += s.albedo * SunColor * shade * pow(max(dot(away, sun), 0.0), 4.0) * 0.5;

    outColor = vec4(applyFog(color, away, dist, -pc.globals.x, sun), clamp((albedo.a - 0.35) / 0.25 + 0.35, 0.0, 1.0));
}
