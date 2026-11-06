using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Rain or snow round the camera. It's drawn over the picture after temporal anti-aliasing, which would otherwise take anything so small
/// and fast for noise and smooth it away, so it tests the world's depth itself.
public sealed unsafe class Precipitation : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4x4 ViewProjection;
        public float4 Wrap;
        public float4 Motion;
        public float4 Shape;
        public float4 Light;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private int _targetVersion = -1;

    public Precipitation()
    {
        var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
        _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        _layout = Pipelines.Layout(128, _setLayout);
        using var vertex = Shader.Load("precipitation.vert");
        using var fragment = Shader.Load("precipitation.frag");
        _pipeline = Pipelines.Create(new PipelineDesc
        {
            Vertex = vertex, Fragment = fragment, Layout = _layout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            Blend = true, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });
    }

    private void Refresh(RenderTarget target)
    {
        if (_targetVersion == target.Version)
            return;

        _targetVersion = target.Version;
        var old = _pool;
        if (!old.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(old, Gpu.Device.VkAllocator));

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        var layout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];
        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        Gpu.Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth }], []);
    }

    /// Whether anything's falling.
    public static bool Falling(Weather weather) => weather.Snowing > 0.001f || weather.Raining > 0.001f;

    /// Inside rendering over the picture.
    public void Draw(CommandBuffer cmd, RenderTarget target, Camera camera, float aspect, Weather weather, Sky sky)
    {
        Refresh(target);

        var snow = weather.Snowing > 0.001f;
        var box = snow ? 40f : 36f;
        static float Wrapped(double value, float box) => (float)(value - Math.Floor(value / box) * box);
        var push = new Push
        {
            ViewProjection = camera.ViewProjection(aspect),
            Wrap = new float4(Wrapped(camera.X, box), Wrapped(camera.Y, box), Wrapped(camera.Z, box), box),
            Motion = snow ? new float4(weather.FallTime, 1.3f, 0.5f, 0.2f) : new float4(weather.FallTime, 9f, 1.2f, 0.42f),
            Shape = snow ? new float4(0.022f, 0.022f, 1f, weather.Snowing) : new float4(0.55f, 0.009f, 0f, weather.Raining),
            Light = new float4(sky.Zenith * 0.8f + sky.Horizon * 0.6f + sky.SunColor * 0.25f, camera.Near),
        };
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_set], []);
        cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
        cmd.Draw(6, snow ? 9000 : 7000, 0, 0);
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
