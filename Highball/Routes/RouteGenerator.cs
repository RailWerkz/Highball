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
///   heights from the USGS's 3D Elevation Program for tiles marked lidar, where it has them (bare earth, 1 m lidar in much of
///   the US), and the AWS Terrain Tiles (Terrarium PNGs at zoom 15, metres to a 256th) everywhere else, sampled on the
///   route's lattice, and eased to meet any tile already built beside them;
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

        /// The tiles to build with lidar heights; all of them if null.
        public IReadOnlySet<TileId>? Lidar { get; init; }

        /// Just fetch where the rail runs under the tiles, into the network: nothing's built.
        public bool FindTrackOnly { get; init; }

        /// Just fetch the roads under the tiles and put every one back as mapped, edited or not (a bridge taken off, a road
        /// moved or deleted): nothing's built, and the track's left alone.
        public bool RestoreRoadsOnly { get; init; }

        /// With RestoreRoadsOnly: every mapped road in the world is taken out first, not only those in the tiles.
        public bool EveryRoad { get; init; }

        public int RoadLevel { get; init; }
        public int RailLevel { get; init; }

        /// Drop every way fetched from OpenStreetMap before merging what this build fetches (ways edited by hand
        /// stay), so a route regenerated at less detail loses what it no longer asks for.
        public bool ReplaceNetwork { get; init; }
    }

    public const string Attribution = "Elevation: U.S. Geological Survey 3DEP; AWS Terrain Tiles (SRTM, NED, GMTED and others). Land cover: ESA WorldCover 2021, CC BY 4.0. Roads and rail: (c) OpenStreetMap contributors, ODbL.";

    private static readonly HttpClient Http = CreateClient();

    private volatile string _status = "";
    private volatile bool _running;
    private CancellationTokenSource? _cancel;

    public string Status => _status;
    public bool Running => _running;
    public float Progress { get; private set; }

    /// Bumped whenever a tile or the network is written, so the map redraws what's built.
    public int Version => Volatile.Read(ref _version);
    private int _version;

    /// How many tiles are built at once. It says nothing about how hard the services are asked: that's up to their Limits, shared by all.
    private static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// What the services are asked for at most, however many tiles are building. The USGS's is a public service that's easily overloaded,
    /// so it gets two requests at a time, spaced out. The others are files on S3, which take more.
    private static readonly Limit UsgsLimit = new("USGS 3DEP", 2, TimeSpan.FromMilliseconds(500));
    private static readonly Limit TerrariumLimit = new("AWS Terrain Tiles", 6, TimeSpan.Zero);
    private static readonly Limit CoverLimit = new("ESA WorldCover", 4, TimeSpan.Zero);

    /// One service's limit: so many requests at once, a gap between their starts, and when it's said no or failed, everyone holds off
    /// together for as long as it asks, or longer each time.
    private sealed class Limit(string name, int slots, TimeSpan gap)
    {
        private readonly SemaphoreSlim _slots = new(slots);
        private readonly object _lock = new();
        private DateTime _next = DateTime.MinValue;
        private DateTime _heldUntil = DateTime.MinValue;
        private double _backOff;
        private long _waited;
        private int _requests;
        private int _refused;

        public void Reset() => (_waited, _requests, _refused) = (0, 0, 0);

        public string Report => $"{name}: {_requests} requests, {_refused} failed or refused, {_waited / (double)System.Diagnostics.Stopwatch.Frequency:0}s spent waiting for a turn";

        /// Takes a turn, for as long as the request's running: dispose it to give it back.
        public async Task<IDisposable> Turn(CancellationToken token)
        {
            var from = System.Diagnostics.Stopwatch.GetTimestamp();
            await _slots.WaitAsync(token);
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (_lock)
                    {
                        var now = DateTime.UtcNow;
                        var at = _next > _heldUntil ? _next : _heldUntil;
                        wait = at - now;
                        if (wait <= TimeSpan.Zero)
                        {
                            _next = now + gap;
                            break;
                        }
                    }

                    await Task.Delay(wait, token);
                }
            }
            catch
            {
                _slots.Release();
                throw;
            }

            Interlocked.Add(ref _waited, System.Diagnostics.Stopwatch.GetTimestamp() - from);
            Interlocked.Increment(ref _requests);
            return new Release(_slots);
        }

        public void Went() { lock (_lock) _backOff = 0.0; }

        /// It failed or said no: everyone holds off. It can say how long.
        public void HoldOff(TimeSpan? asked = null)
        {
            Interlocked.Increment(ref _refused);
            lock (_lock)
            {
                _backOff = Math.Min(Math.Max(_backOff * 2.0, 5.0), 300.0);
                var seconds = Math.Max(_backOff, asked?.TotalSeconds ?? 0.0);
                var until = DateTime.UtcNow.AddSeconds(seconds);
                if (until > _heldUntil)
                    _heldUntil = until;
                Log.Warn($"{name}: holding off for {seconds:0}s");
            }
        }

        private sealed class Release(SemaphoreSlim slots) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                    slots.Release();
            }
        }
    }

    /// Writes a file in the cache whole or not at all, since another tile may want the same one at the same moment.
    private static async Task WriteCache(string path, byte[] bytes, CancellationToken token = default)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, bytes, token);
        try
        {
            File.Move(temporary, path, true);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporary);
        }
    }

    /// Whether a failed answer's the service being busy, and how long it asked to be left for.
    private static bool Busy(HttpResponseMessage response) =>
        (int)response.StatusCode is 408 or 429 or 500 or 502 or 503 or 504;

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    /// Time spent on each step of building a tile, added up across the tiles building at once.
    private sealed class StepTimes
    {
        private static readonly string[] Names = ["lidar", "standard heights", "meeting neighbours", "land cover", "saving"];
        private readonly long[] _ticks = new long[Names.Length];
        public const int Lidar = 0, Standard = 1, Meet = 2, Cover = 3, Save = 4;

        public void Add(int step, long from) => Interlocked.Add(ref _ticks[step], System.Diagnostics.Stopwatch.GetTimestamp() - from);

        public void Log(int done, int total, TimeSpan elapsed)
        {
            var steps = string.Join(", ", Names.Select((n, i) => string.Create(CultureInfo.InvariantCulture, $"{n} {_ticks[i] / (double)System.Diagnostics.Stopwatch.Frequency / Math.Max(done, 1):0.00}s")));
            RailWerkz.Core.Logging.Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Route builder: {done} of {total} tiles in {elapsed.TotalSeconds:0}s on {Workers} workers, {elapsed.TotalSeconds / Math.Max(done, 1):0.00}s a tile. Each tile's steps: {steps}"));
            RailWerkz.Core.Logging.Log.Info($"Route builder: {UsgsLimit.Report}; {TerrariumLimit.Report}; {CoverLimit.Report}");
        }
    }

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
            using var counted = Highball.App.Background.Begin("Route build");
            try
            {
                await Build(request, token);
                if (!request.FindTrackOnly && !request.RestoreRoadsOnly)
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
        var usgs = new Usgs3dep();
        var unavailable = 0;
        var cover = new WorldCover();

        if (r.FindTrackOnly)
        {
            Say("Asking OpenStreetMap where the track is", 0.5f);
            var (railWays, railNodes) = await OpenStreetMap(route, r.Tiles, [], rails.Classes, token);
            token.ThrowIfCancellationRequested();
            r.Network.Merge(railWays, railNodes);
            r.Network.Save(route);
            Interlocked.Increment(ref _version);
            Say($"{railWays.Count} rail lines found", 1f);
            return;
        }

        if (r.RestoreRoadsOnly)
        {
            // The roads in the tiles replaced with exactly what the road detail asks for: every mapped road with a point in them
            // goes (edited or not), then what's fetched goes in. At NONE nothing's fetched, so they're all gone. Roads drawn by hand stay.
            var tileSet = r.Tiles.ToHashSet();
            bool InTiles(NetworkWay w)
            {
                if (r.EveryRoad)
                    return true;
                for (var i = 0; i < w.Lat.Length; i++)
                    if (tileSet.Contains(grid.TileAt(projection.Forward(new GeoPoint(w.Lat[i], w.Lon[i])))))
                        return true;
                return false;
            }

            List<NetworkWay> roadWays = [];
            List<NetworkNode> roadNodes = [];
            if (roads.Classes.Length > 0)
            {
                Say("Asking OpenStreetMap for the roads", 0.5f);
                (roadWays, roadNodes) = await OpenStreetMap(route, r.Tiles, roads.Classes, [], token);
                token.ThrowIfCancellationRequested();
            }

            var (removed, added) = r.Network.ReplaceRoads(InTiles, roadWays.Where(w => !w.Rail), roadNodes);
            r.Network.Save(route);
            Interlocked.Increment(ref _version);
            Say($"Roads ({roads.Name.ToLowerInvariant()}): {removed} taken out, {added} put in", 1f);
            return;
        }

        // Tiles nearest the middle of the selection first.
        var middle = new RoutePoint(r.Tiles.Average(t => t.I + 0.5), r.Tiles.Average(t => t.J + 0.5));
        var tiles = r.Tiles.OrderBy(t => Math.Pow(t.I + 0.5 - middle.East, 2) + Math.Pow(t.J + 0.5 - middle.North, 2)).ToList();
        var share = 0.8f / tiles.Count;
        var pending = tiles.ToHashSet();

        // The edges of the tiles built so far, for those beside them to meet; pending and these are under the one lock.
        var meeting = new object();
        var edges = new Dictionary<TileId, float[]>();
        var times = new StepTimes();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var done = 0;
        var lastTold = 0L;
        UsgsLimit.Reset();
        TerrariumLimit.Reset();
        CoverLimit.Reset();
        Say($"Building {tiles.Count} tile{(tiles.Count == 1 ? "" : "s")}, {Workers} at a time", 0f);

        // Tiles meet their neighbours in the order they're listed, as if they were built one by one, whichever's heights come first:
        // the steps left at the corners depend on that order, and it's kept to the one that's known to leave the least.
        var turns = tiles.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        await Parallel.ForEachAsync(tiles.Select((tile, place) => (tile, place)), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, async (job, ct) =>
        {
            var (tile, place) = job;
            var samples = grid.Samples;
            var geo = new GeoPoint[samples * samples];
            Parallel.For(0, samples, z =>
            {
                for (var x = 0; x < samples; x++)
                    geo[z * samples + x] = projection.Inverse(grid.Sample(tile, x, z));
            });

            var step = System.Diagnostics.Stopwatch.GetTimestamp();
            float[]? exact = null;
            if (r.Lidar is null || r.Lidar.Contains(tile))
            {
                try
                {
                    exact = await usgs.Heights(geo, grid.Spacing, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Interlocked.Increment(ref unavailable);
                    Log.Warn($"USGS heights: tile {tile} fell back to the coarser source ({e.Message})");
                }
            }

            times.Add(StepTimes.Lidar, step);
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            var complete = exact is not null && !exact.Any(float.IsNaN);
            var heights = new float[geo.Length];
            if (!complete)
            {
                var near = await elevation.Fetch(Bounds(geo), ct);
                Parallel.For(0, samples, z =>
                {
                    for (var x = 0; x < samples; x++)
                        heights[z * samples + x] = Terrarium.Elevation(near, zoom: 15, geo[z * samples + x]);
                });
            }

            var source = exact is null ? "AWS Terrain Tiles" : complete ? "USGS 3DEP" : "USGS 3DEP, AWS Terrain Tiles";
            if (exact is not null)
                Usgs3dep.Blend(heights, exact, samples);
            times.Add(StepTimes.Standard, step);

            // A tile meets those before it, and those after it meet it. What matters is that its edges are there for them before
            // it stops being pending, not that it's on the disk yet.
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            if (place > 0)
                await turns[place - 1].Task.WaitAsync(ct);
            lock (meeting)
            {
                MeetNeighbours(route, tile, heights, samples, pending, edges);
                edges[tile] = EdgesOf(heights, samples);
                pending.Remove(tile);
            }

            turns[place].SetResult();
            times.Add(StepTimes.Meet, step);
            step = System.Diagnostics.Stopwatch.GetTimestamp();
            var cells = grid.CoverCells;
            var cellSize = grid.TileSize / cells;
            var corner = grid.Corner(tile);
            var centres = new GeoPoint[cells * cells];
            for (var z = 0; z < cells; z++)
                for (var x = 0; x < cells; x++)
                    centres[z * cells + x] = projection.Inverse(new RoutePoint(corner.East + (x + 0.5) * cellSize, corner.North + grid.TileSize - (z + 0.5) * cellSize));
            var classes = await cover.Classes(centres, cellSize);
            times.Add(StepTimes.Cover, step);

            step = System.Diagnostics.Stopwatch.GetTimestamp();
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
                HeightSource = source,
            }, heights, classes);
            built.Save(route.TileFolder(tile));
            times.Add(StepTimes.Save, step);

            // (The map reads what is built again each time it is told, which is not to be every tile of thousands.)
            var now = watch.ElapsedMilliseconds;
            var told = Interlocked.Read(ref lastTold);
            if (now - told > 2000 && Interlocked.CompareExchange(ref lastTold, now, told) == told)
                Interlocked.Increment(ref _version);
            elevation.Trim();
            var finished = Interlocked.Increment(ref done);
            Say($"Built {finished} of {tiles.Count} tiles, {Workers} at a time", finished * share);
            if (finished % 200 == 0)
                times.Log(finished, tiles.Count, watch.Elapsed);
        });

        Interlocked.Increment(ref _version);
        times.Log(done, tiles.Count, watch.Elapsed);

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
            Interlocked.Increment(ref _version);
            Say($"{ways.Count(w => w.Rail)} rail lines, {ways.Count(w => !w.Rail)} roads, {nodes.Count} switches and crossings tagged", 0.98f);
        }

        if (unavailable > 0)
            Say($"No USGS heights for {unavailable} of {tiles.Count} tiles (coarser source used)", 1f);
        Progress = 1f;
    }

    /// How many samples in from its edge a tile eases to meet a tile already built beside it.
    private const int MeetSamples = 150;

    /// Eases a tile's heights to meet the edges of the tiles already built beside it (those still to be built this time meet
    /// this one in turn), so lidar beside standard heights leaves no step. Rows run north to south.
    private static void MeetNeighbours(RouteInfo route, TileId tile, float[] heights, int samples, HashSet<TileId> pending, Dictionary<TileId, float[]> edges)
    {
        var last = samples - 1;
        float[]? Beside(int di, int dj)
        {
            var near = new TileId(tile.I + di, tile.J + dj);
            if (pending.Contains(near))
                return null;
            if (edges.TryGetValue(near, out var known))
                return known;
            var folder = route.TileFolder(near);
            if (!File.Exists(Path.Combine(folder, "Tile.json")))
                return null;
            return edges[near] = EdgesOf(RouteTile.Load(folder).Heights, samples);
        }

        // One neighbour's edge: from(k) its sample, to(k) the one here that's the same place; in(k, n) the sample n in from there.
        void Meet(float[]? other, Func<int, int> from, Func<int, int> to, Func<int, int, int> inward)
        {
            if (other is null)
                return;
            Parallel.For(0, samples, k =>
            {
                var gap = other[from(k)] - heights[to(k)];
                for (var n = 0; n < MeetSamples; n++)
                {
                    var t = 1f - (float)n / MeetSamples;
                    heights[inward(k, n)] += gap * t * t * (3f - 2f * t);
                }
            });
        }

        // North and south first, then east and west from what that's made of the edges, so each corner ends up on both.
        Meet(Beside(0, 1), k => samples + k, k => k, (k, n) => n * samples + k);
        Meet(Beside(0, -1), k => k, k => last * samples + k, (k, n) => (last - n) * samples + k);
        Meet(Beside(-1, 0), k => 3 * samples + k, k => k * samples, (k, n) => k * samples + n);
        Meet(Beside(1, 0), k => 2 * samples + k, k => k * samples + last, (k, n) => k * samples + last - n);
    }

    /// A tile's four edges, one after another: its first row, last row, first column and last column.
    private static float[] EdgesOf(float[] heights, int samples)
    {
        var last = samples - 1;
        var edges = new float[samples * 4];
        for (var k = 0; k < samples; k++)
        {
            edges[k] = heights[k];
            edges[samples + k] = heights[last * samples + k];
            edges[2 * samples + k] = heights[k * samples];
            edges[3 * samples + k] = heights[k * samples + last];
        }

        return edges;
    }

    private static (double South, double West, double North, double East) Bounds(IReadOnlyCollection<GeoPoint> points) =>
        (points.Min(p => p.Lat), points.Min(p => p.Lon), points.Max(p => p.Lat), points.Max(p => p.Lon));

    // --- Elevation: USGS 3DEP. ---

    /// The USGS's elevation service, which mosaics the best bare-earth data it has for a place. Each tile's lattice is
    /// read from one image in degrees, at twice the lattice's resolution.
    private sealed class Usgs3dep
    {
        private const string Service = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage";
        private const double Margin = 0.0002;
        private const int MaxImage = 8000;
        private const int Attempts = 6;
        /// The service's mark for no data is -999999.
        private const float NoData = -100000f;

        /// How many samples the coarser source fades into this one over, where this one's data stops.
        private const int FadeSamples = 50;

        /// The height at each sample (NaN where there's no data), or null if there's none anywhere in the tile.
        public async Task<float[]?> Heights(GeoPoint[] samples, double spacing, CancellationToken token)
        {
            var (south, west, north, east) = Bounds(samples);
            if (north < 17.0 || south > 72.0 || west > -64.0)
                return null;

            south -= Margin;
            west -= Margin;
            north += Margin;
            east += Margin;
            var middle = (south + north) * 0.5 * Math.PI / 180.0;
            var width = Math.Clamp((int)Math.Ceiling((east - west) * 111320.0 * Math.Cos(middle) / (spacing * 0.5)), 2, MaxImage);
            var height = Math.Clamp((int)Math.Ceiling((north - south) * 110940.0 / (spacing * 0.5)), 2, MaxImage);

            var box = string.Create(CultureInfo.InvariantCulture, $"{west:F8},{south:F8},{east:F8},{north:F8}");
            var path = Path.Combine(CacheFolder("usgs3dep"), string.Create(CultureInfo.InvariantCulture, $"{south:F6}_{west:F6}_{width}x{height}.tif"));
            var cached = File.Exists(path);
            var bytes = cached ? await File.ReadAllBytesAsync(path, token) : await Fetch(box, width, height, token);
            var (w, h, image) = ReadTiff(bytes);

            var heights = new float[samples.Length];
            var found = 0;
            Parallel.For(0, samples.Length, () => 0, (i, _, count) =>
            {
                var px = (samples[i].Lon - west) / (east - west) * w - 0.5;
                var py = (north - samples[i].Lat) / (north - south) * h - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(px), 0, w - 2);
                var y0 = Math.Clamp((int)Math.Floor(py), 0, h - 2);
                var tx = (float)(px - x0);
                var ty = (float)(py - y0);
                var (a, b, c, d) = (image[y0 * w + x0], image[y0 * w + x0 + 1], image[(y0 + 1) * w + x0], image[(y0 + 1) * w + x0 + 1]);
                if (a < NoData || b < NoData || c < NoData || d < NoData)
                {
                    heights[i] = float.NaN;
                    return count;
                }

                var top = a + (b - a) * tx;
                heights[i] = top + (c + (d - c) * tx - top) * ty;
                return count + 1;
            }, count => Interlocked.Add(ref found, count));

            if (found == 0)
                return null;

            // (Nowhere to be had means nothing worth keeping.)
            if (!cached)
                await File.WriteAllBytesAsync(path, bytes, token);
            return heights;
        }

        /// Replaces heights with exact, fading from one to the other beside where exact has no data so there's no step.
        public static void Blend(float[] heights, float[] exact, int samples)
        {
            // How many samples each is from the nearest without data, in two sweeps.
            var far = new int[exact.Length];
            for (var i = 0; i < far.Length; i++)
                far[i] = float.IsNaN(exact[i]) ? 0 : FadeSamples;
            for (var z = 0; z < samples; z++)
                for (var x = 0; x < samples; x++)
                {
                    if (x > 0)
                        far[z * samples + x] = Math.Min(far[z * samples + x], far[z * samples + x - 1] + 1);
                    if (z > 0)
                        far[z * samples + x] = Math.Min(far[z * samples + x], far[(z - 1) * samples + x] + 1);
                }

            for (var z = samples - 1; z >= 0; z--)
                for (var x = samples - 1; x >= 0; x--)
                {
                    if (x < samples - 1)
                        far[z * samples + x] = Math.Min(far[z * samples + x], far[z * samples + x + 1] + 1);
                    if (z < samples - 1)
                        far[z * samples + x] = Math.Min(far[z * samples + x], far[(z + 1) * samples + x] + 1);
                }

            for (var i = 0; i < heights.Length; i++)
                if (far[i] > 0)
                    heights[i] += (exact[i] - heights[i]) * far[i] / FadeSamples;
        }

        private static async Task<byte[]> Fetch(string box, int width, int height, CancellationToken token)
        {
            var url = $"{Service}?bbox={box}&bboxSR=4326&imageSR=4326&size={width},{height}&format=tiff&pixelType=F32&noData=-999999" +
                "&interpolation=RSP_BilinearInterpolation&adjustAspectRatio=false&f=image";

            // (The service answers 502 now and then under load, and an error can come back as a page.)
            for (var attempt = 1; ; attempt++)
            {
                string? problem;
                using (await UsgsLimit.Turn(token))
                {
                    try
                    {
                        using var response = await Http.GetAsync(url, token);
                        if (response.IsSuccessStatusCode)
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync(token);
                            if (bytes.Length > 8 && bytes[0] == 'I' && bytes[1] == 'I')
                            {
                                UsgsLimit.Went();
                                return bytes;
                            }

                            problem = "the service didn't send an image";
                            UsgsLimit.HoldOff();
                        }
                        else
                        {
                            problem = $"the service answered {(int)response.StatusCode}";
                            UsgsLimit.HoldOff(Busy(response) ? RetryAfter(response) : null);
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
                    {
                        problem = e.Message;
                        UsgsLimit.HoldOff();
                    }
                }

                if (attempt >= Attempts)
                    throw new InvalidDataException(problem);
            }
        }

        /// An uncompressed, little-endian, tiled or stripped TIFF of one 32-bit float channel, which is what the service sends.
        private static (int Width, int Height, float[] Pixels) ReadTiff(byte[] data)
        {
            var (width, height, tileWidth, tileHeight, rows, compression, bits, format) = (0, 0, 0, 0, 0, 1, 0, 0);
            long[] offsets = [];
            var ifd = (int)BitConverter.ToUInt32(data, 4);
            int count = BitConverter.ToUInt16(data, ifd);
            for (var e = 0; e < count; e++)
            {
                var o = ifd + 2 + e * 12;
                var tag = BitConverter.ToUInt16(data, o);
                var type = BitConverter.ToUInt16(data, o + 2);
                var n = (int)BitConverter.ToUInt32(data, o + 4);
                int Single() => type == 3 ? BitConverter.ToUInt16(data, o + 8) : (int)BitConverter.ToUInt32(data, o + 8);
                switch (tag)
                {
                    case 256: width = Single(); break;
                    case 257: height = Single(); break;
                    case 258: bits = Single(); break;
                    case 259: compression = Single(); break;
                    case 278: rows = Single(); break;
                    case 322: tileWidth = Single(); break;
                    case 323: tileHeight = Single(); break;
                    case 339: format = Single(); break;
                    case 273 or 324:
                        var size = type == 3 ? 2 : 4;
                        var at = n * size <= 4 ? o + 8 : (int)BitConverter.ToUInt32(data, o + 8);
                        offsets = new long[n];
                        for (var i = 0; i < n; i++)
                            offsets[i] = size == 2 ? BitConverter.ToUInt16(data, at + i * 2) : BitConverter.ToUInt32(data, at + i * 4);
                        break;
                }
            }

            if (compression != 1 || bits != 32 || format != 3 || width <= 0 || height <= 0)
                throw new InvalidDataException($"unexpected TIFF: compression {compression}, {bits} bits, format {format}");

            // Strips are tiles the width of the image.
            if (tileWidth == 0)
                (tileWidth, tileHeight) = (width, rows > 0 && rows < height ? rows : height);
            var across = (width + tileWidth - 1) / tileWidth;
            var pixels = new float[width * height];
            var (columns, lines) = (tileWidth, tileHeight);
            Parallel.For(0, offsets.Length, t =>
            {
                var x0 = t % across * columns;
                var y0 = t / across * lines;
                var length = Math.Min(columns, width - x0);

                // A block with nothing in it (open sea, say) has no place in the file.
                if (offsets[t] == 0)
                {
                    for (var y = 0; y < lines && y0 + y < height; y++)
                        pixels.AsSpan((y0 + y) * width + x0, length).Fill(-999999f);
                    return;
                }

                for (var y = 0; y < lines && y0 + y < height; y++)
                    Buffer.BlockCopy(data, (int)offsets[t] + y * columns * 4, pixels, ((y0 + y) * width + x0) * 4, length * 4);
            });
            return (width, height, pixels);
        }
    }

    // --- Elevation: Terrarium tiles. ---

    private sealed class Terrarium(int zoom)
    {
        private readonly ConcurrentDictionary<(int X, int Y), Lazy<Task<float[]>>> _tiles = new();

        /// The tiles round the bounds, each fetched and decoded the once however many ask for it. What comes back stays good when Trim runs.
        public async Task<Dictionary<(int X, int Y), float[]>> Fetch((double South, double West, double North, double East) bounds, CancellationToken token)
        {
            var (x0, y0) = Tile(bounds.North, bounds.West);
            var (x1, y1) = Tile(bounds.South, bounds.East);
            var jobs = new List<(int X, int Y)>();
            for (var y = y0 - 1; y <= y1 + 1; y++)
                for (var x = x0 - 1; x <= x1 + 1; x++)
                    jobs.Add((x, y));

            var found = new ConcurrentDictionary<(int X, int Y), float[]>();
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (t, ct) =>
            {
                var lazy = _tiles.GetOrAdd(t, key => new Lazy<Task<float[]>>(() => Load(key)));
                try
                {
                    found[t] = await lazy.Value;
                }
                catch
                {
                    // Not for good: the next tile to want it tries again.
                    _tiles.TryRemove(new KeyValuePair<(int X, int Y), Lazy<Task<float[]>>>(t, lazy));
                    throw;
                }
            });
            return new Dictionary<(int X, int Y), float[]>(found);
        }

        private async Task<float[]> Load((int X, int Y) t)
        {
            var path = Path.Combine(CacheFolder("terrarium", zoom.ToString(), t.X.ToString()), $"{t.Y}.png");
            if (!File.Exists(path))
            {
                for (var attempt = 1; ; attempt++)
                {
                    using var turn = await TerrariumLimit.Turn(default);
                    try
                    {
                        using var response = await Http.GetAsync($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{zoom}/{t.X}/{t.Y}.png");
                        if (Busy(response))
                            TerrariumLimit.HoldOff(RetryAfter(response));
                        else
                        {
                            response.EnsureSuccessStatusCode();
                            await WriteCache(path, await response.Content.ReadAsByteArrayAsync());
                            TerrariumLimit.Went();
                            break;
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && attempt < 4)
                    {
                        TerrariumLimit.HoldOff();
                    }

                    if (attempt >= 4)
                        throw new InvalidDataException($"AWS Terrain Tiles wouldn't send {t.X}/{t.Y}");
                }
            }

            using var image = Stb.Load(path, 4);
            var pixels = image.AsSpan<byte4>();
            var heights = new float[256 * 256];
            for (var i = 0; i < heights.Length && i < pixels.Length; i++)
                heights[i] = pixels[i].X * 256f + pixels[i].Y + pixels[i].Z / 256f - 32768f;
            return heights;
        }

        /// Drops decoded tiles once there are many (a long route touches thousands).
        public void Trim()
        {
            if (_tiles.Count > 400)
                _tiles.Clear();
        }

        private static (double X, double Y) Pixel(int zoom, double lat, double lon)
        {
            var n = 256.0 * (1 << zoom);
            var x = (lon + 180.0) / 360.0 * n;
            var y = (1.0 - Math.Asinh(Math.Tan(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * n;
            return (x, y);
        }

        private (int X, int Y) Tile(double lat, double lon)
        {
            var (x, y) = Pixel(zoom, lat, lon);
            return ((int)(x / 256), (int)(y / 256));
        }

        private static float At(Dictionary<(int X, int Y), float[]> tiles, int gx, int gy) =>
            tiles.TryGetValue((gx >> 8, gy >> 8), out var tile) ? tile[(gy & 255) * 256 + (gx & 255)] : 0f;

        public static float Elevation(Dictionary<(int X, int Y), float[]> tiles, int zoom, GeoPoint p)
        {
            var (px, py) = Pixel(zoom, p.Lat, p.Lon);
            px -= 0.5;
            py -= 0.5;
            var ix = (int)Math.Floor(px);
            var iy = (int)Math.Floor(py);
            var tx = (float)(px - ix);
            var ty = (float)(py - iy);
            var top = At(tiles, ix, iy) + (At(tiles, ix + 1, iy) - At(tiles, ix, iy)) * tx;
            var bottom = At(tiles, ix, iy + 1) + (At(tiles, ix + 1, iy + 1) - At(tiles, ix, iy + 1)) * tx;
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

        private readonly ConcurrentDictionary<string, Lazy<Task<Cog>>> _files = new();

        private static string FileName(int lat, int lon) =>
            $"ESA_WorldCover_10m_2021_v200_{(lat < 0 ? 'S' : 'N')}{Math.Abs(lat):00}{(lon < 0 ? 'W' : 'E')}{Math.Abs(lon):000}_Map.tif";

        private static async Task<byte[]> Range(string url, long offset, long length, string cacheName)
        {
            var path = Path.Combine(CacheFolder("worldcover"), $"{cacheName}_{offset}_{length}.bin");
            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path);

            for (var attempt = 1; ; attempt++)
            {
                using var turn = await CoverLimit.Turn(default);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                    using var response = await Http.SendAsync(request);
                    if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
                        throw new FileNotFoundException(url);
                    if (Busy(response))
                        CoverLimit.HoldOff(RetryAfter(response));
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        await WriteCache(path, bytes);
                        CoverLimit.Went();
                        return bytes;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && attempt < 4)
                {
                    CoverLimit.HoldOff();
                }

                if (attempt >= 4)
                    throw new InvalidDataException($"ESA WorldCover wouldn't send {cacheName}");
            }
        }

        /// A file's header, read the once however many tiles ask for it at the same moment.
        private Task<Cog> Open(int lat, int lon) => _files.GetOrAdd(FileName(lat, lon), name => new Lazy<Task<Cog>>(() => Read(name))).Value;

        private static async Task<Cog> Read(string name)
        {
            var cog = new Cog { Url = $"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/{name}" };

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
            var byUrl = new Dictionary<string, Cog>();
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
                byUrl[cog.Url] = cog;
            }

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
