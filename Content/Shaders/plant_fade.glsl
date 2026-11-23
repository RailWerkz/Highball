// What plants' and impostors' shaders share for handing one level of detail to the next, and for cut-outs that hold up far off.
//
// Between two levels (a tree's full model and its reduced one, the reduced one and its impostor) both are drawn over a short
// stretch of distance, and each pixel is answered by exactly one of them: the pixel's own random number against how far
// through the stretch the instance is, the same number in both draws, so the two never both show nor both leave a hole. The
// number changes every frame, which the temporal pass (FSR 2) averages into a smooth fade rather than a pop (after SmartImposter's
// dithered transitions, MIT).

// How much of the way to the edge either side the hand-over takes.
const float FadeWidth = 0.12;

// An instance's hand-overs at a level's two edges (from and to, each already moved by its stagger): x, how much of the way it
// has come in at the near edge (1: all of it), y, how far it has gone out at the far one (0: none). Pixels whose random number
// is below x and at least y are this level's.
vec2 levelFade(float away, float from, float to)
{
    float fadeIn = from > 0.0 ? smoothstep(from * (1.0 - 0.5 * FadeWidth), from * (1.0 + 0.5 * FadeWidth), away) : 1.0;
    float fadeOut = to < 1e8 ? smoothstep(to * (1.0 - 0.5 * FadeWidth), to * (1.0 + 0.5 * FadeWidth), away) : 0.0;
    return vec2(fadeIn, fadeOut);
}

// Whether the instance has any of this level to draw at all (outside its stretches it's collapsed away).
bool levelShown(float away, float from, float to)
{
    return away >= from * (1.0 - 0.5 * FadeWidth) && away < to * (1.0 + 0.5 * FadeWidth);
}

// A pixel's random number this frame: interleaved gradient noise (Jimenez 2014), moved on by a number that changes each frame
// (seed: anything the frame's draws share that changes from one frame to the next, here the wind's flutter phase).
float pixelRandom(vec2 fragCoord, float seed)
{
    float ign = fract(52.9829189 * fract(dot(fragCoord, vec2(0.06711056, 0.00583715))));
    return fract(ign + fract(seed * 157.31));
}

bool fadedOut(vec2 fade, vec2 fragCoord, float seed)
{
    float h = pixelRandom(fragCoord, seed);
    return h >= fade.x || h < fade.y;
}
