#version 450
// Plants and rocks: lit like the ground they stand on, darker low down where they shade themselves, with the sun glowing through
// thin leaves seen against it. Alpha goes to the MSAA samples, so cut-out edges stay soft in the distance.
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec3 vTint;
layout(location = 4) in float vHeight;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D opacityMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 shape;
    vec4 shift; // w: the camera's height
    vec4 wind;
} pc;

void main()
{
    vec4 map = texture(albedoMap, vUv);
    float alpha = pc.shape.w > 1.5 ? 1.0 : pc.shape.w > 0.5 ? texture(opacityMap, vUv).r : map.a;
    if (alpha < 0.3)
        discard;

    float dist = length(vRelative);
    vec3 away = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);

    // A solid thing shows the side it's lit from; a card of grass keeps its soft normal whichever way it's seen.
    if (pc.shape.y < 0.1 && dot(n, away) > 0.0)
        n = -n;

    Surface s;
    // A rock's map is detail over its average, as on the ground, so its colour is all its tint's.
    s.albedo = pc.shape.w > 1.5 ? map.rgb / max(textureLod(albedoMap, vec2(0.5), 16.0).rgb, vec3(0.01)) * vTint : map.rgb * vTint;
    s.metallic = 0.0;
    s.roughness = 0.85;
    s.normal = n;
    s.view = -away;
    s.occlusion = mix(0.55, 1.0, clamp(vHeight * 1.4, 0.0, 1.0));
    s.specular = 0.3;

    vec3 sun = sky.sunDirection.xyz;
    float shade = sunShadow(vRelative, n, sun);
    vec3 color = shadeLight(s, sun, SunColor * shade) + shadeSky(s, sun);

    if (pc.shape.x > 0.0)
        color += s.albedo * SunColor * shade * pow(max(dot(away, sun), 0.0), 4.0) * 0.5;

    outColor = vec4(applyFog(color, away, dist, pc.shift.w, sun), clamp((alpha - 0.3) / 0.25 + 0.3, 0.0, 1.0));
}
