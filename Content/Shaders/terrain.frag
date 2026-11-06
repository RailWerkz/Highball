#version 450
#include "atmosphere.glsl"
#include "noise.glsl"
#include "lighting.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"

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
layout(set = 0, binding = 6) uniform sampler2D grassSurface; // ambient occlusion in red, roughness in green
layout(set = 0, binding = 7) uniform sampler2D earthSurface;
layout(set = 0, binding = 8) uniform sampler2D cliffSurface;
layout(set = 0, binding = 9) uniform sampler2D desertAlbedo;
layout(set = 0, binding = 10) uniform sampler2D desertNormal;
layout(set = 0, binding = 11) uniform sampler2D desertSurface;
layout(set = 0, binding = 12) uniform sampler2D beachAlbedo;
layout(set = 0, binding = 13) uniform sampler2D beachNormal;
layout(set = 0, binding = 14) uniform sampler2D beachSurface;
layout(set = 0, binding = 15) uniform sampler2D grassHeight;
layout(set = 0, binding = 16) uniform sampler2D earthHeight;

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
const float DesertTile = 12.0;
const float BeachTile = 7.0;
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

// Open water, which has no maps: smooth, and with nothing to hide the sky.
const vec2 WaterSurface = vec2(1.0, 0.12);
const float GroundSpecular = 0.25;

void main()
{
    vec3 world = pc.camera.xyz + vRelative;
    // (Not wrapped: nothing here repeats at any round distance, so a wrap would show as a line across the ground.)
    vec2 tex = world.xz;
    float dist = length(vRelative);
    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    vec3 sun = pc.sun.xyz;

    float pixel = max(length(dFdx(world.xz)), length(dFdy(world.xz))) + 1e-4;

    vec4 m = vMaterials / max(dot(vMaterials, vec4(1.0)), 1e-3);

    // Where grass meets earth, whichever stands taller at this spot wins, so the edge follows clumps and stones, not a smooth fade.
    float land = m.x + m.y;
    if (m.x > 0.001 && m.y > 0.001)
    {
        float taller = texture(grassHeight, tex / GrassTile).r - texture(earthHeight, tex / EarthTile).r;
        float grassShare = smoothstep(-0.12, 0.12, (m.x / land - 0.5) * 2.0 + taller * 0.8);
        m.x = land * grassShare;
        m.y = land * (1.0 - grassShare);
    }

    // Bare ground goes to weathered desert rock as it steepens, patchily.
    float desert = clamp(smoothstep(0.985, 0.9, n.y) + (fbm(tex * 0.012, pixel * 0.012) - 0.55) * 1.2, 0.0, 1.0);

    // Sand along the water's edge, in its own colour.
    float shore = smoothstep(0.02, 0.3, m.z) * (1.0 - smoothstep(0.55, 0.9, m.z)) * step(n.y, 2.0) * smoothstep(0.85, 0.95, n.y);

    // The land cover's colour, varied broadly, times the textures' detail.
    vec3 albedo = vCover.rgb * (0.7 + 0.6 * fbm(tex * 0.004, pixel * 0.004));
    vec3 earthGrain = mix(detail2(earthAlbedo, tex, EarthTile), detail2(desertAlbedo, tex, DesertTile), desert);
    vec3 grain = detail2(grassAlbedo, tex, GrassTile) * m.x + earthGrain * m.y + vec3(m.z + m.w);
    albedo *= grain;

    // Their relief, laid onto the ground's own normal (projected straight down; flat ground mostly faces up).
    vec2 earthBump = mix(relief2(earthNormal, tex, EarthTile), relief2(desertNormal, tex, DesertTile), desert);
    vec2 bump = relief2(grassNormal, tex, GrassTile) * m.x + earthBump * m.y;

    // How much sky reaches it, and how rough it is, in the same proportions.
    vec2 earthFinish = mix(texture(earthSurface, tex / EarthTile).rg, texture(desertSurface, tex / DesertTile).rg, desert);
    vec2 finish = texture(grassSurface, tex / GrassTile).rg * m.x + earthFinish * m.y + WaterSurface * (m.z + m.w);

    if (shore > 0.0)
    {
        albedo = mix(albedo, texture(beachAlbedo, tex / BeachTile).rgb, shore);
        bump = mix(bump, relief2(beachNormal, tex, BeachTile), shore);
        finish = mix(finish, texture(beachSurface, tex / BeachTile).rg, shore);
        m.z *= 1.0 - shore;
    }

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
        vec3 wx = world;
        // Rock in the colour of the ground it's cut through, its grain softened: a bank in pale desert isn't a black hole.
        vec3 grainy = detail2(cliffAlbedo, wx.zy, CliffTile) * blend.x + detail2(cliffAlbedo, wx.xz, CliffTile) * blend.y
            + detail2(cliffAlbedo, wx.xy, CliffTile) * blend.z;
        vec3 rockColor = mix(vec3(0.17, 0.16, 0.145), vCover.rgb * 0.72, 0.8) * mix(vec3(1.0), grainy, 0.55);
        // Gentler than the ground's: on a slope the relief tilts faces right away from the sun.
        vec2 rx = relief2(cliffNormal, wx.zy, CliffTile) * 0.6;
        vec2 ry = relief2(cliffNormal, wx.xz, CliffTile) * 0.6;
        vec2 rz = relief2(cliffNormal, wx.xy, CliffTile) * 0.6;
        vec3 cliff = normalize(vec3(n.x, rx.y + n.y, rx.x + n.z) * blend.x + vec3(ry.x + n.x, n.y, ry.y + n.z) * blend.y
            + vec3(rz.x + n.x, rz.y + n.y, n.z) * blend.z);
        albedo = mix(albedo, rockColor, rock);
        surface = normalize(mix(surface, cliff, rock));

        vec2 rockFinish = texture(cliffSurface, wx.zy / CliffTile).rg * blend.x + texture(cliffSurface, wx.xz / CliffTile).rg * blend.y
            + texture(cliffSurface, wx.xy / CliffTile).rg * blend.z;
        finish = mix(finish, rockFinish, rock);
    }

    Surface s;
    s.albedo = albedo;
    s.metallic = 0.0;
    s.roughness = finish.y;
    s.normal = surface;
    s.view = -view;
    s.occlusion = finish.x;
    // Soil and grass at this scale are far bumpier than their maps say; water isn't.
    s.specular = mix(GroundSpecular, 1.0, clamp(m.z + m.w, 0.0, 1.0));
    float water = clamp(m.z + m.w, 0.0, 1.0);
    weatherSurface(s, smoothstep(0.68, 0.76, fbm(tex * 0.05, pixel * 0.05)) * smoothstep(0.985, 0.998, n.y) * (1.0 - water), 1.0 - water);

    // Shadow from the ground's own shape: the relief is too fine for the map to resolve, and would only speckle it.
    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative, n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);

    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, 1.0);
}
