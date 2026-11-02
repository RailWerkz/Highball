#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inData;

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec4 vData;

layout(push_constant) uniform Push
{
    mat4 viewProjection; // camera at the origin
    vec4 offset;         // the chunk's origin relative to the camera
    vec4 camera;         // camera position in world metres; w: how far rails and ties reach
    vec4 sun;
} pc;

void main()
{
    vec3 relative = inPosition + pc.offset.xyz;
    vRelative = relative;
    vNormal = inNormal;
    vData = inData;
    // Drawn a fraction nearer the camera than it is: the same place on screen, but in front of the terrain that
    // coarser levels of detail lift a little above the line's formation.
    gl_Position = pc.viewProjection * vec4(relative * 0.9985, 1.0);
}
