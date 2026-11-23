using Highball.App;
using System.Collections.Concurrent;
using Highball.Routes;

namespace Highball.World;

/// What an edit to the network changed, worked out off the main thread: the new lines, the heights of every tile
/// whose reshaping changed (reshaped again from the ground as built, with the hand edits on top), and the areas
/// (world metres: min x, min z, max x, max z) whose line meshes need building again.
public sealed record NetworkChange(NetworkLines Lines, IReadOnlyDictionary<TileId, float[]> Heights, IReadOnlyList<(double X0, double Z0, double X1, double Z1)> Areas)
{
    /// A rebuild of some cells (CellStore.Merge): the lines laid differently or new (in Lines), the last lines gone, and lines moved from
    /// one place to another; null after a rebuild in full. What taking the change in needs look at, and nothing else.
    public IReadOnlyList<NetworkLine>? Made { get; init; }
    public IReadOnlyList<NetworkLine>? Gone { get; init; }
    public IReadOnlyList<(int From, int To)>? Moved { get; init; }

    /// The cells whose content changed, for saving only those (null: all of them).
    public IReadOnlySet<TileId>? Touched { get; init; }
}

public sealed partial class TerrainSource
{
    /// Rebuilds the lines from the network as it is now. Only lines that came out different (or that the junctions
    /// they meet pulled to a new height) cost anything: their ground is sampled again and the tiles they reach are
    /// reshaped again. solveAgain: the saved track's thrown away and it's worked out afresh and saved.
    /// Places the next rebuild lays the track and shapes the ground round again even though no way's changed there (a bridge's end
    /// set by hand, which shapes the ground under it).
    private readonly List<(double X, double Z)> _rebuildAround = [];

    /// The ways by cell and node, for working out which an edit's rebuild is handed (made when first needed, kept up to date).
    private WayIndex? _wayIndex;
    private readonly Lock _wayLock = new();

    /// The ways indexed by cell and node ahead of the first edit (off the main thread, from the network as opened).
    internal void PrepareWays(List<NetworkWay> ways) => Task.Run(() =>
    {
        lock (_wayLock)
            if (_wayIndex is null && _route.Projection is { } projection && _route.Grid is { } grid)
            {
                var index = new WayIndex(projection, grid.TileSize);
                index.Update(ways);
                _wayIndex = index;
            }
    });

    /// HB_NOCELLREBUILD=1: a local rebuild hands the passes the whole route, as before stage 2 (for comparing).
    private static readonly bool WholeRoute = Environment.GetEnvironmentVariable("HB_NOCELLREBUILD") == "1";

    private static readonly Lock MarkersLock = new();

    public void RebuildAround(double x, double z)
    {
        lock (_rebuildAround)
            _rebuildAround.Add((x, z));
    }

    public Task<NetworkChange> RebuildNetwork(bool solveAgain = false)
    {
        var ways = Network.Ways.Values.ToList(); // edits replace ways rather than change them, so this is a snapshot
        var bridgeEdits = new Dictionary<long, BridgeEdit>(Bridges);
        List<(double X, double Z)> around;
        lock (_rebuildAround)
        {
            around = [.. _rebuildAround];
            _rebuildAround.Clear();
        }

        var tagged = new Dictionary<long, NetworkNode>(Network.Nodes);
        var previous = Lines!;
        var shapes = TrackEdits.Snapshot();
        var built = _tiles.Keys.ToHashSet();
        var samples = Samples;
        var crossings = Objects.Where(o => o.Kind == PlacedKind.GradeCrossing).ToList();

        return Task.Run(() =>
        {
            using var counted = Background.Begin("Laying track");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stages = new List<string>();
            void Stage(string name)
            {
                stages.Add($"{name} {clock.ElapsedMilliseconds}");
                clock.Restart();
            }

            if (solveAgain)
                NetworkLines.ForgetSolved(_route);
            WayIndex index;
            int reindexed;
            lock (_wayLock)
            {
                index = _wayIndex ??= new WayIndex(_route.Projection!, _route.Grid!.TileSize);
                reindexed = index.Update(ways);
            }

            Stage($"ways ({reindexed} indexed again)");

            // Laid again near the edit only, if it was to track alone (LocalRebuild); otherwise in full.
            var local = solveAgain ? null : LocalFor(previous, ways, crossings, around);
            Stage(local?.Dirty is { } d0 ? $"window ({d0.Count} cells laid again, {local.Context!.Count} held round them)" : "window");
            NetworkLines Lay(LocalRebuild? near, Action<string> stage)
            {
                // (Only the ways through the cells, if it's a rebuild of some: WayIndex.Handed.)
                var handed = near?.Context is { } cells ? index.Handed(cells).Select(id => index.Way(id)!).ToList() : ways;
                var laid = NetworkLines.Build(handed, _route, built, shapes, near);
                laid.BridgeEdits = bridgeEdits;
                stage("build");
                laid.SetCrossings(crossings, _route.Projection!);
                // (Only built tiles have ground to sample: a line running off the built area keeps its points there unsampled, as on open.)
                var sampled = laid.AdoptGround(near?.Seen ?? previous.Lines).Where(built.Contains).ToList();
                Parallel.ForEach(sampled, id =>
                {
                    var data = _route.LoadTile(id);
                    laid.SampleGround(id, data.Heights, samples);
                });
                stage($"ground ({sampled.Count} tiles)");
                laid.Before = near?.Previous;
                laid.FinishProfiles();
                var held = near is null ? 0 : laid.HoldFarRail(near);
                stage(near is null ? "profiles" : $"profiles ({held} rail points far from the edit held as they were)");
                laid.AdoptDrapes(near?.Seen ?? previous.Lines);
                laid.FindLayouts(tagged);
                stage("layouts");
                laid.Bridges = BridgePlanner.Find(laid, near?.Previous);
                stage("bridges");
                // (Whatever a pass did to a line it should have left be is undone: kept lines come out exactly as they were.)
                if (laid.RestoreFrozen() is var touched and > 0)
                    RailWerkz.Core.Logging.Log.Info($"Local rebuild: {touched} kept lines were changed by a pass and put back");
                if (near is not null && laid.KeepFarPads(near) is var pads and > 0)
                    RailWerkz.Core.Logging.Log.Info($"Local rebuild: {pads} crossings' pads far from the edit kept as they were");
                if (near is not null)
                    RailWerkz.Core.Logging.Log.Info($"Local rebuild: rail heights at junctions agree to {laid.WorstRailStep():0.000} m (the last lines {previous.WorstRailStep():0.000} m)");
                return laid;
            }

            var lines = Lay(local, Stage);
            CellStore.Merged? merged = null;
            if (local?.Dirty is not null)
            {
                // The window's lines put in the route's (CellStore.Merge). A line laid differently outside the dirty cells means they
                // weren't enough: they're grown to take it in, once, and laid again; then in full.
                merged = CellStore.Merge(previous, lines, local, ways.ToDictionary(w => w.Id));
                Stage("merged");
                for (var grow = 0; merged?.Spill is { } spill; grow++)
                {
                    if (grow == 2)
                    {
                        RailWerkz.Core.Logging.Log.Info($"Local rebuild: {spill} still; laid in full");
                        (local, merged) = (null, null);
                        lines = Lay(null, Stage);
                        break;
                    }

                    var grown = GrownFor(local!, merged.Made);
                    RailWerkz.Core.Logging.Log.Info($"Local rebuild: {spill}; {local!.Dirty!.Count} cells grown to {grown.Dirty!.Count} and laid again");
                    local = grown;
                    lines = Lay(local, Stage);
                    merged = CellStore.Merge(previous, lines, local, ways.ToDictionary(w => w.Id));
                    Stage("merged");
                }

                if (merged is not null)
                {
                    merged.Lines.BridgeEdits = bridgeEdits;
                    lines = merged.Lines;
                }
            }

            // (The markers file, the junctions' list for the Planner, written off the rebuild's path.)
            var layouts = lines.Layouts;
            _ = Task.Run(() =>
            {
                lock (MarkersLock)
                    TrackLayouts.SaveMarkers(_route, layouts);
            });
            if (local is not null && Environment.GetEnvironmentVariable("HB_LOCALCHECK") == "1")
            {
                var full = Lay(null, _ => { });
                RailWerkz.Core.Logging.Log.Info($"Local check: laid in full, rail heights at junctions agree to {full.WorstRailStep():0.000} m (the local's {lines.WorstRailStep():0.000} m)");
                if (merged is not null)
                    CheckCells(previous, merged, full, local);
                else
                    CheckLocal(lines, full);
            }

            var old = new Dictionary<long, NetworkLine>();
            if (merged is null)
                foreach (var line in previous.Lines)
                    old.TryAdd(line.Key, line);

            // Where the ground's to be shaped again (world metres, min x, min z, max x, max z): round the stretches of line that
            // came out different, ReshapeReach either side, not the whole of every line that did (a long line that moved at one
            // end still lies as it was elsewhere).
            var kept = new HashSet<long>();
            var reshape = new List<(double X0, double Z0, double X1, double Z1)>();
            var areas = new List<(double, double, double, double)>();
            var gone = new List<NetworkLine>();
            var made = new List<NetworkLine>();
            if (merged is not null)
            {
                // (What came out different is known: the merge's.)
                made.AddRange(merged.Made);
                gone.AddRange(merged.Gone);
                foreach (var line in made.Concat(gone))
                    areas.Add((line.MinX, line.MinZ, line.MaxX, line.MaxZ));
                foreach (var (_, to) in merged.Moved)
                    areas.Add((lines.Lines[to].MinX, lines.Lines[to].MinZ, lines.Lines[to].MaxX, lines.Lines[to].MaxZ));
            }
            else
            {
                foreach (var line in lines.Lines)
                {
                    if (old.TryGetValue(line.Key, out var same) && SameProfile(same, line))
                    {
                        kept.Add(line.Key);
                        continue;
                    }

                    areas.Add((line.MinX, line.MinZ, line.MaxX, line.MaxZ));
                    made.Add(line);
                }

                foreach (var line in previous.Lines)
                {
                    if (kept.Contains(line.Key))
                        continue;
                    areas.Add((line.MinX, line.MinZ, line.MaxX, line.MaxZ));
                    gone.Add(line);
                }
            }

            // (Matched by where they lie, not by key: a line split where track's laid off it, or joined, is new lines over the
            // same track, and only round the split has the track changed.)
            reshape.AddRange(Moved(made, gone));
            reshape.AddRange(Moved(gone, made));

            // A crossing's pad that's new, gone or changed reshapes the ground round it, and its roads' ends.
            var size = _route.Grid!.TileSize;
            foreach (var pad in PadsChanged(previous.Pads, lines.Pads))
            {
                if (Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
                    RailWerkz.Core.Logging.Log.Info($"Pad debug: {pad}");
                var reach = pad.Half + 20f;
                var (a, b) = (pad.At(pad.From - 10f), pad.At(pad.To + 10f));
                var (x0, x1, z0, z1) = (Math.Min(a.X, b.X) - reach, Math.Max(a.X, b.X) + reach, Math.Min(a.Z, b.Z) - reach, Math.Max(a.Z, b.Z) + reach);
                areas.Add((x0, z0, x1, z1));
                reshape.Add(Grown((x0, z0, x1, z1), ReshapeReach));
            }

            // Places asked for by hand (a bridge's end set): the ground round them shaped again and its meshes made again, though the
            // lines there come out as they were (what changed is how the ground meets them).
            foreach (var (ax, az) in around)
            {
                const double reach = AroundReach;
                areas.Add((ax - reach, az - reach, ax + reach, az + reach));
                reshape.Add((ax - reach, az - reach, ax + reach, az + reach));
            }

            // Each tile's window: what of it the places above reach, in its own samples.
            var windows = new Dictionary<TileId, (int X0, int Z0, int X1, int Z1)>();
            foreach (var (x0, z0, x1, z1) in reshape)
                for (var j = (int)Math.Floor(-z1 / size); j <= (int)Math.Floor(-z0 / size); j++)
                    for (var i = (int)Math.Floor(x0 / size); i <= (int)Math.Floor(x1 / size); i++)
                    {
                        var id = new TileId(i, j);
                        if (!_tiles.ContainsKey(id))
                            continue;
                        var (tx, tz) = (i * size, -(j + 1) * size);
                        var w = (Math.Clamp((int)Math.Floor((x0 - tx) / Spacing), 0, Quads), Math.Clamp((int)Math.Floor((z0 - tz) / Spacing), 0, Quads),
                            Math.Clamp((int)Math.Ceiling((x1 - tx) / Spacing), 0, Quads), Math.Clamp((int)Math.Ceiling((z1 - tz) / Spacing), 0, Quads));
                        windows[id] = windows.TryGetValue(id, out var had)
                            ? (Math.Min(had.X0, w.Item1), Math.Min(had.Z0, w.Item2), Math.Max(had.X1, w.Item3), Math.Max(had.Z1, w.Item4))
                            : w;
                    }

            lines.InheritBallast(previous);
            var heights = new ConcurrentDictionary<TileId, float[]>();
            var (inWindows, whole) = (0, 0);
            Parallel.ForEach(windows, pair =>
            {
                var (id, window) = pair;
                var tileWatch = System.Diagnostics.Stopwatch.StartNew();
                var raw = _route.LoadTile(id).Heights;
                var loaded = tileWatch.ElapsedMilliseconds;
                var reshaped = (float[])raw.Clone();
                // The tile as it was last shaped, from the ground kept (GroundCache), with only the window shaped again; the whole tile
                // if it wasn't kept, or the window's most of it.
                var side = Math.Max(window.X1 - window.X0, window.Z1 - window.Z0) + 1;
                if (GroundCache.KeyOf(_route, previous, id, raw, samples) is { } was && GroundCache.TryLoad(_route, id, was, reshaped))
                {
                    lines.ConformBlocks(id, raw, reshaped, window.X0, window.Z0, window.X1, window.Z1);
                    Interlocked.Increment(ref inWindows);
                    if (ReshapeCheck)
                        CheckWindow(lines, id, raw, reshaped);
                }
                else
                {
                    if (Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
                        RailWerkz.Core.Logging.Log.Info($"Reshape debug: tile {id} whole: window {side} samples of {samples}, kept ground under {GroundCache.KeptKey(_route, id):x}, the last lines' key {GroundCache.KeyOf(_route, previous, id, raw, samples):x}, the new {GroundCache.KeyOf(_route, lines, id, raw, samples):x}");
                    lines.ConformWhole(id, raw, reshaped);
                    Interlocked.Increment(ref whole);
                    if (ReshapeCheck)
                        CheckWindow(lines, id, raw, reshaped);
                }

                var shaped = tileWatch.ElapsedMilliseconds;
                if (GroundCache.KeyOf(_route, lines, id, raw, samples) is { } now)
                {
                    // (Written off the rebuild's path: it's for the next open or edit, and the tile's shown meanwhile from what's in memory.)
                    var toSave = (float[])reshaped.Clone();
                    _ = Task.Run(() => GroundCache.Save(_route, id, now, raw, toSave));
                }
                var saved = tileWatch.ElapsedMilliseconds;
                ApplyEdits(reshaped, _tiles[id].Edits);
                lines.DrapeMarks(id, reshaped, samples);
                heights[id] = reshaped;
                if (Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
                    RailWerkz.Core.Logging.Log.Info($"Reshape debug: tile {id}, ms: load {loaded}, shape {shaped - loaded}, save {saved - shaped}, drape {tileWatch.ElapsedMilliseconds - saved}");
            });
            Stage($"reshape ({inWindows} tiles round what changed, {whole} whole; {reshape.Count} stretches changed, {made.Count} lines laid differently, {gone.Count} gone)");
            RailWerkz.Core.Logging.Log.Info($"Track rebuild, ms: {string.Join(", ", stages)}");

            return new NetworkChange(lines, heights, areas) { Made = merged?.Made, Gone = merged?.Gone, Moved = merged?.Moved, Touched = merged?.Touched };
        });
    }

    /// How far round a stretch of line that's changed the ground's shaped again, metres: the furthest anything along a line shapes
    /// the ground (a wall along what's under a bridge, WallMost; a yard graded between tracks; a crossing's pad and its roads).
    private const double ReshapeReach = 250.0;

    /// HB_RESHAPECHECK=1: a tile shaped again round what changed is shaped whole too, and the two compared, logged.
    private static readonly bool ReshapeCheck = Environment.GetEnvironmentVariable("HB_RESHAPECHECK") == "1";

    private static (double X0, double Z0, double X1, double Z1) Grown((double X0, double Z0, double X1, double Z1) r, double by) =>
        (r.X0 - by, r.Z0 - by, r.X1 + by, r.Z1 + by);

    /// The stretches of these lines that don't lie on any of those (within a centimetre, in plan and in height, carried on the same
    /// way, as wide), as places to shape the ground again round, grown by ReshapeReach.
    private static List<(double X0, double Z0, double X1, double Z1)> Moved(List<NetworkLine> these, List<NetworkLine> those)
    {
        const double cell = 20.0;
        var index = new Dictionary<(int, int), List<(NetworkLine Line, int Segment)>>();
        foreach (var line in those)
            for (var p = 0; p + 1 < line.Count; p++)
                foreach (var c in new GridCells(Math.Min(line.X[p], line.X[p + 1]), Math.Min(line.Z[p], line.Z[p + 1]),
                             Math.Max(line.X[p], line.X[p + 1]), Math.Max(line.Z[p], line.Z[p + 1]), cell))
                {
                    if (!index.TryGetValue(c, out var list))
                        index[c] = list = [];
                    list.Add((line, p));
                }

        bool OnAny(NetworkLine line, int p)
        {
            var (x, z, y) = (line.X[p], line.Z[p], line.Y[p]);
            if (!index.TryGetValue(((int)Math.Floor(x / cell), (int)Math.Floor(z / cell)), out var near))
                return false;
            var structure = line.Structures[Math.Min(p, line.Structures.Length - 1)];
            foreach (var (other, q) in near)
            {
                if (other.Rail != line.Rail || other.Mark != line.Mark || MathF.Abs(other.Width - line.Width) > 0.01f || MathF.Abs(other.Formation - line.Formation) > 0.01f ||
                    other.Bed != line.Bed || other.Embedded != line.Embedded)
                    continue;
                var (ax, az, dx, dz) = (other.X[q], other.Z[q], other.X[q + 1] - other.X[q], other.Z[q + 1] - other.Z[q]);
                var length2 = dx * dx + dz * dz;
                var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                if (ex * ex + ez * ez > 0.01 * 0.01)
                    continue;
                var oy = other.Y[q] + (other.Y[q + 1] - other.Y[q]) * (float)t;
                var os = (float)(other.SinkOf(q) + (other.SinkOf(q + 1) - other.SinkOf(q)) * t);
                var structureThere = other.Structures[Math.Min(t < 0.5 ? q : q + 1, other.Structures.Length - 1)];
                if (MathF.Abs(oy - y) <= 0.01f && MathF.Abs(os - line.SinkOf(p)) <= 0.01f && structureThere == structure)
                    return true;
            }

            return false;
        }

        var spans = new System.Collections.Concurrent.ConcurrentBag<(double, double, double, double)>();
        Parallel.ForEach(these, line =>
        {
            for (var p = 0; p < line.Count; p++)
            {
                if (OnAny(line, p))
                    continue;
                var first = p;
                while (p + 1 < line.Count && !OnAny(line, p + 1))
                    p++;
                var (a, b) = (Math.Max(first - 1, 0), Math.Min(p + 1, line.Count - 1));
                var (x0, z0, x1, z1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                for (var q = a; q <= b; q++)
                    (x0, z0, x1, z1) = (Math.Min(x0, line.X[q]), Math.Min(z0, line.Z[q]), Math.Max(x1, line.X[q]), Math.Max(z1, line.Z[q]));
                spans.Add(Grown((x0, z0, x1, z1), ReshapeReach));
                if (Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
                    RailWerkz.Core.Logging.Log.Info($"Moved debug: line {line.Key} ({(line.Rail ? "rail" : line.Mark ? "mark" : "road")}, {line.Count} points, {(line.Frozen ? "kept" : line.Halo ? "halo" : "laid")}) points {first}-{p} at ({(x0 + x1) / 2:0}, {(z0 + z1) / 2:0}), {x1 - x0:0} x {z1 - z0:0} m");
            }
        });

        return [.. spans];
    }

    private void CheckWindow(NetworkLines lines, TileId id, float[] raw, float[] windowed)
    {
        var whole = (float[])raw.Clone();
        lines.Conform(id, whole, Samples);
        var (worst, off) = (0f, 0);
        for (var i = 0; i < whole.Length; i++)
        {
            var d = MathF.Abs(whole[i] - windowed[i]);
            if (d > 0.005f)
                off++;
            worst = MathF.Max(worst, d);
        }

        RailWerkz.Core.Logging.Log.Info(off == 0
            ? $"Reshape check: tile {id} shaped round what changed is as the whole tile shaped again"
            : $"Reshape check: tile {id} shaped round what changed is off from the whole tile shaped again by {worst:0.000} m at most, {off} samples over 5 mm");
    }

    /// How far round a place asked for by hand (RebuildAround) the ground's shaped again, metres: a bridge end's walls run along what's
    /// underneath up to WallMost either way.
    private const double AroundReach = 150.0;

    /// How many rings of lines meeting those laid again are laid again round them and put back after (LocalRebuild.Halo).
    private const int HaloRings = 3;

    /// How far an edit to track can change a line, metres: a road eased onto a level crossing (75 m), a road bridge lifted clear
    /// of new track and climbing back down (200 m and more), a junction's height easing along what meets there.
    private const double LocalReach = 500.0;

    /// A local rebuild, if what's changed since the last lines were laid is track alone (no road, mapped track, crossing or
    /// anything else): the roads and marks with a point within LocalReach of the changed track (as it was and as it is) are laid
    /// again, the rest kept. Null for a full rebuild.
    private LocalRebuild? LocalFor(NetworkLines previous, List<NetworkWay> ways, List<PlacedObject> crossings, List<(double X, double Z)> around)
    {
        if (previous.Ways is not { } was || !previous.SameCrossings(crossings))
        {
            RailWerkz.Core.Logging.Log.Info($"Local rebuild: not this time ({(previous.Ways is null ? "the last lines weren't laid from ways" : "a level crossing's changed")}), laying it all");
            return null;
        }

        var changed = new List<NetworkWay>();
        var now = new HashSet<long>();
        foreach (var way in ways)
        {
            now.Add(way.Id);
            if (!was.TryGetValue(way.Id, out var before))
                changed.Add(way);
            else if (!ReferenceEquals(before, way))
            {
                changed.Add(way);
                changed.Add(before);
            }
        }

        foreach (var (id, gone) in was)
            if (!now.Contains(id))
                changed.Add(gone);
        if (changed.FirstOrDefault(w => !w.Rail || RouteNetwork.IsGuide(w)) is { } other)
        {
            RailWerkz.Core.Logging.Log.Info($"Local rebuild: not this time (way {other.Id} changed, {(other.Rail ? "mapped track" : "a road")}), laying it all");
            return null;
        }


        var projection = _route.Projection!;
        var places = new List<(double X, double Z)>(around);
        foreach (var way in changed)
            for (var i = 0; i < way.Lat.Length; i++)
            {
                var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
                places.Add((p.East, -p.North));
                // (Long segments marked along their length, not only at their ends.)
                if (i + 1 < way.Lat.Length)
                {
                    var q = projection.Forward(new GeoPoint(way.Lat[i + 1], way.Lon[i + 1]));
                    var steps = (int)(Math.Sqrt((q.East - p.East) * (q.East - p.East) + (q.North - p.North) * (q.North - p.North)) / 50.0);
                    for (var k = 1; k <= steps; k++)
                        places.Add((p.East + (q.East - p.East) * k / (steps + 1), -(p.North + (q.North - p.North) * k / (steps + 1))));
                }
            }

        // The cells (tiles) the edit's in, as it was and as it is.
        var size = _route.Grid!.TileSize;
        TileId Cell(double x, double z) => new((int)Math.Floor(x / size), (int)Math.Floor(-z / size));
        var seed = places.Select(p => Cell(p.X, p.Z)).ToHashSet();
        var candidates = Over(previous, Grown(seed, 2));

        var near = previous.ChainsNear(places, LocalReach, candidates);
        // The halo, HaloRings deep (each ring the chains meeting the last).
        var halo = new HashSet<long>();
        var reached = new HashSet<long>(near);
        for (var ring = 0; ring < HaloRings; ring++)
        {
            var next = previous.ChainsMeeting(reached, candidates);
            if (next.Count == 0)
                break;
            halo.UnionWith(next);
            reached.UnionWith(next);
        }

        if (WholeRoute)
            return new LocalRebuild(previous, near, halo, places);

        // What the edit will lay differently, all of it: the lines of the chains through the ways it changed (a line split by a new
        // switch is new lines its whole length), and rail near it (its heights eased out over RailOuter, and its far junctions with
        // it). Their cells are dirty, and one ring round them (docs/cell-routes.md, Edits), and one more is held round those.
        var changedNodes = changed.SelectMany(w => w.Nodes).ToHashSet();
        var edit = NetworkLines.EditCells(places);
        var dirty = new HashSet<TileId>(seed);
        foreach (var line in candidates)
        {
            if (!line.Nodes.Any(n => changedNodes.Contains(n.Node)) && !(line.Rail && Enumerable.Range(0, line.Count).Any(q => NetworkLines.NearEdit(edit, line.X[q], line.Z[q]))))
                continue;
            foreach (var whole in previous.LinesOfChain(line.Chain))
                for (var q = 0; q < whole.Count; q++)
                    dirty.Add(Cell(whole.X[q], whole.Z[q]));
        }

        dirty = Grown(dirty, 1);
        var context = Grown(dirty, 1);
        return new LocalRebuild(previous, near, halo, places) { Dirty = dirty, Context = context, Size = size, Seen = Over(previous, context) };
    }

    /// A local rebuild's cells grown to take in all of these lines (laid differently, and reaching out of them), with the ring round.
    private static LocalRebuild GrownFor(LocalRebuild local, IEnumerable<NetworkLine> lines)
    {
        var dirty = new HashSet<TileId>(local.Dirty!);
        foreach (var line in lines)
            for (var q = 0; q < line.Count; q++)
                if (!local.InDirty(line.X[q], line.Z[q]))
                    dirty.Add(local.CellOf(line.X[q], line.Z[q]));
        dirty = Grown(dirty, 1);
        var context = Grown(dirty, 1);
        return local with { Dirty = dirty, Context = context, Seen = Over(local.Previous, context) };
    }

    /// These cells and the rings round them.
    private static HashSet<TileId> Grown(HashSet<TileId> cells, int rings)
    {
        var grown = new HashSet<TileId>(cells);
        foreach (var c in cells)
            for (var dj = -rings; dj <= rings; dj++)
                for (var di = -rings; di <= rings; di++)
                    grown.Add(new TileId(c.I + di, c.J + dj));
        return grown;
    }

    /// The lines over (reaching into) any of these cells.
    private static List<NetworkLine> Over(NetworkLines lines, IEnumerable<TileId> cells)
    {
        var picked = new HashSet<int>();
        var over = new List<NetworkLine>();
        foreach (var cell in cells)
            foreach (var i in lines.LinesIn(cell))
                if (picked.Add(i))
                    over.Add(lines.Lines[i]);
        return over;
    }

    /// HB_LOCALCHECK=1, a rebuild of some cells: its lines and things against the same laid in full, cell by cell (CellStore.CheckMerge).
    private static void CheckCells(NetworkLines previous, CellStore.Merged merged, NetworkLines full, LocalRebuild local) =>
        CellStore.CheckMerge(previous, merged, full, local);

    /// HB_LOCALCHECK=1: a local rebuild's lines against the same laid in full, line by line, logged.
    private static void CheckLocal(NetworkLines local, NetworkLines full)
    {
        var byKey = full.Lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        var (missing, compared, off, worst, worstKey) = (0, 0, 0, 0.0, 0L);
        foreach (var line in local.Lines)
        {
            if (!byKey.TryGetValue(line.Key, out var other) || other.Count != line.Count)
            {
                missing++;
                continue;
            }

            compared++;
            var most = 0.0;
            for (var i = 0; i < line.Count; i++)
                most = Math.Max(most, Math.Abs(line.Y[i] - other.Y[i]));
            if (most > 0.01)
                off++;
            if (most > worst)
                (worst, worstKey) = (most, line.Key);
        }

        var extra = full.Lines.Count(l => !local.Lines.Any(m => m.Key == l.Key));
        // Where a kept line and one laid again meet at a node: they should be at one place and one height there.
        var (seams, worstStep, worstGap) = (0, 0.0, 0.0);
        foreach (var group in local.Lines.SelectMany(l => l.Nodes.Select(n => (Line: l, n.Node, n.Point))).GroupBy(u => u.Node))
        {
            var members = group.ToList();
            if (!members.Any(m => m.Line.Frozen || m.Line.Halo) || members.All(m => m.Line.Frozen || m.Line.Halo))
                continue;
            var kept = members.First(m => m.Line.Frozen || m.Line.Halo);
            foreach (var m in members.Where(m => !m.Line.Frozen && !m.Line.Halo))
            {
                seams++;
                worstStep = Math.Max(worstStep, Math.Abs(m.Line.Y[m.Point] - kept.Line.Y[kept.Point]));
                worstGap = Math.Max(worstGap, Math.Sqrt(Math.Pow(m.Line.X[m.Point] - kept.Line.X[kept.Point], 2) + Math.Pow(m.Line.Z[m.Point] - kept.Line.Z[kept.Point], 2)));
            }
        }

        // The same joins in the full rebuild, for what they'd be anyway.
        var fullByKey = full.Lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        var (fullStep, fullGap) = (0.0, 0.0);
        foreach (var group in local.Lines.SelectMany(l => l.Nodes.Select(n => (Line: l, n.Node, n.Point))).GroupBy(u => u.Node))
        {
            var members = group.ToList();
            if (!members.Any(m => m.Line.Frozen || m.Line.Halo) || members.All(m => m.Line.Frozen || m.Line.Halo))
                continue;
            var kept = members.First(m => m.Line.Frozen || m.Line.Halo);
            if (!fullByKey.TryGetValue(kept.Line.Key, out var fk) || fk.Count != kept.Line.Count)
                continue;
            foreach (var m in members.Where(m => !m.Line.Frozen && !m.Line.Halo))
                if (fullByKey.TryGetValue(m.Line.Key, out var fm) && fm.Count == m.Line.Count)
                {
                    fullStep = Math.Max(fullStep, Math.Abs(fm.Y[m.Point] - fk.Y[kept.Point]));
                    fullGap = Math.Max(fullGap, Math.Sqrt(Math.Pow(fm.X[m.Point] - fk.X[kept.Point], 2) + Math.Pow(fm.Z[m.Point] - fk.Z[kept.Point], 2)));
                }
        }

        RailWerkz.Core.Logging.Log.Info($"Local check: {seams} joins between kept lines and lines laid again: worst step {worstStep:0.000} m, worst gap {worstGap:0.000} m (the full rebuild's: {fullStep:0.000} m, {fullGap:0.000} m)");
        RailWerkz.Core.Logging.Log.Info($"Local check: {compared} lines compared, {off} over 1 cm apart (worst {worst:0.000} m, line {worstKey}{(local.Lines.FirstOrDefault(l => l.Key == worstKey) is { } w ? $", {(w.Frozen ? "kept" : "laid again")}, {(w.Rail ? "rail" : w.Mark ? "mark" : "road")}" : "")}); {missing} only in the local, {extra} only in the full");
    }

    /// The pads in either list that the other hasn't the same of.
    private static IEnumerable<CrossingPad> PadsChanged(IReadOnlyList<CrossingPad> before, IReadOnlyList<CrossingPad> after)
    {
        var (was, now) = (before.ToHashSet(), after.ToHashSet());
        return before.Where(p => !now.Contains(p)).Concat(after.Where(p => !was.Contains(p)));
    }

    private static bool SameProfile(NetworkLine a, NetworkLine b)
    {
        if (a.Count != b.Count || a.TurnoutZones.Count != b.TurnoutZones.Count || !a.RailCuts.SequenceEqual(b.RailCuts) || !a.TieCuts.SequenceEqual(b.TieCuts) || !a.BedDrops.SequenceEqual(b.BedDrops))
            return false;
        for (var i = 0; i < a.Count; i++)
            if (MathF.Abs(a.Y[i] - b.Y[i]) > 0.01f || Math.Abs(a.X[i] - b.X[i]) > 0.01 || Math.Abs(a.Z[i] - b.Z[i]) > 0.01)
                return false;

        // A switch resized or no longer a switch changes what its lines build, though the track itself may not move.
        for (var i = 0; i < a.TurnoutZones.Count; i++)
        {
            var (za, zb) = (a.TurnoutZones[i], b.TurnoutZones[i]);
            if (za.Turnout.Node != zb.Turnout.Node || za.Turnout.Number != zb.Turnout.Number || za.Rails != zb.Rails || za.Timbers != zb.Timbers || za.Ballast != zb.Ballast)
                return false;
        }

        return true;
    }

    /// Takes a rebuild's results in. Main thread. Returns the sample rectangles of the tiles that changed, for the
    /// terrain to rebuild.
    public List<(int X0, int Z0, int X1, int Z1)> Apply(NetworkChange change)
    {
        Lines = change.Lines;
        lock (_unsavedLock)
        {
            if (change.Touched is null)
                _unsavedCells = null;
            else
                _unsavedCells?.UnionWith(change.Touched);
        }

        var rects = new List<(int, int, int, int)>();
        foreach (var (id, heights) in change.Heights)
        {
            var tile = _tiles[id];
            var fresh = MakeTile(id, heights, tile.Cover, Samples, null);
            Array.Copy(fresh.Coarse, tile.Coarse, tile.Coarse.Length);
            tile.MinHeight = fresh.MinHeight;
            tile.MaxHeight = fresh.MaxHeight;
            if (tile.Full is not null)
                tile.Full = heights;
            rects.Add((id.I * Quads, -(id.J + 1) * Quads, (id.I + 1) * Quads, -id.J * Quads));
        }

        return rects;
    }
}
