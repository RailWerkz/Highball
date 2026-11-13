#version 450
// A vehicle's silhouette for VehicleOutline's mask: the same instances as the world's models, flat.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec4 inRotation;      // quaternion
layout(location = 4) in vec4 inPlaceAndScale; // xyz: where it stands relative to the camera, w: uniform scale

layout(push_constant) uniform Push
{
    mat4 viewProjection; // unjittered: the outline sits over the finished picture
    vec4 channel;        // which of the mask's channels: driven (r) or picked (g)
} pc;

void main()
{
    vec3 v = inPosition * inPlaceAndScale.w;
    vec3 turned = v + 2.0 * cross(inRotation.xyz, cross(inRotation.xyz, v) + inRotation.w * v);
    gl_Position = pc.viewProjection * vec4(turned + inPlaceAndScale.xyz, 1.0);
}
