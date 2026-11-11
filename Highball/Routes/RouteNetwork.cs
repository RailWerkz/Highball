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

/// One OpenStreetMap node on the rail lines that's tagged as something itself: a switch (railway=switch, with what kind
/// in railway:switch and which way it turns in railway:turnout_side) or a crossing of two tracks (railway=railway_crossing).
public sealed class NetworkNode
{
    public long Id { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public Dictionary<string, string> Tags { get; set; } = [];

    /// The railway= values worth a node of its own.
    public static bool Wanted(string railway) => railway is "switch" or "railway_crossing";

    /// The tags kept on such a node: its railway:* tags, and its ref and name.
    public static bool Kept(string key) => key.StartsWith("railway", StringComparison.Ordinal) || key is "ref" or "name";
}

/// Every road and rail way of a route, keyed by OpenStreetMap id: Network.json.gz in the route's folder, with the tagged
/// rail nodes in Nodes.json.gz. Each build merges what it fetched into this, so a line that runs on into a newly built
/// tile stays one line.
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

    /// Set on what's left of an OpenStreetMap way taken out by hand (no nodes, so nothing draws it), so fetching
    /// OpenStreetMap again doesn't put it back.
    public const string RemovedTag = "highball:removed";

    /// Set on a yard's map ways once it's cleared to be laid by hand: they aren't built, only followed. Its value says
    /// whether the way had been edited before, for when it's put back.
    public const string GuideTag = "highball:guide";

    /// Set on track laid by hand along a guide: it's already what it should be, so it isn't fitted.
    public const string ExactTag = "highball:exact";

    public static bool IsGuide(NetworkWay way) => way.Tags.ContainsKey(GuideTag) || (way.Rail && way.Id > 0 && !way.Tags.ContainsKey(ExactTag));

    /// What a track's laid on, by TrackBed's name, and how high above the ground in metres, if set by hand.
    public const string BedTag = "highball:bed";
    public const string BedHeightTag = "highball:bedheight";

    /// A mapped track's mark on the ground, drawn where the track would go until it's laid by hand.
    public const string TrackMarkTag = "highball:trackmark";

    public Dictionary<long, NetworkWay> Ways { get; } = [];

    /// The switches and crossings tagged on the rail lines, by node id.
    public Dictionary<long, NetworkNode> Nodes { get; } = [];

    /// Changed since it was loaded or saved.
    public bool Unsaved { get; private set; }

    /// Bumped on every change, so views that cache projected lines know to redo them.
    public int Version { get; private set; }

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "Network.json.gz");
    private static string NodesIn(RouteInfo route) => Path.Combine(route.Folder, "Nodes.json.gz");

    public void Merge(IEnumerable<NetworkWay> ways, IEnumerable<NetworkNode>? nodes = null)
    {
        foreach (var way in ways)
            if (!Ways.TryGetValue(way.Id, out var mine) || !mine.Tags.ContainsKey(EditedTag))
                Ways[way.Id] = way;
        foreach (var node in nodes ?? [])
            Nodes[node.Id] = node;
        Version++;
    }

    /// Takes out every way as fetched from OpenStreetMap, keeping those edited or laid by hand.
    public void DropFetched()
    {
        foreach (var id in Ways.Where(w => !w.Value.Tags.ContainsKey(EditedTag)).Select(w => w.Key).ToList())
            Ways.Remove(id);
        Nodes.Clear();
        Version++;
    }

    /// Puts a way in (replacing any with its id), or takes one out (way null). One of OpenStreetMap's (a positive id)
    /// is left as a marker that it was taken out.
    public void Set(long id, NetworkWay? way)
    {
        if (way is null && id > 0)
            Ways[id] = new NetworkWay { Id = id, Tags = new Dictionary<string, string> { [EditedTag] = "yes", [RemovedTag] = "yes" } };
        else if (way is null)
            Ways.Remove(id);
        else
            Ways[id] = way;
        Version++;
        Unsaved = true;
    }

    /// An id for a new way or node, below every one in use or ever handed out (OpenStreetMap's are all positive). The
    /// lowest handed out is saved (Ids.json), so an id taken out never comes back to attach whatever still names it (a
    /// switch's size, a hand throw) to new track.
    public long NewId()
    {
        var lowest = Math.Min(0L, Ways.Count > 0 ? Math.Min(Ways.Keys.Min(), Ways.Values.Min(w => w.Nodes.Length > 0 ? w.Nodes.Min() : 0L)) : 0L);
        _nextId = Math.Min(_nextId, lowest - 1);
        Unsaved = true;
        return _nextId--;
    }

    private long _nextId = -1;

    private static string IdsIn(RouteInfo route) => Path.Combine(route.Folder, "Ids.json");

    private sealed class Ids
    {
        public long Next { get; set; } = -1;
    }

    /// The lowest id named anywhere the network's ids are kept by other files, for a route saved before Ids.json.
    private static long LowestReferenced(RouteInfo route)
    {
        var lowest = 0L;
        foreach (var name in new[] { "Switches.json", "SwitchCurves.json" })
        {
            var path = Path.Combine(route.Folder, name);
            if (!File.Exists(path))
                continue;
            try
            {
                foreach (var key in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))?.Keys ?? Enumerable.Empty<string>())
                    if (long.TryParse(key, out var id))
                        lowest = Math.Min(lowest, id);
            }
            catch (JsonException)
            {
            }
        }

        foreach (var o in World.RouteObjects.Load(route))
            lowest = Math.Min(lowest, Math.Min(o.Node ?? 0L, o.Toward ?? 0L));
        return lowest;
    }

    public void Save(RouteInfo route)
    {
        Directory.CreateDirectory(route.Folder);
        using var file = File.Create(PathIn(route));
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, Ways.Values.ToList());
        using (var nodes = new GZipStream(File.Create(NodesIn(route)), CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(nodes, Nodes.Values.ToList());
        File.WriteAllText(IdsIn(route), JsonSerializer.Serialize(new Ids { Next = _nextId }));
        Unsaved = false;
    }

    public static RouteNetwork Load(RouteInfo route)
    {
        var network = new RouteNetwork();
        var path = PathIn(route);
        if (!File.Exists(path))
            return network;

        using (var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            network.Merge(JsonSerializer.Deserialize<List<NetworkWay>>(gzip) ?? []);
        if (File.Exists(NodesIn(route)))
        {
            using var nodes = new GZipStream(File.OpenRead(NodesIn(route)), CompressionMode.Decompress);
            network.Merge([], JsonSerializer.Deserialize<List<NetworkNode>>(nodes) ?? []);
        }

        var saved = File.Exists(IdsIn(route)) ? JsonSerializer.Deserialize<Ids>(File.ReadAllText(IdsIn(route)))?.Next : null;
        network._nextId = Math.Min(saved ?? -1L, LowestReferenced(route) - 1);
        return network;
    }
}
