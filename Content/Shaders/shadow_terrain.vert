#version 450

layout(location = 0) in vec3 inPosition;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset; // the patch's origin relative to the camera
} pc;

void main()
{
    gl_Position = pc.light * vec4(inPosition + pc.offset.xyz, 1.0);
}
