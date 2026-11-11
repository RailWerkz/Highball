using System.Text.Json;
using System.Text.Json.Serialization;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// The regions the world is exported by: a pack for each, and the area a self-hosted server runs.
[JsonConverter(typeof(JsonStringEnumConverter<Region>))]
public enum Region { West, Midwest, South, Northeast }

/// Which region each built tile belongs to, set by hand in the Planner: Regions.json in the world's folder, tile
/// ("I_J") to region. Tiles not in it belong to none yet.
public sealed class RouteRegions
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly Dictionary<TileId, Region> _tiles = [];

    public IReadOnlyDictionary<TileId, Region> Tiles => _tiles;

    public Region? Of(TileId tile) => _tiles.TryGetValue(tile, out var region) ? region : null;

    public int Count(Region region) => _tiles.Values.Count(r => r == region);

    /// Puts tiles in a region, or takes them out of any (region null).
    public void Set(IEnumerable<TileId> tiles, Region? region)
    {
        foreach (var tile in tiles)
            if (region is { } r)
                _tiles[tile] = r;
            else
                _tiles.Remove(tile);
    }

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Regions.json");

    public void Save(RouteInfo route)
    {
        Directory.CreateDirectory(route.Folder);
        var map = _tiles.OrderBy(t => t.Key.J).ThenBy(t => t.Key.I).ToDictionary(t => t.Key.ToString(), t => t.Value);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(map, Options));
    }

    public static RouteRegions Load(RouteInfo route)
    {
        var regions = new RouteRegions();
        var path = PathIn(route);
        if (!File.Exists(path))
            return regions;
        try
        {
            foreach (var (key, region) in JsonSerializer.Deserialize<Dictionary<string, Region>>(File.ReadAllText(path), Options) ?? [])
                if (TileId.TryParse(key, out var tile))
                    regions._tiles[tile] = region;
        }
        catch (Exception e)
        {
            Log.Warn($"Regions: {e.Message}");
        }

        return regions;
    }
}
