using Highball.App;
using System.Collections.Concurrent;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The heights and ground cover of a route's built tiles, addressed by global sample: gx = east / spacing and
/// gz = -north / spacing from the route origin, so tile (I, J) covers gx 1000I..1000(I+1) and gz -1000(J+1)..-1000J,
/// its rows running north to south like its Heights.bin.
///
/// Every tile keeps a copy of its heights at every 8th sample (16 m) and its cover classes in memory; the full 2 m
/// heights of tiles near the camera load on worker threads when asked for and drop out again when they're far away.
/// A sample on an edge two tiles share always comes from the same one of them (see Canonical), so meshes built on
/// either side of the edge agree exactly.
public sealed partial class TerrainSource
{
    public const int CoarseStride = 8;
    private const int MaxFull = 40;

    public sealed class Tile
    {
        public required TileId Id { get; init; }
        public required float[] Coarse { get; init; }
        public required byte[] Cover { get; init; }
        public required float MinHeight { get; set; }
        public required float MaxHeight { get; set; }
        internal volatile float[]? Full;
        /// Hand sculpting, as an offset per sample (see TerrainEdits); null if the tile hasn't been touched.
        internal float[]? Edits;
        internal bool EditsDirty;
        internal bool CoverDirty;
        internal Task? Loading;
        internal long Used;
    }

    private readonly RouteInfo _route;
    private readonly Dictionary<TileId, Tile> _tiles = [];
    private readonly ConcurrentQueue<(Tile Tile, float[] Heights)> _loaded = new();
    private long _clock;

    /// Quads per tile side (1000 at 2 m).
    public int Quads { get; }
    public int Samples => Quads + 1;
    public int CoarseSamples => Quads / CoarseStride + 1;
    public int CoverCells { get; }
    public float Spacing { get; }
    public IReadOnlyDictionary<TileId, Tile> Tiles => _tiles;

    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// Built tiles' extent in samples.
    public int MinGx { get; }
    public int MaxGx { get; }
    public int MinGz { get; }
    public int MaxGz { get; }

    /// The lines shaped by hand.
    public required TrackEdits TrackEdits { get; init; }

    private TerrainSource(RouteInfo route, List<Tile> tiles)
    {
        _route = route;
        var grid = route.Grid!;
        Quads = grid.Samples - 1;
        Spacing = (float)grid.Spacing;
        CoverCells = grid.CoverCells;

        foreach (var tile in tiles)
            _tiles[tile.Id] = tile;

        MinHeight = tiles.Count > 0 ? tiles.Min(t => t.MinHeight) : 0f;
        MaxHeight = tiles.Count > 0 ? tiles.Max(t => t.MaxHeight) : 0f;
        MinGx = tiles.Count > 0 ? tiles.Min(t => t.Id.I) * Quads : 0;
        MaxGx = tiles.Count > 0 ? (tiles.Max(t => t.Id.I) + 1) * Quads : 0;
        MinGz = tiles.Count > 0 ? -(tiles.Max(t => t.Id.J) + 1) * Quads : 0;
        MaxGz = tiles.Count > 0 ? -tiles.Min(t => t.Id.J) * Quads : 0;
    }

    /// Reads every built tile (on worker threads) and keeps its coarse heights and cover. The route's roads and rails
    /// are read too: their profiles come from the ground as built, then every tile they touch is read again and
    /// reshaped to carry them.
    public static Task<TerrainSource> Open(RouteInfo route) => Task.Run(() =>
    {
        using var counted = Background.Begin("Opening terrain");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var built = route.BuiltTiles();
        var ids = built.ToArray();
        var tiles = new Tile?[ids.Length];
        var network = RouteNetwork.Load(route);
        if (App.DevTools.Refit)
            NetworkLines.ForgetSolved(route);
        var edits = TrackEdits.Load(route);
        var lines = NetworkLines.Build(network.Ways.Values, route, built, edits.Snapshot());

        Parallel.For(0, ids.Length, i =>
        {
            try
            {
                var folder = route.TileFolder(ids[i]);
                var data = RouteTile.Load(folder);
                lines.SampleGround(ids[i], data.Heights, data.Meta.Samples);
                if (lines.LinesIn(ids[i]).Count == 0)
                    tiles[i] = MakeTile(ids[i], data.Heights, data.Cover, data.Meta.Samples, LoadEdits(folder, data.Meta.Samples));
            }
            catch (Exception e)
            {
                Log.Warn($"Terrain: tile {ids[i]} unreadable ({e.Message})");
            }
        });

        lines.FinishProfiles();
        lines.FindLayouts(network.Nodes);
        TrackLayouts.SaveMarkers(route, lines.Layouts);

        Parallel.For(0, ids.Length, i =>
        {
            if (tiles[i] is not null || lines.LinesIn(ids[i]).Count == 0)
                return;
            try
            {
                var folder = route.TileFolder(ids[i]);
                var data = RouteTile.Load(folder);
                lines.Conform(ids[i], data.Heights, data.Meta.Samples);
                tiles[i] = MakeTile(ids[i], data.Heights, data.Cover, data.Meta.Samples, LoadEdits(folder, data.Meta.Samples));
                lines.DrapeMarks(ids[i], data.Heights, data.Meta.Samples);
            }
            catch (Exception e)
            {
                Log.Warn($"Terrain: tile {ids[i]} unreadable ({e.Message})");
            }
        });

        var source = new TerrainSource(route, [.. tiles.OfType<Tile>()]) { Lines = lines, Network = network, TrackEdits = edits };
        Log.Info($"Terrain: {source._tiles.Count} tiles and {lines.Lines.Count} lines opened in {watch.ElapsedMilliseconds} ms");
        return source;
    });

    private static Tile MakeTile(TileId id, float[] heights, byte[] cover, int samples, float[]? edits)
    {
        ApplyEdits(heights, edits);

        var coarseSamples = (samples - 1) / CoarseStride + 1;
        var coarse = new float[coarseSamples * coarseSamples];
        for (var z = 0; z < coarseSamples; z++)
            for (var x = 0; x < coarseSamples; x++)
                coarse[z * coarseSamples + x] = heights[z * CoarseStride * samples + x * CoarseStride];

        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var h in heights)
        {
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }

        return new Tile { Id = id, Coarse = coarse, Cover = cover, MinHeight = min, MaxHeight = max, Edits = edits };
    }

    /// The route's roads and rails, which the heights have been reshaped to carry.
    public NetworkLines? Lines { get; private set; }

    /// The signals, hand throws and grade crossings placed on the route.
    public List<PlacedObject> Objects { get; } = [];

    /// The ways they were made from, as edited.
    public RouteNetwork Network { get; private init; } = new();

    // --- Full heights near the camera. ---

    /// Asks for the full heights of every built tile within radius metres of (x, z), or of (aheadX, aheadZ) where the
    /// camera's heading, nearest the camera first, and lets the others go once more than MaxFull are held. Main
    /// thread, once a frame.
    public void Want(double x, double z, double aheadX, double aheadZ, double radius)
    {
        _clock++;
        while (_loaded.TryDequeue(out var done))
        {
            done.Tile.Full = done.Heights;
            done.Tile.Loading = null;
        }

        var size = Quads * Spacing;
        double DistanceTo(Tile tile, double px, double pz)
        {
            var x0 = tile.Id.I * size;
            var z0 = -(tile.Id.J + 1) * size;
            var dx = Math.Max(0.0, Math.Max(x0 - px, px - (x0 + size)));
            var dz = Math.Max(0.0, Math.Max(z0 - pz, pz - (z0 + size)));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        foreach (var tile in _tiles.Values.Where(t => DistanceTo(t, x, z) <= radius || DistanceTo(t, aheadX, aheadZ) <= radius).OrderBy(t => DistanceTo(t, x, z)))
        {

            tile.Used = _clock;
            if (tile.Full is null && tile.Loading is null)
            {
                var folder = _route.TileFolder(tile.Id);
                var samples = Samples;
                tile.Loading = Task.Run(() =>
                {
                    try
                    {
                        using var counted = Background.Begin("Tile loads");
                        var heights = RouteTile.Load(folder).Heights;
                        Lines?.Conform(tile.Id, heights, samples);
                        ApplyEdits(heights, tile.Edits);
                        _loaded.Enqueue((tile, heights));
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"Terrain: tile {tile.Id} unreadable ({e.Message})");
                    }
                });
            }
        }

        var held = _tiles.Values.Where(t => t.Full is not null).ToList();
        if (held.Count <= MaxFull)
            return;

        foreach (var tile in held.Where(t => t.Used != _clock).OrderBy(t => t.Used).Take(held.Count - MaxFull))
            tile.Full = null;
    }

    /// Whether every built tile whose samples [gx0, gx1] x [gz0, gz1] touch has its full heights in memory.
    public bool FullReady(int gx0, int gz0, int gx1, int gz1)
    {
        for (var k = FloorDiv(gz0, Quads); k <= FloorDiv(gz1, Quads); k++)
            for (var i = FloorDiv(gx0, Quads); i <= FloorDiv(gx1, Quads); i++)
                if (_tiles.TryGetValue(new TileId(i, -k - 1), out var tile) && tile.Full is null)
                    return false;
        return true;
    }

    /// Whether any built tile lies inside the sample rectangle (more than touching its edge).
    public bool AnyBuilt(int gx0, int gz0, int gx1, int gz1)
    {
        for (var k = FloorDiv(gz0, Quads); k <= FloorDiv(gz1 - 1, Quads); k++)
            for (var i = FloorDiv(gx0, Quads); i <= FloorDiv(gx1 - 1, Quads); i++)
                if (_tiles.ContainsKey(new TileId(i, -k - 1)))
                    return true;
        return false;
    }

    /// Lowest and highest ground in the sample rectangle, from the tiles' own ranges.
    public (float Min, float Max) HeightRange(int gx0, int gz0, int gx1, int gz1)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var k = FloorDiv(gz0, Quads); k <= FloorDiv(gz1 - 1, Quads); k++)
        {
            for (var i = FloorDiv(gx0, Quads); i <= FloorDiv(gx1 - 1, Quads); i++)
            {
                if (!_tiles.TryGetValue(new TileId(i, -k - 1), out var tile))
                    continue;
                min = MathF.Min(min, tile.MinHeight);
                max = MathF.Max(max, tile.MaxHeight);
            }
        }

        return min <= max ? (min, max) : (0f, 0f);
    }

    // --- Sampling. ---

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// The tile a sample is read from, and where in it. The tile it falls in by floor division, or on a shared edge,
    /// the first built one of: the tile to the west, to the north, to the north-west.
    private bool Canonical(int gx, int gz, out Tile tile, out int sx, out int sz)
    {
        var i = FloorDiv(gx, Quads);
        var k = FloorDiv(gz, Quads);
        sx = gx - i * Quads;
        sz = gz - k * Quads;

        if (_tiles.TryGetValue(new TileId(i, -k - 1), out tile!))
            return true;

        if (sx == 0 && _tiles.TryGetValue(new TileId(i - 1, -k - 1), out tile!))
        {
            sx = Quads;
            return true;
        }

        if (sz == 0 && _tiles.TryGetValue(new TileId(i, -k), out tile!))
        {
            sz = Quads;
            return true;
        }

        if (sx == 0 && sz == 0 && _tiles.TryGetValue(new TileId(i - 1, -k), out tile!))
        {
            sx = Quads;
            sz = Quads;
            return true;
        }

        return false;
    }

    /// The height at a sample: exact from the full heights when they're loaded, or at a coarse sample; otherwise
    /// interpolated between coarse samples. False outside the built tiles.
    public bool TryHeight(int gx, int gz, out float height)
    {
        if (!Canonical(gx, gz, out var tile, out var sx, out var sz))
        {
            height = 0f;
            return false;
        }

        var full = tile.Full;
        if (full is not null)
        {
            height = full[sz * Samples + sx];
            return true;
        }

        var cs = CoarseSamples;
        var fx = sx / (float)CoarseStride;
        var fz = sz / (float)CoarseStride;
        var x0 = Math.Min((int)fx, cs - 2);
        var z0 = Math.Min((int)fz, cs - 2);
        var tx = fx - x0;
        var tz = fz - z0;
        var c = tile.Coarse;
        var top = c[z0 * cs + x0] + (c[z0 * cs + x0 + 1] - c[z0 * cs + x0]) * tx;
        var bottom = c[(z0 + 1) * cs + x0] + (c[(z0 + 1) * cs + x0 + 1] - c[(z0 + 1) * cs + x0]) * tx;
        height = top + (bottom - top) * tz;
        return true;
    }

    /// The ground height under a point in world metres (x east, z south), interpolated; 0 outside the route.
    public float HeightAt(double x, double z)
    {
        var fx = x / Spacing;
        var fz = z / Spacing;
        var gx = (int)Math.Floor(fx);
        var gz = (int)Math.Floor(fz);
        var tx = (float)(fx - gx);
        var tz = (float)(fz - gz);
        if (!TryHeight(gx, gz, out var a))
            return 0f;
        if (!TryHeight(gx + 1, gz, out var b)) b = a;
        if (!TryHeight(gx, gz + 1, out var c)) c = a;
        if (!TryHeight(gx + 1, gz + 1, out var d)) d = a;
        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * tz;
    }

    /// The WorldCover classes around a sample, averaged over a square radius metres across: the ground colour (and
    /// how much of it is forest), and how much of each ground texture it calls for (grass, bare earth, water).
    public (float4 Cover, float4 Materials) Ground(int gx, int gz, float radius)
    {
        if (!Canonical(gx, gz, out _, out _, out _))
            return default;

        var x = gx * Spacing;
        var z = gz * Spacing;
        x += WarpLarge * (ValueNoise(x / 140f, z / 140f) * 2f - 1f) + WarpSmall * (ValueNoise(x / 35f + 17.3f, z / 35f) * 2f - 1f);
        z += WarpLarge * (ValueNoise(x / 140f + 41.7f, z / 140f + 9.1f) * 2f - 1f) + WarpSmall * (ValueNoise(x / 35f, z / 35f + 63.2f) * 2f - 1f);

        var (localCover, localMaterials) = Blend(x, z, MathF.Max(radius, LocalRadius), 3);
        var (regionalCover, regionalMaterials) = Blend(x, z, MathF.Max(radius, RegionalRadius), 2);
        return (localCover + (regionalCover - localCover) * RegionalShare, localMaterials + (regionalMaterials - localMaterials) * RegionalShare);
    }

    // How the land cover is blended into the ground: biomes rather than the 10 m cells it comes in. Each class's
    // patch is softened over LocalRadius, its edges pushed about by noise so they wander instead of stepping, and the
    // result is drawn part way to the average over RegionalRadius, so a town or a stretch of farmland reads as one place.
    private const float LocalRadius = 26f;
    private const float RegionalRadius = 160f;
    private const float RegionalShare = 0.35f;
    private const float WarpLarge = 22f;
    private const float WarpSmall = 7f;

    /// Tent-weighted averages of the cover's colour and textures around (x, z) world metres, over (2n + 1)^2 points
    /// out to radius, read across tile edges; points off the built tiles don't count.
    private (float4 Cover, float4 Materials) Blend(float x, float z, float radius, int n)
    {
        var cover = default(float4);
        var materials = default(float4);
        var weights = 0f;
        Tile? last = null;
        for (var j = -n; j <= n; j++)
        {
            for (var i = -n; i <= n; i++)
            {
                var u = (float)i / n;
                var v = (float)j / n;
                var w = MathF.Max(0f, 1f - MathF.Sqrt(u * u + v * v) * 0.8f);
                if (w <= 0f || CoverAt(x + u * radius, z + v * radius, ref last) is not { } c)
                    continue;
                cover += Palette(c) * w;
                materials += MaterialsOf(c) * w;
                weights += w;
            }
        }

        return weights > 0f ? (cover / weights, materials / weights) : default;
    }

    /// The cover class of the 10 m cell under a point, from whichever tile it's in; last is the tile the previous
    /// point fell in, tried first.
    private byte? CoverAt(float x, float z, ref Tile? last)
    {
        var cellSize = Quads * Spacing / CoverCells;
        var cx = (int)MathF.Floor(x / cellSize);
        var cz = (int)MathF.Floor(z / cellSize);
        var i = FloorDiv(cx, CoverCells);
        var k = FloorDiv(cz, CoverCells);
        var tile = last;
        if (tile is null || tile.Id.I != i || tile.Id.J != -k - 1)
        {
            if (!_tiles.TryGetValue(new TileId(i, -k - 1), out tile))
                return null;
            last = tile;
        }

        return tile.Cover[(cz - k * CoverCells) * CoverCells + (cx - i * CoverCells)];
    }

    /// Smooth value noise in 0..1, for warping the cover's edges.
    private static float ValueNoise(float x, float z)
    {
        var x0 = MathF.Floor(x);
        var z0 = MathF.Floor(z);
        var tx = x - x0;
        var tz = z - z0;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        var ix = (int)x0;
        var iz = (int)z0;
        float H(int a, int b)
        {
            var h = (uint)(a * 374761393 + b * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16)) / (float)uint.MaxValue;
        }

        var top = H(ix, iz) + (H(ix + 1, iz) - H(ix, iz)) * tx;
        var bottom = H(ix, iz + 1) + (H(ix + 1, iz + 1) - H(ix, iz + 1)) * tx;
        return top + (bottom - top) * tz;
    }

    /// Each class's ground texture: x grass (and the forest floor), y bare earth, z water.
    private static float4 MaterialsOf(byte cover) => cover switch
    {
        50 => new float4(0.35f, 0.65f, 0f, 0f), // built-up: yards and verges between the hard ground
        60 or 70 => new float4(0f, 1f, 0f, 0f),
        80 => new float4(0f, 0f, 1f, 0f),
        _ => new float4(1f, 0f, 0f, 0f),
    };

    /// Linear albedo for each ESA WorldCover class; alpha is how much of it is tree canopy.
    private static float4 Palette(byte cover) => cover switch
    {
        10 => new float4(0.040f, 0.062f, 0.026f, 1f),   // tree cover
        20 => new float4(0.072f, 0.082f, 0.044f, 0.3f), // shrubland
        30 => new float4(0.078f, 0.100f, 0.044f, 0f),   // grassland
        40 => new float4(0.100f, 0.100f, 0.054f, 0f),   // cropland
        50 => new float4(0.062f, 0.074f, 0.048f, 0.2f), // built-up: roofs and roads among lawns and street trees
        60 => new float4(0.110f, 0.100f, 0.082f, 0f),   // bare
        70 => new float4(0.700f, 0.720f, 0.750f, 0f),   // snow and ice
        80 => new float4(0.015f, 0.030f, 0.040f, 0f),   // water
        90 => new float4(0.050f, 0.074f, 0.040f, 0.2f), // wetland
        95 => new float4(0.032f, 0.058f, 0.030f, 0.8f), // mangroves
        100 => new float4(0.090f, 0.095f, 0.060f, 0f),  // moss and lichen
        _ => new float4(0.078f, 0.100f, 0.044f, 0f),
    };
}
