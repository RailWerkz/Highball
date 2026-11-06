#version 450

layout(location = 0) in vec2 vCorner;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 placeSize;
    vec4 color;
    vec4 screenX;
    vec4 screenY;
} pc;

void main()
{
    float r = length(vCorner);
    float glow = 0.5 * pow(max(1.0 - r, 0.0), 4.0) + 3.0 * smoothstep(0.1, 0.02, r);
    // Added to what's behind it: the blend keeps all of that.
    outColor = vec4(pc.color.rgb * glow, 1.0);
}
