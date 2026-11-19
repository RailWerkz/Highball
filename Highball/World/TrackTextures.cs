using Brutal.VulkanApi;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The track's PBR materials, at the size the settings ask for, as TrackMaterials (Content/Materials/Track.json) says: three maps
/// each, colour, normal, and ambient occlusion, roughness, metalness and height packed into one, in that order. By default
/// Metal021 is the clean top of a rail, Metal025 its sides and the tie plates, Metal024 the weathering on painted parts,
/// Rocks022 the ballast and the ground beside it, and Wood062 the ties and the poles. The poles' wood takes a 4K zip if there's
/// one and the setting's at its largest; it's close to 300 MB once it's up, mipmaps and all.
///
/// Changes in the Track Materials window are picked up on the next frame: new textures are decoded on a worker thread, and the
/// ones in use stay on screen until they're ready. A texture used by several parts is loaded once.
/// Bound as the world's set 4 while track is drawn.
public sealed class TrackTextures : IDisposable
{
    /// Rocks, rail top, rail side, bridge steel and pole wood, three maps each; then each paint's three maps (TrackMaterials.Paints,
    /// in order); then the paints' palette. A tie's or plate's first three maps are in the same places, with the rest repeated so
    /// every binding fits the one layout.
    public static readonly int PaintCount = TrackMaterials.Paints.Length;
    /// (A crossing's own paints' maps come last, after the surfaces' lookup, so the bindings before them stay where they were.)
    public static readonly int SetSize = 15 + PaintCount * 3 + 1 + SurfaceTextures.Slots * 3 + 1;

    /// What's to be loaded has changed: a texture in the Track Materials window, or another surface texture come into use.
    private static int WantedVersion => TrackMaterials.TextureVersion * 65536 + SurfaceTextures.Version;
    public const int SetIndex = 4;

    public static VkDescriptorSetLayout SetLayout => TextureBinding.SetLayoutFor(SetSize);

    /// The line shader's first five texture sets, in its order.
    private static readonly TrackMaterials.Slot[] LineSlots =
        [TrackMaterials.Slot.Ballast, TrackMaterials.Slot.RailTop, TrackMaterials.Slot.RailSide, TrackMaterials.Slot.BridgeSteel, TrackMaterials.Slot.PoleWood];

    private readonly Texture2D _neutral;
    private readonly TextureBinding _none;

    /// Uploaded textures by id and size, kept while something uses them.
    private readonly Dictionary<(string Id, string Size), Texture2D[]> _loaded = [];
    private Texture2D? _palette;
    private Texture2D? _surfaceLookup;
    private TextureBinding? _lines;
    private TextureBinding? _ties;
    private TextureBinding? _plates;
    private int _size;
    private int _textureVersion = -1;
    private int _version = -1;
    private Task<Dictionary<(string Id, string Size), TrackMaterials.Maps?>>? _decoding;
    private int _decodingVersion;
    private int _decodingSize;

    /// What each part's drawn with right now, by id ("" for nothing found).
    public static IReadOnlyDictionary<string, string> InUse => _inUse;
    private static Dictionary<string, string> _inUse = [];

    /// Whether new textures are being loaded.
    public static bool Loading { get; private set; }

    public TrackTextures()
    {
        _neutral = Texture2D.Solid(new byte4(128, 128, 128, 255));
        _none = new TextureBinding(Enumerable.Repeat(_neutral, SetSize).ToArray());

        // The first set's loaded straight away, so the track never shows without them.
        _size = Wanted;
        _decodingVersion = WantedVersion;
        _decodingSize = _size;
        Apply(DecodeAll(Wanted, Needed(Wanted)));
    }

    /// The rock's colour on its own, at the small size, for a scene that isn't drawing track.
    public static Texture2D RockColor() =>
        TrackMaterials.Decode(TrackMaterials.Current.TextureOf(TrackMaterials.Slot.Ballast), "1K") is { } d
            ? Texture2D.FromPixels(d.Width, d.Height, d.Color, repeat: true, mipmaps: true, srgb: true, name: "ballast.color")
            : Texture2D.Solid(new byte4(110, 105, 100, 255));

    /// The texture quality settings (Settings.TrackTextureQuality), 4K at most: Low has everything at 1K, Medium at 2K, High at 2K
    /// but the ballast and pole wood (seen closest) at 4K, Ultra everything at 4K. The ballast's never under 2K: its stones are a
    /// few centimetres across, and blur at less. A texture that doesn't come in a size is loaded at the nearest it does
    /// (TrackMaterials.Decode).
    public static readonly string[] Qualities = ["Low", "Medium", "High", "Ultra"];

    /// The quality wanted, 0 (Low) to 3 (Ultra).
    private static int Wanted => Math.Clamp(Settings.Current.TrackTextureQuality, 0, Qualities.Length - 1);

    /// The size most textures are loaded at, at a quality.
    private static string Label(int quality) => quality switch { <= 0 => "1K", >= 3 => "4K", _ => "2K" };

    /// The size a slot's texture is loaded at, at a quality.
    private static string SizeOf(TrackMaterials.Slot slot, int quality) => slot switch
    {
        TrackMaterials.Slot.Ballast => quality >= 2 ? "4K" : "2K",
        TrackMaterials.Slot.PoleWood => quality >= 2 ? "4K" : Label(quality),
        _ => Label(quality),
    };

    /// A quality as the settings show it.
    public static string Describe(int quality)
    {
        quality = Math.Clamp(quality, 0, Qualities.Length - 1);
        var name = Qualities[quality].ToUpperInvariant();
        return quality == 2 ? $"{name} (2K, BALLAST 4K)" : $"{name} ({Label(quality)})";
    }

    /// Every (texture, size) the current materials need.
    private static List<(string Id, string Size)> Needed(int size)
    {
        var materials = TrackMaterials.Current;
        var label = Label(size);
        var needed = new List<(string, string)>();
        foreach (var (slot, _, _, _) in TrackMaterials.Slots)
            needed.Add((materials.TextureOf(slot), SizeOf(slot, size)));
        for (var i = 0; i < PaintCount; i++)
            needed.Add((materials.PaintOf(i).Texture, label));
        foreach (var id in SurfaceTextures.Bound(out _))
            if (id.Length > 0)
                needed.Add((id, label));
        return needed.Distinct().ToList();
    }

    private static Dictionary<(string Id, string Size), TrackMaterials.Maps?> DecodeAll(int size, List<(string Id, string Size)> needed)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var decoded = new Dictionary<(string, string), TrackMaterials.Maps?>();
        var results = new TrackMaterials.Maps?[needed.Count];
        // The JPEGs take far longer than the upload.
        Parallel.For(0, needed.Count, i => results[i] = TrackMaterials.Decode(needed[i].Id, needed[i].Size));
        for (var i = 0; i < needed.Count; i++)
            decoded[needed[i]] = results[i];
        Log.Info($"Track textures: {decoded.Count} decoded at {Qualities[size]} quality in {clock.ElapsedMilliseconds} ms");
        return decoded;
    }

    /// Picks up a change of size in the settings or of textures or paints in the Track Materials window. Called every frame.
    public void Refresh()
    {
        TrackMaterials.CheckFile();
        var size = Wanted;
        if (_decoding is { IsCompleted: true } done)
        {
            _decoding = null;
            Loading = false;
            // Only what was asked for most recently goes up.
            if (_decodingVersion == WantedVersion && _decodingSize == size)
            {
                _size = size;
                Apply(done.Result);
            }
        }

        if (_decoding is null && (WantedVersion != _textureVersion || size != _size))
        {
            // Already loaded ones aren't read again.
            var needed = Needed(size).Where(n => !_loaded.TryGetValue(n, out var have) || have.Length == 0).ToList();
            _decodingVersion = WantedVersion;
            _decodingSize = size;
            if (needed.Count == 0)
            {
                _size = size;
                Apply([]);
            }
            else
            {
                Loading = true;
                _decoding = Task.Run(() => DecodeAll(size, needed));
            }
        }

        if (TrackMaterials.Version != _version && _lines is not null)
            Rebind();
    }

    /// Uploads newly decoded textures, drops ones nothing uses, and binds them all again.
    private void Apply(Dictionary<(string Id, string Size), TrackMaterials.Maps?> decoded)
    {
        foreach (var (key, maps) in decoded)
        {
            if (maps is not { } d || (_loaded.TryGetValue(key, out var have) && have.Length == 3))
                continue;
            var name = Path.GetFileName(key.Id.TrimEnd('/'));
            _loaded[key] =
            [
                Texture2D.FromPixels(d.Width, d.Height, d.Color, repeat: true, mipmaps: true, srgb: true, name: name + ".color"),
                Texture2D.FromPixels(d.Width, d.Height, d.Normal, repeat: true, mipmaps: true, name: name + ".normal"),
                Texture2D.FromPixels(d.Width, d.Height, d.Packed, repeat: true, mipmaps: true, name: name + ".packed"),
            ];
        }

        // Ones that failed are remembered as missing, so they aren't tried every frame.
        foreach (var (key, maps) in decoded)
        {
            if (maps is null)
                _loaded.TryAdd(key, []);
        }

        _textureVersion = _decodingVersion;
        Rebind();

        var needed = Needed(_size).ToHashSet();
        foreach (var key in _loaded.Keys.Where(k => !needed.Contains(k)).ToList())
        {
            foreach (var texture in _loaded[key])
                texture.Dispose();
            _loaded.Remove(key);
        }
    }

    /// The three maps for a texture: the size asked for, else the smaller size (a 4K pole wood that isn't there), else neutral.
    private Texture2D[] Maps(string id, string size)
    {
        if (_loaded.TryGetValue((id, size), out var maps) && maps.Length == 3)
            return maps;
        if (_loaded.TryGetValue((id, Label(_size)), out maps) && maps.Length == 3)
            return maps;
        return [_neutral, _neutral, _neutral];
    }

    private void Rebind()
    {
        var materials = TrackMaterials.Current;
        var label = Label(_size);
        Texture2D[] Of(TrackMaterials.Slot slot) => Maps(materials.TextureOf(slot), SizeOf(slot, _size));

        var palette = MakePalette(materials);
        var lines = new List<Texture2D>();
        foreach (var slot in LineSlots)
            lines.AddRange(Of(slot));
        for (var i = 0; i < TrackMaterials.SharedPaints; i++)
            lines.AddRange(Maps(materials.PaintOf(i).Texture, label));
        lines.Add(palette);
        // The paved surfaces' textures in use (SurfaceTextures), after it, then which slot each texture's in.
        var bound = SurfaceTextures.Bound(out var lookup);
        foreach (var id in bound)
            lines.AddRange(id.Length > 0 ? Maps(id, label) : [_neutral, _neutral, _neutral]);
        var lookupPixels = new byte[SurfaceTextures.Max * 4];
        for (var i = 0; i < lookup.Length; i++)
        {
            lookupPixels[i * 4] = lookup[i].Slot;
            lookupPixels[i * 4 + 1] = lookup[i].Kind;
        }
        var surfaceLookup = Texture2D.FromPixels(SurfaceTextures.Max, 1, lookupPixels, repeat: false, mipmaps: false, linear: false, name: "surfaces");
        lines.Add(surfaceLookup);
        for (var i = TrackMaterials.SharedPaints; i < PaintCount; i++)
            lines.AddRange(Maps(materials.PaintOf(i).Texture, label));

        Texture2D[] Alone(Texture2D[] three) => [.. Enumerable.Range(0, SetSize - 1).Select(i => three[i % 3]), _neutral];

        var (oldLines, oldTies, oldPlates, oldPalette, oldLookup) = (_lines, _ties, _plates, _palette, _surfaceLookup);
        _surfaceLookup = surfaceLookup;
        _lines = new TextureBinding([.. lines]);
        _ties = new TextureBinding(Alone(Of(TrackMaterials.Slot.Ties)));
        _plates = new TextureBinding(Alone(Of(TrackMaterials.Slot.Plates)));
        _palette = palette;
        oldLines?.Dispose();
        oldTies?.Dispose();
        oldPlates?.Dispose();
        oldPalette?.Dispose();
        oldLookup?.Dispose();
        _version = TrackMaterials.Version;

        var inUse = new Dictionary<string, string>();
        foreach (var (slot, _, _, _) in TrackMaterials.Slots)
            inUse[slot.ToString()] = Of(slot)[0] == _neutral ? "" : materials.TextureOf(slot);
        for (var i = 0; i < PaintCount; i++)
            inUse[TrackMaterials.Paints[i].Name] = Maps(materials.PaintOf(i).Texture, label)[0] == _neutral ? "" : materials.PaintOf(i).Texture;
        _inUse = inUse;
    }

    /// Each paint as two texels: its colour (square-rooted, for precision in the dark ones) and how strongly its texture shows
    /// through; then whether it's metal and its roughness. A third row has each signal lamp's lens: its colour, square-rooted, and
    /// its brightness over LensScale.
    public const float LensScale = 32f;

    private static Texture2D MakePalette(TrackMaterials materials)
    {
        var pixels = new byte[PaintCount * 3 * 4];
        static byte Unit(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        for (var i = 0; i < PaintCount; i++)
        {
            var paint = materials.PaintOf(i);
            var top = i * 4;
            pixels[top] = Unit(MathF.Sqrt(Math.Max(paint.Color[0], 0f)));
            pixels[top + 1] = Unit(MathF.Sqrt(Math.Max(paint.Color[1], 0f)));
            pixels[top + 2] = Unit(MathF.Sqrt(Math.Max(paint.Color[2], 0f)));
            pixels[top + 3] = Unit(paint.Strength);
            var bottom = (PaintCount + i) * 4;
            pixels[bottom] = paint.Metal ? (byte)255 : (byte)0;
            pixels[bottom + 1] = Unit(paint.Roughness);
            pixels[bottom + 2] = 0;
            pixels[bottom + 3] = 255;
        }

        for (var i = 0; i < Math.Min(TrackMaterials.Lamps.Length, PaintCount); i++)
        {
            var lamp = materials.LampOf(i);
            var at = (PaintCount * 2 + i) * 4;
            pixels[at] = Unit(MathF.Sqrt(Math.Max(lamp.Color[0], 0f)));
            pixels[at + 1] = Unit(MathF.Sqrt(Math.Max(lamp.Color[1], 0f)));
            pixels[at + 2] = Unit(MathF.Sqrt(Math.Max(lamp.Color[2], 0f)));
            pixels[at + 3] = Unit(lamp.LensBrightness / LensScale);
        }

        return Texture2D.FromPixels(PaintCount, 3, pixels, repeat: false, mipmaps: false, linear: false, name: "paints");
    }

    public void BindLines(CommandBuffer cmd, VkPipelineLayout layout) => _lines!.BindAt(cmd, layout, SetIndex);
    public void BindTies(CommandBuffer cmd, VkPipelineLayout layout) => _ties!.BindAt(cmd, layout, SetIndex);
    public void BindPlates(CommandBuffer cmd, VkPipelineLayout layout) => _plates!.BindAt(cmd, layout, SetIndex);

    /// For anything else drawn with the part pipeline: it goes by its own texture.
    public void BindNone(CommandBuffer cmd, VkPipelineLayout layout) => _none.BindAt(cmd, layout, SetIndex);

    public void Dispose()
    {
        // A load still going is left to finish on its own; nothing it makes is uploaded.
        _decoding = null;
        Loading = false;
        _lines?.Dispose();
        _ties?.Dispose();
        _plates?.Dispose();
        _palette?.Dispose();
        foreach (var maps in _loaded.Values)
        {
            foreach (var texture in maps)
                texture.Dispose();
        }

        _loaded.Clear();
        _none.Dispose();
        _neutral.Dispose();
    }
}
