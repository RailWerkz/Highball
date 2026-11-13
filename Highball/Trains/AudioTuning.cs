using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// How the trains sound, as tuned by ear in the Audio Editor (Builder/AudioEditor.cs) and kept in
/// Content/Audio/AudioTuning.json: the mix (each group's level and the bus compressor over them all), each locomotive's
/// sounds (levels, pitch, and where on it each sound comes from), and the running gear (levels, and when and how often
/// the knocks come). Read live every frame, so a change is heard at once. What it doesn't say is as the game had it, so a
/// missing or empty file sounds the same as no file.
public sealed class AudioTuning
{
    /// The groups' levels, and the compressor that holds the whole of them down when they pile up (a lash-up of ten, all
    /// working, with their wheels): it measures how loud all the train sounds are together where they're heard and turns
    /// everything down past the threshold, by the ratio, easing in over the attack and out over the release.
    public sealed class MixTuning
    {
        public float Master { get; set; } = 1f;
        public float Engines { get; set; } = 1f;
        public float Horn { get; set; } = 1f;
        public float Bell { get; set; } = 1f;
        public float AirCompressor { get; set; } = 1f;
        public float RunningGear { get; set; } = 1f;

        public bool Compressor { get; set; } = true;
        public float ThresholdDb { get; set; } = -10f;
        public float Ratio { get; set; } = 3f;
        public float AttackMs { get; set; } = 40f;
        public float ReleaseMs { get; set; } = 500f;
        public float MakeupDb { get; set; }
    }

    /// The wheels, trucks and motors: each sound's level, and what sets off the ones that come now and then.
    public sealed class GearTuning
    {
        public float Rolling { get; set; } = 1f;
        public float SlowRolling { get; set; } = 0.7f;
        public float Clank { get; set; } = 1f;
        public float Flange { get; set; } = 1f;
        public float Squeal { get; set; } = 1f;
        public float Motors { get; set; } = 0.8f;
        public float Slip { get; set; } = 1f;

        /// Axle clanks over the joints: every so many metres at random, between these, once going faster than ClankMinSpeed (m/s).
        public float ClankEveryMin { get; set; } = 23f;
        public float ClankEveryMax { get; set; } = 69f;
        public float ClankMinSpeed { get; set; } = 1f;

        /// Flanges screeching on a curve: full at FlangeRadius (metres) and tighter, from FlangeOnset of that; a chance each
        /// second of FlangeChance at full, once faster than FlangeMinSpeed (m/s).
        public float FlangeRadius { get; set; } = 500f;
        public float FlangeOnset { get; set; } = 0.15f;
        public float FlangeChance { get; set; } = 0.455f;
        public float FlangeMinSpeed { get; set; } = 2f;

        /// Brake shoes squealing as it slows: braking harder than SquealForce (newtons a car) between these speeds (m/s), a
        /// chance each second of SquealChance.
        public float SquealForce { get; set; } = 2000f;
        public float SquealMinSpeed { get; set; } = 0.3f;
        public float SquealMaxSpeed { get; set; } = 7f;
        public float SquealChance { get; set; } = 0.325f;
    }

    /// One locomotive's sounds: each one's level, the engine's pitch, and where each comes from on it, in the model's axes
    /// (metres: x across, y up from the rail, z towards its front; null for where the game puts it).
    public sealed class VehicleTuning
    {
        public float Engine { get; set; } = 1f;
        public float Chug { get; set; } = 1f;
        public float Turbo { get; set; } = 1f;
        public float Startup { get; set; } = 1f;
        public float AirCompressor { get; set; } = 1f;
        public float BrakeFan { get; set; } = 1f;
        public float Horn { get; set; } = 1f;
        public float Bell { get; set; } = 1f;
        public float EnginePitch { get; set; } = 1f;

        public float[]? EngineAt { get; set; }
        public float[]? AirCompressorAt { get; set; }
        public float[]? BrakeFanAt { get; set; }
        public float[]? HornAt { get; set; }
        public float[]? BellAt { get; set; }
    }

    public MixTuning Mix { get; set; } = new();
    public GearTuning Gear { get; set; } = new();
    public Dictionary<string, VehicleTuning> Vehicles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly VehicleTuning Untuned = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string File_ = "Audio/AudioTuning.json";

    /// The tuning in use.
    public static AudioTuning Current { get; private set; } = Load();

    /// Changed since it was last saved or read.
    public static bool Unsaved { get; set; }

    /// A locomotive's tuning to read; the game's own where it has none.
    public VehicleTuning For(string vehicle) => Vehicles.TryGetValue(vehicle, out var tuning) ? tuning : Untuned;

    /// A locomotive's tuning to change, made if it has none.
    public VehicleTuning Edit(string vehicle)
    {
        if (!Vehicles.TryGetValue(vehicle, out var tuning))
            Vehicles[vehicle] = tuning = new VehicleTuning();
        return tuning;
    }

    private static AudioTuning Load()
    {
        var path = ContentPath.Get(File_);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AudioTuning>(File.ReadAllText(path), Options) ?? new AudioTuning();
                loaded.Vehicles = new Dictionary<string, VehicleTuning>(loaded.Vehicles, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Audio tuning: {path} unreadable ({e.Message}), the game's own used");
        }

        return new AudioTuning();
    }

    /// Reads the file again, throwing away what's been changed since.
    public static void Revert()
    {
        Current = Load();
        Unsaved = false;
    }

    /// Back to the game's own sound: nothing tuned. Not saved till Save.
    public static void Reset()
    {
        Current = new AudioTuning();
        Unsaved = true;
    }

    /// Writes it into the repository's Content (so it's kept and built in) and the running copy beside the game.
    public static bool Save()
    {
        try
        {
            var text = JsonSerializer.Serialize(Current, Options);
            foreach (var root in new[] { ContentPath.SourceRoot, ContentPath.Root }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(root, File_);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }

            Unsaved = false;
            Log.Info($"Audio tuning: saved to {Path.Combine(ContentPath.SourceRoot, File_)}");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Audio tuning: couldn't save ({e.Message})");
            return false;
        }
    }

    /// A position as kept, or null.
    public static float3? Point(float[]? p) => p is { Length: 3 } ? new float3(p[0], p[1], p[2]) : null;

    public static float[] Keep(float3 p) => [MathF.Round(p.X, 3), MathF.Round(p.Y, 3), MathF.Round(p.Z, 3)];
}
