#version 450

layout(location = 0) in vec3 inPosition;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 rotation;      // quaternion
    vec4 placeAndScale; // xyz: where it stands relative to the camera, w: uniform scale
} pc;

void main()
{
    vec3 v = inPosition * pc.placeAndScale.w;
    vec3 turned = v + 2.0 * cross(pc.rotation.xyz, cross(pc.rotation.xyz, v) + pc.rotation.w * v);
    gl_Position = pc.light * vec4(turned + pc.placeAndScale.xyz, 1.0);
}
