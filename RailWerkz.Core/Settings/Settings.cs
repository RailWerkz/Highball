using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace RailWerkz.Core.Settings;

public enum UnitSystem { Imperial, Metric }

/// Everything the player chooses and everything the game remembers, saved as JSON in the user's data folder.
public sealed class Settings
{
    // Profile
    public string DriverName { get; set; } = "ENGINEER";

    // Display
    public string Accent { get; set; } = "SIGNAL";
    public bool Fullscreen { get; set; }
    /// The window as it was last left, when not fullscreen or maximized; no width until there's been one.
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    public bool VSync { get; set; } = true;
    public float UiScale { get; set; } = 1f;
    public bool ReduceMotion { get; set; }

    // Graphics
    public int Quality { get; set; } = 2; // 0 LOW, 1 MEDIUM, 2 HIGH, 3 ULTRA
    public float ViewDistance { get; set; } = 8000f;
    public float FieldOfView { get; set; } = 60f;
    public string Skybox { get; set; } = "Procedural";
    /// Samples per pixel in the 3D view: 1 (off), 2, 4 or 8.
    public int Msaa { get; set; } = 4;

    // Audio
    public float MasterVolume { get; set; } = 0.8f;
    public float MusicVolume { get; set; } = 0.55f;
    public float EffectsVolume { get; set; } = 0.9f;
    public float VoiceVolume { get; set; } = 1f;
    public string HoverSound { get; set; } = "GLASS";
    public string SelectSound { get; set; } = "BELLS";
    public string BackSound { get; set; } = "BELLS";
    public string TypingSound { get; set; } = "NOISE";

    // Controls
    public float Sensitivity { get; set; } = 1f;
    public bool InvertY { get; set; }

    // Simulation
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;
    public bool AssistedBraking { get; set; }

    // Multiplayer
    public string LastHost { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 27960;

    // Progress
    public string LastRoute { get; set; } = "TheRoute";
    public double MilesDriven { get; set; }
    public double SecondsDriven { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// Another settings file instead of the player's (--settingsfile, for testing).
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(ContentPath.UserData, "Settings.json");

    public static Settings Current { get; private set; } = new();

    /// Test runs (dev flags) never write the player's settings.
    public static bool ReadOnly { get; set; }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Warn($"Settings unreadable, starting fresh: {e.Message}");
            Current = new Settings();
        }
    }

    public static void Save()
    {
        if (ReadOnly)
            return;

        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception e)
        {
            Log.Warn($"Could not save settings: {e.Message}");
        }
    }
}
