using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// A route's manifest: Content/Routes/NAME/Route.json. The folder holds everything else the route is built from
/// (terrain tiles, the road and rail network, scenery), which the Route Builder writes.
public sealed class RouteInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public LatLon Origin { get; set; } = new();
    public double LengthKm { get; set; }
    public string Era { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// The tile layout, fixed when the first area is chosen (with the origin); null until then.
    public RouteGrid? Grid { get; set; }

    /// The folder name under Content/Routes.
    [JsonIgnore]
    public string Id { get; private set; } = "";

    [JsonIgnore]
    public string Folder => _folder ?? Path.Combine(Root, Id);

    private string? _folder;

    /// The installed pack it's played from (its tiles read from there), or null for a route being built.
    [JsonIgnore]
    public PackSource? Pack { get; private set; }

    /// A pack's route: its manifest and track files in folder (where they've been unpacked), its tiles in the pack. With no pack,
    /// the folder alone, as a route of its own (what PackSource hands CellStore).
    public static RouteInfo FromPack(string folder, string id, PackSource? pack)
    {
        var route = JsonSerializer.Deserialize<RouteInfo>(File.ReadAllText(Path.Combine(folder, "Route.json")), Options)
                    ?? throw new InvalidDataException($"{folder}: no Route.json");
        route.Id = id;
        route._folder = folder;
        route.Pack = pack;
        return route;
    }

    /// One built tile: from the pack if it's played from one, else from its folder.
    public RouteTile LoadTile(TileId tile) => Pack?.Load(tile) ?? RouteTile.Load(TileFolder(tile));

    public RouteTile.Info LoadTileInfo(TileId tile) => Pack?.LoadInfo(tile) ?? RouteTile.LoadInfo(TileFolder(tile));

    [JsonIgnore]
    public RouteProjection? Projection => Grid is null ? null : _projection ??= Zone is { } zone ? new RouteProjection(zone) : new RouteProjection(new GeoPoint(Origin.Latitude, Origin.Longitude));

    /// The UTM zone the tiles are laid out on; null for a route made before zones, laid out around its own origin.
    public int? Zone { get; set; }

    private RouteProjection? _projection;

    public sealed class LatLon
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// The repository's Content/Routes when running from a checkout (where the builder writes), else beside the build.
    public static string Root => Path.Combine(ContentPath.SourceRoot, "Routes");

    /// Fixes the origin and the tile layout. Only once: moving them would move every tile already built.
    public void SetOrigin(GeoPoint origin)
    {
        if (Grid is not null)
            return;

        Origin = new LatLon { Latitude = Math.Round(origin.Lat, 6), Longitude = Math.Round(origin.Lon, 6) };
        Zone = RouteProjection.ZoneOf(origin.Lon);
        Grid = new RouteGrid();
        _projection = null;
        Save();
    }

    public string TileFolder(TileId tile) => Path.Combine(Folder, "Tiles", tile.ToString());

    /// The tiles built so far.
    public HashSet<TileId> BuiltTiles() => Pack is not null ? Pack.Built() : [.. TileList().Keys];

    /// Every built tile and its heights file's size and time (what the cells' stamp takes, CellStore.StampOf): kept in Cache/Tiles.bin
    /// with the time of the Tiles folder, so a route of 50,000 tiles doesn't list every tile's folder at each open (seconds). A tile
    /// added or taken out changes the folder's time; one built again over another (RouteTile.Save) deletes the list. Empty for a pack.
    public IReadOnlyDictionary<TileId, (long Length, long Ticks)> TileList()
    {
        var tiles = new Dictionary<TileId, (long, long)>();
        var root = Path.Combine(Folder, "Tiles");
        if (Pack is not null || !Directory.Exists(root))
            return tiles;
        var list = TileListPath(Folder);
        var stamp = Directory.GetLastWriteTimeUtc(root).Ticks;
        try
        {
            if (File.Exists(list))
            {
                using var r = new BinaryReader(File.OpenRead(list));
                if (r.ReadInt32() == TileListMagic && r.ReadInt64() == stamp)
                {
                    for (var n = r.ReadInt32(); n > 0; n--)
                        tiles[new TileId(r.ReadInt32(), r.ReadInt32())] = (r.ReadInt64(), r.ReadInt64());
                    return tiles;
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            tiles.Clear();
        }

        // (One listing of every tile's files, not a file asked after per folder.)
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heights = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }))
            if (file.Name == "Tile.json")
                built.Add(file.Directory!.Name);
            else if (file.Name == "Heights.bin.gz")
                heights[file.Directory!.Name] = (file.Length, file.LastWriteTimeUtc.Ticks);
        foreach (var name in built)
            if (TileId.TryParse(name, out var id))
                tiles[id] = heights.GetValueOrDefault(name, (-1L, 0L));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(list)!);
            var temporary = $"{list}.{Environment.CurrentManagedThreadId}.tmp";
            using (var w = new BinaryWriter(File.Create(temporary)))
            {
                w.Write(TileListMagic);
                w.Write(stamp);
                w.Write(tiles.Count);
                foreach (var (id, (length, ticks)) in tiles)
                {
                    w.Write(id.I);
                    w.Write(id.J);
                    w.Write(length);
                    w.Write(ticks);
                }
            }

            File.Move(temporary, list, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Route {Name}: the tiles' list not kept ({e.Message})");
        }

        return tiles;
    }

    private const int TileListMagic = 0x54534C54; // "TLST"

    /// Where a route's list of built tiles is kept (TileList).
    public static string TileListPath(string routeFolder) => Path.Combine(routeFolder, "Cache", "Tiles.bin");

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Route.json"), JsonSerializer.Serialize(this, Options));
    }

    /// A new, empty route in a folder named after it.
    public static RouteInfo Create(string name)
    {
        var id = new string(name.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (id.Length == 0)
            id = "Route";

        var unique = id;
        for (var n = 2; Directory.Exists(Path.Combine(Root, unique)); n++)
            unique = $"{id}{n}";

        var route = new RouteInfo { Name = name.Trim(), Author = RailWerkz.Core.Settings.Settings.Current.DriverName, Version = "0.0.1", Id = unique };
        route.Save();
        Log.Info($"Route {route.Name} created in {route.Folder}");
        return route;
    }

    /// A new, empty route in place of this one (its folder already cleared): the same folder and name, nothing built, its
    /// origin and grid unset till the first area's picked.
    public RouteInfo Fresh()
    {
        var route = new RouteInfo { Name = Name, Author = Author, Version = "0.0.1", Id = Id, _folder = _folder };
        route.Save();
        return route;
    }

    /// The folder of the one world the Route Builder edits.
    public const string WorldId = "World";

    /// The one world the Route Builder edits: Content/Routes/World, made the first time it's asked for.
    public static RouteInfo World()
    {
        var manifest = Path.Combine(Root, WorldId, "Route.json");
        if (File.Exists(manifest))
        {
            var world = JsonSerializer.Deserialize<RouteInfo>(File.ReadAllText(manifest), Options) ?? throw new InvalidDataException($"{manifest} is empty");
            world.Id = WorldId;
            if (world.Name.Length == 0)
                world.Name = "North America";
            return world;
        }

        var created = new RouteInfo { Name = "North America", Author = RailWerkz.Core.Settings.Settings.Current.DriverName, Version = "0.0.1", Id = WorldId };
        created.Save();
        Log.Info($"World created in {created.Folder}");
        return created;
    }

    /// Every route under Content/Routes with a readable manifest, by name.
    public static List<RouteInfo> All()
    {
        var routes = new List<RouteInfo>();
        if (!Directory.Exists(Root))
            return routes;

        foreach (var folder in Directory.GetDirectories(Root))
        {
            var manifest = Path.Combine(folder, "Route.json");
            if (!File.Exists(manifest))
                continue;

            try
            {
                var route = JsonSerializer.Deserialize<RouteInfo>(File.ReadAllText(manifest), Options);
                if (route is null)
                    continue;

                route.Id = Path.GetFileName(folder);
                if (route.Name.Length == 0)
                    route.Name = route.Id;
                routes.Add(route);
            }
            catch (Exception e)
            {
                Log.Warn($"Route {Path.GetFileName(folder)}: {e.Message}");
            }
        }

        return [.. routes.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
