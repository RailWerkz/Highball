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

    /// A pack's route: its manifest and track files in folder (where they've been unpacked), its tiles in the pack.
    public static RouteInfo FromPack(string folder, string id, PackSource pack)
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
    public HashSet<TileId> BuiltTiles()
    {
        if (Pack is not null)
            return Pack.Built();
        var tiles = new HashSet<TileId>();
        var root = Path.Combine(Folder, "Tiles");
        if (!Directory.Exists(root))
            return tiles;

        foreach (var folder in Directory.GetDirectories(root))
            if (TileId.TryParse(Path.GetFileName(folder), out var id) && File.Exists(Path.Combine(folder, "Tile.json")))
                tiles.Add(id);
        return tiles;
    }

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
