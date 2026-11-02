using System.Runtime.CompilerServices;

namespace RailWerkz.Core.Utils;

public static class MathEx
{
    public const float Pi = MathF.PI;
    public const float Tau = MathF.PI * 2f;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;
    public const float KnotsToMps = 0.514444f;
    public const float MetersToFeet = 3.280839895f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float3 Lerp(float3 a, float3 b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    public static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Saturate((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    /// Frame-rate independent exponential approach.
    public static float Damp(float current, float target, float sharpness, float dt) =>
        Lerp(current, target, 1f - MathF.Exp(-sharpness * dt));

    public static float3 Damp(float3 current, float3 target, float sharpness, float dt) =>
        Lerp(current, target, 1f - MathF.Exp(-sharpness * dt));

    public static float EaseOut(float k)
    {
        var c = Saturate(k);
        return 1f - (1f - c) * (1f - c) * (1f - c);
    }

    public static float WrapAngle(float angle)
    {
        angle %= Tau;
        if (angle > Pi) angle -= Tau;
        if (angle < -Pi) angle += Tau;
        return angle;
    }

    public static float Length(float3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    public static float Length(float2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
    public static float Distance(float3 a, float3 b) => Length(a - b);
    public static float Distance2D(float3 a, float3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    public static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static float3 Cross(float3 a, float3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static float3 Normalize(float3 v)
    {
        var length = Length(v);
        return length > 1e-6f ? v / length : new float3(0f, 1f, 0f);
    }

    /// Direction from yaw (0 = +Z, turning towards +X) and pitch (up positive), as RailWerkz does.
    public static float3 Direction(float yaw, float pitch)
    {
        var cosPitch = MathF.Cos(pitch);
        return Normalize(new float3(MathF.Sin(yaw) * cosPitch, MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch));
    }

    public static float Yaw(float3 direction) => MathF.Atan2(direction.X, direction.Z);

    public static float Pitch(float3 direction) =>
        MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z));

    /// A repeatable pseudo-random 0-1 from a few integers (Lodestar's hash).
    public static float Hash(uint a, uint b = 0)
    {
        var h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    /// Vulkan perspective with reverse-Z and no far plane: depth is near / distance, 1 at the near plane falling to 0 at
    /// infinity, which spreads float depth evenly from a cab to the horizon. Clear depth to 0 and test GreaterOrEqual.
    /// Clip +Y is up (the viewport is flipped). Laid out like System.Numerics so it multiplies the same way (v * M).
    public static float4x4 Perspective(float fovYRadians, float aspect, float near)
    {
        var f = 1f / MathF.Tan(fovYRadians * 0.5f);
        var m = default(float4x4);
        m.M11 = f / aspect;
        m.M22 = f;
        m.M34 = -1f;
        m.M43 = near;
        return m;
    }

    public static float4x4 LookAt(float3 eye, float3 target, float3 up) => LookTo(eye, target - eye, up);

    /// LookAt from a direction. Prefer it when the direction is already known: eye + forward rounds the direction to
    /// the eye's float precision (~1e-4 at a km out), which a narrow field of view turns into pixels of jitter.
    public static float4x4 LookTo(float3 eye, float3 forward, float3 up)
    {
        var z = Normalize(-forward);
        var x = Normalize(Cross(up, z));
        var y = Cross(z, x);
        var m = float4x4.Identity;
        m.M11 = x.X; m.M21 = x.Y; m.M31 = x.Z;
        m.M12 = y.X; m.M22 = y.Y; m.M32 = y.Z;
        m.M13 = z.X; m.M23 = z.Y; m.M33 = z.Z;
        m.M41 = -Dot(x, eye);
        m.M42 = -Dot(y, eye);
        m.M43 = -Dot(z, eye);
        return m;
    }

    public static float4x4 Translation(float3 p)
    {
        var m = float4x4.Identity;
        m.M41 = p.X; m.M42 = p.Y; m.M43 = p.Z;
        return m;
    }

    public static float4x4 Scale(float3 s)
    {
        var m = float4x4.Identity;
        m.M11 = s.X; m.M22 = s.Y; m.M33 = s.Z;
        return m;
    }

    public static float4x4 RotationX(float a)
    {
        var (s, c) = MathF.SinCos(a);
        var m = float4x4.Identity;
        m.M22 = c; m.M23 = s; m.M32 = -s; m.M33 = c;
        return m;
    }

    public static float4x4 RotationY(float a)
    {
        var (s, c) = MathF.SinCos(a);
        var m = float4x4.Identity;
        m.M11 = c; m.M13 = -s; m.M31 = s; m.M33 = c;
        return m;
    }

    public static float4x4 RotationZ(float a)
    {
        var (s, c) = MathF.SinCos(a);
        var m = float4x4.Identity;
        m.M11 = c; m.M12 = s; m.M21 = -s; m.M22 = c;
        return m;
    }

    /// Scale, then roll (Z), pitch (X), yaw (Y), then position: a RailWerkz Transform as a matrix.
    public static float4x4 Compose(float3 position, float3 rotation, float3 scale) =>
        Scale(scale) * RotationZ(rotation.Z) * RotationX(rotation.X) * RotationY(rotation.Y) * Translation(position);

    public static float3 TransformPoint(float3 p, in float4x4 m) =>
        new(p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    public static float3 TransformDirection(float3 p, in float4x4 m) =>
        new(p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33);

    /// World point to clip space (x, y, z, w) through a view-projection.
    public static float4 Project(float3 p, in float4x4 m) =>
        new(p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43,
            p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44);
}

/// A small fast RNG (xorshift) with the helpers the game needs: ranges, disks, chance.
public sealed class Rng(uint seed = 0x1234567u)
{
    private uint _state = seed == 0 ? 0x1234567u : seed;

    public static Rng Shared { get; } = new((uint)Environment.TickCount);

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    public float Next() => (NextUInt() & 0xFFFFFF) / (float)0x1000000;
    public float Range(float min, float max) => min + (max - min) * Next();
    public int Range(int min, int maxExclusive) => min + (int)(NextUInt() % (uint)Math.Max(1, maxExclusive - min));
    public bool Chance(float probability) => Next() < probability;
    public float Sign() => Next() < 0.5f ? -1f : 1f;

    /// A uniformly distributed point in a disk of the given radius.
    public float2 Disk(float radius)
    {
        var r = radius * MathF.Sqrt(Next());
        var a = Next() * MathEx.Tau;
        return new float2(MathF.Cos(a) * r, MathF.Sin(a) * r);
    }

    public float3 UnitSphere()
    {
        var z = Range(-1f, 1f);
        var a = Next() * MathEx.Tau;
        var r = MathF.Sqrt(1f - z * z);
        return new float3(MathF.Cos(a) * r, z, MathF.Sin(a) * r);
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Range(0, items.Count)];
}
