using System.Collections.Concurrent;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The lines as a window over the route's cells that follows the camera (docs/cell-routes.md, Streaming): the cells within
/// WindowRings of the camera's are loaded whole, read in the background as it moves (CellStore.Read), and taken in as a change of
/// lines like an edit's (the lines loaded made, those let go gone, every other one in its place as it was).
public sealed partial class TerrainSource
{
    /// Reads and writes of the route's cells, one at a time (a window read while cells are being put in place would find them
    /// not matching the index).
    internal static readonly Lock CellsLock = new();

    /// The cells round a place the window should be complete in: WindowRings each way of its cell, and those it's complete in now
    /// that are within a ring more (so going back and forth over a border doesn't read the cells again and again).
    private HashSet<TileId> WindowFor(double x, double z, CellWindow window, int rings)
    {
        var at = CellAt(_route, x, z);
        var wanted = CellStore.Around(at, rings);
        foreach (var c in window.Complete)
            if (Math.Abs(c.I - at.I) <= rings + 1 && Math.Abs(c.J - at.J) <= rings + 1)
                wanted.Add(c);
        return wanted;
    }

    /// Whether the window should move for the camera at (x, z): a cell within WindowRings of its isn't loaded whole, or one loaded
    /// whole is more than a ring further out.
    public bool WindowStale(double x, double z)
    {
        if (Lines?.Window is not { } window || WindowRings is not { } rings)
            return false;
        var at = CellAt(_route, x, z);
        foreach (var c in CellStore.Around(at, rings))
            if (!window.Complete.Contains(c))
                return true;
        foreach (var c in window.Complete)
            if (Math.Abs(c.I - at.I) > rings + 1 || Math.Abs(c.J - at.J) > rings + 1)
                return true;
        return false;
    }

    /// Whether anything laid since the cells were last written is only in memory: the window can't then be read again from them.
    public bool CellsUnsaved
    {
        get
        {
            lock (_unsavedLock)
                return _unsavedCells is not { Count: 0 };
        }
    }

    /// The window moved round (x, z): its cells read in the background, and the change from the lines shown worked out. Null if
    /// the lines aren't a window, or the cells have something laid that isn't written yet (CellsUnsaved).
    public Task<NetworkChange>? Stream(double x, double z)
    {
        if (Lines is not { Window: { } window } shown || WindowRings is not { } rings || CellsUnsaved)
            return null;
        var wanted = WindowFor(x, z, window, rings);
        return Task.Run(() =>
        {
            using var counted = Background.Begin("Streaming track");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (paused, gen2) = (GC.GetTotalPauseDuration(), GC.CollectionCount(2));
            var lines = ReadWindow(shown, wanted) ?? throw new InvalidOperationException("the window's cells couldn't be read");
            var read = watch.ElapsedMilliseconds;
            var change = ChangeTo(shown, lines);
            Log.Info($"Track streamed: window complete in {lines.Window!.Complete.Count} cells (was {window.Complete.Count}), {lines.Window.Read.Count} read; " +
                     $"{change.Made!.Count} lines loaded, {change.Gone!.Count} let go, {lines.Lines.Count(l => !l.IsAbsent)} in memory; {change.Heights.Count} tiles shaped again; " +
                     $"ms: read {read}, change {watch.ElapsedMilliseconds - read}; at cell {CellAt(_route, x, z)}, working set {Environment.WorkingSet >> 20} MB, managed heap {GC.GetTotalMemory(false) >> 20} MB; " +
                     $"the collector paused every thread {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:0} ms meanwhile ({GC.CollectionCount(2) - gen2} full collections)");
            return change;
        });
    }

    /// The lines as a window complete in these cells, read from the route's cells, with what isn't kept in them as the lines shown
    /// have it (CellStore.RestoreFrom), and every tile they're complete in keyed for its ground. Null if they can't be read.
    internal NetworkLines? ReadWindow(NetworkLines shown, IReadOnlySet<TileId> cells)
    {
        NetworkLines? lines;
        lock (CellsLock)
        {
            if (CellStore.ReadIndex(_route) is not { } index)
                return null;
            lines = CellStore.Read(_route, index, cells);
        }

        if (lines is null)
            return null;
        CellStore.RestoreFrom(lines, shown);
        lines.PrepareShapingKeys(lines.Window?.Complete.Where(_tiles.ContainsKey) ?? _tiles.Keys, Samples);
        lines.Warm();
        return lines;
    }

    /// What taking a window read again in changes from the lines shown: the lines loaded (in places that held none, or another line),
    /// those let go, each place's line as it was otherwise (the same line read again: the same, bit for bit, so its meshes stand); the
    /// tiles newly complete whose ground comes out other than as it's shown (as last kept, ShapeAsKept), shaped.
    internal NetworkChange ChangeTo(NetworkLines shown, NetworkLines lines)
    {
        var (made, gone) = (new List<NetworkLine>(), new List<NetworkLine>());
        var count = Math.Max(shown.Lines.Count, lines.Lines.Count);
        for (var i = 0; i < count; i++)
        {
            var was = i < shown.Lines.Count ? shown.Lines[i] : NetworkLine.Absent;
            var now = i < lines.Lines.Count ? lines.Lines[i] : NetworkLine.Absent;
            if (SameLine(was, now))
                continue;
            if (!was.IsAbsent)
                gone.Add(was);
            if (!now.IsAbsent)
                made.Add(now);
        }

        var areas = made.Concat(gone).Select(l => (l.MinX, l.MinZ, l.MaxX, l.MaxZ)).ToList();
        var heights = new ConcurrentDictionary<TileId, float[]>();
        var fresh = lines.Window is { } now2 ? now2.Complete.Where(c => _tiles.ContainsKey(c) && !shown.Complete(c) && lines.LinesIn(c).Count > 0).ToList() : [];
        Parallel.ForEach(fresh, id =>
        {
            var raw = _route.LoadTile(id).Heights;
            // (Shown as it was last kept: if that's what these lines shape it to, it's as it should be.)
            if (GroundCache.KeyOf(_route, lines, id, raw, Samples) is { } key && GroundCache.KeptKey(_route, id) == key)
                return;
            var shaped = (float[])raw.Clone();
            lines.ConformKept(_route, id, shaped, Samples);
            ApplyEdits(shaped, _tiles[id].Edits);
            lines.DrapeMarks(id, shaped, Samples);
            heights[id] = shaped;
        });

        foreach (var id in heights.Keys)
        {
            var size = Quads * Spacing;
            areas.Add((id.I * size, -(id.J + 1) * size, (id.I + 1) * size, -id.J * size));
        }

        return new NetworkChange(lines, heights, areas) { Made = made, Gone = gone, Moved = [], Touched = new HashSet<TileId>() };
    }

    /// The whole route's track, for what's of all of it (the packs and servers, WorldExport): a window's own cells written first if
    /// they're not as it stands, then every cell read; its rail graph built (switches set as graph's). The window's lines and graph
    /// as they are if they're the whole route already.
    internal (NetworkLines Lines, Trains.RailGraph Graph) WholeTrack(NetworkLines window, Trains.RailGraph graph, IReadOnlyList<PlacedObject> objects)
    {
        if (window.Window is null)
            return (window, graph);
        NetworkLines? whole;
        lock (CellsLock)
        {
            var stamp = CellStore.StampOf(_route, _tiles.Keys, objects);
            if (CellStore.ReadIndex(_route) is not { } index || index.Stale(stamp) is not null)
            {
                if (CellStore.Write(_route, stamp, window))
                    lock (_unsavedLock)
                        _unsavedCells = [];
            }

            var now = CellStore.ReadIndex(_route) ?? throw new InvalidOperationException("the route's cells couldn't be read");
            whole = CellStore.Read(_route, now, now.Cells.Keys) ?? throw new InvalidOperationException("the route's cells couldn't be read whole");
        }

        CellStore.RestoreFrom(whole, window);
        var built = RailGraphBuilder.Build(whole);
        built.Reversed = graph.Reversed;
        Log.Info($"Export: the whole route's track read ({whole.Lines.Count(l => !l.IsAbsent)} lines; the window had {window.Lines.Count(l => !l.IsAbsent)})");
        return (whole, built);
    }

    /// Whether two lines in one place are the same line read twice (or both no line).
    private static bool SameLine(NetworkLine a, NetworkLine b) =>
        ReferenceEquals(a, b) || (!a.IsAbsent && !b.IsAbsent && a.Key == b.Key && a.Name == b.Name && a.Count == b.Count && a.Chain == b.Chain);
}
