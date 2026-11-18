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
// The paved surfaces' textures in use (SurfaceTextures.Bound): sixteen, three maps each, the roads' asphalt first; then which
// slot each texture's in, by its number.
layout(set = 4, binding = 37) uniform sampler2D surface0Color;
layout(set = 4, binding = 38) uniform sampler2D surface0Normal;
layout(set = 4, binding = 39) uniform sampler2D surface0Packed;
layout(set = 4, binding = 40) uniform sampler2D surface1Color;
layout(set = 4, binding = 41) uniform sampler2D surface1Normal;
layout(set = 4, binding = 42) uniform sampler2D surface1Packed;
layout(set = 4, binding = 43) uniform sampler2D surface2Color;
layout(set = 4, binding = 44) uniform sampler2D surface2Normal;
layout(set = 4, binding = 45) uniform sampler2D surface2Packed;
layout(set = 4, binding = 46) uniform sampler2D surface3Color;
layout(set = 4, binding = 47) uniform sampler2D surface3Normal;
layout(set = 4, binding = 48) uniform sampler2D surface3Packed;
layout(set = 4, binding = 49) uniform sampler2D surface4Color;
layout(set = 4, binding = 50) uniform sampler2D surface4Normal;
layout(set = 4, binding = 51) uniform sampler2D surface4Packed;
layout(set = 4, binding = 52) uniform sampler2D surface5Color;
layout(set = 4, binding = 53) uniform sampler2D surface5Normal;
layout(set = 4, binding = 54) uniform sampler2D surface5Packed;
layout(set = 4, binding = 55) uniform sampler2D surface6Color;
layout(set = 4, binding = 56) uniform sampler2D surface6Normal;
layout(set = 4, binding = 57) uniform sampler2D surface6Packed;
layout(set = 4, binding = 58) uniform sampler2D surface7Color;
layout(set = 4, binding = 59) uniform sampler2D surface7Normal;
layout(set = 4, binding = 60) uniform sampler2D surface7Packed;
layout(set = 4, binding = 61) uniform sampler2D surface8Color;
layout(set = 4, binding = 62) uniform sampler2D surface8Normal;
layout(set = 4, binding = 63) uniform sampler2D surface8Packed;
layout(set = 4, binding = 64) uniform sampler2D surface9Color;
layout(set = 4, binding = 65) uniform sampler2D surface9Normal;
layout(set = 4, binding = 66) uniform sampler2D surface9Packed;
layout(set = 4, binding = 67) uniform sampler2D surface10Color;
layout(set = 4, binding = 68) uniform sampler2D surface10Normal;
layout(set = 4, binding = 69) uniform sampler2D surface10Packed;
layout(set = 4, binding = 70) uniform sampler2D surface11Color;
layout(set = 4, binding = 71) uniform sampler2D surface11Normal;
layout(set = 4, binding = 72) uniform sampler2D surface11Packed;
layout(set = 4, binding = 73) uniform sampler2D surface12Color;
layout(set = 4, binding = 74) uniform sampler2D surface12Normal;
layout(set = 4, binding = 75) uniform sampler2D surface12Packed;
layout(set = 4, binding = 76) uniform sampler2D surface13Color;
layout(set = 4, binding = 77) uniform sampler2D surface13Normal;
layout(set = 4, binding = 78) uniform sampler2D surface13Packed;
layout(set = 4, binding = 79) uniform sampler2D surface14Color;
layout(set = 4, binding = 80) uniform sampler2D surface14Normal;
layout(set = 4, binding = 81) uniform sampler2D surface14Packed;
layout(set = 4, binding = 82) uniform sampler2D surface15Color;
layout(set = 4, binding = 83) uniform sampler2D surface15Normal;
layout(set = 4, binding = 84) uniform sampler2D surface15Packed;
layout(set = 4, binding = 85) uniform sampler2D surfaceLookup;

// A surface texture's three maps, by slot. (With the derivatives worked out beforehand, outside the branches.)
void surfaceMaps(int slot, vec2 uv, vec2 gx, vec2 gy, out vec3 color, out vec3 normal, out vec4 packed)
{
    if (slot == 0) { color = textureGrad(surface0Color, uv, gx, gy).rgb; normal = textureGrad(surface0Normal, uv, gx, gy).xyz; packed = textureGrad(surface0Packed, uv, gx, gy); }
    else if (slot == 1) { color = textureGrad(surface1Color, uv, gx, gy).rgb; normal = textureGrad(surface1Normal, uv, gx, gy).xyz; packed = textureGrad(surface1Packed, uv, gx, gy); }
    else if (slot == 2) { color = textureGrad(surface2Color, uv, gx, gy).rgb; normal = textureGrad(surface2Normal, uv, gx, gy).xyz; packed = textureGrad(surface2Packed, uv, gx, gy); }
    else if (slot == 3) { color = textureGrad(surface3Color, uv, gx, gy).rgb; normal = textureGrad(surface3Normal, uv, gx, gy).xyz; packed = textureGrad(surface3Packed, uv, gx, gy); }
    else if (slot == 4) { color = textureGrad(surface4Color, uv, gx, gy).rgb; normal = textureGrad(surface4Normal, uv, gx, gy).xyz; packed = textureGrad(surface4Packed, uv, gx, gy); }
    else if (slot == 5) { color = textureGrad(surface5Color, uv, gx, gy).rgb; normal = textureGrad(surface5Normal, uv, gx, gy).xyz; packed = textureGrad(surface5Packed, uv, gx, gy); }
    else if (slot == 6) { color = textureGrad(surface6Color, uv, gx, gy).rgb; normal = textureGrad(surface6Normal, uv, gx, gy).xyz; packed = textureGrad(surface6Packed, uv, gx, gy); }
    else if (slot == 7) { color = textureGrad(surface7Color, uv, gx, gy).rgb; normal = textureGrad(surface7Normal, uv, gx, gy).xyz; packed = textureGrad(surface7Packed, uv, gx, gy); }
    else if (slot == 8) { color = textureGrad(surface8Color, uv, gx, gy).rgb; normal = textureGrad(surface8Normal, uv, gx, gy).xyz; packed = textureGrad(surface8Packed, uv, gx, gy); }
    else if (slot == 9) { color = textureGrad(surface9Color, uv, gx, gy).rgb; normal = textureGrad(surface9Normal, uv, gx, gy).xyz; packed = textureGrad(surface9Packed, uv, gx, gy); }
    else if (slot == 10) { color = textureGrad(surface10Color, uv, gx, gy).rgb; normal = textureGrad(surface10Normal, uv, gx, gy).xyz; packed = textureGrad(surface10Packed, uv, gx, gy); }
    else if (slot == 11) { color = textureGrad(surface11Color, uv, gx, gy).rgb; normal = textureGrad(surface11Normal, uv, gx, gy).xyz; packed = textureGrad(surface11Packed, uv, gx, gy); }
    else if (slot == 12) { color = textureGrad(surface12Color, uv, gx, gy).rgb; normal = textureGrad(surface12Normal, uv, gx, gy).xyz; packed = textureGrad(surface12Packed, uv, gx, gy); }
    else if (slot == 13) { color = textureGrad(surface13Color, uv, gx, gy).rgb; normal = textureGrad(surface13Normal, uv, gx, gy).xyz; packed = textureGrad(surface13Packed, uv, gx, gy); }
    else if (slot == 14) { color = textureGrad(surface14Color, uv, gx, gy).rgb; normal = textureGrad(surface14Normal, uv, gx, gy).xyz; packed = textureGrad(surface14Packed, uv, gx, gy); }
    else if (slot == 15) { color = textureGrad(surface15Color, uv, gx, gy).rgb; normal = textureGrad(surface15Normal, uv, gx, gy).xyz; packed = textureGrad(surface15Packed, uv, gx, gy); }
    else { color = vec3(0.05); normal = vec3(0.5, 0.5, 1.0); packed = vec4(1.0, 0.8, 0.0, 0.5); }
}

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

// The ballast's rock: one PBR material (colour, normal, and ambient occlusion, roughness and height packed), laid by where it is in
// the world, as a world-aligned texture is, and given its relief by parallax occlusion mapping exactly as Godot 4's
// StandardMaterial3D does it ("deep parallax", BaseMaterial3D in scene/resources/material.cpp) and LearnOpenGL teaches it
// (Advanced Lighting, Parallax Mapping), both Tatarchuk's algorithm: the line of sight stepped down through the height map in
// layers, between 8 looking straight down and 32 looking across, and the place it meets the stones taken between the last layer
// above them and the first below. Its colour, roughness and occlusion are the maps' own; the lighting is the shared PBR.
const float RockTile = 1.5;         // metres the rock maps repeat over
const float RockDepth = 0.01;       // the texture's own material's heightmap_scale (1, times 0.01, as Godot takes it), in texture units
const float RockMinLayers = 8.0;    // Godot's and LearnOpenGL's defaults
const float RockMaxLayers = 32.0;
// The rock's colour darkened to what track ballast is (crushed trap rock and granite, dusted with oil and brake dust): the texture's
// own is a pale gravel.
const vec3 RockShade = vec3(0.5, 0.49, 0.47);
// How much of the texture's smoothness to keep: it's a smooth river gravel (roughness 0.42 to 0.73), and crushed ballast is matte
// (about 0.85 to 0.95). Its roughness is taken towards fully rough by this, keeping the map's own variation stone to stone.
const float RockSmoothness = 0.22;

struct Rock { vec3 albedo; vec3 normal; float roughness; float occlusion; };

// The rock at uv, laid along t and b (the world directions uv's x and y run, on the surface), on a surface facing n, seen from
// toEye (unit, from the surface towards the eye).
Rock rockAt(vec2 uv, vec3 t, vec3 b, vec3 n, vec3 toEye)
{
    // Read by uv's own rate of change throughout: the moved place jumps at each stone's edge, and the loop's reads aren't uniform.
    vec2 dx = dFdx(uv);
    vec2 dy = dFdy(uv);

    // The line of sight in the texture's own frame (Godot's view_dir).
    vec3 viewDir = normalize(vec3(dot(toEye, t), dot(toEye, b), dot(toEye, n)));
    float numLayers = mix(RockMaxLayers, RockMinLayers, abs(viewDir.z));
    float layerDepth = 1.0 / numLayers;
    vec2 delta = viewDir.xy * RockDepth / numLayers;
    vec2 ofs = uv;
    float depth = 1.0 - textureGrad(rockPacked, ofs, dx, dy).a;
    float currentDepth = 0.0;
    for (int i = 0; i < 64 && currentDepth < depth; i++)
    {
        ofs -= delta;
        depth = 1.0 - textureGrad(rockPacked, ofs, dx, dy).a;
        currentDepth += layerDepth;
    }

    vec2 prevOfs = ofs + delta;
    float afterDepth = depth - currentDepth;
    float beforeDepth = (1.0 - textureGrad(rockPacked, prevOfs, dx, dy).a) - currentDepth + layerDepth;
    float weight = afterDepth / (afterDepth - beforeDepth);
    ofs = mix(ofs, prevOfs, weight);

    Rock r;
    vec4 packed = textureGrad(rockPacked, ofs, dx, dy);
    r.albedo = textureGrad(rockColor, ofs, dx, dy).rgb * RockShade;
    r.occlusion = packed.r;
    r.roughness = 1.0 - (1.0 - packed.g) * RockSmoothness;
    // The normal map's green points up its image, against the way uv's y runs.
    vec3 m = textureGrad(rockNormal, ofs, dx, dy).xyz * 2.0 - 1.0;
    r.normal = normalize(t * m.x - b * m.y + n * m.z);
    return r;
}

// The rock laid from above (a world-aligned texture's top projection): uv the world's x and z, its axes those laid onto the surface.
Rock rockFromAbove(vec2 worldXZ, vec3 n, vec3 toEye)
{
    vec3 t = normalize(vec3(1.0, 0.0, 0.0) - n * n.x);
    vec3 b = normalize(vec3(0.0, 0.0, 1.0) - n * n.z);
    return rockAt(worldXZ / RockTile, t, b, n, toEye);
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
        vec3 shape = n;
        // By where it is in the world, seen from above: loose stone has no grain to follow the track, and so every bed, switch and
        // crossing reads the same stones at the same place and meets the next with no seam. (The world origin wraps every
        // TexturePeriod, a whole number of tiles.)
        Rock r = rockFromAbove(tex, n, -view);
        albedo = r.albedo;
        n = r.normal;
        roughness = r.roughness;
        occlusion = r.occlusion;
        // Its highlights as its roughness has them.
        specular = 1.0;

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
        // A paved surface in one of the surface textures (SurfaceTextures): 0 the roads' own asphalt (always in the first slot);
        // otherwise 1 + its number (a crossing's panels), whose slot the lookup has; and its kind (asphalt, concrete, wood).
        int surface = (style >> 14) & 63;
        vec2 found = surface == 0 ? vec2(0.0) : texelFetch(surfaceLookup, ivec2(surface - 1, 0), 0).rg * 255.0 + 0.5;
        int slot = int(found.x);
        int kind = int(found.y);
        bool upright = abs(n.y) < 0.5;
        float wear = fbm(world.xz * 0.12, pixel * 0.12);
        // Asphalt over 3 m and concrete over 2 m, laid by where it is; wood over 1.5 m, its grain along the panels (along the
        // track on top, along the face on a side).
        vec2 suv = kind == 2 ? (upright ? vec2(u, v) : vec2(v, u)) / 1.5 : (upright ? boxUv(local, n) : tex) / (kind == 0 ? 3.0 : 2.0);
        vec2 gx = dFdx(suv), gy = dFdy(suv);
        vec3 surfaceColor, surfaceNormal;
        vec4 surfacePacked;
        surfaceMaps(slot, suv, gx, gy, surfaceColor, surfaceNormal, surfacePacked);

        albedo = surfaceColor * (0.9 + 0.2 * wear);
        occlusion = surfacePacked.x;
        roughness = surfacePacked.y;
        n = bumped(n, surfaceNormal * 2.0 - 1.0, suv);
        puddle = kind == 0 && !upright ? smoothstep(0.55, 0.68, wear) : 0.0;
        specular = kind == 0 ? 0.18 : kind == 1 ? 0.25 : 0.4;

        int lanes = style & 7;
        bool oneway = (style & 8) != 0;
        int marks = (style >> 4) & 3;
        float halfWidth = float((style >> 6) & 255) * 0.125;
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
        // The ballast's rock on road shoulders and unpaved roads: the same material, laid by its strongest axis (on the ground its
        // plan, on a wall the wall's face).
        vec3 a = abs(n);
        vec3 t, b;
        vec2 at;
        if (a.y > a.x && a.y > a.z)
        {
            at = local.xz;
            t = vec3(1.0, 0.0, 0.0);
            b = vec3(0.0, 0.0, 1.0);
        }
        else if (a.x > a.z)
        {
            at = local.zy;
            t = vec3(0.0, 0.0, 1.0);
            b = vec3(0.0, 1.0, 0.0);
        }
        else
        {
            at = local.xy;
            t = vec3(1.0, 0.0, 0.0);
            b = vec3(0.0, 1.0, 0.0);
        }
        t = normalize(t - n * dot(n, t));
        b = normalize(b - n * dot(n, b));
        Rock r = rockAt(at / RockTile, t, b, n, -view);
        albedo = r.albedo;
        n = r.normal;
        roughness = r.roughness;
        occlusion = r.occlusion;
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
