#version 450
// The column of hot air over a stack: a quad standing up from the mouth, turned about the upright to face the camera,
// widening as it rises, and bending over as it goes with the air going past (lean: how far its top's pushed, curving more
// the higher it gets, as the smoke does). haze.frag wavers what's seen through it.

layout(location = 0) out vec2 vAcross; // x: -1 to 1 across it; y: 0 at the mouth to 1 at its top
layout(location = 1) out vec3 vRelative;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 placeWidth; // the mouth relative to the camera; how wide it is
    vec4 forward;    // w: the near distance
    vec4 look;       // strength, seconds, how tall, 0
    vec4 lean;       // how far its top's pushed over, in the world's x and z
} pc;

void main()
{
    const vec2 Corners[6] = vec2[](vec2(-1, 0), vec2(1, 0), vec2(1, 1), vec2(-1, 0), vec2(1, 1), vec2(-1, 1));
    vec2 corner = Corners[gl_VertexIndex];
    vec3 mouth = pc.placeWidth.xyz;
    vec3 up = vec3(0.0, 1.0, 0.0);
    vec3 flat_ = vec3(mouth.x, 0.0, mouth.z);
    vec3 side = length(flat_) > 1e-3 ? normalize(cross(up, flat_)) : vec3(1.0, 0.0, 0.0);
    float half_ = pc.placeWidth.w * 0.6 * (1.0 + 0.8 * corner.y);
    vec3 relative = mouth + side * corner.x * half_ + up * (corner.y * pc.look.z - 0.15) + pc.lean.xyz * pow(corner.y, 1.4);

    vAcross = corner;
    vRelative = relative;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
}
