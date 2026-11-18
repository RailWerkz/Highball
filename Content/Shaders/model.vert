#version 460
#extension GL_EXT_buffer_reference : require
// A placed model, one instance each (ModelInstance). Each of a call's draws is one of its parts: which one (its material's row in
// the table, model.frag) is the call's base plus the draw's place in it.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inRotation;      // quaternion
layout(location = 4) in vec4 inPlaceAndScale; // xyz: where it stands relative to the camera, w: uniform scale

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) flat out uint vDraw;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    uvec2 materials; // the draws' material table (model.frag)
    uint base;       // this call's first row in it
    float cameraHeight;
} pc;

vec3 rotate(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

void main()
{
    vec3 relative = rotate(inRotation, inPosition * inPlaceAndScale.w) + inPlaceAndScale.xyz;
    vRelative = relative;
    vNormal = rotate(inRotation, inNormal);
    vUv = inUv;
    vDraw = pc.base + uint(gl_DrawID);
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
