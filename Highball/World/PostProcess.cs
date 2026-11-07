using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Time;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// What happens to the picture once the world's drawn (Content/Shaders/Post): temporal anti-aliasing, bloom, measuring its brightness,
/// god rays and the sun's glare, and then, over the screen, exposure, grading and the tonemap.
public sealed unsafe class PostProcess : IDisposable
{
    private const int BloomLevels = 6;

    /// Which level's pixels the exposure is measured from: small enough to loop over, big enough to tell sky from ground.
    private const int MeasuredLevel = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4x4 PreviousViewProjection;
        public float4 Right;
        public float4 Up;
        public float4 Forward;
        public float4 Moved;
        public float4 Sun;
        public float4 SunUv;
        public float4 SunColor;
        public float4 Grade;
        public float4 Look;
    }

    /// Bindings 0 to 4: an image written, up to three read, and the uniforms. Each pass uses what it needs.
    private readonly VkDescriptorSetLayout _computeSetLayout;
    private readonly VkDescriptorSetLayout _tonemapSetLayout;
    private readonly VkPipelineLayout _computeLayout;
    private readonly VkPipelineLayout _tonemapLayout;
    private readonly VkPipeline _temporal;
    private readonly VkPipeline _down;
    private readonly VkPipeline _up;
    private readonly VkPipeline _measure;
    private readonly VkPipeline _rays;
    private readonly VkPipeline _tonemap;
    private readonly BufferEx _uniforms;
    private readonly StorageImage _measured;

    private readonly StorageImage?[] _history = new StorageImage?[2];
    private readonly StorageImage?[] _downs = new StorageImage?[BloomLevels];
    private readonly StorageImage?[] _ups = new StorageImage?[BloomLevels - 1];
    private StorageImage? _shafts;
    private VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _temporalSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _firstDownSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _measureSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _raysSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _tonemapSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _downSets = new VkDescriptorSet[BloomLevels];
    private readonly VkDescriptorSet[] _upSets = new VkDescriptorSet[BloomLevels - 1];
    private int2 _size;
    private int _targetVersion = -1;
    private int _current;
    private bool _historyValid;
    private int _frame;
    private float4x4 _previousViewProjection;
    private (double X, double Y, double Z) _previousCamera;

    /// The exposure set by hand, or by the Lighting window.
    public float Exposure { get; set; } = 1f;

    /// Whether what the picture measures adjusts the exposure.
    public bool MeasureExposure { get; set; } = true;

    /// Counts frames for what changes its noise each one, when there's temporal anti-aliasing to smooth it out; otherwise 0.
    public int NoiseFrame => Settings.Current.TemporalAA ? _frame : 0;

    public PostProcess()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[5];
        bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        for (var i = 1; i < 4; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        bindings[4] = new VkDescriptorSetLayoutBinding { Binding = 4, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        var compute = new VkDescriptorSetLayoutCreateInfo { BindingCount = 5, Bindings = bindings };
        _computeSetLayout = Gpu.Device.CreateDescriptorSetLayout(in compute, Gpu.Device.VkAllocator);

        for (var i = 0; i < 4; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        bindings[4] = new VkDescriptorSetLayoutBinding { Binding = 4, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        var tonemapInfo = new VkDescriptorSetLayoutCreateInfo { BindingCount = 5, Bindings = bindings };
        _tonemapSetLayout = Gpu.Device.CreateDescriptorSetLayout(in tonemapInfo, Gpu.Device.VkAllocator);

        _computeLayout = Pipelines.Layout(0, _computeSetLayout);
        _tonemapLayout = Pipelines.Layout(0, _tonemapSetLayout);

        using var temporal = Shader.Load("Post/TemporalAA.comp");
        using var down = Shader.Load("Post/BloomDown.comp");
        using var up = Shader.Load("Post/BloomUp.comp");
        using var measure = Shader.Load("Post/Measure.comp");
        using var rays = Shader.Load("Post/GodRays.comp");
        using var fullscreen = Shader.Load("fullscreen.vert");
        using var tonemap = Shader.Load("tonemap.frag");
        _temporal = Pipelines.CreateCompute(temporal, _computeLayout);
        _down = Pipelines.CreateCompute(down, _computeLayout);
        _up = Pipelines.CreateCompute(up, _computeLayout);
        _measure = Pipelines.CreateCompute(measure, _computeLayout);
        _rays = Pipelines.CreateCompute(rays, _computeLayout);
        _tonemap = Pipelines.Create(new PipelineDesc
        {
            Vertex = fullscreen, Fragment = tonemap, Layout = _tonemapLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            RenderPass = Gpu.PresentPass, ColorFormat = Gpu.SwapchainFormat,
        });

        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "post.uniforms");
        _measured = new StorageImage(1, 1, 1, VkFormat.R16G16B16A16SFloat, false, false, "post.measured");
        Gpu.Submit(cmd =>
        {
            var clear = new VkClearColorValue();
            var range = Subresources.Full(VkImageAspectFlags.ColorBit);
            cmd.ClearColorImage(_measured.VkImage, VkImageLayout.General, &clear, [range]);
        });
    }

    private static StorageImage Hdr(int width, int height, string name) =>
        new(Math.Max(width, 1), Math.Max(height, 1), 1, VkFormat.R16G16B16A16SFloat, false, false, name);

    private void DisposeImages()
    {
        _history[0]?.Dispose();
        _history[1]?.Dispose();
        foreach (var image in _downs)
            image?.Dispose();
        foreach (var image in _ups)
            image?.Dispose();
        _shafts?.Dispose();
    }

    private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    private static VkDescriptorImageInfo Read(StorageImage image) =>
        new() { Sampler = image.Sampler, ImageView = image.View, ImageLayout = VkImageLayout.General };

    /// A set for a compute pass: the image it writes, and up to three it reads.
    private VkDescriptorSet ComputeSet(StorageImage output, VkDescriptorImageInfo? one, VkDescriptorImageInfo? two = null, VkDescriptorImageInfo? three = null)
    {
        var set = Allocate(_computeSetLayout);
        var written = new VkDescriptorImageInfo { ImageView = output.View, ImageLayout = VkImageLayout.General };
        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var reads = stackalloc VkDescriptorImageInfo[3];
        var writes = new List<VkWriteDescriptorSet>
        {
            new() { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &written },
            new() { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
        };
        void Add(int binding, VkDescriptorImageInfo? info, VkDescriptorImageInfo* slot)
        {
            if (info is not { } image)
                return;

            *slot = image;
            writes.Add(new VkWriteDescriptorSet { DstSet = set, DstBinding = binding, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = slot });
        }

        Add(1, one, &reads[0]);
        Add(2, two, &reads[1]);
        Add(3, three, &reads[2]);
        Gpu.Device.UpdateDescriptorSets(CollectionsMarshal.AsSpan(writes), []);
        return set;
    }

    private VkDescriptorSet TonemapSet(StorageImage picture)
    {
        var set = Allocate(_tonemapSetLayout);
        var (hdr, bloom, rays, measured) = (Read(picture), Read(_ups[0]!), Read(_shafts!), Read(_measured));
        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        const VkDescriptorType sampled = VkDescriptorType.CombinedImageSampler;
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &hdr },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &bloom },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &rays },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &measured },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
        ], []);
        return set;
    }

    /// New images and sets when the window or the world's target changes. Rare enough to afford to wait for the GPU.
    private void Resize(RenderTarget target)
    {
        if (target.Size == _size && _targetVersion == target.Version && _history[0] is not null)
            return;

        Gpu.WaitIdle();
        DisposeImages();
        if (!_pool.IsNull())
            Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);

        _size = target.Size;
        _targetVersion = target.Version;
        _historyValid = false;
        var (w, h) = (_size.X, _size.Y);
        _history[0] = new StorageImage(w, h, 1, VkFormat.R16G16B16A16SFloat, false, false, "post.a", drawnTo: true);
        _history[1] = new StorageImage(w, h, 1, VkFormat.R16G16B16A16SFloat, false, false, "post.b", drawnTo: true);
        for (var i = 0; i < BloomLevels; i++)
            _downs[i] = Hdr(w >> (i + 1), h >> (i + 1), "post.down");
        for (var i = 0; i < BloomLevels - 1; i++)
            _ups[i] = Hdr(w >> (i + 1), h >> (i + 1), "post.up");
        _shafts = Hdr(w / 4, h / 4, "post.rays");

        const int sets = 8 + BloomLevels * 2 + 4;
        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = sets };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = sets * 4 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = sets };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = sets, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var color = new VkDescriptorImageInfo { Sampler = target.Sampler, ImageView = target.ColorView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        for (var i = 0; i < 2; i++)
        {
            var (picture, past) = (_history[i]!, _history[1 - i]!);
            _temporalSets[i] = ComputeSet(picture, color, Read(past), depth);
            _firstDownSets[i] = ComputeSet(_downs[0]!, Read(picture));
            _measureSets[i] = ComputeSet(_measured, Read(_downs[MeasuredLevel]!), Read(picture), depth);
            _raysSets[i] = ComputeSet(_shafts, Read(picture), null, depth);
            _tonemapSets[i] = TonemapSet(picture);
        }

        for (var i = 1; i < BloomLevels; i++)
            _downSets[i] = ComputeSet(_downs[i]!, Read(_downs[i - 1]!));
        for (var i = 0; i < BloomLevels - 1; i++)
            _upSets[i] = ComputeSet(_ups[i]!, Read(_downs[i]!), Read(i == BloomLevels - 2 ? _downs[i + 1]! : _ups[i + 1]!));
    }

    /// Where to draw the world this frame, a fraction of a pixel off each time. Before anything's recorded with the camera.
    public void Jitter(Camera camera, int2 size)
    {
        if (!Settings.Current.TemporalAA)
        {
            camera.Jitter = default;
            return;
        }

        static float Halton(int index, int radix)
        {
            var (result, fraction) = (0f, 1f / radix);
            for (; index > 0; index /= radix, fraction /= radix)
                result += index % radix * fraction;
            return result;
        }

        var n = _frame % 8 + 1;
        camera.Jitter = new float2((Halton(n, 2) - 0.5f) * 2f / size.X, (Halton(n, 3) - 0.5f) * 2f / size.Y);
    }

    private static void Between(CommandBuffer cmd, VkPipelineStageFlags next = VkPipelineStageFlags.ComputeShaderBit)
    {
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, next, VkDependencyFlags.None, [barrier], [], []);
    }

    private void Run(CommandBuffer cmd, VkPipeline pipeline, VkDescriptorSet set, int width, int height)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Compute, pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computeLayout, 0, [set], []);
        cmd.Dispatch((Math.Max(width, 1) + 7) / 8, (Math.Max(height, 1) + 7) / 8, 1);
        Between(cmd);
    }

    /// Outside any render pass, with the world and everything over it finished.
    public void Draw(CommandBuffer cmd, RenderTarget target, Camera camera, float aspect, Sky sky, Action<CommandBuffer>? overPicture = null)
    {
        Resize(target);

        var s = Settings.Current;
        var grade = ColorGrade.Find(s.ColorGrade);
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        var viewProjection = camera.ViewProjection(aspect);

        // Where the sun is on screen. It's only worth a thing while it's in front of the camera.
        var sun = sky.SunDirection;
        var forward = MathEx.Dot(sun, camera.Forward);
        var inFront = MathEx.SmoothStep(0.05f, 0.35f, forward);
        var along = MathF.Max(forward, 0.02f);
        var sunUv = new float2(MathEx.Dot(sun, camera.Right) / (along * tanX) * 0.5f + 0.5f, 0.5f - MathEx.Dot(sun, camera.Up) / (along * tanY) * 0.5f);

        // Stronger as the sun gets low and its light crosses more air.
        var rays = s.GodRays ? 0.07f + 0.25f * (1f - MathF.Min(MathF.Max(sun.Y, 0f) * 2f, 1f)) : 0f;
        var warmth = grade.Warmth;

        var uniforms = new Uniforms
        {
            PreviousViewProjection = _previousViewProjection,
            Right = new float4(camera.Right * tanX, 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, camera.Near),
            Moved = new float4(
                (float)(camera.X - _previousCamera.X), (float)(camera.Y - _previousCamera.Y), (float)(camera.Z - _previousCamera.Z),
                s.TemporalAA && _historyValid ? 1f : 0f),
            Sun = new float4(sun, inFront),
            SunUv = new float4(sunUv.X, sunUv.Y, aspect, Time.RealDelta),
            SunColor = new float4(sky.SunColor, rays),
            Grade = new float4(1f + warmth, 1f, 1f - warmth, grade.Saturation),
            Look = new float4(grade.Contrast, s.Bloom ? 0.05f : 0f, Exposure, MeasureExposure ? 1f : 0f),
        };
        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var updated = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [updated], [], []);

        _current = 1 - _current;
        var (w, h) = (_size.X, _size.Y);
        using (Profiler.Sample("Temporal AA", cmd))
            Run(cmd, _temporal, _temporalSets[_current], w, h);

        // What temporal anti-aliasing would only smear goes on after it. It's in next frame's history too, but that doesn't match
        // what's round it there, so it's clamped away.
        if (overPicture is not null)
        {
            var into = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkDependencyFlags.None, [into], [], []);
            var color = new VkRenderingAttachmentInfo
            {
                ImageView = _history[_current]!.View,
                ImageLayout = VkImageLayout.General,
                LoadOp = VkAttachmentLoadOp.Load,
                StoreOp = VkAttachmentStoreOp.Store,
            };
            var info = new VkRenderingInfo { RenderArea = new VkRect2D(w, h), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &color };
            cmd.BeginRendering(in info);
            Pipelines.SetViewport(cmd, _size);
            overPicture(cmd);
            cmd.EndRendering();
            var outOf = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [outOf], [], []);
        }

        using (Profiler.Sample("Bloom", cmd))
        {
            Run(cmd, _down, _firstDownSets[_current], w >> 1, h >> 1);
            for (var i = 1; i < BloomLevels; i++)
                Run(cmd, _down, _downSets[i], w >> (i + 1), h >> (i + 1));
            for (var i = BloomLevels - 2; i >= 0; i--)
                Run(cmd, _up, _upSets[i], w >> (i + 1), h >> (i + 1));
        }

        using (Profiler.Sample("Exposure", cmd))
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, _measure);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computeLayout, 0, [_measureSets[_current]], []);
            cmd.Dispatch(1, 1, 1);
            Between(cmd);
        }

        using (Profiler.Sample("God rays", cmd))
        {
            Run(cmd, _rays, _raysSets[_current], w / 4, h / 4);
            Between(cmd, VkPipelineStageFlags.FragmentShaderBit);
        }

        _previousViewProjection = viewProjection;
        _previousCamera = (camera.X, camera.Y, camera.Z);
        _historyValid = true;
        _frame++;
    }

    /// Draws the finished picture over the screen. Inside the present pass, before the interface.
    public void Composite(CommandBuffer cmd)
    {
        if (_history[0] is null)
            return;

        var size = Gpu.Extent;
        cmd.SetViewport(0, [new VkViewport { X = 0f, Y = 0f, Width = size.X, Height = size.Y, MinDepth = 0f, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D(size.X, size.Y)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _tonemap);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _tonemapLayout, 0, [_tonemapSets[_current]], []);
        cmd.Draw(3, 1, 0, 0);
    }

    public void Dispose()
    {
        DisposeImages();
        _measured.Dispose();
        var (pool, uniforms) = (_pool, _uniforms);
        var (computeSets, tonemapSets, computeLayout, tonemapLayout) = (_computeSetLayout, _tonemapSetLayout, _computeLayout, _tonemapLayout);
        Pipelines.Destroy(_temporal, default);
        Pipelines.Destroy(_down, default);
        Pipelines.Destroy(_up, default);
        Pipelines.Destroy(_measure, default);
        Pipelines.Destroy(_rays, default);
        Pipelines.Destroy(_tonemap, default);
        Gpu.Defer(() =>
        {
            uniforms.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(computeLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(tonemapLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(computeSets, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(tonemapSets, Gpu.Device.VkAllocator);
        });
    }
}
