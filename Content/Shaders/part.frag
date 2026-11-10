#version 450
// Depth is tested before shading: nothing here discards (part.vert leaves out what's too far), so ties hidden behind a bed or
// another tie are never shaded.
layout(early_fragment_tests) in;
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in float vTint;
layout(location = 4) in vec3 vModel;
layout(location = 5) in vec3 vModelNormal;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;

// The material a tie or a plate is made of. A single texel for anything else, which goes by its own texture.
layout(set = 4, binding = 0) uniform sampler2D materialColor;
layout(set = 4, binding = 1) uniform sampler2D materialNormal;
layout(set = 4, binding = 2) uniform sampler2D materialPacked; // green roughness, blue metalness

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera; // w: how far the parts are drawn
    vec4 sun;
} pc;

// The material laid over a part with no thought for its own map: along its length (x) on every long face, across on its ends.
vec2 materialUv()
{
    const float Tile = 1.1; // metres a repeat of the material covers
    vec3 a = abs(vModelNormal);
    vec2 uv = a.x > a.y && a.x > a.z ? vModel.zy * vec2(1.0, 1.0) : a.y > a.z ? vModel.xz : vModel.xy;
    // Each one its own piece of the material.
    return uv / Tile + vec2(fract(vTint * 37.0), fract(vTint * 91.0));
}

// A normal map's bump, with the surface's directions worked out from how the position and map change across the pixel.
vec3 mapped(vec3 n, vec3 position, vec2 uv)
{
    vec3 dpx = dFdx(position);
    vec3 dpy = dFdy(position);
    vec2 dux = dFdx(uv);
    vec2 duy = dFdy(uv);
    // Which way round the pixel's own directions lie decides the sign: screen y runs down.
    float side = dot(dpx, cross(dpy, n)) < 0.0 ? -1.0 : 1.0;
    vec3 t = (cross(dpy, n) * dux.x + cross(n, dpx) * duy.x) * side;
    vec3 b = (cross(dpy, n) * dux.y + cross(n, dpx) * duy.y) * side;
    float scale = max(dot(t, t), dot(b, b));
    if (scale < 1e-20)
        return n;
    vec3 bump = texture(materialNormal, uv).xyz * 2.0 - 1.0;
    // A map's green points up its image, which is against the way v runs.
    return normalize(t * bump.x - b * bump.y + n * bump.z * sqrt(scale));
}

void main()
{
    float dist = length(vRelative);

    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    if (dot(n, view) > 0.0)
        n = -n; // mirrored instances turn their faces inside out; light the side we see
    vec3 sun = pc.sun.xyz;

    // Each one a little lighter or darker than the next.
    vec3 albedo = texture(albedoMap, vUv).rgb * (0.75 + 0.5 * vTint);
    float metallic = 0.0;
    float roughness = 0.8;
    vec3 surface = n;

    if (textureSize(materialColor, 0).x > 1)
    {
        vec2 uv = materialUv();
        vec2 packed = texture(materialPacked, uv).gb;
        roughness = packed.x;
        metallic = packed.y;
        // Creosoted and weathered, darker than clean timber; steel as it is.
        albedo = texture(materialColor, uv).rgb * (0.75 + 0.5 * vTint) * mix(0.55, 1.0, metallic);
        surface = mapped(n, vRelative, uv);
    }

    Surface s;
    s.albedo = albedo;
    s.metallic = metallic;
    s.roughness = roughness;
    s.normal = surface;
    s.view = -view;
    s.occlusion = 0.85;
    s.specular = 0.5;
    weatherSurface(s, 0.0, 1.0);

    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative, n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);
    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, 1.0);
}
