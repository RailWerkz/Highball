using System.Runtime.InteropServices;
using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The sky every world shader sees (atmosphere.glsl, set 1): either the analytic one, with the sun and moon drawn from
/// Content/Textures/Planets, or a skybox from Content/Textures/Skyboxes (a horizontal-cross cube map, 4 x 3 faces).
/// A skybox also sets the light: the sun comes from its brightest spot, as strong as the spot stands out, and the
/// ambient and haze colours are its zenith, horizon and ground averaged.
public sealed unsafe class Sky : IDisposable
{
    public const string Procedural = "Procedural";

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public float4 SunDirection;
        public float4 SunColor;
        public float4 Zenith;
        public float4 Horizon;
        public float4 Ground;
        public float4 Mode; // x: 1 for a skybox
    }

    private static VkDescriptorSetLayout _setLayout;

    private readonly Texture2D _sun;
    private readonly Texture2D _moon;
    private Texture2D? _map;
    private BufferEx _uniforms;
    private VkDescriptorPool _pool;

    public string Name { get; private set; } = "";
    public float3 SunDirection { get; private set; }
    public VkDescriptorSet Set { get; private set; }

    /// Procedural first, then every skybox, by file name.
    public static IReadOnlyList<string> Available { get; } =
    [
        Procedural,
        .. Directory.Exists(ContentPath.Texture("Skyboxes"))
            ? Directory.GetFiles(ContentPath.Texture("Skyboxes"), "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToArray()
            : Array.Empty<string>(),
    ];

    public static string Label(string name) => name.Replace('_', ' ').ToUpperInvariant();

    /// The layout of the sky's set: its uniforms, the skybox, the sun and the moon.
    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            if (!_setLayout.IsNull())
                return _setLayout;

            var bindings = stackalloc VkDescriptorSetLayoutBinding[4];
            bindings[0] = new VkDescriptorSetLayoutBinding
            {
                Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            };
            for (var i = 1; i < 4; i++)
            {
                bindings[i] = new VkDescriptorSetLayoutBinding
                {
                    Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit,
                };
            }

            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 4, Bindings = bindings };
            _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
            return _setLayout;
        }
    }

    public Sky(string name)
    {
        _sun = Texture2D.LoadOr("Planets/2k_sun.jpg", new byte4(255, 220, 160, 255));
        _moon = Texture2D.LoadOr("Planets/2k_moon.jpg", new byte4(150, 150, 150, 255));
        Use(name);
    }

    /// Switches to a sky by name (Procedural, or a skybox's file name); an unknown one falls back to Procedural.
    public void Use(string name)
    {
        var uniforms = new Uniforms
        {
            SunDirection = new float4(Normalize(new float3(0.45f, 0.55f, -0.35f)), 1f),
            SunColor = new float4(new float3(1.0f, 0.94f, 0.84f) * 3f, 0f),
            Zenith = new float4(0.09f, 0.22f, 0.52f, 0f),
            Horizon = new float4(0.50f, 0.64f, 0.82f, 0f),
            Ground = new float4(0.24f, 0.26f, 0.28f, 0f),
        };

        Texture2D? map = null;
        if (name != Procedural)
        {
            try
            {
                map = LoadSkybox(ContentPath.Texture($"Skyboxes/{name}.png"), ref uniforms);
            }
            catch (Exception e)
            {
                Log.Warn($"Skybox {name}: {e.Message}");
                name = Procedural;
            }
        }

        Release();
        _map = map ?? Texture2D.Solid(new byte4(128, 160, 200, 255));
        Name = name;
        Apply(in uniforms);
    }

    /// Puts the procedural sky's sun at this height over the horizon and this bearing (degrees, 0 north, 90 east), with
    /// the colours of that time of day: warm near the horizon, dim through dusk, a faint blue at night (there's no moonlight yet).
    public void SetSun(float elevation, float azimuth)
    {
        if (Name != Procedural)
            Use(Procedural);

        var (sinElevation, cosElevation) = MathF.SinCos(elevation * MathF.PI / 180f);
        var (sinAzimuth, cosAzimuth) = MathF.SinCos(azimuth * MathF.PI / 180f);
        var low = 1f - MathEx.SmoothStep(0f, 0.35f, sinElevation);
        var day = MathEx.SmoothStep(-0.12f, 0.08f, sinElevation);
        var risen = MathEx.SmoothStep(-0.02f, 0.05f, sinElevation);

        var sunColor = MathEx.Lerp(new float3(1.0f, 0.94f, 0.84f) * 3f, new float3(1.0f, 0.52f, 0.24f) * 2.4f, low) * risen;
        var zenith = MathEx.Lerp(new float3(0.09f, 0.22f, 0.52f), new float3(0.05f, 0.10f, 0.25f), low) * day + new float3(0.012f, 0.018f, 0.036f);
        var horizon = MathEx.Lerp(new float3(0.50f, 0.64f, 0.82f), new float3(0.85f, 0.50f, 0.30f), low) * day + new float3(0.016f, 0.022f, 0.040f);
        var ground = MathEx.Lerp(new float3(0.24f, 0.26f, 0.28f), new float3(0.16f, 0.12f, 0.10f), low) * day + new float3(0.006f, 0.008f, 0.012f);

        var uniforms = new Uniforms
        {
            // x east, y up, z south.
            SunDirection = new float4(sinAzimuth * cosElevation, sinElevation, -cosAzimuth * cosElevation, 1f),
            SunColor = new float4(sunColor, 0f),
            Zenith = new float4(zenith, 0f),
            Horizon = new float4(horizon, 0f),
            Ground = new float4(ground, 0f),
        };
        ReleaseUniforms();
        Apply(in uniforms);
    }

    /// A uniform buffer and a set for these values over the current map.
    private void Apply(in Uniforms uniforms)
    {
        SunDirection = uniforms.SunDirection.XYZ;

        _uniforms = Buffers.Upload(MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms)), VkBufferUsageFlags.UniformBufferBit, "sky.uniforms");

        var sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 2, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var layout = SetLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        Set = sets[0];

        var buffer = new VkDescriptorBufferInfo { Buffer = _uniforms.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(sizeof(Uniforms)) };
        var images = stackalloc VkDescriptorImageInfo[3];
        Texture2D[] textures = [_map!, _sun, _moon];
        for (var i = 0; i < 3; i++)
            images[i] = new VkDescriptorImageInfo { Sampler = textures[i].Sampler, ImageView = textures[i].View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };

        VkWriteDescriptorSet[] writes =
        [
            new() { DstSet = Set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &buffer },
            new() { DstSet = Set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images },
            new() { DstSet = Set, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images + 1 },
            new() { DstSet = Set, DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images + 2 },
        ];
        Gpu.Device.UpdateDescriptorSets(writes, []);
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) =>
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 1, [Set], []);

    // --- Skyboxes. ---

    /// Cross layout, in cells of a 4 x 3 grid: +Y over -X +Z +X -Z over -Y (the usual cube map faces, x east, y up,
    /// z south).
    private static readonly (int Col, int Row)[] Faces = [(2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1)]; // +X -X +Y -Y +Z -Z

    private static Texture2D LoadSkybox(string path, ref Uniforms uniforms)
    {
        using var image = Stb.Load(path, 4);
        var width = image.Width;
        var height = image.Height;
        var face = width / 4;
        if (face * 4 != width || face * 3 != height)
            throw new InvalidDataException($"{width} x {height} isn't a 4 x 3 cross");

        var pixels = image.AsSpan().ToArray();
        static float Linear(byte c) => MathF.Pow(c / 255f, 2.2f);

        // Average each face, and the sides' middle band (the horizon).
        float3 Average(int col, int row, int y0, int y1)
        {
            var sum = default(float3);
            var count = 0;
            for (var y = y0; y < y1; y += 4)
            {
                for (var x = 0; x < face; x += 4)
                {
                    var i = ((row * face + y) * width + col * face + x) * 4;
                    sum += new float3(Linear(pixels[i]), Linear(pixels[i + 1]), Linear(pixels[i + 2]));
                    count++;
                }
            }

            return sum / Math.Max(count, 1);
        }

        var zenith = Average(1, 0, 0, face);
        var horizon = (Average(0, 1, face * 2 / 5, face * 3 / 5) + Average(1, 1, face * 2 / 5, face * 3 / 5)
            + Average(2, 1, face * 2 / 5, face * 3 / 5) + Average(3, 1, face * 2 / 5, face * 3 / 5)) / 4f;
        var ground = Average(1, 2, 0, face);

        // The sun: the brightest 16 x 16 block over the horizon, and how far it stands out from the sky round it.
        const int block = 16;
        var bestLum = 0f;
        var bestColor = default(float3);
        var bestDir = new float3(0f, 1f, 0f);
        var lumSum = 0f;
        var lumCount = 0;
        for (var f = 0; f < 6; f++)
        {
            if (f == 3)
                continue; // -Y: below the horizon
            var (col, row) = Faces[f];
            for (var by = 0; by < face; by += block)
            {
                for (var bx = 0; bx < face; bx += block)
                {
                    var c = default(float3);
                    for (var y = by; y < by + block; y += 4)
                    {
                        for (var x = bx; x < bx + block; x += 4)
                        {
                            var i = ((row * face + y) * width + col * face + x) * 4;
                            c += new float3(Linear(pixels[i]), Linear(pixels[i + 1]), Linear(pixels[i + 2]));
                        }
                    }

                    c /= block * block / 16f;
                    var dir = Direction(f, (bx + block * 0.5f) / face, (by + block * 0.5f) / face);
                    if (dir.Y < 0f)
                        continue;
                    var lum = c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;
                    lumSum += lum;
                    lumCount++;
                    if (lum > bestLum)
                    {
                        bestLum = lum;
                        bestColor = c;
                        bestDir = dir;
                    }
                }
            }
        }

        var mean = lumSum / Math.Max(lumCount, 1);
        var standsOut = Math.Clamp((bestLum - mean) / MathF.Max(bestLum, 1e-3f) * 1.6f, 0.15f, 1f);
        var daylight = Math.Clamp(mean / 0.25f, 0.05f, 1f);
        var sunColor = bestColor / MathF.Max(MathF.Max(bestColor.X, MathF.Max(bestColor.Y, bestColor.Z)), 1e-3f);
        var sunDir = Normalize(new float3(bestDir.X, MathF.Max(bestDir.Y, 0.05f), bestDir.Z));

        uniforms.SunDirection = new float4(sunDir, 1f);
        uniforms.SunColor = new float4(sunColor * 3f * standsOut * daylight, 0f);
        uniforms.Zenith = new float4(zenith, 0f);
        uniforms.Horizon = new float4(horizon, 0f);
        uniforms.Ground = new float4(ground, 0f);
        uniforms.Mode = new float4(1f, 0f, 0f, 0f);

        // The cross's empty cells are filled with the faces' averages, so the blurrier mips (sampled for haze) fade
        // towards the sky's colours at face edges rather than to black.
        var fill = new[] { (zenith + horizon) * 0.5f, (ground + horizon) * 0.5f };
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                if (Faces.Contains((col, row)))
                    continue;
                var c = row == 0 ? fill[0] : row == 2 ? fill[1] : horizon;
                byte Srgb(float v) => (byte)Math.Clamp(MathF.Pow(v, 1f / 2.2f) * 255f, 0f, 255f);
                for (var y = 0; y < face; y++)
                {
                    for (var x = 0; x < face; x++)
                    {
                        var i = ((row * face + y) * width + col * face + x) * 4;
                        pixels[i] = Srgb(c.X);
                        pixels[i + 1] = Srgb(c.Y);
                        pixels[i + 2] = Srgb(c.Z);
                        pixels[i + 3] = 255;
                    }
                }
            }
        }

        return Texture2D.FromPixels(width, height, pixels, repeat: false, mipmaps: true, srgb: true, name: Path.GetFileName(path));
    }

    /// The direction through a point (u, v in 0..1, v down) on a cube face, as atmosphere.glsl's skyMapUv reads it.
    private static float3 Direction(int face, float u, float v)
    {
        var sc = u * 2f - 1f;
        var tc = v * 2f - 1f;
        var d = face switch
        {
            0 => new float3(1f, -tc, -sc),
            1 => new float3(-1f, -tc, sc),
            2 => new float3(sc, 1f, tc),
            3 => new float3(sc, -1f, -tc),
            4 => new float3(sc, -tc, 1f),
            _ => new float3(-sc, -tc, -1f),
        };
        return Normalize(d);
    }

    private static float3 Normalize(float3 v) => v / MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private void Release()
    {
        _map?.Dispose();
        _map = null;
        ReleaseUniforms();
    }

    private void ReleaseUniforms()
    {
        if (!_uniforms.IsNull())
        {
            var uniforms = _uniforms;
            _uniforms = default;
            Gpu.Defer(uniforms.Dispose);
        }

        if (!_pool.IsNull())
        {
            var pool = _pool;
            _pool = default;
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator));
        }
    }

    public void Dispose()
    {
        Release();
        _sun.Dispose();
        _moon.Dispose();
    }

    /// Releases the set layout at shutdown.
    public static void Shutdown()
    {
        if (_setLayout.IsNull())
            return;
        Gpu.Device.DestroyDescriptorSetLayout(_setLayout, Gpu.Device.VkAllocator);
        _setLayout = default;
    }
}
