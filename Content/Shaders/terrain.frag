#version 450
#include "atmosphere.glsl"
#include "noise.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vCover;
layout(location = 3) in vec4 vMaterials; // grass, bare earth, water
layout(location = 0) out vec4 outColor;

// GroundTextures, in its order.
layout(set = 0, binding = 0) uniform sampler2D grassAlbedo;
layout(set = 0, binding = 1) uniform sampler2D grassNormal;
layout(set = 0, binding = 2) uniform sampler2D earthAlbedo;
layout(set = 0, binding = 3) uniform sampler2D earthNormal;
layout(set = 0, binding = 4) uniform sampler2D cliffAlbedo;
layout(set = 0, binding = 5) uniform sampler2D cliffNormal;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera;
    vec4 sun;
} pc;

// How many metres one repeat of each texture covers. Each is sampled again much larger too, so the repeats don't
// line up into a grid.
const float GrassTile = 4.0;
const float EarthTile = 6.0;
const float CliffTile = 9.0;
const float Second = 5.37;

// A detail map's colour over its average (its last, 1 x 1 mip): around 1, to multiply the land cover's colour by.
vec3 detail(sampler2D map, vec2 uv)
{
    vec3 average = max(textureLod(map, vec2(0.5), 16.0).rgb, vec3(0.01));
    return texture(map, uv).rgb / average;
}

// Two scales of a detail map, the larger one turned so its repeats run a different way.
vec3 detail2(sampler2D map, vec2 p, float tile)
{
    vec2 turned = mat2(0.8, 0.6, -0.6, 0.8) * p;
    return detail(map, p / tile) * mix(vec3(1.0), detail(map, turned / (tile * Second)), 0.5);
}

// A tangent-space normal map's xy (BC5: the blue is rebuilt from them).
vec2 relief(sampler2D map, vec2 uv)
{
    return texture(map, uv).rg * 2.0 - 1.0;
}

vec2 relief2(sampler2D map, vec2 p, float tile)
{
    vec2 turned = mat2(0.8, 0.6, -0.6, 0.8) * p;
    vec2 large = relief(map, turned / (tile * Second));
    large = mat2(0.8, -0.6, 0.6, 0.8) * large; // back into the unturned frame
    return (relief(map, p / tile) + large * 0.35) * 0.75;
}

void main()
{
    vec3 world = pc.camera.xyz + vRelative;
    // Texture coordinates, wrapped every 4 km so they keep their precision on big routes.
    vec2 tex = mod(world.xz, 4096.0);
    float dist = length(vRelative);
    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    vec3 sun = pc.sun.xyz;

    float pixel = max(length(dFdx(world.xz)), length(dFdy(world.xz))) + 1e-4;

    vec4 m = vMaterials / max(dot(vMaterials, vec4(1.0)), 1e-3);

    // The land cover's colour, varied broadly, times the textures' detail.
    vec3 albedo = vCover.rgb * (0.7 + 0.6 * fbm(tex * 0.004, pixel * 0.004));
    vec3 grain = detail2(grassAlbedo, tex, GrassTile) * m.x + detail2(earthAlbedo, tex, EarthTile) * m.y + vec3(m.z + m.w);
    albedo *= grain;

    // Their relief, laid onto the ground's own normal (projected straight down; flat ground mostly faces up).
    vec2 bump = relief2(grassNormal, tex, GrassTile) * m.x + relief2(earthNormal, tex, EarthTile) * m.y;
    vec3 surface = normalize(vec3(n.x + bump.x, n.y, n.z + bump.y));

    // Tree canopy: dark clumps with lighter crowns.
    float canopy = vCover.a * smoothstep(0.35, 0.6, fbm(tex * 0.06, pixel * 0.06));
    albedo = mix(albedo, albedo * vec3(0.55, 0.7, 0.5), canopy * 0.6);

    // Steep ground is cliff, projected from all three sides so it doesn't smear down the slope.
    float rock = smoothstep(0.80, 0.62, n.y);
    if (rock > 0.0)
    {
        vec3 blend = pow(abs(n), vec3(4.0));
        blend /= blend.x + blend.y + blend.z;
        vec3 wx = mod(world, 4096.0);
        vec3 rockColor = vec3(0.17, 0.16, 0.145) * (detail2(cliffAlbedo, wx.zy, CliffTile) * blend.x
            + detail2(cliffAlbedo, wx.xz, CliffTile) * blend.y + detail2(cliffAlbedo, wx.xy, CliffTile) * blend.z);
        // Gentler than the ground's: on a slope the relief tilts faces right away from the sun.
        vec2 rx = relief2(cliffNormal, wx.zy, CliffTile) * 0.6;
        vec2 ry = relief2(cliffNormal, wx.xz, CliffTile) * 0.6;
        vec2 rz = relief2(cliffNormal, wx.xy, CliffTile) * 0.6;
        vec3 cliff = normalize(vec3(n.x, rx.y + n.y, rx.x + n.z) * blend.x + vec3(ry.x + n.x, n.y, ry.y + n.z) * blend.y
            + vec3(rz.x + n.x, rz.y + n.y, n.z) * blend.z);
        albedo = mix(albedo, rockColor, rock);
        surface = normalize(mix(surface, cliff, rock));
    }

    float direct = max(dot(surface, sun), 0.0);
    vec3 color = albedo * (SunColor * direct + skyAmbient(surface));

    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, 1.0);
}
