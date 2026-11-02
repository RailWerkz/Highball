// MIT License
//
// Copyright(c) 2023 Jordan Peck (jordan.me2@gmail.com)
// Copyright(c) 2023 Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// .'',;:cldxkO00KKXXNNWWWNNXKOkxdollcc::::::;:::ccllloooolllllllllooollc:,'...        ...........',;cldxkO000Ok
// The 2D subset of FastNoiseLite that RailWerkz's terrain uses (OpenSimplex2, FBm and Ridged), ported from the
// C++ header so the C# terrain matches the original bit for bit.

using System.Runtime.CompilerServices;

namespace FastNoise;

public sealed class FastNoiseLite
{
    public enum NoiseType { OpenSimplex2 }
    public enum FractalType { None, FBm, Ridged }

    private int _seed = 1337;
    private float _frequency = 0.01f;
    private FractalType _fractalType = FractalType.None;
    private int _octaves = 3;
    private float _lacunarity = 2.0f;
    private float _gain = 0.5f;
    private float _weightedStrength;
    private float _fractalBounding = 1 / 1.75f;

    public FastNoiseLite(int seed = 1337)
    {
        _seed = seed;
        CalculateFractalBounding();
    }

    public void SetSeed(int seed) => _seed = seed;
    public void SetFrequency(float frequency) => _frequency = frequency;
    public void SetNoiseType(NoiseType _) { }
    public void SetFractalType(FractalType fractalType) => _fractalType = fractalType;

    public void SetFractalOctaves(int octaves)
    {
        _octaves = octaves;
        CalculateFractalBounding();
    }

    public void SetFractalLacunarity(float lacunarity) => _lacunarity = lacunarity;

    public void SetFractalGain(float gain)
    {
        _gain = gain;
        CalculateFractalBounding();
    }

    public void SetFractalWeightedStrength(float strength) => _weightedStrength = strength;

    /// <summary>2D noise at the given position, -1 to 1.</summary>
    public float GetNoise(float x, float y)
    {
        TransformNoiseCoordinate(ref x, ref y);

        return _fractalType switch
        {
            FractalType.FBm => GenFractalFBm(x, y),
            FractalType.Ridged => GenFractalRidged(x, y),
            _ => SingleSimplex(_seed, x, y),
        };
    }

    private static readonly float[] Gradients2D =
    [
        0.130526192220052f, 0.99144486137381f, 0.38268343236509f, 0.923879532511287f, 0.608761429008721f, 0.793353340291235f, 0.793353340291235f, 0.608761429008721f,
        0.923879532511287f, 0.38268343236509f, 0.99144486137381f, 0.130526192220051f, 0.99144486137381f, -0.130526192220051f, 0.923879532511287f, -0.38268343236509f,
        0.793353340291235f, -0.60876142900872f, 0.608761429008721f, -0.793353340291235f, 0.38268343236509f, -0.923879532511287f, 0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f, -0.38268343236509f, -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f, -0.99144486137381f, -0.130526192220052f, -0.99144486137381f, 0.130526192220051f, -0.923879532511287f, 0.38268343236509f,
        -0.793353340291235f, 0.608761429008721f, -0.608761429008721f, 0.793353340291235f, -0.38268343236509f, 0.923879532511287f, -0.130526192220052f, 0.99144486137381f,
        0.130526192220052f, 0.99144486137381f, 0.38268343236509f, 0.923879532511287f, 0.608761429008721f, 0.793353340291235f, 0.793353340291235f, 0.608761429008721f,
        0.923879532511287f, 0.38268343236509f, 0.99144486137381f, 0.130526192220051f, 0.99144486137381f, -0.130526192220051f, 0.923879532511287f, -0.38268343236509f,
        0.793353340291235f, -0.60876142900872f, 0.608761429008721f, -0.793353340291235f, 0.38268343236509f, -0.923879532511287f, 0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f, -0.38268343236509f, -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f, -0.99144486137381f, -0.130526192220052f, -0.99144486137381f, 0.130526192220051f, -0.923879532511287f, 0.38268343236509f,
        -0.793353340291235f, 0.608761429008721f, -0.608761429008721f, 0.793353340291235f, -0.38268343236509f, 0.923879532511287f, -0.130526192220052f, 0.99144486137381f,
        0.130526192220052f, 0.99144486137381f, 0.38268343236509f, 0.923879532511287f, 0.608761429008721f, 0.793353340291235f, 0.793353340291235f, 0.608761429008721f,
        0.923879532511287f, 0.38268343236509f, 0.99144486137381f, 0.130526192220051f, 0.99144486137381f, -0.130526192220051f, 0.923879532511287f, -0.38268343236509f,
        0.793353340291235f, -0.60876142900872f, 0.608761429008721f, -0.793353340291235f, 0.38268343236509f, -0.923879532511287f, 0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f, -0.38268343236509f, -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f, -0.99144486137381f, -0.130526192220052f, -0.99144486137381f, 0.130526192220051f, -0.923879532511287f, 0.38268343236509f,
        -0.793353340291235f, 0.608761429008721f, -0.608761429008721f, 0.793353340291235f, -0.38268343236509f, 0.923879532511287f, -0.130526192220052f, 0.99144486137381f,
        0.130526192220052f, 0.99144486137381f, 0.38268343236509f, 0.923879532511287f, 0.608761429008721f, 0.793353340291235f, 0.793353340291235f, 0.608761429008721f,
        0.923879532511287f, 0.38268343236509f, 0.99144486137381f, 0.130526192220051f, 0.99144486137381f, -0.130526192220051f, 0.923879532511287f, -0.38268343236509f,
        0.793353340291235f, -0.60876142900872f, 0.608761429008721f, -0.793353340291235f, 0.38268343236509f, -0.923879532511287f, 0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f, -0.38268343236509f, -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f, -0.99144486137381f, -0.130526192220052f, -0.99144486137381f, 0.130526192220051f, -0.923879532511287f, 0.38268343236509f,
        -0.793353340291235f, 0.608761429008721f, -0.608761429008721f, 0.793353340291235f, -0.38268343236509f, 0.923879532511287f, -0.130526192220052f, 0.99144486137381f,
        0.130526192220052f, 0.99144486137381f, 0.38268343236509f, 0.923879532511287f, 0.608761429008721f, 0.793353340291235f, 0.793353340291235f, 0.608761429008721f,
        0.923879532511287f, 0.38268343236509f, 0.99144486137381f, 0.130526192220051f, 0.99144486137381f, -0.130526192220051f, 0.923879532511287f, -0.38268343236509f,
        0.793353340291235f, -0.60876142900872f, 0.608761429008721f, -0.793353340291235f, 0.38268343236509f, -0.923879532511287f, 0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f, -0.38268343236509f, -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f, -0.99144486137381f, -0.130526192220052f, -0.99144486137381f, 0.130526192220051f, -0.923879532511287f, 0.38268343236509f,
        -0.793353340291235f, 0.608761429008721f, -0.608761429008721f, 0.793353340291235f, -0.38268343236509f, 0.923879532511287f, -0.130526192220052f, 0.99144486137381f,
        0.38268343236509f, 0.923879532511287f, 0.923879532511287f, 0.38268343236509f, 0.923879532511287f, -0.38268343236509f, 0.38268343236509f, -0.923879532511287f,
        -0.38268343236509f, -0.923879532511287f, -0.923879532511287f, -0.38268343236509f, -0.923879532511287f, 0.38268343236509f, -0.38268343236509f, 0.923879532511287f,
    ];

    private const int PrimeX = 501125321;
    private const int PrimeY = 1136930381;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastAbs(float f) => f < 0 ? -f : f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastMin(float a, float b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastFloor(float f) => f >= 0 ? (int)f : (int)f - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    private void CalculateFractalBounding()
    {
        var gain = FastAbs(_gain);
        var amp = gain;
        var ampFractal = 1.0f;

        for (var i = 1; i < _octaves; i++)
        {
            ampFractal += amp;
            amp *= gain;
        }

        _fractalBounding = 1 / ampFractal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(int seed, int xPrimed, int yPrimed)
    {
        var hash = seed ^ xPrimed ^ yPrimed;
        hash *= 0x27d4eb2d;
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GradCoord(int seed, int xPrimed, int yPrimed, float xd, float yd)
    {
        var hash = Hash(seed, xPrimed, yPrimed);
        hash ^= hash >> 15;
        hash &= 127 << 1;

        return xd * Gradients2D[hash] + yd * Gradients2D[hash | 1];
    }

    private void TransformNoiseCoordinate(ref float x, ref float y)
    {
        x *= _frequency;
        y *= _frequency;

        const float SQRT3 = 1.7320508075688772935274463415059f;
        const float F2 = 0.5f * (SQRT3 - 1);
        var t = (x + y) * F2;
        x += t;
        y += t;
    }

    private float GenFractalFBm(float x, float y)
    {
        var seed = _seed;
        var sum = 0f;
        var amp = _fractalBounding;

        for (var i = 0; i < _octaves; i++)
        {
            var noise = SingleSimplex(seed++, x, y);
            sum += noise * amp;
            amp *= Lerp(1.0f, FastMin(noise + 1, 2) * 0.5f, _weightedStrength);

            x *= _lacunarity;
            y *= _lacunarity;
            amp *= _gain;
        }

        return sum;
    }

    private float GenFractalRidged(float x, float y)
    {
        var seed = _seed;
        var sum = 0f;
        var amp = _fractalBounding;

        for (var i = 0; i < _octaves; i++)
        {
            var noise = FastAbs(SingleSimplex(seed++, x, y));
            sum += (noise * -2 + 1) * amp;
            amp *= Lerp(1.0f, 1 - noise, _weightedStrength);

            x *= _lacunarity;
            y *= _lacunarity;
            amp *= _gain;
        }

        return sum;
    }

    /// <summary>2D OpenSimplex2 uses the same algorithm as ordinary Simplex; the skew happens in TransformNoiseCoordinate.</summary>
    private static float SingleSimplex(int seed, float x, float y)
    {
        const float SQRT3 = 1.7320508075688772935274463415059f;
        const float G2 = (3 - SQRT3) / 6;

        var i = FastFloor(x);
        var j = FastFloor(y);
        var xi = x - i;
        var yi = y - j;

        var t = (xi + yi) * G2;
        var x0 = xi - t;
        var y0 = yi - t;

        i *= PrimeX;
        j *= PrimeY;

        float n0, n1, n2;

        var a = 0.5f - x0 * x0 - y0 * y0;
        if (a <= 0)
            n0 = 0;
        else
            n0 = (a * a) * (a * a) * GradCoord(seed, i, j, x0, y0);

        var c = (float)(2 * (1 - 2 * G2) * (1 / G2 - 2)) * t + ((float)(-2 * (1 - 2 * G2) * (1 - 2 * G2)) + a);
        if (c <= 0)
        {
            n2 = 0;
        }
        else
        {
            var x2 = x0 + (2 * G2 - 1);
            var y2 = y0 + (2 * G2 - 1);
            n2 = (c * c) * (c * c) * GradCoord(seed, i + PrimeX, j + PrimeY, x2, y2);
        }

        if (y0 > x0)
        {
            var x1 = x0 + G2;
            var y1 = y0 + (G2 - 1);
            var b = 0.5f - x1 * x1 - y1 * y1;
            n1 = b <= 0 ? 0 : (b * b) * (b * b) * GradCoord(seed, i, j + PrimeY, x1, y1);
        }
        else
        {
            var x1 = x0 + (G2 - 1);
            var y1 = y0 + G2;
            var b = 0.5f - x1 * x1 - y1 * y1;
            n1 = b <= 0 ? 0 : (b * b) * (b * b) * GradCoord(seed, i + PrimeX, j, x1, y1);
        }

        return (n0 + n1 + n2) * 99.83685446303647f;
    }
}
