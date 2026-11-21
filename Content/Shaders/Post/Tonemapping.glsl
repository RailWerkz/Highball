// Library of tonemapping functions. tonemap.frag picks one by PostUniforms.mapping.x (Highball/World/Tonemapper.cs).

#include "HableTonemapping.glsl"

vec3 tonemapLinear(in vec3 color, in float exposure, in float gamma)
{
    color = clamp(exposure * color, 0., 1.);
    color = pow(color, vec3(1. / gamma));
    return color;
}

vec3 tonemapReinhardSimple(in vec3 color, in float exposure, in float gamma)
{
    color *= exposure/(1. + color / exposure);
    color = pow(color, vec3(1. / gamma));
    return clamp(color, 0.0, 1.0);
}

vec3 tonemapReinhardLuma(in vec3 color, in float exposure, in float gamma)
{
    color *= exposure;
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    float toneMappedLuma = luma / (1. + luma);
    color *= toneMappedLuma / luma;
    color = pow(clamp(color, 0.0, 1.0), vec3(1. / gamma));
    return color;
}

vec3 tonemapUncharted2(in vec3 color, in float exposure, in float gamma)
{

    float A = 0.15;
    float B = 0.50;
    float C = 0.10;
    float D = 0.20;
    float E = 0.02;
    float F = 0.30;
    float W = 11.2;

    color *= exposure;
    color = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
    float white = ((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F;
    color /= white;
    color = pow(clamp(color, 0.0, 1.0), vec3(1. / gamma));
    return color;
}

// (Hejl and Burgess-Dawson: the display's gamma is built into the curve.)
vec3 tonemapFilmic(in vec3 color, in float exposure)
{
    color *= exposure;
    color = max(vec3(0.), color - vec3(0.004));
    color = (color * (6.2 * color + .5)) / (color * (6.2 * color + 1.7) + 0.06);
    return clamp(color, 0.0, 1.0);
}

vec3 tonemapRomBinDaHouse(in vec3 color, in float exposure, in float gamma)
{
    color *= exposure;
    color = exp( -1.0 / ( 2.72*color + 0.15 ) );
    color = pow(clamp(color, 0.0, 1.0), vec3(1. / gamma));
    return color;
}

vec3 tonemapACES(in vec3 color, in float exposure)
{
    color *= exposure;
    // Narkowicz 2015, "ACES Filmic Tone Mapping Curve"
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    color =  (color * (a * color + b)) / (color * (c * color + d) + e);
    return clamp(color, 0.0, 1.0);
}

// sRGB => XYZ => D65_2_D60 => AP1 => RRT_SAT
const mat3 ACESInputMat =
{
    {0.59719, 0.07600, 0.02840},
    {0.35458, 0.90834, 0.13383},
    {0.04823, 0.01566, 0.83777}
};

// ODT_SAT => XYZ => D60_2_D65 => sRGB
const mat3 ACESOutputMat =
{
    { 1.60475, -0.10208, -0.00327},
    {-0.53108,  1.10813, -0.07276},
    {-0.07367, -0.00605,  1.07602}
};

vec3 RRTAndODTFit(in vec3 v)
{
    vec3 a = v * (v + 0.0245786f) - 0.000090537f;
    vec3 b = v * (0.983729f * v + 0.4329510f) + 0.238081f;
    return a / b;
}

vec3 tonemapACESFitted(vec3 color, in float exposure)
{
    color *= exposure;
    color = ACESInputMat * color;
    // Apply RRT and ODT
    color = RRTAndODTFit(color);
    color = ACESOutputMat * color;
    return clamp(color, 0.0, 1.0);
}


// Minimal AGX implementation from: https://iolite-engine.com/blog_posts/minimal_agx_implementation
// MIT licensed

#define AGX_LOOK 0

// Mean error^2: 3.6705141e-06
vec3 agxDefaultContrastApprox(vec3 x)
{
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;

    return + 15.5     * x4 * x2
    - 40.14    * x4 * x
    + 31.96    * x4
    - 6.868    * x2 * x
    + 0.4298   * x2
    + 0.1191   * x
    - 0.00232;
}

vec3 agx(vec3 val)
{
    const mat3 agx_mat = mat3(
    0.842479062253094, 0.0423282422610123, 0.0423756549057051,
    0.0784335999999992,  0.878468636469772,  0.0784336,
    0.0792237451477643, 0.0791661274605434, 0.879142973793104);

    const float min_ev = -12.47393f;
    const float max_ev = 4.026069f;

    // Input transform (inset)
    val = agx_mat * val;

    // Log2 space encoding
    val = clamp(log2(val), min_ev, max_ev);
    val = (val - min_ev) / (max_ev - min_ev);

    // Apply sigmoid function approximation
    val = agxDefaultContrastApprox(val);

    return val;
}

vec3 agxEotf(vec3 val)
{
    const mat3 agx_mat_inv = mat3(
    1.19687900512017, -0.0528968517574562, -0.0529716355144438,
    -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
    -0.0990297440797205, -0.0989611768448433, 1.15107367264116);

    // Inverse input transform (outset)
    val = agx_mat_inv * val;

    // sRGB IEC 61966-2-1 2.2 Exponent Reference EOTF Display
    // NOTE: We're linearizing the output here. Comment/adjust when
    // *not* using a sRGB render target
    // (Kept from below zero, where pow is undefined: the outset can push a channel just under it.)
    val = pow(max(val, vec3(0.0)), vec3(2.2));

    return val;
}

vec3 agxLook(vec3 val)
{
    // Default
    vec3 offset = vec3(0.0);
    vec3 slope = vec3(1.0);
    vec3 power = vec3(1.0);
    float sat = 1.0;

    #if AGX_LOOK == 1
        // Golden
        slope = vec3(1.0, 0.9, 0.5);
        power = vec3(0.8);
        sat = 0.8;
    #elif AGX_LOOK == 2
        // Punchy
        slope = vec3(1.0);
        power = vec3(1.35, 1.35, 1.35);
        sat = 1.4;
    #endif

    // ASC CDL
    val = pow(val * slope + offset, power);

    const vec3 lw = vec3(0.2126, 0.7152, 0.0722);
    float luma = dot(val, lw);

    return luma + sat * (val - luma);
}


// -----------------------------------------------------------------------------
// GT7 Tone Mapping
//
// GLSL port of the sample implementation published by Polyphony Digital Inc.
// (MIT License, Copyright (c) 2025 Polyphony Digital Inc.) alongside
// "Physically Based Tone Mapping in Gran Turismo 7", SIGGRAPH 2025 Advances in
// Real-Time Rendering course:
//   https://blog.selfshadow.com/publications/s2025-shading-course/pdi/supplemental/gt7_tone_mapping.cpp
//   https://s3.amazonaws.com/gran-turismo.com/pdi_publications/s2025_PBS_Physically_Based_Tone_Mapping_GT7.pdf
//
// The operator works in the GT frame-buffer scale where 1.0 = 100 cd/m^2 and
// expects linear Rec.2020 input. For SDR output it tone maps against a paper
// white of 250 cd/m^2 and divides the result by 2.5 so that 1.0 is sRGB white.
//
// Strategy (paper, slides 145-150):
//   1. "Twisted" colour: apply the GT Tone Mapping V2 curve per RGB channel.
//   2. Convert both the input and the twisted colour to a uniform colour space
//      (ICtCp by default, Jzazbz optional).
//   3. "Untwisted" colour: keep the twisted luminance, fade the input chroma
//      towards white as luminance approaches the peak, convert back to RGB.
//   4. Blend twisted and untwisted RGB.
// -----------------------------------------------------------------------------

#define GT7_UCS_ICTCP  0
#define GT7_UCS_JZAZBZ 1
#ifndef GT7_UCS
#define GT7_UCS GT7_UCS_ICTCP
#endif

// Frame-buffer value 1.0 corresponds to this physical luminance in cd/m^2.
const float GT7_REFERENCE_LUMINANCE = 100.0;
// SDR paper white assumed by the GT7 curve parameters, in cd/m^2.
const float GT7_SDR_PAPER_WHITE = 250.0;
// Scene value (after exposure) that maps to SDR paper white. The other
// tonemappers in this file treat an exposed value of 1.0 as display white, so
// 1.0 is placed at the GT paper white to keep the exposure slider comparable.
const float GT7_SCENE_WHITE_SCALE = GT7_SDR_PAPER_WHITE / GT7_REFERENCE_LUMINANCE;

// Default look parameters from the reference implementation.
const float GT7_BLEND_RATIO = 0.6;  // 0 = per-channel only, 1 = hue preserving only
const float GT7_FADE_START  = 0.98; // chroma fade start, relative to peak luminance in UCS
const float GT7_FADE_END    = 1.16; // chroma fade end

// Jzazbz scales the PQ exponent.
const float GT7_JZAZBZ_EXPONENT_SCALE = 1.7;

// Linear Rec.709 <-> linear Rec.2020 (D65). Columns listed, as the mat3 constructor expects.
const mat3 GT7_REC709_TO_REC2020 = mat3(
    0.6274040, 0.0690970, 0.0163916,
    0.3292820, 0.9195400, 0.0880132,
    0.0433136, 0.0113612, 0.8955950);

const mat3 GT7_REC2020_TO_REC709 = mat3(
     1.6604910, -0.1245505, -0.0181508,
    -0.5876411,  1.1328999, -0.1005789,
    -0.0728499, -0.0083494,  1.1187297);

// -----------------------------------------------------------------------------
// "GT Tone Mapping" curve V2 with convergent shoulder.
// -----------------------------------------------------------------------------
struct Gt7Curve
{
    float peakIntensity;
    float midPoint;
    float linearSection;
    float toeStrength;
    float kA;
    float kB;
    float kC;
};

Gt7Curve gt7InitializeCurve(float monitorIntensity, float alpha, float grayPoint, float linearSection, float toeStrength)
{
    Gt7Curve curve;
    curve.peakIntensity = monitorIntensity;
    curve.midPoint      = grayPoint;
    curve.linearSection = linearSection;
    curve.toeStrength   = toeStrength;

    // Pre-compute constants for the shoulder region.
    float k  = (linearSection - 1.0) / (alpha - 1.0);
    curve.kA = monitorIntensity * linearSection + monitorIntensity * k;
    curve.kB = -monitorIntensity * k * exp(linearSection / k);
    curve.kC = -1.0 / (k * monitorIntensity);
    return curve;
}

float gt7EvaluateCurve(Gt7Curve curve, float x)
{
    if (x < 0.0)
    {
        return 0.0;
    }

    if (x < curve.linearSection * curve.peakIntensity)
    {
        float weightLinear = smoothstep(0.0, curve.midPoint, x);
        float weightToe    = 1.0 - weightLinear;
        float toeMapped    = curve.midPoint * pow(x / curve.midPoint, curve.toeStrength);
        return weightToe * toeMapped + weightLinear * x;
    }

    // Shoulder mapping for highlights.
    return curve.kA + curve.kB * exp(x * curve.kC);
}

vec3 gt7EvaluateCurve(Gt7Curve curve, vec3 rgb)
{
    return vec3(gt7EvaluateCurve(curve, rgb.r), gt7EvaluateCurve(curve, rgb.g), gt7EvaluateCurve(curve, rgb.b));
}

// -----------------------------------------------------------------------------
// EOTF / inverse-EOTF for ST-2084 (PQ), on frame-buffer values (1.0 = 100 cd/m^2).
// exponentScaleFactor scales the m2 exponent, which Jzazbz requires.
// -----------------------------------------------------------------------------
const float GT7_PQ_M1 = 0.1593017578125; // (2610 / 4096) / 4
const float GT7_PQ_M2 = 78.84375;        // (2523 / 4096) * 128
const float GT7_PQ_C1 = 0.8359375;       // 3424 / 4096
const float GT7_PQ_C2 = 18.8515625;      // (2413 / 4096) * 32
const float GT7_PQ_C3 = 18.6875;         // (2392 / 4096) * 32
const float GT7_PQ_PEAK = 10000.0;       // Maximum luminance supported by PQ (cd/m^2)

vec3 gt7EotfSt2084(vec3 n, float exponentScaleFactor)
{
    n = clamp(n, 0.0, 1.0);
    float m2 = GT7_PQ_M2 * exponentScaleFactor;
    vec3 np = pow(n, vec3(1.0 / m2));
    vec3 l  = max(np - GT7_PQ_C1, 0.0);
    l = l / (GT7_PQ_C2 - GT7_PQ_C3 * np);
    l = pow(l, vec3(1.0 / GT7_PQ_M1));
    return l * (GT7_PQ_PEAK / GT7_REFERENCE_LUMINANCE);
}

vec3 gt7InverseEotfSt2084(vec3 v, float exponentScaleFactor)
{
    float m2 = GT7_PQ_M2 * exponentScaleFactor;
    vec3 y  = max(v, 0.0) * (GT7_REFERENCE_LUMINANCE / GT7_PQ_PEAK);
    vec3 ym = pow(y, vec3(GT7_PQ_M1));
    return exp2(m2 * (log2(GT7_PQ_C1 + GT7_PQ_C2 * ym) - log2(1.0 + GT7_PQ_C3 * ym)));
}

// -----------------------------------------------------------------------------
// ICtCp conversion (ITU-T T.302). Input / output: linear Rec.2020.
// -----------------------------------------------------------------------------
vec3 gt7RgbToICtCp(vec3 rgb)
{
    vec3 lms = vec3(
        dot(rgb, vec3(1688.0, 2146.0,  262.0)),
        dot(rgb, vec3( 683.0, 2951.0,  462.0)),
        dot(rgb, vec3(  99.0,  309.0, 3688.0))) / 4096.0;

    vec3 pq = gt7InverseEotfSt2084(lms, 1.0);

    return vec3(
        dot(pq, vec3( 2048.0,   2048.0,    0.0)),
        dot(pq, vec3( 6610.0, -13613.0, 7003.0)),
        dot(pq, vec3(17933.0, -17390.0, -543.0))) / 4096.0;
}

vec3 gt7ICtCpToRgb(vec3 ictcp)
{
    vec3 pq = vec3(
        ictcp.x + 0.00860904 * ictcp.y + 0.11103   * ictcp.z,
        ictcp.x - 0.00860904 * ictcp.y - 0.11103   * ictcp.z,
        ictcp.x + 0.560031   * ictcp.y - 0.320627  * ictcp.z);

    vec3 lms = gt7EotfSt2084(pq, 1.0);

    return max(vec3(
        dot(lms, vec3( 3.43661,   -2.50645,    0.0698454)),
        dot(lms, vec3(-0.79133,    1.9836,    -0.192271)),
        dot(lms, vec3(-0.0259499, -0.0989137,  1.12486))), 0.0);
}

// -----------------------------------------------------------------------------
// Jzazbz conversion (Safdar et al. 2017), coefficients adjusted for linear Rec.2020.
// -----------------------------------------------------------------------------
vec3 gt7RgbToJzazbz(vec3 rgb)
{
    vec3 lms = vec3(
        dot(rgb, vec3(0.530004, 0.355704, 0.086090)),
        dot(rgb, vec3(0.289388, 0.525395, 0.157481)),
        dot(rgb, vec3(0.091098, 0.147588, 0.734234)));

    vec3 pq = gt7InverseEotfSt2084(lms, GT7_JZAZBZ_EXPONENT_SCALE);

    float iz = 0.5 * pq.x + 0.5 * pq.y;

    return vec3(
        (0.44 * iz) / (1.0 - 0.56 * iz) - 1.6295499532821566e-11,
        dot(pq, vec3(3.524000, -4.066708,  0.542708)),
        dot(pq, vec3(0.199076,  1.096799, -1.295875)));
}

vec3 gt7JzazbzToRgb(vec3 jab)
{
    float jz = jab.x + 1.6295499532821566e-11;
    float iz = jz / (0.44 + 0.56 * jz);

    vec3 pq = vec3(
        iz + jab.y *  0.1386050432715393  + jab.z *  0.05804731615611869,
        iz + jab.y * -0.1386050432715393  + jab.z * -0.05804731615611869,
        iz + jab.y * -0.09601924202631895 + jab.z * -0.8118918960560390);

    vec3 lms = gt7EotfSt2084(pq, GT7_JZAZBZ_EXPONENT_SCALE);

    return vec3(
        dot(lms, vec3( 2.990669, -2.049742,  0.088977)),
        dot(lms, vec3(-1.634525,  3.145627, -0.483037)),
        dot(lms, vec3(-0.042505, -0.377983,  1.448019)));
}

// -----------------------------------------------------------------------------
// Unified colour space (UCS): ICtCp or Jzazbz. Component x is luminance.
// -----------------------------------------------------------------------------
vec3 gt7RgbToUcs(vec3 rgb)
{
#if GT7_UCS == GT7_UCS_JZAZBZ
    return gt7RgbToJzazbz(rgb);
#else
    return gt7RgbToICtCp(rgb);
#endif
}

vec3 gt7UcsToRgb(vec3 ucs)
{
#if GT7_UCS == GT7_UCS_JZAZBZ
    return gt7JzazbzToRgb(ucs);
#else
    return gt7ICtCpToRgb(ucs);
#endif
}

// -----------------------------------------------------------------------------
// GT7 tone mapping against a peak luminance.
// Input:  linear Rec.2020 frame-buffer values (1.0 = 100 cd/m^2)
// Output: linear Rec.2020 frame-buffer values in [0, peak]
// -----------------------------------------------------------------------------
vec3 gt7ToneMap(vec3 rgb, float peak)
{
    // Curve parameters from the reference implementation (slightly different from GT Sport).
    Gt7Curve curve = gt7InitializeCurve(peak, 0.25, 0.538, 0.444, 1.280);

    // Peak luminance expressed in UCS, used to normalise the chroma fade.
    float peakUcs = gt7RgbToUcs(vec3(peak)).x;

    // Convert to UCS to separate luminance and chroma.
    vec3 ucs = gt7RgbToUcs(rgb);

    // Per-channel tone mapping ("twisted" colour).
    vec3 skewedRgb = gt7EvaluateCurve(curve, rgb);
    vec3 skewedUcs = gt7RgbToUcs(skewedRgb);

    // Whiten the input chroma as luminance approaches the peak, and pair it
    // with the twisted luminance for a smoother highlight ("untwisted" colour).
    float chromaScale = 1.0 - smoothstep(GT7_FADE_START, GT7_FADE_END, ucs.x / peakUcs);
    vec3 scaledRgb = gt7UcsToRgb(vec3(skewedUcs.x, ucs.yz * chromaScale));

    // Final blend between per-channel and UCS-scaled results.
    vec3 blended = mix(skewedRgb, scaledRgb, GT7_BLEND_RATIO);

    return min(blended, vec3(peak));
}

// SDR tonemap against paper white, rescaled so paper white is 1.0. Outputs linear Rec.2020 in [0, 1]
vec3 gt7ToneMapSdr(vec3 rgb)
{
    float peak = GT7_SDR_PAPER_WHITE / GT7_REFERENCE_LUMINANCE;
    return gt7ToneMap(rgb, peak) / peak;
}

// HDR tonemap, 1.0 lands on paper white like SDR but highlights roll off to the display peak. Outputs linear Rec.709
vec3 tonemapGranTurismoHdr(vec3 color, in float exposure, in float paperWhiteNits, in float peakNits)
{
    vec3 x = max(color * exposure, 0.0) * (paperWhiteNits / GT7_REFERENCE_LUMINANCE);
    x = GT7_REC709_TO_REC2020 * x;
    x = gt7ToneMap(x, max(peakNits, paperWhiteNits) / GT7_REFERENCE_LUMINANCE);
    x = GT7_REC2020_TO_REC709 * x;
    return x * (GT7_REFERENCE_LUMINANCE / paperWhiteNits);
}

// Scene-referred linear Rec.709 in, gamma-encoded display colour out, matching the
// other tonemappers in this file.
vec3 tonemapGranTurismo(vec3 color, in float exposure, in float gamma)
{
    vec3 x = max(color * exposure, 0.0) * GT7_SCENE_WHITE_SCALE;
    x = GT7_REC709_TO_REC2020 * x;
    x = gt7ToneMapSdr(x);
    x = GT7_REC2020_TO_REC709 * x;
    return pow(clamp(x, 0.0, 1.0), vec3(1.0 / gamma));
}

// Convenience function to switch between different tonemaps based on an index
// As long as the branch taken is the same for all screen values the performance shouldn't be affected
// But a specialization constant should preferably be used
vec3 tonemapSwitch(uint index, in vec3 color, in float exposure, in float gamma, in HableTonemapUniforms hableUniforms)
{
    vec3 outCol = color;
    switch(index)
    {
        case 0:
        outCol = tonemapUncharted2(color, exposure, gamma); break;
        case 1:
        outCol = agx(color * exposure);
        outCol = agxLook(outCol); // Optional
        outCol = agxEotf(outCol);
        outCol = pow(clamp(outCol, 0.0, 1.0), vec3(1. / gamma)); break;
        case 2:
        outCol = tonemapACESFitted(color, exposure);
        outCol = pow(clamp(outCol, 0.0, 1.0), vec3(1. / gamma)); break;
        case 3:
        outCol = tonemapACES(color, exposure);
        outCol = pow(clamp(outCol, 0.0, 1.0), vec3(1. / gamma)); break;
        case 4:
        outCol = tonemapFilmic(color, exposure); break;
        case 5:
        outCol = tonemapRomBinDaHouse(color, exposure, gamma); break;
        case 6:
        outCol = tonemapReinhardSimple(color, exposure, gamma); break;
        case 7:
        outCol = tonemapReinhardLuma(color, exposure, gamma); break;
        case 8:
        outCol = tonemapGranTurismo(color, exposure, gamma); break;
        case 9:
        outCol = tonemapLinear(color, exposure, gamma); break;
        case 10:
        outCol = HableTonemap(color, exposure, gamma, hableUniforms); break;
        default:
        outCol = pow(clamp(color, 0.0, 1.0), vec3(1.0 / gamma)); break;
    }
    return outCol;

}
