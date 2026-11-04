using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Brutal.StbApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// Builds tiles of a route on a worker thread:
///   heights from the AWS Terrain Tiles (Terrarium PNGs at zoom 15, metres to a 256th), sampled on the route's lattice;
///   ground cover from ESA WorldCover 2021 (10 m classes), read straight out of its cloud-optimised GeoTIFFs by range
///   requests;
///   roads and rail lines from OpenStreetMap through the Overpass API, at the detail asked for, merged into the
///   route's network whole.
/// Downloads are cached under the user's data folder, so rebuilding a tile costs nothing.
public sealed class RouteGenerator
{
    public sealed class Request
    {
        public required RouteInfo Route { get; init; }
        public required RouteNetwork Network { get; init; }
        public required List<TileId> Tiles { get; init; }
        public int RoadLevel { get; init; }
        public int RailLevel { get; init; }

        /// Drop every way fetched from OpenStreetMap before merging what this build fetches (ways edited by hand
        /// stay), so a route regenerated at less detail loses what it no longer asks for.
        public bool ReplaceNetwork { get; init; }
    }

    public const string Attribution = "Elevation: AWS Terrain Tiles (SRTM, NED, GMTED and others). Land cover: ESA WorldCover 2021, CC BY 4.0. Roads and rail: (c) OpenStreetMap contributors, ODbL.";

    private static readonly HttpClient Http = CreateClient();

    private volatile string _status = "";
    private volatile bool _running;
    private CancellationTokenSource? _cancel;

    public string Status => _status;
    public bool Running => _running;
    public float Progress { get; private set; }

    /// Bumped whenever a tile or the network is written, so the map redraws what's built.
    public int Version { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Highball-RouteBuilder", typeof(RouteGenerator).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return client;
    }

    private static string CacheFolder(params string[] parts)
    {
        var folder = Path.Combine([ContentPath.UserData, "TileCache", .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Start(Request request)
    {
        if (_running || request.Tiles.Count == 0)
            return;

        _running = true;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Task.Run(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await Build(request, token);
                _status = $"Built {request.Tiles.Count} tile{(request.Tiles.Count == 1 ? "" : "s")} in {watch.Elapsed.TotalSeconds:0}s";
                Log.Info($"Route builder: {_status}");
            }
            catch (OperationCanceledException)
            {
                _status = "Cancelled";
            }
            catch (Exception e)
            {
                _status = $"Failed: {e.Message}";
                Log.Error($"Route builder: {e}");
            }
            finally
            {
                Progress = 0f;
                _running = false;
            }
        });
    }

    public void Cancel() => _cancel?.Cancel();

    private void Say(string status, float progress)
    {
        _status = status;
        Progress = progress;
    }

    private async Task Build(Request r, CancellationToken token)
    {
        var route = r.Route;
        var grid = route.Grid!;
        var projection = route.Projection!;
        var roads = Detail.RoadLevels[r.RoadLevel];
        var rails = Detail.RailLevels[r.RailLevel];
        var elevation = new Terrarium(15);
        var cover = new WorldCover();

        // Tiles nearest the middle of the selection first.
        var middle = new RoutePoint(r.Tiles.Average(t => t.I + 0.5), r.Tiles.Average(t => t.J + 0.5));
        var tiles = r.Tiles.OrderBy(t => Math.Pow(t.I + 0.5 - middle.East, 2) + Math.Pow(t.J + 0.5 - middle.North, 2)).ToList();
        var share = 0.8f / tiles.Count;

        for (var n = 0; n < tiles.Count; n++)
        {
            token.ThrowIfCancellationRequested();
            var tile = tiles[n];
            var label = $"Tile {tile.I}, {tile.J} ({n + 1} of {tiles.Count})";

            Say($"{label}: elevation", n * share);
            var samples = grid.Samples;
            var geo = new GeoPoint[samples * samples];
            Parallel.For(0, samples, z =>
            {
                for (var x = 0; x < samples; x++)
                    geo[z * samples + x] = projection.Inverse(grid.Sample(tile, x, z));
            });

            await elevation.Fetch(Bounds(geo), token);
            var heights = new float[geo.Length];
            Parallel.For(0, samples, z =>
            {
                for (var x = 0; x < samples; x++)
                    heights[z * samples + x] = elevation.Elevation(geo[z * samples + x]);
            });

            Say($"{label}: land cover", (n + 0.6f) * share);
            var cells = grid.CoverCells;
            var cellSize = grid.TileSize / cells;
            var corner = grid.Corner(tile);
            var centres = new GeoPoint[cells * cells];
            for (var z = 0; z < cells; z++)
                for (var x = 0; x < cells; x++)
                    centres[z * cells + x] = projection.Inverse(new RoutePoint(corner.East + (x + 0.5) * cellSize, corner.North + grid.TileSize - (z + 0.5) * cellSize));
            var classes = await cover.Classes(centres, cellSize);

            var built = new RouteTile(new RouteTile.Info
            {
                I = tile.I,
                J = tile.J,
                Samples = samples,
                Spacing = grid.Spacing,
                CoverCells = cells,
                MinHeight = heights.Min(),
                MaxHeight = heights.Max(),
                Roads = roads.Name,
                Rails = rails.Name,
                Built = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            }, heights, classes);
            built.Save(route.TileFolder(tile));
            Version++;
            elevation.Trim();
        }

        if (roads.Classes.Length + rails.Classes.Length > 0 || r.ReplaceNetwork)
        {
            token.ThrowIfCancellationRequested();
            Say("Asking OpenStreetMap for roads and rail", 0.82f);
            var (ways, nodes) = roads.Classes.Length + rails.Classes.Length > 0 ? await OpenStreetMap(route, tiles, roads.Classes, rails.Classes, token) : ([], []);
            token.ThrowIfCancellationRequested();
            if (r.ReplaceNetwork)
                r.Network.DropFetched();
            r.Network.Merge(ways, nodes);
            r.Network.Save(route);
            Version++;
            Say($"{ways.Count(w => w.Rail)} rail lines, {ways.Count(w => !w.Rail)} roads, {nodes.Count} switches and crossings tagged", 0.98f);
        }

        Progress = 1f;
    }

    private static (double South, double West, double North, double East) Bounds(IReadOnlyCollection<GeoPoint> points) =>
        (points.Min(p => p.Lat), points.Min(p => p.Lon), points.Max(p => p.Lat), points.Max(p => p.Lon));

    // --- Elevation: Terrarium tiles. ---

    private sealed class Terrarium(int zoom)
    {
        private readonly ConcurrentDictionary<(int X, int Y), float[]> _tiles = new();

        public async Task Fetch((double South, double West, double North, double East) bounds, CancellationToken token)
        {
            var (x0, y0) = Tile(bounds.North, bounds.West);
            var (x1, y1) = Tile(bounds.South, bounds.East);
            var jobs = new List<(int X, int Y)>();
            for (var y = y0 - 1; y <= y1 + 1; y++)
                for (var x = x0 - 1; x <= x1 + 1; x++)
                    if (!_tiles.ContainsKey((x, y)))
                        jobs.Add((x, y));

            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (t, ct) =>
            {
                var path = Path.Combine(CacheFolder("terrarium", zoom.ToString(), t.X.ToString()), $"{t.Y}.png");
                if (!File.Exists(path))
                {
                    var bytes = await Http.GetByteArrayAsync($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{zoom}/{t.X}/{t.Y}.png", ct);
                    await File.WriteAllBytesAsync(path, bytes, ct);
                }

                using var image = Stb.Load(path, 4);
                var pixels = image.AsSpan<byte4>();
                var heights = new float[256 * 256];
                for (var i = 0; i < heights.Length && i < pixels.Length; i++)
                    heights[i] = pixels[i].X * 256f + pixels[i].Y + pixels[i].Z / 256f - 32768f;
                _tiles[t] = heights;
            });
        }

        /// Drops decoded tiles once there are many (a long route touches thousands).
        public void Trim()
        {
            if (_tiles.Count > 400)
                _tiles.Clear();
        }

        private (double X, double Y) Pixel(double lat, double lon)
        {
            var n = 256.0 * (1 << zoom);
            var x = (lon + 180.0) / 360.0 * n;
            var y = (1.0 - Math.Asinh(Math.Tan(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * n;
            return (x, y);
        }

        private (int X, int Y) Tile(double lat, double lon)
        {
            var (x, y) = Pixel(lat, lon);
            return ((int)(x / 256), (int)(y / 256));
        }

        private float At(int gx, int gy) =>
            _tiles.TryGetValue((gx >> 8, gy >> 8), out var tile) ? tile[(gy & 255) * 256 + (gx & 255)] : 0f;

        public float Elevation(GeoPoint p)
        {
            var (px, py) = Pixel(p.Lat, p.Lon);
            px -= 0.5;
            py -= 0.5;
            var ix = (int)Math.Floor(px);
            var iy = (int)Math.Floor(py);
            var tx = (float)(px - ix);
            var ty = (float)(py - iy);
            var top = At(ix, iy) + (At(ix + 1, iy) - At(ix, iy)) * tx;
            var bottom = At(ix, iy + 1) + (At(ix + 1, iy + 1) - At(ix, iy + 1)) * tx;
            return top + (bottom - top) * ty;
        }
    }

    // --- Land cover: ESA WorldCover cloud-optimised GeoTIFFs. ---

    /// Reads the 3Â° x 3Â° WorldCover files a few tiles at a time with HTTP range requests: the header and its
    /// overviews once, then only the 1024-pixel tiles under the samples, at the overview level that suits the spacing.
    private sealed class WorldCover
    {
        private sealed class Level
        {
            public int Width, Height, TileWidth, TileHeight, Predictor = 1;
            public long[] Offsets = [];
            public long[] Counts = [];
        }

        private sealed class Cog
        {
            public string Url = "";
            public bool Missing;
            public readonly List<Level> Levels = [];
            public readonly ConcurrentDictionary<(int Level, int Tile), byte[]> Tiles = new();
        }

        private readonly Dictionary<string, Cog> _files = [];

        private static string FileName(int lat, int lon) =>
            $"ESA_WorldCover_10m_2021_v200_{(lat < 0 ? 'S' : 'N')}{Math.Abs(lat):00}{(lon < 0 ? 'W' : 'E')}{Math.Abs(lon):000}_Map.tif";

        private static async Task<byte[]> Range(string url, long offset, long length, string cacheName)
        {
            var path = Path.Combine(CacheFolder("worldcover"), $"{cacheName}_{offset}_{length}.bin");
            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
                throw new FileNotFoundException(url);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(path, bytes);
            return bytes;
        }

        private async Task<Cog> Open(int lat, int lon)
        {
            var name = FileName(lat, lon);
            if (_files.TryGetValue(name, out var open))
                return open;

            var cog = new Cog { Url = $"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/{name}" };
            _files[name] = cog;

            try
            {
                var header = await Range(cog.Url, 0, 1 << 17, Path.GetFileNameWithoutExtension(name));
                async Task<byte[]> Bytes(long offset, long length) =>
                    offset + length <= header.Length ? header[(int)offset..(int)(offset + length)] : await Range(cog.Url, offset, length, Path.GetFileNameWithoutExtension(name));

                if (header[0] != 'I' || BitConverter.ToUInt16(header, 2) != 42)
                    throw new InvalidDataException("not a little-endian classic TIFF");

                long ifd = BitConverter.ToUInt32(header, 4);
                while (ifd != 0)
                {
                    var countBytes = await Bytes(ifd, 2);
                    int count = BitConverter.ToUInt16(countBytes, 0);
                    var entries = await Bytes(ifd + 2, count * 12 + 4);
                    var level = new Level();
                    var mask = false;

                    for (var e = 0; e < count; e++)
                    {
                        var o = e * 12;
                        var tag = BitConverter.ToUInt16(entries, o);
                        var type = BitConverter.ToUInt16(entries, o + 2);
                        var n = (int)BitConverter.ToUInt32(entries, o + 4);
                        var size = type == 3 ? 2 : 4;
                        uint Single() => type == 3 ? BitConverter.ToUInt16(entries, o + 8) : BitConverter.ToUInt32(entries, o + 8);

                        async Task<long[]> Array()
                        {
                            var raw = n * size <= 4 ? entries[(o + 8)..(o + 8 + n * size)] : await Bytes(BitConverter.ToUInt32(entries, o + 8), n * size);
                            var values = new long[n];
                            for (var i = 0; i < n; i++)
                                values[i] = size == 2 ? BitConverter.ToUInt16(raw, i * 2) : BitConverter.ToUInt32(raw, i * 4);
                            return values;
                        }

                        switch (tag)
                        {
                            case 254: mask = (Single() & 4) != 0; break;
                            case 256: level.Width = (int)Single(); break;
                            case 257: level.Height = (int)Single(); break;
                            case 317: level.Predictor = (int)Single(); break;
                            case 322: level.TileWidth = (int)Single(); break;
                            case 323: level.TileHeight = (int)Single(); break;
                            case 324: level.Offsets = await Array(); break;
                            case 325: level.Counts = await Array(); break;
                        }
                    }

                    if (!mask && level.TileWidth > 0 && level.Offsets.Length > 0)
                        cog.Levels.Add(level);

                    ifd = BitConverter.ToUInt32(entries, count * 12);
                }
            }
            catch (FileNotFoundException)
            {
                cog.Missing = true; // no land in that square: open sea
            }

            return cog;
        }

        private static byte[] Inflate(byte[] data, int width, int height, int predictor)
        {
            using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            var pixels = new byte[width * height];
            var read = 0;
            int n;
            while (read < pixels.Length && (n = input.Read(pixels, read, pixels.Length - read)) > 0)
                read += n;

            if (predictor == 2)
            {
                for (var y = 0; y < height; y++)
                    for (var x = 1; x < width; x++)
                        pixels[y * width + x] += pixels[y * width + x - 1];
            }

            return pixels;
        }

        /// The class under each sample (0 where there's no data).
        public async Task<byte[]> Classes(GeoPoint[] samples, double spacing)
        {
            var needed = new HashSet<(string File, int Level, int Tile)>();
            var lookups = new (Cog? Cog, int Level, int Tile, int Index)[samples.Length];

            for (var i = 0; i < samples.Length; i++)
            {
                var (lat, lon) = samples[i];
                var fileLat = (int)Math.Floor(lat / 3.0) * 3;
                var fileLon = (int)Math.Floor(lon / 3.0) * 3;
                var cog = await Open(fileLat, fileLon);
                if (cog.Missing || cog.Levels.Count == 0)
                    continue;

                // The coarsest level whose pixels are still finer than the spacing.
                var level = 0;
                for (var l = cog.Levels.Count - 1; l >= 0; l--)
                {
                    var metres = 3.0 / cog.Levels[l].Width * 111000.0 * Math.Cos(lat * Math.PI / 180.0);
                    if (metres <= spacing)
                    {
                        level = l;
                        break;
                    }
                }

                var L = cog.Levels[level];
                var px = Math.Clamp((int)((lon - fileLon) / 3.0 * L.Width), 0, L.Width - 1);
                var py = Math.Clamp((int)((fileLat + 3 - lat) / 3.0 * L.Height), 0, L.Height - 1);
                var across = (L.Width + L.TileWidth - 1) / L.TileWidth;
                var tile = py / L.TileHeight * across + px / L.TileWidth;
                lookups[i] = (cog, level, tile, py % L.TileHeight * L.TileWidth + px % L.TileWidth);
                needed.Add((cog.Url, level, tile));
            }

            var byUrl = _files.Values.ToDictionary(c => c.Url);
            await Parallel.ForEachAsync(needed, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (job, _) =>
            {
                var cog = byUrl[job.File];
                if (cog.Tiles.ContainsKey((job.Level, job.Tile)))
                    return;

                var L = cog.Levels[job.Level];
                if (L.Counts[job.Tile] == 0)
                {
                    cog.Tiles[(job.Level, job.Tile)] = new byte[L.TileWidth * L.TileHeight];
                    return;
                }

                var data = await Range(cog.Url, L.Offsets[job.Tile], L.Counts[job.Tile], Path.GetFileNameWithoutExtension(cog.Url));
                cog.Tiles[(job.Level, job.Tile)] = Inflate(data, L.TileWidth, L.TileHeight, L.Predictor);
            });

            var classes = new byte[samples.Length];
            for (var i = 0; i < lookups.Length; i++)
            {
                var (cog, level, tile, index) = lookups[i];
                classes[i] = cog is not null && cog.Tiles.TryGetValue((level, tile), out var pixels) ? pixels[index] : (byte)0;
            }

            return classes;
        }
    }

    // --- Roads and rail: OpenStreetMap through Overpass. ---

    private static string Box(double south, double west, double north, double east) =>
        string.Create(CultureInfo.InvariantCulture, $"{south:0.######},{west:0.######},{north:0.######},{east:0.######}");

    /// The railway values to ask for, given the classes wanted (the classes are told apart afterwards by their tags).
    private static IEnumerable<string> RailwayValues(RailClass[] classes)
    {
        var values = new HashSet<string>();
        foreach (var c in classes)
        {
            switch (c)
            {
                case RailClass.Main or RailClass.Branch or RailClass.Industrial or RailClass.Yard:
                    values.UnionWith(["rail", "preserved"]);
                    break;
                case RailClass.Transit:
                    values.UnionWith(["light_rail", "subway", "tram", "monorail", "funicular"]);
                    break;
                case RailClass.NarrowGauge:
                    values.Add("narrow_gauge");
                    break;
                case RailClass.Disused:
                    values.UnionWith(["disused", "abandoned"]);
                    break;
            }
        }

        return values;
    }

    /// Every wanted way touching the tiles, whole: from Geofabrik's regional extracts, or from Overpass if those
    /// can't be had.
    private async Task<(List<NetworkWay> Ways, List<NetworkNode> Nodes)> OpenStreetMap(RouteInfo route, List<TileId> tiles, RoadClass[] roads, RailClass[] rails, CancellationToken token)
    {
        try
        {
            return await FromExtracts(route, tiles, roads, rails, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"OSM extract: {e.Message}; asking Overpass instead");
            return await FromOverpass(route, tiles, roads, rails, token);
        }
    }

    /// The smallest Geofabrik region under each corner of the selection (more than one where it crosses a border),
    /// each downloaded once and read for the part of the selection it holds.
    private async Task<(List<NetworkWay> Ways, List<NetworkNode> Nodes)> FromExtracts(RouteInfo route, List<TileId> tiles, RoadClass[] roads, RailClass[] rails, CancellationToken token)
    {
        var grid = route.Grid!;
        var projection = route.Projection!;
        Say("OpenStreetMap: finding the region", 0.82f);
        var regions = await OsmExtract.Regions(token);

        var corners = tiles.SelectMany(t => new[] { t, t with { I = t.I + 1 }, t with { J = t.J + 1 }, new TileId(t.I + 1, t.J + 1) })
            .Distinct().Select(t => projection.Inverse(grid.Corner(t))).ToList();
        var byRegion = corners.GroupBy(p => OsmExtract.Smallest(regions, p)?.Id ?? "").ToList();
        if (byRegion.Any(g => g.Key.Length == 0))
            throw new InvalidOperationException("part of the selection is outside every Geofabrik region");

        var box = Bounds(corners);
        var found = new Dictionary<long, NetworkWay>();
        var foundNodes = new Dictionary<long, NetworkNode>();
        var r = 0;
        foreach (var group in byRegion)
        {
            token.ThrowIfCancellationRequested();
            var region = regions.First(x => x.Id == group.Key);
            var path = await OsmExtract.Download(region, (done, total) =>
                Say(total > 0 ? $"Downloading {region.Name} map data: {done >> 20} of {total >> 20} MB" : $"Downloading {region.Name} map data: {done >> 20} MB",
                    0.82f + 0.08f * (total > 0 ? done / (float)total : 0f)), token);

            var label = $"{region.Name} ({++r} of {byRegion.Count})";
            var (ways, nodes) = await Task.Run(() => OsmExtract.Read(path, roads, rails, box, s => Say($"{label}: {s}", 0.9f), token), token);
            foreach (var way in ways)
                found[way.Id] = way;
            foreach (var node in nodes)
                foundNodes[node.Id] = node;
        }

        return ([.. found.Values], [.. foundNodes.Values]);
    }

    /// Every wanted way touching the tiles, whole, from the public Overpass servers. Big selections are asked for in
    /// pieces of about 20 km.
    private async Task<(List<NetworkWay> Ways, List<NetworkNode> Nodes)> FromOverpass(RouteInfo route, List<TileId> tiles, RoadClass[] roads, RailClass[] rails, CancellationToken token)
    {
        var grid = route.Grid!;
        var projection = route.Projection!;
        var perChunk = Math.Max(1, (int)(20000 / grid.TileSize));
        var chunks = tiles.GroupBy(t => (Math.Floor(t.I / (double)perChunk), Math.Floor(t.J / (double)perChunk))).ToList();
        var highways = roads.SelectMany(Detail.HighwayValues).ToList();
        var railways = RailwayValues(rails).ToList();
        var found = new Dictionary<long, NetworkWay>();
        var foundNodes = new Dictionary<long, NetworkNode>();

        for (var c = 0; c < chunks.Count; c++)
        {
            token.ThrowIfCancellationRequested();
            var corners = chunks[c].SelectMany(t => new[] { t, t with { I = t.I + 1 }, t with { J = t.J + 1 }, new TileId(t.I + 1, t.J + 1) })
                .Select(t => projection.Inverse(grid.Corner(t))).ToList();
            var (south, west, north, east) = Bounds(corners);
            var box = Box(south, west, north, east);

            // Exact matches rather than one regular expression (Overpass answers those from its index), and a short
            // server timeout: busy servers turn away queries that ask for a long slot.
            var parts = string.Concat(highways.Select(v => $"way[highway={v}]({box});")) + string.Concat(railways.Select(v => $"way[railway={v}]({box});")) +
                        (railways.Count > 0 ? $"node[railway=switch]({box});node[railway=railway_crossing]({box});" : "");
            var query = $"[out:json][timeout:90];({parts});out body geom;";

            Say($"OpenStreetMap: area {c + 1} of {chunks.Count}", 0.82f + 0.15f * c / chunks.Count);
            var cache = Path.Combine(CacheFolder("overpass"), $"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(query)))[..16]}.json");
            string json;
            if (File.Exists(cache))
            {
                json = await File.ReadAllTextAsync(cache, token);
            }
            else
            {
                json = await Overpass(query, token);
                await File.WriteAllTextAsync(cache, json, token);
            }

            Parse(json, roads, rails, found, foundNodes);
        }

        // Only the switches and crossings on the lines fetched.
        var railNodes = found.Values.Where(w => w.Rail).SelectMany(w => w.Nodes).ToHashSet();
        return ([.. found.Values], [.. foundNodes.Values.Where(n => railNodes.Contains(n.Id))]);
    }

    private static void Parse(string json, RoadClass[] roads, RailClass[] rails, Dictionary<long, NetworkWay> found, Dictionary<long, NetworkNode> foundNodes)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.GetProperty("elements"u8).EnumerateArray())
        {
            if (element.GetProperty("type"u8).GetString() == "node" && element.TryGetProperty("tags"u8, out var nodeTags))
            {
                var nodeKept = new Dictionary<string, string>();
                foreach (var tag in nodeTags.EnumerateObject())
                    if (NetworkNode.Kept(tag.Name) && tag.Value.GetString() is { } value)
                        nodeKept[tag.Name] = value;
                if (NetworkNode.Wanted(nodeKept.GetValueOrDefault("railway", "")))
                {
                    var nodeId = element.GetProperty("id"u8).GetInt64();
                    foundNodes[nodeId] = new NetworkNode
                    {
                        Id = nodeId, Lat = element.GetProperty("lat"u8).GetDouble(), Lon = element.GetProperty("lon"u8).GetDouble(), Tags = nodeKept,
                    };
                }

                continue;
            }

            if (element.GetProperty("type"u8).GetString() != "way" || !element.TryGetProperty("geometry"u8, out var geometry))
                continue;

            var id = element.GetProperty("id"u8).GetInt64();
            if (found.ContainsKey(id))
                continue;

            var tags = element.TryGetProperty("tags"u8, out var t) ? t : default;
            string Tag(string key) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

            bool rail;
            int cls;
            if (Detail.Rail(Tag) is { } railClass && rails.Contains(railClass))
                (rail, cls) = (true, (int)railClass);
            else if (Detail.Road(Tag("highway")) is { } roadClass && roads.Contains(roadClass))
                (rail, cls) = (false, (int)roadClass);
            else
                continue;

            var lat = new List<double>();
            var lon = new List<double>();
            foreach (var node in geometry.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    continue;
                lat.Add(node.GetProperty("lat"u8).GetDouble());
                lon.Add(node.GetProperty("lon"u8).GetDouble());
            }

            if (lat.Count < 2)
                continue;

            var nodes = element.TryGetProperty("nodes"u8, out var n) ? n.EnumerateArray().Select(x => x.GetInt64()).ToArray() : [];
            var kept = new Dictionary<string, string>();
            foreach (var key in RouteNetwork.KeptTags)
                if (Tag(key) is { Length: > 0 } value)
                    kept[key] = value;

            found[id] = new NetworkWay { Id = id, Rail = rail, Class = cls, Nodes = nodes, Lat = [.. lat], Lon = [.. lon], Tags = kept };
        }
    }

    private static readonly (string Name, string Url)[] OverpassServers =
    [
        ("overpass-api.de", "https://overpass-api.de/api/interpreter"),
        ("VK Maps", "https://maps.mail.ru/osm/tools/overpass/api/interpreter"),
        ("Kumi Systems", "https://overpass.kumi.systems/api/interpreter"),
        ("private.coffee", "https://overpass.private.coffee/api/interpreter"),
    ];

    private const int OverpassRounds = 6;

    /// The public Overpass servers are often busy or stalled: each gets two minutes, then the next one is tried, and
    /// after a round of all of them a longer wait before the next round.
    private async Task<string> Overpass(string query, CancellationToken token)
    {
        Exception? last = null;
        for (var round = 0; round < OverpassRounds; round++)
        {
            foreach (var (name, url) in OverpassServers)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    _status = $"OpenStreetMap: asking {name}";
                    using var response = await Http.PostAsync(url, new FormUrlEncodedContent([new("data", query)]), attempt.Token);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(attempt.Token);
                    if (json.TrimStart().StartsWith('{'))
                        return json;
                    last = new InvalidDataException("not JSON");
                }
                catch (Exception e) when (e is HttpRequestException or InvalidDataException || (e is OperationCanceledException && !token.IsCancellationRequested))
                {
                    last = e is OperationCanceledException ? new TimeoutException("no answer in 2 minutes") : e;
                    Log.Warn($"Overpass {name}: {last.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }

            Say($"OpenStreetMap servers busy ({last?.Message}); trying again", Progress);
            await Task.Delay(TimeSpan.FromSeconds(15 + round * 15), token);
        }

        throw new HttpRequestException($"No OpenStreetMap server answered: {last?.Message}", last);
    }
}
