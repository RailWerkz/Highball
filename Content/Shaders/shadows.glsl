// The sun's shadows, from SunShadows (set 2), and the clouds' (set 3). Positions are relative to the camera, as everywhere in the
// world shaders.
#include "clouds.glsl"

layout(set = 2, binding = 0) uniform ShadowUniforms
{
    mat4 cascades[4];
    vec4 splits;  // how far each cascade reaches along the view
    vec4 forward; // the view's direction; w: 1 when there are shadows to read
    vec4 texels;  // each cascade's texel, in metres
    vec4 horizon; // xy: the horizon map's middle relative to the camera; z: 1 / its width; w: 1 when there is one
    vec4 lights;  // the lamps' grid: cells per pixel across and down; slices per log of a distance; w: 1 when there are lamps
    vec4 weather; // x: how wet; y: how much snow lies
} shadow;

layout(set = 2, binding = 1) uniform sampler2DArrayShadow shadowMap;
// How high the land rises round each point, in eight directions from east going round towards +z, as the sine of the angle.
layout(set = 2, binding = 2) uniform sampler2D horizonMap0;
layout(set = 2, binding = 3) uniform sampler2D horizonMap1;

// How lit a point is in one cascade, 0 to 1: nine filtered taps, for an edge a couple of texels soft.
float shadowInCascade(int cascade, vec3 relative, vec3 n, float nl)
{
    // Off the surface along its normal, more when the sun grazes it, so it doesn't shadow itself.
    vec3 lifted = relative + n * shadow.texels[cascade] * (1.0 + 2.0 * (1.0 - nl));
    vec4 p = shadow.cascades[cascade] * vec4(lifted, 1.0);
    // The world's viewport is flipped so +y is up, and these were drawn the same way.
    vec2 uv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);

    vec2 step = vec2(1.0) / vec2(textureSize(shadowMap, 0).xy);
    float lit = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
            lit += texture(shadowMap, vec4(uv + vec2(x, y) * step, float(cascade), p.z));
    }

    return lit / 9.0;
}

// How much of the sun reaches a point past what's on the ground. Cascades blend over the last tenth of their reach, and the
// last fades out.
float groundShadow(vec3 relative, vec3 n, vec3 sun)
{
    if (shadow.forward.w < 0.5)
        return 1.0;

    float nl = max(dot(n, sun), 0.0);
    if (nl <= 0.0)
        return 1.0; // already facing away: no direct light for a shadow to take

    float depth = dot(relative, shadow.forward.xyz);
    for (int c = 0; c < 4; c++)
    {
        float reach = shadow.splits[c];
        if (depth >= reach)
            continue;

        float lit = shadowInCascade(c, relative, n, nl);
        float edge = smoothstep(reach * 0.9, reach, depth);
        if (edge > 0.0)
            lit = mix(lit, c < 3 ? shadowInCascade(c + 1, relative, n, nl) : 1.0, edge);
        return lit;
    }

    return 1.0;
}

// How much of the sun gets past the land between it and a point, however far away that land is. Softly, since the map is coarse, the
// sun isn't a point, and the point itself may stand a little above or below the map's idea of the ground.
float horizonShadow(vec3 relative, vec3 sun)
{
    if (shadow.horizon.w < 0.5)
        return 1.0;

    vec2 uv = (relative.xz - shadow.horizon.xy) * shadow.horizon.z + 0.5;
    vec2 edges = min(uv, 1.0 - uv);
    float inside = smoothstep(0.0, 0.03, min(edges.x, edges.y));
    if (inside <= 0.0)
        return 1.0;

    float around = atan(sun.z, sun.x) / (3.14159265 / 4.0);
    around = around < 0.0 ? around + 8.0 : around;
    int low = int(around) % 8;
    int high = (low + 1) % 8;
    vec4 east = texture(horizonMap0, uv);
    vec4 west = texture(horizonMap1, uv);
    float a = low < 4 ? east[low] : west[low - 4];
    float b = high < 4 ? east[high] : west[high - 4];
    float rise = mix(a, b, fract(around));
    if (rise <= 0.0)
        return 1.0;

    return mix(1.0, smoothstep(rise - 0.006, rise + 0.014, sun.y), inside);
}

// How much of the sun reaches a point, 0 to 1.
float sunShadow(vec3 relative, vec3 n, vec3 sun)
{
    return groundShadow(relative, n, sun) * horizonShadow(relative, sun) * cloudSunShadow(relative);
}
