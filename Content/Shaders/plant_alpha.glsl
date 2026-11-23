// Cut-outs for plants' fragment shaders (plant.frag, plant_depth.frag, shadow_plant.frag, impostor.frag).

// A cut-out's alpha raised with its mip level: averaging thin blades and leaves into a smaller mip thins their alpha out, so with
// a fixed cut-off far grass and canopies wear away to nothing. A quarter more for every level down keeps them about as full as
// they are close to (Golus 2017; SmartImposter lowers its cut-off with distance to the same end).
float holdCoverage(float alpha, sampler2D map, vec2 uv)
{
    return alpha * (1.0 + max(textureQueryLod(map, uv).x, 0.0) * 0.25);
}

// A plant's cut-out, as plant.frag and plant_depth.frag both take it (cutout: plant.frag's shape.w; mapAlpha: its colour map's
// alpha), every map read and its mip taken whatever the cut-out comes from, so it's in uniform control flow.
float plantAlpha(sampler2D albedoMap, sampler2D opacityMap, vec2 uv, float mapAlpha, float cutout)
{
    float own = holdCoverage(texture(opacityMap, uv).r, opacityMap, uv);
    float fromMap = holdCoverage(mapAlpha, albedoMap, uv);
    return cutout > 1.5 ? 1.0 : cutout > 0.5 ? own : fromMap;
}
