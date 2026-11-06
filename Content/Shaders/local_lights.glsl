// Lamps, headlights and signals, found through a grid over the view: each cell lists the lights that can reach it (LocalLights.cs),
// so a pixel only looks at a few of however many there are. Include after shadows.glsl, which has the grid's measurements.

struct LocalLight
{
    vec4 placeRange;   // relative to the camera; how far it reaches
    vec4 colorOuter;   // its colour one metre away; the cosine of where its beam ends, -1 for all round
    vec4 facingInner;  // the way it points; the cosine of where its beam starts to fade
};

layout(std430, set = 2, binding = 4) readonly buffer LocalLightList
{
    LocalLight localLights[];
};

// Sixteen bytes a cell: how many, then up to fifteen lights' numbers.
layout(std430, set = 2, binding = 5) readonly buffer LocalLightCells
{
    uint localCells[];
};

const int CellsAcross = 16;
const int CellsDown = 9;
const int CellSlices = 16;

vec3 shadeLocalLights(Surface s, vec3 relative)
{
    if (shadow.lights.w < 0.5)
        return vec3(0.0);

    float along = dot(relative, shadow.forward.xyz);
    if (along <= 0.0)
        return vec3(0.0);

    int x = clamp(int(gl_FragCoord.x * shadow.lights.x), 0, CellsAcross - 1);
    int y = clamp(int(gl_FragCoord.y * shadow.lights.y), 0, CellsDown - 1);
    int slice = clamp(int(log(max(along, 1.0)) * shadow.lights.z), 0, CellSlices - 1);
    int cell = ((slice * CellsDown + y) * CellsAcross + x) * 4;

    uint count = localCells[cell] & 255u;
    vec3 sum = vec3(0.0);
    for (uint i = 0u; i < count; i++)
    {
        uint place = i + 1u;
        uint number = (localCells[cell + int(place / 4u)] >> (place % 4u * 8u)) & 255u;
        LocalLight light = localLights[number];

        vec3 to = light.placeRange.xyz - relative;
        float d2 = dot(to, to);
        float range = light.placeRange.w;
        if (d2 >= range * range)
            continue;

        vec3 l = to / sqrt(max(d2, 1e-6));
        float falling = d2 / (range * range);
        falling = clamp(1.0 - falling * falling, 0.0, 1.0);
        float strength = falling * falling / (d2 + 1.0);
        if (light.colorOuter.w > -0.5)
            strength *= smoothstep(light.colorOuter.w, light.facingInner.w, dot(-l, light.facingInner.xyz));

        if (strength > 0.0)
            sum += shadeLight(s, l, light.colorOuter.rgb * strength);
    }

    return sum;
}
