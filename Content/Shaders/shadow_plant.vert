#version 450
// A plant or rock in a shadow map: the same as plant.vert, so shadows move and fade with what casts them.

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace;
layout(location = 4) in vec4 inTint;

layout(location = 0) out vec2 vUv;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset;
    vec4 shape;
    vec4 shift;
} pc;

void main()
{
    float size = inPlace.w;
    float random = inTint.w;

    vec3 standing = inPlace.xyz + pc.offset.xyz;
    size *= 1.0 - smoothstep(0.75, 1.0, length(standing) / (pc.shape.z * mix(0.25, 1.0, random)));

    float turn = fract(random * 37.7) * 6.2831853;
    float c = cos(turn);
    float s = sin(turn);
    vec3 model = (inPosition + pc.shift.xyz) * size;
    vec3 turned = vec3(model.x * c - model.z * s, model.y, model.x * s + model.z * c);

    if (pc.shape.x > 0.0)
    {
        float gust = sin(pc.offset.w * 1.7 + (inPlace.x + inPlace.z) * 0.13 + random * 6.28) + 0.5 * sin(pc.offset.w * 4.1 + random * 17.0);
        turned.xz += vec2(0.8, 0.5) * gust * inPosition.y * inPosition.y * pc.shape.x * size;
    }

    vUv = inUv;
    gl_Position = pc.light * vec4(standing + turned, 1.0);
}
