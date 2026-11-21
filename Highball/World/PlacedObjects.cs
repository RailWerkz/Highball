using System.Text.Json;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

public enum PlacedKind { Signal, HandThrow, GradeCrossing, PoleTelegraph, PoleDistribution, PoleTelecom, LightCobra, LightYardMast, LightPlatform, TieSingle, TieSection, TieSectionBallast, SignalBridge }

/// A signal, hand throw, grade crossing, utility pole or light pole placed by hand. Heading: the way it faces, degrees from +X towards
/// +Z (a grade crossing: the way its road runs; a utility pole: the way its wires would). Node: the switch a hand throw works. Width: a
/// grade crossing's road. Opposite: a hand throw on the switch's other side; a grade crossing's masts on the left of the road's
/// traffic rather than the right. Run: what it was laid along with (the id they share). Turn: a grade crossing's masts turned on
/// their posts, degrees. Style: a grade crossing's surface (CrossingStyle). A signal's Node and Toward: the track nodes it stands
/// between, governing trains that pass Node and then come to it on their way to Toward. Approach: how far out along each track a
/// grade crossing's approach circuits reach, metres (0 for DefaultApproach). Bell: a grade crossing's bell (CrossingBell). Surface:
/// which of its kind's textures a grade crossing's panels are, by id (SurfaceTextures.Of; null for the kind's first). Warning: what a
/// grade crossing warns with (CrossingWarning). Fill: a planked crossing's asphalt, by id (SurfaceTextures.Of(Asphalt); null for the
/// first). Heads: a signal's heads on its mast, top to bottom (null: one three-light head, as signals were before there were
/// others). Stacks: a signal bridge's heads over each track it spans, the tracks left to right as a driver coming at it sees them,
/// each top to bottom (null, or a track past its end: none). A signal bridge stands across the tracks at Lat, Lon (on the one
/// it was put on), its heads facing Heading, and its span and legs are fitted to whatever tracks are under it each time it's built.
/// Laid: a signal or signal bridge put there by Route > Lay Out Signals (Signalling.SignalLayout), which that takes away again.
/// BackHeads, BackStacks: the heads on a mast's or a bridge's back, facing the other way (back to back with the front ones), top to
/// bottom; a bridge's by the same tracks as Stacks (left to right as a driver coming at its front sees them). A signal bridge's
/// Style: SignalBridgeStyle (a bridge on two legs, or a cantilever on one); a cantilever's leg stands on the right of the tracks as
/// a driver coming at its front sees them, or on the left if Opposite.
public sealed record PlacedObject(long Id, PlacedKind Kind, double Lat, double Lon, double Heading, long? Node = null, float Width = 0f, bool Opposite = false, long? Run = null,
    float Turn = 0f, int Style = 0, long? Toward = null, float Approach = 0f, int Bell = 0, string? Surface = null, int Warning = 0, string? Fill = null,
    IReadOnlyList<SignalHeadType>? Heads = null, IReadOnlyList<IReadOnlyList<SignalHeadType>>? Stacks = null, bool Laid = false,
    IReadOnlyList<SignalHeadType>? BackHeads = null, IReadOnlyList<IReadOnlyList<SignalHeadType>>? BackStacks = null)
{
    /// A signal's heads, top to bottom: as set, or the one three-light head it had before heads could be chosen.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SignalHeadType> MastHeads => Heads is { Count: > 0 } heads ? heads : SignalHeads.Default;

    /// A signal bridge's heads over the track'th track from the left, top to bottom (none if it has none set).
    public IReadOnlyList<SignalHeadType> StackOver(int track) => Stacks is { } stacks && track < stacks.Count ? stacks[track] : [];

    /// A signal's heads on the back of its mast, top to bottom (none unless set).
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SignalHeadType> MastBackHeads => BackHeads ?? [];

    /// Whether a signal bridge is a cantilever: one leg, its arm out over the tracks.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Cantilever => Kind == PlacedKind.SignalBridge && (SignalBridgeStyle)Style == SignalBridgeStyle.Cantilever;

    /// A grade crossing's approach circuits: half a mile out by default, a mile at most.
    public const float DefaultApproach = 804.672f;
    public const float MaxApproach = 1609.344f;
    public const float MinApproach = 160.9344f;

    /// How far its approach circuits reach, metres: as set, or the default.
    [System.Text.Json.Serialization.JsonIgnore]
    public float ApproachLength => Approach > 0f ? Math.Clamp(Approach, MinApproach, MaxApproach) : DefaultApproach;
}

/// What carries a signal bridge's heads over the tracks: a truss on a leg either side (Bridge), or an arm out from one leg
/// (Cantilever), as is usual over one or two tracks.
public enum SignalBridgeStyle { Bridge = 0, Cantilever = 1 }

/// What a signal head is: three lenses (green, yellow and red, top to bottom), each under its own hood (Triple); the same three
/// under one long hood that covers them all (Hooded); or a searchlight, one lens that shows one aspect at a time, red, yellow,
/// green or lunar white (Searchlight). Saved by name.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SignalHeadType>))]
public enum SignalHeadType { Triple, Hooded, Searchlight }

public static class SignalHeads
{
    public static readonly IReadOnlyList<SignalHeadType> Default = [SignalHeadType.Triple];

    /// The most heads one mast or one track's place on a bridge takes.
    public const int MaxStack = 4;

    public static string Name(this SignalHeadType type) => type switch
    {
        SignalHeadType.Hooded => "Long hood",
        SignalHeadType.Searchlight => "Searchlight",
        _ => "Three-light",
    };

    /// The lamps a head has: a colour-light head's three lenses, or every aspect a searchlight's one lens can show.
    public static World.SignalLamps Lenses(this SignalHeadType type) => type == SignalHeadType.Searchlight
        ? World.SignalLamps.Red | World.SignalLamps.Yellow | World.SignalLamps.Green | World.SignalLamps.White
        : World.SignalLamps.Red | World.SignalLamps.Yellow | World.SignalLamps.Green;

    /// What a head shows lit: a colour-light head any of its lenses at once; a searchlight only one aspect, the most restrictive of
    /// those asked for (red, then yellow, green, lunar white).
    public static World.SignalLamps Shown(this SignalHeadType type, World.SignalLamps lit)
    {
        lit &= type.Lenses();
        if (type != SignalHeadType.Searchlight)
            return lit;
        foreach (var one in (World.SignalLamps[])[World.SignalLamps.Red, World.SignalLamps.Yellow, World.SignalLamps.Green, World.SignalLamps.White])
            if (lit.HasFlag(one))
                return one;
        return World.SignalLamps.None;
    }

    /// Every head on a signal or signal bridge, in the order their lamps are numbered (NetworkMeshes.LampsOf): a mast's front top to
    /// bottom, then its back; a bridge's front track by track from the left, each top to bottom, then its back the same way.
    public static IEnumerable<SignalHeadType> All(PlacedObject o) => o.Kind == PlacedKind.SignalBridge
        ? (o.Stacks ?? []).SelectMany(s => s).Concat((o.BackStacks ?? []).SelectMany(s => s))
        : o.MastHeads.Concat(o.MastBackHeads);

    /// A bridge's style for this many tracks under it: a cantilever over one or two, a bridge over more.
    public static SignalBridgeStyle StyleFor(int tracks) => tracks <= 2 ? SignalBridgeStyle.Cantilever : SignalBridgeStyle.Bridge;
}

/// What a grade crossing's laid with: precast concrete panels, timber planks, asphalt poured level with the rails, or asphalt with a
/// timber flangeway plank either side of each rail (Planked), each in one of its kind's textures (SurfaceTextures;
/// PlacedObject.Surface: Planked's picks its planks' wood). (1 was rubber panels, now laid as concrete.)
public enum CrossingStyle { Concrete = 0, Timber = 2, Asphalt = 3, Planked = 4 }

/// What a grade crossing warns road traffic with: masts with flashers, a bell and gate arms (Gates); the masts, flashers and bell
/// without arms (Lights); a post with only its crossbuck, no lights and no sound (Crossbuck); or nothing at all (None).
public enum CrossingWarning { Gates = 0, Lights = 1, None = 2, Crossbuck = 3 }

/// What a grade crossing rings while it works: an electronic bell (a speaker), or a mechanical one (a gong struck by a hammer).
/// Each time it starts it rings one of that kind's recordings, picked at random (Trains.CrossingBells).
public enum CrossingBell { Electronic, Mechanical }

public static class PlacedKinds
{
    public static bool IsUtilityPole(this PlacedKind kind) => kind is PlacedKind.PoleTelegraph or PlacedKind.PoleDistribution or PlacedKind.PoleTelecom;
    public static bool IsLightPole(this PlacedKind kind) => kind is PlacedKind.LightCobra or PlacedKind.LightYardMast or PlacedKind.LightPlatform;
    public static bool IsTie(this PlacedKind kind) => kind is PlacedKind.TieSingle or PlacedKind.TieSection or PlacedKind.TieSectionBallast;

    /// What goes down anywhere, or off a road or track, singly or in runs: the poles, and the loose ties.
    public static bool IsPole(this PlacedKind kind) => kind.IsUtilityPole() || kind.IsLightPole() || kind.IsTie();

    /// Its model under Meshes/Poles.
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
        PlacedKind.SignalBridge => "Signal bridge",
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
