using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.Trains;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Exhaust smoke, marched the way Kitten Space Agency marches its volumetric trails, at a train's scale. Each plume is a
/// chain of capsules (ExhaustSmoke's segments). Each frame they're sorted into tiles of the screen on the CPU; then, at half
/// the screen's size, smoke_march.comp gathers for each pixel just the capsules its ray crosses, marches the stretch they
/// span, blends overlapping capsules into one shape and carves it with the clouds' noise, fixed in the world as the trails'
/// is, so the smoke moves through it. smoke_composite.frag lays the result over the picture, each pixel preferring the
/// half-size samples that marched as far as it did, so smoke doesn't bleed over the edge of what's in front of it.
public sealed unsafe class Smoke : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4 Right;    // right * tan(half fov x)
        public float4 Up;       // up * tan(half fov y)
        public float4 Forward;  // w: the near distance
        public float4 Sun;
        public float4 SunColor;
        public float4 SkyTop;
        public float4 SkyLow;
        public float4 Noise;    // xyz: the camera's place in the noise (wrapped); w: noise tiles per metre
        public float4 Info;     // segments, tiles across, tiles down, seconds
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuSegment
    {
        public float4 A;    // one end relative to the camera; its radius
        public float4 B;    // the other
        public float4 Look; // density, soot, age, billow
    }

    public const int MaxSegments = ExhaustSmoke.MaxPuffs;
    private const int TilesX = 48;
    private const int TilesY = 27;
    /// The most capsules one tile lists: the thickest, so where old, spread-out, faint smoke crowds a tile, the dense fresh
    /// plume is never what's left out (that cut it off along the tile's edge). Every tile can have its fill, so zoomed in,
    /// with each capsule over many tiles, the list never runs out partway down the screen.
    private const int PerTile = 256;
    private const int MaxIndices = TilesX * TilesY * PerTile;

    /// The noise's tile, in metres; the camera's place in it wraps at a whole number of them so it stays precise.
    private const double NoiseTile = 2.5;
    private const double NoiseWrap = NoiseTile * 100.0;

    private readonly VkDescriptorSetLayout _marchSetLayout;
    private readonly VkDescriptorSetLayout _compositeSetLayout;
    private readonly VkPipelineLayout _marchLayout;
    private readonly VkPipelineLayout _compositeLayout;
    private readonly VkPipeline _march;
    private readonly VkPipeline _composite;
    private readonly BufferEx _uniforms;
    private readonly BufferEx _segments;
    private readonly BufferEx _tiles;
    private readonly BufferEx _indices;
    private readonly GpuSegment[] _gpu = new GpuSegment[MaxSegments];
    private readonly uint[] _tileRanges = new uint[TilesX * TilesY * 2];
    private readonly uint[] _list = new uint[MaxIndices];
    private readonly List<int>[] _binned = Enumerable.Range(0, TilesX * TilesY).Select(_ => new List<int>()).ToArray();
    private readonly float[] _thickness = new float[MaxSegments];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private StorageImage? _color;
    private StorageImage? _along;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _marchSet;
    private VkDescriptorSet _compositeSet;
    private int _targetVersion = -1;
    private int _count;

    public Smoke()
    {
        const VkDescriptorType storage = VkDescriptorType.StorageBuffer, image = VkDescriptorType.StorageImage, sampled = VkDescriptorType.CombinedImageSampler;
        _marchSetLayout = SetLayout(VkShaderStageFlags.ComputeBit, VkDescriptorType.UniformBuffer, storage, storage, storage, image, image, sampled, sampled);
        _compositeSetLayout = SetLayout(VkShaderStageFlags.FragmentBit, sampled, sampled, sampled, VkDescriptorType.UniformBuffer);
        _marchLayout = Pipelines.Layout(0, _marchSetLayout);
        _compositeLayout = Pipelines.Layout(0, _compositeSetLayout);
        using (var march = Shader.Load("smoke_march.comp"))
            _march = Pipelines.CreateCompute(march, _marchLayout);
        using var vertex = Shader.Load("fullscreen.vert");
        using var fragment = Shader.Load("smoke_composite.frag");
        _composite = Pipelines.Create(new PipelineDesc
        {
            Vertex = vertex, Fragment = fragment, Layout = _compositeLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            Blend = true, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });
        var transfer = VkBufferUsageFlags.TransferDstBit;
        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | transfer, MemoryPreference.PreferGpu, "smoke.uniforms");
        _segments = Buffers.Create(MaxSegments * sizeof(GpuSegment), VkBufferUsageFlags.StorageBufferBit | transfer, MemoryPreference.PreferGpu, "smoke.segments");
        _tiles = Buffers.Create(_tileRanges.Length * sizeof(uint), VkBufferUsageFlags.StorageBufferBit | transfer, MemoryPreference.PreferGpu, "smoke.tiles");
        _indices = Buffers.Create(MaxIndices * sizeof(uint), VkBufferUsageFlags.StorageBufferBit | transfer, MemoryPreference.PreferGpu, "smoke.indices");
    }

    public bool Any => _count > 0;

    private static VkDescriptorSetLayout SetLayout(VkShaderStageFlags stage, params VkDescriptorType[] types)
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[types.Length];
        for (var i = 0; i < types.Length; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = types[i], DescriptorCount = 1, StageFlags = stage };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = types.Length, Bindings = bindings };
        return Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
    }

    private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    /// The half-size images the march writes, and the sets that bind them, made again when the picture's size changes.
    private void Refresh(RenderTarget target, Clouds clouds)
    {
        if (_targetVersion == target.Version && _color is not null)
            return;

        _targetVersion = target.Version;
        var oldPool = _pool;
        if (!oldPool.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(oldPool, Gpu.Device.VkAllocator));
        // An image puts off its own release till the GPU's done with it.
        _color?.Dispose();
        _along?.Dispose();

        var (w, h) = (Math.Max(1, target.Size.X / 2), Math.Max(1, target.Size.Y / 2));
        _color = new StorageImage(w, h, 1, VkFormat.R16G16B16A16SFloat, false, false, "smoke.color");
        _along = new StorageImage(w, h, 1, VkFormat.R32SFloat, false, false, "smoke.along");

        var sizes = stackalloc VkDescriptorPoolSize[4];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 3 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 };
        sizes[3] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 5 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 2, PoolSizeCount = 4, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        _marchSet = Allocate(_marchSetLayout);
        _compositeSet = Allocate(_compositeSetLayout);

        var uniforms = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var segments = new VkDescriptorBufferInfo { Buffer = _segments.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(MaxSegments * sizeof(GpuSegment)) };
        var tiles = new VkDescriptorBufferInfo { Buffer = _tiles.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(_tileRanges.Length * sizeof(uint)) };
        var indices = new VkDescriptorBufferInfo { Buffer = _indices.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(MaxIndices * sizeof(uint)) };
        var colorOut = new VkDescriptorImageInfo { ImageView = _color.View, ImageLayout = VkImageLayout.General };
        var alongOut = new VkDescriptorImageInfo { ImageView = _along.View, ImageLayout = VkImageLayout.General };
        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var noise = new VkDescriptorImageInfo { Sampler = clouds.Noise.Sampler, ImageView = clouds.Noise.View, ImageLayout = VkImageLayout.General };
        var colorIn = new VkDescriptorImageInfo { Sampler = _color.Sampler, ImageView = _color.View, ImageLayout = VkImageLayout.General };
        var alongIn = new VkDescriptorImageInfo { Sampler = _along.Sampler, ImageView = _along.View, ImageLayout = VkImageLayout.General };
        const VkDescriptorType sampled = VkDescriptorType.CombinedImageSampler;
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniforms },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &segments },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &tiles },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &indices },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &colorOut },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &alongOut },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _marchSet, DstBinding = 7, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &noise },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &colorIn },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &alongIn },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniforms },
        ], []);
    }

    /// Sorts this frame's segments into the screen's tiles and writes them, with where the camera is, for the march.
    /// Outside any render pass, before the world.
    public void Upload(CommandBuffer cmd, Camera camera, float aspect, IReadOnlyList<SmokeSegment> segments, Sky sky)
    {
        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        foreach (var list in _binned)
            list.Clear();

        _count = 0;
        for (var i = 0; i < segments.Count && _count < MaxSegments; i++)
        {
            var s = segments[i];
            var a = new float3((float)(s.AX - camera.X), (float)(s.AY - camera.Y), (float)(s.AZ - camera.Z));
            var b = new float3((float)(s.BX - camera.X), (float)(s.BY - camera.Y), (float)(s.BZ - camera.Z));
            var centre = (a + b) * 0.5f;
            var half = b - a;
            var reach = 0.5f * MathF.Sqrt(Dot(half, half)) + MathF.Max(s.RadiusA, s.RadiusB);
            var ahead = Dot(centre, forward);
            if (ahead + reach < camera.Near || Dot(centre, centre) > 3000f * 3000f || s.Density * MathF.Max(s.RadiusA, s.RadiusB) < 0.004f)
                continue; // behind, too far off to see, or too thin to show

            // The tiles its bounding ball covers: all of them if the camera's in or right by it.
            int x0 = 0, x1 = TilesX - 1, y0 = 0, y1 = TilesY - 1;
            if (ahead - reach > camera.Near * 2f)
            {
                // Generous: a ball off to the side of the view is drawn stretched, wider than its outline at the middle.
                var spread = reach / MathF.Sqrt(MathF.Max(ahead * ahead - reach * reach, 1e-4f)) * 1.6f;
                var sx = Dot(centre, right) / (ahead * tanX);
                var sy = Dot(centre, up) / (ahead * tanY);
                var rx = spread / tanX;
                var ry = spread / tanY;
                if (sx + rx < -1f || sx - rx > 1f || sy + ry < -1f || sy - ry > 1f)
                    continue;
                // And a tile more each way, so nothing's clipped along a tile's edge.
                x0 = Math.Clamp((int)MathF.Floor((sx - rx + 1f) * 0.5f * TilesX) - 1, 0, TilesX - 1);
                x1 = Math.Clamp((int)MathF.Floor((sx + rx + 1f) * 0.5f * TilesX) + 1, 0, TilesX - 1);
                y0 = Math.Clamp((int)MathF.Floor((1f - (sy + ry)) * 0.5f * TilesY) - 1, 0, TilesY - 1);
                y1 = Math.Clamp((int)MathF.Floor((1f - (sy - ry)) * 0.5f * TilesY) + 1, 0, TilesY - 1);
            }

            var index = _count++;
            _thickness[index] = s.Density * MathF.Max(s.RadiusA, s.RadiusB);
            _gpu[index] = new GpuSegment
            {
                A = new float4(a, s.RadiusA),
                B = new float4(b, s.RadiusB),
                Look = new float4(s.Density, s.Soot, s.Age, s.Billow),
            };
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    _binned[y * TilesX + x].Add(index);
        }

        if (_count == 0)
            return;

        var used = 0;
        for (var t = 0; t < _binned.Length; t++)
        {
            var list = _binned[t];
            if (list.Count > PerTile)
                list.Sort((a, b) => _thickness[b].CompareTo(_thickness[a]));
            var n = Math.Min(list.Count, PerTile);
            for (var k = 0; k < n; k++)
                _list[used + k] = (uint)list[k];
            _tileRanges[t * 2] = (uint)used;
            _tileRanges[t * 2 + 1] = (uint)n;
            used += n;
        }

        static float Wrap(double v) => (float)((v - Math.Floor(v / NoiseWrap) * NoiseWrap) / NoiseTile);
        var uniforms = new Uniforms
        {
            Right = new float4(right * tanX, 0f),
            Up = new float4(up * tanY, 0f),
            Forward = new float4(forward, camera.Near),
            Sun = new float4(sky.SunDirection, 0f),
            SunColor = new float4(sky.SunColor, 0f),
            SkyTop = new float4(sky.Zenith, 0f),
            SkyLow = new float4(sky.Horizon, 0f),
            Noise = new float4(Wrap(camera.X), Wrap(camera.Y), Wrap(camera.Z), (float)(1.0 / NoiseTile)),
            Info = new float4(_count, TilesX, TilesY, (float)(_clock.Elapsed.TotalSeconds % 1000.0)),
        };
        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        Write(cmd, _segments, MemoryMarshal.AsBytes(_gpu.AsSpan(0, _count)));
        Write(cmd, _tiles, MemoryMarshal.AsBytes(_tileRanges.AsSpan()));
        Write(cmd, _indices, MemoryMarshal.AsBytes(_list.AsSpan(0, Math.Max(used, 1))));
        var written = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [written], [], []);
    }

    /// A buffer update carries 64 KB at most: more goes in several.
    private static void Write(CommandBuffer cmd, BufferEx buffer, ReadOnlySpan<byte> bytes)
    {
        for (var start = 0; start < bytes.Length; start += 65536)
            cmd.UpdateBuffer(buffer.VkBuffer, ByteSize.Of(start), bytes.Slice(start, Math.Min(65536, bytes.Length - start)));
    }

    /// Marches the smoke into the half-size images. Outside any render pass, once the world's depth is finished.
    public void March(CommandBuffer cmd, RenderTarget target, Clouds clouds)
    {
        if (_count == 0)
            return;

        Refresh(target, clouds);
        // The world's depth written, and last frame's composite done reading what's about to be written over.
        var before = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ShaderReadBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, [before], [], []);
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _march);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _marchLayout, 0, [_marchSet], []);
        cmd.Dispatch((_color!.Width + 7) / 8, (_color.Height + 7) / 8, 1);
        var after = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [after], [], []);
    }

    /// Lays the marched smoke over the picture. Inside rendering over the picture.
    public void Draw(CommandBuffer cmd)
    {
        if (_count == 0 || _color is null)
            return;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _composite);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _compositeLayout, 0, [_compositeSet], []);
        cmd.Draw(3, 1, 0, 0);
    }

    private static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public void Dispose()
    {
        var pool = _pool;
        _color?.Dispose();
        _along?.Dispose();
        var (marchLayout, compositeLayout, marchSetLayout, compositeSetLayout) = (_marchLayout, _compositeLayout, _marchSetLayout, _compositeSetLayout);
        Pipelines.Destroy(_march, default);
        Pipelines.Destroy(_composite, default);
        _uniforms.Dispose();
        _segments.Dispose();
        _tiles.Dispose();
        _indices.Dispose();
        Gpu.Defer(() =>
        {
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(marchLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(compositeLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(marchSetLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(compositeSetLayout, Gpu.Device.VkAllocator);
        });
    }
}
