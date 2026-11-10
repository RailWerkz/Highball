using System.Text.Json;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// A cut left on a route, as saved: its vehicles from the front (paint, which way round, load, name), where its front
/// stands (on the map, and the way it faces along the track there), each car's handbrake, each coupling's state, and
/// whether it was the one being driven.
public sealed record PlacedCut(
    string Name,
    double Lat,
    double Lon,
    double Heading,
    List<ConsistCar> Cars,
    List<bool>? Handbrakes = null,
    List<bool>? Coupled = null,
    List<bool>? Hoses = null,
    bool Controlled = false,
    List<double[]?>? Supplies = null);

/// The stock left on a route: Stock.json in its folder.
public static class RouteStock
{
    private static string PathIn(Routes.RouteInfo route) => Path.Combine(route.Folder, "Stock.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<PlacedCut> Load(Routes.RouteInfo route)
    {
        try
        {
            var path = PathIn(route);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PlacedCut>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e)
        {
            Log.Warn($"Placed stock: {e.Message}");
            return [];
        }
    }

    public static void Save(Routes.RouteInfo route, IReadOnlyList<PlacedCut> cuts)
    {
        var path = PathIn(route);
        if (cuts.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Info("Placed stock: none on the track, Stock.json removed");
            }
            return;
        }

        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(path, JsonSerializer.Serialize(cuts, Options));
        Log.Info($"Placed stock: {cuts.Count} cuts saved");
    }
}
