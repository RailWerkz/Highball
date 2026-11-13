#version 450
// Lays the half-size marched smoke over the picture. Each pixel prefers the smoke samples that marched as far as it did, so
// smoke behind an engine's roof doesn't bleed over its edge, nor the roof's clear sky into the smoke.

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D smokeColor; // rgb: light it adds; a: how much of what's behind shows
layout(set = 0, binding = 1) uniform sampler2D smokeAlong; // how far ahead the world was at each sample
layout(set = 0, binding = 2) uniform sampler2D sceneDepth;
layout(set = 0, binding = 3) uniform SmokeUniforms
{
    vec4 right;
    vec4 up;
    vec4 forward; // w: the near distance
    vec4 sun;
    vec4 sunColor;
    vec4 skyTop;
    vec4 skyLow;
    vec4 noise;
    vec4 info;
} u;

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 screen = textureSize(sceneDepth, 0);
    ivec2 half_ = textureSize(smokeColor, 0);
    float here = u.forward.w / max(texelFetch(sceneDepth, pixel, 0).r, 1e-8);

    // A tent over the 3 x 3 half-size samples round this pixel, wider than bilinear so the march's step dither is smoothed
    // away, each weighted again by how near the depth it marched to is to this pixel's.
    vec2 place = (vec2(pixel) + 0.5) * vec2(half_) / vec2(screen) - 0.5;
    ivec2 middle = ivec2(round(place));
    vec4 sum = vec4(0.0);
    float weights = 0.0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            ivec2 at = clamp(middle + ivec2(x, y), ivec2(0), half_ - 1);
            vec2 d = abs(vec2(middle + ivec2(x, y)) - place);
            float tent = max(0.0, 1.0 - d.x / 1.5) * max(0.0, 1.0 - d.y / 1.5);
            float there = texelFetch(smokeAlong, at, 0).r;
            float like = 1.0 / (1e-3 + abs(there - here) / max(here, 1e-3));
            float w = tent * like;
            sum += texelFetch(smokeColor, at, 0) * w;
            weights += w;
        }
    vec4 smoke = weights > 1e-6 ? sum / weights : vec4(0.0, 0.0, 0.0, 1.0);
    if (smoke.a > 0.999)
        discard;
    // What the blend keeps of what's behind is the alpha.
    outColor = smoke;
}
