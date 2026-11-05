#version 450
// A tie or tie plate in a shadow map: placed as part.vert places it.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec4 inPlace;
layout(location = 4) in vec4 inForward;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset;
} pc;

void main()
{
    vec3 forward = normalize(inForward.xyz);
    vec3 right = normalize(cross(forward, vec3(0.0, 1.0, 0.0)));
    vec3 up = cross(right, forward);

    vec3 model = vec3(inPosition.x * (inForward.w > 0.0 ? inForward.w : 1.0), inPosition.yz);
    gl_Position = pc.light * vec4(inPlace.xyz + mat3(right, up, forward) * model + pc.offset.xyz, 1.0);
}
