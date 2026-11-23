using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Highball.Trains;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// Exports the world for players and servers, into Export/WORLD/:
///   REGION.hbpack    a client pack per region: its tiles, their ground as shaped for the track, and the finished track to
///                    draw on them, as the route's cells (tiles in no region go in Unassigned.hbpack; with no regions set at
///                    all, everything goes in World.hbpack)
///   REGION.hbserver  a region's track for a self-hosted server: the rail graph with heights baked in, and signals
///   World.hbserver   the whole world's, for the company server
///   manifest.json    the version, and every pack's and tile's hash, for patches
/// Then reads every file back and checks it against what was written (the track read back from a pack's cells builds the
/// same rail graph as the server's), and if it's right installs the packs in Content/Packs/WORLD/ for Single Player.
///
/// What a pack carries of the track (docs/cell-routes.md, "Packs"): every cell of the route, whatever its region, with the
/// cells' index. CellStore reads only the whole set for now (a line, a turnout or a bridge is in pieces across cells, and a
/// thing's in its anchor's cell only), so a region's pack can't yet be cut down to its own tiles' cells and the ones its
/// lines reach into; once cells can be read as a window (stage 4), a region's pack carries the cells of its tiles plus every
/// cell holding a piece of a line, or the anchor of a thing, that's in its tiles.
public static class WorldExport
{
    /// The route files kept whole in every pack besides the cells: what the cells don't hold and the game still reads (the
    /// manifest, the placed objects, bridge settings, switch settings, hand shapes and signalling, and the network the lines'
    /// ways are put back from: CellStore.Restore).
    private static readonly string[] TrackFiles =
        ["Route.json", "Switches.json", "SwitchCurves.json", "Bridges.json", "Objects.json", "Signalling.json", "TrackEdits.bin.gz"];

    public static string Root => Path.Combine(Path.GetDirectoryName(ContentPath.SourceRoot)!, "Export");

    public sealed record Result(string Folder, List<string> Lines);

    /// How near rail the ground's kept exact in packs (PackFile.TerrainTolerance): the banks, cuttings and fields seen from a cab.
    private const double KeepNearTrack = 300.0;

    /// HB_PACKMEASURE=1: every tile also packed with its shaped ground baked into its heights, and the sizes logged against the
    /// heights as they came plus the shaped ground kept apart (what packs do).
    private static readonly bool Measure = Environment.GetEnvironmentVariable("HB_PACKMEASURE") == "1";

    /// Whether ground is within KeepNearTrack of any rail, by 100 m cells (the packs' blocks fall on them).
    private static PackFile.NearTrack NearTrack(RailGraph graph)
    {
        const double cell = 100.0;
        var near = new HashSet<(long, long)>();
        var reach = (int)Math.Ceiling(KeepNearTrack / cell) + 1;
        foreach (var edge in graph.Edges)
        {
            for (var s = 0.0; ; s = Math.Min(s + 20.0, edge.Length))
            {
                var (x, _, z) = edge.Position(s);
                var (east, north) = (x, -z);
                var (ci, cj) = ((long)Math.Floor(east / cell), (long)Math.Floor(north / cell));
                for (var dj = -reach; dj <= reach; dj++)
                for (var di = -reach; di <= reach; di++)
                {
                    var (x0, z0) = ((ci + di) * cell, (cj + dj) * cell);
                    var dx = Math.Max(0.0, Math.Max(x0 - east, east - (x0 + cell)));
                    var dz = Math.Max(0.0, Math.Max(z0 - north, north - (z0 + cell)));
                    if (dx * dx + dz * dz <= KeepNearTrack * KeepNearTrack)
                        near.Add((ci + di, cj + dj));
                }

                if (s >= edge.Length)
                    break;
            }
        }

        return (east, north) => near.Contains(((long)Math.Floor(east / cell), (long)Math.Floor(north / cell)));
    }

    /// The route's cells as they are now, written first if what's on disk isn't of the lines as they stand (a save not yet followed by
    /// its cells, or none yet). Returns the files to pack, relative to the route's folder: the index, then every cell.
    private static List<string> CurrentCells(RouteInfo route, HashSet<TileId> built, IReadOnlyList<PlacedObject> objects, NetworkLines lines, List<string> report)
    {
        var stamp = CellStore.StampOf(route, built, objects);
        var index = CellStore.ReadIndex(route);
        var why = index is null ? "none readable" : index.Stale(stamp);
        if (why is not null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            CellStore.Write(route, stamp, lines);
            index = CellStore.ReadIndex(route) ?? throw new InvalidOperationException("the route's cells couldn't be written (see the log)");
            if (index.Stale(stamp) is { } still)
                throw new InvalidOperationException($"the route's cells were written but aren't current ({still})");
            report.Add($"Cells written first ({why}) in {watch.ElapsedMilliseconds} ms");
        }

        var folder = Path.GetFileName(CellStore.FolderOf(route));
        return [$"{folder}/index.bin", .. index!.Cells.Keys.OrderBy(t => t.J).ThenBy(t => t.I).Select(t => $"{folder}/{t}.cell")];
    }

    /// The ground of a tile with lines on it as the track's shaped it, before sculpting (as the editor's open has it: the ground cache,
    /// or shaped now if that's not current); null for a tile no line's on.
    private static float[]? Shaped(RouteInfo route, NetworkLines lines, TileId id, float[] raw, int samples)
    {
        if (lines.LinesIn(id).Count == 0)
            return null;
        var shaped = (float[])raw.Clone();
        if (GroundCache.KeyOf(route, lines, id, raw, samples) is { } key && GroundCache.TryLoad(route, id, key, shaped))
            return shaped;
        lines.ConformKept(route, id, shaped, samples);
        return shaped;
    }

    /// How far shaped ground may come out from its exact shape in a pack: where the pack keeps the ground under it more roughly than
    /// this (away from rail), it's kept exact there too (NearShaped).
    private const double ShapedTolerance = 0.1;

    /// HB_PACKROUGHROADS=1: ground kept exact near rail only, as before packs carried shaped ground: about a third of the tiles' size
    /// for World (30 MB against 93), but roads away from rail then sit up to TerrainTolerance off their beds.
    private static readonly bool RoughRoads = Environment.GetEnvironmentVariable("HB_PACKROUGHROADS") == "1";

    /// Ground kept exact (near) as well wherever the track's shaped it (roads far from rail) and the ground kept as near rail alone
    /// would be off by more than ShapedTolerance under it, by the same 100 m cells, and one round them: there the shaped ground's cut
    /// and fill goes on ground as built, so roads sit on their beds as in the editor. Worked out for the whole route before any tile's
    /// packed, so two tiles sharing a border judge it alike.
    private static PackFile.NearTrack NearShaped(RouteInfo route, NetworkLines track, HashSet<TileId> built, PackFile.NearTrack rail)
    {
        const int cell = 50;
        var grid = route.Grid!;
        var header = new PackFile.Header(route.Zone ?? 0, "", grid.TileSize, grid.Samples, grid.CoverCells, [], [], PackFile.Version);
        var shapedCells = new System.Collections.Concurrent.ConcurrentDictionary<(long, long), byte>();
        Parallel.ForEach(built, id =>
        {
            var folder = route.TileFolder(id);
            var tile = RouteTile.Load(folder);
            if (Shaped(route, track, id, tile.Heights, tile.Meta.Samples) is not { } shaped)
                return;
            var heights = (float[])tile.Heights.Clone();
            if (LoadEdits(folder, heights.Length) is { } edits)
                for (var i = 0; i < heights.Length; i++)
                    heights[i] += edits[i];
            var blob = PackFile.EncodeTile(new PackFile.Tile(id, heights.Min(), heights.Max(), tile.Meta.HeightSource, heights, tile.Cover), grid, rail);
            var plain = PackFile.DecodeTile(header, new PackFile.TileEntry(id, 0, blob.Length, []), blob).Heights;
            var n = tile.Meta.Samples;
            var blocks = (n - 1) / cell;
            var (i0, j0) = ((long)id.I * blocks, (long)(id.J + 1) * blocks - 1);
            var seen = new HashSet<(long, long)>();
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var i = z * n + x;
                if (BitConverter.SingleToUInt32Bits(shaped[i]) != BitConverter.SingleToUInt32Bits(tile.Heights[i]) && Math.Abs(plain[i] - heights[i]) > ShapedTolerance)
                    seen.Add((i0 + Math.Min(x / cell, blocks - 1), j0 - Math.Min(z / cell, blocks - 1)));
            }

            foreach (var (ci, cj) in seen)
                for (var dj = -1; dj <= 1; dj++)
                for (var di = -1; di <= 1; di++)
                    shapedCells.TryAdd((ci + di, cj + dj), 0);
        });

        var size = grid.Spacing * cell;
        return (east, north) => rail(east, north) || shapedCells.ContainsKey(((long)Math.Floor(east / size), (long)Math.Floor(north / size)));
    }

    public static Result Run(RouteInfo route, RailGraph graph, NetworkLines track, IReadOnlyList<PlacedObject> objects, Action<string>? progress = null)
    {
        if (route.Grid is not { } grid || route.Zone is not { } zone)
            throw new InvalidOperationException($"{route.Name} isn't on a zone grid yet");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var folder = Path.Combine(Root, route.Id);
        if (Directory.Exists(folder))
            foreach (var old in Directory.GetFiles(folder))
                File.Delete(old);
        Directory.CreateDirectory(folder);

        var lines = new List<string>();
        var built = route.BuiltTiles();
        progress?.Invoke("Writing the cells");
        var cellFiles = CurrentCells(route, built, objects, track, lines);
        progress?.Invoke("Finding the shaped ground");
        var near = RoughRoads ? NearTrack(graph) : NearShaped(route, track, built, NearTrack(graph));
        var regions = RouteRegions.Load(route);
        var groups = built.GroupBy(t => regions.Of(t)?.ToString() ?? (regions.Tiles.Count == 0 ? "World" : "Unassigned"))
            .ToDictionary(g => g.Key, g => g.OrderBy(t => -t.J).ThenBy(t => t.I).ToList());

        var about = new PackFile.About(PackFile.Version, CellStore.FormatVersion, CellStore.BuildVersion, PackFile.GroundVersion,
            typeof(WorldExport).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"));
        var files = TrackFiles.Concat(RouteNetwork.Files(route)).Concat(cellFiles).Select(name => (name, path: Path.Combine(route.Folder, name))).Where(f => File.Exists(f.path))
            .Select(f => (f.name, data: File.ReadAllBytes(f.path))).Append((name: PackFile.AboutName, data: JsonSerializer.SerializeToUtf8Bytes(about))).ToList();
        var trackBytes = files.Sum(f => (long)f.data.Length);
        var cellBytes = files.Where(f => cellFiles.Contains(f.name)).Sum(f => (long)f.data.Length);

        var manifest = new Dictionary<string, object>
        {
            ["world"] = route.Name, ["version"] = route.Version, ["zone"] = zone, ["exported"] = about.Exported,
            ["pack"] = about.Pack, ["cellFormat"] = about.CellFormat, ["cellBuild"] = about.CellBuild, ["ground"] = about.Ground,
        };
        var packs = new List<object>();
        var done = 0;
        long rawBytes = 0, packBytes = 0, groundBytes = 0, tileBytes = 0, bakedBytes = 0, groundCacheBytes = 0;
        var shapedTiles = 0;
        var worstStep = 0.0;
        var steps = 0;
        foreach (var (name, tiles) in groups)
        {
            var path = Path.Combine(folder, name + ".hbpack");
            var hashes = new Dictionary<string, string>();
            using (var pack = new PackFile.Writer(path, zone, name, grid))
            {
                // Loaded and compressed a batch at a time on every core (each tile is a tenth of a second of Brotli), written in order.
                var header = new PackFile.Header(zone, name, grid.TileSize, grid.Samples, grid.CoverCells, [], [], PackFile.Version);
                var grounds = new List<(TileId Id, byte[] Blob)>();
                const int Batch = 64;
                for (var start = 0; start < tiles.Count; start += Batch)
                {
                    var batch = tiles.Skip(start).Take(Batch).ToList();
                    var blobs = new byte[batch.Count][];
                    var ground = new byte[]?[batch.Count];
                    var sizes = new long[batch.Count];
                    var baked = new long[batch.Count];
                    var cached = new long[batch.Count];
                    var stepOf = new (double Worst, int Count)[batch.Count];
                    Parallel.For(0, batch.Count, k =>
                    {
                        var id = batch[k];
                        var tileFolder = route.TileFolder(id);
                        sizes[k] = Directory.GetFiles(tileFolder).Sum(f => new FileInfo(f).Length);
                        var tile = RouteTile.Load(tileFolder);
                        var raw = tile.Heights;
                        var shaped = Shaped(route, track, id, raw, tile.Meta.Samples);
                        var heights = (float[])raw.Clone();
                        var edits = LoadEdits(tileFolder, heights.Length);
                        if (edits is not null)
                            for (var i = 0; i < heights.Length; i++)
                                heights[i] += edits[i];
                        blobs[k] = PackFile.EncodeTile(new PackFile.Tile(id, heights.Min(), heights.Max(), tile.Meta.HeightSource, heights, tile.Cover), grid, near);
                        if (shaped is null)
                            return;

                        // The shaped ground kept as cut and fill off the heights as built, sculpting added as the editor adds it; and how far
                        // it comes out from the ground as shaped where the pack keeps the ground under it only roughly (away from rail).
                        var plain = PackFile.DecodeTile(header, new PackFile.TileEntry(id, 0, blobs[k].Length, []), blobs[k]).Heights;
                        var changed = new bool[raw.Length];
                        for (var i = 0; i < raw.Length; i++)
                        {
                            changed[i] = BitConverter.SingleToUInt32Bits(shaped[i]) != BitConverter.SingleToUInt32Bits(raw[i]);
                            if (edits is not null)
                                shaped[i] += edits[i];
                        }

                        ground[k] = PackFile.EncodeGround(id, heights, shaped, changed);
                        var (worst, count) = (0.0, 0);
                        for (var i = 0; i < raw.Length; i++)
                        {
                            if (!changed[i])
                                continue;
                            var off = Math.Abs(plain[i] - heights[i]);
                            worst = Math.Max(worst, off);
                            if (off > 0.25)
                                count++;
                        }

                        stepOf[k] = (worst, count);
                        var cache = new FileInfo(GroundCache.PathOf(route, id));
                        cached[k] = cache.Exists ? cache.Length : 0;
                        if (Measure)
                            baked[k] = PackFile.EncodeTile(new PackFile.Tile(id, shaped.Min(), shaped.Max(), tile.Meta.HeightSource, shaped, tile.Cover), grid, near).Length;
                    });

                    for (var k = 0; k < batch.Count; k++)
                    {
                        rawBytes += sizes[k];
                        tileBytes += blobs[k].Length;
                        bakedBytes += Measure ? (ground[k] is null ? blobs[k].Length : baked[k]) : 0;
                        groundCacheBytes += cached[k];
                        worstStep = Math.Max(worstStep, stepOf[k].Worst);
                        steps += stepOf[k].Count;
                        pack.AddEncoded(batch[k], blobs[k]);
                        hashes[batch[k].ToString()] = Convert.ToHexString(pack.Tiles[^1].Hash);
                        if (ground[k] is { } g)
                        {
                            grounds.Add((batch[k], g));
                            groundBytes += g.Length;
                            shapedTiles++;
                        }
                    }

                    done += batch.Count;
                    progress?.Invoke($"Packing tiles: {done} of {built.Count}");
                }

                foreach (var (id, blob) in grounds)
                    pack.AddFile(PackFile.GroundName(id), blob);
                foreach (var (fileName, data) in files)
                    pack.AddFile(fileName, data);
            }

            var bytes = new FileInfo(path).Length;
            packBytes += bytes;
            packs.Add(new { name, file = Path.GetFileName(path), tiles = tiles.Count, bytes, sha256 = Convert.ToHexString(PackFile.Hash(File.ReadAllBytes(path))), tileHashes = hashes });
            lines.Add($"{name}.hbpack: {tiles.Count} tiles, {bytes / 1048576.0:0.0} MB");
        }

        lines.Add($"Packed: tiles {tileBytes / 1048576.0:0.0} MB, shaped ground {groundBytes / 1048576.0:0.0} MB for {shapedTiles} tiles (the ground cache's {groundCacheBytes / 1048576.0:0.0} MB), " +
                  $"track files {trackBytes / 1048576.0:0.0} MB a pack (cells {cellBytes / 1048576.0:0.0} MB in {cellFiles.Count - 1}); " +
                  $"shaped ground off its exact shape by up to {worstStep:0.00} m where the ground under it is kept roughly ({steps} samples over 0.25 m)");
        if (Measure)
            lines.Add($"Measured: shaped ground baked into the tiles {bakedBytes / 1048576.0:0.0} MB, against {(tileBytes + groundBytes) / 1048576.0:0.0} MB as tiles and shaped ground apart");
        manifest["packs"] = packs;

        // Server data: the whole world, and each region's own share.
        progress?.Invoke("Writing server track");
        var servers = new List<object>();
        // (Each signal bridge's track that has heads on a side is a signal of its own, and so is a mast's back: an id of its own from
        // the object's, past any a placed object's given (side and track in bits 56 to 61), at the object, facing the side's way. Which
        // track nodes a bridge's or a back's stands between is for the signalling to work out from the tracks.)
        static long Derived(PlacedObject o, int side, int track) => (1L << 62) | ((long)(side * 32 + track) << 56) | (o.Id & ((1L << 56) - 1));
        var signals = objects.Where(o => o.Kind == PlacedKind.Signal).Select(o => new RailGraphFile.Signal(o.Id, o.Node, o.Toward, o.Lat, o.Lon, o.Heading))
            .Concat(objects.Where(o => o.Kind == PlacedKind.Signal && o.MastBackHeads.Count > 0)
                .Select(o => new RailGraphFile.Signal(Derived(o, 1, 31), o.Toward, o.Node, o.Lat, o.Lon, o.Heading + 180.0)))
            .Concat(objects.Where(o => o.Kind == PlacedKind.SignalBridge).SelectMany(o =>
                new[] { (Stacks: o.Stacks, Side: 0), (Stacks: o.BackStacks, Side: 1) }.SelectMany(side => (side.Stacks ?? []).Select((heads, track) => (heads, track))
                    .Where(s => s.heads.Count > 0)
                    .Select(s => new RailGraphFile.Signal(Derived(o, side.Side, s.track), null, null, o.Lat, o.Lon, o.Heading + side.Side * 180.0)))))
            .ToList();
        void Server(string name, Func<RailEdge, bool>? keep)
        {
            var path = Path.Combine(folder, name + ".hbserver");
            RailGraphFile.Write(path, zone, name, graph, signals, keep);
            var read = RailGraphFile.Read(path);
            servers.Add(new { name, file = Path.GetFileName(path), bytes = new FileInfo(path).Length, edges = read.Graph.Edges.Count, switches = read.Graph.Switches.Count, km = Math.Round(read.Graph.TotalLength / 1000.0, 3), checksum = read.Graph.Checksum() });
            lines.Add($"{name}.hbserver: {read.Graph.Edges.Count} edges, {read.Graph.Switches.Count} switches, {read.Graph.TotalLength / 1000.0:0.00} km, {read.Signals.Count} signals, {new FileInfo(path).Length / 1024.0:0} KB, checksum {read.Graph.Checksum():X8}");
        }

        Server("World", null);
        foreach (var region in regions.Tiles.Values.Distinct())
            Server(region.ToString(), RegionKeeps(grid, t => regions.Of(t) == region));
        manifest["servers"] = servers;
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        progress?.Invoke("Checking what was written");
        var check = Check(route, folder, graph);
        lines.AddRange(check);

        // Installed for Single Player to find, once it's checked out.
        if (!check.Any(l => l.StartsWith("PROBLEM")))
        {
            var install = Path.Combine(PackSource.Root, route.Id);
            Directory.CreateDirectory(install);
            foreach (var old in Directory.GetFiles(install, "*.hbpack").Concat(Directory.GetFiles(install, "*.hbserver")))
                File.Delete(old);
            foreach (var pack in Directory.GetFiles(folder, "*.hbpack").Concat(Directory.GetFiles(folder, "*.hbserver")).Append(Path.Combine(folder, "manifest.json")))
                File.Copy(pack, Path.Combine(install, Path.GetFileName(pack)), true);
            lines.Add($"Installed in {install}");
        }
        lines.Insert(0, $"Exported {route.Name} v{route.Version} to {folder} in {watch.Elapsed.TotalSeconds:0} s: tiles {rawBytes / 1048576.0:0.0} MB as built, {packBytes / 1048576.0:0.0} MB packed");
        foreach (var line in lines)
            Log.Info($"Export: {line}");
        return new Result(folder, lines);
    }

    /// Which edges a region's server track keeps: those whose middle is on one of its tiles. The game playing a region's pack keeps
    /// the same (PackTrack), so the two agree on the checksum.
    public static Func<RailEdge, bool> RegionKeeps(RouteGrid grid, Func<TileId, bool> inRegion) => e =>
    {
        var (x, z) = e.Plan(e.Length / 2);
        return inRegion(grid.TileAt(new RoutePoint(x, -z)));
    };

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

    /// Every pack's tiles read back against the route's (heights within rounding, cover exact, shaped ground within 5 mm, embedded
    /// files exact), its cells read back as the game reads them and the rail graph built from them against the server's, and the
    /// world's server graph against the one it was written from.
    private static List<string> Check(RouteInfo route, string folder, RailGraph graph)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var tiles = 0;
        var worst = 0f;
        var worstGround = 0.0;
        var world = RailGraphFile.Read(Path.Combine(folder, "World.hbserver")).Graph;
        foreach (var path in Directory.GetFiles(folder, "*.hbpack"))
        {
            using var file = File.OpenRead(path);
            var header = PackFile.ReadHeader(file);
            var grounds = header.Files.Where(f => f.Name.StartsWith(PackFile.GroundFolder, StringComparison.Ordinal)).ToDictionary(f => f.Name);
            foreach (var entry in header.Tiles)
            {
                var tile = PackFile.ReadTile(file, header, entry);
                var source = RouteTile.Load(route.TileFolder(entry.Id));
                var edits = LoadEdits(route.TileFolder(entry.Id), source.Heights.Length);
                for (var i = 0; i < tile.Heights.Length; i++)
                    worst = Math.Max(worst, Math.Abs(tile.Heights[i] - (source.Heights[i] + (edits?[i] ?? 0f))));
                if (!tile.Cover.AsSpan().SequenceEqual(source.Cover))
                    problems.Add($"{Path.GetFileName(path)} {entry.Id}: land cover differs");

                // (The ground cache, now current for every tile with lines: the export shaped any that wasn't.)
                if (grounds.TryGetValue(PackFile.GroundName(entry.Id), out var g))
                {
                    var shaped = (float[])source.Heights.Clone();
                    var key = GroundCache.KeptKey(route, entry.Id);
                    if (key is null || !GroundCache.TryLoad(route, entry.Id, key.Value, shaped))
                        problems.Add($"{Path.GetFileName(path)} {entry.Id}: no shaped ground kept to check against");
                    else
                    {
                        var read = (float[])tile.Heights.Clone();
                        PackFile.ApplyGround(PackFile.ReadBlob(file, g.Offset, g.Length), read);
                        for (var i = 0; i < read.Length; i++)
                            if (BitConverter.SingleToUInt32Bits(shaped[i]) != BitConverter.SingleToUInt32Bits(source.Heights[i]))
                                worstGround = Math.Max(worstGround, Math.Abs(read[i] - (tile.Heights[i] + (shaped[i] - source.Heights[i]))));
                    }
                }

                tiles++;
            }

            foreach (var entry in header.Files)
            {
                if (entry.Name.StartsWith(PackFile.GroundFolder, StringComparison.Ordinal) || entry.Name == PackFile.AboutName)
                    continue;
                if (!PackFile.ReadBlob(file, entry.Offset, entry.Length).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(route.Folder, entry.Name))))
                    problems.Add($"{Path.GetFileName(path)}: {entry.Name} differs");
            }

            file.Dispose();
            // The track as the game reads it from this pack: opened as Single Player opens it, its cells read, and its rail graph built.
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using var pack = PackSource.Open(path);
                var lines = CellStore.Read(pack.CellRoute, pack.Cells, pack.Cells.Cells.Keys);
                if (lines is null)
                    problems.Add($"{Path.GetFileName(path)}: its cells couldn't be read back");
                else
                {
                    CellStore.Restore(lines, RouteNetwork.Load(pack.Route), TrackEdits.Load(pack.Route).Snapshot(), pack.Built(), RouteBridges.Load(pack.Route), RouteObjects.Load(pack.Route));
                    var read = RailGraphBuilder.Build(lines);
                    var server = Path.ChangeExtension(path, ".hbserver");
                    var mine = PackTrack.Checksum(read, pack);
                    if (read.Checksum() != graph.Checksum())
                        problems.Add($"{Path.GetFileName(path)}: the rail graph from its cells ({read.Edges.Count} edges, checksum {read.Checksum():X8}) isn't the editor's ({graph.Edges.Count}, {graph.Checksum():X8})");
                    else if (File.Exists(server) && RailGraphFile.Read(server).Graph.Checksum() is var theirs && theirs != mine)
                        problems.Add($"{Path.GetFileName(path)}: the rail graph from its cells has checksum {mine:X8}, its server track {theirs:X8}");
                    else
                        notes.Add($"{Path.GetFileName(path)}: {lines.Lines.Count} lines read back from {pack.Cells.Cells.Count} cells, the rail graph built from them the editor's, checksum {mine:X8}" +
                                  $"{(File.Exists(server) ? " as its server track's" : "")}, in {watch.ElapsedMilliseconds} ms");
                }
            }
            catch (Exception e) when (e is PackOutdatedException or InvalidDataException or IOException)
            {
                problems.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        // Rounding to decimetres is half of 0.1 m at most; a little more for float error.
        if (worst > 0.06f + PackFile.TerrainTolerance / 10f)
            problems.Add($"heights differ by up to {worst:0.000} m");
        if (worstGround > 0.0055)
            problems.Add($"shaped ground differs by up to {worstGround:0.0000} m");

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
            ? [$"Checked: {tiles} tiles read back (heights within {worst:0.000} m, shaped ground's cut and fill within {worstGround * 1000.0:0.0} mm, cover and track files exact); server graph matches", .. notes]
            : problems.Select(p => "PROBLEM: " + p).ToList();
    }
}
