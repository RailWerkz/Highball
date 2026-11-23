#version 450
// A tree drawn into one frame of its impostor (ImpostorBaker, impostor.glsl): seen straight along the frame's direction, framed
// on its bounding sphere, nearer greater (reverse-Z, as the world is drawn).

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out float vDepth;

layout(push_constant) uniform Push
{
    vec4 right;
    vec4 up;
    vec4 view;   // xyz: towards the viewer, w: the sphere's radius
    vec4 centre; // xyz: the sphere's centre, w: 0 colour, 1 normal and depth
    vec4 cut;    // x: where its cut-out comes from, as plant.frag's shape.w
} pc;

void main()
{
    vec3 p = (inPosition - pc.centre.xyz) / pc.view.w;
    float depth = dot(p, pc.view.xyz);
    vNormal = inNormal;
    vUv = inUv;
    vDepth = depth;
    gl_Position = vec4(dot(p, pc.right.xyz), dot(p, pc.up.xyz), 0.5 + 0.5 * depth, 1.0);
}
