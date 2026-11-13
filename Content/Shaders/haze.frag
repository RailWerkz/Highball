#version 450
// Heat haze: what's behind the column, read from the picture a little to one side and up or down, the offset wandering
// with noise that rises like the hot air does. Strongest just over the mouth and down the middle, gone at the sides and
// the top; smaller with distance, as the bending is a few centimetres of real air. What's in front of the column (the
// engine's own roof) is left alone.

layout(location = 0) in vec2 vAcross;
layout(location = 1) in vec3 vRelative;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 placeWidth;
    vec4 forward; // w: the near distance
    vec4 look;    // strength, seconds, how tall, 0
    vec4 lean;    // how far its top's pushed over, in the world's x and z
} pc;

layout(set = 0, binding = 0) uniform sampler2D picture;
layout(set = 0, binding = 1) uniform sampler2D sceneDepth;
layout(set = 0, binding = 2) uniform sampler3D noiseTexture;

void main()
{
    // In front of the column there's nothing for it to bend.
    float ahead = pc.forward.w / max(texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r, 1e-8);
    float here = dot(vRelative, pc.forward.xyz);
    if (ahead < here - 0.3)
        discard;

    float across = 1.0 - abs(vAcross.x);
    float strength = pc.look.x * pow(across, 1.5) * smoothstep(0.0, 0.12, vAcross.y) * pow(1.0 - vAcross.y, 1.3);
    if (strength < 0.002)
        discard;

    float t = pc.look.y;
    // Smooth, slow-rolling noise: shimmer wobbles what's behind, it doesn't tear its edges into zigzags.
    vec3 uvw = vec3(vAcross.x * 0.45, vAcross.y * 1.0 - t * 0.8, t * 0.1);
    vec2 wave = texture(noiseTexture, uvw).rg + texture(noiseTexture, uvw * 2.1 + 0.5).rg * 0.35 - 0.675;

    // A few pixels' bend near by, less far off.
    vec2 size = vec2(textureSize(picture, 0));
    float distance_ = length(vRelative);
    vec2 offset = wave * strength * 22.0 * clamp(25.0 / max(distance_, 1.0), 0.15, 0.8);
    vec3 behind = textureLod(picture, (gl_FragCoord.xy + offset) / size, 0.0).rgb;
    // The blend keeps what's behind by the alpha: none of it, as what's bent is what's behind.
    outColor = vec4(behind, 0.0);
}
