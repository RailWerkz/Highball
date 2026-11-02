using RailWerkz.Core.Utils;

namespace RailWerkz.Vulkan;

/// A bone's transform relative to its parent: what a pose is made of, one per bone.
public struct BoneTransform(float3 translation, floatQuat rotation, float3 scale)
{
    public float3 Translation = translation;
    public floatQuat Rotation = rotation;
    public float3 Scale = scale;

    public readonly float4x4 Matrix =>
        float4x4.CreateScale(Scale) * float4x4.CreateFromQuaternion(Rotation) * float4x4.CreateTranslation(Translation);

    /// Part way from a to b: rotations along the short arc, the rest straight.
    public static BoneTransform Blend(in BoneTransform a, in BoneTransform b, float t) =>
        new(MathEx.Lerp(a.Translation, b.Translation, t), Quat.Nlerp(a.Rotation, b.Rotation, t), MathEx.Lerp(a.Scale, b.Scale, t));
}

/// One node of the hierarchy a skin hangs from. Joint bones are the ones vertices weight to; the others are the
/// parents above them that still move them (an armature node, a file's up-axis fix).
public sealed record Bone(string Name, int Parent, BoneTransform Rest, bool Joint);

/// One matrix of the palette the vertices index: a bone (or -1 for the model itself), and the inverse of where it
/// stood when the skin was bound.
public readonly record struct SkinJoint(int Bone, float4x4 InverseBind);

/// An animation stored in the glTF file: keyed translations, rotations and scales per bone, sampled linearly.
public sealed class EmbeddedClip
{
    public enum Path : byte { Translation, Rotation, Scale }

    public readonly record struct Channel(int Bone, Path Target, float[] Times, float[] Values);

    public required string Name { get; init; }
    public required float Duration { get; init; }
    public required IReadOnlyList<Channel> Channels { get; init; }

    /// Every distinct key time, for turning the clip into keyframes.
    public IEnumerable<float> KeyTimes => Channels.SelectMany(c => c.Times).Select(t => MathF.Round(t, 4)).Distinct().Order();

    /// The pose at a time, over the rest pose for bones the clip leaves alone.
    public void Sample(float time, Span<BoneTransform> pose)
    {
        foreach (var channel in Channels)
        {
            var times = channel.Times;
            var i = Array.BinarySearch(times, time);
            int a, b;
            var t = 0f;
            if (i >= 0)
            {
                a = b = i;
            }
            else
            {
                b = Math.Min(~i, times.Length - 1);
                a = Math.Max(b - 1, 0);
                if (b != a)
                    t = Math.Clamp((time - times[a]) / (times[b] - times[a]), 0f, 1f);
            }

            var v = channel.Values;
            ref var bone = ref pose[channel.Bone];
            switch (channel.Target)
            {
                case Path.Rotation:
                    bone.Rotation = Quat.Nlerp(new floatQuat(v[a * 4], v[a * 4 + 1], v[a * 4 + 2], v[a * 4 + 3]), new floatQuat(v[b * 4], v[b * 4 + 1], v[b * 4 + 2], v[b * 4 + 3]), t);
                    break;
                case Path.Translation:
                    bone.Translation = MathEx.Lerp(new float3(v[a * 3], v[a * 3 + 1], v[a * 3 + 2]), new float3(v[b * 3], v[b * 3 + 1], v[b * 3 + 2]), t);
                    break;
                default:
                    bone.Scale = MathEx.Lerp(new float3(v[a * 3], v[a * 3 + 1], v[a * 3 + 2]), new float3(v[b * 3], v[b * 3 + 1], v[b * 3 + 2]), t);
                    break;
            }
        }
    }
}

/// The bones a skinned model bends with, parents before children, and the palette of joint matrices its vertices
/// index. Placement is the model's turn, centring and scaling (the same Model.Load gives a static model), applied
/// after the bones, so bone matrices come out in the model's own metres with +z forward.
public sealed class Skeleton
{
    /// The most joints the skinned shader takes (its uJoints array).
    public const int MaxJoints = 64;

    public IReadOnlyList<Bone> Bones { get; }
    public IReadOnlyList<SkinJoint> Joints { get; }
    public IReadOnlyList<EmbeddedClip> Clips { get; }
    public float4x4 Placement { get; internal set; }

    /// The topmost joint: the one a whole-body move (crouching, lying down) shifts.
    public int Root { get; }

    private readonly BoneTransform[] _rest;

    internal Skeleton(List<Bone> bones, List<SkinJoint> joints, List<EmbeddedClip> clips)
    {
        Bones = bones;
        Joints = joints;
        Clips = clips;
        Placement = float4x4.Identity;
        _rest = [.. bones.Select(b => b.Rest)];
        Root = Math.Max(0, bones.FindIndex(b => b.Joint));
    }

    public ReadOnlySpan<BoneTransform> Rest => _rest;

    public int Find(string name)
    {
        for (var i = 0; i < Bones.Count; i++)
        {
            if (Bones[i].Name == name)
                return i;
        }

        return -1;
    }

    /// Each bone's matrix in the model's space for a pose.
    public void Globals(ReadOnlySpan<BoneTransform> pose, Span<float4x4> globals)
    {
        for (var i = 0; i < Bones.Count; i++)
        {
            var parent = Bones[i].Parent;
            globals[i] = pose[i].Matrix * (parent < 0 ? Placement : globals[parent]);
        }
    }

    /// The joint matrices for the shader from the bones' global matrices.
    public void Palette(ReadOnlySpan<float4x4> globals, Span<float4x4> palette)
    {
        for (var j = 0; j < Joints.Count; j++)
            palette[j] = Joints[j].Bone < 0 ? Joints[j].InverseBind * Placement : Joints[j].InverseBind * globals[Joints[j].Bone];
    }

    /// The palette for a pose, in one go, with scratch space for the globals.
    public void Palette(ReadOnlySpan<BoneTransform> pose, Span<float4x4> globals, Span<float4x4> palette)
    {
        Globals(pose, globals);
        Palette(globals, palette);
    }
}

/// Quaternion helpers with the order spelled out: Mul(a, b) turns by b first, then a (the Hamilton product).
public static class Quat
{
    public static floatQuat Mul(floatQuat a, floatQuat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public static floatQuat Inverse(floatQuat q) => new(-q.X, -q.Y, -q.Z, q.W);

    public static floatQuat Normalize(floatQuat q)
    {
        var length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return length > 1e-8f ? new floatQuat(q.X / length, q.Y / length, q.Z / length, q.W / length) : new floatQuat(0f, 0f, 0f, 1f);
    }

    /// Straight-line blend, normalised, the short way round: close enough to a slerp for the small steps between keys.
    public static floatQuat Nlerp(floatQuat a, floatQuat b, float t)
    {
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        var s = dot < 0f ? -t : t;
        var k = 1f - t;
        return Normalize(new floatQuat(a.X * k + b.X * s, a.Y * k + b.Y * s, a.Z * k + b.Z * s, a.W * k + b.W * s));
    }

    public static floatQuat Axis(float3 axis, float angle)
    {
        var (s, c) = MathF.SinCos(angle * 0.5f);
        return new floatQuat(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    /// Degrees about x, then y, then z.
    public static floatQuat FromEuler(float3 degrees) =>
        Mul(Axis(new float3(0f, 0f, 1f), degrees.Z * MathEx.Deg2Rad),
            Mul(Axis(new float3(0f, 1f, 0f), degrees.Y * MathEx.Deg2Rad), Axis(new float3(1f, 0f, 0f), degrees.X * MathEx.Deg2Rad)));

    /// The inverse of FromEuler, in degrees.
    public static float3 ToEuler(floatQuat q)
    {
        // q = qz * qy * qx: as a matrix (column vectors) R = Rz Ry Rx.
        var m20 = 2f * (q.X * q.Z - q.W * q.Y);
        var y = MathF.Asin(Math.Clamp(-m20, -1f, 1f));
        var m21 = 2f * (q.Y * q.Z + q.W * q.X);
        var m22 = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var m10 = 2f * (q.X * q.Y + q.W * q.Z);
        var m00 = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        var x = MathF.Atan2(m21, m22);
        var z = MathF.Atan2(m10, m00);
        return new float3(x, y, z) * MathEx.Rad2Deg;
    }

    /// The rotation of a matrix that may also scale (rows normalised first).
    public static floatQuat FromMatrix(in float4x4 m)
    {
        var x = MathEx.Normalize(new float3(m.M11, m.M12, m.M13));
        var y = MathEx.Normalize(new float3(m.M21, m.M22, m.M23));
        var z = MathEx.Normalize(new float3(m.M31, m.M32, m.M33));
        var r = float4x4.Identity;
        r.M11 = x.X; r.M12 = x.Y; r.M13 = x.Z;
        r.M21 = y.X; r.M22 = y.Y; r.M23 = y.Z;
        r.M31 = z.X; r.M32 = z.Y; r.M33 = z.Z;
        return Normalize(floatQuat.CreateFromRotationMatrix(r));
    }
}
