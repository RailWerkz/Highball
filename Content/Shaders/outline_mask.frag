#version 450

layout(location = 0) out vec4 outMask;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 channel;
} pc;

void main()
{
    // Alpha 1 adds to what's there (the blend is src + dst * srcAlpha), so a vehicle both driven and picked has both.
    outMask = vec4(pc.channel.rg, 0.0, 1.0);
}
