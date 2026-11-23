using System.IO.Compression;
using System.Text.Json;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// --networkcheck ROUTE: the network per cell checked against what it was read from, on the route itself. Loads the
/// network (the whole-route files of before, or Network/), writes every cell, loads it again and compares every way and
/// node exactly; then saves a way put back as it was (one cell), and a way turned round so its first point is in another
/// cell (and back), checking it leaves the old cell. Times the old format's load and save beside the new.
public static class NetworkCheck
{
    public static void Run(string id)
    {
        var route = RouteInfo.All().FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"no route called {id}");
        var problems = new List<string>();
        void Say(string text) => Log.Info($"Network check: {text}");

        // The old format, from the old files (or what migration left of them).
        var whole = new[] { "Network.json.gz", "Nodes.json.gz" }.Select(n => Path.Combine(route.Folder, n)).ToArray();
        if (!File.Exists(whole[0]))
            whole = [.. whole.Select(p => p + ".old")];
        long wholeBytes = 0;
        if (File.Exists(whole[0]))
        {
            wholeBytes = whole.Where(File.Exists).Sum(p => new FileInfo(p).Length);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            List<NetworkWay> ways;
            using (var gzip = new GZipStream(File.OpenRead(whole[0]), CompressionMode.Decompress))
                ways = JsonSerializer.Deserialize<List<NetworkWay>>(gzip) ?? [];
            List<NetworkNode> nodes = [];
            if (File.Exists(whole[1]))
            {
                using var gzip = new GZipStream(File.OpenRead(whole[1]), CompressionMode.Decompress);
                nodes = JsonSerializer.Deserialize<List<NetworkNode>>(gzip) ?? [];
            }

            var read = watch.Elapsed.TotalMilliseconds;
            var scratch = Path.Combine(Path.GetTempPath(), "HighballNetworkCheck");
            Directory.CreateDirectory(scratch);
            watch.Restart();
            using (var gzip = new GZipStream(File.Create(Path.Combine(scratch, "Network.json.gz")), CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, ways);
            using (var gzip = new GZipStream(File.Create(Path.Combine(scratch, "Nodes.json.gz")), CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, nodes);
            Say($"old format ({Path.GetFileName(whole[0])}): {ways.Count} ways, {nodes.Count} nodes, {wholeBytes / 1048576.0:0.00} MB; " +
                $"load {read:0} ms, save (whole, as every Ctrl+S did) {watch.Elapsed.TotalMilliseconds:0} ms");
            Directory.Delete(scratch, true);
        }

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var first = RouteNetwork.Load(route);
        var firstMs = loadWatch.Elapsed.TotalMilliseconds;
        var fromCells = File.Exists(NetworkCells.IndexIn(route));
        Say($"loaded {first.Ways.Count} ways, {first.Nodes.Count} nodes from {(fromCells ? "Network/" : "the old files")} in {firstMs:0} ms; next id {first.NextId}");

        // Every cell written (the migration itself if it was the old files).
        first.RewriteAll();
        first.Save(route);
        Say($"full save: {first.LastSave.Cells} cells written, {first.LastSave.Bytes / 1048576.0:0.00} MB, {first.LastSave.Ms:0} ms");

        loadWatch.Restart();
        var second = RouteNetwork.Load(route);
        var secondMs = loadWatch.Elapsed.TotalMilliseconds;
        var differences = Compare(first, second, problems);
        Say($"read back from Network/ in {secondMs:0} ms: {second.Ways.Count} ways, {second.Nodes.Count} nodes, {second.Duplicates} in two cells, " +
            $"next id {second.NextId}; {differences} differences");
        var ids = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(route.Folder, "Ids.json"))).GetProperty("Next").GetInt64();
        if (ids != first.NextId || second.NextId != first.NextId)
            problems.Add($"Ids.json says {ids}, loaded {second.NextId}, was {first.NextId}");

        var folder = NetworkCells.FolderIn(route);
        var files = Directory.GetFiles(folder);
        Say($"Network/: {files.Length} files, {files.Sum(f => new FileInfo(f).Length) / 1048576.0:0.00} MB (old files {wholeBytes / 1048576.0:0.00} MB)");

        // A small edit: one way put back as an equal copy.
        var some = second.Ways.Values.Where(w => w.Rail && w.Lat.Length > 1).OrderBy(w => w.Id).First();
        second.Set(some.Id, Copy(some, false));
        second.Save(route);
        Say($"one way saved: {second.LastSave.Cells} of {second.LastSave.OfCells} cells written ({second.LastSave.Bytes / 1024.0:0.0} KB), {second.LastSave.Ms:0} ms");
        if (second.LastSave.Cells != 1)
            problems.Add($"one way's save wrote {second.LastSave.Cells} cells");

        // A way turned round so it starts in another cell: it has to leave the old cell's file.
        var locate = new NetworkCells.Locator(route);
        var across = second.Ways.Values.Where(w => w.Lat.Length > 1 && w.Nodes.Length > 1 && w.Nodes[0] != w.Nodes[^1])
            .FirstOrDefault(w => locate.Of(w) != locate.Of(Copy(w, true)));
        if (across is null)
            problems.Add("no way crosses a cell border to turn round");
        else
        {
            var (from, to) = (locate.Of(across), locate.Of(Copy(across, true)));
            var turned = Copy(across, true);
            second.Set(across.Id, turned);
            second.Save(route);
            Say($"way {across.Id} turned round, from cell {from} to {to}: {second.LastSave.Cells} cells written, {second.LastSave.Ms:0} ms");
            var third = RouteNetwork.Load(route);
            if (third.StoredCell(across.Id) != to || third.Duplicates != 0 || !Same(third.Ways[across.Id], turned))
                problems.Add($"way {across.Id} turned round: read back in cell {third.StoredCell(across.Id)}, {third.Duplicates} in two cells");
            if (File.Exists(NetworkCells.PathIn(route, from)) && NetworkCells.Decode(File.ReadAllBytes(NetworkCells.PathIn(route, from)), from).Ways.Any(w => w.Id == across.Id))
                problems.Add($"way {across.Id} is still in cell {from}'s file");

            third.Set(across.Id, Copy(across, false));
            third.Save(route);
            var fourth = RouteNetwork.Load(route);
            var back = Compare(first, fourth, problems);
            Say($"way {across.Id} put back: {third.LastSave.Cells} cells written; read back, {back} differences from the first load, cell {fourth.StoredCell(across.Id)}");
        }

        foreach (var problem in problems.Take(50))
            Log.Error($"Network check: {problem}");
        Say(problems.Count == 0 ? "PASSED" : $"FAILED, {problems.Count} problems");
    }

    private static NetworkWay Copy(NetworkWay w, bool reversed) => new()
    {
        Id = w.Id, Rail = w.Rail, Class = w.Class,
        Nodes = reversed ? [.. w.Nodes.Reverse()] : [.. w.Nodes],
        Lat = reversed ? [.. w.Lat.Reverse()] : [.. w.Lat],
        Lon = reversed ? [.. w.Lon.Reverse()] : [.. w.Lon],
        Tags = new Dictionary<string, string>(w.Tags),
    };

    private static bool Bits(double[] a, double[] b) =>
        a.Length == b.Length && a.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.Select(BitConverter.DoubleToInt64Bits));

    private static bool Tags(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(t => b.TryGetValue(t.Key, out var v) && v == t.Value);

    private static bool Same(NetworkWay a, NetworkWay b) =>
        a.Id == b.Id && a.Rail == b.Rail && a.Class == b.Class && a.Nodes.SequenceEqual(b.Nodes) && Bits(a.Lat, b.Lat) && Bits(a.Lon, b.Lon) && Tags(a.Tags, b.Tags);

    private static bool Same(NetworkNode a, NetworkNode b) =>
        a.Id == b.Id && BitConverter.DoubleToInt64Bits(a.Lat) == BitConverter.DoubleToInt64Bits(b.Lat) &&
        BitConverter.DoubleToInt64Bits(a.Lon) == BitConverter.DoubleToInt64Bits(b.Lon) && Tags(a.Tags, b.Tags);

    /// Every way and node of one against the other, exactly; the differences counted and the first few listed.
    private static int Compare(RouteNetwork a, RouteNetwork b, List<string> problems)
    {
        var count = 0;
        void Differ(string what)
        {
            if (count++ < 20)
                problems.Add(what);
        }

        foreach (var (id, way) in a.Ways)
            if (!b.Ways.TryGetValue(id, out var other))
                Differ($"way {id} missing");
            else if (!Same(way, other))
                Differ($"way {id} differs");
        foreach (var id in b.Ways.Keys.Where(id => !a.Ways.ContainsKey(id)))
            Differ($"way {id} extra");
        foreach (var (id, node) in a.Nodes)
            if (!b.Nodes.TryGetValue(id, out var other))
                Differ($"node {id} missing");
            else if (!Same(node, other))
                Differ($"node {id} differs");
        foreach (var id in b.Nodes.Keys.Where(id => !a.Nodes.ContainsKey(id)))
            Differ($"node {id} extra");
        if (a.NextId != b.NextId)
            Differ($"next id {a.NextId} against {b.NextId}");
        return count;
    }
}
