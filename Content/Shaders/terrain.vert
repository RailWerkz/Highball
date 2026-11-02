#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inCover;
layout(location = 3) in vec4 inMaterials;

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec4 vCover;
layout(location = 3) out vec4 vMaterials;

layout(push_constant) uniform Push
{
    mat4 viewProjection; // camera at the origin
    vec4 offset;         // the patch's origin relative to the camera
    vec4 camera;         // camera position in world metres
    vec4 sun;
} pc;

void main()
{
    vec3 relative = inPosition + pc.offset.xyz;
    vRelative = relative;
    vNormal = inNormal;
    vCover = inCover;
    vMaterials = inMaterials;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
