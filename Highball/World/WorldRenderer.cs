using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Draws the world: the sky, then the terrain, roads and rails, into an HDR target (RenderWorld, before the present pass), then maps
/// that onto the screen with a filmic curve (Composite, inside the present pass, under the interface).
public sealed unsafe class WorldRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainPush
    {
        public float4x4 ViewProjection;
        public float4 Offset;
        public float4 Camera;
        public float4 Sun;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyPush
    {
        public float4 Right;
        public float4 Up;
        public float4 Forward;
        public float4 Sun;
        public float4 Camera;
    }

    /// Exactly the 128 bytes every GPU guarantees for push constants.
    [StructLayout(LayoutKind.Sequential)]
    private struct ModelPush
    {
        public float4x4 ViewProjection;
        public float4 Rotation;
        public float4 PlaceAndScale;
        public float4 BaseColor;
        public float4 Material;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly RenderTarget _target = new();
    private readonly VkPipelineLayout _layout;
    private readonly VkPipelineLayout _terrainLayout;
    private readonly VkPipelineLayout _modelLayout;
    private readonly GroundTextures _ground = new();
    private readonly SunShadows _shadows = new();
    private readonly Clouds _clouds = new();
    private readonly AmbientOcclusion _occlusion = new();
    private readonly TerrainHorizon _horizon = new();
    private readonly Vegetation _vegetation = new();
    private VkPipeline _sky;
    private VkPipeline _terrain;
    private VkPipeline _terrainWire;
    private VkPipeline _line;
    private VkPipeline _lineWire;
    private VkPipeline _part;
    private VkPipeline _partWire;
    private VkPipeline _model;
    private VkPipeline _plant;
    private readonly VkDescriptorSetLayout _tonemapSetLayout;
    private readonly VkPipelineLayout _tonemapLayout;
    private readonly VkPipeline _tonemap;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _tonemapSet;
    private int _targetVersion = -1;
    private VkSampleCountFlags _pipelineSamples;

    /// Unit vector towards the sun.
    public float3 Sun => Sky.SunDirection;

    /// The sky and the light it gives; switch it with Sky.Use.
    public Sky Sky { get; } = new(RailWerkz.Core.Settings.Settings.Current.Skybox);
    public float Exposure { get; set; } = 1f;
    public bool Wireframe { get; set; }
    public bool Shadows { get => _shadows.Enabled; set => _shadows.Enabled = value; }
    public Clouds Clouds => _clouds;
    public Vegetation Vegetation => _vegetation;

    public WorldRenderer()
    {
        // One layout for every world pipeline: push constants, and a texture at set 0 for those that sample one.
        _layout = Pipelines.Layout(128, TextureBinding.SetLayout, Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        _terrainLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(GroundTextures.Count), Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        _modelLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(3), Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        CreateWorldPipelines();

        using var fullscreen = Shader.Load("fullscreen.vert");
        using var tonemap = Shader.Load("tonemap.frag");
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };
        var setLayout = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
        _tonemapSetLayout = Gpu.Device.CreateDescriptorSetLayout(in setLayout, Gpu.Device.VkAllocator);
        _tonemapLayout = Pipelines.Layout(16, _tonemapSetLayout);
        _tonemap = Pipelines.Create(new PipelineDesc
        {
            Vertex = fullscreen, Fragment = tonemap, Layout = _tonemapLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            RenderPass = Gpu.PresentPass, ColorFormat = Gpu.SwapchainFormat,
        });
    }

    /// The pipelines that draw into the target, which are made for its samples per pixel.
    private void CreateWorldPipelines()
    {
        _pipelineSamples = RenderTarget.Samples;
        using var fullscreen = Shader.Load("fullscreen.vert");
        using var sky = Shader.Load("sky.frag");
        using var terrainVert = Shader.Load("terrain.vert");
        using var terrainFrag = Shader.Load("terrain.frag");
        using var lineVert = Shader.Load("line.vert");
        using var lineFrag = Shader.Load("line.frag");
        using var partVert = Shader.Load("part.vert");
        using var partFrag = Shader.Load("part.frag");
        using var modelVert = Shader.Load("model.vert");
        using var modelFrag = Shader.Load("model.frag");
        using var plantVert = Shader.Load("plant.vert");
        using var plantFrag = Shader.Load("plant.frag");

        _sky = Pipelines.Create(new PipelineDesc { Vertex = fullscreen, Fragment = sky, Layout = _layout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false });
        var terrain = new PipelineDesc { Vertex = terrainVert, Fragment = terrainFrag, Layout = _terrainLayout, Attributes = TerrainVertex.Layout, Stride = Marshal.SizeOf<TerrainVertex>() };
        _terrain = Pipelines.Create(terrain);
        _terrainWire = Pipelines.Create(new PipelineDesc
        {
            Vertex = terrainVert, Fragment = terrainFrag, Layout = _terrainLayout, Attributes = TerrainVertex.Layout, Stride = terrain.Stride, Wireframe = true,
        });

        // Lines are open ribbons and parapets seen from either side, so nothing's culled.
        var stride = Marshal.SizeOf<LineVertex>();
        _line = Pipelines.Create(new PipelineDesc { Vertex = lineVert, Fragment = lineFrag, Layout = _layout, Attributes = LineVertex.Layout, Stride = stride, Cull = VkCullModeFlags.None });
        _lineWire = Pipelines.Create(new PipelineDesc
        {
            Vertex = lineVert, Fragment = lineFrag, Layout = _layout, Attributes = LineVertex.Layout, Stride = stride, Cull = VkCullModeFlags.None, Wireframe = true,
        });
        // Models placed as instances (ties, tie plates). Mirrored instances flip their winding, so nothing's culled.
        var part = new PipelineDesc
        {
            Vertex = partVert, Fragment = partFrag, Layout = _layout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = TieInstance.Layout, InstanceStride = Marshal.SizeOf<TieInstance>(), Cull = VkCullModeFlags.None,
        };
        _part = Pipelines.Create(part);
        _partWire = Pipelines.Create(new PipelineDesc
        {
            Vertex = partVert, Fragment = partFrag, Layout = _layout, Attributes = Mesh.VertexLayout, Stride = part.Stride,
            InstanceAttributes = TieInstance.Layout, InstanceStride = part.InstanceStride, Cull = VkCullModeFlags.None, Wireframe = true,
        });
        // Whole models under physically based light (lighting.glsl). Nothing's culled: the shader lights the side it sees.
        _model = Pipelines.Create(new PipelineDesc
        {
            Vertex = modelVert, Fragment = modelFrag, Layout = _modelLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None,
        });
        // Plants and rocks, many of one mesh. Cards are seen from both sides.
        _plant = Pipelines.Create(new PipelineDesc
        {
            Vertex = plantVert, Fragment = plantFrag, Layout = _modelLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = PlantInstance.Layout, InstanceStride = Marshal.SizeOf<PlantInstance>(), Cull = VkCullModeFlags.None, AlphaToCoverage = true,
        });
    }

    private void DestroyWorldPipelines()
    {
        Pipelines.Destroy(_sky, default);
        Pipelines.Destroy(_terrain, default);
        Pipelines.Destroy(_terrainWire, default);
        Pipelines.Destroy(_line, default);
        Pipelines.Destroy(_lineWire, default);
        Pipelines.Destroy(_part, default);
        Pipelines.Destroy(_partWire, default);
        Pipelines.Destroy(_model, default);
        Pipelines.Destroy(_plant, default);
    }

    private TerrainSource? _groundLightSource;
    private double _groundLightX;
    private double _groundLightZ;

    private static float3 Normalize(float3 v) => v / MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    /// Records the world into the HDR target. Outside any render pass.
    public void Render(CommandBuffer cmd, int2 size, Camera camera, Terrain? terrain, NetworkMeshes? network, IReadOnlyList<ModelDraw>? models = null)
    {
        if (_pipelineSamples != RenderTarget.Samples)
        {
            DestroyWorldPipelines();
            CreateWorldPipelines();
        }

        _target.Resize(size);
        var aspect = size.X / (float)size.Y;
        var camPos = new float4((float)camera.X, (float)camera.Y, (float)camera.Z, 0f);
        var sun = new float4(Sun, 0f);

        if (terrain is not null)
        {
            UpdateGroundLight(terrain.Source, camera);
            _horizon.Update(terrain.Source, terrain.Epoch, camera.X, camera.Z);
            _shadows.UseHorizon(_horizon);
            _vegetation.Enabled = RailWerkz.Core.Settings.Settings.Current.Vegetation;
            _vegetation.Update(terrain, camera);
        }

        using (Profiler.Sample("Sun shadows"))
            _shadows.Render(cmd, camera, aspect, Sun, terrain, models, terrain is null ? null : _vegetation, network);
        if (terrain is not null)
            _clouds.GroundLevel = terrain.Source.HeightAt(0.0, 0.0);
        using (Profiler.Sample("Cloud shade"))
            _clouds.Prepare(cmd, _target, camera, aspect, Sky);

        _target.Begin(cmd, new float4(0f, 0f, 0f, 1f));

        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var sky = new SkyPush
        {
            Right = new float4(camera.Right * (tanY * aspect), 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, 0f),
            Sun = sun,
            Camera = camPos,
        };
        var skyScope = Profiler.Sample("Sky");
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _sky);
        Sky.Bind(cmd, _layout);
        cmd.PushConstants(_layout, Stages, ByteSize.Zero, sky);
        cmd.Draw(3, 1, 0, 0);

        skyScope.Dispose();

        if (terrain is not null)
        {
            using var _ = Profiler.Sample("Terrain");
            var push = new TerrainPush { ViewProjection = camera.ViewProjection(aspect), Camera = camPos, Sun = sun };
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _terrainWire : _terrain);
            Sky.Bind(cmd, _terrainLayout);
            _shadows.Bind(cmd, _terrainLayout);
            _clouds.Bind(cmd, _terrainLayout);
            _ground.Binding.Bind(cmd, _terrainLayout);
            terrain.Draw(cmd, camera.X, camera.Y, camera.Z, offset =>
            {
                push.Offset = new float4(offset, 0f);
                cmd.PushConstants(_terrainLayout, Stages, ByteSize.Zero, push);
            });
        }

        if (network is not null)
        {
            var push = new TerrainPush
            {
                ViewProjection = camera.ViewProjection(aspect),
                Camera = new float4(camPos.X, camPos.Y, camPos.Z, network.NearRadius),
                Sun = sun,
            };
            void Push(float3 offset)
            {
                push.Offset = new float4(offset, 0f);
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            }

            using (Profiler.Sample("Roads and rails"))
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _lineWire : _line);
                Sky.Bind(cmd, _layout);
                _shadows.Bind(cmd, _layout);
                _clouds.Bind(cmd, _layout);
                network.BindBallast(cmd, _layout);
                network.DrawLines(cmd, camera.X, camera.Y, camera.Z, Push);
            }

            using (Profiler.Sample("Ties and plates"))
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _partWire : _part);
                network.DrawParts(cmd, _layout, camera.X, camera.Y, camera.Z, Push);
            }
        }

        if (terrain is not null && _vegetation.Enabled)
        {
            using var _ = Profiler.Sample("Plants and rocks");
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _plant);
            Sky.Bind(cmd, _modelLayout);
            _shadows.Bind(cmd, _modelLayout);
            _clouds.Bind(cmd, _modelLayout);
            _vegetation.Draw(cmd, _modelLayout, camera, aspect);
        }

        if (models is not null)
        {
            using var _ = Profiler.Sample("Models");
            DrawModels(cmd, camera, aspect, models);
        }

        _target.End(cmd);

        if (RailWerkz.Core.Settings.Settings.Current.AmbientOcclusion)
        {
            using var _ = Profiler.Sample("Ambient occlusion");
            _occlusion.Draw(cmd, _target, camera, aspect, layout =>
            {
                Sky.Bind(cmd, layout);
                _shadows.Bind(cmd, layout);
                _clouds.Bind(cmd, layout);
            });
        }

        // Over everything: they march only as far as the depth just drawn, so they're right from under, in or above the layers.
        using (Profiler.Sample("Clouds"))
            _clouds.Draw(cmd, _target);
    }

    /// The land cover's colour round the camera, for what's reflected in metal and lights things from beneath. It changes slowly,
    /// so only every so often as the camera moves.
    private void UpdateGroundLight(TerrainSource source, Camera camera)
    {
        const double Step = 64.0;
        const double Across = 150.0;
        var dx = camera.X - _groundLightX;
        var dz = camera.Z - _groundLightZ;
        if (ReferenceEquals(source, _groundLightSource) && dx * dx + dz * dz < Step * Step)
            return;

        var sum = default(float3);
        var samples = 0;
        for (var z = -1; z <= 1; z++)
        {
            for (var x = -1; x <= 1; x++)
            {
                var gx = (int)Math.Round((camera.X + x * Across) / source.Spacing);
                var gz = (int)Math.Round((camera.Z + z * Across) / source.Spacing);
                var cover = source.Ground(gx, gz, 0f).Cover;
                if (cover.X + cover.Y + cover.Z <= 0f)
                    continue;

                sum += new float3(cover.X, cover.Y, cover.Z);
                samples++;
            }
        }

        // Nothing's known here yet: ask again next frame.
        if (samples == 0)
            return;

        (_groundLightSource, _groundLightX, _groundLightZ) = (source, camera.X, camera.Z);
        Sky.SetGroundColor(sum / samples);
    }

    private void DrawModels(CommandBuffer cmd, Camera camera, float aspect, IReadOnlyList<ModelDraw> models)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _model);
        Sky.Bind(cmd, _modelLayout);
        _shadows.Bind(cmd, _modelLayout);
        _clouds.Bind(cmd, _modelLayout);
        var viewProjection = camera.ViewProjection(aspect);

        foreach (var draw in models)
        {
            var (sin, cos) = MathF.SinCos(draw.Yaw * 0.5f);
            var relative = new float3((float)(draw.Position.X - camera.X), (float)(draw.Position.Y - camera.Y), (float)(draw.Position.Z - camera.Z));
            foreach (var part in draw.Model.Parts)
            {
                var push = new ModelPush
                {
                    ViewProjection = viewProjection,
                    Rotation = new float4(0f, sin, 0f, cos),
                    PlaceAndScale = new float4(relative, draw.Scale),
                    BaseColor = draw.BaseColor ?? part.BaseColor,
                    Material = new float4(draw.Metallic ?? part.Metallic, draw.Roughness ?? part.Roughness, (float)camera.Y, part.AlphaCutoff),
                };
                part.Textures.Bind(cmd, _modelLayout);
                cmd.PushConstants(_modelLayout, Stages, ByteSize.Zero, push);
                draw.Model.Mesh.DrawRange(cmd, part.FirstIndex, part.IndexCount);
            }
        }
    }

    /// Draws the tonemapped world over the screen. Inside the present pass, before the interface.
    public void Composite(CommandBuffer cmd)
    {
        if (_target.Version != _targetVersion)
            RefreshSet();

        var size = Gpu.Extent;
        cmd.SetViewport(0, [new VkViewport { X = 0f, Y = 0f, Width = size.X, Height = size.Y, MinDepth = 0f, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D(size.X, size.Y)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _tonemap);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _tonemapLayout, 0, [_tonemapSet], []);
        cmd.PushConstants(_tonemapLayout, Stages, ByteSize.Zero, new float4(Exposure, 0f, 0f, 0f));
        cmd.Draw(3, 1, 0, 0);
    }

    /// The target was remade: a new set pointing at it, from a pool of its own, since the old one may still be in use
    /// by a frame in flight.
    private void RefreshSet()
    {
        FreeSet();
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var layout = _tonemapSetLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _tonemapSet = sets[0];

        var image = new VkDescriptorImageInfo { Sampler = _target.Sampler, ImageView = _target.ColorView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var write = new VkWriteDescriptorSet
        {
            DstSet = _tonemapSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &image,
        };
        Gpu.Device.UpdateDescriptorSets([write], []);
        _targetVersion = _target.Version;
    }

    private void FreeSet()
    {
        if (_pool.IsNull())
            return;

        var pool = _pool;
        _pool = default;
        _tonemapSet = default;
        Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator));
    }

    public void Dispose()
    {
        FreeSet();
        _target.Dispose();
        DestroyWorldPipelines();
        Pipelines.Destroy(default, _terrainLayout);
        Pipelines.Destroy(default, _modelLayout);
        _ground.Dispose();
        _shadows.Dispose();
        _clouds.Dispose();
        _occlusion.Dispose();
        _horizon.Dispose();
        _vegetation.Dispose();
        Pipelines.Destroy(default, _layout);
        Sky.Dispose();
        Pipelines.Destroy(_tonemap, _tonemapLayout);
        var setLayout = _tonemapSetLayout;
        Gpu.Defer(() => Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator));
    }
}
