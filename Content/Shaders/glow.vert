#version 450
// The glow of a lamp seen from anywhere: a quad facing the camera, never smaller than a few pixels, so a signal still reads far away.

layout(location = 0) out vec2 vCorner;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 placeSize; // relative to the camera; how wide, in metres
    vec4 color;     // w: the smallest it gets, as a share of how far away it is
    vec4 screenX;   // the view's across
    vec4 screenY;   // the view's up
} pc;

void main()
{
    const vec2 Corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
    vec2 corner = Corners[gl_VertexIndex];
    float size = max(pc.placeSize.w, length(pc.placeSize.xyz) * pc.color.w);
    vCorner = corner;
    // A little towards the camera, so a lamp isn't swallowed by its own housing.
    vec3 place = pc.placeSize.xyz * (1.0 - 0.3 / max(length(pc.placeSize.xyz), 1.0));
    gl_Position = pc.viewProjection * vec4(place + (pc.screenX.xyz * corner.x + pc.screenY.xyz * corner.y) * size, 1.0);
}
