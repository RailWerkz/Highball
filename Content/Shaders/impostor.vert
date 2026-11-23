#version 460
#extension GL_EXT_buffer_reference : require
// A far tree as its impostor (impostor.glsl): one quad over its bounding sphere, facing the camera, placed, sized, turned and
// faded exactly as plant.vert places the tree's own model, so the two hand over without a jump.

#include "plant_fade.glsl"

layout(location = 0) in vec3 inPosition; // the quad's corner, -1 to 1 in x and y
layout(location = 3) in vec4 inPlace;
layout(location = 4) in vec4 inTint;

layout(location = 0) out vec3 vRelative;      // the quad's point, relative to the camera
layout(location = 1) flat out vec3 vCentre;   // the sphere's centre, relative to the camera
layout(location = 2) flat out float vRadius;  // its radius, in metres
layout(location = 3) flat out vec3 vTint;
layout(location = 4) flat out vec2 vTurn;     // the instance's turn, cos and sin
layout(location = 6) flat out vec2 vFade;

// (As plant.vert: the depth prepass and the shaded pass must land on the same depth.)
invariant gl_Position;

layout(buffer_reference, std430, buffer_reference_align = 16) readonly buffer Cells
{
    vec4 cell[];
};

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 shape;   // y: how much its normal leans up, z: how far away it's gone
    vec4 band;    // xy: from and to how far away it's drawn; z: its sphere's centre's height, w: its radius (at a size of 1)
    vec4 globals; // x: the ground's origin below the camera (minus its height), y: the flutter's phase
    Cells cells;
    uint base;
    float delta;
} pc;

void main()
{
    vec4 cell = pc.cells.cell[pc.base + gl_DrawID];
    vec3 offset = vec3(cell.x, pc.globals.x, cell.y);
    float size = inPlace.w;
    float random = inTint.w;

    vec3 standing = inPlace.xyz + offset;
    float away = length(standing);
    size *= 1.0 - smoothstep(0.75, 1.0, away / (pc.shape.z * mix(0.25, 1.0, random)));

    float stagger = 0.9 + 0.2 * fract(random * 13.1);
    float from = pc.band.x * stagger;
    float to = pc.band.y < 1e8 ? pc.band.y * stagger : 1e9;
    vFade = levelFade(away, from, to);
    if (!levelShown(away, from, to))
        size = 0.0;

    float turn = fract(random * 37.7) * 6.2831853;
    vTurn = vec2(cos(turn), sin(turn));

    vec3 centre = standing + vec3(0.0, pc.band.z * size, 0.0);
    float radius = pc.band.w * size;
    vec3 toward = normalize(-centre);
    vec3 right = normalize(cross(abs(toward.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0), toward));
    vec3 up = cross(toward, right);
    vec3 corner = centre + (right * inPosition.x + up * inPosition.y) * radius;

    vRelative = corner;
    vCentre = centre;
    vRadius = max(radius, 1e-4);
    vTint = inTint.rgb;
    gl_Position = pc.viewProjection * vec4(corner, 1.0);
}
