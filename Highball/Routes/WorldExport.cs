using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Highball.Trains;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// Exports the world for players and servers, into Export/WORLD/:
///   REGION.hbpack    a client pack per region: its tiles and the track to draw on them (tiles in no region go in
///                    Unassigned.hbpack; with no regions set at all, everything goes in World.hbpack)
///   REGION.hbserver  a region's track for a self-hosted server: the rail graph with heights baked in, and signals
///   World.hbserver   the whole world's, for the company server
///   manifest.json    the version, and every pack's and tile's hash, for patches
/// Then reads every file back and checks it against what was written.
public static class WorldExport
{
    /// The route files the client lays the track out from, as the editor does, kept whole in every pack.
    private static readonly string[] TrackFiles =
        ["Route.json", "Network.json.gz", "Nodes.json.gz", "Switches.json", "SwitchCurves.json", "Bridges.json", "Objects.json", "Signalling.json", "TrackEdits.bin.gz"];

    public static string Root => Path.Combine(Path.GetDirectoryName(ContentPath.SourceRoot)!, "Export");

    public sealed record Result(string Folder, List<string> Lines);

    public static Result Run(RouteInfo route, RailGraph graph, IReadOnlyList<PlacedObject> objects, Action<string>? progress = null)
    {
        if (route.Grid is not { } grid || route.Zone is not { } zone)
            throw new InvalidOperationException($"{route.Name} isn't on a zone grid yet");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var folder = Path.Combine(Root, route.Id);
        if (Directory.Exists(folder))
            foreach (var old in Directory.GetFiles(folder))
                File.Delete(old);
        Directory.CreateDirectory(folder);

        var built = route.BuiltTiles();
        var regions = RouteRegions.Load(route);
        var groups = built.GroupBy(t => regions.Of(t)?.ToString() ?? (regions.Tiles.Count == 0 ? "World" : "Unassigned"))
            .ToDictionary(g => g.Key, g => g.OrderBy(t => -t.J).ThenBy(t => t.I).ToList());

        var files = TrackFiles.Select(name => (name, path: Path.Combine(route.Folder, name))).Where(f => File.Exists(f.path))
            .Select(f => (f.name, data: File.ReadAllBytes(f.path))).ToList();

        var lines = new List<string>();
        var manifest = new Dictionary<string, object>
        {
            ["world"] = route.Name, ["version"] = route.Version, ["zone"] = zone, ["exported"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
        };
        var packs = new List<object>();
        var done = 0;
        long rawBytes = 0, packBytes = 0;
        foreach (var (name, tiles) in groups)
        {
            var path = Path.Combine(folder, name + ".hbpack");
            var hashes = new Dictionary<string, string>();
            using (var pack = new PackFile.Writer(path, zone, name, grid))
            {
                foreach (var id in tiles)
                {
                    var tileFolder = route.TileFolder(id);
                    rawBytes += Directory.GetFiles(tileFolder).Sum(f => new FileInfo(f).Length);
                    var tile = RouteTile.Load(tileFolder);
                    var heights = tile.Heights;
                    if (LoadEdits(tileFolder, heights.Length) is { } edits)
                        for (var i = 0; i < heights.Length; i++)
                            heights[i] += edits[i];
                    pack.Add(new PackFile.Tile(id, heights.Min(), heights.Max(), tile.Meta.HeightSource, heights, tile.Cover));
                    hashes[id.ToString()] = Convert.ToHexString(pack.Tiles[^1].Hash);
                    progress?.Invoke($"Packing tiles: {++done} of {built.Count}");
                }

                foreach (var (fileName, data) in files)
                    pack.AddFile(fileName, data);
            }

            var bytes = new FileInfo(path).Length;
            packBytes += bytes;
            packs.Add(new { name, file = Path.GetFileName(path), tiles = tiles.Count, bytes, sha256 = Convert.ToHexString(PackFile.Hash(File.ReadAllBytes(path))), tileHashes = hashes });
            lines.Add($"{name}.hbpack: {tiles.Count} tiles, {bytes / 1048576.0:0.0} MB");
        }

        manifest["packs"] = packs;

        // Server data: the whole world, and each region's own share.
        progress?.Invoke("Writing server track");
        var servers = new List<object>();
        void Server(string name, Func<RailEdge, bool>? keep)
        {
            var path = Path.Combine(folder, name + ".hbserver");
            RailGraphFile.Write(path, zone, name, graph, objects, keep);
            var read = RailGraphFile.Read(path);
            servers.Add(new { name, file = Path.GetFileName(path), bytes = new FileInfo(path).Length, edges = read.Graph.Edges.Count, switches = read.Graph.Switches.Count, km = Math.Round(read.Graph.TotalLength / 1000.0, 3) });
            lines.Add($"{name}.hbserver: {read.Graph.Edges.Count} edges, {read.Graph.Switches.Count} switches, {read.Graph.TotalLength / 1000.0:0.00} km, {read.Signals.Count} signals, {new FileInfo(path).Length / 1024.0:0} KB");
        }

        Server("World", null);
        foreach (var region in regions.Tiles.Values.Distinct())
            Server(region.ToString(), e =>
            {
                var (x, z) = e.Plan(e.Length / 2);
                return regions.Of(grid.TileAt(new RoutePoint(x, -z))) == region;
            });
        manifest["servers"] = servers;
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        progress?.Invoke("Checking what was written");
        lines.AddRange(Check(route, folder, graph));
        lines.Insert(0, $"Exported {route.Name} v{route.Version} to {folder} in {watch.Elapsed.TotalSeconds:0} s: tiles {rawBytes / 1048576.0:0.0} MB as built, {packBytes / 1048576.0:0.0} MB packed");
        foreach (var line in lines)
            Log.Info($"Export: {line}");
        return new Result(folder, lines);
    }

    private static float[]? LoadEdits(string folder, int count)
    {
        var path = Path.Combine(folder, "Edits.bin.gz");
        if (!File.Exists(path))
            return null;
        using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        var edits = MemoryMarshal.Cast<byte, float>(output.ToArray()).ToArray();
        return edits.Length == count ? edits : null;
    }

    /// Every pack's tiles read back against the route's (heights within rounding, cover exact, embedded files exact),
    /// and the world's server graph against the one it was written from.
    private static List<string> Check(RouteInfo route, string folder, RailGraph graph)
    {
        var problems = new List<string>();
        var tiles = 0;
        var worst = 0f;
        foreach (var path in Directory.GetFiles(folder, "*.hbpack"))
        {
            using var file = File.OpenRead(path);
            var header = PackFile.ReadHeader(file);
            foreach (var entry in header.Tiles)
            {
                var tile = PackFile.ReadTile(file, header, entry);
                var source = RouteTile.Load(route.TileFolder(entry.Id));
                var edits = LoadEdits(route.TileFolder(entry.Id), source.Heights.Length);
                for (var i = 0; i < tile.Heights.Length; i++)
                    worst = Math.Max(worst, Math.Abs(tile.Heights[i] - (source.Heights[i] + (edits?[i] ?? 0f))));
                if (!tile.Cover.AsSpan().SequenceEqual(source.Cover))
                    problems.Add($"{Path.GetFileName(path)} {entry.Id}: land cover differs");
                tiles++;
            }

            foreach (var entry in header.Files)
                if (!PackFile.ReadBlob(file, entry.Offset, entry.Length).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(route.Folder, entry.Name))))
                    problems.Add($"{Path.GetFileName(path)}: {entry.Name} differs");
        }

        // Rounding to decimetres is half of 0.1 m at most; a little more for float error.
        if (worst > 0.06f)
            problems.Add($"heights differ by up to {worst:0.000} m");

        var world = RailGraphFile.Read(Path.Combine(folder, "World.hbserver")).Graph;
        if (world.Edges.Count != graph.Edges.Count || world.Vertices.Count != graph.Vertices.Count || world.Switches.Count != graph.Switches.Count)
            problems.Add($"server graph: {world.Edges.Count} edges, {world.Vertices.Count} vertices, {world.Switches.Count} switches read back against {graph.Edges.Count}, {graph.Vertices.Count}, {graph.Switches.Count}");
        else
        {
            for (var e = 0; e < graph.Edges.Count; e++)
            {
                var (a, b) = (graph.Edges[e], world.Edges[e]);
                for (var k = 0; k <= 4; k++)
                {
                    var s = a.Length * k / 4;
                    var (pa, pb) = (a.Position(s), b.Position(s));
                    if (Math.Abs(pa.X - pb.X) + Math.Abs(pa.Y - pb.Y) + Math.Abs(pa.Z - pb.Z) > 1e-6)
                    {
                        problems.Add($"server graph: edge {e} differs at {s:0} m");
                        break;
                    }
                }
            }

            for (var v = 0; v < graph.Vertices.Count; v++)
                if (graph.Vertices[v].Kind != world.Vertices[v].Kind)
                {
                    problems.Add($"server graph: vertex {v} is {world.Vertices[v].Kind}, was {graph.Vertices[v].Kind}");
                    break;
                }
        }

        return problems.Count == 0
            ? [$"Checked: {tiles} tiles read back (heights within {worst:0.000} m, cover and track files exact); server graph matches"]
            : problems.Select(p => "PROBLEM: " + p).ToList();
    }
}
