#version 450
// Writes how far back to last frame's place this point of a moving model is, where it's what the world shows here.

layout(location = 0) in vec3 vBack;
layout(location = 0) out vec4 outMotion;

layout(set = 0, binding = 0) uniform sampler2D sceneDepth;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 rotation;
    vec4 placeAndScale;
    vec4 previousRotation;
    vec4 previousPlaceAndNear; // w: the near distance
} pc;

void main()
{
    // Depth is the near distance over the distance along the view, so each turns back into metres the same way. Kept only
    // where it's within a whisker of what the world drew here: not behind it (hidden), and not in front of it (a gap in a
    // railing, where the world shows what's beyond).
    float near = pc.previousPlaceAndNear.w;
    float scene = near / max(texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r, 1e-9);
    float mine = near / max(gl_FragCoord.z, 1e-9);
    if (abs(mine - scene) > mine * 0.004 + 0.05)
        discard;
    outMotion = vec4(vBack, 1.0);
}
