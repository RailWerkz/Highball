namespace RailWerkz.Core.Audio;

public enum MusicTrack
{
    None,
    Menu,
    Credits,
    /// Loading a route (it carries on through the load).
    Loading,
    /// Background music while driving, if the player wants any.
    Driving,
}

/// Which files in Content/Audio/Music each track plays: every MP3 in the folder named after it (Music/Menu/*.mp3),
/// in name order. A looping track runs through its files and starts again. An empty or missing folder is silence.
public static class Music
{
    public readonly record struct Track(string[] Files, bool Loop);

    private static readonly Dictionary<MusicTrack, Track> Cache = [];

    public static Track For(MusicTrack track)
    {
        if (track == MusicTrack.None)
            return new([], false);

        if (Cache.TryGetValue(track, out var cached))
            return cached;

        var folder = IO.ContentPath.Audio(Path.Combine("Music", track.ToString()));
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.mp3").Order(StringComparer.OrdinalIgnoreCase).Select(f => $"{track}/{Path.GetFileName(f)}").ToArray()
            : [];

        return Cache[track] = new Track(files, true);
    }
}
