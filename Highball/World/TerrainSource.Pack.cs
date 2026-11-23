using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// Opening an installed pack to play: the track read from the pack's cells (CellStore) and the ground from its tiles with the
/// shaping the export kept, so a player's game never solves, grades or shapes anything at open (docs/cell-routes.md, stage 5).
public sealed partial class TerrainSource
{
    public static Task<TerrainSource> OpenPack(PackSource pack) => Task.Run(() =>
    {
        using var counted = Background.Begin("Opening terrain");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var route = pack.Route;
        var stages = new List<string>();
        var stage = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name)
        {
            stages.Add($"{name} {stage.ElapsedMilliseconds}");
            stage.Restart();
        }

        var built = route.BuiltTiles();
        var ids = built.ToArray();
        var network = RouteNetwork.Load(route);
        var edits = TrackEdits.Load(route);
        var bridgeEdits = RouteBridges.Load(route);
        var objects = RouteObjects.Load(route);
        Stage("files");

        // (A window complete in the pack's own tiles: a region's pack carries only the cells that's read from, CellStore.ReadFor.)
        var lines = CellStore.Read(pack.CellRoute, pack.Cells, pack.Built())
                    ?? throw new PackOutdatedException(pack.Path, "its cells couldn't be read (see the log)");
        CellStore.Restore(lines, network, edits.Snapshot(), built, bridgeEdits, objects);
        Stage("cells");

        // Every tile with its ground as the export shaped it (sculpting's in the pack's heights already), and the road marks laid on it
        // as the editor lays them once its ground's shaped.
        var tiles = new Tile?[ids.Length];
        Parallel.For(0, ids.Length, i =>
        {
            try
            {
                var data = pack.LoadShaped(ids[i]);
                tiles[i] = MakeTile(ids[i], data.Heights, data.Cover, data.Meta.Samples, null);
                lines.DrapeMarks(ids[i], data.Heights, data.Meta.Samples);
            }
            catch (Exception e)
            {
                Log.Warn($"Terrain: tile {ids[i]} unreadable ({e.Message})");
            }
        });
        Stage("tiles");
        Log.Info($"Terrain open (pack), ms: {string.Join(", ", stages)}");

        var source = new TerrainSource(route, ids, [.. tiles.OfType<Tile>()], new Dictionary<TileId, FarTile>()) { Lines = lines, Network = network, TrackEdits = edits };
        source.Objects.AddRange(objects);
        foreach (var (key, edit) in bridgeEdits)
            source.Bridges[key] = edit;
        Log.Info($"Terrain: {source._tiles.Count} tiles and {lines.Lines.Count(l => !l.IsAbsent)} lines opened from the pack in {watch.ElapsedMilliseconds} ms " +
                 $"(peak working set so far {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB, managed heap {GC.GetTotalMemory(false) >> 20} MB)");
        return source;
    });
}
