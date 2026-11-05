// What the ambient occlusion passes share. Define OCCLUSION_SET and OCCLUSION_BINDING first.

layout(set = OCCLUSION_SET, binding = OCCLUSION_BINDING) uniform OcclusionUniforms
{
    vec4 right;   // across the view, scaled to its edge
    vec4 up;      // up the view, scaled to its edge
    vec4 forward; // w: the near distance
} occlusion;

// How far along the view a depth is. The depth buffer is reversed, with no far plane; 0 is the sky.
float alongView(float depth)
{
    return occlusion.forward.w / max(depth, 1e-8);
}

// A pixel's place relative to the camera.
vec3 placeAt(vec2 uv, float depth)
{
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return (occlusion.forward.xyz + ndc.x * occlusion.right.xyz + ndc.y * occlusion.up.xyz) * alongView(depth);
}

// A surface's normal from the depth round it, taking on each axis the neighbour that lies on the same surface.
vec3 normalAt(sampler2D depths, ivec2 pixel, ivec2 size, vec3 here)
{
    vec3 near[4];
    const ivec2 offsets[4] = ivec2[](ivec2(-1, 0), ivec2(1, 0), ivec2(0, -1), ivec2(0, 1));
    for (int i = 0; i < 4; i++)
    {
        ivec2 at = clamp(pixel + offsets[i], ivec2(0), size - 1);
        near[i] = placeAt((vec2(at) + 0.5) / vec2(size), texelFetch(depths, at, 0).r);
    }

    vec3 dx = dot(near[0] - here, near[0] - here) < dot(near[1] - here, near[1] - here) ? here - near[0] : near[1] - here;
    vec3 dy = dot(near[2] - here, near[2] - here) < dot(near[3] - here, near[3] - here) ? here - near[2] : near[3] - here;
    vec3 n = cross(dx, dy);
    float length2 = dot(n, n);
    if (length2 < 1e-16)
        return vec3(0.0, 1.0, 0.0);

    n /= sqrt(length2);
    return dot(n, here) > 0.0 ? -n : n;
}
