using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

public enum PlacedKind { Signal, HandThrow, GradeCrossing, PoleTelegraph, PoleDistribution, PoleTelecom, LightCobra, LightYardMast, LightPlatform, TieSingle, TieSection, TieSectionBallast }

/// A signal, hand throw, grade crossing, utility pole or light pole placed by hand. Heading: the way it faces, degrees from +X towards
/// +Z (a grade crossing: the way its road runs; a utility pole: the way its wires would). Node: the switch a hand throw works. Width: a
/// grade crossing's road. Opposite: a hand throw on the switch's other side; a grade crossing's masts on the left of the road's
/// traffic rather than the right. Run: what it was laid along with (the id they share). Turn: a grade crossing's masts turned on
/// their posts, degrees. Style: a grade crossing's surface (CrossingStyle). A signal's Node and Toward: the track nodes it stands
/// between, governing trains that pass Node and then come to it on their way to Toward. Approach: how far out along each track a
/// grade crossing's approach circuits reach, metres (0 for DefaultApproach).
public sealed record PlacedObject(long Id, PlacedKind Kind, double Lat, double Lon, double Heading, long? Node = null, float Width = 0f, bool Opposite = false, long? Run = null,
    float Turn = 0f, int Style = 0, long? Toward = null, float Approach = 0f)
{
    /// A grade crossing's approach circuits: half a mile out by default, a mile at most.
    public const float DefaultApproach = 804.672f;
    public const float MaxApproach = 1609.344f;
    public const float MinApproach = 160.9344f;

    /// How far its approach circuits reach, metres: as set, or the default.
    public float ApproachLength => Approach > 0f ? Math.Clamp(Approach, MinApproach, MaxApproach) : DefaultApproach;
}

/// What a grade crossing's laid with: precast concrete panels, rubber panels, timber planks, or asphalt poured level with the rails.
public enum CrossingStyle { Concrete, Rubber, Timber, Asphalt }

public static class PlacedKinds
{
    public static bool IsUtilityPole(this PlacedKind kind) => kind is PlacedKind.PoleTelegraph or PlacedKind.PoleDistribution or PlacedKind.PoleTelecom;
    public static bool IsLightPole(this PlacedKind kind) => kind is PlacedKind.LightCobra or PlacedKind.LightYardMast or PlacedKind.LightPlatform;
    public static bool IsTie(this PlacedKind kind) => kind is PlacedKind.TieSingle or PlacedKind.TieSection or PlacedKind.TieSectionBallast;

    /// What goes down anywhere, or off a road or track, singly or in runs: the poles, and the loose ties.
    public static bool IsPole(this PlacedKind kind) => kind.IsUtilityPole() || kind.IsLightPole() || kind.IsTie();

    /// Its model under Models/Poles.
    public static string PoleModel(this PlacedKind kind) => kind switch
    {
        PlacedKind.PoleTelegraph => "pole_telegraph",
        PlacedKind.PoleDistribution => "pole_distribution",
        PlacedKind.PoleTelecom => "pole_telecom",
        PlacedKind.LightCobra => "light_cobra",
        PlacedKind.LightYardMast => "light_yard_mast",
        PlacedKind.LightPlatform => "light_platform",
        PlacedKind.TieSingle => "tie_single",
        PlacedKind.TieSection => "tie_section",
        _ => "tie_section_ballast",
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
        PlacedKind.LightPlatform => "Platform lamp",
        PlacedKind.TieSingle => "Single tie",
        PlacedKind.TieSection => "Tie section, no ballast",
        _ => "Tie section, on ballast",
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
