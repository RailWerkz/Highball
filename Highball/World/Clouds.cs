using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.Time;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Volumetric clouds over the procedural sky, adapted from Kitten Space Agency's cloud shaders. Compute passes march them at half the
/// screen's size and map their shade on the ground; clouds.glsl (set 3) shows both to the world. They're drawn with the sky, so
/// the ground simply covers them, which holds as long as the camera stays under the layer. A skybox brings its own clouds.
public sealed unsafe class Clouds : IDisposable
{
    private const int NoiseSize = 128;
    private const int ShadowMapSize = 256;
    private const float ShadowMapHalfWidth = 20000f;
    private const float Bottom = 2400f;
    private const float Top = 5200f;
    private const float Density = 0.02f;
    /// The camera's place in the noise wraps at this, a whole number of every noise's tiles, so it stays precise.
    private const double NoisePeriod = 120000.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4x4 PreviousViewProjection;
        public float4 Right;
        public float4 Up;
        public float4 Forward;
        public float4 Sun;
        public float4 SunColor;
        public float4 Zenith;
        public float4 Horizon;
        public float4 Offset;
        public float4 Layer;
        public float4 Frame;
    }

    private static VkDescriptorSetLayout _setLayout;

    private readonly VkDescriptorSetLayout _computeSetLayout;
    private readonly VkPipelineLayout _computeLayout;
    private readonly VkPipeline _march;
    private readonly VkPipeline _shadow;
    private readonly StorageImage _noise;
    private readonly StorageImage _shadowMap;
    private readonly BufferEx _uniforms;

    private StorageImage? _colorA;
    private StorageImage? _colorB;
    private VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _marchSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _worldSets = new VkDescriptorSet[2];
    private VkDescriptorSet _shadowSet;
    private int2 _size;
    private int _current;
    private long _frame;
    private bool _historyValid;
    private float4x4 _previousViewProjection;
    private double _windX;
    private double _windZ;

    /// How much of the sky is cloud, 0 to 1.
    public float Coverage { get; set; } = 0.5f;

    /// How fast they drift, in metres a second.
    public float Wind { get; set; } = 12f;

    /// The layout of the set world shaders read them through: the uniforms, the clouds and their shade.
    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            if (!_setLayout.IsNull())
                return _setLayout;

            var bindings = stackalloc VkDescriptorSetLayoutBinding[3];
            bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            bindings[1] = new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            bindings[2] = new VkDescriptorSetLayoutBinding { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 3, Bindings = bindings };
            _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
            return _setLayout;
        }
    }

    public static void Shutdown()
    {
        if (_setLayout.IsNull())
            return;
        Gpu.Device.DestroyDescriptorSetLayout(_setLayout, Gpu.Device.VkAllocator);
        _setLayout = default;
    }

    public Clouds()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[4];
        bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        bindings[1] = new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        bindings[2] = new VkDescriptorSetLayoutBinding { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        bindings[3] = new VkDescriptorSetLayoutBinding { Binding = 3, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 4, Bindings = bindings };
        _computeSetLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        _computeLayout = Pipelines.Layout(0, _computeSetLayout);

        using var march = Shader.Load("Clouds/RaymarchCloud.comp");
        using var shadow = Shader.Load("Clouds/CloudShadow.comp");
        _march = Pipelines.CreateCompute(march, _computeLayout);
        _shadow = Pipelines.CreateCompute(shadow, _computeLayout);

        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "clouds.uniforms");
        _noise = new StorageImage(NoiseSize, NoiseSize, NoiseSize, VkFormat.R8G8B8A8UNorm, mipmaps: true, repeat: true, "clouds.noise");
        _shadowMap = new StorageImage(ShadowMapSize, ShadowMapSize, 1, VkFormat.R16G16B16A16SFloat, mipmaps: false, repeat: false, "clouds.shadow");
        GenerateNoise();
    }

    /// Writes the noise once: a compute shader's work, which takes a moment but never changes.
    private void GenerateNoise()
    {
        var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
        var setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        var layout = Pipelines.Layout(0, setLayout);
        using var shader = Shader.Load("Clouds/GenerateNoise.comp");
        var pipeline = Pipelines.CreateCompute(shader, layout);

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        var pool = Gpu.Device.CreateDescriptorPool(in poolInfo, Gpu.Device.VkAllocator);
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, SetLayouts = &setLayout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);

        var image = new VkDescriptorImageInfo { ImageView = _noise.View, ImageLayout = VkImageLayout.General };
        Gpu.Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = sets[0], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &image }], []);

        Gpu.Submit(cmd =>
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, pipeline);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [sets[0]], []);
            cmd.Dispatch(NoiseSize / 8, NoiseSize / 8, NoiseSize / 8);
            _noise.GenerateMips(cmd);
        });

        Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyPipeline(pipeline, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
    }

    /// New images and sets for this screen size. Only when the window changes, so it can afford to wait for the GPU.
    private void Resize(int2 screen)
    {
        var size = new int2(Math.Max(1, screen.X / 2), Math.Max(1, screen.Y / 2));
        if (size == _size && _colorA is not null)
            return;

        Gpu.WaitIdle();
        _colorA?.Dispose();
        _colorB?.Dispose();
        if (!_pool.IsNull())
            Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);

        _size = size;
        _colorA = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.a");
        _colorB = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.b");
        _historyValid = false;

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 3 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 10 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 5 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 5, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        _marchSets[0] = ComputeSet(_colorA, _colorB);
        _marchSets[1] = ComputeSet(_colorB, _colorA);
        _shadowSet = ComputeSet(_shadowMap, _colorA);
        _worldSets[0] = WorldSet(_colorA);
        _worldSets[1] = WorldSet(_colorB);
    }

    private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    private static VkDescriptorImageInfo Sampled(StorageImage image) =>
        new() { Sampler = image.Sampler, ImageView = image.View, ImageLayout = VkImageLayout.General };

    private VkDescriptorSet ComputeSet(StorageImage output, StorageImage history)
    {
        var set = Allocate(_computeSetLayout);
        var target = new VkDescriptorImageInfo { ImageView = output.View, ImageLayout = VkImageLayout.General };
        var previous = Sampled(history);
        var noise = Sampled(_noise);
        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &target },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &previous },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &noise },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
        ], []);
        return set;
    }

    private VkDescriptorSet WorldSet(StorageImage color)
    {
        var set = Allocate(SetLayout);
        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var image = Sampled(color);
        var shade = Sampled(_shadowMap);
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &shade },
        ], []);
        return set;
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) =>
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 3, [_worldSets[_current]], []);

    /// Marches this frame's clouds. Outside any render pass, before the world.
    public void Render(CommandBuffer cmd, int2 screen, Camera camera, float aspect, Sky sky)
    {
        Resize(screen);

        var active = sky.Name == Sky.Procedural && Coverage > 0.001f;
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var viewProjection = camera.ViewProjection(aspect);

        // Wind pushes the noise east and a little south, so the clouds move the same whichever way the camera goes.
        _windX += Wind * Time.RealDelta;
        _windZ += Wind * 0.35 * Time.RealDelta;
        static float Wrap(double value) => (float)(value - Math.Floor(value / NoisePeriod) * NoisePeriod);

        var uniforms = new Uniforms
        {
            PreviousViewProjection = _previousViewProjection,
            Right = new float4(camera.Right * (tanY * aspect), 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, 0f),
            Sun = new float4(sky.SunDirection, 0f),
            SunColor = new float4(sky.SunColor, 0f),
            Zenith = new float4(sky.Zenith, 0f),
            Horizon = new float4(sky.Horizon, 0f),
            // The noise's height isn't wrapped or moved: the camera doesn't climb far enough to matter.
            Offset = new float4(Wrap(camera.X - _windX), 0f, Wrap(camera.Z - _windZ), (float)camera.Y),
            Layer = new float4(Bottom, Top, Coverage, Density),
            Frame = new float4(_frame % 1024, active ? 1f : 0f, _historyValid ? 1f : 0f, ShadowMapHalfWidth),
        };

        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var updated = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [updated], [], []);

        if (active)
        {
            _current = 1 - _current;
            cmd.BindPipeline(VkPipelineBindPoint.Compute, _march);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computeLayout, 0, [_marchSets[_current]], []);
            cmd.Dispatch((_size.X + 7) / 8, (_size.Y + 7) / 8, 1);

            cmd.BindPipeline(VkPipelineBindPoint.Compute, _shadow);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _computeLayout, 0, [_shadowSet], []);
            cmd.Dispatch(ShadowMapSize / 8, ShadowMapSize / 8, 1);

            var written = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [written], [], []);
        }

        _historyValid = active;
        _previousViewProjection = viewProjection;
        _frame++;
    }

    public void Dispose()
    {
        _noise.Dispose();
        _shadowMap.Dispose();
        _colorA?.Dispose();
        _colorB?.Dispose();
        var (pool, setLayout, layout, uniforms) = (_pool, _computeSetLayout, _computeLayout, _uniforms);
        Pipelines.Destroy(_march, default);
        Pipelines.Destroy(_shadow, default);
        Gpu.Defer(() =>
        {
            uniforms.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
        });
    }
}
