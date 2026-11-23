using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// A built tile as it's drawn far off, when it isn't loaded: its ground every eighth of its side (250 m at 2 km tiles) and the
/// cover class most of each eighth by eighth holds, worked out from its coarse heights as last loaded (shaped and sculpted), and its
/// range. Kept for every built tile (TerrainOverview), a few hundred bytes each, so a route opens and draws its far ground without
/// reading its tiles (docs/cell-routes.md, Streaming everything).
public sealed class FarTile
{
    /// Cells a side: heights at Cells + 1 a side, a cover class per cell.
    public const int Cells = 8;

    public required float MinHeight { get; init; }
    public required float MaxHeight { get; init; }
    public required float[] Heights { get; init; }
    public required byte[] Cover { get; init; }

    /// The ground at a sample of the tile (0..quads each way), straight between the overview's.
    public float HeightAt(int sx, int sz, int quads)
    {
        var (fx, fz) = (sx * (float)Cells / quads, sz * (float)Cells / quads);
        var (x0, z0) = (Math.Clamp((int)fx, 0, Cells - 1), Math.Clamp((int)fz, 0, Cells - 1));
        var (tx, tz) = (fx - x0, fz - z0);
        const int n = Cells + 1;
        var h = Heights;
        var top = h[z0 * n + x0] + (h[z0 * n + x0 + 1] - h[z0 * n + x0]) * tx;
        var bottom = h[(z0 + 1) * n + x0] + (h[(z0 + 1) * n + x0 + 1] - h[(z0 + 1) * n + x0]) * tx;
        return top + (bottom - top) * tz;
    }

    public bool Same(FarTile other) =>
        MinHeight.Equals(other.MinHeight) && MaxHeight.Equals(other.MaxHeight) && Heights.AsSpan().SequenceEqual(other.Heights) && Cover.AsSpan().SequenceEqual(other.Cover);
}

/// Every built tile's overview (FarTile), kept with the route in Cache/Terrain, a file per block of 32 x 32 tiles (64 km), so what
/// changes writes only its block. Derived: anything missing is read from the tiles again. Never for a pack (read only).
internal static class TerrainOverview
{
    private const int Magic = 0x52564F54; // "TOVR"
    private const int Version = 1;
    public const int Block = 32;

    private static string FolderOf(RouteInfo route) => Path.Combine(route.Folder, "Cache", "Terrain");

    public static (int I, int J) BlockOf(TileId id) => (TerrainSource.FloorDiv(id.I, Block), TerrainSource.FloorDiv(id.J, Block));

    private static string PathOf(RouteInfo route, (int I, int J) block) => Path.Combine(FolderOf(route), $"{block.I}_{block.J}.bin");

    /// Every tile's overview kept (none for a pack, or when there's none kept).
    public static Dictionary<TileId, FarTile> Read(RouteInfo route)
    {
        var all = new ConcurrentDictionary<TileId, FarTile>();
        var folder = FolderOf(route);
        if (route.Pack is not null || !Directory.Exists(folder))
            return [];
        Parallel.ForEach(Directory.GetFiles(folder, "*.bin"), path =>
        {
            try
            {
                using var file = File.OpenRead(path);
                using var zip = new BrotliStream(file, CompressionMode.Decompress);
                using var r = new BinaryReader(new BufferedStream(zip, 1 << 16));
                if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadInt32() != FarTile.Cells)
                    return;
                const int n = (FarTile.Cells + 1) * (FarTile.Cells + 1);
                for (var count = r.ReadInt32(); count > 0; count--)
                {
                    var id = new TileId(r.ReadInt32(), r.ReadInt32());
                    var (min, max) = (r.ReadSingle(), r.ReadSingle());
                    var heights = new float[n];
                    r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(heights.AsSpan()));
                    var cover = r.ReadBytes(FarTile.Cells * FarTile.Cells);
                    all[id] = new FarTile { MinHeight = min, MaxHeight = max, Heights = heights, Cover = cover };
                }
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn($"Terrain overview: {Path.GetFileName(path)} unreadable ({e.Message}); its tiles are read again");
            }
        });
        return new Dictionary<TileId, FarTile>(all);
    }

    /// Writes these blocks from every tile's overview.
    public static void Write(RouteInfo route, IReadOnlyDictionary<TileId, FarTile> far, IReadOnlyCollection<(int I, int J)> blocks)
    {
        if (route.Pack is not null || blocks.Count == 0)
            return;
        Directory.CreateDirectory(FolderOf(route));
        var byBlock = far.GroupBy(e => BlockOf(e.Key)).Where(g => blocks.Contains(g.Key)).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Key.I).ThenBy(e => e.Key.J).ToList());
        foreach (var block in blocks)
        {
            var path = PathOf(route, block);
            if (!byBlock.TryGetValue(block, out var entries))
            {
                File.Delete(path);
                continue;
            }

            var temporary = path + ".tmp";
            using (var file = File.Create(temporary))
            using (var zip = new BrotliStream(file, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(zip))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(FarTile.Cells);
                w.Write(entries.Count);
                foreach (var (id, f) in entries)
                {
                    w.Write(id.I);
                    w.Write(id.J);
                    w.Write(f.MinHeight);
                    w.Write(f.MaxHeight);
                    w.Write(MemoryMarshal.AsBytes(f.Heights.AsSpan()));
                    w.Write(f.Cover);
                }
            }

            File.Move(temporary, path, true);
        }
    }
}

/// The tiles streamed round the camera: only those within TileRings of its tile (and of where it's heading) are loaded, their coarse
/// heights and cover in memory, read and shaped on worker threads as it moves (as at an open) and let go once they're two rings past
/// that; every other built tile is drawn from its overview (FarTile). A tile loaded or let go is handed to the terrain to build the
/// patches over it again (TakeTileChanges), so neighbouring patches always read a tile the same way.
public sealed partial class TerrainSource
{
    /// How many tiles each way of the camera's are loaded: HB_TILES=N (whole: every tile, never let go); by default the track's window
    /// and two rings more (6: 13 x 13 tiles, 26 km), past where the terrain draws anything finer than the overview's 250 m.
    internal static int? TileRings { get; } = Environment.GetEnvironmentVariable("HB_TILES") switch
    {
        "whole" or "all" => null,
        { Length: > 0 } n when int.TryParse(n, out var rings) && rings >= 0 => rings,
        _ => WindowRings is { } w ? w + 2 : null,
    };

    /// Tiles read at once in the background as the camera moves.
    private const int MostTileLoads = 4;

    /// Tiles let go a frame at most.
    private const int MostTileDrops = 16;

    private readonly ConcurrentDictionary<TileId, FarTile> _far = new();
    private readonly ConcurrentQueue<(TileId Id, Tile? Tile, NetworkLines? Lines)> _arrived = new();
    private readonly HashSet<TileId> _tileLoads = [];
    private readonly HashSet<TileId> _unreadable = [];
    private readonly List<(int X0, int Z0, int X1, int Z1)> _tileChanges = [];
    private (int Loaded, int Dropped, int Discarded) _tileCounts;

    /// The overview's blocks changed since they were last written, and whether a write's under way.
    private readonly HashSet<(int I, int J)> _farDirty = [];
    private readonly Lock _farLock = new();
    private Task _farWrite = Task.CompletedTask;
    private long _farWrittenAt;

    /// Tiles loaded and let go since the start, and loads thrown away (the lines changed while they were shaped), for logs.
    public (int Loaded, int Dropped, int Discarded) TileCounts => _tileCounts;

    /// The sample rectangles of the tiles loaded or let go since last asked: the terrain's patches over them are built again.
    public List<(int X0, int Z0, int X1, int Z1)> TakeTileChanges()
    {
        var changes = new List<(int, int, int, int)>(_tileChanges);
        _tileChanges.Clear();
        return changes;
    }

    private (int, int, int, int) RectOf(TileId id) => (id.I * Quads, -(id.J + 1) * Quads, (id.I + 1) * Quads, -id.J * Quads);

    /// A tile's overview from its coarse heights (straight between them) and its cover (the class most of each cell is).
    private static FarTile FarOf(Tile tile)
    {
        var cs = (int)Math.Round(Math.Sqrt(tile.Coarse.Length));
        var cc = (int)Math.Round(Math.Sqrt(tile.Cover.Length));
        return new FarTile { MinHeight = tile.MinHeight, MaxHeight = tile.MaxHeight, Heights = FarHeights(tile.Coarse, cs), Cover = FarCover(tile.Cover, cc) };
    }

    private static float[] FarHeights(float[] coarse, int cs)
    {
        const int n = FarTile.Cells + 1;
        var heights = new float[n * n];
        for (var v = 0; v < n; v++)
            for (var u = 0; u < n; u++)
            {
                var (fx, fz) = (u * (cs - 1) / (float)FarTile.Cells, v * (cs - 1) / (float)FarTile.Cells);
                var (x0, z0) = (Math.Min((int)fx, cs - 2), Math.Min((int)fz, cs - 2));
                var (tx, tz) = (fx - x0, fz - z0);
                var top = coarse[z0 * cs + x0] + (coarse[z0 * cs + x0 + 1] - coarse[z0 * cs + x0]) * tx;
                var bottom = coarse[(z0 + 1) * cs + x0] + (coarse[(z0 + 1) * cs + x0 + 1] - coarse[(z0 + 1) * cs + x0]) * tx;
                heights[v * n + u] = top + (bottom - top) * tz;
            }

        return heights;
    }

    private static byte[] FarCover(byte[] cover, int cc)
    {
        var far = new byte[FarTile.Cells * FarTile.Cells];
        Span<int> counts = stackalloc int[256];
        for (var v = 0; v < FarTile.Cells; v++)
            for (var u = 0; u < FarTile.Cells; u++)
            {
                counts.Clear();
                for (var r = v * cc / FarTile.Cells; r < (v + 1) * cc / FarTile.Cells; r++)
                    for (var c = u * cc / FarTile.Cells; c < (u + 1) * cc / FarTile.Cells; c++)
                        counts[cover[r * cc + c]]++;
                var best = 0;
                for (var k = 1; k < 256; k++)
                    if (counts[k] > counts[best])
                        best = k;
                far[v * FarTile.Cells + u] = (byte)best;
            }

        return far;
    }

    /// A tile's overview as the ground's come out (shaped again by an edit or a window moving) while it isn't loaded.
    private void ShapedFar(TileId id, float[] heights)
    {
        var cs = CoarseSamples;
        var coarse = new float[cs * cs];
        for (var z = 0; z < cs; z++)
            for (var x = 0; x < cs; x++)
                coarse[z * cs + x] = heights[z * CoarseStride * Samples + x * CoarseStride];
        var (min, max) = (float.MaxValue, float.MinValue);
        foreach (var h in heights)
            (min, max) = (MathF.Min(min, h), MathF.Max(max, h));
        var cover = _far.TryGetValue(id, out var was) ? was.Cover : new byte[FarTile.Cells * FarTile.Cells];
        _far[id] = new FarTile { MinHeight = min, MaxHeight = max, Heights = FarHeights(coarse, cs), Cover = cover };
        KeepOverview([id]);
    }

    /// A tile's sculpting: the loaded tile's, else as saved.
    private float[]? EditsOf(TileId id) =>
        _tiles.TryGetValue(id, out var tile) ? tile.Edits : _route.Pack is null ? LoadEdits(_route.TileFolder(id), Samples) : null;

    /// The overview of these tiles to be written (in the background, soon).
    private void KeepOverview(IEnumerable<TileId> changed)
    {
        lock (_farLock)
            foreach (var id in changed)
                _farDirty.Add(TerrainOverview.BlockOf(id));
        WriteOverview(false);
    }

    /// Writes the overview's changed blocks in the background, unless one was written in the last 20 s (now: whatever the time).
    public void WriteOverview(bool now)
    {
        if (_route.Pack is not null)
            return;
        List<(int, int)> blocks;
        lock (_farLock)
        {
            if (_farDirty.Count == 0 || !_farWrite.IsCompleted || (!now && Environment.TickCount64 - _farWrittenAt < 20_000))
                return;
            blocks = [.. _farDirty];
            _farDirty.Clear();
            _farWrittenAt = Environment.TickCount64;
            var far = new Dictionary<TileId, FarTile>(_far);
            _farWrite = Task.Run(() =>
            {
                try
                {
                    TerrainOverview.Write(_route, far, blocks);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Terrain overview: not written ({e.Message})");
                    lock (_farLock)
                        _farDirty.UnionWith(blocks);
                }
            });
        }
    }

    /// Main thread, once a frame (from Want): takes in the tiles read, lets go of those far off, and asks for those near that aren't
    /// loaded, nearest the camera first.
    private void StreamTiles(double x, double z, double aheadX, double aheadZ)
    {
        if (TileRings is not { } rings)
            return;

        List<Tile>? came = null;
        while (_arrived.TryDequeue(out var a))
        {
            _tileLoads.Remove(a.Id);
            // (Shaped by lines since replaced, by an edit or the window moving: asked for again.)
            if (a.Tile is null)
                _unreadable.Add(a.Id);
            if (a.Tile is null || _tiles.ContainsKey(a.Id))
                continue;
            if (!ReferenceEquals(a.Lines, Lines))
            {
                _tileCounts.Discarded++;
                continue;
            }

            (came ??= []).Add(a.Tile);
        }

        var at = CellAt(_route, x, z);
        var ahead = CellAt(_route, aheadX, aheadZ);
        int RingsTo(TileId id) => Math.Min(Math.Max(Math.Abs(id.I - at.I), Math.Abs(id.J - at.J)), Math.Max(Math.Abs(id.I - ahead.I), Math.Abs(id.J - ahead.J)));

        List<Tile>? gone = null;
        foreach (var tile in _tiles.Values)
            if (RingsTo(tile.Id) > rings + 2 && !tile.EditsDirty && !tile.CoverDirty && !tile.Touched)
            {
                (gone ??= []).Add(tile);
                if (gone.Count == MostTileDrops)
                    break;
            }

        if (came is not null || gone is not null)
        {
            var next = new Dictionary<TileId, Tile>(_tiles);
            var changed = new List<TileId>();
            foreach (var tile in came ?? [])
            {
                next[tile.Id] = tile;
                var f = FarOf(tile);
                if (!_far.TryGetValue(tile.Id, out var was) || !was.Same(f))
                {
                    _far[tile.Id] = f;
                    changed.Add(tile.Id);
                }

                _tileChanges.Add(RectOf(tile.Id));
                _tileCounts.Loaded++;
            }

            foreach (var tile in gone ?? [])
            {
                next.Remove(tile.Id);
                // (As it's been shaped and sculpted while loaded.)
                var f = FarOf(tile);
                if (!_far.TryGetValue(tile.Id, out var was) || !was.Same(f))
                {
                    _far[tile.Id] = f;
                    changed.Add(tile.Id);
                }

                _tileChanges.Add(RectOf(tile.Id));
                _tileCounts.Dropped++;
            }

            _tiles = next;
            Log.Info($"Tiles streamed: {came?.Count ?? 0} loaded, {gone?.Count ?? 0} let go, {next.Count} in memory (at tile {at}; {_tileCounts.Discarded} thrown away so far)");
            if (changed.Count > 0)
                KeepOverview(changed);
        }

        if (_tileLoads.Count >= MostTileLoads)
            return;
        var wanted = new List<(TileId Id, int Distance)>();
        foreach (var centre in at == ahead ? [at] : (TileId[])[at, ahead])
            for (var dj = -rings; dj <= rings; dj++)
                for (var di = -rings; di <= rings; di++)
                {
                    var id = new TileId(centre.I + di, centre.J + dj);
                    if (_built.Contains(id) && !_tiles.ContainsKey(id) && !_tileLoads.Contains(id) && !_unreadable.Contains(id))
                        wanted.Add((id, Math.Max(Math.Abs(id.I - at.I), Math.Abs(id.J - at.J))));
                }

        foreach (var (id, _) in wanted.DistinctBy(w => w.Id).OrderBy(w => w.Distance).Take(MostTileLoads - _tileLoads.Count))
            LoadNear(id);
    }

    /// Reads a tile and shapes it as an open does (TerrainSource.Open), in the background: its ground as the lines loaded shape it if
    /// it has any, as last shaped if it's outside their window (ShapeAsKept), as it came otherwise; its sculpting added. A pack's
    /// tiles come shaped.
    private void LoadNear(TileId id)
    {
        var (lines, route, samples) = (Lines, _route, Samples);
        _tileLoads.Add(id);
        Task.Run(() =>
        {
            try
            {
                using var counted = Background.Begin("Tile loads");
                float[]? edits = null;
                RouteTile data;
                if (route.Pack is { } pack)
                    data = pack.LoadShaped(id);
                else
                {
                    data = route.LoadTile(id);
                    if (lines is not null && lines.LinesIn(id).Count > 0)
                        lines.ConformKept(route, id, data.Heights, samples);
                    else if (lines is null || !lines.Complete(id))
                        ShapeAsKept(route, id, data.Heights);
                    edits = LoadEdits(route.TileFolder(id), samples);
                }

                _arrived.Enqueue((id, MakeTile(id, data.Heights, data.Cover, samples, edits), lines));
            }
            catch (Exception e)
            {
                Log.Warn($"Terrain: tile {id} unreadable ({e.Message})");
                _arrived.Enqueue((id, null, lines));
            }
        });
    }
}
