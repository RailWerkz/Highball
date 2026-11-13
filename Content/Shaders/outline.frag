#version 450
// The glow round a driven or picked vehicle's silhouette, over the finished picture: where the mask changes within a pixel or
// so, a bright line; within a few, a softer one; within seven, a faint halo. Each breathes slowly.

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D mask;

layout(push_constant) uniform Push
{
    vec4 driven; // its colour (r channel)
    vec4 picked; // its colour (g channel)
    vec4 params; // x: how bright it is now (the breathing), y: pixels per interface unit, zw: one over the screen's size
} pc;

const int Around = 10;

// Whether the mask (half the screen's size, read filtered) differs from here anywhere on a ring this many pixels out, per channel.
vec2 ring(vec2 p, vec2 here, float radius)
{
    vec2 differs = vec2(0.0);
    for (int i = 0; i < Around; i++)
    {
        float a = (float(i) + 0.5) * (6.2831853 / float(Around));
        differs = max(differs, abs(texture(mask, (p + vec2(cos(a), sin(a)) * radius) * pc.params.zw).rg - here));
    }
    return differs;
}

void main()
{
    vec2 p = gl_FragCoord.xy;
    vec2 here = texture(mask, p * pc.params.zw).rg;
    float s = pc.params.y;

    vec2 line = ring(p, here, max(0.65 * s, 1.0));
    vec2 soft = max(line, max(ring(p, here, 1.25 * s), ring(p, here, 1.75 * s)));
    vec2 halo = max(soft, max(ring(p, here, 2.6 * s), ring(p, here, 3.5 * s)));
    vec2 alpha = (0.6 * line + 0.16 * soft + 0.07 * halo) * pc.params.x;
    alpha = min(alpha, vec2(1.0));
    float a = min(alpha.r * pc.driven.a + alpha.g * pc.picked.a, 1.0);
    if (a <= 0.002)
        discard;
    vec3 color = (pc.driven.rgb * alpha.r * pc.driven.a + pc.picked.rgb * alpha.g * pc.picked.a);
    // Premultiplied over what's there: the blend is src + dst * srcAlpha, so alpha here is how much shows through.
    outColor = vec4(color, 1.0 - a);
}
