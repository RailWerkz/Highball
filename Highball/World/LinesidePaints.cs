using System.Text.Json;
using System.Text.Json.Nodes;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// What each lineside model's parts are painted with, as its asset says (Content/Meshes/Lineside/&lt;Kind&gt;/asset.json): a model's parts
/// are named after a paint (Fixed_Black), and its asset.json says, for each of those slots, either the shared paint it's drawn with
/// (a name from TrackMaterials.Paints: "Black") or a paint of its own (texture, colour, strength, metal, roughness, as Track.json
/// has them). So the hand throw's black can be its own, apart from the signal heads' black.
///
/// Every slot of every model has a palette entry of its own (line.frag: AssetPaint on), whether it's linked to a shared paint or not,
/// so changing one only changes the palette: nothing's rebuilt. Only the shared seven paints (Black to Concrete) can be swapped:
/// the lenses, the wood and the ballast are drawn by line.frag's own rules.
public static class LinesidePaints
{
    /// A model's slot: its asset, the model's file (under Content/Meshes), the paint it's named for, its palette entry (after
    /// TrackMaterials.Paints), and what it's drawn with: Link, a shared paint's name, unless it has Own.
    public sealed class Slot
    {
        public required Asset Asset { get; init; }
        public required string Model { get; init; }
        public required string Name { get; init; }
        public required int Index { get; init; }
        public string Link { get; set; } = "";
        public TrackMaterials.Paint? Own { get; set; }
    }

    /// One asset.json: its folder under Lineside, its name, where it is, and its models' slots.
    public sealed class Asset
    {
        public required string Folder { get; init; }
        public required string Name { get; init; }
        public required string Path { get; init; }
        public List<Slot> Slots { get; } = [];
        public bool Unsaved { get; set; }
    }

    private static List<Asset>? _assets;
    private static List<Slot> _slots = [];
    private static Dictionary<(string Model, string Slot), Slot> _byModel = [];

    public static IReadOnlyList<Asset> Assets => Load();

    /// Every slot, by palette entry (TrackMaterials.Paints.Length + i).
    public static IReadOnlyList<Slot> All
    {
        get
        {
            Load();
            return _slots;
        }
    }

    /// The shared paints a slot can be: TrackMaterials.Paints' first seven.
    public static IEnumerable<string> Shareable => TrackMaterials.Paints.Take(TrackMaterials.SharedPaints).Select(p => p.Name);

    /// The slot a model's paint is, if its asset has it. model is its path under Content/Meshes ("Lineside/HandThrows/hand_throw.glb").
    public static Slot? Find(string model, string paint)
    {
        Load();
        return _byModel.GetValueOrDefault((model, paint));
    }

    /// What a slot's drawn with: its own paint, or the shared one it's linked to.
    public static TrackMaterials.Paint PaintOf(Slot slot)
    {
        if (slot.Own is { } own)
            return own;
        var at = Array.FindIndex(TrackMaterials.Paints, p => p.Name == slot.Link);
        return TrackMaterials.Current.PaintOf(at >= 0 ? at : Array.FindIndex(TrackMaterials.Paints, p => p.Name == slot.Name));
    }

    /// Links a slot to a shared paint (dropping a paint of its own).
    public static void Link(Slot slot, string paint)
    {
        if (slot.Own is null && slot.Link == paint)
            return;
        var texture = PaintOf(slot).Texture;
        slot.Link = paint;
        slot.Own = null;
        Changed(slot, PaintOf(slot).Texture != texture);
    }

    /// Gives a slot a paint of its own.
    public static void SetOwn(Slot slot, TrackMaterials.Paint paint)
    {
        var before = PaintOf(slot);
        if (slot.Own is not null && Same(before, paint))
            return;
        slot.Own = paint;
        Changed(slot, before.Texture != paint.Texture);
    }

    private static void Changed(Slot slot, bool texture)
    {
        slot.Asset.Unsaved = true;
        TrackMaterials.Touch(texture);
    }

    public static bool Same(TrackMaterials.Paint a, TrackMaterials.Paint b) =>
        a.Texture == b.Texture && a.Color.SequenceEqual(b.Color) && a.Strength == b.Strength && a.Metal == b.Metal && a.Roughness == b.Roughness;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// The Lineside folders: the repository's first (the one saved to), then the build's.
    private static IEnumerable<string> Roots()
    {
        yield return Path.Combine(ContentPath.SourceRoot, "Meshes", "Lineside");
        yield return Path.Combine(ContentPath.Root, "Meshes", "Lineside");
    }

    private static List<Asset> Load()
    {
        if (_assets is { } loaded)
            return loaded;
        var assets = new List<Asset>();
        var slots = new List<Slot>();
        var byModel = new Dictionary<(string, string), Slot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shareable = Shareable.ToHashSet();
        foreach (var root in Roots().Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                var path = Path.Combine(dir, "asset.json");
                if (!File.Exists(path) || !seen.Add(folder))
                    continue;
                try
                {
                    var doc = JsonNode.Parse(File.ReadAllText(path))!;
                    var asset = new Asset { Folder = folder, Name = (string?)doc["name"] ?? folder, Path = path };
                    foreach (var model in doc["models"]?.AsArray() ?? [])
                    {
                        var file = $"Lineside/{folder}/{(string?)model?["file"]}";
                        foreach (var (name, value) in model?["materials"]?.AsObject() ?? [])
                        {
                            if (!shareable.Contains(name))
                                continue;
                            var slot = new Slot { Asset = asset, Model = file, Name = name, Index = TrackMaterials.Paints.Length + slots.Count, Link = name };
                            if (value is JsonValue v && v.TryGetValue<string>(out var link))
                                slot.Link = shareable.Contains(link) ? link : name;
                            else if (value is JsonObject own && own.Deserialize<TrackMaterials.Paint>(Options) is { Color.Length: 3 } paint)
                                slot.Own = paint;
                            slots.Add(slot);
                            asset.Slots.Add(slot);
                            byModel[(file, name)] = slot;
                        }
                    }

                    assets.Add(asset);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log.Warn($"Lineside paints: {path}: {e.Message}; its models are drawn with the shared paints");
                }
            }
        }

        (_slots, _byModel) = (slots, byModel);
        return _assets = assets;
    }

    /// Writes an asset's slots back to its asset.json, keeping everything else in it.
    public static void Save(Asset asset)
    {
        var doc = JsonNode.Parse(File.ReadAllText(asset.Path))!;
        var folder = asset.Folder;
        foreach (var model in doc["models"]?.AsArray() ?? [])
        {
            var file = $"Lineside/{folder}/{(string?)model?["file"]}";
            if (model?["materials"] is not JsonObject materials)
                continue;
            foreach (var slot in asset.Slots.Where(s => s.Model == file))
                materials[slot.Name] = slot.Own is { } own ? JsonSerializer.SerializeToNode(own, Options) : JsonValue.Create(slot.Link);
        }

        File.WriteAllText(asset.Path, doc.ToJsonString(Indented).Replace("\r\n", "\n") + "\n");
        asset.Unsaved = false;
        Log.Info($"Lineside paints saved to {asset.Path}");
    }

    /// Puts an asset's slots back as its asset.json has them.
    public static void Revert(Asset asset)
    {
        var texture = asset.Slots.Select(PaintOf).Select(p => p.Texture).ToList();
        var doc = JsonNode.Parse(File.ReadAllText(asset.Path))!;
        foreach (var model in doc["models"]?.AsArray() ?? [])
        {
            var file = $"Lineside/{asset.Folder}/{(string?)model?["file"]}";
            foreach (var (name, value) in model?["materials"]?.AsObject() ?? [])
            {
                if (asset.Slots.FirstOrDefault(s => s.Model == file && s.Name == name) is not { } slot)
                    continue;
                (slot.Link, slot.Own) = (name, null);
                if (value is JsonValue v && v.TryGetValue<string>(out var link))
                    slot.Link = link;
                else if (value is JsonObject own && own.Deserialize<TrackMaterials.Paint>(Options) is { Color.Length: 3 } paint)
                    slot.Own = paint;
            }
        }

        asset.Unsaved = false;
        TrackMaterials.Touch(!asset.Slots.Select(PaintOf).Select(p => p.Texture).SequenceEqual(texture));
    }
}
