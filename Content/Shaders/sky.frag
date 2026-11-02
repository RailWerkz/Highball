#version 450
#include "atmosphere.glsl"

layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push
{
    vec4 right;   // camera right * tan(half fov x)
    vec4 up;      // camera up * tan(half fov y)
    vec4 forward;
    vec4 sun;
    vec4 camera;
} pc;

void main()
{
    vec3 dir = normalize(pc.forward.xyz + vNdc.x * pc.right.xyz + vNdc.y * pc.up.xyz);
    vec3 sun = pc.sun.xyz;
    vec3 color = skyColor(dir, sun) + sunDisc(dir, sun);
    outColor = vec4(color, 1.0);
}
