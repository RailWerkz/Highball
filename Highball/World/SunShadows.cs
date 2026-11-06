using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The sun's shadows: four cascades of depth drawn from the sun, each wider and coarser than the last, which shadows.glsl
/// reads through set 2. Everything is relative to the camera, like the rest of the world. Each cascade is a sphere round its
/// slice of the view, so its size doesn't change as the camera turns, and it moves in whole texels, so edges don't crawl.
public sealed unsafe class SunShadows : IDisposable
{
    public const int Cascades = 4;

    /// How far each cascade reaches along the view, in metres. Past the last there are no shadows.
    private static readonly float[] Reach = [15f, 60f, 250f, 1500f];

    /// How far towards the sun casters are looked for, so a far ridge can still shade the camera's valley.
    private const float TowardsSun = 5000f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4x4 Cascade0;
        public float4x4 Cascade1;
        public float4x4 Cascade2;
        public float4x4 Cascade3;
        public float4 Splits;
        public float4 Forward; // w: 1 when there are shadows to read
        public float4 Texels;  // each cascade's texel, in metres
        public float4 Horizon; // xy: the horizon map's middle relative to the camera; z: 1 / its width; w: 1 when there is one
        public float4 Lights;
        public float4 Weather;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainPush
    {
        public float4x4 Light;
        public float4 Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModelPush
    {
        public float4x4 Light;
        public float4 Rotation;
        public float4 PlaceAndScale;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private static VkDescriptorSetLayout _setLayout;

    private ShadowMap _map = new(Quality.ShadowSize, Cascades);
    private Texture2D? _horizon0;
    private Texture2D? _horizon1;
    private readonly BufferEx _uniforms;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private readonly Texture2D _noHorizon = Texture2D.Solid(new byte4(0, 0, 0, 0));
    private TerrainHorizon? _horizon;
    private int _horizonVersion;
    private readonly LocalLights _localLights;

    /// How wet the ground is and how much snow lies, for shaders: set before Render.
    public float4 Weather { get; set; }
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _terrain;
    private readonly VkPipeline _model;
    private readonly VkPipeline _line;
    private readonly VkPipeline _part;
    private readonly VkPipelineLayout _plantLayout;
    private readonly VkPipeline _plant;
    private readonly float4x4[] _matrices = new float4x4[Cascades];

    public bool Enabled { get; set; } = !DevTools.NoShadows;

    /// The layout of the shadows' set: their uniforms, the depth array, the horizon map's two images, and the lamps and their grid.
    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            if (!_setLayout.IsNull())
                return _setLayout;

            var bindings = stackalloc VkDescriptorSetLayoutBinding[6];
            bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            for (var i = 1; i < 4; i++)
                bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            for (var i = 4; i < 6; i++)
                bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 6, Bindings = bindings };
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

    public SunShadows(LocalLights lights)
    {
        _localLights = lights;
        _uniforms = Buffers.Create(sizeof(Uniforms), VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "shadows.uniforms");

        MakeSet(_noHorizon, _noHorizon);

        _layout = Pipelines.Layout(128);
        using var terrainVert = Shader.Load("shadow_terrain.vert");
        using var modelVert = Shader.Load("shadow_model.vert");
        VertexAttribute[] position = [new(0, 3, 0)];
        _terrain = Pipelines.Create(new PipelineDesc
        {
            Vertex = terrainVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<TerrainVertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });
        _model = Pipelines.Create(new PipelineDesc
        {
            Vertex = modelVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });

        _line = Pipelines.Create(new PipelineDesc
        {
            Vertex = terrainVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<LineVertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });
        using var partVert = Shader.Load("shadow_part.vert");
        _part = Pipelines.Create(new PipelineDesc
        {
            Vertex = partVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = TieInstance.Layout, InstanceStride = Marshal.SizeOf<TieInstance>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });

        _plantLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(3));
        using var plantVert = Shader.Load("shadow_plant.vert");
        using var plantFrag = Shader.Load("shadow_plant.frag");
        _plant = Pipelines.Create(new PipelineDesc
        {
            Vertex = plantVert, Fragment = plantFrag, Layout = _plantLayout, Attributes = [new(0, 3, 0), new(2, 2, 24)], Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = PlantInstance.Layout, InstanceStride = Marshal.SizeOf<PlantInstance>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });
    }

    /// A set of its own for each horizon map, since the last may still be in use by a frame in flight.
    private void MakeSet(Texture2D horizon0, Texture2D horizon1)
    {
        (_horizon0, _horizon1) = (horizon0, horizon1);
        var old = _pool;
        if (!old.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(old, Gpu.Device.VkAllocator));

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var layout = SetLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];

        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var image = new VkDescriptorImageInfo { Sampler = _map.Sampler, ImageView = _map.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var image0 = new VkDescriptorImageInfo { Sampler = horizon0.Sampler, ImageView = horizon0.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var image1 = new VkDescriptorImageInfo { Sampler = horizon1.Sampler, ImageView = horizon1.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var lampList = new VkDescriptorBufferInfo { Buffer = _localLights.LightBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(LocalLights.LightBufferSize) };
        var lampCells = new VkDescriptorBufferInfo { Buffer = _localLights.CellBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(LocalLights.CellBufferSize) };
        VkWriteDescriptorSet[] writes =
        [
            new() { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
            new() { DstSet = _set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image },
            new() { DstSet = _set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image0 },
            new() { DstSet = _set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image1 },
            new() { DstSet = _set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &lampList },
            new() { DstSet = _set, DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &lampCells },
        ];
        Gpu.Device.UpdateDescriptorSets(writes, []);
    }

    /// The map of the land's rise to read from now on, once it has one.
    public void UseHorizon(TerrainHorizon horizon)
    {
        _horizon = horizon;
        if (horizon.Version == _horizonVersion || horizon.Sectors0 is not { } a || horizon.Sectors1 is not { } b)
            return;

        _horizonVersion = horizon.Version;
        MakeSet(a, b);
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) =>
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 2, [_set], []);

    /// Draws the cascades and updates what shadows.glsl reads. Outside any render pass, before the world.
    public void Render(CommandBuffer cmd, Camera camera, float aspect, float3 sun, Terrain? terrain, IReadOnlyList<ModelDraw>? models, Vegetation? vegetation = null, NetworkMeshes? network = null, YardLights? yards = null)
    {
        // The Quality setting's changed: the old map's freed once the frames using it are done.
        if (_map.Size != Quality.ShadowSize)
        {
            var old = _map;
            _map = new ShadowMap(Quality.ShadowSize, Cascades);
            Gpu.Defer(old.Dispose);
            MakeSet(_horizon0!, _horizon1!);
        }

        // A sun on the horizon has no sensible shadows, and one under it gives no light to shade.
        var active = Enabled && sun.Y > 0.02f;
        var uniforms = new Uniforms
        {
            Splits = new float4(Reach[0], Reach[1], Reach[2], Reach[3]),
            Forward = new float4(camera.Forward, active ? 1f : 0f),
            Lights = _localLights.Measures,
            Weather = Weather,
            Horizon = _horizon is { Sectors0: not null } horizon && Enabled
                ? new float4((float)(horizon.CentreX - camera.X), (float)(horizon.CentreZ - camera.Z), 1f / TerrainHorizon.Width, 1f)
                : default,
        };

        _map.BeginFrame(cmd);
        var right = MathEx.Normalize(MathEx.Cross(MathF.Abs(sun.Y) > 0.99f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f), sun));
        var up = MathEx.Cross(sun, right);
        var near = camera.Near;
        var texels = new float[Cascades];

        for (var c = 0; c < Cascades; c++)
        {
            var far = Reach[c];
            var (centre, radius) = Slice(camera, aspect, near, far);
            near = far;
            var texel = radius * 2f / _map.Size;
            texels[c] = texel;

            // In whole texels of the world, not of the camera, so the map's grid stays put as the camera moves.
            centre += right * Snap(centre, right, camera, texel) + up * Snap(centre, up, camera, texel);
            _matrices[c] = Matrix(centre, right, up, sun, radius);

            _map.BeginLayer(cmd, c);
            if (active)
                DrawCasters(cmd, camera, c, centre, right, up, sun, radius, terrain, models, vegetation, network, yards);
            _map.EndLayer(cmd);
        }

        _map.EndFrame(cmd);

        (uniforms.Cascade0, uniforms.Cascade1, uniforms.Cascade2, uniforms.Cascade3) = (_matrices[0], _matrices[1], _matrices[2], _matrices[3]);
        uniforms.Texels = new float4(texels[0], texels[1], texels[2], texels[3]);

        // Inline in the command stream, so a frame still in flight keeps the values it was recorded with.
        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [barrier], [], []);
    }

    /// The sphere round the part of the view between two distances, relative to the camera.
    private static (float3 Centre, float Radius) Slice(Camera camera, float aspect, float near, float far)
    {
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        // k: the squared distance of a slice's corner from the view's axis, per metre along it, squared.
        var k = tanY * tanY * (1f + aspect * aspect);
        // The centre on the axis that's equally far from the near and far corners, kept within the slice.
        var along = Math.Min(far, (far + near) * 0.5f * (1f + k));
        var radius = MathF.Sqrt((far - along) * (far - along) + far * far * k);
        return (camera.Forward * along, radius);
    }

    /// How far to move along an axis to land on a whole texel of the world.
    private static float Snap(float3 relative, float3 axis, Camera camera, float texel)
    {
        var at = (double)MathEx.Dot(relative, axis) + camera.X * axis.X + camera.Y * axis.Y + camera.Z * axis.Z;
        return (float)(Math.Round(at / texel) * texel - at);
    }

    /// World metres (relative to the camera) to the cascade's clip space: x and y across it, depth 1 at the sun's end of
    /// the range and 0 at the other, with the row-vector convention of the rest of the game's matrices.
    private static float4x4 Matrix(float3 centre, float3 right, float3 up, float3 sun, float radius)
    {
        var depth = TowardsSun + radius;
        float[] m =
        [
            right.X / radius, up.X / radius, sun.X / depth, 0f,
            right.Y / radius, up.Y / radius, sun.Y / depth, 0f,
            right.Z / radius, up.Z / radius, sun.Z / depth, 0f,
            -MathEx.Dot(centre, right) / radius, -MathEx.Dot(centre, up) / radius, (radius - MathEx.Dot(centre, sun)) / depth, 1f,
        ];
        return new float4x4(m);
    }

    private void DrawCasters(CommandBuffer cmd, Camera camera, int cascade, float3 centre, float3 right, float3 up, float3 sun, float radius,
        Terrain? terrain, IReadOnlyList<ModelDraw>? models, Vegetation? vegetation, NetworkMeshes? network, YardLights? yards)
    {
        // Could something this size at this place shade anything in the cascade? Anywhere towards the sun can.
        bool Reaches(float3 relative, float size)
        {
            var d = relative - centre;
            return MathF.Abs(MathEx.Dot(d, right)) <= radius + size && MathF.Abs(MathEx.Dot(d, up)) <= radius + size && MathEx.Dot(d, sun) >= -radius - size;
        }

        var light = _matrices[cascade];

        if (vegetation is not null)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _plant);
            vegetation.DrawCasters(cmd, _plantLayout, camera, light, cascade, Reaches);
        }

        if (network is not null)
        {
            void Place(float3 offset)
            {
                var push = new TerrainPush { Light = light, Offset = new float4(offset, 0f) };
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            }

            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _line);
            network.DrawLineCasters(cmd, camera.X, camera.Y, camera.Z, cascade, Reaches, Place);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _part);
            network.DrawPartCasters(cmd, camera.X, camera.Y, camera.Z, cascade, Reaches, Place);
            if (cascade <= 2)
                yards?.DrawCasters(cmd, camera, Reaches, Place);
        }

        if (models is not null)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _model);
            foreach (var draw in models)
            {
                if (!draw.CastsShadow)
                    continue;

                var relative = new float3((float)(draw.Position.X - camera.X), (float)(draw.Position.Y - camera.Y), (float)(draw.Position.Z - camera.Z));
                var mesh = draw.Model.Mesh;
                var size = MathF.Max(MathEx.Length(mesh.BoundsMin), MathEx.Length(mesh.BoundsMax)) * draw.Scale;
                if (size > 0f && !Reaches(relative, size))
                    continue;

                var (sin, cos) = MathF.SinCos(draw.Yaw * 0.5f);
                var push = new ModelPush { Light = light, Rotation = new float4(0f, sin, 0f, cos), PlaceAndScale = new float4(relative, draw.Scale) };
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
                foreach (var part in draw.Model.Parts)
                    mesh.DrawRange(cmd, part.FirstIndex, part.IndexCount);
            }
        }

        if (terrain is not null)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _terrain);
            terrain.DrawCasters(cmd, camera.X, camera.Y, camera.Z, Reaches, offset =>
            {
                var push = new TerrainPush { Light = light, Offset = new float4(offset, 0f) };
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            });
        }
    }

    public void Dispose()
    {
        _map.Dispose();
        _noHorizon.Dispose();
        var (uniforms, pool, layout) = (_uniforms, _pool, _layout);
        Pipelines.Destroy(_plant, _plantLayout);
        Pipelines.Destroy(_terrain, default);
        Pipelines.Destroy(_model, default);
        Pipelines.Destroy(_line, default);
        Pipelines.Destroy(_part, default);
        Gpu.Defer(() =>
        {
            uniforms.Dispose();
            Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
        });
    }
}
