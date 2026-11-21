#ifndef HABLE_TONEMAPPING_GLSL_INCLUDED
#define HABLE_TONEMAPPING_GLSL_INCLUDED

// GLSL port of Hable tonemapping curve, from public domain C++ code
// From "filmicworlds.com/blog/filmic-tonemapping-with-piecewise-power-curves"
// The curve is built on the CPU (Highball/World/Tonemapper.cs, HableCurve) and handed over in PostUniforms.hable.

struct HableSegment
{
    float offsetX;
    float offsetY;
    float scaleX;
    float scaleY;
    float lnA;
    float b;
};

struct HableTonemapUniforms
{
    HableSegment segments[3];
    float invW;
    float x0;
    float x1;
    float padding0;
};

float EvalHableSegment(float x, HableSegment segment)
{
    float x0 = (x - segment.offsetX) * segment.scaleX;
    float y0 = (x0 > 0.0) ? exp(segment.lnA + segment.b * log(x0)) : 0.0;
    return y0 * segment.scaleY + segment.offsetY;
}

float EvalHableCurve(float x, HableTonemapUniforms hableUniforms)
{
    uint segmentIndex = (x < hableUniforms.x0) ? 0u : ((x < hableUniforms.x1) ? 1u : 2u);
    return EvalHableSegment(x, hableUniforms.segments[segmentIndex]);
}

vec3 HableTonemap(vec3 color, float exposure, float gamma, HableTonemapUniforms hableUniforms)
{
    color *= exposure;

    color *= hableUniforms.invW;

    color.x = EvalHableCurve(color.x, hableUniforms);
    color.y = EvalHableCurve(color.y, hableUniforms);
    color.z = EvalHableCurve(color.z, hableUniforms);

    return pow(clamp(color, 0.0, 1.0), vec3(1.0 / gamma));
}

#endif // HABLE_TONEMAPPING_GLSL_INCLUDED
