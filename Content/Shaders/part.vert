#version 450

layout(location = 0) in vec3 inPosition; // the model: x across the track, y up, z along
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace;    // where this one stands relative to the chunk, and a random tint
layout(location = 4) in vec4 inForward;  // the way its z faces, and how much longer across it is (0: as made)

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out float vTint;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera;
    vec4 sun;
} pc;

void main()
{
    vec3 forward = normalize(inForward.xyz);
    vec3 right = normalize(cross(forward, vec3(0.0, 1.0, 0.0)));
    vec3 up = cross(right, forward);
    mat3 basis = mat3(right, up, forward);

    vec3 model = vec3(inPosition.x * (inForward.w > 0.0 ? inForward.w : 1.0), inPosition.yz);
    vec3 relative = inPlace.xyz + basis * model + pc.offset.xyz;
    vRelative = relative;
    vNormal = basis * inNormal;
    vUv = inUv;
    vTint = inPlace.w;
    // Pulled a fraction towards the camera like the rest of the track (see line.vert).
    gl_Position = pc.viewProjection * vec4(relative * 0.9985, 1.0);
}
