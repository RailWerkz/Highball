#version 450
// The last step: adds the bloom, god rays and the sun's glare to the picture, exposes it, grades it and fits it to the screen with
// the chosen tonemapper (Post/Tonemapping.glsl, Settings.Tonemapper).

layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D hdr;
layout(set = 0, binding = 1) uniform sampler2D bloom;
layout(set = 0, binding = 2) uniform sampler2D rays;
layout(set = 0, binding = 3) uniform sampler2D measured;
#include "Post/PostData.glsl"
#include "Post/Tonemapping.glsl"

// The picture's measured brightness at which the set exposure is left as it is.
const float MeasuredBrightness = 0.42;

// The tonemapper that already gives the display's gamma (Hejl and Burgess-Dawson's filmic): no sRGB encoding after it.
const uint GammaBuiltIn = 4u;

float hableNumber(int i)
{
    return post.hable[i / 4][i % 4];
}

HableTonemapUniforms hableCurve()
{
    HableTonemapUniforms curve;
    for (int k = 0; k < 3; k++)
    {
        int at = k * 6;
        curve.segments[k] = HableSegment(hableNumber(at), hableNumber(at + 1), hableNumber(at + 2), hableNumber(at + 3), hableNumber(at + 4), hableNumber(at + 5));
    }
    curve.invW = hableNumber(18);
    curve.x0 = hableNumber(19);
    curve.x1 = hableNumber(20);
    curve.padding0 = 0.0;
    return curve;
}

vec3 toSrgb(vec3 c)
{
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// The veil of light a bright sun leaves in a lens and in the eye: a tight glow, a wide one, and faint spokes.
vec3 glare(vec2 uv, float visible, float air)
{
    if (visible <= 0.0 || post.sunColor.w <= 0.0)
        return vec3(0.0);

    vec2 d = (uv - post.sunUv.xy) * vec2(post.sunUv.z, 1.0);
    float away = length(d);
    // The tight core's the lens's own flare round the sun's disc, over everything; the wide halo and spokes are the bright air round
    // it, faint over anything near enough to stand in front of that air (a tree against the sun), as the god rays are.
    float halo = mix(0.15, 1.0, air);
    float glow = 0.5 * exp(-away * 22.0) + 0.09 * exp(-away * 4.5) * halo;
    float turn = atan(d.y, d.x);
    float spokes = pow(abs(cos(turn * 3.0 + 0.4)), 40.0) + 0.6 * pow(abs(cos(turn * 7.0 + 1.3)), 60.0);
    glow += 0.05 * spokes * exp(-away * 9.0) * halo;
    return post.sunColor.rgb * glow * visible * min(post.sunColor.w * 4.0, 1.0);
}

void main()
{
    vec3 color = mix(texture(hdr, vUv).rgb, texture(bloom, vUv).rgb, post.look.y);
    vec2 reading = texelFetch(measured, ivec2(0), 0).rg;

    vec2 rayed = texture(rays, vUv).rg;
    color += post.sunColor.rgb * rayed.r * post.sunColor.w;
    color += glare(vUv, reading.g, post.sunColor.w > 0.0 && post.sun.w > 0.0 ? rayed.g : 1.0);

    // Partly, not all the way: night should still look darker than noon.
    float exposure = post.look.z;
    if (post.look.w > 0.5)
        exposure *= clamp(pow(MeasuredBrightness / exp2(reading.r), 0.55), 0.35, 5.0);
    color *= exposure;

    // Contrast about mid grey, saturation, then white balance (after, so a toned black and white keeps its tone).
    color = pow(max(color, vec3(0.0)) / 0.18, vec3(post.look.x)) * 0.18;
    color = max(mix(vec3(luma(color)), color, post.grade.w), vec3(0.0));
    color *= post.grade.rgb;
    // The look's shadows raised towards its colour: fully in the blacks, none from a little over mid grey (faded or tinted blacks).
    color += post.tone.rgb * 0.18 * (1.0 - smoothstep(0.0, 0.3, luma(color)));

    // Darker towards the corners, as through any lens (the look's own strength).
    vec2 corner = vUv * 2.0 - 1.0;
    color *= 1.0 - post.tone.w * dot(corner, corner) * 0.5;

    // Dithered, so the sky's slow gradients don't show as bands in eight bits.
    // Exposed and graded already, so each curve gets an exposure of 1, and a gamma of 1 so it stays linear for the sRGB encoding.
    uint mapper = uint(post.mapping.x + 0.5);
    vec3 mapped = tonemapSwitch(mapper, color, 1.0, 1.0, hableCurve());
    vec3 fitted = mapper == GammaBuiltIn ? mapped : toSrgb(clamp(mapped, 0.0, 1.0));
    outColor = vec4(fitted + (noiseAt(gl_FragCoord.xy) - 0.5) / 255.0, 1.0);
}
