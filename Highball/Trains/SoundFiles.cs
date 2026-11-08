using RailWerkz.Core.Audio;
using RailWerkz.Core.IO;

namespace Highball.Trains;

/// The trains' sound files (paths under Content/Audio), loaded the first time anything asks for one, folded to mono so
/// they can be placed on the train. A file that won't load is warned about once and stays silent.
public static class SoundFiles
{
    private static readonly HashSet<string> Tried = [];

    /// A sound to loop: its name in the audio engine, or null.
    public static string? Loop(string? path) => Get(path, "loop:", name => AudioEngine.LoadSeamless(name, path!, mono: true));

    /// A sound to play once.
    public static string? Shot(string? path) => Get(path, "shot:", name => AudioEngine.Load(name, path!, mono: true));

    private static string? Get(string? path, string kind, Func<string, bool> load)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var name = kind + path;
        if (Tried.Add(name))
            load(name);
        return AudioEngine.Has(name) ? name : null;
    }

    /// Every WAV in a folder under Content/Audio whose name starts so, as paths for Loop and Shot.
    public static string[] Matching(string folder, string prefix)
    {
        var path = ContentPath.Audio(folder);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, prefix + "*.wav").Order(StringComparer.OrdinalIgnoreCase).Select(f => Path.Combine(folder, Path.GetFileName(f))).ToArray()
            : [];
    }
}
