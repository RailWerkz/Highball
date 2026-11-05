#version 450
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D albedoMap;
layout(set = 0, binding = 1) uniform sampler2D metallicRoughnessMap; // glTF: green roughness, blue metalness
layout(set = 0, binding = 2) uniform sampler2D normalMap;            // one texel when the model has none

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 rotation;
    vec4 placeAndScale;
    vec4 baseColor;
    vec4 material; // x: metallic, y: roughness, z: camera height, w: alpha cutoff
} pc;

// A normal map's bump, with the surface's directions along the map worked out from how the position and map change across the
// pixel, so models needn't carry tangents, and mirrored parts of a map come out right.
vec3 mapped(vec3 n, vec3 position, vec2 uv)
{
    vec3 dpx = dFdx(position);
    vec3 dpy = dFdy(position);
    vec2 dux = dFdx(uv);
    vec2 duy = dFdy(uv);
    vec3 t = cross(dpy, n) * dux.x + cross(n, dpx) * duy.x;
    vec3 b = cross(dpy, n) * dux.y + cross(n, dpx) * duy.y;
    float scale = max(dot(t, t), dot(b, b));
    if (scale < 1e-20)
        return n;

    vec3 bump = texture(normalMap, uv).xyz * 2.0 - 1.0;
    // A map's green points up its image, which is against the way glTF's v runs.
    return normalize(t * bump.x - b * bump.y + n * bump.z * sqrt(scale));
}

void main()
{
    vec4 albedo = texture(albedoMap, vUv) * pc.baseColor;
    if (albedo.a < pc.material.w)
        discard;

    float dist = length(vRelative);
    vec3 away = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    if (dot(n, away) > 0.0)
        n = -n; // nothing's culled, so light the side we see
    vec3 shape = n;
    if (textureSize(normalMap, 0).x > 1)
        n = mapped(n, vRelative, vUv);

    vec2 map = texture(metallicRoughnessMap, vUv).gb;
    Surface s;
    s.albedo = albedo.rgb;
    s.metallic = clamp(pc.material.x * map.y, 0.0, 1.0);
    s.roughness = clamp(pc.material.y * map.x, 0.0, 1.0);
    s.normal = n;
    s.view = -away;
    s.occlusion = 1.0;
    s.specular = 1.0;

    vec3 sun = sky.sunDirection.xyz;
    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative, shape, sun)) + shadeSky(s, sun);
    outColor = vec4(applyFog(color, away, dist, pc.material.z, sun), 1.0);
}
