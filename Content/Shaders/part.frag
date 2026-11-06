#version 450
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in float vTint;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera; // w: how far the parts are drawn
    vec4 sun;
} pc;

void main()
{
    float dist = length(vRelative);
    if (dist > pc.camera.w)
        discard;

    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    if (dot(n, view) > 0.0)
        n = -n; // mirrored instances turn their faces inside out; light the side we see
    vec3 sun = pc.sun.xyz;

    // Each one a little lighter or darker than the next.
    vec3 albedo = texture(albedoMap, vUv).rgb * (0.75 + 0.5 * vTint);

    // Weathered wood, and rusted plates: neither shows bare metal.
    Surface s;
    s.albedo = albedo;
    s.metallic = 0.0;
    s.roughness = 0.8;
    s.normal = n;
    s.view = -view;
    s.occlusion = 0.85;
    s.specular = 0.5;
    weatherSurface(s, 0.0, 1.0);

    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative, n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);
    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, 1.0);
}
