// What the weather does to a surface before it's lit. Include after shadows.glsl, which has the weather's measurements.
//
// puddle: how much water would pool here, 0 to 1, for ground that can hold it. hold: how much snow it can carry: a polished railhead
// keeps being polished by the wheels.
void weatherSurface(inout Surface s, float puddle, float hold)
{
    float wet = shadow.weather.x;
    if (wet > 0.0)
    {
        float facing = smoothstep(0.3, 0.9, s.normal.y);
        float soak = wet * mix(0.5, 1.0, facing);
        // Water fills what scatters light in a dry surface, so it goes darker and glossier.
        s.albedo *= mix(1.0, mix(0.6, 0.85, s.metallic), soak);
        // Soil and stone only go so glossy before there's standing water.
        s.roughness = mix(s.roughness, max(s.roughness * 0.65, min(s.roughness, 0.38)), soak);
        s.specular = mix(s.specular, max(s.specular, 0.5), soak);

        float pool = puddle * wet * facing;
        if (pool > 0.0)
        {
            s.albedo *= 1.0 - 0.3 * pool;
            s.roughness = mix(s.roughness, 0.05, pool);
            s.specular = mix(s.specular, 1.0, pool);
            s.normal = normalize(mix(s.normal, vec3(0.0, 1.0, 0.0), pool));
        }
    }

    float snow = shadow.weather.y * hold * smoothstep(0.35, 0.8, s.normal.y);
    if (snow > 0.0)
    {
        s.albedo = mix(s.albedo, vec3(0.82, 0.84, 0.88), snow);
        s.metallic *= 1.0 - snow;
        s.roughness = mix(s.roughness, 0.75, snow);
        s.specular = mix(s.specular, 0.6, snow);
        s.occlusion = mix(s.occlusion, 1.0, snow * 0.5);
    }
}
