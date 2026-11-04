using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The sizes chosen by hand for a route's switches, by their OpenStreetMap node: Switches.json in its folder. A switch
/// not in it is sized automatically (TurnoutFitting); AsMapped leaves it as drawn, built procedurally.
public static class SwitchSizes
{
    public const int AsMapped = 0;

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Switches.json");

    public static Dictionary<long, int> Load(RouteInfo route)
    {
        try
        {
            var path = PathIn(route);
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<long, int>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e)
        {
            Log.Warn($"Switch sizes: {e.Message}");
            return [];
        }
    }

    /// Sets one switch's size (null: back to automatic) and saves.
    public static void Set(RouteInfo route, long node, int? size)
    {
        var sizes = Load(route);
        if (size is { } chosen)
            sizes[node] = chosen;
        else
            sizes.Remove(node);
        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(sizes));
    }
}

/// How far along its diverging track each switch's curve ends, where that was set by hand, by node: SwitchCurves.json in
/// the route's folder. A switch not in it lands its curve where TurnoutFitting finds best.
public static class SwitchCurves
{
    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "SwitchCurves.json");

    public static Dictionary<long, float> Load(RouteInfo route)
    {
        try
        {
            var path = PathIn(route);
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<long, float>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e)
        {
            Log.Warn($"Switch curves: {e.Message}");
            return [];
        }
    }

    /// Sets where one switch's curve ends (null: back to automatic) and saves.
    public static void Set(RouteInfo route, long node, float? reach)
    {
        var curves = Load(route);
        if (reach is { } set)
            curves[node] = set;
        else
            curves.Remove(node);
        Directory.CreateDirectory(route.Folder);
        File.WriteAllText(PathIn(route), JsonSerializer.Serialize(curves));
    }
}

/// A change to where a switch's curve ends, for undo.
public sealed record SwitchCurveEdit(long Node, float? Before, float? After);

/// What a switch could be fitted to, from TurnoutFitting: the size it picks by itself (null if none fits), the sizes
/// with room before the next junction, the size chosen by hand, if any, and why nothing fits, if nothing does. Reach: how
/// far along the diverging track its curve ends; ReachByHand: whether that was set by hand.
public sealed record SwitchOptions(int? Auto, int[] Fits, int? Chosen, string? Why = null, float? Reach = null, bool ReachByHand = false);
