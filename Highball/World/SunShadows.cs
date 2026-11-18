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
    private static readonly string[] CascadeNames = ["Cascade 0", "Cascade 1", "Cascade 2", "Cascade 3"];

    /// How far each cascade reaches along the view, in metres. Past the last there are no shadows.
    private static readonly float[] Reach = [15f, 60f, 250f, 1500f];

    /// The track's own cascade, a fifth layer after the four: only the thin parts of the track (rails, ties, plates, switch
    /// blades), out to TrackReach, so their shadows on the ballast carry on past the second cascade without the others drawing
    /// them coarser or blurring everything else. The ballast and ground aren't in it, so nothing reading it shadows itself.
    public const float TrackReach = 120f;
    private const int TrackLayer = Cascades;

    /// How far towards the sun casters are looked for, so a far ridge can still shade the camera's valley.
    private const float TowardsSun = 5000f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4x4 Cascade0;
        public float4x4 Cascade1;
        public float4x4 Cascade2;
        public float4x4 Cascade3;
        public float4x4 Track;
        public float4 Splits;
        public float4 Forward; // w: 1 when there are shadows to read
        public float4 Texels;  // each cascade's texel, in metres
        public float4 Horizon; // xy: the horizon map's middle relative to the camera; z: 1 / its width; w: 1 when there is one
        public float4 Lights;
        public float4 Weather;
        public float4 TrackShadow; // x: the track cascade's texel, in metres; y: how far it reaches (0: none)
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
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private static VkDescriptorSetLayout _setLayout;

    private ShadowMap _map = new(Quality.ShadowSize, Cascades + 1);
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

    /// The cascades from this one on are cached: drawn every other frame (taking turns), and in between read as they were
    /// drawn, through their matrix moved to where the camera's gone. Far off, nothing moves enough in a frame to show it,
    /// and they're half the casters. The near ones, where trains and their shadows are seen up close, are drawn every frame.
    private const int CachedFrom = 2;

    /// How far a cached cascade's centre may lag where it would be drawn now, as a share of its radius, before it's drawn
    /// again whatever the turn. Cached cascades are drawn this much wider, so the view they're read for stays inside them.
    private const float CacheDrift = 0.05f;

    /// What each cascade was last drawn with: its centre in the world, its radius, the sun, the shadow map it went into
    /// (and whether casters were drawn at all): a cached one is redrawn when any of these no longer holds.
    private readonly (double X, double Y, double Z)[] _drawnCentre = new (double, double, double)[Cascades];
    private readonly float[] _drawnRadius = new float[Cascades];
    private readonly float3[] _drawnSun = new float3[Cascades];
    private readonly ShadowMap?[] _drawnInto = new ShadowMap?[Cascades];
    private readonly bool[] _drawnActive = new bool[Cascades];
    private readonly float[] _drawnTexel = new float[Cascades];
    private long _frame;

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
        using var lineVert = Shader.Load("shadow_line.vert");
        VertexAttribute[] position = [new(0, 3, 0)];
        _terrain = Pipelines.Create(new PipelineDesc
        {
            Vertex = terrainVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<TerrainVertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });
        _model = Pipelines.Create(new PipelineDesc
        {
            Vertex = modelVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            InstanceAttributes = ModelInstance.Layout[..2], InstanceStride = Marshal.SizeOf<ModelInstance>(),
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });

        _line = Pipelines.Create(new PipelineDesc
        {
            Vertex = lineVert, Layout = _layout, Attributes = LineVertex.Layout, Stride = Marshal.SizeOf<LineVertex>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });
        using var partVert = Shader.Load("shadow_part.vert");
        _part = Pipelines.Create(new PipelineDesc
        {
            Vertex = partVert, Layout = _layout, Attributes = position, Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = TieInstance.Layout, InstanceStride = Marshal.SizeOf<TieInstance>(), Cull = VkCullModeFlags.None, DepthOnly = true,
            DepthBiasConstant = -60f, DepthBiasSlope = -1.75f,
        });

        _plantLayout = Pipelines.Layout(128, TextureBinding.SetLayoutFor(4));
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
    public void Render(CommandBuffer cmd, Camera camera, float aspect, float3 sun, Terrain? terrain, IReadOnlyList<ModelDraw>? models, ModelInstances instances, Vegetation? vegetation = null, NetworkMeshes? network = null)
    {
        _instances = instances;
        // The Quality setting's changed: the old map's freed once the frames using it are done.
        if (_map.Size != Quality.ShadowSize)
        {
            var old = _map;
            _map = new ShadowMap(Quality.ShadowSize, Cascades + 1);
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

        _frame++;
        for (var c = 0; c < Cascades; c++)
        {
            var far = Reach[c];
            var (centre, radius) = Slice(camera, aspect, near, far);
            // What this cascade is read for: its own slice, and the last tenth of the one before, where the two blend.
            var seenFrom = c == 0 ? 0f : near * 0.9f;
            near = far;
            var cached = c >= CachedFrom;
            if (cached)
                radius *= 1f + CacheDrift;
            var texel = radius * 2f / _map.Size;

            // In whole texels of the world, not of the camera, so the map's grid stays put as the camera moves.
            centre += right * Snap(centre, right, camera, texel) + up * Snap(centre, up, camera, texel);

            if (cached && !Redraw(c, camera, centre, radius, sun, active))
            {
                // As it was drawn: the same place in the world, relative to where the camera is now.
                var (x, y, z) = _drawnCentre[c];
                var then = new float3((float)(x - camera.X), (float)(y - camera.Y), (float)(z - camera.Z));
                _matrices[c] = Matrix(then, right, up, sun, _drawnRadius[c]);
                texels[c] = _drawnTexel[c];
                continue;
            }

            texels[c] = texel;
            _matrices[c] = Matrix(centre, right, up, sun, radius);
            _drawnCentre[c] = (camera.X + centre.X, camera.Y + centre.Y, camera.Z + centre.Z);
            _drawnRadius[c] = radius;
            _drawnSun[c] = sun;
            _drawnInto[c] = _map;
            _drawnActive[c] = active;
            _drawnTexel[c] = texel;

            using (Profiler.Sample(CascadeNames[c], cmd))
            {
                _map.BeginLayer(cmd, c);
                _receiverLow = LowestGround(terrain, camera, centre, radius);
                if (active)
                    DrawCasters(cmd, camera, aspect, c, centre, right, up, sun, radius, seenFrom, far, terrain, models, vegetation, network, _matrices[c]);
                _map.EndLayer(cmd);
            }
        }

        // The track's cascade: the view out to TrackReach, with the thin parts of the track alone.
        {
            var (centre, radius) = Slice(camera, aspect, camera.Near, TrackReach);
            var texel = radius * 2f / _map.Size;
            centre += right * Snap(centre, right, camera, texel) + up * Snap(centre, up, camera, texel);
            uniforms.Track = Matrix(centre, right, up, sun, radius);
            uniforms.TrackShadow = new float4(texel, active && network is not null ? TrackReach : 0f, 0f, 0f);
            using (Profiler.Sample("Track cascade", cmd))
            {
                _map.BeginLayer(cmd, TrackLayer);
                _receiverLow = LowestGround(terrain, camera, centre, radius);
                if (active && network is not null)
                    DrawCasters(cmd, camera, aspect, TrackLayer, centre, right, up, sun, radius, 0f, TrackReach, null, null, null, network, uniforms.Track);
                _map.EndLayer(cmd);
            }
        }

        _map.EndFrame(cmd);

        (uniforms.Cascade0, uniforms.Cascade1, uniforms.Cascade2, uniforms.Cascade3) = (_matrices[0], _matrices[1], _matrices[2], _matrices[3]);
        uniforms.Texels = new float4(texels[0], texels[1], texels[2], texels[3]);

        // Inline in the command stream, so a frame still in flight keeps the values it was recorded with.
        cmd.UpdateBuffer(_uniforms.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)));
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.UniformReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [barrier], [], []);
    }

    /// The lowest ground under a cascade's sphere (relative to the camera's height), less ReceiverMargin: nothing it shades lies
    /// lower. Measured at every sample of the ground's grid under it (with the hand sculpting and the track's detail), where that's
    /// at most ReceiverSamples across; the ground's bilinear between its samples, so its lowest is at one of them. Wider, from the
    /// tiles' own ranges, which can only be lower. Without terrain, negative infinity (casters reach as far as ShadowSlack says).
    private static float LowestGround(Terrain? terrain, Camera camera, float3 centre, float radius)
    {
        if (terrain is null)
            return float.NegativeInfinity;
        var source = terrain.Source;
        var spacing = source.Spacing;
        var (x, z) = (camera.X + centre.X, camera.Z + centre.Z);
        var across = (int)Math.Ceiling(2.0 * radius / spacing) + 2;
        if (across <= ReceiverSamples)
        {
            var (sx, sz) = ((int)Math.Floor((x - radius) / spacing), (int)Math.Floor((z - radius) / spacing));
            var lowest = float.MaxValue;
            for (var j = 0; j < across; j++)
            {
                for (var i = 0; i < across; i++)
                {
                    var (px, pz) = ((sx + i) * (double)spacing, (sz + j) * (double)spacing);
                    var h = source.HeightAt(px, pz);
                    var fine = source.FineHeightAt(px, pz);
                    lowest = MathF.Min(lowest, float.IsNaN(fine) ? h : MathF.Min(h, fine));
                }
            }

            return (float)(lowest - camera.Y) - ReceiverMargin;
        }

        var gx0 = (int)Math.Floor((x - radius) / spacing);
        var gz0 = (int)Math.Floor((z - radius) / spacing);
        var gx1 = (int)Math.Ceiling((x + radius) / spacing) + 1;
        var gz1 = (int)Math.Ceiling((z + radius) / spacing) + 1;
        var (min, max) = source.HeightRange(gx0, gz0, gx1, gz1);
        if (min >= max && min == 0f)
            return float.NegativeInfinity; // no tiles there: no knowing
        return (float)(min - camera.Y) - ReceiverMargin;
    }

    private float _receiverLow = float.NegativeInfinity;

    /// Whether a cached cascade has to be drawn this frame: on its turn, or whenever what it holds no longer fits (the sun's
    /// moved, the view's left the room it was drawn with, the map's new, shadows came or went).
    private bool Redraw(int c, Camera camera, float3 centre, float radius, float3 sun, bool active)
    {
        if (_drawnInto[c] != _map || _drawnActive[c] != active || MathEx.Dot(_drawnSun[c], sun) < 0.99999f)
            return true;
        var (x, y, z) = _drawnCentre[c];
        var (dx, dy, dz) = (camera.X + centre.X - x, camera.Y + centre.Y - y, camera.Z + centre.Z - z);
        var drift = Math.Sqrt(dx * dx + dy * dy + dz * dz) + Math.Abs(radius - _drawnRadius[c]);
        if (drift > _drawnRadius[c] * CacheDrift)
            return true;
        // Taking turns, so no frame pays for both.
        return (_frame + c) % 2 == 0;
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

    /// How far below a caster's foot its shadow is followed (ground lower than it: a cutting, a bank), and the tallest anything
    /// but the ground stands (a yard mast): a caster's sphere may be as wide as a patch of trees, but its shadow only reaches as
    /// far as its height throws it. (The terrain's own shadows aren't culled this way: a ridge can shade a valley kilometres off.)
    private const float ShadowSlack = 20f;

    /// Whether something (its bounding sphere relative to the camera, and its top) could shade any part of the view a
    /// cascade's read for. The top is how high its shadow starts: it's thrown as far as from there down to the lowest ground
    /// the cascade covers, at the sun's angle (CasterReach).
    public delegate bool CasterTest(float3 centre, float size, float top);

    /// How far under the lowest ground sampled anything a cascade shades might still lie: between samples, the detail the track's
    /// cells carve (a ditch) and what's drawn just over the ground's own surface.
    private const float ReceiverMargin = 3f;

    /// The most samples across a cascade the lowest ground is measured from; wider ones take the tiles' own ranges.
    private const int ReceiverSamples = 33;

    /// The screen height (pixels) and slack a cascade's level of detail is judged by (DrawCasters' lodFloor).
    private const float ShadowLodReference = 1080f;
    private const float ShadowLodSlack = 2f;
    public const float TallestCaster = 30f;

    private ModelInstances? _instances;
    private readonly List<ModelInstances.Batch> _casters = [];

    private void DrawCasters(CommandBuffer cmd, Camera camera, float aspect, int cascade, float3 centre, float3 right, float3 up, float3 sun, float radius,
        float seenFrom, float seenTo, Terrain? terrain, IReadOnlyList<ModelDraw>? models, Vegetation? vegetation, NetworkMeshes? network, float4x4 light)
    {
        // Could something this size at this place shade anything in the cascade? Anywhere towards the sun can.
        bool InCascade(float3 relative, float size)
        {
            var d = relative - centre;
            return MathF.Abs(MathEx.Dot(d, right)) <= radius + size && MathF.Abs(MathEx.Dot(d, up)) <= radius + size && MathEx.Dot(d, sun) >= -radius - size;
        }

        // The part of the view this cascade's read for: the camera's frustum between seenFrom and seenTo along it, as planes
        // (normal, offset) that a point is outside of where Dot(normal, p) + offset > 0.
        var (forward, cr, cu) = (camera.Forward, camera.Right, camera.Up);
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        (float3 N, float D)[] planesArray =
        [
            (MathEx.Normalize(cr - forward * tanX), 0f),
            (MathEx.Normalize(-cr - forward * tanX), 0f),
            (MathEx.Normalize(cu - forward * tanY), 0f),
            (MathEx.Normalize(-cu - forward * tanY), 0f),
            (-forward, seenFrom),
            (forward, -seenTo),
        ];
        var lift = MathF.Max(sun.Y, 0.08f);
        var texel = radius * 2f / _map.Size;
        var edge = texel * 4f;

        // Models are drawn no finer than the cascade's texels can show: at the level of detail they'd have on screen where a
        // pixel is ShadowLodSlack texels wide (the filtering blurs a texel or two anyway). The nearest cascade keeps full detail.
        var lodFloor = texel * ShadowLodReference / (2f * MathF.Tan(camera.FieldOfView * 0.5f)) * ShadowLodSlack;

        // Can its shadow fall anywhere this cascade's read for? Its sphere, swept away from the sun as far as a shadow from
        // its height could reach, must touch that part of the view: everything behind or beside the camera whose shadow
        // falls away from the view is left out.
        // How far towards the ground away from the sun a shadow from this high can be thrown: down to the cascade's lowest ground
        // where that's known (a tie throws its shadow a metre, not a hundred), else as far as ShadowSlack allows.
        var low = _receiverLow;
        float CasterReach(float size, float top) => float.IsNegativeInfinity(low)
            ? (MathF.Min(2f * size, TallestCaster) + ShadowSlack) / lift
            : MathF.Max(top - low, 0f) / lift;

        bool Reaches(float3 relative, float size, float top)
        {
            if (!InCascade(relative, size))
                return false;
            var r = size + edge;
            var end = relative - sun * MathF.Min(CasterReach(size, top), TowardsSun);
            foreach (var (n, d) in planesArray)
                if (MathEx.Dot(n, relative) + d > r && MathEx.Dot(n, end) + d > r)
                    return false;
            return true;
        }

        var trackOnly = cascade == TrackLayer;

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

            // (The track's cascade draws as the nearest does, everything thin, but nothing that isn't.)
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _line);
            network.DrawLineCasters(cmd, camera.X, camera.Y, camera.Z, trackOnly ? 0 : cascade, Reaches, Place, thinOnly: trackOnly);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _part);
            network.DrawPartCasters(cmd, camera.X, camera.Y, camera.Z, trackOnly ? 0 : cascade, Reaches, Place, coarse: trackOnly);
        }

        if (models is not null && _instances is not null)
        {
            _casters.Clear();
            _instances.Add(models, camera, (draw, centre, size) => draw.CastsShadow && (size <= 0f || Reaches(centre, size, centre.Y + size)), _casters, lodFloor);
            if (_casters.Count > 0)
            {
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, _model);
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, new ModelPush { Light = light });
                foreach (var batch in _casters)
                    batch.Model.DrawShape(cmd, batch.Buffer, batch.Count, batch.First);
            }
        }

        if (terrain is not null)
        {
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _terrain);
            terrain.DrawCasters(cmd, camera.X, camera.Y, camera.Z, InCascade, offset =>
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
