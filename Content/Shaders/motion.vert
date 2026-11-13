#version 450
// A moving model drawn again for MotionVectors: where each point of it was last frame, as a move from where it is now.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec4 inRotation;          // quaternion, now
layout(location = 4) in vec4 inPlaceAndScale;     // xyz: where it stands relative to the camera; w: uniform scale
layout(location = 5) in vec4 inPreviousRotation;  // quaternion, last frame
layout(location = 6) in vec4 inPreviousPlace;     // xyz: where it stood last frame relative to the camera now

layout(location = 0) out vec3 vBack;

layout(push_constant) uniform Push
{
    mat4 viewProjection; // this frame's, jittered as the world was drawn, camera at the origin
    vec4 near;           // x: the near distance
} pc;

vec3 rotate(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

void main()
{
    vec3 local = inPosition * inPlaceAndScale.w;
    vec3 now = rotate(inRotation, local) + inPlaceAndScale.xyz;
    vec3 before = rotate(inPreviousRotation, local) + inPreviousPlace.xyz;
    vBack = before - now;
    gl_Position = pc.viewProjection * vec4(now, 1.0);
}
