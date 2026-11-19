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
        /// The camera's x and z wrapped to TexturePeriod, worked out in doubles: what ground and track textures are laid from.
        public float4 TexOrigin;
    }

    /// How far texture coordinates run before they wrap. The camera's own position is a float in the shaders, which 100 km
    /// out can only move in steps of centimetres; textures laid from it would step with it. Laid from the camera's place
    /// within this period instead (exact to half a millimetre), they glide. It's a whole number of every ground and track
    /// texture's repeat (terrain.frag, line.frag: 4, 6, 9, 12, 7, 1.5, 1.2, 0.8, 3 and 2.4 m) and of terrain.frag's larger,
    /// turned layer of each (5 x Second x its repeat, from the 3-4-5 Turn), so the wrap leaves no seam in them. The broad
    /// patterns are laid from the full position, where centimetres don't show; the finer noise (over 0.2 a metre) wraps, and
    /// steps once every period, where a random pattern doesn't show it. Change a repeat or Second and this must change too.
    public const double TexturePeriod = 6804.0;

    private static float4 TexOriginOf(Camera camera)
    {
        static double Wrap(double v) => v - Math.Floor(v / TexturePeriod) * TexturePeriod;
        // w: the clock grade crossings' flashers keep time by (line.frag).
        return new float4((float)Wrap(camera.X), 0f, (float)Wrap(camera.Z), NetworkMeshes.FlashTime);
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
        public ulong Materials;
        public uint Base;
        public float CameraHeight;
    }

    /// One draw's material, as model.frag reads it: its textures' MaterialTable slots, its colour, and metallic, roughness, alpha
    /// cutoff (below 0: see-through) and whether it has a normal map.
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawMaterial
    {
        public uint Albedo, MetalRough, Normal, Occlusion;
        public float4 BaseColor;
        public float4 Factors;
    }

    private readonly FrameRing<DrawMaterial> _drawMaterials = new(VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "models.materials");
    private readonly FrameRing<VkDrawIndexedIndirectCommand> _modelCommands = new(VkBufferUsageFlags.IndirectBufferBit, "models.commands");
    private DrawMaterial[] _materialScratch = new DrawMaterial[64];
    private VkDrawIndexedIndirectCommand[] _commandScratch = new VkDrawIndexedIndirectCommand[64];

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly RenderTarget _target = new();
    private readonly VkPipelineLayout _layout;
    private readonly VkPipelineLayout _terrainLayout;
    private readonly VkPipelineLayout _modelLayout;
    private readonly VkPipelineLayout _bindlessLayout;
    private readonly GroundTextures _ground = new();
    private readonly LocalLights _lights = new();
    private readonly Weather _weather = new();

    /// How much of the lamps' light shows against the day now (LocalLights.AgainstDay).
    private float _againstDay = 1f;

    /// A lamp's light against what the sun and sky put on the ground, by the sun's height (the sine of its elevation): its own
    /// strength over the two together, so about 2% at noon, a third at sunset, and all of it once it's dark.
    public static float AgainstDay(float sunHeight)
    {
        var ground = 3f * MathF.Max(sunHeight, 0f) + 0.3f * RailWerkz.Core.Utils.MathEx.SmoothStep(-0.12f, 0.08f, sunHeight);
        return 0.05f / (ground + 0.05f);
    }
    private readonly SunShadows _shadows;
    private VkPipeline _glow;
    private readonly Precipitation _precipitation = new();
    private readonly Smoke _smoke = new();
    private readonly HeatHaze _haze = new();
    private readonly Clouds _clouds = new();
    private readonly AmbientOcclusion _occlusion = new();
    private readonly PostProcess _post = new();
    private readonly MotionVectors _motion = new();
    private readonly ModelInstances _instances = new();
    private readonly VehicleOutline _outline = new();
    private readonly List<ModelInstances.Batch> _seen = [];
    private readonly List<ModelInstances.Batch> _moving = [];
    private readonly TerrainHorizon _horizon = new();
    private readonly Vegetation _vegetation = new();
    private VkPipeline _sky;
    private VkPipeline _terrain;
    private VkPipeline _terrainWire;
    private VkPipeline _line;
    private VkPipeline _lineFade;
    private VkPipeline _lineDepth;
    private VkPipeline _lineWire;
    private VkPipeline _part;
    private VkPipeline _partWire;
    private VkPipeline _model;
    private VkPipeline _modelSeeThrough;
    private VkPipeline _plant;
    private VkPipeline _plantDepth;
    private VkSampleCountFlags _pipelineSamples;

    /// Unit vector towards the sun.
    public float3 Sun => Sky.SunDirection;

    /// The sky and the light it gives; switch it with Sky.Use.
    public Sky Sky { get; } = new(RailWerkz.Core.Settings.Settings.Current.Skybox);
    public float Exposure { get => _post.Exposure; set => _post.Exposure = value; }
    public bool MeasureExposure { get => _post.MeasureExposure; set => _post.MeasureExposure = value; }
    public bool Wireframe { get; set; }
    public bool Shadows { get => _shadows.Enabled; set => _shadows.Enabled = value; }
    public Clouds Clouds => _clouds;
    public Vegetation Vegetation => _vegetation;




    public WorldRenderer()
    {
        _shadows = new SunShadows(_lights);
        // One layout for every world pipeline: push constants, and a texture at set 0 for those that sample one.
        _layout = Pipelines.Layout(128, TextureBinding.SetLayout, Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout, TrackTextures.SetLayout);
        _terrainLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(GroundTextures.Count), Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        // (Colour, metal-roughness, normal and occlusion: RenderModel.Bind.)
        _modelLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(4), Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        // Models: every texture in one array (MaterialTable), the rest as the plants have it.
        _bindlessLayout = Pipelines.Layout(128, MaterialTable.SetLayout, Sky.SetLayout, SunShadows.SetLayout, Clouds.SetLayout);
        CreateWorldPipelines();
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
        using var plantDepthFrag = Shader.Load("plant_depth.frag");
        using var glowVert = Shader.Load("glow.vert");
        using var glowFrag = Shader.Load("glow.frag");

        // Drawn after what's solid, at the far plane against its depth: only where nothing else is, so it isn't worked out for
        // pixels the ground covers.
        _sky = Pipelines.Create(new PipelineDesc { Vertex = fullscreen, Fragment = sky, Layout = _layout, Cull = VkCullModeFlags.None, DepthTest = true, DepthWrite = false });
        var terrain = new PipelineDesc { Vertex = terrainVert, Fragment = terrainFrag, Layout = _terrainLayout, Attributes = TerrainVertex.Layout, Stride = Marshal.SizeOf<TerrainVertex>() };
        _terrain = Pipelines.Create(terrain);
        _terrainWire = Pipelines.Create(new PipelineDesc
        {
            Vertex = terrainVert, Fragment = terrainFrag, Layout = _terrainLayout, Attributes = TerrainVertex.Layout, Stride = terrain.Stride, Wireframe = true,
        });

        // Lines are open ribbons and parapets seen from either side, so nothing's culled.
        var stride = Marshal.SizeOf<LineVertex>();
        _line = Pipelines.Create(new PipelineDesc { Vertex = lineVert, Fragment = lineFrag, Layout = _layout, Attributes = LineVertex.Layout, Stride = stride, Cull = VkCullModeFlags.None });
        // Over the terrain with no depth of its own, or the ambient occlusion would take every speck for a crack.
        // The track's depth alone, before the terrain: the ground under ballast and the beds behind one another are then never shaded.
        _lineDepth = Pipelines.Create(new PipelineDesc { Vertex = lineVert, Layout = _layout, Attributes = LineVertex.Layout, Stride = stride, Cull = VkCullModeFlags.None, NoColor = true });
        _lineFade = Pipelines.Create(new PipelineDesc { Vertex = lineVert, Fragment = lineFrag, Layout = _layout, Attributes = LineVertex.Layout, Stride = stride, Cull = VkCullModeFlags.None, AlphaToCoverage = true, DepthWrite = false });
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
            Vertex = modelVert, Fragment = modelFrag, Layout = _bindlessLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None,
            InstanceAttributes = ModelInstance.Layout[..2], InstanceStride = Marshal.SizeOf<ModelInstance>(),
        });
        // Their see-through parts (glass), once everything solid and the sky are down: laid over what's behind, which they
        // don't hide from what's drawn after (no depth written).
        _modelSeeThrough = Pipelines.Create(new PipelineDesc
        {
            Vertex = modelVert, Fragment = modelFrag, Layout = _bindlessLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None,
            InstanceAttributes = ModelInstance.Layout[..2], InstanceStride = Marshal.SizeOf<ModelInstance>(), DepthWrite = false, Blend = true,
        });
        // Drawn over what's solid, and no part of it.
        _glow = Pipelines.Create(new PipelineDesc { Vertex = glowVert, Fragment = glowFrag, Layout = _layout, Cull = VkCullModeFlags.None, DepthWrite = false, Blend = true });
        // Plants and rocks, many of one mesh. Cards are seen from both sides.
        // Their depth first (cut out, nothing shaded), then shaded only where that depth is theirs: grass stacked many deep in
        // front of the camera is lit once a pixel, not once a blade.
        _plantDepth = Pipelines.Create(new PipelineDesc
        {
            Vertex = plantVert, Fragment = plantDepthFrag, Layout = _modelLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = PlantInstance.Layout, InstanceStride = Marshal.SizeOf<PlantInstance>(), Cull = VkCullModeFlags.None, AlphaToCoverage = true, NoColor = true,
        });
        _plant = Pipelines.Create(new PipelineDesc
        {
            Vertex = plantVert, Fragment = plantFrag, Layout = _modelLayout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = PlantInstance.Layout, InstanceStride = Marshal.SizeOf<PlantInstance>(), Cull = VkCullModeFlags.None, AlphaToCoverage = true,
            DepthWrite = false, DepthCompare = VkCompareOp.Equal,
        });
    }

    private void DestroyWorldPipelines()
    {
        Pipelines.Destroy(_sky, default);
        Pipelines.Destroy(_terrain, default);
        Pipelines.Destroy(_terrainWire, default);
        Pipelines.Destroy(_line, default);
        Pipelines.Destroy(_lineFade, default);
        Pipelines.Destroy(_lineDepth, default);
        Pipelines.Destroy(_lineWire, default);
        Pipelines.Destroy(_part, default);
        Pipelines.Destroy(_partWire, default);
        Pipelines.Destroy(_model, default);
        Pipelines.Destroy(_modelSeeThrough, default);
        Pipelines.Destroy(_plant, default);
        Pipelines.Destroy(_plantDepth, default);
        Pipelines.Destroy(_glow, default);
    }

    private TerrainSource? _groundLightSource;
    private double _groundLightX;
    private double _groundLightZ;

    private static float3 Normalize(float3 v) => v / MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    /// The size the world's drawn at for a picture this size: the Render resolution setting's share of it with temporal
    /// anti-aliasing on (which builds the full-size picture from it), all of it without.
    public static int2 WorldSize(int2 output)
    {
        var s = RailWerkz.Core.Settings.Settings.Current;
        var scale = s.TemporalAA ? Math.Clamp(s.RenderScale, 0.5f, 1f) : 1f;
        return new int2(Math.Max(1, (int)MathF.Round(output.X * scale)), Math.Max(1, (int)MathF.Round(output.Y * scale)));
    }

    /// Records the world into the HDR target, at WorldSize(output), and the finished picture at output. Outside any render pass.
    public void Render(CommandBuffer cmd, int2 output, FrameScene scene)
    {
        var size = WorldSize(output);
        var (camera, terrain, network, models, outlined) = (scene.Camera, scene.Terrain, scene.Network, scene.Models, scene.Outlines);
        if (_pipelineSamples != RenderTarget.Samples)
        {
            DestroyWorldPipelines();
            CreateWorldPipelines();
        }

        _target.Resize(size);
        _post.Jitter(camera, size);
        _instances.Begin();
        var aspect = size.X / (float)size.Y;
        var camPos = new float4((float)camera.X, (float)camera.Y, (float)camera.Z, 0f);
        var sun = new float4(Sun, 0f);

        if (terrain is not null)
        {
            UpdateGroundLight(terrain.Source, camera);
            _horizon.Update(terrain.Source, terrain.Epoch, camera.X, camera.Z);
            _shadows.UseHorizon(_horizon);
            _vegetation.Enabled = RailWerkz.Core.Settings.Settings.Current.Vegetation;
            _vegetation.Update(cmd, terrain, camera);
        }

        _weather.Update();
        Sky.SetExtraHaze(_weather.ExtraHaze);
        _shadows.Weather = new float4(_weather.Wetness, _weather.SnowCover, 0f, 0f);

        // Lamps come on towards dusk, a little before the sun's gone.
        var lampsOn = 1f - RailWerkz.Core.Utils.MathEx.SmoothStep(-0.02f, 0.1f, Sky.RealSunHeight);
        _againstDay = AgainstDay(Sky.RealSunHeight);
        _lights.AgainstDay = _againstDay;
        _lights.Begin();
        if (network is not null)
        {
            AddSignals(network);
            AddCrossingFlashers(network);
            AddLightPoles(network, camera, lampsOn);
        }
        foreach (var light in scene.Lights)
            _lights.Add(in light);
        using (Profiler.Sample("Local lights", cmd))
            _lights.Upload(cmd, camera, size, aspect);
        using (Profiler.Sample("Smoke upload", cmd))
            _smoke.Upload(cmd, camera, aspect, scene.Smoke, Sky);

        using (Profiler.Sample("Sun shadows", cmd))
            _shadows.Render(cmd, camera, aspect, Sun, terrain, models, _instances, terrain is null ? null : _vegetation, network);
        if (terrain is not null)
            _clouds.GroundLevel = terrain.Source.HeightAt(0.0, 0.0);
        using (Profiler.Sample("Cloud shade", cmd))
            _clouds.Prepare(cmd, _target, camera, aspect, Sky);

        var plants = terrain is not null && _vegetation.Enabled ? _vegetation.CullCamera(cmd, camera, aspect) : -1;
        _target.Begin(cmd, new float4(0f, 0f, 0f, 1f));

        var view = camera.View(aspect);
        if (network is not null && !Wireframe)
        {
            using var _ = Profiler.Sample("Track depth", cmd);
            var depthPush = new TerrainPush
            {
                ViewProjection = camera.JitteredViewProjection(aspect),
                Camera = new float4(camPos.X, camPos.Y, camPos.Z, network.NearRadius),
                Sun = sun,
                TexOrigin = TexOriginOf(camera),
            };
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _lineDepth);
            network.DrawLines(cmd, camera.X, camera.Y, camera.Z, view, offset =>
            {
                depthPush.Offset = new float4(offset, 0f);
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, depthPush);
            });
        }

        // The plants' depth first, so the ground and track under them are never shaded (their colour comes later, tested equal).
        if (terrain is not null && _vegetation.Enabled)
        {
            using var _ = Profiler.Sample("Plant depth", cmd);
            _vegetation.Draw(cmd, _modelLayout, camera, aspect, _plantDepth, _plant, plants, Vegetation.PlantPass.Depth);
        }

        // The ground, then the track's fades into it, then what stands on them. The fades are drawn well nearer the camera than
        // they are and write no depth, so they show over coarse ground; plants and models must come after, to cover them (the
        // plants' colour is drawn after them, with the equal depth test, over whatever of a fade landed on them).
        if (terrain is not null)
        {
            using var _ = Profiler.Sample("Terrain", cmd);
            var push = new TerrainPush { ViewProjection = camera.JitteredViewProjection(aspect), Camera = camPos, Sun = sun, TexOrigin = TexOriginOf(camera) };
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _terrainWire : _terrain);
            Sky.Bind(cmd, _terrainLayout);
            _shadows.Bind(cmd, _terrainLayout);
            _clouds.Bind(cmd, _terrainLayout);
            _ground.Binding.Bind(cmd, _terrainLayout);
            terrain.Draw(cmd, camera.X, camera.Y, camera.Z, view, offset =>
            {
                push.Offset = new float4(offset, 0f);
                cmd.PushConstants(_terrainLayout, Stages, ByteSize.Zero, push);
            });
        }

        if (network is not null)
        {
            var push = new TerrainPush
            {
                ViewProjection = camera.JitteredViewProjection(aspect),
                Camera = new float4(camPos.X, camPos.Y, camPos.Z, network.NearRadius),
                Sun = sun,
                TexOrigin = TexOriginOf(camera),
            };
            void Push(float3 offset)
            {
                push.Offset = new float4(offset, 0f);
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            }

            using (Profiler.Sample("Roads and rails", cmd))
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _lineWire : _lineFade);
                Sky.Bind(cmd, _layout);
                _shadows.Bind(cmd, _layout);
                _clouds.Bind(cmd, _layout);
                network.BindBallast(cmd, _layout);
                // Before the rest, so whatever of the track stands in front of a fade covers it.
                network.DrawFades(cmd, camera.X, camera.Y, camera.Z, view, Push);
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _lineWire : _line);
                network.DrawLines(cmd, camera.X, camera.Y, camera.Z, view, Push);
            }

            using (Profiler.Sample("Ties and plates", cmd))
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, Wireframe ? _partWire : _part);
                // (Camera.w: how far a tie model's drawn, part.vert's cut-off.)
                push.Camera = new float4(camPos.X, camPos.Y, camPos.Z, NetworkMeshes.TieReach);
                network.DrawParts(cmd, _layout, camera.X, camera.Y, camera.Z, view, Push);
                push.Camera = new float4(camPos.X, camPos.Y, camPos.Z, network.NearRadius);
            }
        }

        if (terrain is not null && _vegetation.Enabled)
        {
            using var _ = Profiler.Sample("Plants and rocks", cmd);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _plant);
            Sky.Bind(cmd, _modelLayout);
            _shadows.Bind(cmd, _modelLayout);
            _clouds.Bind(cmd, _modelLayout);
            _vegetation.Draw(cmd, _modelLayout, camera, aspect, _plantDepth, _plant, plants, Vegetation.PlantPass.Color);
        }

        _seen.Clear();
        _moving.Clear();
        if (models is not null)
        {
            using var _ = Profiler.Sample("Models", cmd);
            _instances.Add(models, camera, (_, centre, radius) => ModelInstances.InView(camera, aspect, centre, radius), _seen);
            _instances.Add(models, camera, (draw, centre, radius) => draw.Moving && ModelInstances.InView(camera, aspect, centre, radius), _moving);
            DrawModels(cmd, camera, aspect);
        }

        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var sky = new SkyPush
        {
            Right = new float4(camera.Right * (tanY * aspect), 0f),
            Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, 0f),
            Sun = sun,
            Camera = camPos,
        };
        var skyScope = Profiler.Sample("Sky", cmd);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _sky);
        Sky.Bind(cmd, _layout);
        cmd.PushConstants(_layout, Stages, ByteSize.Zero, sky);
        cmd.Draw(3, 1, 0, 0);

        skyScope.Dispose();

        using (Profiler.Sample("See-through", cmd))
            DrawModels(cmd, camera, aspect, seeThrough: true);

        using (Profiler.Sample("Glows", cmd))
            DrawGlows(cmd, camera, aspect);

        _target.End(cmd);

        // How what moves moved, for the temporal pass to follow it.
        using (Profiler.Sample("Motion", cmd))
            _motion.Record(cmd, _target, camera, aspect, _moving);
        (_outline.DrivenColor, _outline.PickedColor, _outline.Scale) = (scene.OutlineStyle.Driven, scene.OutlineStyle.Picked, scene.OutlineStyle.Scale);
        _outline.Record(cmd, output, camera, aspect, outlined);

        if (RailWerkz.Core.Settings.Settings.Current.AmbientOcclusion)
        {
            using var _ = Profiler.Sample("Ambient occlusion", cmd);
            _occlusion.Draw(cmd, _target, _motion.Image!, camera, aspect, _post.NoiseFrame, layout =>
            {
                Sky.Bind(cmd, layout);
                _shadows.Bind(cmd, layout);
                _clouds.Bind(cmd, layout);
            });
        }

        // Over everything: they march only as far as the depth just drawn, so they're right from under, in or above the layers.
        using (Profiler.Sample("Clouds", cmd))
            _clouds.Draw(cmd, _target);

        // The exhaust smoke marched against the finished depth, to be laid over the picture after anti-aliasing.
        using (Profiler.Sample("Smoke march", cmd))
            _smoke.March(cmd, _target, _clouds);

        using (Profiler.Sample("After the world", cmd))
        {
            var falling = Precipitation.Falling(_weather);
            var haze = scene.Haze.Count > 0;
            _post.Draw(cmd, _target, output, _motion.Image!, camera, aspect, Sky, !falling && !_smoke.Any && !haze ? null : over =>
            {
                // The haze bends what's behind it, so it goes first, from the picture as it was; the smoke over it, and rain
                // and snow fall in front of both as much as behind.
                if (haze && _post.Picture is { } picture)
                    using (Profiler.Sample("Heat haze", over))
                        _haze.Draw(over, _target, picture, _post.PictureVersion, _clouds, camera, aspect, scene.Haze);
                using (Profiler.Sample("Smoke", over))
                    _smoke.Draw(over);
                if (falling)
                    _precipitation.Draw(over, _target, camera, aspect, _weather, Sky);
            }, copyPicture: haze);
        }
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

    /// What every signal's showing. There are no signalling rules to set them yet, so they all stand at stop. The lit lens shines by
    /// itself (line.frag, the lit lens paints); what it throws is a light at the mouth of its hood (signal.glb's red lens), opened
    /// wide but only forwards, so none of it falls back on the head and the dark lenses above.
    private void AddSignals(NetworkMeshes network)
    {
        foreach (var (x, y, z, heading, lit) in network.Signals())
        {
            var angle = heading * Math.PI / 180.0;
            var (fx, fz) = (Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < NetworkMeshes.Lamps.Length; i++)
            {
                var (lamp, _, paint, height, _) = NetworkMeshes.Lamps[i];
                if (!lit.HasFlag(lamp))
                    continue;
                // As set in the Track Materials window (TrackMaterials.Lamps), found by the lens's paint.
                var index = Array.FindIndex(TrackMaterials.Lamps, l => "Lens" + l.Name == paint);
                var s = TrackMaterials.Current.LampOf(Math.Max(index, 0));
                var color = new float3(s.Color[0], s.Color[1], s.Color[2]);
                var outer = MathF.Cos(Math.Clamp(s.Beam, 1f, 179f) * MathF.PI / 180f);
                var inner = MathF.Cos(Math.Clamp(s.Beam, 1f, 179f) * 0.65f * MathF.PI / 180f);
                // Just in front of the lens's dome (Tools/Blender/lineside.py), inside its hood.
                _lights.Add(new LocalLight(x + fx * 0.15, y + height, z + fz * 0.15, s.Range, color * s.Intensity, new float3((float)fx, 0f, (float)fz), inner, outer,
                    s.GlowSize, color * s.GlowBrightness));
            }
        }
    }

    /// Every working grade crossing's flashers: a red light from each lamp as it flashes, as a signal's lit lens throws one.
    private void AddCrossingFlashers(NetworkMeshes network)
    {
        var index = Array.FindIndex(TrackMaterials.Lamps, l => l.Name == "Red");
        var s = TrackMaterials.Current.LampOf(Math.Max(index, 0));
        var color = new float3(s.Color[0], s.Color[1], s.Color[2]);
        var outer = MathF.Cos(Math.Clamp(s.Beam, 1f, 179f) * MathF.PI / 180f);
        var inner = MathF.Cos(Math.Clamp(s.Beam, 1f, 179f) * 0.65f * MathF.PI / 180f);
        foreach (var (x, y, z, face, on) in network.CrossingFlashers())
            if (on > 0.01f)
                _lights.Add(new LocalLight(x, y, z, s.Range, color * s.Intensity * on, face, inner, outer, s.GlowSize, color * s.GlowBrightness * on));
    }

    /// How far from the camera placed light poles are lit, in metres.
    private const double LightPoleReach = 1500.0;

    /// Every light pole placed with the Lineside tool, lit after dusk: a spot from each lamp head (the model's lens) straight down,
    /// as wide and far as that kind of pole throws its light, with a glow on the lens.
    private void AddLightPoles(NetworkMeshes network, Camera camera, float on)
    {
        if (on <= 0f)
            return;
        foreach (var (x, y, z, kind) in network.LightPoleLamps())
        {
            var (dx, dz) = (x - camera.X, z - camera.Z);
            if (dx * dx + dz * dz > LightPoleReach * LightPoleReach)
                continue;
            // Range, colour at a metre, beam (half angle, degrees), glow size. Sodium-ish warm white.
            var (range, power, beam, glow) = kind switch
            {
                PlacedKind.LightYardMast => (70f, 900f, 62f, 2.2f),
                PlacedKind.LightCobra => (32f, 220f, 58f, 1.2f),
                _ => (14f, 45f, 70f, 0.6f),
            };
            var tint = new float3(1f, 0.8f, 0.56f);
            var (outer, inner) = (MathF.Cos(beam * MathF.PI / 180f), MathF.Cos(beam * 0.6f * MathF.PI / 180f));
            _lights.Add(new LocalLight(x, y, z, range, tint * power * on, new float3(0f, -1f, 0f), inner, outer, glow, tint * 2f * on));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlowPush
    {
        public float4x4 ViewProjection;
        public float4 PlaceSize;
        public float4 Color;
        public float4 ScreenX;
        public float4 ScreenY;
    }

    private void DrawGlows(CommandBuffer cmd, Camera camera, float aspect)
    {
        var lights = _lights.Used;
        if (lights.Count == 0)
            return;

        var push = new GlowPush
        {
            ViewProjection = camera.JitteredViewProjection(aspect),
            ScreenX = new float4(camera.Right, 0f),
            ScreenY = new float4(camera.Up, 0f),
        };
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _glow);
        // (The lamp itself still shows by day, as a headlight does, if not as hard as at night.)
        var glowShows = MathF.Max(MathF.Sqrt(_againstDay), 0.35f);
        foreach (var light in lights)
        {
            if (light.Glow <= 0f)
                continue;

            // About three pixels at any distance, on a screen a thousand high.
            var pixel = MathF.Tan(camera.FieldOfView * 0.5f) * 2f / 1000f * 3f;
            push.PlaceSize = new float4((float)(light.X - camera.X), (float)(light.Y - camera.Y), (float)(light.Z - camera.Z), light.Glow);
            push.Color = new float4(light.GlowColor * glowShows, pixel);
            cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
            cmd.Draw(6, 1, 0, 0);
        }
    }

    /// The models in view, their solid parts or (seeThrough) their glass: every part of a model's batch in one indirect call,
    /// whatever its materials, each draw reading its own from a table (DrawMaterial) and its textures from the MaterialTable.
    private void DrawModels(CommandBuffer cmd, Camera camera, float aspect, bool seeThrough = false)
    {
        if (seeThrough && !_seen.Any(b => b.Model.HasSeeThrough))
            return;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, seeThrough ? _modelSeeThrough : _model);
        MaterialTable.Bind(cmd, _bindlessLayout);
        Sky.Bind(cmd, _bindlessLayout);
        _shadows.Bind(cmd, _bindlessLayout);
        _clouds.Bind(cmd, _bindlessLayout);
        var push = new ModelPush { ViewProjection = camera.JitteredViewProjection(aspect), CameraHeight = (float)camera.Y };

        foreach (var batch in _seen)
        {
            if (seeThrough && !batch.Model.HasSeeThrough)
                continue;

            var parts = batch.Model.Parts;
            var slots = batch.Model.Slots;
            if (_materialScratch.Length < parts.Count)
            {
                _materialScratch = new DrawMaterial[parts.Count * 2];
                _commandScratch = new VkDrawIndexedIndirectCommand[parts.Count * 2];
            }

            var n = 0;
            long triangles = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.SeeThrough != seeThrough || part.IndexCount == 0)
                    continue;
                var slot = slots[i];
                _materialScratch[n] = new DrawMaterial
                {
                    Albedo = (uint)slot.Albedo, MetalRough = (uint)slot.MetalRough, Normal = (uint)slot.Normal, Occlusion = (uint)slot.Occlusion,
                    BaseColor = batch.BaseColor ?? part.BaseColor,
                    Factors = new float4(batch.Metallic ?? part.Metallic, batch.Roughness ?? part.Roughness, part.SeeThrough ? -1f : part.AlphaCutoff, slot.HasNormal ? 1f : 0f),
                };
                _commandScratch[n] = new VkDrawIndexedIndirectCommand
                {
                    IndexCount = part.IndexCount, InstanceCount = batch.Count, FirstIndex = part.FirstIndex, VertexOffset = 0, FirstInstance = batch.First,
                };
                triangles += part.IndexCount / 3 * (long)batch.Count;
                n++;
            }

            if (n == 0)
                continue;
            var (materials, row) = _drawMaterials.Append(_materialScratch.AsSpan(0, n));
            var (commands, first) = _modelCommands.Append(_commandScratch.AsSpan(0, n));
            push.Materials = MaterialAddress(materials);
            const int stride = 20;
            if (Gpu.MultiDraw)
            {
                push.Base = (uint)row;
                cmd.PushConstants(_bindlessLayout, Stages, ByteSize.Zero, push);
                batch.Model.Mesh.DrawIndexedIndirect(cmd, batch.Buffer, commands, (long)first * stride, n, triangles);
                continue;
            }

            for (var i = 0; i < n; i++)
            {
                push.Base = (uint)(row + i);
                cmd.PushConstants(_bindlessLayout, Stages, ByteSize.Zero, push);
                batch.Model.Mesh.DrawIndexedIndirect(cmd, batch.Buffer, commands, (long)(first + i) * stride, 1, _commandScratch[i].IndexCount / 3 * (long)batch.Count);
            }
        }
    }

    private static ulong MaterialAddress(Brutal.VulkanApi.Abstractions.BufferEx buffer)
    {
        var address = buffer.GetDeviceAddress();
        return System.Runtime.CompilerServices.Unsafe.As<VkDeviceAddress, ulong>(ref address);
    }

    /// Draws the finished world over the screen. Inside the present pass, before the interface.
    public void Composite(CommandBuffer cmd)
    {
        _post.Composite(cmd);
        _outline.Composite(cmd, RailWerkz.Core.Time.Time.Now);
    }

    public void Dispose()
    {
        _target.Dispose();
        DestroyWorldPipelines();
        Pipelines.Destroy(default, _terrainLayout);
        Pipelines.Destroy(default, _modelLayout);
        Pipelines.Destroy(default, _bindlessLayout);
        _drawMaterials.Dispose();
        _modelCommands.Dispose();
        _ground.Dispose();
        _shadows.Dispose();
        _clouds.Dispose();
        _occlusion.Dispose();
        _precipitation.Dispose();
        _smoke.Dispose();
        _haze.Dispose();
        _lights.Dispose();
        _post.Dispose();
        _motion.Dispose();
        _instances.Dispose();
        _outline.Dispose();
        _horizon.Dispose();
        _vegetation.Dispose();
        Pipelines.Destroy(default, _layout);
        Sky.Dispose();
    }
}
