#version 450
#include "atmosphere.glsl"
#include "noise.glsl"
#include "shadows.glsl"

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

// LineMaterial, and what each packs into vData (see LineVertex).
const int Ballast = 0;
const int Rail = 1;
const int Asphalt = 2;
const int Gravel = 3;
const int Concrete = 4;
const int Painted = 5;

// NetworkMeshes.Paints: what a placed model's parts are painted.
const vec3 Paints[7] = vec3[](vec3(0.018), vec3(0.62, 0.62, 0.60), vec3(0.26, 0.27, 0.28), vec3(0.60, 0.42, 0.05),
    vec3(0.50, 0.02, 0.015), vec3(0.02, 0.32, 0.12), vec3(0.30, 0.29, 0.26));

const vec3 Steel = vec3(0.40, 0.40, 0.40);
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
    float specular = 0.0;
    float gloss = 16.0;

    if (material == Ballast)
    {
        float patches = fbm(tex * 0.35, pixel * 0.35);
        albedo = texture(ballastMap, tex / 1.5).rgb * 0.65 * (0.85 + 0.3 * patches);

        int tracks = style & 7;
        bool disused = (style & 8) != 0;
        float railOffset = vData.w;
        float far = smoothstep(nearRadius - 60.0, nearRadius, dist);
        if (n.y > 0.95)
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
        float rust = noise(vec2(v * 3.0, height * 40.0));
        albedo = mix(Rust * (0.8 + 0.4 * rust), Steel, worn);
        specular = mix(0.04, 0.5, worn);
        gloss = mix(12.0, 90.0, worn);
    }
    else if (material == Asphalt)
    {
        float grit = fbm(tex * 6.0, pixel * 6.0);
        float wear = fbm(tex * 0.12, pixel * 0.12);
        albedo = vec3(0.036, 0.037, 0.039) * (0.85 + 0.3 * grit) * (0.85 + 0.3 * wear);

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
        // The ballast's stone, finer and browner: the walkways and road shoulders.
        float patches = fbm(tex * 0.5, pixel * 0.5);
        albedo = texture(ballastMap, tex / 0.9).rgb * vec3(0.78, 0.72, 0.6) * (0.8 + 0.4 * patches);
    }
    else if (material == Concrete)
    {
        float stain = fbm(tex * 0.5 + world.y * 0.7, pixel * 0.7);
        albedo = vec3(0.22, 0.215, 0.20) * (0.75 + 0.4 * stain);
    }
    else if (material == Painted)
    {
        albedo = Paints[clamp(style, 0, 6)];
        specular = style == 2 ? 0.2 : 0.08;
        gloss = 40.0;
    }

    float direct = max(dot(n, sun), 0.0) * sunShadow(vRelative, n, sun);
    vec3 skyLight = skyAmbient(n);
    vec3 color = albedo * (SunColor * direct + skyLight);
    vec3 halfway = normalize(sun - view);
    color += SunColor * specular * pow(max(dot(n, halfway), 0.0), gloss) * direct;

    color = applyFog(color, view, dist, pc.camera.y, sun);
    outColor = vec4(color, 1.0);
}
