// Physically based shading: metal-roughness surfaces under the sun, the sky and, one day, any other light. Include after
// atmosphere.glsl. Everything is linear HDR.
//
// Light colours follow the game's convention: a light of colour C on a white surface facing it gives C, with no divide
// by pi. So the diffuse term is the albedo itself, and the specular term is multiplied by pi to match.

struct Surface
{
    vec3 albedo;
    float metallic;
    float roughness; // perceptual, 0..1
    vec3 normal;
    vec3 view;       // unit vector from the surface towards the camera
    float occlusion; // how much of the sky reaches it, from 0 in a crevice to 1 in the open
    float specular;  // how much of its highlights show: 1, or less on ground whose bumps hide them from the light
};

// Below this the highlight is smaller than a pixel can show and only sparkles.
const float MinRoughness = 0.045;

vec3 surfaceF0(Surface s)
{
    return mix(vec3(0.04), s.albedo, s.metallic);
}

vec3 fresnelSchlick(float cosine, vec3 f0)
{
    float k = 1.0 - cosine;
    float k2 = k * k;
    return f0 + (1.0 - f0) * k2 * k2 * k;
}

// One direct light: its unit direction from the surface and its colour there (attenuated and shadowed by the caller).
// The sun goes through here, and each local light will too, whichever way they end up being found.
vec3 shadeLight(Surface s, vec3 light, vec3 color)
{
    float nl = dot(s.normal, light);
    if (nl <= 0.0)
        return vec3(0.0);

    vec3 h = normalize(light + s.view);
    float nv = max(dot(s.normal, s.view), 1e-3);
    float nh = max(dot(s.normal, h), 0.0);
    float vh = max(dot(s.view, h), 0.0);

    float a = max(s.roughness, MinRoughness);
    a *= a;
    float a2 = a * a;

    // GGX distribution, and the visibility term of Smith's height-correlated masking.
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (Pi * d * d);
    float visibility = 0.5 / (nl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(nl * nl * (1.0 - a2) + a2));

    vec3 f = fresnelSchlick(vh, surfaceF0(s));
    vec3 specular = f * distribution * visibility * Pi * s.specular;
    vec3 diffuse = s.albedo * (1.0 - s.metallic) * (1.0 - f);
    return (diffuse + specular) * color * nl;
}

// Light arriving from the whole sky on a surface facing n, and up off the real ground beneath it, which goes dark at night.
vec3 skyIrradiance(vec3 n)
{
    return mix(sky.groundLit.rgb, Zenith * 0.9 + Horizon * 0.45, n.y * 0.5 + 0.5);
}

// The sky in a mirror direction, or the ground below the horizon, blurring as the surface gets rougher.
vec3 skyReflection(vec3 dir, float roughness, vec3 sun)
{
    // Fading to the average hides the seams of a skybox's cross, which its blurrier levels can't blend across.
    vec3 sharp = sky.mode.x > 0.5 ? textureLod(skyMap, skyMapUv(dir, 0.02), roughness * 6.0).rgb : analyticSky(dir, sun);
    sharp *= 1.0 - clamp(sky.groundLit.w * 9000.0, 0.0, 1.0) * 0.5; // the thick cloud of rain or snow is darker than the sky under it
    sharp = mix(sharp, sky.groundLit.rgb, smoothstep(0.0, 0.06, -dir.y));
    return mix(sharp, skyIrradiance(dir), roughness * roughness);
}

// Karis's fit of the split-sum lookup: how much of the reflected sky a surface returns. x scales f0, y is added.
vec2 environmentBrdf(float roughness, float nv)
{
    vec4 r = roughness * vec4(-1.0, -0.0275, -0.572, 0.022) + vec4(1.0, 0.0425, 1.04, -0.04);
    float a = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a + r.zw;
}

// All the light from the sky: diffuse, and the reflection that metals get nearly all their look from.
vec3 shadeSky(Surface s, vec3 sun)
{
    float nv = max(dot(s.normal, s.view), 0.0);
    vec3 f0 = surfaceF0(s);
    vec2 brdf = environmentBrdf(s.roughness, nv);

    vec3 mirror = reflect(-s.view, s.normal);
    // Rough surfaces lean the reflection towards the normal, and it can't come from behind the surface.
    mirror = normalize(mix(mirror, s.normal, s.roughness * s.roughness));
    float horizon = clamp(1.0 + dot(mirror, s.normal), 0.0, 1.0);

    vec3 specular = skyReflection(mirror, s.roughness, sun) * (f0 * brdf.x + brdf.y) * horizon * horizon * s.specular;
    vec3 diffuse = skyIrradiance(s.normal) * s.albedo * (1.0 - s.metallic) * (1.0 - (f0 * brdf.x + brdf.y));
    return (diffuse + specular) * s.occlusion;
}
