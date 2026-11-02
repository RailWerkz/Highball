// The sky and the air, from the Sky (set 1): the analytic sky or a skybox, which the sky pass draws and everything
// else fades into, so distant ground meets the horizon in the same colour. Linear HDR; the tonemap pass maps it to
// the screen.

layout(set = 1, binding = 0) uniform SkyUniforms
{
    vec4 sunDirection;
    vec4 sunColor;
    vec4 zenith;
    vec4 horizon;
    vec4 ground;
    vec4 mode; // x: 1 for a skybox
} sky;

layout(set = 1, binding = 1) uniform sampler2D skyMap;  // horizontal cross: +Y over -X +Z +X -Z over -Y
layout(set = 1, binding = 2) uniform sampler2D sunMap;  // equirectangular
layout(set = 1, binding = 3) uniform sampler2D moonMap; // equirectangular

#define SunColor (sky.sunColor.rgb)
#define Zenith (sky.zenith.rgb)
#define Horizon (sky.horizon.rgb)
#define Ground (sky.ground.rgb)

const float Pi = 3.14159265;

// Where a direction lands in the cross (the usual cube map faces), kept inset from the face's edge so filtering
// doesn't reach into the next cell.
vec2 skyMapUv(vec3 d, float inset)
{
    vec3 a = abs(d);
    vec2 cell;
    float sc, tc, ma;
    if (a.x >= a.y && a.x >= a.z)
    {
        ma = a.x;
        if (d.x > 0.0) { sc = -d.z; tc = -d.y; cell = vec2(2.0, 1.0); }
        else { sc = d.z; tc = -d.y; cell = vec2(0.0, 1.0); }
    }
    else if (a.y >= a.z)
    {
        ma = a.y;
        if (d.y > 0.0) { sc = d.x; tc = d.z; cell = vec2(1.0, 0.0); }
        else { sc = d.x; tc = -d.z; cell = vec2(1.0, 2.0); }
    }
    else
    {
        ma = a.z;
        if (d.z > 0.0) { sc = d.x; tc = -d.y; cell = vec2(1.0, 1.0); }
        else { sc = -d.x; tc = -d.y; cell = vec2(3.0, 1.0); }
    }

    vec2 uv = clamp(vec2(sc, tc) / ma * 0.5 + 0.5, vec2(inset), vec2(1.0 - inset));
    return (cell + uv) / vec2(4.0, 3.0);
}

vec3 analyticSky(vec3 dir, vec3 sun)
{
    float up = max(dir.y, 0.0);
    vec3 color = mix(Horizon, Zenith, 1.0 - pow(1.0 - up, 3.0));
    if (dir.y < 0.0)
        color = mix(Horizon, Ground, clamp(-dir.y * 4.0, 0.0, 1.0));

    // Forward scattering round the sun, stronger low down where the light crosses more air.
    float mu = max(dot(dir, sun), 0.0);
    color += SunColor * (0.06 * pow(mu, 6.0) + 0.25 * pow(mu, 64.0)) * (0.5 + 0.5 * (1.0 - up));
    return color;
}

// The sky in a direction, sharp.
vec3 skyColor(vec3 dir, vec3 sun)
{
    if (sky.mode.x > 0.5)
    {
        float face = float(textureSize(skyMap, 0).x) * 0.25;
        return textureLod(skyMap, skyMapUv(dir, 0.5 / face), 0.0).rgb;
    }
    return analyticSky(dir, sun);
}

// The sky in a direction, blurred, for haze.
vec3 skyHaze(vec3 dir, vec3 sun)
{
    if (sky.mode.x > 0.5)
        return textureLod(skyMap, skyMapUv(dir, 0.02), 5.0).rgb;
    return analyticSky(dir, sun);
}

// A textured sphere seen as a disc of the given angular radius round centre: its colour and how much of the pixel
// it covers.
vec4 planet(sampler2D map, vec3 dir, vec3 centre, float radius, vec3 light)
{
    float mu = dot(dir, centre);
    float edge = cos(radius);
    if (mu < edge - 0.0001)
        return vec4(0.0);

    vec3 t1 = normalize(cross(centre, abs(centre.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 t2 = cross(t1, centre);
    vec2 p = vec2(dot(dir, t1), dot(dir, t2)) / sin(radius);
    float r2 = dot(p, p);
    float z = sqrt(max(1.0 - r2, 0.0));
    vec2 uv = vec2(atan(p.x, z) / (2.0 * Pi) + 0.5, 0.5 - asin(clamp(p.y, -1.0, 1.0)) / Pi);
    vec3 normal = p.x * t1 + p.y * t2 - z * centre;
    float lit = light == vec3(0.0) ? 0.4 + 0.6 * z : max(dot(-normal, light), 0.0);
    float cover = 1.0 - smoothstep(0.92, 1.0, sqrt(r2));
    return vec4(texture(map, uv).rgb * lit, cover);
}

// The sun and the moon over the analytic sky (a skybox has its own).
vec3 sunDisc(vec3 dir, vec3 sun)
{
    if (sky.mode.x > 0.5)
        return vec3(0.0);

    vec4 disc = planet(sunMap, dir, sun, 0.0093, vec3(0.0));
    vec3 moonAt = normalize(vec3(-sun.x, 0.3, -sun.z));
    vec4 moon = planet(moonMap, dir, moonAt, 0.0090, sun);
    return SunColor * 25.0 * disc.rgb * disc.a + moon.rgb * moon.a * 0.6;
}

// Light from the whole sky on a surface facing n: mostly the blue overhead, the ground's bounce from below. Kept
// well under the sun so greys stay grey rather than going blue-white.
vec3 skyAmbient(vec3 n)
{
    return mix(Ground * 0.5, Zenith * 0.9 + Horizon * 0.45, n.y * 0.5 + 0.5);
}

// Exponential height fog: thicker in the valleys, thinning with altitude along the ray.
vec3 applyFog(vec3 color, vec3 dir, float dist, float eyeHeight, vec3 sun)
{
    const float density = 0.000055;
    const float falloff = 0.0009;
    float heightTerm = exp(-falloff * max(eyeHeight, 0.0));
    float rise = dir.y * dist * falloff;
    float integral = abs(rise) > 1e-4 ? (1.0 - exp(-rise)) / rise : 1.0;
    float amount = 1.0 - exp(-density * dist * heightTerm * integral);
    vec3 haze = skyHaze(normalize(vec3(dir.x, max(dir.y, 0.0) * 0.5 + 0.02, dir.z)), sun);
    return mix(color, haze, clamp(amount, 0.0, 1.0));
}
