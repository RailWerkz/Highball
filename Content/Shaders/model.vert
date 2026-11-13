#version 450
// A placed model, one instance each (ModelInstance); its material's factors in the push constants.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inRotation;      // quaternion
layout(location = 4) in vec4 inPlaceAndScale; // xyz: where it stands relative to the camera, w: uniform scale

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 baseColor;
    vec4 material;      // x: metallic, y: roughness, z: camera height, w: alpha cutoff
} pc;

vec3 rotate(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

void main()
{
    vec3 relative = rotate(inRotation, inPosition * inPlaceAndScale.w) + inPlaceAndScale.xyz;
    vRelative = relative;
    vNormal = rotate(inRotation, inNormal);
    vUv = inUv;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
