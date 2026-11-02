#version 450
// One triangle covering the screen: ndc in clip space, uv 0..1 from the top left.
layout(location = 0) out vec2 vNdc;
layout(location = 1) out vec2 vUv;

void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vNdc = p * 2.0 - 1.0;
    vUv = p;
    gl_Position = vec4(vNdc, 0.0, 1.0);
}
