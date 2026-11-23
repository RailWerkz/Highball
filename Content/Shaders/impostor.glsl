// Octahedral impostors (after SmartImposter's, MIT): a tree baked once from Frames x Frames directions over the upper
// hemisphere (ImpostorBaker), each frame its colour and cut-out, and its normal and depth, of the tree seen from there, framed on
// its bounding sphere. A far tree is then one quad facing the camera that looks up the frame nearest the way it's seen and
// marches that frame's depth, so it has its real silhouette and parallax from any side.
//
// Model space here is the tree's own, turned as its instance is, with its sphere's centre at the origin and its radius 1.

const float Frames = 12.0;
// Each frame's width in texels (ImpostorBaker.FrameSize).
const float FramePixels = 96.0;

// A direction in the upper hemisphere and where it lands in the frames' square, -1 to 1 each way (hemi-octahedral).
vec2 hemiOctEncode(vec3 v)
{
    v.y = max(v.y, 0.0);
    vec3 a = abs(v);
    vec2 p = v.xz / (a.x + a.y + a.z);
    return vec2(p.x + p.y, p.x - p.y);
}

vec3 hemiOctDecode(vec2 g)
{
    vec2 p = vec2(g.x + g.y, g.x - g.y) * 0.5;
    return normalize(vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y));
}

// Which way, towards the viewer, a frame was baked from.
vec3 frameView(vec2 frame)
{
    return hemiOctDecode((frame + 0.5) / Frames * 2.0 - 1.0);
}

// The frame's picture plane: right and up as it was baked (ImpostorBaker, impostor_bake.vert).
void frameBasis(vec3 view, out vec3 right, out vec3 up)
{
    right = normalize(cross(vec3(0.0, 1.0, 0.0), view));
    up = cross(view, right);
}

// A point in model space and where it falls in the atlas in a frame.
vec2 frameUv(vec2 frame, vec3 p, vec3 right, vec3 up)
{
    vec2 local = vec2(0.5 + 0.5 * dot(p, right), 0.5 - 0.5 * dot(p, up));
    return (frame + clamp(local, vec2(0.5 / FramePixels), vec2(1.0 - 0.5 / FramePixels))) / Frames;
}

// What a pixel of an impostor sees: o, where its ray meets the quad and d, the ray, both in model space. One frame is picked for
// the pixel, at random weighted as the four round the view would be blended (seen over a few frames, the temporal pass blends
// them), and the ray marched through that frame's depth to where it first meets the tree. False where it misses.
// depthMap: normal in rgb, depth (towards the viewer, -1 to 1 over the sphere, 0 where empty) in a. albedoMap's a: coverage.
bool impostorHit(sampler2D albedoMap, sampler2D depthMap, vec3 o, vec3 d, vec2 randoms, float footprint, out vec2 uv, out vec3 hit, out vec4 albedo)
{
    vec2 g = hemiOctEncode(-d);
    vec2 cell = (g * 0.5 + 0.5) * Frames - 0.5;
    vec2 base = floor(cell);
    vec2 f = cell - base;
    vec2 frame = clamp(base + vec2(randoms.x < f.x ? 1.0 : 0.0, randoms.y < f.y ? 1.0 : 0.0), vec2(0.0), vec2(Frames - 1.0));

    vec3 view = frameView(frame);
    vec3 right, up;
    frameBasis(view, right, up);
    float along = dot(d, view);
    if (along > -1e-3)
        return false;

    // The ray from where it's level with the front of the sphere to where it's level with the back, in even steps, for the first
    // place it's behind the surface, then between that and the step before.
    float start = dot(o, view);
    float t0 = (1.0 - start) / along;
    float t1 = (-1.0 - start) / along;
    vec2 grad = vec2(footprint * 0.5 / Frames, 0.0);
    const int Steps = 8;
    float before = t0;
    float gapBefore = 1.0;
    float t = t0;
    bool found = false;
    for (int i = 1; i <= Steps; i++)
    {
        t = mix(t0, t1, float(i) / float(Steps));
        vec3 p = o + d * t;
        float surface = textureGrad(depthMap, frameUv(frame, p, right, up), grad, grad.yx).a * 2.0 - 1.0;
        float gap = dot(p, view) - surface;
        if (gap <= 0.0)
        {
            // Between this step and the last, where the gap closes.
            t = mix(before, t, gapBefore / max(gapBefore - gap, 1e-5));
            found = true;
            break;
        }
        before = t;
        gapBefore = gap;
    }
    if (!found)
        return false;

    hit = o + d * t;
    uv = frameUv(frame, hit, right, up);
    albedo = textureGrad(albedoMap, uv, grad, grad.yx);
    return albedo.a >= 0.35;
}

// A quad's ray in model space: the instance's turn undone (as plant.vert turns it: by angle, about the vertical).
vec3 intoModel(vec3 v, vec2 turn)
{
    return vec3(v.x * turn.x + v.z * turn.y, v.y, -v.x * turn.y + v.z * turn.x);
}

vec3 outOfModel(vec3 v, vec2 turn)
{
    return vec3(v.x * turn.x - v.z * turn.y, v.y, v.x * turn.y + v.z * turn.x);
}
