#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec4 inData;

layout(push_constant) uniform Push
{
    mat4 light;
    vec4 offset; // the chunk's origin relative to the camera
} pc;

void main()
{
    // Loose stone lying on the ground (see line.frag) has nothing under it to shade, and where it thins out it would show that it had:
    // it goes in under the ground here, so it casts nothing onto it.
    vec3 place = inPosition + pc.offset.xyz;
    int code = int(inData.z + 0.5);
    bool lowBed = (code & 7) == 0 && ((code >> 3) & 16) != 0;
    if (inData.w < 0.0 || lowBed)
        place.y -= 1.5;
    gl_Position = pc.light * vec4(place, 1.0);
}
