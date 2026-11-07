#version 450
#include "atmosphere.glsl"
#include "lighting.glsl"
#include "noise.glsl"
#include "shadows.glsl"
#include "local_lights.glsl"
#include "weather.glsl"

layout(location = 0) in vec3 vRelative;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vData;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera;
    vec4 sun;
} pc;

// Crushed stone from Tools/Blender/track_parts.py: a seamless tile 1.5 m across.
layout(set = 0, binding = 0) uniform sampler2D ballastMap;

// TrackTextures: colour, normal, and green roughness and blue metalness, for the ballast, the top of a rail, the sides of one
// (Metal025, as the tie plates), the weathering on painted parts (Metal024). The packed map also has ambient
// occlusion in red and height in alpha. Then the wood of the poles (Wood062, at the largest size there is).
layout(set = 4, binding = 0) uniform sampler2D rockColor;
layout(set = 4, binding = 1) uniform sampler2D rockNormal;
layout(set = 4, binding = 2) uniform sampler2D rockPacked;
layout(set = 4, binding = 3) uniform sampler2D railTopColor;
layout(set = 4, binding = 4) uniform sampler2D railTopNormal;
layout(set = 4, binding = 5) uniform sampler2D railTopPacked;
layout(set = 4, binding = 6) uniform sampler2D railSideColor;
layout(set = 4, binding = 7) uniform sampler2D railSideNormal;
layout(set = 4, binding = 8) uniform sampler2D railSidePacked;
layout(set = 4, binding = 9) uniform sampler2D paintedColor;
layout(set = 4, binding = 10) uniform sampler2D paintedNormal;
layout(set = 4, binding = 11) uniform sampler2D paintedPacked;
layout(set = 4, binding = 12) uniform sampler2D woodColor;
layout(set = 4, binding = 13) uniform sampler2D woodNormal;
layout(set = 4, binding = 14) uniform sampler2D woodPacked;

// LineMaterial, and what each packs into vData (see LineVertex).
const int Ballast = 0;
const int Rail = 1;
const int Asphalt = 2;
const int Gravel = 3;
const int Concrete = 4;
const int Painted = 5;
const int BridgeSteel = 6; // a bridge's steel. Style 1: galvanised railing, otherwise painted girders

// NetworkMeshes.Paints: what a placed model's parts are painted.
const vec3 Paints[7] = vec3[](vec3(0.018), vec3(0.62, 0.62, 0.60), vec3(0.26, 0.27, 0.28), vec3(0.60, 0.42, 0.05),
    vec3(0.50, 0.02, 0.015), vec3(0.02, 0.32, 0.12), vec3(0.30, 0.29, 0.26));
// The last of them isn't a paint, but its map on the coordinates the model brought: v along the grain.
const int WoodPaint = 7;

const vec3 Steel = vec3(0.40, 0.40, 0.40);
const vec3 BareSteel = vec3(0.56, 0.57, 0.58);
const vec3 Rust = vec3(0.075, 0.048, 0.032);
const vec3 TieWood = vec3(0.055, 0.045, 0.036);
const vec3 White = vec3(0.55);
const vec3 Yellow = vec3(0.60, 0.42, 0.06);

// How much of a pixel the stripe |x - centre| < halfWidth covers. Stripes thinner than a pixel spread over it, fainter.
float stripe(float x, float centre, float halfWidth, float pixel)
{
    float w = max(pixel, halfWidth * 2.0);
    return clamp((w * 0.5 - abs(x - centre)) / pixel + 0.5, 0.0, 1.0) * (halfWidth * 2.0 / w);
}

// On for 3 m of every 12.
float dashes(float v, float pixel)
{
    float f = fract(v / 12.0) * 12.0;
    return clamp((3.0 - f) / pixel + 0.5, 0.0, 1.0) * clamp(f / pixel + 0.5, 0.0, 1.0);
}

vec3 paint(vec3 base, vec3 color, float amount)
{
    return mix(base, color, clamp(amount, 0.0, 1.0));
}

// The ways a map's u and v run across the surface, worked out from how the position and map change across the pixel, both to the
// same scale (its square is returned). Which way round the pixel's own directions lie decides the sign: screen y runs down.
float mapAxes(vec2 uv, vec3 n, out vec3 t, out vec3 b)
{
    vec3 dpx = dFdx(vRelative);
    vec3 dpy = dFdy(vRelative);
    vec2 dux = dFdx(uv);
    vec2 duy = dFdy(uv);
    vec3 acrossY = cross(dpy, n);
    float side = dot(dpx, acrossY) < 0.0 ? -1.0 : 1.0;
    t = (acrossY * dux.x + cross(n, dpx) * duy.x) * side;
    b = (acrossY * dux.y + cross(n, dpx) * duy.y) * side;
    return max(dot(t, t), dot(b, b));
}

// A normal map's bump. Its green points up its image, which is against the way v runs.
vec3 bumped(vec3 n, vec3 bump, vec2 uv)
{
    vec3 t, b;
    float scale = mapAxes(uv, n, t, b);
    return scale < 1e-20 ? n : normalize(t * bump.x - b * bump.y + n * bump.z * sqrt(scale));
}

// Moves a map's position by what its height does to the line of sight, so what stands proud covers what's behind it. relief: how many
// metres the map's height spans, and tile how many metres it repeats over. Fades out with distance, where it'd only shimmer.
vec2 parallax(sampler2D packed, vec2 uv, vec3 n, vec3 view, float relief, float tile, float dist)
{
    float fade = 1.0 - smoothstep(12.0, 40.0, dist);
    if (fade <= 0.0)
        return uv;

    vec3 t, b;
    float scale = mapAxes(uv, n, t, b);
    if (scale < 1e-20)
        return uv;

    float inverse = 1.0 / sqrt(scale);
    // From the surface towards the eye, along the map's directions. Not too near the grazing, where it'd run away.
    vec3 toEye = -view;
    vec2 across = vec2(dot(toEye, t), dot(toEye, b)) * inverse / max(dot(toEye, n), 0.25);
    vec2 reach = across * relief / tile * fade;

    // Down the line of sight from the top of the relief to where it first goes in under the map's height.
    const int steps = 10;
    float lod = textureQueryLod(packed, uv).x;
    float above = 1.0;
    float gap = 1.0 - textureLod(packed, uv + reach * 0.5, lod).a;
    for (int i = 1; i <= steps; i++)
    {
        float level = 1.0 - float(i) / float(steps);
        float next = level - textureLod(packed, uv + reach * (level - 0.5), lod).a;
        if (next <= 0.0)
            return uv + reach * (mix(above, level, gap / max(gap - next, 1e-4)) - 0.5);
        above = level;
        gap = next;
    }

    return uv - reach * 0.5;
}

// The ballast's rock at where parallax took base to. What's read goes by base's own rate of change, since where stones overlap the
// moved position jumps, and would pick a blurred mip all along each stone's edge.
void rock(vec2 base, vec2 stone, float weather, inout vec3 n, out vec3 albedo, out float roughness, out float occlusion)
{
    vec2 dx = dFdx(base);
    vec2 dy = dFdy(base);
    vec4 maps = textureGrad(rockPacked, stone, dx, dy);
    // Crushed rock weathered by dust and oil: the map on its own is clean river stone.
    albedo = textureGrad(rockColor, stone, dx, dy).rgb * vec3(0.46, 0.44, 0.42) * (0.8 + 0.3 * weather);
    // Dark in the gaps between the stones whatever the light, which is what still reads from far off.
    albedo *= mix(0.45, 1.0, smoothstep(0.25, 0.7, maps.a));
    roughness = maps.g;
    occlusion = 0.8 * maps.r;
    vec3 bump = textureGrad(rockNormal, stone, dx, dy).xyz * 2.0 - 1.0;
    bump.xy *= 1.5;
    n = bumped(n, bump, base);
}

// A material's map seen straight down its strongest axis, so a part with no map of its own can still wear one.
vec2 boxUv(vec3 position, vec3 n)
{
    vec3 a = abs(n);
    return a.y > a.x && a.y > a.z ? position.xz : a.x > a.z ? position.zy : position.xy;
}

void main()
{
    vec3 world = pc.camera.xyz + vRelative;
    // (Not wrapped: nothing here repeats at any round distance, so a wrap would show as a line across the ground.)
    vec2 tex = world.xz;
    float dist = length(vRelative);
    vec3 view = vRelative / max(dist, 1e-3);
    vec3 n = normalize(vNormal);
    vec3 sun = pc.sun.xyz;
    float nearRadius = pc.camera.w;

    int code = int(vData.z + 0.5);
    int material = code & 7;
    int style = code >> 3;
    float u = vData.x;
    float v = vData.y;
    float pixel = max(length(dFdx(world)), length(dFdy(world))) + 1e-4;
    float uPixel = max(fwidth(u), 1e-4);

    if ((material == Rail || material == Painted) && dist > nearRadius)
        discard;

    vec3 albedo = vec3(0.2);
    float metallic = 0.0;
    float roughness = 0.9;
    float specular = 0.1; // loose stone and rough ground hide most of their highlights from a low sun
    float occlusion = 1.0;
    float puddle = 0.0;
    float hold = 1.0;
    float coverage = 1.0; // how much of the ground underneath it covers, where it fades into it
    vec3 shadowNormal = n;
    // The fade asks for its shadow from the ground it lies on, a little more the further out, so that where it meets the bed there's no step.
    float fadeShadowDrop = 0.0;

    if (material == Ballast)
    {
        float patches = fbm(tex * 0.35, pixel * 0.35);
        // Along the track and across it as the surface runs, so the shoulder's stones aren't drawn out down its slope.
        vec3 shape = n;
        vec2 base = vec2(u, v) / 1.5;
        rock(base, parallax(rockPacked, base, n, view, 0.06, 1.5, dist), patches, n, albedo, roughness, occlusion);

        int tracks = style & 7;
        bool disused = (style & 8) != 0;
        float railOffset = vData.w;
        float far = smoothstep(nearRadius - 60.0, nearRadius, dist);
        if (shape.y > 0.95)
        {
            for (int k = 0; k < tracks; k++)
            {
                float o = (float(k) - float(tracks - 1) * 0.5) * 4.1;
                // Oil and brake dust under the track.
                float under = 1.0 - smoothstep(0.9, 1.5, abs(u - o));
                albedo *= mix(1.0, 0.55, under * (disused ? 0.2 : 1.0));
                if (!disused)
                {
                    // Past the real rails and ties the track is painted on: the ties as a band (they cover 46% of
                    // it) and the rails as lines.
                    albedo = paint(albedo, TieWood, stripe(u, o, 1.295, uPixel) * 0.46 * far);
                    float rails = stripe(u, o - railOffset, 0.037, uPixel) + stripe(u, o + railOffset, 0.037, uPixel);
                    albedo = paint(albedo, Steel * 0.8, rails * far);
                }
            }
        }

        if (disused)
            albedo = mix(albedo, vec3(0.07, 0.10, 0.04), 0.55 * smoothstep(0.3, 0.7, fbm(tex * 0.8, pixel * 0.8)));
    }
    else if (material == Rail)
    {
        float height = vData.w;
        float worn = smoothstep(0.170, 0.180, height) * smoothstep(0.5, 0.9, n.y);
        // The top wears Metal021 and the rest Metal025, running along the rail so its streaks do too.
        vec2 along = vec2(v, u + height) / 0.6;
        vec2 topPacked = texture(railTopPacked, along).gb;
        vec2 sidePacked = texture(railSidePacked, along).gb;
        vec3 bump = mix(texture(railSideNormal, along).xyz, mix(texture(railTopNormal, along).xyz, vec3(0.5, 0.5, 1.0), 0.7), worn) * 2.0 - 1.0;
        n = bumped(n, bump, along);
        // The railhead is polished bare steel and the rest rust, which isn't a metal to look at. Past a few pixels a rail is too
        // thin for a sharp highlight to do anything but sparkle.
        // The top's polished by wheels, brighter than any map of a sheet of steel; the sides are more rust than metal.
        albedo = mix(texture(railSideColor, along).rgb, mix(texture(railTopColor, along).rgb, BareSteel, 0.65), worn);
        metallic = mix(sidePacked.y * 0.5, 1.0, worn);
        hold = 1.0 - worn;
        roughness = max(mix(sidePacked.x, topPacked.x * 0.45, worn), min(pixel * 2.0, 0.6));
        specular = 1.0;
    }
    else if (material == Asphalt)
    {
        float grit = fbm(tex * 6.0, pixel * 6.0);
        float wear = fbm(tex * 0.12, pixel * 0.12);
        albedo = vec3(0.036, 0.037, 0.039) * (0.85 + 0.3 * grit) * (0.85 + 0.3 * wear);
        roughness = mix(0.8, 0.95, grit);
        puddle = smoothstep(0.55, 0.68, fbm(tex * 0.12, pixel * 0.12));
        specular = 0.18;

        int lanes = style & 7;
        bool oneway = (style & 8) != 0;
        int marks = (style >> 4) & 3;
        float halfWidth = float(style >> 6) * 0.125;
        // Markings stop short of junctions, where the roads' surfaces overlap.
        float show = smoothstep(halfWidth + 1.0, halfWidth + 4.0, vData.w) * (0.75 + 0.25 * wear);

        if (marks == 1 && !oneway)
        {
            albedo = paint(albedo, Yellow, stripe(u, 0.0, 0.06, uPixel) * dashes(v, pixel) * show);
        }
        else if (marks >= 2)
        {
            float inner = marks == 3 ? 1.2 : 0.5;
            float outer = marks == 3 ? 3.0 : 0.5;
            float left = -halfWidth + (oneway ? inner : outer);
            float right = halfWidth - outer;
            albedo = paint(albedo, oneway ? Yellow : White, stripe(u, left, 0.075, uPixel) * show);
            albedo = paint(albedo, White, stripe(u, right, 0.075, uPixel) * show);
            if (oneway)
            {
                for (int k = 1; k < lanes; k++)
                    albedo = paint(albedo, White, stripe(u, mix(left, right, float(k) / float(lanes)), 0.075, uPixel) * dashes(v, pixel) * show);
            }
            else
            {
                albedo = paint(albedo, Yellow, (stripe(u, -0.15, 0.06, uPixel) + stripe(u, 0.15, 0.06, uPixel)) * show);
                int each = max(lanes / 2, 1);
                for (int k = 1; k < each; k++)
                {
                    float t = float(k) / float(each);
                    albedo = paint(albedo, White, stripe(u, right * t, 0.075, uPixel) * dashes(v, pixel) * show);
                    albedo = paint(albedo, White, stripe(u, left * t, 0.075, uPixel) * dashes(v, pixel) * show);
                }
            }
        }
    }
    else if (material == Gravel)
    {
        // The ballast's rock run on: the edge of the bed, road shoulders, unpaved roads and the marks where track is to go.
        // Data.w below zero is how far in from where it gives out: it fades into the ground, the tallest stones last, in patches.
        // There the stones run on from the bed's, and lie flatter the further out, where a stone's relief would only smear what's kept.
        bool fading = vData.w < 0.0;
        // (Data.w starts from -0.3 at the outer edge, so multisampling reading it a little past the edge still finds it fading.)
        float rise = fading ? clamp((-vData.w - 0.3) / 2.4, 0.0, 1.0) : 1.0;
        // (Style 1 lies on the bank itself, where it would shade itself from the ground just under it.)
        fadeShadowDrop = style == 1 ? -0.4 : 0.06 * (1.0 - rise);
        vec2 base = (fading ? vec2(u, v) : tex) / 1.5;
        vec2 stone = parallax(rockPacked, base, n, view, 0.06 * smoothstep(0.4, 1.0, rise), 1.5, dist);
        if (fading)
        {
            // Where it's seen, so that what's left is stones and not the gaps between them.
            // Filtered the same every way: a low view draws a stretched footprint out into streaks.
            float footprint = max(length(dFdx(base)), length(dFdy(base))) * float(textureSize(rockPacked, 0).x);
            float stones = textureLod(rockPacked, stone, log2(max(footprint, 1.0))).a;
            float drift = fbm(tex * 0.9, pixel * 0.9);
            // The ground rises between the stones, filling the gaps first and leaving the tallest last. A stone's either there or
            // not, never see-through, until it's too far off to make out one.
            float ground = mix(1.05, 0.1, rise) + (0.5 - drift) * 0.5;
            float soft = 0.03 + 0.04 * log2(max(footprint, 1.0));
            coverage = smoothstep(ground - soft, ground + soft, stones) * smoothstep(0.0, 0.15, rise);
        }

        float patches = fbm(tex * 0.35, pixel * 0.35);
        rock(base, stone, patches, n, albedo, roughness, occlusion);
    }
    else if (material == Concrete)
    {
        // Cast in panels, with the joints showing, and streaked by the rain running down it.
        vec2 face = boxUv(world, n);
        bool upright = abs(n.y) < 0.5;
        float streaks = fbm(upright ? vec2(face.x * 1.6, world.y * 0.1) : face * 0.5, pixel * 1.6);
        float stain = fbm(face * 0.5 + world.y * 0.3, pixel * 0.7);
        float joints = upright ? max(stripe(fract(face.x / 3.0) * 3.0, 0.0, 0.012, pixel), stripe(fract(world.y / 2.4) * 2.4, 0.0, 0.01, pixel)) : 0.0;
        albedo = vec3(0.36, 0.34, 0.30) * (0.72 + 0.28 * stain) * (0.72 + 0.42 * streaks) * (1.0 - 0.55 * joints);
        roughness = 0.85;
        specular = 0.25;
    }
    else if (material == BridgeSteel)
    {
        // Pale paint weathered by Metal024; on the girders it's peeled off in patches to the rust beneath.
        vec2 worn = boxUv(world, n) / 1.2;
        vec2 packed = texture(paintedPacked, worn).gb;
        vec3 grime = texture(paintedColor, worn).rgb;
        vec3 painted = (style == 1 ? vec3(0.34, 0.35, 0.35) : vec3(0.50, 0.51, 0.48)) * mix(vec3(1.0), grime * 2.0, 0.35);
        float peeled = style == 1 ? 0.0 : 0.85 * smoothstep(0.66, 0.70, fbm(boxUv(world, n) * 1.8, pixel * 1.8));
        albedo = mix(painted, vec3(0.30, 0.20, 0.12) * (0.6 + grime), peeled);
        roughness = mix(0.55, 0.9, peeled);
        specular = 0.5;
        n = bumped(n, texture(paintedNormal, worn).xyz * 2.0 - 1.0, worn);
    }
    else if (material == Painted && style == WoodPaint)
    {
        // The map's grain runs across it, and it repeats every 1.5 m.
        vec2 grain = vec2(v, u) / 1.5;
        vec2 packed = texture(woodPacked, grain).rg;
        albedo = texture(woodColor, grain).rgb;
        occlusion = packed.x;
        roughness = packed.y;
        specular = 0.4;
        n = bumped(n, texture(woodNormal, grain).xyz * 2.0 - 1.0, grain);
    }
    else if (material == Painted)
    {
        albedo = Paints[clamp(style, 0, 6)];
        // Galvanised steel, or paint, either weathered by Metal024.
        vec2 worn = boxUv(world, n) / 0.8;
        vec2 packed = texture(paintedPacked, worn).gb;
        vec3 grime = texture(paintedColor, worn).rgb;
        albedo *= mix(vec3(1.0), grime * 2.0, style == 2 ? 0.8 : 0.3);
        metallic = style == 2 ? 1.0 : 0.0;
        roughness = mix(style == 2 ? 0.5 : 0.45, packed.x, 0.6);
        specular = 1.0;
        n = bumped(n, texture(paintedNormal, worn).xyz * 2.0 - 1.0, worn);
    }

    if (dot(n, view) > 0.0)
        n = -n; // nothing's culled, so light the side we see

    Surface s;
    s.albedo = albedo;
    s.metallic = metallic;
    s.roughness = roughness;
    s.normal = n;
    s.view = -view;
    s.occlusion = occlusion;
    s.specular = specular;
    weatherSurface(s, puddle, hold);

    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative - vec3(0.0, fadeShadowDrop, 0.0), material == Gravel ? shadowNormal : n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative);
    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, coverage);
}
