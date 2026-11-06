#version 450
// Rain or snow in a box round the camera, with no vertex data: each instance works out its own place from its number. It's fixed to the
// world and wraps round the box, so the camera moves through it rather than carrying it along.

layout(location = 0) out vec2 vCorner;
layout(location = 1) out float vFade;
layout(location = 2) out float vAlong;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 wrap;   // xyz: the camera, wrapped to the box; w: the box's size
    vec4 motion; // x: seconds; y: how fast it falls; zw: the wind
    vec4 shape;  // x: half its length; y: half its width; z: 1 for snow; w: how thick it's falling
    vec4 light;  // what it's lit by; w: the near distance
} pc;

vec3 hash(int i)
{
    uvec3 v = uvec3(i, i * 16807, i * 48271) * 1597334677u;
    v = (v.yzx ^ (v >> 15u)) * 3812015801u;
    v = (v.zxy ^ (v >> 15u)) * 1597334677u;
    return vec3(v) / 4294967295.0;
}

void main()
{
    const vec2 Corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
    vec2 corner = Corners[gl_VertexIndex];
    vec3 r = hash(gl_InstanceIndex);
    float box = pc.wrap.w;
    float t = pc.motion.x;
    float speed = pc.motion.y * (0.8 + 0.4 * r.y);

    vec3 moved = vec3(pc.motion.z * t, -speed * t, pc.motion.w * t);
    if (pc.shape.z > 0.5)
        moved.xz += vec2(sin(t * 0.9 + r.x * 40.0), cos(t * 0.7 + r.z * 40.0)) * 0.5;
    vec3 place = mod(r * box + moved - pc.wrap.xyz, box) - box * 0.5;

    vec3 fall = normalize(vec3(pc.motion.z, -speed, pc.motion.w));
    vec3 side = normalize(cross(fall, place));
    vec3 along = pc.shape.z > 0.5 ? normalize(cross(side, place)) : fall;
    float distance2 = length(place);
    // Never thinner than about a pixel, or it would shimmer in and out.
    float thick = max(pc.shape.y, distance2 * (pc.shape.z > 0.5 ? 0.0011 : 0.0016));
    float tall = pc.shape.z > 0.5 ? thick : pc.shape.x;
    vec3 relative = place + side * corner.x * thick + along * corner.y * tall;

    vFade = smoothstep(0.4, 2.0, distance2) * (1.0 - smoothstep(box * 0.32, box * 0.5, distance2));
    // Thinner falling means fewer of them, not fainter ones.
    if (r.z > pc.shape.w)
        vFade = 0.0;
    vCorner = corner;
    gl_Position = pc.viewProjection * vec4(relative, 1.0);
    vAlong = gl_Position.w;
}
