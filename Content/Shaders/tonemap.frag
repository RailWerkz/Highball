#version 450

layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D hdr;

layout(push_constant) uniform Push
{
    vec4 settings; // x: exposure
} pc;

// Narkowicz's fit of the ACES filmic curve.
vec3 aces(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

vec3 toSrgb(vec3 c)
{
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

void main()
{
    vec3 color = texture(hdr, vUv).rgb * pc.settings.x;
    outColor = vec4(toSrgb(aces(color)), 1.0);
}
