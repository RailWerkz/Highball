using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.Trains;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The shimmer of hot exhaust over a running engine's stack: a column rising from the mouth through which what's behind
/// wavers, as the hot air bends the light (haze.frag). It's drawn over the picture after temporal anti-aliasing, reading a
/// copy of the picture as it was (PostProcess.Picture) so it can move what's behind it about.
public sealed unsafe class HeatHaze : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4x4 ViewProjection;
        public float4 PlaceWidth; // the mouth relative to the camera; how wide it is
        public float4 Forward;    // the view's forward; w: the near distance
        public float4 Look;       // strength, seconds, how tall the column, 0
        public float4 Lean;       // how far its top is pushed over, in the world's x and z
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
    private const float Tall = 4.5f;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private (int Target, int Picture) _versions = (-1, -1);

    public HeatHaze()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[3];
        for (var i = 0; i < 3; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 3, Bindings = bindings };
        _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        _layout = Pipelines.Layout(sizeof(Push), _setLayout);
        using var vertex = Shader.Load("haze.vert");
        using var fragment = Shader.Load("haze.frag");
        _pipeline = Pipelines.Create(new PipelineDesc
        {
            Vertex = vertex, Fragment = fragment, Layout = _layout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            Blend = true, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });
    }

    private void Refresh(RenderTarget target, StorageImage picture, int pictureVersion, Clouds clouds)
    {
        if (_versions == (target.Version, pictureVersion))
            return;

        _versions = (target.Version, pictureVersion);
        var old = _pool;
        if (!old.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(old, Gpu.Device.VkAllocator));

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        var layout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];
        var seen = new VkDescriptorImageInfo { Sampler = target.Sampler, ImageView = picture.View, ImageLayout = VkImageLayout.General };
        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var noise = new VkDescriptorImageInfo { Sampler = clouds.Noise.Sampler, ImageView = clouds.Noise.View, ImageLayout = VkImageLayout.General };
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &seen },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &noise },
        ], []);
    }

    /// Inside rendering over the picture, with Picture just copied.
    public void Draw(CommandBuffer cmd, RenderTarget target, StorageImage picture, int pictureVersion, Clouds clouds, Camera camera, float aspect,
        IReadOnlyList<HazeSource> sources)
    {
        if (sources.Count == 0)
            return;

        Refresh(target, picture, pictureVersion, clouds);
        var push = new Push
        {
            ViewProjection = camera.ViewProjection(aspect),
            Forward = new float4(camera.Forward, camera.Near),
        };
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_set], []);
        var seconds = (float)(_clock.Elapsed.TotalSeconds % 1000.0);
        foreach (var source in sources)
        {
            var (dx, dy, dz) = (source.X - camera.X, source.Y - camera.Y, source.Z - camera.Z);
            if (dx * dx + dy * dy + dz * dz > 600.0 * 600.0)
                continue; // too far off for a shimmer to show
            push.PlaceWidth = new float4((float)dx, (float)dy, (float)dz, source.Width);
            push.Look = new float4(source.Strength, seconds, Tall, 0f);
            push.Lean = new float4(source.LeanX, 0f, source.LeanZ, 0f);
            cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            cmd.Draw(6, 1, 0, 0);
        }
    }

    public void Dispose()
    {
        var (pool, layout, setLayout) = (_pool, _layout, _setLayout);
        Pipelines.Destroy(_pipeline, default);
        Gpu.Defer(() =>
        {
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
        });
    }
}
