using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

public enum PlacedKind { Signal, HandThrow, GradeCrossing, PoleTelegraph, PoleDistribution, PoleTelecom, LightCobra, LightYardMast, LightPlatform }

/// A signal, hand throw, grade crossing, utility pole or light pole placed by hand. Heading: the way it faces, degrees from +X towards
/// +Z (a grade crossing: the way its road runs; a utility pole: the way its wires would). Node: the switch a hand throw works. Width: a
/// grade crossing's road. Opposite: a hand throw on the switch's other side.
public sealed record PlacedObject(long Id, PlacedKind Kind, double Lat, double Lon, double Heading, long? Node = null, float Width = 0f, bool Opposite = false);

public static class PlacedKinds
{
    public static bool IsUtilityPole(this PlacedKind kind) => kind is PlacedKind.PoleTelegraph or PlacedKind.PoleDistribution or PlacedKind.PoleTelecom;
    public static bool IsLightPole(this PlacedKind kind) => kind is PlacedKind.LightCobra or PlacedKind.LightYardMast or PlacedKind.LightPlatform;
    public static bool IsPole(this PlacedKind kind) => kind.IsUtilityPole() || kind.IsLightPole();

    /// A pole's model under Models/Poles.
    public static string PoleModel(this PlacedKind kind) => kind switch
    {
        PlacedKind.PoleTelegraph => "pole_telegraph",
        PlacedKind.PoleDistribution => "pole_distribution",
        PlacedKind.PoleTelecom => "pole_telecom",
        PlacedKind.LightCobra => "light_cobra",
        PlacedKind.LightYardMast => "light_yard_mast",
        _ => "light_platform",
    };

    public static string Name(this PlacedKind kind) => kind switch
    {
        PlacedKind.Signal => "Signal",
        PlacedKind.HandThrow => "Hand throw",
        PlacedKind.GradeCrossing => "Grade crossing",
        PlacedKind.PoleTelegraph => "Telegraph pole",
        PlacedKind.PoleDistribution => "Power pole",
        PlacedKind.PoleTelecom => "Telephone pole",
        PlacedKind.LightCobra => "Street light",
        PlacedKind.LightYardMast => "Yard mast",
        _ => "Platform lamp",
    };
}

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

/// Several such changes as one undo: a run of poles.
public sealed record PlacedRunEdit(IReadOnlyList<PlacedEdit> Edits);
