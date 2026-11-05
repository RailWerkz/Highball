// What the passes after the world share. Binding 4 of each pass's set; PostProcess.Uniforms is the same.

layout(set = 0, binding = 4) uniform PostUniforms
{
    mat4 previousViewProjection; // last frame's, camera at the origin
    vec4 right;   // across the view, scaled to its edge
    vec4 up;      // up the view, scaled to its edge
    vec4 forward; // w: the near distance
    vec4 moved;   // the camera's move since last frame; w: 1 when last frame's image can be used
    vec4 sun;     // w: 0 behind the camera to 1 in front
    vec4 sunUv;   // xy: where it is on screen; z: the view's aspect; w: seconds since last frame
    vec4 sunColor; // w: how strong the god rays and glare are, 0 for none
    vec4 grade;   // xyz: the white balance; w: saturation
    vec4 look;    // x: contrast; y: how much bloom; z: the set exposure; w: 1 to adjust it by what the image measures
} post;

const vec3 LumaWeights = vec3(0.2126, 0.7152, 0.0722);

float luma(vec3 color)
{
    return dot(color, LumaWeights);
}

float noiseAt(vec2 pixel)
{
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}
