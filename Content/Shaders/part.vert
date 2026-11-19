#version 450

layout(location = 0) in vec3 inPosition; // the model: x across the track, y up, z along
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inPlace;    // where this one stands relative to the chunk, and a random tint
layout(location = 4) in vec4 inForward;  // the way its z faces, and how much longer across it is (0: as made)

layout(location = 0) out vec3 vRelative;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out float vTint;
layout(location = 4) out vec3 vModel;
layout(location = 5) out vec3 vModelNormal;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 offset;
    vec4 camera; // w: a pixel's angle at the middle of the view, radians (WorldRenderer)
    vec4 sun;
} pc;

// NetworkMeshes.TieReach and TieSpacing, and the fewest pixels apart a tie model's drawn at (line.frag's too).
const float TieReach = 1000.0;
const float TieSpacing = 0.495;
const float TieModelPixels = 4.0;

void main()
{
    vec3 forward = normalize(inForward.xyz);
    vec3 right = normalize(cross(forward, vec3(0.0, 1.0, 0.0)));
    vec3 up = cross(right, forward);
    mat3 basis = mat3(right, up, forward);

    vec3 model = vec3(inPosition.x * (inForward.w > 0.0 ? inForward.w : 1.0), inPosition.yz);
    vec3 relative = inPlace.xyz + basis * model + pc.offset.xyz;
    vRelative = relative;
    vNormal = basis * inNormal;
    vUv = inUv;
    vTint = inPlace.w;
    vModel = model;
    vModelNormal = inNormal;
    // Pulled a fraction towards the camera like the rest of the track (see line.vert).
    gl_Position = pc.viewProjection * vec4(relative * 0.9985, 1.0);
    // Where ties lie closer together on screen than TieModelPixels the whole tie's left out, and the ballast paints them (line.frag,
    // by the same measure): smaller, a tie and the gap beside it share pixels, and which one each pixel lands on flips as the view
    // moves, the track's crawling stripes. Their spacing on screen: TieSpacing, foreshortened by how end-on it's seen (the way
    // along the track against the way to it), over how many metres a pixel spans there (camera.w: a pixel's angle). And past
    // TieReach in any case. Left out here rather than by discarding in part.frag, which would cost every pixel it draws its early
    // depth test.
    vec3 centre = inPlace.xyz + pc.offset.xyz;
    float away = length(centre);
    float across = sqrt(max(1.0 - pow(dot(forward, centre / max(away, 1e-3)), 2.0), 0.0));
    float apart = TieSpacing * across / max(away * pc.camera.w, 1e-6);
    if (away > TieReach || apart < TieModelPixels)
        gl_Position = vec4(0.0, 0.0, 0.0, -1.0);
}
