#version 450
// A moving model drawn again for MotionVectors: where each point of it was last frame, as a move from where it is now.

layout(location = 0) in vec3 inPosition;

layout(location = 0) out vec3 vBack;

layout(push_constant) uniform Push
{
    mat4 viewProjection;         // this frame's, jittered as the world was drawn, camera at the origin
    vec4 rotation;               // quaternion, now
    vec4 placeAndScale;          // xyz: where it stands relative to the camera; w: uniform scale
    vec4 previousRotation;       // quaternion, last frame
    vec4 previousPlaceAndNear;   // xyz: where it stood last frame relative to the camera now; w: the near distance
} pc;

vec3 rotate(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

void main()
{
    vec3 local = inPosition * pc.placeAndScale.w;
    vec3 now = rotate(pc.rotation, local) + pc.placeAndScale.xyz;
    vec3 before = rotate(pc.previousRotation, local) + pc.previousPlaceAndNear.xyz;
    vBack = before - now;
    gl_Position = pc.viewProjection * vec4(now, 1.0);
}
