#ifndef SCATTERING_FUNCTIONS_GLSL_INCLUDED
#define SCATTERING_FUNCTIONS_GLSL_INCLUDED

float BeerAbsorption(float density)
{
    return exp(-density);
}

// Multiple scattering approximation using multiple octaves of single scattering
// Based on the paper "Oz: The Great and Volumetric" by Wrenninge 2013
// Further approximated by a rational function
float BeerAbsorptionMultipleScattering(float density)
{
    /*
    float result = 0.0;
    float k = 0.5;
    
    for (int i = 0; i < 4; i++)
    {
        result += k * BeerAbsorption(k * density);
        k *= 0.5;
    }

    return result;
    */

    // Cheaper approximation of the above
    return 1.0 / (1.0 + 0.35 * density + 0.065 * density * density);
}

// The Henyey-Greenstein phase function models anisotropic scattering of light
// with eccentricity controlling forward/backward scattering
// Astrophysical Journal, Vol. 93, p. 70-83 (1941)
float HenyeyGreensteinPhaseFunction(float eccentricity, float viewDirDotLight)
{
    return (1.0 - eccentricity * eccentricity) / (4.0 * PI * pow(1.0 + eccentricity * eccentricity
                                                 - 2.0 * eccentricity * viewDirDotLight, 3.0/2.0));
}

// Similarly to Red Dead Redemption 2 2019 presentation, manually compose multiple HG phase functions
// to balance between realism and artistic control
void GetPhaseFunctions(float dotLight, out float directLightingPhaseFunction, out float indirectLightingPhaseFunction)
{
    // Strong forward scattering for direct light
    directLightingPhaseFunction = 0.2 * HenyeyGreensteinPhaseFunction(0.8, dotLight);

    // Diffuse light and some back-scattering for indirect light
    indirectLightingPhaseFunction = 2.5 * HenyeyGreensteinPhaseFunction(0.2, dotLight) +
                                    0.7 * HenyeyGreensteinPhaseFunction(-0.4, dotLight);
}

float Remap(float value, float inputMin, float inputMax, float outputMin, float outputMax)
{
    return outputMin + (value - inputMin) / (inputMax - inputMin) * (outputMax - outputMin);
}

#ifndef MULTIPLE_SCATTERING_EDGE_EFFECT_MIN_VALUE
#define MULTIPLE_SCATTERING_EDGE_EFFECT_MIN_VALUE 0.05
#endif

// Modified "Dark edge" effect from Nubis Siggraph 2023 presentation
// Approximates the multiple scattering effect where the edges of clouds appear darker than the center
// The dimensionalProfile and noiseProfile are used as a proxy for the depth inside the cloud
float GetMultipleScatteringEdgeEffect(float dimensionalProfile, float noiseProfile)
{
    float depth = saturate(noiseProfile + dimensionalProfile);
    return Remap(dimensionalProfile, 0.0, 1.0, MULTIPLE_SCATTERING_EDGE_EFFECT_MIN_VALUE, 1.0);
}

// Apply "erosion" of volume or low-detail shape using high-detail shape
// Original idea from Horizon Zero Dawn 2015 presentation, applied differently
// to be able to control the "sharpness" of the edge and limit erosion to a max depth inside
// the low-detail volume to avoid over-eroding it and losing the main structure.
float ApplyErosion(float highDetail, float lowDetail, float erosionMaxDepth, float edgeSharpness, out float erodedValue)
{
    highDetail = highDetail * erosionMaxDepth + (1.0 - erosionMaxDepth);

    float threshold = 1.0 - lowDetail;
    erodedValue = highDetail - threshold;

    return clamp(erodedValue / (1.0 - edgeSharpness), 0.0, 1.0);
}

#endif // SCATTERING_FUNCTIONS_GLSL_INCLUDED
