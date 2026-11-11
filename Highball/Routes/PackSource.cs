using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// An installed client pack opened to play: its tiles read from the pack as they're needed, and its track files (small)
/// unpacked into a cache folder that stands in for the route's folder, so everything that lays the track out reads them
/// as it would a route's. Anything written while playing (the solved track, junction markers) lands in that cache, never
/// in the pack or in a route being edited.
public sealed class PackSource : IDisposable
{
    private readonly FileStream _file;
    private readonly Lock _lock = new();
    private readonly Dictionary<TileId, PackFile.TileEntry> _tiles;

    public PackFile.Header Header { get; }
    public string Path { get; }

    /// The route as the pack carries it, reading its tiles from here.
    public RouteInfo Route { get; }

    /// Where installed packs live: Content/Packs/WORLD/REGION.hbpack (the export installs them there too).
    public static string Root => System.IO.Path.Combine(ContentPath.SourceRoot, "Packs");

    private PackSource(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        Header = PackFile.ReadHeader(_file);
        _tiles = Header.Tiles.ToDictionary(t => t.Id);

        // The track files, unpacked fresh each time into a cache folder of this pack's own.
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var world = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "World";
        var cache = System.IO.Path.Combine(ContentPath.UserData, "PackCache", world, name);
        if (Directory.Exists(cache))
            Directory.Delete(cache, true);
        Directory.CreateDirectory(cache);
        foreach (var entry in Header.Files)
            File.WriteAllBytes(System.IO.Path.Combine(cache, entry.Name), PackFile.ReadBlob(_file, entry.Offset, entry.Length));

        Route = RouteInfo.FromPack(cache, $"{world}.{name}", this);
        if (Route.Zone != Header.Zone)
            throw new InvalidDataException($"{path}: its route is on zone {Route.Zone}, its tiles on {Header.Zone}");
        Log.Info($"Pack {world}/{name}: {_tiles.Count} tiles, {Header.Files.Count} track files unpacked to {cache}");
    }

    public static PackSource Open(string path) => new(path);

    /// Every installed pack: (world, region, path), by world then region.
    public static List<(string World, string Region, string Path)> Installed()
    {
        if (!Directory.Exists(Root))
            return [];
        return [.. Directory.GetDirectories(Root).SelectMany(world => Directory.GetFiles(world, "*.hbpack")
                .Select(pack => (System.IO.Path.GetFileName(world), System.IO.Path.GetFileNameWithoutExtension(pack), pack)))
            .OrderBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item2, StringComparer.OrdinalIgnoreCase)];
    }

    public HashSet<TileId> Built() => [.. _tiles.Keys];

    /// One tile as the route keeps it: heights (with sculpting already in), cover and what it is.
    public RouteTile Load(TileId id)
    {
        if (!_tiles.TryGetValue(id, out var entry))
            throw new FileNotFoundException($"tile {id} isn't in {Path}");
        byte[] blob;
        lock (_lock)
            blob = PackFile.ReadBlob(_file, entry.Offset, entry.Length);
        var tile = PackFile.DecodeTile(Header, entry, blob);
        return new RouteTile(Info(tile), tile.Heights, tile.Cover);
    }

    public RouteTile.Info LoadInfo(TileId id) => Load(id).Meta;

    private RouteTile.Info Info(PackFile.Tile tile) => new()
    {
        I = tile.Id.I, J = tile.Id.J, Samples = Header.Samples, Spacing = Header.TileSize / (Header.Samples - 1), CoverCells = Header.CoverCells,
        MinHeight = tile.MinHeight, MaxHeight = tile.MaxHeight, HeightSource = tile.HeightSource,
    };

    public void Dispose() => _file.Dispose();
}
