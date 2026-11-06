#version 450
// A streak of rain or a flake of snow, lit by the sky. Drops catch far less light than flakes.

layout(location = 0) in vec2 vCorner;
layout(location = 1) in float vFade;
layout(location = 2) in float vAlong;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 wrap;
    vec4 motion;
    vec4 shape;
    vec4 light;
} pc;

layout(set = 0, binding = 0) uniform sampler2D sceneDepth;

void main()
{
    // Behind whatever's solid here.
    if (pc.light.w / max(texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r, 1e-8) < vAlong)
        discard;

    vec3 light = pc.light.rgb;
    float amount;
    if (pc.shape.z > 0.5)
    {
        amount = 0.85 * smoothstep(1.0, 0.3, length(vCorner));
    }
    else
    {
        amount = 0.4 * (1.0 - abs(vCorner.x)) * smoothstep(1.0, 0.5, abs(vCorner.y));
        light *= 1.2; // a touch darker than bright cloud behind it and lighter than the ground
    }

    amount *= vFade;
    // What the blend keeps of what's behind is the alpha.
    outColor = vec4(light * amount, 1.0 - amount);
}
