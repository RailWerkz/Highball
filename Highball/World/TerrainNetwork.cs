using Highball.App;
using System.Collections.Concurrent;
using Highball.Routes;

namespace Highball.World;

/// What an edit to the network changed, worked out off the main thread: the new lines, the heights of every tile
/// whose reshaping changed (reshaped again from the ground as built, with the hand edits on top), and the areas
/// (world metres: min x, min z, max x, max z) whose line meshes need building again.
public sealed record NetworkChange(NetworkLines Lines, IReadOnlyDictionary<TileId, float[]> Heights, IReadOnlyList<(double X0, double Z0, double X1, double Z1)> Areas);

public sealed partial class TerrainSource
{
    /// Rebuilds the lines from the network as it is now. Only lines that came out different (or that the junctions
    /// they meet pulled to a new height) cost anything: their ground is sampled again and the tiles they reach are
    /// reshaped again. solveAgain: the saved track's thrown away and it's worked out afresh and saved.
    public Task<NetworkChange> RebuildNetwork(bool solveAgain = false)
    {
        var ways = Network.Ways.Values.ToList(); // edits replace ways rather than change them, so this is a snapshot
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

            // Laid again near the edit only, if it was to track alone (LocalRebuild); otherwise in full.
            var local = solveAgain ? null : LocalFor(previous, ways, crossings);
            NetworkLines Lay(LocalRebuild? near, Action<string> stage)
            {
                var laid = NetworkLines.Build(ways, _route, built, shapes, near);
                stage("build");
                laid.SetCrossings(crossings, _route.Projection!);
                var sampled = laid.AdoptGround(previous).ToList();
                Parallel.ForEach(sampled, id =>
                {
                    var data = _route.LoadTile(id);
                    laid.SampleGround(id, data.Heights, samples);
                });
                stage($"ground ({sampled.Count} tiles)");
                laid.Before = near?.Previous;
                laid.FinishProfiles();
                stage("profiles");
                laid.AdoptDrapes(previous);
                laid.FindLayouts(tagged);
                stage("layouts");
                laid.Bridges = BridgePlanner.Find(laid, near?.Previous);
                stage("bridges");
                // (Whatever a pass did to a line it should have left be is undone: kept lines come out exactly as they were.)
                if (laid.RestoreFrozen() is var touched and > 0)
                    RailWerkz.Core.Logging.Log.Info($"Local rebuild: {touched} kept lines were changed by a pass and put back");
                return laid;
            }

            var lines = Lay(local, Stage);
            TrackLayouts.SaveMarkers(_route, lines.Layouts);
            Stage(local is null ? "markers (in full)" : $"markers (near the edit: {local.Near.Count} roads and marks laid again, {local.Halo.Count} round them)");
            if (local is not null && Environment.GetEnvironmentVariable("HB_LOCALCHECK") == "1")
                CheckLocal(lines, Lay(null, _ => { }));

            var old = new Dictionary<long, NetworkLine>();
            foreach (var line in previous.Lines)
                old.TryAdd(line.Key, line);

            var kept = new HashSet<long>();
            var tiles = new HashSet<TileId>();
            var areas = new List<(double, double, double, double)>();
            foreach (var line in lines.Lines)
            {
                if (old.TryGetValue(line.Key, out var same) && SameProfile(same, line))
                {
                    kept.Add(line.Key);
                    continue;
                }

                tiles.UnionWith(line.Tiles);
                areas.Add((line.MinX, line.MinZ, line.MaxX, line.MaxZ));
            }

            foreach (var line in previous.Lines)
            {
                if (kept.Contains(line.Key))
                    continue;
                tiles.UnionWith(line.Tiles);
                areas.Add((line.MinX, line.MinZ, line.MaxX, line.MaxZ));
            }

            // A crossing's pad that's new, gone or changed reshapes the ground round it, and its roads' ends.
            var size = _route.Grid!.TileSize;
            foreach (var pad in PadsChanged(previous.Pads, lines.Pads))
            {
                var reach = pad.Half + 20f;
                var (a, b) = (pad.At(pad.From - 10f), pad.At(pad.To + 10f));
                var (x0, x1, z0, z1) = (Math.Min(a.X, b.X) - reach, Math.Max(a.X, b.X) + reach, Math.Min(a.Z, b.Z) - reach, Math.Max(a.Z, b.Z) + reach);
                areas.Add((x0, z0, x1, z1));
                for (var j = (int)Math.Floor(-z1 / size); j <= (int)Math.Floor(-z0 / size); j++)
                    for (var i = (int)Math.Floor(x0 / size); i <= (int)Math.Floor(x1 / size); i++)
                        if (_tiles.ContainsKey(new TileId(i, j)))
                            tiles.Add(new TileId(i, j));
            }

            var heights = new ConcurrentDictionary<TileId, float[]>();
            Parallel.ForEach(tiles, id =>
            {
                var reshaped = _route.LoadTile(id).Heights;
                lines.Conform(id, reshaped, samples);
                ApplyEdits(reshaped, _tiles[id].Edits);
                lines.DrapeMarks(id, reshaped, samples);
                heights[id] = reshaped;
            });
            Stage($"reshape ({tiles.Count} tiles)");
            RailWerkz.Core.Logging.Log.Info($"Track rebuild, ms: {string.Join(", ", stages)}");

            return new NetworkChange(lines, heights, areas);
        });
    }

    /// How many rings of lines meeting those laid again are laid again round them and put back after (LocalRebuild.Halo).
    private const int HaloRings = 3;

    /// How far an edit to track can change a line, metres: a road eased onto a level crossing (75 m), a road bridge lifted clear
    /// of new track and climbing back down (200 m and more), a junction's height easing along what meets there.
    private const double LocalReach = 500.0;

    /// A local rebuild, if what's changed since the last lines were laid is track alone (no road, mapped track, crossing or
    /// anything else): the roads and marks with a point within LocalReach of the changed track (as it was and as it is) are laid
    /// again, the rest kept. Null for a full rebuild.
    private LocalRebuild? LocalFor(NetworkLines previous, List<NetworkWay> ways, List<PlacedObject> crossings)
    {
        if (previous.Ways is not { } was || !previous.SameCrossings(crossings))
            return null;
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
        if (changed.Any(w => !w.Rail || RouteNetwork.IsGuide(w)))
            return null;

        var projection = _route.Projection!;
        var places = new List<(double X, double Z)>();
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

        var near = previous.ChainsNear(places, LocalReach);
        // The halo, HaloRings deep (each ring the chains meeting the last).
        var halo = new HashSet<long>();
        var reached = new HashSet<long>(near);
        for (var ring = 0; ring < HaloRings; ring++)
        {
            var next = previous.ChainsMeeting(reached);
            if (next.Count == 0)
                break;
            halo.UnionWith(next);
            reached.UnionWith(next);
        }

        return new LocalRebuild(previous, near, halo);
    }

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
    private static IEnumerable<CrossingPad> PadsChanged(IReadOnlyList<CrossingPad> before, IReadOnlyList<CrossingPad> after) =>
        before.Where(p => !after.Contains(p)).Concat(after.Where(p => !before.Contains(p)));

    private static bool SameProfile(NetworkLine a, NetworkLine b)
    {
        if (a.Count != b.Count || a.TurnoutZones.Count != b.TurnoutZones.Count || !a.RailCuts.SequenceEqual(b.RailCuts) || !a.TieCuts.SequenceEqual(b.TieCuts))
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
