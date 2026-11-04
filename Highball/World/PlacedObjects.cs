using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

public enum PlacedKind { Signal, HandThrow, GradeCrossing }

/// A signal, hand throw or grade crossing placed by hand. Heading: the way it faces, degrees from +X towards +Z (a grade
/// crossing: the way its road runs). Node: the switch a hand throw works. Width: a grade crossing's road. Opposite: a hand
/// throw on the switch's other side.
public sealed record PlacedObject(long Id, PlacedKind Kind, double Lat, double Lon, double Heading, long? Node = null, float Width = 0f, bool Opposite = false);

/// The objects placed on a route: Objects.json in its folder.
public static class RouteObjects
{
    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Objects.json");

    public static List<PlacedObject> Load(RouteInfo route)
    {
        try
        {
            var path = PathIn(route);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PlacedObject>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e)
        {
            Log.Warn($"Placed objects: {e.Message}");
            return [];
        }
    }

    public static void Save(RouteInfo route, IReadOnlyList<PlacedObject> objects)
    {
        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(objects));
    }
}

/// A change to the placed objects, for undo: one object before and after (null: it wasn't there).
public sealed record PlacedEdit(PlacedObject? Before, PlacedObject? After);
