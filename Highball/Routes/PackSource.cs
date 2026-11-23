using System.Text.Json;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// An installed client pack opened to play: its tiles and their shaped ground read from the pack as they're needed, and its
/// track files (the route's cells, network, objects and settings) unpacked into a cache folder that stands in for the route's
/// folder, so the track is read from the cells as the editor reads them: nothing is solved, graded or shaped (TerrainSource.OpenPack).
/// A pack made with cells or a pack layout this game doesn't read is refused (PackOutdatedException), never laid out here.
public sealed class PackSource : IDisposable
{
    private readonly FileStream _file;
    private readonly Lock _lock = new();
    private readonly Dictionary<TileId, PackFile.TileEntry> _tiles;
    private readonly Dictionary<TileId, PackFile.FileEntry> _ground = [];

    public PackFile.Header Header { get; }
    public string Path { get; }

    /// What the pack was made with (Pack.json).
    public PackFile.About About { get; }

    /// The route as the pack carries it, reading its tiles from here.
    public RouteInfo Route { get; }

    /// The same folder as a route of its own, for CellStore, which reads a route's cells only when it isn't a pack's (the editor's
    /// open never takes a pack's): the cells unpacked are this pack's own, made by the export.
    internal RouteInfo CellRoute { get; }

    /// The cells' index, read and checked when the pack's opened.
    internal CellStore.Index Cells { get; }

    /// Where installed packs live: Content/Packs/WORLD/REGION.hbpack (the export installs them there too).
    public static string Root => System.IO.Path.Combine(ContentPath.SourceRoot, "Packs");

    private PackSource(string path)
    {
        Path = path;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            Header = PackFile.ReadHeader(_file);
        }
        catch (InvalidDataException e)
        {
            _file.Dispose();
            throw new PackOutdatedException(path, e.Message);
        }

        _tiles = Header.Tiles.ToDictionary(t => t.Id);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var world = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "World";
        try
        {
            About = ReadAbout(path);
            var cache = Unpack(path, world, name);
            Route = RouteInfo.FromPack(cache, $"{world}.{name}", this);
            if (Route.Zone != Header.Zone)
                throw new InvalidDataException($"{path}: its route is on zone {Route.Zone}, its tiles on {Header.Zone}");
            CellRoute = RouteInfo.FromPack(cache, $"{world}.{name}", null);

            // The cells checked now, so a pack this game can't read is refused here, plainly, not half opened.
            Cells = CellStore.ReadIndex(CellRoute) ?? throw new PackOutdatedException(path, $"its cells are of another format than this game's ({CellStore.FormatVersion})");
            if (Cells.Stale(Cells.Stamp) is { } why)
                throw new PackOutdatedException(path, $"its cells were {why}");
            Log.Info($"Pack {world}/{name}: {_tiles.Count} tiles ({_ground.Count} with shaped ground), {Cells.Cells.Count} cells, opened in {watch.ElapsedMilliseconds} ms " +
                     $"(made by {About.Game}, {About.Exported}; unpacked to {cache})");
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// The pack's Pack.json, checked against what this game reads.
    private PackFile.About ReadAbout(string path)
    {
        if (Header.Files.FirstOrDefault(f => f.Name == PackFile.AboutName) is not { } entry)
            throw new PackOutdatedException(path, $"it's a pack of version {Header.Version}, made before packs carried the finished track");
        var about = JsonSerializer.Deserialize<PackFile.About>(PackFile.ReadBlob(_file, entry.Offset, entry.Length))
                    ?? throw new PackOutdatedException(path, "its Pack.json is empty");
        if (about.Pack != PackFile.Version)
            throw new PackOutdatedException(path, $"it's a pack of version {about.Pack}, this game reads {PackFile.Version}");
        if (about.CellFormat != CellStore.FormatVersion)
            throw new PackOutdatedException(path, $"its cells are of format {about.CellFormat}, this game reads {CellStore.FormatVersion}");
        if (about.CellBuild != CellStore.BuildVersion)
            throw new PackOutdatedException(path, $"its track was built by build version {about.CellBuild}, this game's is {CellStore.BuildVersion}");
        if (about.Ground != PackFile.GroundVersion)
            throw new PackOutdatedException(path, $"its shaped ground is of version {about.Ground}, this game reads {PackFile.GroundVersion}");
        return about;
    }

    /// The track files unpacked into a cache folder of this pack's own (by its path, so two checkouts' packs of one name don't
    /// share one), every time the pack's changed since, checked against the hashes the pack keeps; the shaped ground stays in
    /// the pack. Anything else in the folder (written while playing) is cleared each time.
    private string Unpack(string path, string world, string name)
    {
        var info = new FileInfo(path);
        var full = System.IO.Path.GetFullPath(path);
        var key = Convert.ToHexString(PackFile.Hash(System.Text.Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..8];
        var cache = System.IO.Path.Combine(ContentPath.UserData, "PackCache", world, $"{name}-{key}");
        var marker = System.IO.Path.Combine(cache, "Unpacked.txt");
        var identity = $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{PackFile.Version}";
        var files = new List<PackFile.FileEntry>();
        foreach (var entry in Header.Files)
        {
            if (entry.Name.StartsWith(PackFile.GroundFolder, StringComparison.Ordinal))
            {
                if (TileId.TryParse(System.IO.Path.GetFileNameWithoutExtension(entry.Name), out var id))
                    _ground[id] = entry;
                continue;
            }

            files.Add(entry);
        }

        var root = System.IO.Path.GetFullPath(cache) + System.IO.Path.DirectorySeparatorChar;
        string Target(PackFile.FileEntry entry)
        {
            // (A file's name is relative to the route's folder, perhaps in a folder of its own: Cells/ and Network/ have one file a cell.)
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(cache, entry.Name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{path}: a track file outside the route's folder ({entry.Name})");
            return target;
        }

        var keep = files.Select(Target).Append(System.IO.Path.GetFullPath(marker)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = File.Exists(marker) && File.ReadAllText(marker) == identity;
        if (Directory.Exists(cache))
            foreach (var old in Directory.GetFiles(cache, "*", SearchOption.AllDirectories))
                if (!current || !keep.Contains(System.IO.Path.GetFullPath(old)))
                    File.Delete(old);

        if (current && files.All(f => File.Exists(Target(f))))
            return cache;

        Directory.CreateDirectory(cache);
        File.Delete(marker);
        var bytes = 0L;
        foreach (var entry in files)
        {
            var target = Target(entry);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var data = PackFile.ReadBlob(_file, entry.Offset, entry.Length);
            if (!PackFile.Hash(data).AsSpan().SequenceEqual(entry.Hash))
                throw new InvalidDataException($"{path}: {entry.Name} is damaged (its hash isn't the one the pack was made with)");
            File.WriteAllBytes(target, data);
            bytes += data.Length;
        }

        File.WriteAllText(marker, identity);
        Log.Info($"Pack {world}/{name}: {files.Count} track files ({bytes / 1048576.0:0.0} MB) unpacked to {cache}");
        return cache;
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

    /// One tile as the route keeps it: heights (with sculpting already in), cover and what it is; the ground as it came, not shaped
    /// for the track (LoadShaped).
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

    /// One tile with its ground shaped for the track as the export found it (the editor's Conform, kept in the pack).
    public RouteTile LoadShaped(TileId id)
    {
        var tile = Load(id);
        if (_ground.TryGetValue(id, out var entry))
        {
            byte[] blob;
            lock (_lock)
                blob = PackFile.ReadBlob(_file, entry.Offset, entry.Length);
            PackFile.ApplyGround(blob, tile.Heights);
        }

        return tile;
    }

    public RouteTile.Info LoadInfo(TileId id) => Load(id).Meta;

    private RouteTile.Info Info(PackFile.Tile tile) => new()
    {
        I = tile.Id.I, J = tile.Id.J, Samples = Header.Samples, Spacing = Header.TileSize / (Header.Samples - 1), CoverCells = Header.CoverCells,
        MinHeight = tile.MinHeight, MaxHeight = tile.MaxHeight, HeightSource = tile.HeightSource,
    };

    public void Dispose() => _file.Dispose();
}

/// A pack this game can't read as it is: made by another version of the game, so it needs exporting again.
public sealed class PackOutdatedException(string path, string why)
    : Exception($"{System.IO.Path.GetFileName(path)} needs re-exporting: {why}. Export it again from the Route Builder (File > Export for Players and Servers).");
