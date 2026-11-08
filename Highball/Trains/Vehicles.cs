using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// What a vehicle is, as the consist editor sorts them. Locomotives come first; the rest is rolling stock.
public enum VehicleKind
{
    Locomotive,
    Autorack,
    Boxcar,
    CoveredHopper,
    OpenHopper,
    Gondola,
    Flatcar,
    TankCar,
    Intermodal,
    Reefer,
    Caboose,
}

/// A lamp on a locomotive: its headlight's (one of its lamps) or a ditch light, at its front or rear end, and on which side (a
/// ditch light's 0 left, 1 right, looking out from that end). Position and Facing are in the model's own axes: metres from
/// its origin on the rail between the trucks, front +z, up +y.
public readonly record struct VehicleLamp(bool Ditch, bool Front, int Side, float3 Position, float3 Facing);

public static class VehicleKinds
{
    /// In the order the editor lists them.
    public static readonly VehicleKind[] All = Enum.GetValues<VehicleKind>();

    public static bool IsRollingStock(this VehicleKind kind) => kind != VehicleKind.Locomotive;

    public static string Plural(this VehicleKind kind) => kind switch
    {
        VehicleKind.Locomotive => "Locomotives",
        VehicleKind.Autorack => "Autoracks",
        VehicleKind.Boxcar => "Boxcars",
        VehicleKind.CoveredHopper => "Covered hoppers",
        VehicleKind.OpenHopper => "Open hoppers",
        VehicleKind.Gondola => "Gondolas",
        VehicleKind.Flatcar => "Flatcars",
        VehicleKind.TankCar => "Tank cars",
        VehicleKind.Intermodal => "Intermodal",
        VehicleKind.Reefer => "Reefers",
        VehicleKind.Caboose => "Cabooses",
        _ => kind.ToString(),
    };
}

/// One paint scheme of a vehicle and the model that wears it.
public sealed class Livery
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// Under Content/Models.
    public required string Model { get; init; }
}

/// How a locomotive's prime mover sounds, from vehicle.json's "engineSound": two loops recorded at idle and at full
/// throttle (paths under Content/Audio), crossfaded and pitched between as the revs follow the throttle, and an exhaust
/// chug mixed in under load.
public sealed class EngineSoundDef
{
    public string Idle { get; init; } = "";
    public string Run { get; init; } = "";
    public string? Chug { get; init; }

    /// How far the idle loop is pitched up by full revs, and how far down the full-throttle loop starts, as they crossfade.
    public float IdlePitchTop { get; init; } = 1.5f;
    public float RunPitchBottom { get; init; } = 0.7f;

    /// Seconds to spool from idle to full revs, and back down.
    public float SpoolUp { get; init; } = 8f;
    public float SpoolDown { get; init; } = 5f;

    /// How loud it all is: every engine sound's level is scaled by this. A touch under 1, so it sits under the rest of the mix.
    public float Volume { get; init; } = 0.9f;

    /// Starting up from cold, played once as the train's placed, and how long until it's running and can make power
    /// (a little under the recording, which ends idling).
    public string? Startup { get; init; }
    public double StartupSeconds { get; init; } = 12.0;

    /// The air compressor: cutting in, running (looped), and the unloader's blast as it cuts out.
    public string? CompressorStart { get; init; }
    public string? CompressorLoop { get; init; }
    public string? CompressorStop { get; init; }

    /// The dynamic brake's grid blowers, looped while it's on.
    public string? DynamicFan { get; init; }
}

/// One kind of vehicle, as its vehicle.json describes it: everything about it that doesn't change from one car to the next.
/// Quantities are SI throughout (metres, kilograms, watts, newtons, metres a second); the file names carry the unit.
public sealed class VehicleDef
{
    /// Unique across the catalog; consists refer to vehicles by it.
    public required string Id { get; init; }

    /// Ids of vehicles it's taken the place of: consists saved with one of them get this one.
    public IReadOnlyList<string> Replaces { get; init; } = [];
    public required string Name { get; init; }
    public required VehicleKind Kind { get; init; }

    /// Who built it and when, and its wheel arrangement or car type: shown, not simulated.
    public string Builder { get; init; } = "";
    public string Built { get; init; } = "";
    public string Type { get; init; } = "";

    /// Over the pulling faces of its couplers: how much track it takes up in a train, its knuckles locked with its
    /// neighbours'. vehicle.json gives it over the knuckles' noses, as the model's drawn; this is less by both noses.
    public required double Length { get; init; }

    /// How far each knuckle's nose stands past its pulling face (Couplers/coupler.json): half of how far two coupled heads
    /// overlap.
    public double KnuckleNose { get; init; }

    /// Between the centre pins of its two trucks.
    public double TruckCentres { get; init; }

    /// From its front coupler's pulling face to its front truck's centre, where the ends aren't alike (a locomotive's cab end);
    /// zero for half of what's left over, the same at each end. (vehicle.json gives it to the knuckle's nose.)
    public double FrontOverhangGiven { get; init; }
    public int Axles { get; init; } = 4;
    public double WheelDiameter { get; init; } = 0.914;

    /// Empty, and as loaded to its rated capacity (the same for a locomotive).
    public required double MassEmpty { get; init; }
    public double MassLoaded { get; init; }

    /// At the rail, for a locomotive; zero for anything that can't pull.
    public double Power { get; init; }
    public double StartingTractiveEffort { get; init; }
    public double ContinuousTractiveEffort { get; init; }
    public double DynamicBrakeEffort { get; init; }

    /// Its own top speed, metres a second.
    public double MaxSpeed { get; init; }

    public IReadOnlyList<Livery> Liveries { get; init; } = [];

    /// How its engine sounds; null for a vehicle without one (or a locomotive not given one yet).
    public EngineSoundDef? EngineSound { get; init; }

    /// Its headlights and ditch lights, from its model's assembly.json where that says (the fixtures cut out of the model),
    /// or put where they usually are on a road engine; none for anything else.
    public IReadOnlyList<VehicleLamp> Lamps { get; init; } = [];

    /// Where its horn and bell are, in the model's own axes like the lamps' (metres from its origin on the rail between the
    /// trucks, front +z, up +y), from the fixtures in its assembly.json; null where that doesn't say, and the sound's put in
    /// the usual place instead.
    public float3? Horn { get; init; }
    public float3? Bell { get; init; }

    public bool CanLoad => MassLoaded > MassEmpty;

    /// Truck centres, or a fair guess at them for a vehicle that doesn't say.
    public double Centres => TruckCentres > 0.0 ? TruckCentres : Length * 0.7;

    /// From each coupler's pulling face to the nearer truck centre.
    public double FrontOverhang => FrontOverhangGiven > 0.0 ? FrontOverhangGiven : (Length - Centres) * 0.5;
    public double RearOverhang => Length - Centres - FrontOverhang;

    public double Mass(bool loaded) => loaded && CanLoad ? MassLoaded : MassEmpty;

    public Livery? FindLivery(string? id) => Liveries.FirstOrDefault(l => l.Id == id) ?? Liveries.FirstOrDefault();

    /// The same vehicle with these lamps, horn and bell.
    public VehicleDef WithFixtures(IReadOnlyList<VehicleLamp> lamps, float3? horn, float3? bell) => new()
    {
        Id = Id, Replaces = Replaces, Name = Name, Kind = Kind, Builder = Builder, Built = Built, Type = Type, Length = Length, KnuckleNose = KnuckleNose,
        TruckCentres = TruckCentres, FrontOverhangGiven = FrontOverhangGiven, Axles = Axles, WheelDiameter = WheelDiameter,
        MassEmpty = MassEmpty, MassLoaded = MassLoaded, Power = Power, StartingTractiveEffort = StartingTractiveEffort,
        ContinuousTractiveEffort = ContinuousTractiveEffort, DynamicBrakeEffort = DynamicBrakeEffort, MaxSpeed = MaxSpeed,
        Liveries = Liveries, EngineSound = EngineSound, Lamps = lamps, Horn = horn, Bell = bell,
    };
}

/// Every vehicle the game knows: each vehicle.json under Content/Models, read once. Adding a vehicle needs no code.
public static class VehicleCatalog
{
    private static List<VehicleDef>? _all;
    private static Dictionary<string, VehicleDef>? _byId;

    public static IReadOnlyList<VehicleDef> All => _all ??= Load();

    public static VehicleDef? Find(string id)
    {
        if (_byId is null)
        {
            _byId = All.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
            // Old ids, for vehicles that have taken their place, unless a vehicle still has that id itself.
            foreach (var vehicle in All)
                foreach (var old in vehicle.Replaces)
                    _byId.TryAdd(old, vehicle);
        }

        return _byId.GetValueOrDefault(id);
    }

    public static IEnumerable<VehicleDef> OfKind(VehicleKind kind) => All.Where(v => v.Kind == kind);

    /// Forgets what was read, so the next look reads the files again.
    public static void Reload()
    {
        _all = null;
        _byId = null;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static List<VehicleDef> Load()
    {
        var root = ContentPath.Get("Models");
        var found = new List<VehicleDef>();
        if (!Directory.Exists(root))
            return found;
        var nose = KnuckleNose(root);

        foreach (var path in Directory.EnumerateFiles(root, "vehicle.json", SearchOption.AllDirectories))
        {
            try
            {
                var file = JsonSerializer.Deserialize<VehicleFile>(File.ReadAllText(path), Options)
                           ?? throw new InvalidDataException("empty");
                var def = file.ToDef(nose);
                if (def.Kind == VehicleKind.Locomotive)
                {
                    var (horn, bell) = HornAndBellOf(Path.GetDirectoryName(path)!, def);
                    def = def.WithFixtures(LampsOf(Path.GetDirectoryName(path)!, def), horn, bell);
                }
                if (found.Any(v => v.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Warn($"Vehicles: {path} repeats the id {def.Id}, left out");
                    continue;
                }

                var missing = def.Liveries.Where(l => !File.Exists(ContentPath.Model(l.Model))).Select(l => l.Model).ToList();
                foreach (var model in missing)
                    Log.Warn($"Vehicles: {def.Id}'s model {model} isn't there");
                found.Add(def);
            }
            catch (Exception e)
            {
                Log.Warn($"Vehicles: {path} unreadable ({e.Message})");
            }
        }

        found.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Log.Info($"Vehicles: {found.Count} in the catalog");
        return found;
    }

    /// A locomotive's horn and bell: the fixtures named so in assembly.json beside its vehicle.json, turned from Blender's axes
    /// into the model's as the lamps are; null for either it hasn't.
    private static (float3? Horn, float3? Bell) HornAndBellOf(string folder, VehicleDef def)
    {
        try
        {
            var assembly = Path.Combine(folder, "assembly.json");
            if (!File.Exists(assembly))
                return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(assembly));
            if (!doc.RootElement.TryGetProperty("fixtures", out var fixtures))
                return (null, null);
            float3? At(string name)
            {
                if (!fixtures.TryGetProperty(name, out var f) || !f.TryGetProperty("position", out var v))
                    return null;
                return new float3(v[0].GetSingle(), v[2].GetSingle(), -v[1].GetSingle());
            }

            return (At("Horn"), At("Bell"));
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicles: {def.Id}'s assembly.json horn and bell unreadable ({e.Message})");
            return (null, null);
        }
    }

    /// A locomotive's lamps: the fixtures in assembly.json beside its vehicle.json (in Blender's axes, Z up and the front
    /// towards -Y, turned here into the model's), or the usual places on a road engine if it hasn't any.
    private static List<VehicleLamp> LampsOf(string folder, VehicleDef def)
    {
        var lamps = new List<VehicleLamp>();
        try
        {
            var assembly = Path.Combine(folder, "assembly.json");
            if (File.Exists(assembly))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(assembly));
                if (doc.RootElement.TryGetProperty("fixtures", out var fixtures))
                {
                    foreach (var fixture in fixtures.EnumerateObject())
                    {
                        var name = fixture.Name;
                        var ditch = name.StartsWith("DitchLight", StringComparison.OrdinalIgnoreCase);
                        if (!ditch && !name.StartsWith("Headlight", StringComparison.OrdinalIgnoreCase))
                            continue;
                        static float3 Turned(JsonElement v) => new(v[0].GetSingle(), v[2].GetSingle(), -v[1].GetSingle());
                        var front = name.Contains("_Front", StringComparison.OrdinalIgnoreCase);
                        var side = name.EndsWith("_Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        lamps.Add(new VehicleLamp(ditch, front, side, Turned(fixture.Value.GetProperty("position")), Turned(fixture.Value.GetProperty("facing"))));
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicles: {def.Id}'s assembly.json lamps unreadable ({e.Message})");
            lamps.Clear();
        }

        if (lamps.Count > 0)
            return lamps;

        // Where they are on most road engines: the headlight high on the nose and on the long hood's end, the ditch lights
        // either side down on the pilots.
        var end = (float)(def.Length * 0.5 + def.KnuckleNose);
        foreach (var front in (ReadOnlySpan<bool>)[true, false])
        {
            var sign = front ? 1f : -1f;
            lamps.Add(new VehicleLamp(false, true == front, 0, new float3(0f, front ? 3.05f : 3.5f, sign * (end - 1.0f)), new float3(0f, 0f, sign)));
            for (var side = 0; side < 2; side++)
            {
                // Left of the end, looking out from it: +x at the front, -x at the rear.
                var x = (side == 0 ? 1f : -1f) * sign * 1.0f;
                lamps.Add(new VehicleLamp(true, front, side, new float3(x, 1.4f, sign * (end - 0.15f)), new float3(0f, 0f, sign)));
            }
        }

        return lamps;
    }

    /// The shared coupler's knuckle nose past its pulling face (every engine and car uses the one coupler).
    private static double KnuckleNose(string root)
    {
        const double measured = 0.098;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Couplers", "coupler.json")));
            return doc.RootElement.TryGetProperty("knuckleNose", out var value) ? value.GetDouble() : measured;
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicles: Couplers/coupler.json unreadable ({e.Message}), knuckles taken as {measured} m");
            return measured;
        }
    }

    /// vehicle.json as written, units in the names. Lengths are over the couplers' knuckle noses, as the models are drawn.
    private sealed class VehicleFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string>? Replaces { get; set; }
        public VehicleKind Kind { get; set; }
        public string? Builder { get; set; }
        public string? Built { get; set; }
        public string? Type { get; set; }
        public double LengthM { get; set; }
        public double TruckCentresM { get; set; }
        public double FrontOverhangM { get; set; }
        public int Axles { get; set; } = 4;
        public double WheelDiameterM { get; set; } = 0.914;
        public double MassEmptyKg { get; set; }
        public double MassLoadedKg { get; set; }
        public double PowerKw { get; set; }
        public double StartingTractiveEffortKn { get; set; }
        public double ContinuousTractiveEffortKn { get; set; }
        public double DynamicBrakeEffortKn { get; set; }
        public double MaxSpeedKmh { get; set; }
        public List<LiveryFile> Liveries { get; set; } = [];
        public EngineSoundDef? EngineSound { get; set; }

        public VehicleDef ToDef(double nose)
        {
            if (string.IsNullOrWhiteSpace(Id))
                throw new InvalidDataException("no id");
            if (LengthM <= 0.0)
                throw new InvalidDataException("no lengthM");
            if (MassEmptyKg <= 0.0)
                throw new InvalidDataException("no massEmptyKg");

            return new VehicleDef
            {
                Id = Id,
                Replaces = Replaces ?? [],
                Name = Name ?? Id,
                Kind = Kind,
                Builder = Builder ?? "",
                Built = Built ?? "",
                Type = Type ?? "",
                Length = LengthM - 2.0 * nose,
                KnuckleNose = nose,
                TruckCentres = TruckCentresM,
                FrontOverhangGiven = FrontOverhangM > 0.0 ? FrontOverhangM - nose : 0.0,
                Axles = Axles,
                WheelDiameter = WheelDiameterM,
                MassEmpty = MassEmptyKg,
                MassLoaded = Math.Max(MassLoadedKg, MassEmptyKg),
                Power = PowerKw * 1000.0,
                StartingTractiveEffort = StartingTractiveEffortKn * 1000.0,
                ContinuousTractiveEffort = ContinuousTractiveEffortKn * 1000.0,
                DynamicBrakeEffort = DynamicBrakeEffortKn * 1000.0,
                MaxSpeed = MaxSpeedKmh / 3.6,
                Liveries = Liveries.Where(l => !string.IsNullOrWhiteSpace(l.Id) && !string.IsNullOrWhiteSpace(l.Model))
                    .Select(l => new Livery { Id = l.Id!, Name = l.Name ?? l.Id!, Model = l.Model! }).ToList(),
                EngineSound = EngineSound is { Idle.Length: > 0, Run.Length: > 0 } ? EngineSound : null,
            };
        }
    }

    private sealed class LiveryFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Model { get; set; }
    }
}
