#version 450
// Plants and rocks: lit like the ground they stand on, darker low down where they shade themselves, with the sun glowing through
// thin leaves seen against it. Alpha goes to the MSAA samples, so cut-out edges stay soft in the distance.
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"
#include "plant_fade.glsl"
#include "plant_alpha.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec3 vTint;
layout(location = 4) in float vHeight;
// The motion image write would otherwise hold the depth test back until after the shader, shading every blade behind the one
// seen; this pass writes no depth (it tests for equal), so testing first changes nothing it draws.
layout(early_fragment_tests) in;

layout(location = 5) in vec3 vBack;
layout(location = 6) flat in vec2 vFade;
layout(location = 0) out vec4 outColor;

// MotionVectors' image: the wind's sway, for the temporal pass. w is minus this depth, so it's used only where the plant is
// still what's seen once everything after it is drawn.
layout(set = 4, binding = 0, rgba16f) uniform writeonly image2D motionOut;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;
    vec4 band;
    vec4 globals; // x: minus the camera's height
} pc;

void main()
{
    // (As plant_depth.frag, exactly: this pass tests for its depth. The maps are read before anything's discarded: after a discard
    // the pixels beside one may be gone, and the mip they'd pick with them, which could then differ between the two passes.)
    vec4 map = texture(albedoMap, vUv);
    float alpha = plantAlpha(albedoMap, opacityMap, vUv, map.a, pc.shape.w);
    if (fadedOut(vFade, gl_FragCoord.xy, pc.globals.y) || alpha < 0.3)
        discard;

    if (pc.shape.x > 0.0)
        imageStore(motionOut, ivec2(gl_FragCoord.xy), vec4(vBack, -gl_FragCoord.z));

    float dist = length(vRelative);
    vec3 away = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal + vec3(0.0, 1e-4, 0.0));

    // A solid thing shows the side it's lit from; a card of grass keeps its soft normal whichever way it's seen.
    if (pc.shape.y < 0.1 && dot(n, away) > 0.0)
        n = -n;

    Surface s;
    // A rock's map is only brightness over its average, as its colours would speckle once divided, so its colour is all its tint's.
    s.albedo = pc.shape.w > 1.5 ? vec3(dot(map.rgb, vec3(0.333)) / max(dot(textureLod(albedoMap, vec2(0.5), 16.0).rgb, vec3(0.333)), 0.01)) * vTint : map.rgb * vTint;
    s.metallic = 0.0;
    s.roughness = 0.85;
    s.normal = n;
    s.view = -away;
    s.occlusion = mix(0.55, 1.0, clamp(vHeight * 1.4, 0.0, 1.0));
    s.specular = 0.3;
    weatherSurface(s, 0.0, 0.85);

    vec3 sun = sky.sunDirection.xyz;
    float shade = sunShadow(vRelative, n, sun);
    vec3 color = shadeLight(s, sun, SunColor * shade) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);

    if (pc.shape.x > 0.0)
        color += s.albedo * SunColor * shade * pow(max(dot(away, sun), 0.0), 4.0) * 0.5;

    outColor = vec4(applyFog(color, away, dist, -pc.globals.x, sun), clamp((alpha - 0.3) / 0.25 + 0.3, 0.0, 1.0));
}
