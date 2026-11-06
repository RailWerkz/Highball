using System.IO.Compression;
using Brutal.StbApi;
using Brutal.VulkanApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The track's PBR materials, read straight from the zips in Content/Textures/Track at the size the settings ask for: colour, normal,
/// and ambient occlusion, roughness, metalness and height packed into one, in that order. Metal021 is the clean top of a rail, Metal025
/// its sides and the tie plates, Metal024 the weathering on painted parts, Rocks022 the ballast, Ground079S the dirt and Wood062 the ties.
/// Bound as the world's set 4 while track is drawn.
public sealed class TrackTextures : IDisposable
{
    /// Rocks, rail top, rail side, painted parts and dirt, three maps each, in the set the line shader reads. A tie's or plate's first three maps are in
    /// the same places, with the rest repeated so that every binding fits the one layout.
    public const int SetSize = 15;
    public const int SetIndex = 4;

    public static VkDescriptorSetLayout SetLayout => TextureBinding.SetLayoutFor(SetSize);

    private static readonly string[] Materials = ["Rocks022", "Metal021", "Metal025", "Wood062", "Metal024", "Ground079S"];

    private Texture2D _neutral;
    private TextureBinding _none;
    private readonly List<Texture2D> _textures = [];
    private TextureBinding? _lines;
    private TextureBinding? _ties;
    private TextureBinding? _plates;
    private int _size;

    public TrackTextures()
    {
        _neutral = Texture2D.Solid(new byte4(128, 128, 128, 255));
        var maps = Enumerable.Repeat(_neutral, SetSize).ToArray();
        _none = new TextureBinding(maps);
        Load();
    }

    private static int Wanted => Settings.Current.TrackTextureSize >= 2048 ? 2048 : 1024;

    private void Load()
    {
        _size = Wanted;
        var label = _size >= 2048 ? "2K" : "1K";
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The JPEGs take far longer than the upload, and there are a lot of them.
        var decoded = new (byte[] Color, byte[] Normal, byte[] Packed, int Width, int Height)?[Materials.Length];
        Parallel.For(0, Materials.Length, i => decoded[i] = Decode(Materials[i], label));

        var sets = new Texture2D[Materials.Length][];
        for (var i = 0; i < Materials.Length; i++)
        {
            if (decoded[i] is { } d)
            {
                sets[i] =
                [
                    Texture2D.FromPixels(d.Width, d.Height, d.Color, repeat: true, mipmaps: true, srgb: true, name: Materials[i] + ".color"),
                    Texture2D.FromPixels(d.Width, d.Height, d.Normal, repeat: true, mipmaps: true, name: Materials[i] + ".normal"),
                    Texture2D.FromPixels(d.Width, d.Height, d.Packed, repeat: true, mipmaps: true, name: Materials[i] + ".packed"),
                ];
                _textures.AddRange(sets[i]);
            }
            else
            {
                sets[i] = [_neutral, _neutral, _neutral];
            }
        }

        Texture2D[] Of(int i) => sets[i];
        Texture2D[] Alone(int i) => [.. Of(i), .. Of(i), .. Of(i), .. Of(i), .. Of(i)];
        _lines = new TextureBinding([.. Of(0), .. Of(1), .. Of(2), .. Of(4), .. Of(5)]);
        _plates = new TextureBinding(Alone(2));
        _ties = new TextureBinding(Alone(3));
        Log.Info($"Track textures: {label} in {clock.ElapsedMilliseconds} ms");
    }

    private static (byte[] Color, byte[] Normal, byte[] Packed, int Width, int Height)? Decode(string material, string label)
    {
        var name = $"{material}_{label}-JPG";
        var path = ContentPath.Texture($"Track/{name}.zip");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            byte[]? Bytes(string map)
            {
                if (zip.GetEntry($"{name}_{map}.jpg") is not { } entry)
                    return null;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }

            var (color, width, height) = Image(Bytes("Color") ?? throw new InvalidDataException("no colour map"));
            var (normal, _, _) = Image(Bytes("NormalGL") ?? throw new InvalidDataException("no normal map"));
            var (rough, _, _) = Image(Bytes("Roughness") ?? throw new InvalidDataException("no roughness map"));
            var metal = Bytes("Metalness") is { } m ? Image(m).Pixels : null;
            var occlusion = Bytes("AmbientOcclusion") is { } o ? Image(o).Pixels : null;
            var height2 = Bytes("Displacement") is { } h ? Image(h).Pixels : null;

            // Stone and wood have no metalness map, nothing in them being metal; the metals have no occlusion map.
            var packed = new byte[rough.Length];
            for (var p = 0; p < packed.Length; p += 4)
            {
                packed[p] = occlusion is null ? (byte)255 : occlusion[p];
                packed[p + 1] = rough[p];
                packed[p + 2] = metal is null ? (byte)0 : metal[p];
                packed[p + 3] = height2 is null ? (byte)128 : height2[p];
            }

            return (color, normal, packed, width, height);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Track textures: {name}: {e.Message}");
            return null;
        }
    }

    private static (byte[] Pixels, int Width, int Height) Image(byte[] jpeg)
    {
        using var image = Stb.LoadFromMemory(jpeg, 4);
        return (image.AsSpan().ToArray(), image.Width, image.Height);
    }

    /// Loads the other size if the setting's changed. Waits for it: it's only ever asked for from the menu.
    public void Refresh()
    {
        if (Wanted == _size)
            return;

        Release();
        Load();
    }

    public void BindLines(CommandBuffer cmd, VkPipelineLayout layout) => _lines!.BindAt(cmd, layout, SetIndex);
    public void BindTies(CommandBuffer cmd, VkPipelineLayout layout) => _ties!.BindAt(cmd, layout, SetIndex);
    public void BindPlates(CommandBuffer cmd, VkPipelineLayout layout) => _plates!.BindAt(cmd, layout, SetIndex);

    /// For anything else drawn with the part pipeline: it goes by its own texture.
    public void BindNone(CommandBuffer cmd, VkPipelineLayout layout) => _none.BindAt(cmd, layout, SetIndex);

    private void Release()
    {
        _lines?.Dispose();
        _ties?.Dispose();
        _plates?.Dispose();
        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();
    }

    public void Dispose()
    {
        Release();
        _none.Dispose();
        _neutral.Dispose();
    }
}
