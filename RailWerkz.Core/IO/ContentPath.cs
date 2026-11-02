namespace RailWerkz.Core.IO;

/// Finds Content/ next to the executable (a build) or above it (running from the repository) and builds paths into it.
public static class ContentPath
{
    private static string? _root;

    /// The game's name, set once at startup: it names the user data folder and the log.
    public static string AppName { get; set; } = "RailWerkz";

    public static string Root => _root ??= Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Content");

            if (Directory.Exists(Path.Combine(candidate, "Fonts")))
                return candidate;

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Content");
    }

    /// The repository's Content when running from a checkout (so tools save where a rebuild won't overwrite),
    /// otherwise the same as Root.
    public static string SourceRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
                dir = dir.Parent;
            return dir is not null ? Path.Combine(dir.FullName, "Content") : Root;
        }
    }

    public static string Get(string relative) => Path.Combine(Root, relative);
    public static string Audio(string name) => Path.Combine(Root, "Audio", name);
    public static string Font(string name) => Path.Combine(Root, "Fonts", name);
    public static string Model(string name) => Path.Combine(Root, "Models", name);
    public static string Shader(string name) => Path.Combine(Root, "Shaders", name);
    public static string Texture(string name) => Path.Combine(Root, "Textures", name);
    public static string Route(string name) => Path.Combine(Root, "Routes", name);

    /// Where settings and saves live: %APPDATA%/AppName (or the platform's equivalent).
    public static string UserData
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
