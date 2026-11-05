#version 450
// The last step: adds the bloom, god rays and the sun's glare to the picture, exposes it, grades it and fits it to the screen.

layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D hdr;
layout(set = 0, binding = 1) uniform sampler2D bloom;
layout(set = 0, binding = 2) uniform sampler2D rays;
layout(set = 0, binding = 3) uniform sampler2D measured;
#include "Post/PostData.glsl"

// The picture's measured brightness at which the set exposure is left as it is.
const float MeasuredBrightness = 0.42;

// Narkowicz's fit of the ACES filmic curve.
vec3 aces(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

vec3 toSrgb(vec3 c)
{
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// The veil of light a bright sun leaves in a lens and in the eye: a tight glow, a wide one, and faint spokes.
vec3 glare(vec2 uv, float visible)
{
    if (visible <= 0.0 || post.sunColor.w <= 0.0)
        return vec3(0.0);

    vec2 d = (uv - post.sunUv.xy) * vec2(post.sunUv.z, 1.0);
    float away = length(d);
    float glow = 0.5 * exp(-away * 22.0) + 0.09 * exp(-away * 4.5);
    float turn = atan(d.y, d.x);
    float spokes = pow(abs(cos(turn * 3.0 + 0.4)), 40.0) + 0.6 * pow(abs(cos(turn * 7.0 + 1.3)), 60.0);
    glow += 0.05 * spokes * exp(-away * 9.0);
    return post.sunColor.rgb * glow * visible * min(post.sunColor.w * 4.0, 1.0);
}

void main()
{
    vec3 color = mix(texture(hdr, vUv).rgb, texture(bloom, vUv).rgb, post.look.y);
    vec2 reading = texelFetch(measured, ivec2(0), 0).rg;

    color += post.sunColor.rgb * texture(rays, vUv).r * post.sunColor.w;
    color += glare(vUv, reading.g);

    // Partly, not all the way: night should still look darker than noon.
    float exposure = post.look.z;
    if (post.look.w > 0.5)
        exposure *= clamp(pow(MeasuredBrightness / exp2(reading.r), 0.55), 0.35, 5.0);
    color *= exposure;

    // White balance, contrast about mid grey, saturation.
    color *= post.grade.rgb;
    color = pow(max(color, vec3(0.0)) / 0.18, vec3(post.look.x)) * 0.18;
    color = max(mix(vec3(luma(color)), color, post.grade.w), vec3(0.0));

    // A little darker towards the corners, as through any lens.
    vec2 corner = vUv * 2.0 - 1.0;
    color *= 1.0 - 0.1 * dot(corner, corner) * 0.5;

    // Dithered, so the sky's slow gradients don't show as bands in eight bits.
    vec3 fitted = toSrgb(aces(color));
    outColor = vec4(fitted + (noiseAt(gl_FragCoord.xy) - 0.5) / 255.0, 1.0);
}
