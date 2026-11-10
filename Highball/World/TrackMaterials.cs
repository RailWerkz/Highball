using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brutal.StbApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// Which textures the track and everything beside it is drawn with, read from Content/Materials/Track.json and changed live by
/// the Track Materials window (MaterialsWindow). TrackTextures loads what this says whenever Version moves on.
///
/// A texture is named by where it is: a path under Content/Textures without the size ("Metal/Metal021", for ambientCG's
/// Metal021_1K-JPG.zip and Metal021_2K-JPG.zip), a zip of its own, or a folder of loose maps, either under Content/Textures or
/// anywhere on disk. Maps are found by name: Color (or Albedo, BaseColor, Diffuse), NormalGL (or Normal, NormalDX, flipped),
/// Roughness, Metalness, AmbientOcclusion (or AO) and Displacement (or Height).
///
/// The parts placed beside the track (signals, hand throws, crossing masts, poles) are made in Blender with their meshes named
/// after a paint (Fixed_Black, Moving_Red): each paint has a texture, a colour it tints it with, how strongly the texture shows
/// through, and whether it's metal.
public sealed class TrackMaterials
{
    /// The textures the track itself has. The order is the order line.frag takes them in (TrackTextures).
    public enum Slot { Ballast, RailTop, RailSide, BridgeSteel, PoleWood, Ties, Plates }

    public static readonly (Slot Slot, string Label, string Help, string Default)[] Slots =
    [
        (Slot.Ballast, "Ballast", "The stone of the track bed and the ground either side", "Terrain/Rocks/Rocks022"),
        (Slot.RailTop, "Rail top", "The running surface of the rail head, worn bright by wheels", "Metal/Metal021"),
        (Slot.RailSide, "Rail sides", "The rail's web, foot and the sides of its head", "Metal/Metal025"),
        (Slot.Ties, "Ties (sleepers)", "The wooden ties, and a turnout's long timbers", "Wood/Wood062"),
        (Slot.Plates, "Tie plates", "The steel plates and spikes holding the rail down", "Metal/Metal025"),
        (Slot.PoleWood, "Pole wood", "Wooden poles, and any part painted Wood", "Wood/Wood062"),
        (Slot.BridgeSteel, "Bridge steel", "The weathering on bridge girders and railings", "Metal/Metal024"),
    ];

    /// The paints line.frag knows, in its order (NetworkMeshes.Paints), with the shader's own values as their defaults.
    public static readonly (string Name, Paint Default)[] Paints =
    [
        ("Black", new Paint("Metal/Metal024", [0.018f, 0.018f, 0.018f], 0.3f, false, 0.45f)),
        ("White", new Paint("Metal/Metal024", [0.62f, 0.62f, 0.60f], 0.3f, false, 0.45f)),
        ("Galv", new Paint("Metal/Metal024", [0.26f, 0.27f, 0.28f], 0.8f, true, 0.5f)),
        ("Yellow", new Paint("Metal/Metal024", [0.60f, 0.42f, 0.05f], 0.3f, false, 0.45f)),
        ("Red", new Paint("Metal/Metal024", [0.50f, 0.02f, 0.015f], 0.3f, false, 0.45f)),
        ("Green", new Paint("Metal/Metal024", [0.02f, 0.32f, 0.12f], 0.3f, false, 0.45f)),
        ("Concrete", new Paint("Metal/Metal024", [0.30f, 0.29f, 0.26f], 0.3f, false, 0.45f)),
    ];

    /// A signal's lamps, in line.frag's order (LensRed, LensYellow, LensGreen), with the game's own values as their defaults.
    public static readonly (string Name, Lamp Default)[] Lamps =
    [
        ("Red", new Lamp([1f, 0.039f, 0.013f], 9f, 3f, 14f, 81f, 0f, 1f)),
        ("Yellow", new Lamp([1f, 0.467f, 0.033f], 9f, 3f, 14f, 81f, 0f, 1f)),
        ("Green", new Lamp([0.057f, 1f, 0.457f], 7f, 2.3f, 14f, 81f, 0f, 1f)),
    ];

    /// One signal lamp: its linear colour (brightest channel 1), how bright its lens glows, how strongly it lights what's in front of
    /// it and how far (metres), the half-angle of its beam (degrees), and the halo seen round it from afar: its size (metres, 0 for
    /// none) and brightness.
    public sealed record Lamp(float[] Color, float LensBrightness, float Intensity, float Range, float Beam, float GlowSize, float GlowBrightness);

    /// One paint: its texture, its linear colour (what the texture's tinted to), how much of the texture's own colour comes
    /// through (0 flat paint, 1 the texture's colour times the paint's, doubled), whether it's metal, and its roughness before
    /// the texture's roughness map is mixed in.
    public sealed record Paint(string Texture, float[] Color, float Strength, bool Metal, float Roughness);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DictionaryKeyPolicy = null,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<Slot, string> Textures { get; set; } = [];
    public Dictionary<string, Paint> PaintSettings { get; set; } = [];
    public Dictionary<string, Lamp> LampSettings { get; set; } = [];

    /// Moves on every change, so whatever draws with these knows to look again.
    [JsonIgnore] public static int Version { get; private set; }

    /// Moves on only when a texture (not just a paint's colour) has changed.
    [JsonIgnore] public static int TextureVersion { get; private set; }

    private static TrackMaterials? _current;
    public static TrackMaterials Current => _current ??= Load();

    /// Whether what's in use differs from what's saved.
    [JsonIgnore] public static bool Unsaved { get; private set; }

    public static string FilePath => Path.Combine(ContentPath.SourceRoot, "Materials", "Track.json");

    public string TextureOf(Slot slot) =>
        Textures.TryGetValue(slot, out var id) && !string.IsNullOrWhiteSpace(id) ? id : Slots.First(s => s.Slot == slot).Default;

    public Paint PaintOf(int index)
    {
        var (name, fallback) = Paints[index];
        return PaintSettings.TryGetValue(name, out var paint) && paint.Color is { Length: 3 } ? paint : fallback;
    }

    public Lamp LampOf(int index)
    {
        var (name, fallback) = Lamps[index];
        return LampSettings.TryGetValue(name, out var lamp) && lamp.Color is { Length: 3 } ? lamp : fallback;
    }

    public void SetLamp(int index, Lamp lamp)
    {
        LampSettings[Lamps[index].Name] = lamp;
        Changed(texture: false);
    }

    public static TrackMaterials Defaults() => new()
    {
        LampSettings = Lamps.ToDictionary(l => l.Name, l => l.Default),
        Textures = Slots.ToDictionary(s => s.Slot, s => s.Default),
        PaintSettings = Paints.ToDictionary(p => p.Name, p => p.Default),
    };

    private static TrackMaterials Load()
    {
        // The repository's copy first: that's the one the window saves to.
        foreach (var path in new[] { FilePath, Path.Combine(ContentPath.Root, "Materials", "Track.json") })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<TrackMaterials>(File.ReadAllText(path), Options) is { } loaded)
                {
                    var defaults = Defaults();
                    foreach (var (slot, id) in defaults.Textures)
                        loaded.Textures.TryAdd(slot, id);
                    foreach (var (name, paint) in defaults.PaintSettings)
                        loaded.PaintSettings.TryAdd(name, paint);
                    loaded.LampSettings ??= [];
                    foreach (var (name, lamp) in defaults.LampSettings)
                        loaded.LampSettings.TryAdd(name, lamp);
                    return loaded;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn($"Track materials: {path}: {e.Message}; using the defaults");
            }
        }

        return Defaults();
    }

    public void SetTexture(Slot slot, string id)
    {
        if (TextureOf(slot) == id)
            return;
        Textures[slot] = id;
        Changed(texture: true);
    }

    public void SetPaint(int index, Paint paint)
    {
        var before = PaintOf(index);
        if (before == paint)
            return;
        PaintSettings[Paints[index].Name] = paint;
        Changed(texture: before.Texture != paint.Texture);
    }

    private static void Changed(bool texture)
    {
        Version++;
        if (texture)
            TextureVersion++;
        Unsaved = true;
    }

    /// Writes what's in use to Content/Materials/Track.json in the repository (or beside the game when it isn't run from one).
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        _fileTime = FileTime();
        Unsaved = false;
        Log.Info($"Track materials saved to {FilePath}");
    }

    private static DateTime _fileTime = FileTime();
    private static long _lastCheck;

    private static DateTime FileTime() => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    /// Picks up Track.json edited outside the game (once a second at most), unless there are changes here not saved yet.
    public static void CheckFile()
    {
        var now = Environment.TickCount64;
        if (now - _lastCheck < 1000)
            return;
        _lastCheck = now;
        var time = FileTime();
        if (time == _fileTime)
            return;
        _fileTime = time;
        if (Unsaved)
            return;
        Log.Info("Track materials: Track.json changed on disk; loading it");
        Revert();
    }

    /// Puts back what was last saved.
    public static void Revert()
    {
        _fileTime = FileTime();
        _current = Load();
        Version++;
        TextureVersion++;
        Unsaved = false;
    }

    /// Puts back the game's own textures and paints (unsaved until saved).
    public static void ResetToDefaults()
    {
        _current = Defaults();
        Changed(texture: true);
    }

    // --- Finding textures. ---

    /// A texture that can be picked: its name as saved, a short label, and the sizes it comes in (empty for one that has only
    /// the one: a zip or a folder of its own).
    public sealed record Choice(string Id, string Label, string Group, string[] Sizes);

    private static readonly string[] SizeLabels = ["1K", "2K", "4K", "8K"];

    /// Every texture under Content/Textures (the repository's and the build's), sorted by folder and name.
    public static List<Choice> Find()
    {
        var found = new Dictionary<string, (string Group, SortedSet<string> Sizes, bool Sized)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots().Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var zip in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, zip).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(relative);
                var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                if (folder.StartsWith("Skyboxes", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("Planets", StringComparison.OrdinalIgnoreCase))
                    continue;

                // ambientCG's Name_2K-JPG.zip: one texture in several sizes.
                var size = SizeLabels.FirstOrDefault(s => name.EndsWith($"_{s}-JPG", StringComparison.OrdinalIgnoreCase) || name.EndsWith($"_{s}-PNG", StringComparison.OrdinalIgnoreCase));
                if (size is not null)
                {
                    var id = (folder.Length > 0 ? folder + "/" : "") + name[..name.LastIndexOf('_')];
                    if (!found.TryGetValue(id, out var entry))
                        found[id] = entry = (folder, new SortedSet<string>(), true);
                    entry.Sizes.Add(size);
                }
                else
                    found.TryAdd(relative, (folder, [], false));
            }

            // Folders of loose maps.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (MapFiles(dir).ContainsKey("color"))
                {
                    var relative = Path.GetRelativePath(root, dir).Replace('\\', '/');
                    found.TryAdd(relative, (Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "", [], false));
                }
            }
        }

        return found
            .Select(f => new Choice(f.Key, Path.GetFileName(f.Key.TrimEnd('/')), f.Value.Group.Length > 0 ? f.Value.Group : "Textures", [.. f.Value.Sizes]))
            .OrderBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Roots()
    {
        yield return Path.Combine(ContentPath.SourceRoot, "Textures");
        yield return Path.Combine(ContentPath.Root, "Textures");
    }

    /// Decoded maps, ready to upload: colour (sRGB), normal (OpenGL's way up), and ambient occlusion, roughness, metalness
    /// and height packed into one, in that order.
    public sealed record Maps(byte[] Color, byte[] Normal, byte[] Packed, int Width, int Height, string Source);

    /// Loads a texture at a size (1K, 2K...), or the nearest it comes in. Null, with a warning, if it can't be found or read.
    public static Maps? Decode(string id, string size)
    {
        try
        {
            var (files, source) = Open(id, size) ?? throw new FileNotFoundException("not found under Content/Textures or on disk");
            using (files)
            {
                var color = files.Read("color") ?? throw new InvalidDataException("no colour map (a file named ..._Color)");
                var (colorPixels, width, height) = Image(color);
                byte[] normal;
                if (files.Read("normalgl") is { } gl)
                    normal = Fit(Image(gl), width, height);
                else if (files.Read("normal") is { } plain)
                    normal = Fit(Image(plain), width, height);
                else if (files.Read("normaldx") is { } dx)
                {
                    normal = Fit(Image(dx), width, height);
                    for (var p = 1; p < normal.Length; p += 4)
                        normal[p] = (byte)(255 - normal[p]);
                }
                else
                    normal = Fill(width, height, 128, 128, 255);

                byte[]? Map(string key) => files.Read(key) is { } bytes ? Fit(Image(bytes), width, height) : null;
                var rough = Map("roughness");
                var metal = Map("metalness");
                var occlusion = Map("ao");
                var displacement = Map("height");

                var packed = new byte[width * height * 4];
                for (var p = 0; p < packed.Length; p += 4)
                {
                    packed[p] = occlusion is null ? (byte)255 : occlusion[p];
                    packed[p + 1] = rough is null ? (byte)200 : rough[p];
                    packed[p + 2] = metal is null ? (byte)0 : metal[p];
                    packed[p + 3] = displacement is null ? (byte)128 : displacement[p];
                }

                return new Maps(colorPixels, normal, packed, width, height, source);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"Track materials: {id} ({size}): {e.Message}");
            return null;
        }
    }

    /// A small copy of a texture's colour map, for the window. Square, size pixels across.
    public static (byte[] Pixels, int Size)? Thumbnail(string id, int size = 96)
    {
        try
        {
            var opened = Open(id, "1K");
            if (opened is null)
                return null;
            using var files = opened.Value.Files;
            if (files.Read("color") is not { } bytes)
                return null;
            var image = Image(bytes);
            return (Resample(image.Pixels, image.Width, image.Height, size, size), size);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// Where a texture's files are: the zip for the size (or the nearest), a zip given by name, or a folder.
    private static (MapSource Files, string Source)? Open(string id, string size)
    {
        id = id.Trim().Replace('\\', '/');
        var candidates = Path.IsPathRooted(id) ? [id] : Roots().Select(r => Path.Combine(r, id)).ToArray();
        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                return (new FolderSource(path), path);
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (new ZipSource(path), path);

            // ambientCG's sizes: the one asked for, then smaller, then larger.
            var at = Array.IndexOf(SizeLabels, size);
            var order = SizeLabels.Take(at + 1).Reverse().Concat(SizeLabels.Skip(at + 1));
            foreach (var label in order)
            {
                foreach (var kind in (string[])["JPG", "PNG"])
                {
                    var zip = $"{path}_{label}-{kind}.zip";
                    if (File.Exists(zip))
                        return (new ZipSource(zip), zip);
                }
            }
        }

        return null;
    }

    /// The key a map's file name says it is ("color", "normalgl", "roughness"...), or null.
    private static string? KeyOf(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".tga" or ".bmp"))
            return null;
        // The last word of the name: Rocks022_2K-JPG_Color, rock_basecolor, coal-albedo.
        var word = name.Split('_', '-', ' ', '.').LastOrDefault() ?? name;
        return word switch
        {
            "color" or "albedo" or "basecolor" or "diffuse" or "col" or "diff" => "color",
            "normalgl" or "nor" or "normal" or "nrm" => word == "normalgl" ? "normalgl" : "normal",
            "normaldx" => "normaldx",
            "roughness" or "rough" or "rgh" => "roughness",
            "metalness" or "metallic" or "metal" => "metalness",
            "ambientocclusion" or "ao" or "occlusion" => "ao",
            "displacement" or "height" or "disp" => "height",
            _ => name.Contains("basecolor") || name.EndsWith("color") || name.Contains("albedo") ? "color"
                : name.Contains("normal") ? (name.Contains("dx") ? "normaldx" : "normal")
                : name.Contains("rough") ? "roughness"
                : name.Contains("metal") ? "metalness"
                : name.Contains("occlusion") ? "ao"
                : name.Contains("displace") || name.Contains("height") ? "height"
                : null,
        };
    }

    private static Dictionary<string, string> MapFiles(string dir)
    {
        var maps = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (KeyOf(file) is { } key)
                maps.TryAdd(key, file);
        }

        return maps;
    }

    private abstract class MapSource : IDisposable
    {
        public abstract byte[]? Read(string key);
        public virtual void Dispose() { }
    }

    private sealed class FolderSource(string dir) : MapSource
    {
        private readonly Dictionary<string, string> _maps = MapFiles(dir);
        public override byte[]? Read(string key) => _maps.TryGetValue(key, out var file) ? File.ReadAllBytes(file) : null;
    }

    private sealed class ZipSource : MapSource
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _maps = [];

        public ZipSource(string path)
        {
            _zip = ZipFile.OpenRead(path);
            foreach (var entry in _zip.Entries)
            {
                // A preview image at the top of an ambientCG zip is named after the texture alone: not a map.
                if (KeyOf(entry.Name) is { } key)
                    _maps.TryAdd(key, entry);
            }
        }

        public override byte[]? Read(string key)
        {
            if (!_maps.TryGetValue(key, out var entry))
                return null;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        public override void Dispose() => _zip.Dispose();
    }

    private static (byte[] Pixels, int Width, int Height) Image(byte[] encoded)
    {
        using var image = Stb.LoadFromMemory(encoded, 4);
        return (image.AsSpan().ToArray(), image.Width, image.Height);
    }

    private static byte[] Fit((byte[] Pixels, int Width, int Height) image, int width, int height) =>
        image.Width == width && image.Height == height ? image.Pixels : Resample(image.Pixels, image.Width, image.Height, width, height);

    /// Box-filtered when shrinking, nearest when growing.
    private static byte[] Resample(byte[] pixels, int width, int height, int toWidth, int toHeight)
    {
        var result = new byte[toWidth * toHeight * 4];
        var stepX = (float)width / toWidth;
        var stepY = (float)height / toHeight;
        for (var y = 0; y < toHeight; y++)
        {
            var y0 = (int)(y * stepY);
            var y1 = Math.Max(y0 + 1, Math.Min(height, (int)((y + 1) * stepY)));
            for (var x = 0; x < toWidth; x++)
            {
                var x0 = (int)(x * stepX);
                var x1 = Math.Max(x0 + 1, Math.Min(width, (int)((x + 1) * stepX)));
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = (sy * width + sx) * 4;
                        r += pixels[i];
                        g += pixels[i + 1];
                        b += pixels[i + 2];
                        a += pixels[i + 3];
                        n++;
                    }
                }

                var o = (y * toWidth + x) * 4;
                result[o] = (byte)(r / n);
                result[o + 1] = (byte)(g / n);
                result[o + 2] = (byte)(b / n);
                result[o + 3] = (byte)(a / n);
            }
        }

        return result;
    }

    private static byte[] Fill(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var p = 0; p < pixels.Length; p += 4)
            (pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]) = (r, g, b, 255);
        return pixels;
    }

    // --- What uses each paint. ---

    private static Dictionary<string, List<string>>? _users;

    /// For each paint, the models with parts painted it ("signal", "hand throw"...), from the models' own part names.
    public static IReadOnlyDictionary<string, List<string>> PaintUsers => _users ??= FindUsers();

    private static Dictionary<string, List<string>> FindUsers()
    {
        var users = Paints.ToDictionary(p => p.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        users["Wood"] = [];
        foreach (var folder in (string[])["Track", "Poles"])
        {
            var dir = ContentPath.Model(folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.glb"))
            {
                try
                {
                    var model = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
                    foreach (var node in NodeNames(file))
                    {
                        var paint = node[(node.IndexOf('_') + 1)..];
                        if (node.Contains('_') && users.TryGetValue(paint, out var list) && !list.Contains(model))
                            list.Add(model);
                    }
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
                {
                    // Not one of ours.
                }
            }
        }

        return users;
    }

    /// The node names in a .glb's JSON chunk.
    private static IEnumerable<string> NodeNames(string glb)
    {
        using var stream = File.OpenRead(glb);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46546C67)
            throw new InvalidDataException("not a .glb");
        reader.ReadUInt32();
        reader.ReadUInt32();
        var length = (int)reader.ReadUInt32();
        reader.ReadUInt32();
        using var json = JsonDocument.Parse(reader.ReadBytes(length));
        if (!json.RootElement.TryGetProperty("nodes", out var nodes))
            return [];
        return nodes.EnumerateArray().Select(n => n.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "").Where(n => n.Length > 0).ToList();
    }
}
