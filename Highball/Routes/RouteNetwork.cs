using System.IO.Compression;
using System.Text.Json;
using RailWerkz.Core.Logging;

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

/// Every road and rail way of a route, keyed by OpenStreetMap id, with the tagged rail nodes: stored per cell in Network/ in
/// the route's folder (NetworkCells), a save writing only the cells changed since. Each build merges what it fetched into
/// this, so a line that runs on into a newly built tile stays one line.
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

    /// Heights pinned by hand at a rail way's nodes (TrackEditor's Level and Grade): "node=metres;node=metres", metres the route's
    /// heights the rails' formation is held to there (NetworkLines.FinishProfiles). Every rail way through a pinned node carries it.
    public const string HeightsTag = "highball:heights";

    /// A way's pinned heights by node (HeightsTag).
    public static Dictionary<long, float> PinsOf(NetworkWay way)
    {
        var pins = new Dictionary<long, float>();
        if (!way.Tags.TryGetValue(HeightsTag, out var text))
            return pins;
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf('=');
            if (at > 0 && long.TryParse(entry.AsSpan(0, at), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var node) &&
                float.TryParse(entry.AsSpan(at + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var metres) &&
                float.IsFinite(metres))
                pins[node] = metres;
        }

        return pins;
    }

    /// A way's pins written back to its tags (none: the tag taken off), only those at its own nodes.
    public static void SetPins(NetworkWay way, IReadOnlyDictionary<long, float> pins)
    {
        var mine = way.Nodes.Distinct().Where(pins.ContainsKey).OrderBy(n => Array.IndexOf(way.Nodes, n)).ToList();
        if (mine.Count == 0)
            way.Tags.Remove(HeightsTag);
        else
            way.Tags[HeightsTag] = string.Join(';', mine.Select(n => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n}={pins[n]:0.###}")));
    }

    /// A mapped track's mark on the ground, drawn where the track would go until it's laid by hand.
    public const string TrackMarkTag = "highball:trackmark";

    public Dictionary<long, NetworkWay> Ways { get; } = [];

    /// The switches and crossings tagged on the rail lines, by node id.
    public Dictionary<long, NetworkNode> Nodes { get; } = [];

    /// Changed since it was loaded or saved.
    public bool Unsaved { get; private set; }

    /// Bumped on every change, so views that cache projected lines know to redo them.
    public int Version { get; private set; }

    // --- How it's stored (NetworkCells): what's on disk as of the last load or save, and what's changed since. ---

    /// Each way and node as stored: the object last loaded or saved and the cell it's stored in. A way is only ever put in
    /// or replaced whole (Set, Merge and the rest), never changed in place, so one that's still the same object is as stored.
    private readonly Dictionary<long, (NetworkWay Way, string Cell)> _storedWays = [];
    private readonly Dictionary<long, (NetworkNode Node, string Cell)> _storedNodes = [];

    /// What's in each cell's file, by id.
    private readonly Dictionary<string, (HashSet<long> Ways, HashSet<long> Nodes)> _cells = new(StringComparer.Ordinal);

    /// Ways and nodes put in, replaced or taken out since the last load or save: their cells as they were and as they are
    /// now are written by the next save.
    private readonly HashSet<long> _changedWays = [];
    private readonly HashSet<long> _changedNodes = [];

    /// Cells written by the next save whatever's changed (read with a problem: a way in two of them, a file not matching its hash).
    private readonly HashSet<string> _changedCells = new(StringComparer.Ordinal);

    /// Every cell is written by the next save: read from the whole-route files of before, or cut on another grid.
    private bool _rewriteAll;

    /// Read from Network.json.gz and Nodes.json.gz: the next save renames them once every cell is written.
    private bool _fromWhole;

    /// The index as last read or written, and when its file was written, so a save after another RouteNetwork of the same
    /// route (the Planner's and the Editor's) saved keeps the cells that one wrote.
    private NetworkCells.Index? _index;
    private DateTime _indexTime;

    /// What the last save wrote (cells written, of how many, bytes, milliseconds), for logs and checks.
    internal (int Cells, int OfCells, long Bytes, double Ms) LastSave { get; private set; }

    /// The cell a way's stored in (null: not stored yet), for checks.
    internal string? StoredCell(long way) => _storedWays.TryGetValue(way, out var s) ? s.Cell : null;

    /// Ways and nodes found in two cells on load (a save cut short).
    internal int Duplicates { get; private set; }

    /// The next id NewId hands out, as Ids.json keeps it.
    internal long NextId => _nextId;

    /// Every cell written by the next save.
    internal void RewriteAll() => _rewriteAll = true;

    private const string WholeName = "Network.json.gz";
    private const string WholeNodesName = "Nodes.json.gz";
    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, WholeName);
    private static string NodesIn(RouteInfo route) => Path.Combine(route.Folder, WholeNodesName);

    /// The files the network is stored in, relative to the route's folder, those that exist: what anything stamped with the
    /// network hashes (CellStore.StampOf) and what a pack carries of it (WorldExport). Only this class knows how it's stored.
    /// Per cell: the index, then every cell's file in name order; else the whole-route files of before.
    public static IReadOnlyList<string> Files(RouteInfo route)
    {
        if (NetworkCells.ReadIndex(route) is { } index)
            return [$"{NetworkCells.Folder}/{NetworkCells.IndexName}", .. index.Cells.Keys.Select(NetworkCells.Relative)];
        return [.. new[] { WholeName, WholeNodesName }.Where(name => File.Exists(Path.Combine(route.Folder, name)))];
    }

    public void Merge(IEnumerable<NetworkWay> ways, IEnumerable<NetworkNode>? nodes = null)
    {
        foreach (var way in ways)
            if (!Ways.TryGetValue(way.Id, out var mine) || !mine.Tags.ContainsKey(EditedTag))
            {
                Ways[way.Id] = way;
                _changedWays.Add(way.Id);
            }

        foreach (var node in nodes ?? [])
            PutNode(node);
        Version++;
    }

    private void PutNode(NetworkNode node)
    {
        Nodes[node.Id] = node;
        _changedNodes.Add(node.Id);
    }

    /// Every mapped road (a positive id: from OpenStreetMap, edited or taken out or not) that inArea says is in the area taken out,
    /// then these put in. Roads drawn by hand (negative ids) stay. Returns how many went and how many came.
    public (int Removed, int Added) ReplaceRoads(Func<NetworkWay, bool> inArea, IEnumerable<NetworkWay> ways, IEnumerable<NetworkNode>? nodes = null)
    {
        var fetched = ways.ToList();
        var ids = fetched.Select(w => w.Id).ToHashSet();
        var doomed = Ways.Values.Where(w => w.Id > 0 && !w.Rail && (ids.Contains(w.Id) || (w.Lat.Length > 0 && inArea(w)))).Select(w => w.Id).ToList();
        foreach (var id in doomed)
        {
            Ways.Remove(id);
            _changedWays.Add(id);
        }

        foreach (var way in fetched)
        {
            Ways[way.Id] = way;
            _changedWays.Add(way.Id);
        }

        foreach (var node in nodes ?? [])
            PutNode(node);
        Version++;
        Unsaved = true;
        return (doomed.Count, fetched.Count);
    }

    /// Puts ways back as fetched, over any edit to them (or their having been taken out). Returns how many, and how many of
    /// those had been changed.
    public (int Restored, int Changed) Restore(IEnumerable<NetworkWay> ways, IEnumerable<NetworkNode>? nodes = null)
    {
        var (restored, changed) = (0, 0);
        foreach (var way in ways)
        {
            if (Ways.TryGetValue(way.Id, out var mine) && mine.Tags.ContainsKey(EditedTag))
                changed++;
            Ways[way.Id] = way;
            _changedWays.Add(way.Id);
            restored++;
        }

        foreach (var node in nodes ?? [])
            PutNode(node);
        Version++;
        Unsaved = true;
        return (restored, changed);
    }

    /// Takes out every way as fetched from OpenStreetMap, keeping those edited or laid by hand.
    public void DropFetched()
    {
        foreach (var id in Ways.Where(w => !w.Value.Tags.ContainsKey(EditedTag)).Select(w => w.Key).ToList())
        {
            Ways.Remove(id);
            _changedWays.Add(id);
        }

        _changedNodes.UnionWith(Nodes.Keys);
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
        _changedWays.Add(id);
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

    /// Writes the cells changed since the last load or save (as they were and as they are: a way whose first point moved
    /// into another cell is taken out of the old one's file), then the index and Ids.json. After reading the whole-route
    /// files of before, every cell is written and those files are renamed *.old.
    public void Save(RouteInfo route)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(NetworkCells.FolderIn(route));
        var locate = new NetworkCells.Locator(route);
        var grid = NetworkCells.GridKey(route);

        // Another RouteNetwork of this route may have saved since: the cells it wrote stay listed.
        var indexPath = NetworkCells.IndexIn(route);
        if (File.Exists(indexPath) && (_index is null || File.GetLastWriteTimeUtc(indexPath) != _indexTime))
            _index = NetworkCells.ReadIndex(route);
        if (_index is { } had && (had.Format != NetworkCells.Format || had.Grid != grid))
            _rewriteAll = true;
        var all = _rewriteAll;
        var index = new NetworkCells.Index { Format = NetworkCells.Format, Grid = grid };
        if (!all && _index is { } kept)
            foreach (var (name, cell) in kept.Cells)
                index.Cells[name] = cell;

        // What changed: what was marked, and (for safety) any way or node that isn't the object stored.
        var ways = new HashSet<long>(_changedWays);
        var nodes = new HashSet<long>(_changedNodes);
        var unmarked = 0;
        foreach (var (id, way) in Ways)
            if ((all || !_storedWays.TryGetValue(id, out var s) || !ReferenceEquals(s.Way, way)) && ways.Add(id) && !all)
                unmarked++;
        foreach (var id in _storedWays.Keys)
            if (!Ways.ContainsKey(id) && ways.Add(id))
                unmarked++;
        foreach (var (id, node) in Nodes)
            if ((all || !_storedNodes.TryGetValue(id, out var s) || !ReferenceEquals(s.Node, node)) && nodes.Add(id) && !all)
                unmarked++;
        foreach (var id in _storedNodes.Keys)
            if (!Nodes.ContainsKey(id) && nodes.Add(id))
                unmarked++;
        if (unmarked > 0)
            Log.Warn($"Network save: {unmarked} ways and nodes changed without being marked");

        var dirty = new HashSet<string>(_changedCells, StringComparer.Ordinal);
        if (all)
            dirty.UnionWith(_index?.Cells.Keys ?? Enumerable.Empty<string>());
        (HashSet<long> Ways, HashSet<long> Nodes) Members(string cell) =>
            _cells.TryGetValue(cell, out var m) ? m : _cells[cell] = ([], []);
        foreach (var id in ways)
        {
            if (_storedWays.TryGetValue(id, out var s))
            {
                Members(s.Cell).Ways.Remove(id);
                dirty.Add(s.Cell);
            }

            if (Ways.TryGetValue(id, out var way))
            {
                var cell = locate.Of(way);
                Members(cell).Ways.Add(id);
                dirty.Add(cell);
                _storedWays[id] = (way, cell);
            }
            else
                _storedWays.Remove(id);
        }

        foreach (var id in nodes)
        {
            if (_storedNodes.TryGetValue(id, out var s))
            {
                Members(s.Cell).Nodes.Remove(id);
                dirty.Add(s.Cell);
            }

            if (Nodes.TryGetValue(id, out var node))
            {
                var cell = locate.Of(node);
                Members(cell).Nodes.Add(id);
                dirty.Add(cell);
                _storedNodes[id] = (node, cell);
            }
            else
                _storedNodes.Remove(id);
        }

        // Each changed cell written whole, on every core (the first save after the old files writes them all).
        var names = dirty.Order(StringComparer.Ordinal).ToArray();
        var written = new NetworkCells.Cell?[names.Length];
        long bytes = 0;
        try
        {
            Parallel.For(0, names.Length, k =>
            {
                var name = names[k];
                var path = NetworkCells.PathIn(route, name);
                if (!_cells.TryGetValue(name, out var members) || members.Ways.Count + members.Nodes.Count == 0)
                {
                    File.Delete(path);
                    return;
                }

                var cellWays = members.Ways.Order().Select(id => Ways[id]).ToList();
                var cellNodes = members.Nodes.Order().Select(id => Nodes[id]).ToList();
                var data = NetworkCells.Encode(cellWays, cellNodes);
                NetworkCells.WriteAtomic(path, data);
                var reach = new HashSet<string>(StringComparer.Ordinal);
                foreach (var way in cellWays)
                    locate.Reach(way, reach);
                reach.Remove(name);
                Interlocked.Add(ref bytes, data.Length);
                written[k] = new NetworkCells.Cell
                {
                    Hash = NetworkCells.Hash(data), Bytes = data.Length, Ways = cellWays.Count, Nodes = cellNodes.Count,
                    Reach = [.. reach.Order(StringComparer.Ordinal)],
                };
            });

            for (var k = 0; k < names.Length; k++)
                if (written[k] is { } cell)
                    index.Cells[names[k]] = cell;
                else
                {
                    index.Cells.Remove(names[k]);
                    _cells.Remove(names[k]);
                }

            // A full write leaves nothing in the folder the index doesn't list.
            if (all)
                foreach (var stray in Directory.GetFiles(NetworkCells.FolderIn(route), "*.bin"))
                    if (!index.Cells.ContainsKey(Path.GetFileNameWithoutExtension(stray)))
                        File.Delete(stray);

            NetworkCells.WriteIndex(route, index);
            NetworkCells.WriteAtomic(IdsIn(route), JsonSerializer.SerializeToUtf8Bytes(new Ids { Next = _nextId }));
        }
        catch
        {
            // Some cells may be written and some not: the next save writes them all.
            _rewriteAll = true;
            throw;
        }

        _index = index;
        _indexTime = File.GetLastWriteTimeUtc(indexPath);
        if (_fromWhole)
        {
            foreach (var path in new[] { PathIn(route), NodesIn(route) })
                if (File.Exists(path))
                    File.Move(path, path + ".old", overwrite: true);
            Log.Info($"Network: {WholeName} and {WholeNodesName} now stored per cell in {NetworkCells.Folder}/; the old files renamed *.old");
        }

        _fromWhole = false;
        _rewriteAll = false;
        _changedWays.Clear();
        _changedNodes.Clear();
        _changedCells.Clear();
        Unsaved = false;
        LastSave = (names.Length, index.Cells.Count, bytes, watch.Elapsed.TotalMilliseconds);
        Log.Info($"Network save: {names.Length} of {index.Cells.Count} cells written ({bytes / 1024.0:0.#} KB), " +
                 $"{ways.Count} ways and {nodes.Count} nodes changed, {watch.Elapsed.TotalMilliseconds:0} ms");
    }

    /// Reads Network/ if it has an index, else the whole-route files of before (Network.json.gz, Nodes.json.gz).
    public static RouteNetwork Load(RouteInfo route)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var network = new RouteNetwork();
        if (NetworkCells.ReadIndex(route) is { } index)
            network.LoadCells(route, index);
        else if (File.Exists(PathIn(route)))
        {
            using (var gzip = new GZipStream(File.OpenRead(PathIn(route)), CompressionMode.Decompress))
                foreach (var way in JsonSerializer.Deserialize<List<NetworkWay>>(gzip) ?? [])
                    network.Ways[way.Id] = way;
            if (File.Exists(NodesIn(route)))
            {
                using var nodes = new GZipStream(File.OpenRead(NodesIn(route)), CompressionMode.Decompress);
                foreach (var node in JsonSerializer.Deserialize<List<NetworkNode>>(nodes) ?? [])
                    network.Nodes[node.Id] = node;
            }

            network._fromWhole = true;
            network._rewriteAll = true;
            Log.Info($"Network load: {network.Ways.Count} ways, {network.Nodes.Count} nodes from {WholeName}, {watch.Elapsed.TotalMilliseconds:0} ms");
        }
        else
            return network;

        var saved = File.Exists(IdsIn(route)) ? JsonSerializer.Deserialize<Ids>(File.ReadAllText(IdsIn(route)))?.Next : null;
        network._nextId = Math.Min(saved ?? -1L, LowestReferenced(route) - 1);
        return network;
    }

    /// Every cell the index lists read (on every core), then put in by cell name.
    private void LoadCells(RouteInfo route, NetworkCells.Index index)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (index.Format != NetworkCells.Format)
            throw new InvalidDataException($"{NetworkCells.IndexIn(route)}: network format {index.Format}, not {NetworkCells.Format}");
        _index = index;
        _indexTime = File.GetLastWriteTimeUtc(NetworkCells.IndexIn(route));
        if (index.Grid != NetworkCells.GridKey(route))
        {
            Log.Warn($"Network load: cells cut on {index.Grid}, the route's now on {NetworkCells.GridKey(route)}; every cell is written again on save");
            _rewriteAll = true;
        }

        var names = index.Cells.Keys.ToArray();
        var read = new (List<NetworkWay> Ways, List<NetworkNode> Nodes, bool Stale)?[names.Length];
        Parallel.For(0, names.Length, k =>
        {
            var path = NetworkCells.PathIn(route, names[k]);
            if (!File.Exists(path))
                return;
            var data = File.ReadAllBytes(path);
            var (ways, nodes) = NetworkCells.Decode(data, path);
            read[k] = (ways, nodes, NetworkCells.Hash(data) != index.Cells[names[k]].Hash);
        });

        for (var k = 0; k < names.Length; k++)
        {
            var name = names[k];
            if (read[k] is not { } cell)
            {
                Log.Warn($"Network load: cell {name} is in the index but has no file");
                _changedCells.Add(name);
                continue;
            }

            if (cell.Stale)
            {
                Log.Warn($"Network load: cell {name} doesn't match the index's hash of it");
                _changedCells.Add(name);
            }

            var members = (Ways: new HashSet<long>(cell.Ways.Count), Nodes: new HashSet<long>(cell.Nodes.Count));
            _cells[name] = members;
            foreach (var way in cell.Ways)
            {
                if (_storedWays.TryGetValue(way.Id, out var other))
                {
                    // A save cut short between two cells' files: the one read first is kept, and both are written again.
                    Duplicates++;
                    _changedCells.Add(other.Cell);
                    _changedCells.Add(name);
                    continue;
                }

                Ways[way.Id] = way;
                _storedWays[way.Id] = (way, name);
                members.Ways.Add(way.Id);
            }

            foreach (var node in cell.Nodes)
            {
                if (_storedNodes.TryGetValue(node.Id, out var other))
                {
                    Duplicates++;
                    _changedCells.Add(other.Cell);
                    _changedCells.Add(name);
                    continue;
                }

                Nodes[node.Id] = node;
                _storedNodes[node.Id] = (node, name);
                members.Nodes.Add(node.Id);
            }
        }

        if (Duplicates > 0)
            Log.Warn($"Network load: {Duplicates} ways and nodes found in two cells; those cells are written again on save");
        Log.Info($"Network load: {Ways.Count} ways, {Nodes.Count} nodes from {names.Length} cells, {watch.Elapsed.TotalMilliseconds:0} ms");
    }
}
