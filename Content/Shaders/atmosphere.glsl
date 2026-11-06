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
    vec4 horizonTowardsSun;
    vec4 moon;    // w: how much of its face is lit, 0 to 1
    vec4 realSun; // sunDirection is the moon's at night; w: 1 while it's the sun's
    vec4 starsX;  // with Y and Z, the turn from the world to the stars
    vec4 starsY;
    vec4 starsZ;
    vec4 groundLit; // the light coming up off the ground round the camera; w: how much thicker the weather makes the air, per metre
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
    // The horizon's colour goes round the compass: at sunset, orange under the sun and dusky blue behind you.
    float flat2 = dot(dir.xz, dir.xz) * dot(sun.xz, sun.xz);
    float round = flat2 > 1e-6 ? dot(dir.xz, sun.xz) / sqrt(flat2) * 0.5 + 0.5 : 0.5;
    vec3 horizon = mix(Horizon, sky.horizonTowardsSun.rgb, round * round * round);

    vec3 color = mix(horizon, Zenith, 1.0 - pow(1.0 - up, 5.0));
    if (dir.y < 0.0)
        color = mix(horizon, Ground, clamp(-dir.y * 4.0, 0.0, 1.0));

    // Forward scattering round the sun, stronger low down where the light crosses more air.
    float mu = max(dot(dir, sun), 0.0);
    color += SunColor * (0.02 * pow(mu, 6.0) + 0.12 * pow(mu, 64.0)) * (0.5 + 0.5 * (1.0 - up));
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
    float lit = light == vec3(0.0) ? 0.4 + 0.6 * z : max(dot(normal, light), 0.0);
    float cover = 1.0 - smoothstep(0.92, 1.0, sqrt(r2));
    return vec4(texture(map, uv).rgb * lit, cover);
}

vec3 starHash(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

// Soft noise for the Milky Way's clouds of stars.
float starCloud(vec3 p)
{
    float total = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 4; i++)
    {
        vec3 c = floor(p);
        vec3 f = p - c;
        f = f * f * (3.0 - 2.0 * f);
        total += amplitude * mix(
            mix(mix(starHash(c).x, starHash(c + vec3(1, 0, 0)).x, f.x), mix(starHash(c + vec3(0, 1, 0)).x, starHash(c + vec3(1, 1, 0)).x, f.x), f.y),
            mix(mix(starHash(c + vec3(0, 0, 1)).x, starHash(c + vec3(1, 0, 1)).x, f.x), mix(starHash(c + vec3(0, 1, 1)).x, starHash(c + vec3(1, 1, 1)).x, f.x), f.y), f.z);
        p *= 2.03;
        amplitude *= 0.5;
    }
    return total;
}

// Stars and the Milky Way, invented but turning with the real sky, once it's dark enough to see them.
vec3 stars(vec3 dir)
{
    float brightness = dot(Zenith, vec3(0.2126, 0.7152, 0.0722));
    float show = (1.0 - smoothstep(0.008, 0.035, brightness)) * smoothstep(0.0, 0.14, dir.y);
    if (show <= 0.0)
        return vec3(0.0);

    // A bright moon washes most of them out.
    show *= 1.0 - 0.8 * sky.moon.w * smoothstep(0.0, 0.1, sky.moon.y);

    vec3 d = normalize(mat3(sky.starsX.xyz, sky.starsY.xyz, sky.starsZ.xyz) * dir);

    // Across the galaxy's plane (its north pole, against the stars, is fixed).
    float across = dot(d, vec3(-0.8677, -0.198, 0.456));
    float band = exp(-across * across / 0.04);
    vec3 color = vec3(0.70, 0.76, 1.0) * band * smoothstep(0.3, 0.75, starCloud(d * 7.0)) * 0.035;

    const float Scale = 200.0;
    vec3 cell = floor(d * Scale);
    vec3 pick = starHash(cell);
    if (pick.x < 0.05 + band * 0.2)
    {
        vec3 at = normalize((cell + 0.25 + 0.5 * starHash(cell + 19.0)) / Scale);
        float strength = 0.05 + 2.5 * pow(pick.y, 20.0);
        vec3 tint = mix(vec3(1.0, 0.78, 0.6), vec3(0.72, 0.84, 1.0), pick.z);
        color += tint * strength * (1.0 - smoothstep(0.0002, 0.0009, distance(d, at)));
    }

    return color * show;
}

// The sun, the moon and the stars over the analytic sky (a skybox has its own).
vec3 sunDisc(vec3 dir, vec3 sun)
{
    if (sky.mode.x > 0.5)
        return vec3(0.0);

    vec3 realSun = sky.realSun.xyz;
    vec4 disc = planet(sunMap, dir, realSun, 0.0093, vec3(0.0));
    vec4 moon = planet(moonMap, dir, sky.moon.xyz, 0.0090, realSun);
    return SunColor * sky.realSun.w * 25.0 * disc.rgb * disc.a + moon.rgb * moon.a * 0.6 + stars(dir) * (1.0 - moon.a);
}

// Light from the whole sky on a surface facing n: mostly the blue overhead, the ground's bounce from below. Kept
// well under the sun so greys stay grey rather than going blue-white.
vec3 skyAmbient(vec3 n)
{
    // Sky above, and on anything facing sideways or down, sunlight bounced off the ground, so shade isn't just the sky's blue.
    return mix(Ground * 0.5, Zenith * 0.9 + Horizon * 0.45, n.y * 0.5 + 0.5) + vec3(0.17, 0.15, 0.12) * (1.0 - clamp(n.y, 0.0, 1.0));
}

// How much air lies along a ray, as metres of it at sea level, for air that thins with this falloff.
float airAlong(vec3 dir, float dist, float eyeHeight, float falloff)
{
    float rise = dir.y * dist * falloff;
    float integral = abs(rise) > 1e-4 ? (1.0 - exp(-rise)) / rise : 1.0;
    return dist * exp(-falloff * max(eyeHeight, 0.0)) * integral;
}

// The air between the eye and a surface, with the same scattering as Atmosphere.cs, a little thicker for dust. Blue is lost first
// and scattered in first, so distant land goes blue, then fades into the horizon's colour, and low in the valleys there's more.
vec3 applyFog(vec3 color, vec3 dir, float dist, float eyeHeight, vec3 sun)
{
    // Less of the blue than the sky's own: past this, ground seen from high up turns the colour of water.
    const vec3 Rayleigh = vec3(5.8e-6, 11.0e-6, 20.0e-6);
    const float Mie = 12e-6;
    const float Dust = 1.2;
    vec3 optical = Rayleigh * airAlong(dir, dist, eyeHeight, 1.0 / 8000.0) + vec3(Mie * airAlong(dir, dist, eyeHeight, 1.0 / 1200.0));
    vec3 through = exp(-optical * Dust - vec3(sky.groundLit.w * dist));
    vec3 haze = skyHaze(normalize(vec3(dir.x, max(dir.y, 0.0) * 0.5 + 0.02, dir.z)), sun);
    // Under cloud thick enough to rain or snow, the air's lit by less than the open sky would.
    haze *= 1.0 - clamp(sky.groundLit.w * 9000.0, 0.0, 1.0) * 0.4;
    return mix(haze, color, through);
}
