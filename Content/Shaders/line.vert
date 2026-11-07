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
    float nearer = 0.0015;
    // The fade into the ground (Gravel, Data.w below zero) further: lying on the ground, any of the terrain's unevenness or
    // coarseness would otherwise cut it off with a hard line, somewhere else at each level of detail. It writes no depth to matter.
    if ((int(inData.z + 0.5) & 7) == 3 && inData.w < 0.0)
        nearer += min(0.3, 30.0 / max(length(relative), 1.0));
    gl_Position = pc.viewProjection * vec4(relative * (1.0 - nearer), 1.0);
}
