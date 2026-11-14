#version 450
// Depth is tested before shading: nothing here discards (see line.vert), and what's hidden behind nearer ground or track is
// never shaded.
layout(early_fragment_tests) in;
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
    vec4 texOrigin; // the camera's x, z wrapped to the texture period, exact (see WorldRenderer.TexturePeriod)
} pc;

// Not read: the rock's in set 4 now. Only here because the layout's set 0 is.
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
// Each paint's own maps (TrackMaterials.Paints: black, white, galv, yellow, red, green, concrete), then their palette: per paint, its
// colour square-rooted and how strongly its texture shows through on the top row; metal and roughness on the bottom row.
layout(set = 4, binding = 15) uniform sampler2D paint0Color;
layout(set = 4, binding = 16) uniform sampler2D paint0Normal;
layout(set = 4, binding = 17) uniform sampler2D paint0Packed;
layout(set = 4, binding = 18) uniform sampler2D paint1Color;
layout(set = 4, binding = 19) uniform sampler2D paint1Normal;
layout(set = 4, binding = 20) uniform sampler2D paint1Packed;
layout(set = 4, binding = 21) uniform sampler2D paint2Color;
layout(set = 4, binding = 22) uniform sampler2D paint2Normal;
layout(set = 4, binding = 23) uniform sampler2D paint2Packed;
layout(set = 4, binding = 24) uniform sampler2D paint3Color;
layout(set = 4, binding = 25) uniform sampler2D paint3Normal;
layout(set = 4, binding = 26) uniform sampler2D paint3Packed;
layout(set = 4, binding = 27) uniform sampler2D paint4Color;
layout(set = 4, binding = 28) uniform sampler2D paint4Normal;
layout(set = 4, binding = 29) uniform sampler2D paint4Packed;
layout(set = 4, binding = 30) uniform sampler2D paint5Color;
layout(set = 4, binding = 31) uniform sampler2D paint5Normal;
layout(set = 4, binding = 32) uniform sampler2D paint5Packed;
layout(set = 4, binding = 33) uniform sampler2D paint6Color;
layout(set = 4, binding = 34) uniform sampler2D paint6Normal;
layout(set = 4, binding = 35) uniform sampler2D paint6Packed;
layout(set = 4, binding = 36) uniform sampler2D paintPalette;

// A paint's three maps at uv: colour, roughness (from the packed map's green) and the normal map's bump.
void paintMaps(int slot, vec2 uv, out vec3 color, out float rough, out vec3 bump)
{
    if (slot == 0) { color = texture(paint0Color, uv).rgb; rough = texture(paint0Packed, uv).g; bump = texture(paint0Normal, uv).xyz; }
    else if (slot == 1) { color = texture(paint1Color, uv).rgb; rough = texture(paint1Packed, uv).g; bump = texture(paint1Normal, uv).xyz; }
    else if (slot == 2) { color = texture(paint2Color, uv).rgb; rough = texture(paint2Packed, uv).g; bump = texture(paint2Normal, uv).xyz; }
    else if (slot == 3) { color = texture(paint3Color, uv).rgb; rough = texture(paint3Packed, uv).g; bump = texture(paint3Normal, uv).xyz; }
    else if (slot == 4) { color = texture(paint4Color, uv).rgb; rough = texture(paint4Packed, uv).g; bump = texture(paint4Normal, uv).xyz; }
    else if (slot == 5) { color = texture(paint5Color, uv).rgb; rough = texture(paint5Packed, uv).g; bump = texture(paint5Normal, uv).xyz; }
    else { color = texture(paint6Color, uv).rgb; rough = texture(paint6Packed, uv).g; bump = texture(paint6Normal, uv).xyz; }
}

// LineMaterial, and what each packs into vData (see LineVertex).
const int Ballast = 0;
const int Rail = 1;
const int Asphalt = 2;
const int Gravel = 3;
const int Concrete = 4;
const int Painted = 5;
const int BridgeSteel = 6; // a bridge's steel. Style 1: galvanised railing, otherwise painted girders

// NetworkMeshes.Paints: what a placed model's parts are painted. The first seven are in paintPalette (TrackMaterials.Paints); the
// one after them isn't a paint, but the pole wood's map on the coordinates the model brought: v along the grain.
const int WoodPaint = 7;

// A signal's lenses (NetworkMeshes.Paints, after the ballast): red, yellow and green as dark glass, then the same three lit, each
// shining its own colour whatever lights it.
const int LensPaint = 9;
const int LitLensPaint = 12;
// A grade crossing's flashers while it's working (NetworkMeshes.FlashLens): the left lens, then the right, lit red in turn at
// FlashRate flashes a second on the clock in texOrigin.w, each easing on and off as a lamp's filament does (NetworkMeshes.FlashOf).
const int FlashLensPaint = 15;
const float FlashRate = 0.9;

// NetworkMeshes: how far the tie models reach (TieReach), how far apart they lie (TieSpacing, 19.5 in), and half a tie's width.
const float TieReach = 1000.0;
const float TieSpacing = 0.495;
const float TieHalfWidth = 0.114;

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
// A random shift for a corner of the hex grid, a whole tile's worth either way.
vec2 hexShift(ivec2 corner)
{
    vec2 p = vec2(corner);
    return fract(sin(vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)))) * 43758.5453);
}

// Where uv is in a grid of triangles: its three corners, and how near it is to each (the weights add to 1). Each corner is the middle
// of a hexagon about HexSize tiles across, the region where it counts most. (Mikkelsen, "Practical Real-Time Hex-Tiling", 2022.)
const float HexSize = 0.85;
void hexGrid(vec2 uv, out vec3 w, out ivec2 c1, out ivec2 c2, out ivec2 c3)
{
    uv *= 1.7320508 / HexSize;
    vec2 skewed = vec2(uv.x - 0.57735027 * uv.y, 1.15470054 * uv.y);
    ivec2 cell = ivec2(floor(skewed));
    vec3 t = vec3(fract(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;
    float s = t.z < 0.0 ? 1.0 : 0.0;
    float s2 = 2.0 * s - 1.0;
    w = vec3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    c1 = cell + ivec2(int(s), int(s));
    c2 = cell + ivec2(int(s), 1 - int(s));
    c3 = cell + ivec2(1 - int(s), int(s));
}

// Where the rock maps are taken for a place: the three shifts, how near each corner is, and parallax's lift (by the shift that counts
// most here). The rock maps repeat every tile, which the eye picks out along a line, so they're taken three times, each shifted at
// random by a corner of a hex grid, and blended by how near each corner is and by height: the stones of one sit on those of another,
// and no stone comes round again where it'd be seen.
struct Hex { vec2 s1; vec2 s2; vec2 s3; vec3 w; vec2 lift; };

Hex hexAt(vec2 base, vec3 n, vec3 view, float relief, float dist)
{
    Hex h;
    ivec2 c1, c2, c3;
    hexGrid(base, h.w, c1, c2, c3);
    h.s1 = hexShift(c1);
    h.s2 = hexShift(c2);
    h.s3 = hexShift(c3);
    vec2 most = h.w.x >= h.w.y && h.w.x >= h.w.z ? h.s1 : h.w.y >= h.w.z ? h.s2 : h.s3;
    h.lift = parallax(rockPacked, base + most, n, view, relief, 1.5, dist) - (base + most);
    return h;
}

// How much each of the three counts: sharpened towards the nearest corner, and towards whichever's stone stands highest.
vec3 hexWeights(Hex h, vec3 heights)
{
    vec3 k = h.w * h.w * h.w * exp2((heights - max(heights.x, max(heights.y, heights.z))) * 12.0);
    return k / (k.x + k.y + k.z);
}

// The stones' height there, at a mip level.
float hexHeight(Hex h, vec2 base, float lod)
{
    vec3 heights = vec3(textureLod(rockPacked, base + h.s1 + h.lift, lod).a, textureLod(rockPacked, base + h.s2 + h.lift, lod).a,
        textureLod(rockPacked, base + h.s3 + h.lift, lod).a);
    return dot(hexWeights(h, heights), heights);
}

// The ballast's stone at base (tile units), taken as h says.
void rock(vec2 base, Hex h, float weather, inout vec3 n, out vec3 albedo, out float roughness, out float occlusion)
{
    vec2 dx = dFdx(base);
    vec2 dy = dFdy(base);
    vec2 s1 = h.s1, s2 = h.s2, s3 = h.s3, lift = h.lift;
    vec4 p1 = textureGrad(rockPacked, base + s1 + lift, dx, dy);
    vec4 p2 = textureGrad(rockPacked, base + s2 + lift, dx, dy);
    vec4 p3 = textureGrad(rockPacked, base + s3 + lift, dx, dy);
    // Sharpened towards the nearest corner, and towards whichever's stone stands highest.
    vec3 k = hexWeights(h, vec3(p1.a, p2.a, p3.a));

    vec4 maps = p1 * k.x + p2 * k.y + p3 * k.z;
    // The blend's sharp: mostly one copy carries it all. Copies that add under 2% aren't read for colour and bump at all, the
    // rest share their weight, which can't be seen and saves most of the reads.
    vec3 use = step(vec3(0.02), k) * k;
    use /= use.x + use.y + use.z;
    vec3 color = vec3(0.0);
    vec3 bump = vec3(0.0);
    if (use.x > 0.0)
    {
        color += textureGrad(rockColor, base + s1 + lift, dx, dy).rgb * use.x;
        bump += textureGrad(rockNormal, base + s1 + lift, dx, dy).xyz * use.x;
    }
    if (use.y > 0.0)
    {
        color += textureGrad(rockColor, base + s2 + lift, dx, dy).rgb * use.y;
        bump += textureGrad(rockNormal, base + s2 + lift, dx, dy).xyz * use.y;
    }
    if (use.z > 0.0)
    {
        color += textureGrad(rockColor, base + s3 + lift, dx, dy).rgb * use.z;
        bump += textureGrad(rockNormal, base + s3 + lift, dx, dy).xyz * use.z;
    }
    bump = bump * 2.0 - 1.0;

    // Crushed rock weathered by dust and oil.
    albedo = color * vec3(0.46, 0.44, 0.42) * (0.8 + 0.3 * weather);
    // Dark in the gaps between the stones whatever the light, which is what still reads from far off.
    albedo *= mix(0.45, 1.0, smoothstep(0.25, 0.7, maps.a));
    roughness = maps.g;
    occlusion = 0.8 * maps.r;
    bump.xy *= 1.5;
    n = bumped(n, normalize(bump), base);
}

// A material's map seen straight down its strongest axis, so a part with no map of its own can still wear one.
vec2 boxUv(vec3 position, vec3 n)
{
    vec3 a = abs(n);
    return a.y > a.x && a.y > a.z ? position.xz : a.x > a.z ? position.zy : position.xy;
}

void main()
{
    // world for broad patterns, local (exact, wrapped to the texture period) for everything finer: as in terrain.frag.
    vec3 world = pc.camera.xyz + vRelative;
    vec3 local = vec3(pc.texOrigin.x, pc.camera.y, pc.texOrigin.z) + vRelative;
    vec2 tex = local.xz;
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
    float pixel = max(length(dFdx(vRelative)), length(dFdy(vRelative))) + 1e-4;
    float uPixel = max(fwidth(u), 1e-4);


    vec3 albedo = vec3(0.2);
    float metallic = 0.0;
    float roughness = 0.9;
    float specular = 0.1; // loose stone and rough ground hide most of their highlights from a low sun
    float occlusion = 1.0;
    float puddle = 0.0;
    float hold = 1.0;
    vec3 emit = vec3(0.0);
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
        rock(base, hexAt(base, n, view, 0.06, dist), patches, n, albedo, roughness, occlusion);

        int tracks = style & 7;
        bool disused = (style & 8) != 0;
        float railOffset = vData.w;
        float far = smoothstep(nearRadius - 60.0, nearRadius, dist);
        // The tie models stop sooner (NetworkMeshes.TieReach): from there the ties are painted, one by one where they're still
        // far enough apart on screen to tell (they lie every TieSpacing along the line, at the same places as the models), and
        // as their average where they aren't.
        float tieFar = smoothstep(TieReach - 40.0, TieReach, dist);
        float vPixel = max(fwidth(v), 1e-4);
        float apart = abs(fract(v / TieSpacing + 0.5) - 0.5) * TieSpacing;
        float oneByOne = clamp((TieHalfWidth - apart) / vPixel + 0.5, 0.0, 1.0);
        float ties = mix(oneByOne, TieHalfWidth * 2.0 / TieSpacing, smoothstep(0.25, 0.5, vPixel / TieSpacing));
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
                    albedo = paint(albedo, TieWood, stripe(u, o, 1.295, uPixel) * ties * tieFar);
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
        float wear = fbm(world.xz * 0.12, pixel * 0.12);
        albedo = vec3(0.036, 0.037, 0.039) * (0.85 + 0.3 * grit) * (0.85 + 0.3 * wear);
        roughness = mix(0.8, 0.95, grit);
        puddle = smoothstep(0.55, 0.68, fbm(world.xz * 0.12, pixel * 0.12));
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
        fadeShadowDrop = 0.06 * (1.0 - rise);
        vec2 base = (fading ? vec2(u, v) : tex) / 1.5;
        Hex stone = hexAt(base, n, view, 0.06 * smoothstep(0.4, 1.0, rise), dist);
        if (fading)
        {
            // Where it's seen, so that what's left is stones and not the gaps between them.
            // Filtered the same every way: a low view draws a stretched footprint out into streaks.
            float footprint = max(length(dFdx(base)), length(dFdy(base))) * float(textureSize(rockPacked, 0).x);
            float stones = hexHeight(stone, base, log2(max(footprint, 1.0)));
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
        vec2 face = boxUv(local, n);
        bool upright = abs(n.y) < 0.5;
        float streaks = fbm(upright ? vec2(face.x * 1.6, local.y * 0.1) : face * 0.5, pixel * 1.6);
        float stain = fbm(face * 0.5 + local.y * 0.3, pixel * 0.7);
        float joints = upright ? max(stripe(fract(face.x / 3.0) * 3.0, 0.0, 0.012, pixel), stripe(fract(local.y / 2.4) * 2.4, 0.0, 0.01, pixel)) : 0.0;
        albedo = vec3(0.36, 0.34, 0.30) * (0.72 + 0.28 * stain) * (0.72 + 0.42 * streaks) * (1.0 - 0.55 * joints);
        roughness = 0.85;
        specular = 0.25;
    }
    else if (material == BridgeSteel)
    {
        // Pale paint weathered by Metal024; on the girders it's peeled off in patches to the rust beneath.
        vec2 worn = boxUv(local, n) / 1.2;
        vec2 packed = texture(paintedPacked, worn).gb;
        vec3 grime = texture(paintedColor, worn).rgb;
        vec3 painted = (style == 1 ? vec3(0.34, 0.35, 0.35) : vec3(0.50, 0.51, 0.48)) * mix(vec3(1.0), grime * 2.0, 0.35);
        float peeled = style == 1 ? 0.0 : 0.85 * smoothstep(0.66, 0.70, fbm(boxUv(local, n) * 1.8, pixel * 1.8));
        albedo = mix(painted, vec3(0.30, 0.20, 0.12) * (0.6 + grime), peeled);
        roughness = mix(0.55, 0.9, peeled);
        specular = 0.5;
        n = bumped(n, texture(paintedNormal, worn).xyz * 2.0 - 1.0, worn);
    }
    else if (material == Painted && style >= LensPaint)
    {
        // Glass: dark and glossy, colourless until it's lit.
        albedo = vec3(0.012);
        roughness = 0.08;
        specular = 1.0;
        if (style >= FlashLensPaint)
        {
            float phase = fract(pc.texOrigin.w * FlashRate + float(style - FlashLensPaint) * 0.5);
            float on = smoothstep(0.0, 0.06, phase) * (1.0 - smoothstep(0.44, 0.52, phase));
            vec4 lens = texelFetch(paintPalette, ivec2(0, 2), 0);
            emit = lens.rgb * lens.rgb * lens.a * 32.0 * on;
        }
        else if (style >= LitLensPaint)
        {
            // TrackMaterials.Lamps: the lens's colour (square-rooted) and its brightness over TrackTextures.LensScale.
            vec4 lens = texelFetch(paintPalette, ivec2(clamp(style - LitLensPaint, 0, 2), 2), 0);
            emit = lens.rgb * lens.rgb * lens.a * 32.0;
        }
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
        // Galvanised steel, or paint, each weathered by its own texture (TrackMaterials: Metal024 unless changed).
        int slot = clamp(style, 0, 6);
        vec4 tint = texelFetch(paintPalette, ivec2(slot, 0), 0);
        vec4 finish = texelFetch(paintPalette, ivec2(slot, 1), 0);
        vec2 worn = boxUv(local, n) / 0.8;
        vec3 grime, bump;
        float rough;
        paintMaps(slot, worn, grime, rough, bump);
        albedo = tint.rgb * tint.rgb * mix(vec3(1.0), grime * 2.0, tint.a);
        metallic = finish.r;
        roughness = mix(finish.g, rough, 0.6);
        specular = 1.0;
        n = bumped(n, bump * 2.0 - 1.0, worn);
    }

    // Nothing's culled, so light the side we see. Which side that is goes by the triangle itself, from how its position changes
    // across the screen: exact however low the view. Not by the normal, smoothed across the bed's rounded section, which looking
    // along the track far off (or up it, over a crest) can lean a fraction past the line of sight while the top is what's seen,
    // and would turn the whole normal into the ground, where no sky or sun reaches it and the rock goes dark. A bump that still
    // leans away from the eye is bent back to just face it.
    vec3 face = cross(dFdx(vRelative), dFdy(vRelative));
    if (dot(face, view) > 0.0)
        face = -face; // towards the eye
    if (dot(normalize(vNormal), face) < 0.0)
        n = -n;
    float facing = dot(n, view);
    if (facing > -0.05)
        n = normalize(n - view * (facing + 0.05));

    Surface s;
    s.albedo = albedo;
    s.metallic = metallic;
    s.roughness = roughness;
    s.normal = n;
    s.view = -view;
    s.occlusion = occlusion;
    s.specular = specular;
    weatherSurface(s, puddle, hold);

    vec3 color = shadeLight(s, sun, SunColor * sunShadow(vRelative - vec3(0.0, fadeShadowDrop, 0.0), material == Gravel ? shadowNormal : n, sun)) + shadeSky(s, sun) + shadeLocalLights(s, vRelative) + emit;
    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, coverage);
}
