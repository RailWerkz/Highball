using System.Text.Json;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Screens;

/// Content/Changelog.json: the releases, newest first, shown on the main menu.
public static class Changelog
{
    public sealed class Release
    {
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
        public List<string> Changes { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<Release> Load()
    {
        var path = ContentPath.Get("Changelog.json");
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Release>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (Exception e)
        {
            Log.Warn($"Changelog: {e.Message}");
            return [];
        }
    }
}
