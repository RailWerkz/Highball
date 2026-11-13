using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Core.Time;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Volumetric clouds over the procedural sky, adapted from Kitten Space Agency's cloud shaders. What they look like comes from a
/// CloudType. Before the world is drawn, a compute pass maps their shade on the ground (clouds.glsl, set 3). After it, compute passes
/// march a checkerboard of them at half the screen's size as far as its depth, settle the noise against last frame, and they're laid over the world,
/// so they hold from under, inside or above them, and in front of a mountain. A skybox brings its own clouds.
public sealed unsafe class Clouds : IDisposable
{
    private const int NoiseSize = 128;
    private const int ShadowMapSize = 256;
    private const float ShadowMapHalfWidth = 20000f;
    /// The camera's place in the noise wraps at this, a whole number of every tile in CloudType, so it stays precise.
    private const double NoisePeriod = 2520000.0;
    private const float NoCloud = -1f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Layer
    {
        public float4 Heights;
        public float4 Tiles;
        public float4 Profile;
        public float4 Look;
        public float4 Look2;
        public float4 Relief;
    }

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
        public float4 Frame;
        public float4 Ground;
        public float4 Motion;
        public float4 High;
        public Layer Layer0;
        public Layer Layer1;
    }

    private static VkDescriptorSetLayout _setLayout;

    private readonly VkDescriptorSetLayout _marchSetLayout;
    private readonly VkDescriptorSetLayout _temporalSetLayout;
    private readonly VkDescriptorSetLayout _compositeSetLayout;
    private readonly VkPipelineLayout _marchLayout;
    private readonly VkPipelineLayout _temporalLayout;
    private readonly VkPipelineLayout _compositeLayout;
    private readonly VkPipeline _march;
    private readonly VkPipeline _temporal;
    private readonly VkPipeline _shadow;
    private readonly VkPipeline _composite;
    private readonly StorageImage _noise;

    /// The tiling noise the clouds are carved from, which exhaust smoke is carved from too (Smoke).
    public StorageImage Noise => _noise;
    private readonly StorageImage _shadowMap;
    private readonly BufferEx _uniforms;
    private readonly VkDescriptorPool _worldPool;
    private readonly VkDescriptorSet _worldSet;

    private StorageImage? _raw;
    private StorageImage? _rawDistance;
    private StorageImage? _colorA;
    private StorageImage? _colorB;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _marchSet;
    private VkDescriptorSet _shadowSet;
    private readonly VkDescriptorSet[] _temporalSets = new VkDescriptorSet[2];
    private readonly VkDescriptorSet[] _compositeSets = new VkDescriptorSet[2];
    private int2 _size;
    private int _targetVersion = -1;
    private int _current;
    private long _frame;
    private bool _active;
    private bool _historyValid;
    private float4x4 _previousViewProjection;
    private double _windX;
    private double _windZ;
    private (double X, double Y, double Z) _previousCamera;

    /// What kind of clouds.
    public CloudType Type { get; set; } = CloudType.Find("FairWeather");

    /// How much of them, 0 to 1; a half is what's typical of the type.
    public float Amount { get; set; } = 0.5f;

    /// How fast they drift, in metres a second.
    public float Wind { get; set; } = 12f;

    /// The route's ground, in metres, which low clouds are measured from.
    public float GroundLevel { get; set; }

    /// The layout of the set world shaders read their shade through: the uniforms and the shade.
    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            if (!_setLayout.IsNull())
                return _setLayout;

            var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
            bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            bindings[1] = new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings };
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

    private static VkDescriptorSetLayout MakeSetLayout(VkShaderStageFlags stage, params VkDescriptorType[] types)
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[types.Length];
        for (var i = 0; i < types.Length; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = types[i], DescriptorCount = 1, StageFlags = stage };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = types.Length, Bindings = bindings };
        return Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
    }

    public Clouds()
    {
        const VkDescriptorType storage = VkDescriptorType.StorageImage;
        const VkDescriptorType sampled = VkDescriptorType.CombinedImageSampler;
        const VkDescriptorType uniform = VkDescriptorType.UniformBuffer;
        _marchSetLayout = MakeSetLayout(VkShaderStageFlags.ComputeBit, storage, storage, sampled, uniform, sampled);
        _temporalSetLayout = MakeSetLayout(VkShaderStageFlags.ComputeBit, storage, sampled, sampled, uniform, sampled);
        _compositeSetLayout = MakeSetLayout(VkShaderStageFlags.FragmentBit, sampled, sampled, uniform);
        _marchLayout = Pipelines.Layout(0, _marchSetLayout);
        _temporalLayout = Pipelines.Layout(0, _temporalSetLayout);
        _compositeLayout = Pipelines.Layout(0, _compositeSetLayout);

        using var march = Shader.Load("Clouds/RaymarchCloud.comp");
        using var temporal = Shader.Load("Clouds/TemporalCloud.comp");
        using var shadow = Shader.Load("Clouds/CloudShadow.comp");
        using var fullscreen = Shader.Load("fullscreen.vert");
        using var composite = Shader.Load("cloud_composite.frag");
        _march = Pipelines.CreateCompute(march, _marchLayout);
        _temporal = Pipelines.CreateCompute(temporal, _temporalLayout);
        _shadow = Pipelines.CreateCompute(shadow, _marchLayout);
        _composite = Pipelines.Create(new PipelineDesc
        {
            Vertex = fullscreen, Fragment = composite, Layout = _compositeLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            Blend = true, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });

        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "clouds.uniforms");
        _noise = new StorageImage(NoiseSize, NoiseSize, NoiseSize, VkFormat.R8G8B8A8UNorm, mipmaps: true, repeat: true, "clouds.noise");
        _shadowMap = new StorageImage(ShadowMapSize, ShadowMapSize, 1, VkFormat.R16G16B16A16SFloat, mipmaps: false, repeat: false, "clouds.shadow");
        GenerateNoise();

        // The world's set only needs the shade and the uniforms, which last the clouds' whole life.
        var sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 2, PoolSizes = sizes };
        _worldPool = Gpu.Device.CreateDescriptorPool(in poolInfo, Gpu.Device.VkAllocator);
        _worldSet = Allocate(_worldPool, SetLayout);
        var buffer = BufferInfo();
        var shade = Sampled(_shadowMap);
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _worldSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = uniform, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = _worldSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = sampled, ImageInfo = &shade },
        ], []);
    }

    /// Writes the noise once: a compute shader's work, which takes a moment but never changes.
    private void GenerateNoise()
    {
        var setLayout = MakeSetLayout(VkShaderStageFlags.ComputeBit, VkDescriptorType.StorageImage);
        var layout = Pipelines.Layout(0, setLayout);
        using var shader = Shader.Load("Clouds/GenerateNoise.comp");
        var pipeline = Pipelines.CreateCompute(shader, layout);

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        var pool = Gpu.Device.CreateDescriptorPool(in poolInfo, Gpu.Device.VkAllocator);
        var set = Allocate(pool, setLayout);

        var image = new VkDescriptorImageInfo { ImageView = _noise.View, ImageLayout = VkImageLayout.General };
        Gpu.Device.UpdateDescriptorSets([new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &image }], []);

        Gpu.Submit(cmd =>
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, pipeline);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [set], []);
            cmd.Dispatch(NoiseSize / 8, NoiseSize / 8, NoiseSize / 8);
            _noise.GenerateMips(cmd);
        });

        Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyPipeline(pipeline, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
    }

    /// New images and sets when the window or the world's target changes. Rare enough to afford to wait for the GPU.
    private void Resize(RenderTarget target)
    {
        var size = new int2(Math.Max(1, target.Size.X / 2), Math.Max(1, target.Size.Y / 2));
        if (size == _size && _targetVersion == target.Version && _colorA is not null)
            return;

        Gpu.WaitIdle();
        _raw?.Dispose();
        _rawDistance?.Dispose();
        _colorA?.Dispose();
        _colorB?.Dispose();
        if (!_pool.IsNull())
            Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);

        _size = size;
        _targetVersion = target.Version;
        _raw = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.raw");
        _rawDistance = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.distance");
        _colorA = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.a");
        _colorB = new StorageImage(size.X, size.Y, 1, VkFormat.R16G16B16A16SFloat, false, false, "clouds.b");
        _historyValid = false;

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 8 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 16 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 8 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 8, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        _marchSet = MarchSet(_raw, _rawDistance, depth);
        _shadowSet = ShadowSet();
        _temporalSets[0] = TemporalSet(_colorA, _colorB);
        _temporalSets[1] = TemporalSet(_colorB, _colorA);
        _compositeSets[0] = CompositeSet(_colorA, depth);
        _compositeSets[1] = CompositeSet(_colorB, depth);
    }

    private static VkDescriptorSet Allocate(VkDescriptorPool pool, VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    private static VkDescriptorImageInfo Sampled(StorageImage image) =>
        new() { Sampler = image.Sampler, ImageView = image.View, ImageLayout = VkImageLayout.General };

    private static VkDescriptorImageInfo Written(StorageImage image) =>
        new() { ImageView = image.View, ImageLayout = VkImageLayout.General };

    private VkDescriptorBufferInfo BufferInfo() =>
        new() { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };

    private VkDescriptorSet MarchSet(StorageImage color, StorageImage distance, VkDescriptorImageInfo depth)
    {
        var set = Allocate(_pool, _marchSetLayout);
        var (colorInfo, distanceInfo, noise, buffer) = (Written(color), Written(distance), Sampled(_noise), BufferInfo());
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &colorInfo },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &distanceInfo },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &noise },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth },
        ], []);
        return set;
    }

    /// The shade map's pass only reads the bindings it shares with the march.
    private VkDescriptorSet ShadowSet()
    {
        var set = Allocate(_pool, _marchSetLayout);
        var (target, noise, buffer) = (Written(_shadowMap), Sampled(_noise), BufferInfo());
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &target },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &noise },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
        ], []);
        return set;
    }

    private VkDescriptorSet TemporalSet(StorageImage output, StorageImage history)
    {
        var set = Allocate(_pool, _temporalSetLayout);
        var (target, raw, distance, past, buffer) = (Written(output), Sampled(_raw!), Sampled(_rawDistance!), Sampled(history), BufferInfo());
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &target },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &raw },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &distance },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &past },
        ], []);
        return set;
    }

    private VkDescriptorSet CompositeSet(StorageImage color, VkDescriptorImageInfo depth)
    {
        var set = Allocate(_pool, _compositeSetLayout);
        var (image, buffer) = (Sampled(color), BufferInfo());
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth },
            new VkWriteDescriptorSet { DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
        ], []);
        return set;
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) =>
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 3, [_worldSet], []);

    /// The amount moves a layer's coverage either side of what's typical for its type.
    private Layer Describe(CloudLayerShape? shape)
    {
        if (shape is null)
            return default;

        var coverage = shape.Coverage + (Amount - 0.5f) * 0.6f;
        return new Layer
        {
            Heights = new float4(GroundLevel + shape.Base, GroundLevel + shape.Top, coverage, shape.Density),
            Tiles = new float4(shape.TileX, shape.TileZ, shape.ShapeTile, shape.DetailTile),
            Profile = new float4(shape.BottomFade, shape.TopStart, shape.TopReach, shape.Anvil),
            Look = new float4(shape.Cells, shape.Ramp, shape.Erosion, shape.Sharpness),
            Look2 = new float4(shape.Ambient, shape.Banks, shape.Shear, shape.SunGain),
            Relief = new float4(shape.BaseWobble, shape.TopWobble, 0f, 0f),
        };
    }

    /// Writes this frame's uniforms and maps the shade on the ground. Outside any render pass, before the world.
    public void Prepare(CommandBuffer cmd, RenderTarget target, Camera camera, float aspect, Sky sky)
    {
        Resize(target);

        var type = Type;
        var volumes = type.Lower is null ? 0 : type.Upper is null ? 1 : 2;
        _active = sky.Name == Sky.Procedural && Amount > 0.001f && (volumes > 0 || type.High != HighCloud.None);

        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var viewProjection = camera.ViewProjection(aspect);

        // Wind pushes the noise east and a little south, so the clouds move the same whichever way the camera goes.
        var windStepX = Wind * (double)Time.RealDelta;
        var windStepZ = windStepX * 0.35;
        _windX += windStepX;
        _windZ += windStepZ;
        static float Wrap(double value) => (float)(value - Math.Floor(value / NoisePeriod) * NoisePeriod);

        // The shade map's middle stays on its grid as the camera moves, or its texels crawl over the ground.
        var texel = ShadowMapHalfWidth * 2.0 / ShadowMapSize;
        var mapX = Math.Round(camera.X / texel) * texel;
        var mapZ = Math.Round(camera.Z / texel) * texel;

        // Thins the high sheet's patches as the amount falls.
        var thin = (0.5f - Amount) * 0.5f;

        var uniforms = new Uniforms
        {
            PreviousViewProjection = _previousViewProjection,
            Right = new float4(camera.Right * (tanY * aspect), 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, camera.Near),
            Sun = new float4(sky.SunDirection, 0f),
            SunColor = new float4(sky.SunColor, 0f),
            Zenith = new float4(sky.Zenith, 0f),
            Horizon = new float4(sky.Horizon, 0f),
            Offset = new float4(Wrap(camera.X - _windX), (float)camera.Y, Wrap(camera.Z - _windZ), (float)camera.Y),
            Frame = new float4(_frame % 1024, _active ? 1f : 0f, _historyValid ? 1f : 0f, ShadowMapHalfWidth),
            Ground = new float4(GroundLevel - (float)camera.Y, volumes, (float)(mapX - camera.X), (float)(mapZ - camera.Z)),
            Motion = new float4(
                (float)(camera.X - _previousCamera.X - windStepX),
                (float)(camera.Y - _previousCamera.Y),
                (float)(camera.Z - _previousCamera.Z - windStepZ), 0f),
            High = type.High == HighCloud.None
                ? new float4(float.MaxValue / 2f, 0f, NoCloud, 0f)
                : new float4(GroundLevel + type.HighHeight, type.HighOpacity * MathF.Min(Amount * 2f, 1.3f), (float)type.High, thin),
            Layer0 = Describe(type.Lower),
            Layer1 = Describe(type.Upper),
        };

        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var updated = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [updated], [], []);

        if (_active)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, _shadow);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _marchLayout, 0, [_shadowSet], []);
            cmd.Dispatch(ShadowMapSize / 8, ShadowMapSize / 8, 1);

            var written = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [written], [], []);
        }

        _previousViewProjection = viewProjection;
        _previousCamera = (camera.X, camera.Y, camera.Z);
    }

    /// Marches this frame's clouds and lays them over the target. Outside any render pass, after the world's ended.
    public void Draw(CommandBuffer cmd, RenderTarget target)
    {
        if (!_active)
        {
            _historyValid = false;
            _frame++;
            return;
        }

        _current = 1 - _current;

        using (Profiler.Sample("March", cmd))
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, _march);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _marchLayout, 0, [_marchSet], []);
            var across = _historyValid ? (_size.X + 1) / 2 : _size.X; // A checkerboard once there's a last frame to fill in from.
            cmd.Dispatch((across + 7) / 8, (_size.Y + 7) / 8, 1);
        }

        var marched = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [marched], [], []);

        using (Profiler.Sample("Settle", cmd))
        {
            cmd.BindPipeline(VkPipelineBindPoint.Compute, _temporal);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _temporalLayout, 0, [_temporalSets[_current]], []);
            cmd.Dispatch((_size.X + 7) / 8, (_size.Y + 7) / 8, 1);
        }

        var settled = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [settled], [], []);

        using (Profiler.Sample("Lay over", cmd))
        {
            target.BeginOverlay(cmd);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _composite);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _compositeLayout, 0, [_compositeSets[_current]], []);
            cmd.Draw(3, 1, 0, 0);
            target.EndOverlay(cmd);
        }

        _historyValid = true;
        _frame++;
    }

    public void Dispose()
    {
        _noise.Dispose();
        _shadowMap.Dispose();
        _raw?.Dispose();
        _rawDistance?.Dispose();
        _colorA?.Dispose();
        _colorB?.Dispose();
        var (pool, worldPool, uniforms) = (_pool, _worldPool, _uniforms);
        var (marchSetLayout, temporalSetLayout, compositeSetLayout) = (_marchSetLayout, _temporalSetLayout, _compositeSetLayout);
        var (marchLayout, temporalLayout, compositeLayout) = (_marchLayout, _temporalLayout, _compositeLayout);
        Pipelines.Destroy(_march, default);
        Pipelines.Destroy(_temporal, default);
        Pipelines.Destroy(_shadow, default);
        Pipelines.Destroy(_composite, default);
        Gpu.Defer(() =>
        {
            uniforms.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorPool(worldPool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(marchLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(temporalLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(compositeLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(marchSetLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(temporalSetLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(compositeSetLayout, Gpu.Device.VkAllocator);
        });
    }
}
