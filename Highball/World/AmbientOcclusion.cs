using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.Time;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Darkens the corners the sky can't reach: traced at half size from the depth once the world's drawn, blurred, and laid over the
/// colour (Content/Shaders/Occlusion). The renderer is forward, with no list of what each pixel's light was, so the laying over works
/// out how much of a pixel's light is the sky's. The sun's isn't dimmed, as far as it can tell.
public sealed unsafe class AmbientOcclusion : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4 Right;
        public float4 Up;
        public float4 Forward;
    }

    private readonly VkDescriptorSetLayout _traceSetLayout;
    private readonly VkDescriptorSetLayout _blurSetLayout;
    private readonly VkDescriptorSetLayout _compositeSetLayout;
    private readonly VkPipelineLayout _traceLayout;
    private readonly VkPipelineLayout _blurLayout;
    private readonly VkPipelineLayout _compositeLayout;
    private readonly VkPipeline _trace;
    private readonly VkPipeline _blur;
    private readonly VkPipeline _composite;
    private readonly BufferEx _uniforms;

    private StorageImage? _raw;
    private StorageImage? _blurred;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _traceSet;
    private VkDescriptorSet _blurSet;
    private VkDescriptorSet _compositeSet;
    private int2 _size;
    private int _targetVersion = -1;

    public AmbientOcclusion()
    {
        const VkDescriptorType storage = VkDescriptorType.StorageImage;
        const VkDescriptorType sampled = VkDescriptorType.CombinedImageSampler;
        const VkDescriptorType uniform = VkDescriptorType.UniformBuffer;
        _traceSetLayout = MakeSetLayout(VkShaderStageFlags.ComputeBit, storage, sampled, uniform);
        _blurSetLayout = MakeSetLayout(VkShaderStageFlags.ComputeBit, storage, sampled, sampled, uniform);
        _compositeSetLayout = MakeSetLayout(VkShaderStageFlags.FragmentBit, sampled, sampled, uniform);
        _traceLayout = Pipelines.Layout(0, _traceSetLayout);
        _blurLayout = Pipelines.Layout(0, _blurSetLayout);
        _compositeLayout = Pipelines.Layout(0, _compositeSetLayout, Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);

        using var trace = Shader.Load("Occlusion/TraceOcclusion.comp");
        using var blur = Shader.Load("Occlusion/BlurOcclusion.comp");
        using var fullscreen = Shader.Load("fullscreen.vert");
        using var composite = Shader.Load("occlusion_composite.frag");
        _trace = Pipelines.CreateCompute(trace, _traceLayout);
        _blur = Pipelines.CreateCompute(blur, _blurLayout);
        _composite = Pipelines.Create(new PipelineDesc
        {
            Vertex = fullscreen, Fragment = composite, Layout = _compositeLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            Blend = true, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });

        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "occlusion.uniforms");
    }

    private static VkDescriptorSetLayout MakeSetLayout(VkShaderStageFlags stage, params VkDescriptorType[] types)
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

    /// New images and sets when the window or the world's target changes. Rare enough to afford to wait for the GPU.
    private void Resize(RenderTarget target)
    {
        var size = new int2(Math.Max(1, target.Size.X / 2), Math.Max(1, target.Size.Y / 2));
        if (size == _size && _targetVersion == target.Version && _raw is not null)
            return;

        Gpu.WaitIdle();
        _raw?.Dispose();
        _blurred?.Dispose();
        if (!_pool.IsNull())
            Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);

        _size = size;
        _targetVersion = target.Version;
        _raw = new StorageImage(size.X, size.Y, 1, VkFormat.R8G8B8A8UNorm, false, false, "occlusion.raw");
        _blurred = new StorageImage(size.X, size.Y, 1, VkFormat.R8G8B8A8UNorm, false, false, "occlusion.blurred");

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 5 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 3 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 3, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var rawWritten = new VkDescriptorImageInfo { ImageView = _raw.View, ImageLayout = VkImageLayout.General };
        var rawRead = new VkDescriptorImageInfo { Sampler = _raw.Sampler, ImageView = _raw.View, ImageLayout = VkImageLayout.General };
        var blurredWritten = new VkDescriptorImageInfo { ImageView = _blurred.View, ImageLayout = VkImageLayout.General };
        var blurredRead = new VkDescriptorImageInfo { Sampler = _blurred.Sampler, ImageView = _blurred.View, ImageLayout = VkImageLayout.General };

        _traceSet = Allocate(_traceSetLayout);
        _blurSet = Allocate(_blurSetLayout);
        _compositeSet = Allocate(_compositeSetLayout);
        const VkDescriptorType image = VkDescriptorType.CombinedImageSampler;
        const VkDescriptorType stored = VkDescriptorType.StorageImage;
        const VkDescriptorType uniform = VkDescriptorType.UniformBuffer;
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _traceSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = stored, ImageInfo = &rawWritten },
            new VkWriteDescriptorSet { DstSet = _traceSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = image, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _traceSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = uniform, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = _blurSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = stored, ImageInfo = &blurredWritten },
            new VkWriteDescriptorSet { DstSet = _blurSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = image, ImageInfo = &rawRead },
            new VkWriteDescriptorSet { DstSet = _blurSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = image, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _blurSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = uniform, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = image, ImageInfo = &blurredRead },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = image, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _compositeSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = uniform, BufferInfo = &buffer },
        ], []);
    }

    /// Outside any render pass, after the world's ended and before anything's laid over it. bindWorld binds the sky, the shadows and
    /// the clouds' shade to a layout.
    public void Draw(CommandBuffer cmd, RenderTarget target, Camera camera, float aspect, Action<VkPipelineLayout> bindWorld)
    {
        Resize(target);

        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var uniforms = new Uniforms
        {
            Right = new float4(camera.Right * (tanY * aspect), 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, camera.Near),
        };
        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var updated = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [updated], [], []);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _trace);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _traceLayout, 0, [_traceSet], []);
        cmd.Dispatch((_size.X + 7) / 8, (_size.Y + 7) / 8, 1);

        var traced = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [traced], [], []);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _blur);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _blurLayout, 0, [_blurSet], []);
        cmd.Dispatch((_size.X + 7) / 8, (_size.Y + 7) / 8, 1);

        var blurred = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [blurred], [], []);

        target.BeginOverlay(cmd);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _composite);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _compositeLayout, 0, [_compositeSet], []);
        bindWorld(_compositeLayout);
        cmd.Draw(3, 1, 0, 0);
        target.EndOverlay(cmd);
    }

    public void Dispose()
    {
        _raw?.Dispose();
        _blurred?.Dispose();
        var (pool, uniforms) = (_pool, _uniforms);
        var (traceSets, blurSets, compositeSets) = (_traceSetLayout, _blurSetLayout, _compositeSetLayout);
        var (traceLayout, blurLayout, compositeLayout) = (_traceLayout, _blurLayout, _compositeLayout);
        Pipelines.Destroy(_trace, default);
        Pipelines.Destroy(_blur, default);
        Pipelines.Destroy(_composite, default);
        Gpu.Defer(() =>
        {
            uniforms.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(traceLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(blurLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(compositeLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(traceSets, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(blurSets, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(compositeSets, Gpu.Device.VkAllocator);
        });
    }
}
