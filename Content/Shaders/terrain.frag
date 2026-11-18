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
layout(location = 4) in float vBallast; // how much of the ground is a track's ballast run out over it (NetworkLines.BallastAt)
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
// The ballast's rock (GroundTextures.BallastMaps): colour, normal, and ambient occlusion, roughness, metalness and height packed.
layout(set = 0, binding = 17) uniform sampler2D ballastColor;
layout(set = 0, binding = 18) uniform sampler2D ballastNormal;
layout(set = 0, binding = 19) uniform sampler2D ballastPacked;
// fbm (noise.glsl's) that repeats every NoisePeriod cells, two unlike ones in red and green (GroundTextures.BroadNoise).
layout(set = 0, binding = 20) uniform sampler2D broadNoise;
const float NoisePeriod = 32.0;

// fbm for the broad patterns, read rather than worked out: one read for twenty hashes, its mips fading the finer octaves out with
// distance as fbm does by hand.
float broad(vec2 p)
{
    return texture(broadNoise, p / NoisePeriod).r;
}

// The other one, turned, for patterns fine enough that a repeat could line up with the first's.
float broad2(vec2 p)
{
    return texture(broadNoise, mat2(0.8, 0.6, -0.6, 0.8) * p / NoisePeriod).g;
}

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera;
    vec4 sun;
    vec4 texOrigin; // the camera's x, z wrapped to the texture period, exact (see WorldRenderer.TexturePeriod)
} pc;

// How many metres one repeat of each texture covers. Each is sampled again much larger too, so the repeats don't
// line up into a grid.
const float GrassTile = 4.0;
const float EarthTile = 6.0;
const float CliffTile = 9.0;
const float DesertTile = 12.0;
const float BeachTile = 7.0;
const float Second = 5.4; // 27/5: with the 3-4-5 Turn, a whole number of repeats fits WorldRenderer.TexturePeriod, so its wrap leaves no seam

// A material weighing less than this isn't read at all: most of the ground is one or two of them, and these reads are most of its cost.
// The GPU works out each read's blur from the pixels beside it, which may have skipped it, but only where it weighs next to nothing.
// (Giving it the blur with textureGrad instead is safe, but costs all of what skipping saves.)
const float Unused = 0.001;

const mat2 Turn = mat2(0.8, 0.6, -0.6, 0.8);

// A detail map's colour over its average (its last, 1 x 1 mip): around 1, to multiply the land cover's colour by.
vec3 detail(sampler2D map, vec2 uv)
{
    vec3 average = max(textureLod(map, vec2(0.5), 16.0).rgb, vec3(0.01));
    return texture(map, uv).rgb / average;
}

// How much a scale of a texture is worth reading, by how much of its repeat a pixel covers (pixel, in metres): once a pixel is past
// a twelfth of a repeat the GPU reads it from a 16 x 16 mip or smaller, near enough its average (1 for detail, flat for relief),
// so it fades to that and isn't read at all. Most of the ground in a wide view is that far off, and these reads are most of
// what it costs. (Where it's skipped the pixels beside one still reading it are weighted to nothing, as with Unused.)
float worth(float tile, float pixel)
{
    return 1.0 - smoothstep(tile / 32.0, tile / 12.0, pixel);
}

// Two scales of a detail map, the larger one turned so its repeats run a different way.
vec3 detail2(sampler2D map, vec2 p, float tile, float pixel)
{
    vec3 d = vec3(1.0);
    float near = worth(tile, pixel);
    if (near > 0.0)
        d = mix(vec3(1.0), detail(map, p / tile), near);
    float far = worth(tile * Second, pixel);
    if (far > 0.0)
        d *= mix(vec3(1.0), detail(map, Turn * p / (tile * Second)), 0.5 * far);
    return d;
}

// A tangent-space normal map's xy (BC5: the blue is rebuilt from them).
vec2 relief(sampler2D map, vec2 uv)
{
    return texture(map, uv).rg * 2.0 - 1.0;
}

vec2 relief2(sampler2D map, vec2 p, float tile, float pixel)
{
    vec2 sum = vec2(0.0);
    float near = worth(tile, pixel);
    if (near > 0.0)
        sum = relief(map, p / tile) * near;
    float far = worth(tile * Second, pixel);
    if (far > 0.0)
    {
        vec2 large = relief(map, Turn * p / (tile * Second));
        sum += mat2(0.8, -0.6, 0.6, 0.8) * large * (0.35 * far); // back into the unturned frame
    }
    return sum * 0.75;
}

// Open water, which has no maps: smooth, and with nothing to hide the sky.
const vec2 WaterSurface = vec2(1.0, 0.12);
const float GroundSpecular = 0.25;

void main()
{
    // world: where this is, for broad patterns only. Far out the camera's position is coarse as a float (centimetres, 100 km
    // from the origin), which a pattern tens of metres across doesn't show. local: the same place within the texture period,
    // exact to under a millimetre anywhere, for everything finer; the period is a whole number of every texture's repeat, so
    // the wrap leaves no seam in them.
    vec3 world = pc.camera.xyz + vRelative;
    vec3 local = vec3(pc.texOrigin.x, pc.camera.y, pc.texOrigin.z) + vRelative;
    vec2 tex = local.xz;
    float dist = length(vRelative);
    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    vec3 sun = pc.sun.xyz;

    float pixel = max(length(dFdx(vRelative.xz)), length(dFdy(vRelative.xz))) + 1e-4;

    // At the edge of a triangle MSAA can read these from beyond its corners, where they overshoot, by more the smaller it is: +30 of one and
    // -29 of another still add up to 1, but anything left out because it weighs nothing leaves the rest 30 times too bright.
    vec4 m = max(vMaterials, vec4(0.0));
    m /= max(dot(m, vec4(1.0)), 1e-3);

    // Where grass meets earth, whichever stands taller at this spot wins, so the edge follows clumps and stones, not a smooth fade.
    float land = m.x + m.y;
    if (m.x > Unused && m.y > Unused)
    {
        float taller = texture(grassHeight, tex / GrassTile).r - texture(earthHeight, tex / EarthTile).r;
        float grassShare = smoothstep(-0.12, 0.12, (m.x / land - 0.5) * 2.0 + taller * 0.8);
        m.x = land * grassShare;
        m.y = land * (1.0 - grassShare);
    }

    // Bare ground goes to weathered desert rock as it steepens, patchily.
    float desert = clamp(smoothstep(0.985, 0.9, n.y) + (broad(world.xz * 0.012) - 0.55) * 1.2, 0.0, 1.0);

    // Sand along the water's edge, in its own colour.
    float shore = smoothstep(0.02, 0.3, m.z) * (1.0 - smoothstep(0.55, 0.9, m.z)) * step(n.y, 2.0) * smoothstep(0.85, 0.95, n.y);

    // The land cover's colour, varied broadly, times the textures' detail.
    vec3 albedo = vCover.rgb * (0.7 + 0.6 * broad(world.xz * 0.004));

    // grain: their detail. bump: their relief, laid onto the ground's own normal (projected straight down; flat ground mostly faces up).
    // finish: how much sky reaches it, and how rough it is. All in the same proportions, adding up only the materials that are here.
    vec3 grain = vec3(m.z + m.w);
    vec2 bump = vec2(0.0);
    vec2 finish = WaterSurface * (m.z + m.w);
    if (m.x > Unused)
    {
        grain += detail2(grassAlbedo, tex, GrassTile, pixel) * m.x;
        bump += relief2(grassNormal, tex, GrassTile, pixel) * m.x;
        finish += texture(grassSurface, tex / GrassTile).rg * m.x;
    }

    float earth = m.y * (1.0 - desert);
    if (earth > Unused)
    {
        grain += detail2(earthAlbedo, tex, EarthTile, pixel) * earth;
        bump += relief2(earthNormal, tex, EarthTile, pixel) * earth;
        finish += texture(earthSurface, tex / EarthTile).rg * earth;
    }

    float rocky = m.y * desert;
    if (rocky > Unused)
    {
        grain += detail2(desertAlbedo, tex, DesertTile, pixel) * rocky;
        bump += relief2(desertNormal, tex, DesertTile, pixel) * rocky;
        finish += texture(desertSurface, tex / DesertTile).rg * rocky;
    }

    albedo *= grain;

    if (shore > 0.0)
    {
        albedo = mix(albedo, texture(beachAlbedo, tex / BeachTile).rgb, shore);
        bump = mix(bump, relief2(beachNormal, tex, BeachTile, pixel), shore);
        finish = mix(finish, texture(beachSurface, tex / BeachTile).rg, shore);
        m.z *= 1.0 - shore;
    }

    vec3 surface = normalize(vec3(n.x + bump.x, n.y, n.z + bump.y));

    // Tree canopy: dark clumps with lighter crowns.
    if (vCover.a > 0.0)
    {
        float canopy = vCover.a * smoothstep(0.35, 0.6, broad2(world.xz * 0.06));
        albedo = mix(albedo, albedo * vec3(0.55, 0.7, 0.5), canopy * 0.6);
    }

    // Steep ground is cliff, projected from all three sides so it doesn't smear down the slope.
    float rock = smoothstep(0.80, 0.62, n.y);
    if (rock > 0.0)
    {
        vec3 blend = pow(abs(n), vec3(4.0));
        blend /= blend.x + blend.y + blend.z;
        vec3 wx = local;
        // Rock in the colour of the ground it's cut through, its grain softened: a bank in pale desert isn't a black hole.
        vec3 grainy = detail2(cliffAlbedo, wx.zy, CliffTile, pixel) * blend.x + detail2(cliffAlbedo, wx.xz, CliffTile, pixel) * blend.y
            + detail2(cliffAlbedo, wx.xy, CliffTile, pixel) * blend.z;
        vec3 rockColor = mix(vec3(0.17, 0.16, 0.145), vCover.rgb * 0.72, 0.8) * mix(vec3(1.0), grainy, 0.55);
        // Gentler than the ground's: on a slope the relief tilts faces right away from the sun.
        vec2 rx = relief2(cliffNormal, wx.zy, CliffTile, pixel) * 0.6;
        vec2 ry = relief2(cliffNormal, wx.xz, CliffTile, pixel) * 0.6;
        vec2 rz = relief2(cliffNormal, wx.xy, CliffTile, pixel) * 0.6;
        vec3 cliff = normalize(vec3(n.x, rx.y + n.y, rx.x + n.z) * blend.x + vec3(ry.x + n.x, n.y, ry.y + n.z) * blend.y
            + vec3(rz.x + n.x, rz.y + n.y, n.z) * blend.z);
        albedo = mix(albedo, rockColor, rock);
        surface = normalize(mix(surface, cliff, rock));

        vec2 rockFinish = texture(cliffSurface, wx.zy / CliffTile).rg * blend.x + texture(cliffSurface, wx.xz / CliffTile).rg * blend.y
            + texture(cliffSurface, wx.xy / CliffTile).rg * blend.z;
        finish = mix(finish, rockFinish, rock);
    }

    // The track's ballast run out over the ground beside it, laid into the ground's own surface (not a layer over it, which the
    // ground beneath could shade or come up through): all stone at the bed's toe, thinning out to none across its fade, the
    // ground rising between the stones, filling the gaps first and leaving the tallest last, in patches.
    float ballast = clamp(vBallast, 0.0, 1.0) * (1.0 - clamp(m.z + m.w, 0.0, 1.0));
    if (ballast > Unused)
    {
        vec2 bu = tex / 1.5;
        vec4 packedRock = texture(ballastPacked, bu);
        float stones = packedRock.a;
        float drift = fbm(world.xz * 0.6, pixel * 0.6);
        float footprint = max(length(dFdx(bu)), length(dFdy(bu))) * float(textureSize(ballastPacked, 0).x);
        float soft = 0.03 + 0.04 * log2(max(footprint, 1.0));
        float level = mix(1.05, 0.1, ballast) + (0.5 - drift) * 0.5;
        float cover = max(smoothstep(level - soft, level + soft, stones) * smoothstep(0.0, 0.15, ballast), smoothstep(0.85, 1.0, ballast));
        // Crushed rock weathered by dust and oil, dark in the gaps between the stones (as line.frag's rock).
        vec3 rockAlbedo = texture(ballastColor, bu).rgb * vec3(0.46, 0.44, 0.42) * mix(0.45, 1.0, smoothstep(0.25, 0.7, stones));
        vec2 rockBump = (texture(ballastNormal, bu).rg * 2.0 - 1.0) * 1.5;
        albedo = mix(albedo, rockAlbedo, cover);
        surface = normalize(mix(surface, normalize(vec3(n.x + rockBump.x, n.y, n.z + rockBump.y)), cover));
        finish = mix(finish, vec2(0.8 * packedRock.r, packedRock.g), cover);
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
    // (Where puddles would be matters only on wet ground.)
    float puddle = shadow.weather.x > 0.0 ? smoothstep(0.68, 0.76, fbm(world.xz * 0.05, pixel * 0.05)) * smoothstep(0.985, 0.998, n.y) * (1.0 - water) : 0.0;
    weatherSurface(s, puddle, 1.0 - water);

    // Shadow from the ground's own shape: the relief is too fine for the map to resolve, and would only speckle it.
    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative, n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);

    color = applyFog(color, view, dist, pc.camera.y, sun);
    
    outColor = vec4(color, 1.0);
}
