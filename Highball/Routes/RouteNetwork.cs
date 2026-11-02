using System.IO.Compression;
using System.Text.Json;

namespace Highball.Routes;

/// Road classes, biggest first (OpenStreetMap's highway=*).
public enum RoadClass { Highway, Major, Minor, Local, Service }

/// Rail classes, from OpenStreetMap's railway=, usage= and service= tags.
public enum RailClass { Main, Branch, Industrial, Yard, Transit, NarrowGauge, Disused }

/// The road and rail detail a build fetches: each a level that takes in every class above it.
public static class Detail
{
    public static readonly (string Name, RoadClass[] Classes)[] RoadLevels =
    [
        ("NONE", []),
        ("HIGHWAYS", [RoadClass.Highway]),
        ("MAIN", [RoadClass.Highway, RoadClass.Major]),
        ("MAIN+MINOR", [RoadClass.Highway, RoadClass.Major, RoadClass.Minor]),
        ("ALL", [RoadClass.Highway, RoadClass.Major, RoadClass.Minor, RoadClass.Local]),
        ("ALL+SERVICE", [RoadClass.Highway, RoadClass.Major, RoadClass.Minor, RoadClass.Local, RoadClass.Service]),
    ];

    public static readonly (string Name, RailClass[] Classes)[] RailLevels =
    [
        ("NONE", []),
        ("MAIN", [RailClass.Main]),
        ("MAIN+BRANCH", [RailClass.Main, RailClass.Branch]),
        ("+INDUSTRIAL", [RailClass.Main, RailClass.Branch, RailClass.Industrial]),
        ("+YARDS", [RailClass.Main, RailClass.Branch, RailClass.Industrial, RailClass.Yard]),
        ("ALL", [RailClass.Main, RailClass.Branch, RailClass.Industrial, RailClass.Yard, RailClass.Transit, RailClass.NarrowGauge]),
        ("ALL+DISUSED", [RailClass.Main, RailClass.Branch, RailClass.Industrial, RailClass.Yard, RailClass.Transit, RailClass.NarrowGauge, RailClass.Disused]),
    ];

    public static RoadClass? Road(string highway) => highway.Replace("_link", "") switch
    {
        "motorway" or "trunk" => RoadClass.Highway,
        "primary" or "secondary" => RoadClass.Major,
        "tertiary" => RoadClass.Minor,
        "residential" or "unclassified" or "living_street" => RoadClass.Local,
        "service" or "track" => RoadClass.Service,
        _ => null,
    };

    /// Lines tagged without a usage are taken as branch lines; main lines are nearly always tagged usage=main.
    public static RailClass? Rail(Func<string, string> tag) => tag("railway") switch
    {
        "disused" or "abandoned" => RailClass.Disused,
        "light_rail" or "subway" or "tram" or "monorail" or "funicular" => RailClass.Transit,
        "narrow_gauge" => RailClass.NarrowGauge,
        "rail" or "preserved" => tag("service") is "yard" or "siding" or "spur" or "crossover"
            ? RailClass.Yard
            : tag("usage") switch
            {
                "main" => RailClass.Main,
                "industrial" or "military" or "test" => RailClass.Industrial,
                _ => RailClass.Branch,
            },
        _ => null,
    };

    /// The highway values a road level asks Overpass for.
    public static IEnumerable<string> HighwayValues(RoadClass c) => c switch
    {
        RoadClass.Highway => ["motorway", "motorway_link", "trunk", "trunk_link"],
        RoadClass.Major => ["primary", "primary_link", "secondary", "secondary_link"],
        RoadClass.Minor => ["tertiary", "tertiary_link"],
        RoadClass.Local => ["residential", "unclassified", "living_street"],
        _ => ["service", "track"],
    };
}

/// One OpenStreetMap way: a road or a rail line, whole (not cut at tile edges), with its node ids so junctions
/// between ways can be found, its points in degrees (independent of any projection), and the tags that matter.
public sealed class NetworkWay
{
    public long Id { get; set; }
    public bool Rail { get; set; }
    public int Class { get; set; }
    public long[] Nodes { get; set; } = [];
    public double[] Lat { get; set; } = [];
    public double[] Lon { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = [];
}

/// Every road and rail way of a route, keyed by OpenStreetMap id: Network.json.gz in the route's folder. Each build
/// merges what it fetched into this, so a line that runs on into a newly built tile stays one line.
public sealed class RouteNetwork
{
    /// The tags worth keeping: names and numbers, track and lane counts, gauge, power, speed, structures.
    public static readonly string[] KeptTags =
    [
        "name", "ref", "operator", "highway", "railway", "usage", "service", "tracks", "gauge", "electrified",
        "voltage", "frequency", "maxspeed", "lanes", "surface", "oneway", "bridge", "tunnel", "layer", "cutting", "embankment",
    ];

    /// Set on ways edited by hand (and on those laid new, which have negative ids), so fetching OpenStreetMap again
    /// doesn't put them back as they were.
    public const string EditedTag = "highball:edited";

    public Dictionary<long, NetworkWay> Ways { get; } = [];

    /// Changed since it was loaded or saved.
    public bool Unsaved { get; private set; }

    /// Bumped on every change, so views that cache projected lines know to redo them.
    public int Version { get; private set; }

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Network.json.gz");

    public void Merge(IEnumerable<NetworkWay> ways)
    {
        foreach (var way in ways)
            if (!Ways.TryGetValue(way.Id, out var mine) || !mine.Tags.ContainsKey(EditedTag))
                Ways[way.Id] = way;
        Version++;
    }

    /// Puts a way in (replacing any with its id), or takes one out (way null).
    public void Set(long id, NetworkWay? way)
    {
        if (way is null)
            Ways.Remove(id);
        else
            Ways[id] = way;
        Version++;
        Unsaved = true;
    }

    /// An id for a new way or node, below every one in use (OpenStreetMap's are all positive).
    public long NewId()
    {
        var lowest = Math.Min(0L, Ways.Count > 0 ? Math.Min(Ways.Keys.Min(), Ways.Values.Min(w => w.Nodes.Length > 0 ? w.Nodes.Min() : 0L)) : 0L);
        _nextId = Math.Min(_nextId, lowest - 1);
        return _nextId--;
    }

    private long _nextId = -1;

    public void Save(RouteInfo route)
    {
        Directory.CreateDirectory(route.Folder);
        using var file = File.Create(PathIn(route));
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gzip, Ways.Values.ToList());
        Unsaved = false;
    }

    public static RouteNetwork Load(RouteInfo route)
    {
        var network = new RouteNetwork();
        var path = PathIn(route);
        if (!File.Exists(path))
            return network;

        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        network.Merge(JsonSerializer.Deserialize<List<NetworkWay>>(gzip) ?? []);
        return network;
    }
}
