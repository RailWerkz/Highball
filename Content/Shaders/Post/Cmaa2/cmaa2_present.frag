#version 450
// Lays CMAA2's smoothed picture (already tonemapped and display encoded) over the screen, as it is.

layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D picture;

void main()
{
    outColor = vec4(texture(picture, vUv).rgb, 1.0);
}
