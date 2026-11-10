using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// One vehicle in a consist: which one, in which paint, which way round, how full it's loaded, and the name the player gave
/// it (a road number, say), shown on its tag. Load is the share of its rated load carried, 0 to 1; files from before it
/// existed only say Loaded, which counts as full. Cargo is what it's seen carrying once loaded (a well car's stacks), for
/// a vehicle that has cargoes; null for its first.
public sealed record ConsistCar(string Vehicle, string? Livery = null, bool Reversed = false, bool Loaded = false, string? Tag = null, double? Load = null,
    string? Cargo = null)
{
    [JsonIgnore]
    public VehicleDef? Def => VehicleCatalog.Find(Vehicle);

    /// The share of its rated load it carries, 0 (empty) to 1 (full). A car saved before loads could be set, under an old
    /// livery id that showed it carrying something (a well car's "double stack"), counts as full, so it looks as it did.
    [JsonIgnore]
    public double Fill => Math.Clamp(Load ?? (Loaded || ShowedLoaded ? 1.0 : 0.0), 0.0, 1.0);

    private bool ShowedLoaded => Livery is { } id && Def?.FindLivery(id) is { } livery && livery.Was.TryGetValue(id, out var meant) && meant is not null;

    /// What it's seen carrying once loaded; null for a vehicle without cargoes.
    [JsonIgnore]
    public string? CargoId => Def?.CargoFor(Livery, Cargo);

    /// The same car loaded to this share (Loaded kept in step, for anything that only asks whether it carries anything).
    public ConsistCar WithFill(double fill)
    {
        fill = Math.Clamp(fill, 0.0, 1.0);
        return Settled() with { Load = fill, Loaded = fill > 0.0 };
    }

    /// The same car with what an old file left to be worked out written down: its livery's current id, its load and its
    /// cargo. Done before any change, so changing one thing doesn't lose what an old livery id meant for the others.
    public ConsistCar Settled()
    {
        var fill = Fill;
        return this with { Livery = Def?.FindLivery(Livery)?.Id ?? Livery, Load = fill, Loaded = fill > 0.0, Cargo = CargoId };
    }

    /// The model it's drawn with: its paint, carrying its cargo once it's loaded.
    [JsonIgnore]
    public string? ModelPath => Def?.FindLivery(Livery)?.ModelFor(CargoId, Fill);
}

/// A train as made up: its vehicles from the front (index 0) to the rear. Saved under the user's data folder, one file
/// a consist, so it can be put on any route.
public sealed class Consist
{
    public string Name { get; set; } = "New consist";
    public List<ConsistCar> Cars { get; set; } = [];

    public static string Folder
    {
        get
        {
            var path = Path.Combine(ContentPath.UserData, "Consists");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    // --- What it adds up to. Vehicles missing from the catalog count for nothing. ---

    [JsonIgnore]
    public int Locomotives => Cars.Count(c => c.Def?.Kind == VehicleKind.Locomotive);
    [JsonIgnore]
    public int RollingStock => Cars.Count(c => c.Def is { } d && d.Kind.IsRollingStock());
    [JsonIgnore]
    public int Missing => Cars.Count(c => c.Def is null);

    /// Over the couplers, end to end.
    [JsonIgnore]
    public double Length => Cars.Sum(c => c.Def?.Length ?? 0.0);
    [JsonIgnore]
    public double Mass => Cars.Sum(c => c.Def?.Mass(c.Fill) ?? 0.0);
    [JsonIgnore]
    public double Power => Cars.Sum(c => c.Def?.Power ?? 0.0);
    [JsonIgnore]
    public double StartingTractiveEffort => Cars.Sum(c => c.Def?.StartingTractiveEffort ?? 0.0);
    [JsonIgnore]
    public int Axles => Cars.Sum(c => c.Def?.Axles ?? 0);

    /// The slowest of its vehicles' own top speeds, or zero when none says.
    [JsonIgnore]
    public double MaxSpeed
    {
        get
        {
            var speeds = Cars.Select(c => c.Def?.MaxSpeed ?? 0.0).Where(s => s > 0.0).ToList();
            return speeds.Count > 0 ? speeds.Min() : 0.0;
        }
    }

    // --- On disk. ---

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// The file it's saved to: its name, made safe for a path.
    [JsonIgnore]
    public string FileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(Name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return (safe.Length > 0 ? safe : "Consist") + ".json";
        }
    }

    [JsonIgnore]
    public string FilePath => Path.Combine(Folder, FileName);

    /// The file it was read from or last saved to, so a rename moves it rather than leaving the old one behind.
    [JsonIgnore]
    public string? SavedAt { get; private set; }

    public void Save()
    {
        try
        {
            var path = FilePath;
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            if (SavedAt is { } old && !string.Equals(Path.GetFullPath(old), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(old))
                File.Delete(old);
            SavedAt = path;
            Log.Info($"Consist {Name}: saved, {Cars.Count} vehicles");
        }
        catch (Exception e)
        {
            Log.Error($"Consist {Name}: couldn't save ({e.Message})");
        }
    }

    public static Consist? Load(string path)
    {
        try
        {
            var consist = JsonSerializer.Deserialize<Consist>(File.ReadAllText(path), Options);
            if (consist is null)
                return null;
            consist.Cars ??= [];
            consist.SavedAt = path;
            return consist;
        }
        catch (Exception e)
        {
            Log.Warn($"Consist: {path} unreadable ({e.Message})");
            return null;
        }
    }

    /// Every saved consist, most recently changed first.
    public static List<Consist> All() => Directory.EnumerateFiles(Folder, "*.json")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .Select(Load).OfType<Consist>().ToList();

    public void Delete()
    {
        try
        {
            var path = SavedAt ?? FilePath;
            if (File.Exists(path))
                File.Delete(path);
            SavedAt = null;
        }
        catch (Exception e)
        {
            Log.Error($"Consist {Name}: couldn't delete ({e.Message})");
        }
    }

    public Consist Clone() => new() { Name = Name, Cars = [.. Cars], SavedAt = SavedAt };
}
