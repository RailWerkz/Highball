#version 460
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_nonuniform_qualifier : require
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) flat in uint vDraw;
layout(location = 0) out vec4 outColor;

// Every model texture (MaterialTable); each draw's material says which are its own.
layout(set = 0, binding = 0) uniform sampler2D textures[];

// A draw's material: its textures' slots (albedo; metallic-roughness, glTF's green roughness and blue metalness; normal; occlusion,
// red how much of the sky reaches it), its colour, and x: metallic, y: roughness, z: alpha cutoff (below 0, see-through glass),
// w: 1 if it has a normal map.
struct Material
{
    uint albedo, metalRough, normal, occlusion;
    vec4 baseColor;
    vec4 factors;
};

layout(buffer_reference, scalar) readonly buffer Materials { Material m[]; };

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    Materials materials;
    uint base;
    float cameraHeight;
} pc;

// A normal map's bump, with the surface's directions along the map worked out from how the position and map change across the
// pixel, so models needn't carry tangents, and mirrored parts of a map come out right.
vec3 mapped(vec3 n, vec3 position, vec2 uv, uint normalMap)
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

    vec3 bump = texture(textures[nonuniformEXT(normalMap)], uv).xyz * 2.0 - 1.0;
    // A map's green points up its image, which is against the way glTF's v runs.
    return normalize(t * bump.x - b * bump.y + n * bump.z * sqrt(scale));
}

void main()
{
    Material mat = pc.materials.m[vDraw];
    vec4 albedo = texture(textures[nonuniformEXT(mat.albedo)], vUv) * mat.baseColor;
    if (albedo.a < mat.factors.z)
        discard;

    float dist = length(vRelative);
    vec3 away = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    if (dot(n, away) > 0.0)
        n = -n; // nothing's culled, so light the side we see
    vec3 shape = n;
    if (mat.factors.w > 0.5)
        n = mapped(n, vRelative, vUv, mat.normal);

    vec2 map = texture(textures[nonuniformEXT(mat.metalRough)], vUv).gb;
    Surface s;
    s.albedo = albedo.rgb;
    s.metallic = clamp(mat.factors.x * map.y, 0.0, 1.0);
    s.roughness = clamp(mat.factors.y * map.x, 0.0, 1.0);
    s.normal = n;
    s.view = -away;
    s.occlusion = texture(textures[nonuniformEXT(mat.occlusion)], vUv).r;
    s.specular = 1.0;
    weatherSurface(s, 0.0, 1.0);

    vec3 sun = sky.sunDirection.xyz;
    vec3 sunlight = SunColor * sunShadow(vRelative, shape, sun);
    vec3 color = shadeLight(s, sun, sunlight) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);
    if (mat.factors.z >= 0.0)
    {
        outColor = vec4(applyFog(color, away, dist, pc.cameraHeight, sun), 1.0);
        return;
    }

    // See-through: the alpha is how much of the light its own colour stops; what it reflects is on top of that, whole, and
    // more of it the flatter it's seen (Fresnel), as glass goes to a mirror at a glancing look. The blend is this plus what's
    // behind times the alpha out, so the colour goes out already weighted and the alpha is how much of behind shows.
    Surface d = s;
    d.specular = 0.0;
    vec3 diffuse = shadeLight(d, sun, sunlight) + shadeSky(d, sun) + shadeLocalLights(d, vRelative);
    vec3 reflected = max(color - diffuse, vec3(0.0));
    float glance = pow(1.0 - clamp(dot(n, -away), 0.0, 1.0), 5.0);
    float opacity = mix(albedo.a, 1.0, glance);
    vec3 seen = diffuse * opacity + reflected;
    // Its own light fogged as anything's is; the haze in front only as far as it hides what's behind (which has its own).
    vec3 haze = applyFog(vec3(0.0), away, dist, pc.cameraHeight, sun);
    vec3 fogged = applyFog(seen, away, dist, pc.cameraHeight, sun) - haze * (1.0 - opacity);
    outColor = vec4(fogged, 1.0 - opacity);
}
