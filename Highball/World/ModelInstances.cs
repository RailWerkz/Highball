using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi;
using System.Runtime.InteropServices;
using RailWerkz.Vulkan;

namespace Highball.World;

/// One placed model as the instanced shaders read it (model.vert, motion.vert, shadow_model.vert), binding 1.
[StructLayout(LayoutKind.Sequential)]
public struct ModelInstance
{
    /// Quaternion, x y z w.
    public float4 Rotation;

    /// xyz: where it stands relative to the camera; w: uniform scale.
    public float4 PlaceAndScale;

    /// Last frame's quaternion.
    public float4 PreviousRotation;

    /// xyz: where it stood last frame, relative to the camera now.
    public float4 PreviousPlace;

    public static readonly VertexAttribute[] Layout = [new(3, 4, 0), new(4, 4, 16), new(5, 4, 32), new(6, 4, 48)];
}

/// The frame's placed models as instances, written into a host-visible buffer of the frame's own slot, so it can be filled
/// at any point while the frame's recorded (inside a shadow cascade, say) with nothing to copy and no barrier. Draws of the
/// same model (and material override) are gathered into one run, drawn with one call per material, or one for the whole
/// mesh where materials don't matter (depth and motion).
public sealed class ModelInstances : IDisposable
{
    /// A run of instances of one model, from First, in Buffer.
    public readonly record struct Batch(RenderModel Model, BufferEx Buffer, int First, int Count, float4? BaseColor, float? Metallic, float? Roughness);

    private readonly BufferEx[] _buffers = new BufferEx[Gpu.FramesInFlight];
    private readonly int[] _capacity = new int[Gpu.FramesInFlight];
    private readonly Dictionary<(RenderModel, float4?, float?, float?), List<int>> _groups = [];
    private readonly List<(RenderModel, float4?, float?, float?)> _order = [];
    private ModelInstance[] _scratch = new ModelInstance[64];
    private int _slot;
    private int _used;

    /// Call once a frame before adding: the slot's buffer is free again, its frame long done.
    public void Begin()
    {
        _slot = Gpu.FrameSlot;
        _used = 0;
    }

    /// Adds the draws keep accepts (given each one's bounding sphere, relative to the camera: centre and radius), gathered
    /// into runs appended to into.
    public void Add(IReadOnlyList<ModelDraw> draws, Camera camera, Func<ModelDraw, float3, float, bool>? keep, List<Batch> into)
    {
        foreach (var list in _groups.Values)
            list.Clear();
        _order.Clear();

        for (var i = 0; i < draws.Count; i++)
        {
            var draw = draws[i];
            if (keep is not null)
            {
                var (centre, radius) = Sphere(draw, camera);
                if (!keep(draw, centre, radius))
                    continue;
            }

            // Its lower-detail version by its distance (RenderModel.ForDistance).
            var relative = draw.RelativeTo(camera.X, camera.Y, camera.Z);
            var model = draw.Model.ForDistance(MathF.Sqrt(relative.X * relative.X + relative.Y * relative.Y + relative.Z * relative.Z));
            var key = (model, draw.BaseColor, draw.Metallic, draw.Roughness);
            if (!_groups.TryGetValue(key, out var list))
                _groups[key] = list = [];
            if (list.Count == 0)
                _order.Add(key);
            list.Add(i);
        }

        var total = 0;
        foreach (var key in _order)
            total += _groups[key].Count;
        if (total == 0)
            return;

        if (_scratch.Length < total)
            _scratch = new ModelInstance[Math.Max(total, _scratch.Length * 2)];
        var buffer = Reserve(_used + total);
        var first = _used;
        var n = 0;
        foreach (var key in _order)
        {
            var list = _groups[key];
            into.Add(new Batch(key.Item1, buffer, first + n, list.Count, key.Item2, key.Item3, key.Item4));
            foreach (var i in list)
                _scratch[n++] = Instance(draws[i], camera);
        }

        using (var mapped = buffer.Map())
            MemoryMarshal.AsBytes(_scratch.AsSpan(0, total)).CopyTo(mapped.AsSpan()[(first * Marshal.SizeOf<ModelInstance>())..]);
        _used += total;

        // Unused groups are dropped now and then, so models let go of aren't held on to.
        if (_groups.Count > 4 * Math.Max(_order.Count, 8))
            foreach (var stale in _groups.Where(g => g.Value.Count == 0).Select(g => g.Key).ToList())
                _groups.Remove(stale);
    }

    /// The slot's buffer, with room for count instances. A buffer outgrown mid-frame stays alive for the runs already
    /// recorded from it (they name it) and is let go of once the frame's done.
    private BufferEx Reserve(int count)
    {
        var buffer = _buffers[_slot];
        if (_capacity[_slot] >= count)
            return buffer;

        var capacity = Math.Max(256, _capacity[_slot]);
        while (capacity < count)
            capacity *= 2;
        var grown = Buffers.Create((long)capacity * Marshal.SizeOf<ModelInstance>(), VkBufferUsageFlags.VertexBufferBit, MemoryPreference.PreferCpu, "models.instances");
        // What was written already is read from the old buffer by the runs that name it.
        if (_capacity[_slot] > 0)
            Gpu.Defer(buffer.Dispose);

        _buffers[_slot] = grown;
        _capacity[_slot] = capacity;
        return grown;
    }

    private static ModelInstance Instance(in ModelDraw draw, Camera camera)
    {
        var relative = draw.RelativeTo(camera.X, camera.Y, camera.Z);
        var rotation = draw.Orientation;
        var was = draw.PreviousAt is { } at
            ? new float3((float)(at.X - camera.X), (float)(at.Y - camera.Y), (float)(at.Z - camera.Z))
            : relative;
        return new ModelInstance
        {
            Rotation = rotation,
            PlaceAndScale = new float4(relative, draw.Scale),
            PreviousRotation = draw.PreviousRotation ?? rotation,
            PreviousPlace = new float4(was, 0f),
        };
    }

    /// A draw's bounding sphere relative to the camera.
    public static (float3 Centre, float Radius) Sphere(in ModelDraw draw, Camera camera)
    {
        var mesh = draw.Model.Mesh;
        var middle = (mesh.BoundsMin + mesh.BoundsMax) * (0.5f * draw.Scale);
        var half = (mesh.BoundsMax - mesh.BoundsMin) * (0.5f * draw.Scale);
        var radius = MathF.Sqrt(half.X * half.X + half.Y * half.Y + half.Z * half.Z);
        return (draw.RelativeTo(camera.X, camera.Y, camera.Z) + Rotate(draw.Orientation, middle), radius);
    }

    private static float3 Rotate(float4 q, float3 v)
    {
        var u = new float3(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2f;
        return v + t * q.W + Cross(u, t);
    }

    private static float3 Cross(float3 a, float3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// Whether a sphere relative to the camera is at least partly inside its view.
    public static bool InView(Camera camera, float aspect, float3 centre, float radius)
    {
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        var (forward, right, up) = (camera.Forward, camera.Right, camera.Up);
        var depth = Dot(centre, forward);
        if (depth < -radius)
            return false;
        // Each side plane through the camera, its normal pointing out of the view.
        return Outside(centre, Normalize(right - forward * tanX)) <= radius && Outside(centre, Normalize(-right - forward * tanX)) <= radius &&
               Outside(centre, Normalize(up - forward * tanY)) <= radius && Outside(centre, Normalize(-up - forward * tanY)) <= radius;
    }

    private static float Outside(float3 p, float3 n) => Dot(p, n);
    private static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static float3 Normalize(float3 v) => v / MathF.Sqrt(Dot(v, v));

    public void Dispose()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            if (_capacity[i] > 0)
                Gpu.Defer(_buffers[i].Dispose);
            _capacity[i] = 0;
        }
    }
}
