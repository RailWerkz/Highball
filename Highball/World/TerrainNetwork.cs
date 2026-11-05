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

        return Task.Run(() =>
        {
            using var counted = Background.Begin("Laying track");
            if (solveAgain)
                NetworkLines.ForgetSolved(_route);
            var lines = NetworkLines.Build(ways, _route, built, shapes);
            Parallel.ForEach(lines.AdoptGround(previous), id =>
            {
                var data = RouteTile.Load(_route.TileFolder(id));
                lines.SampleGround(id, data.Heights, samples);
            });
            lines.FinishProfiles();
            lines.AdoptDrapes(previous);
            lines.FindLayouts(tagged);
            TrackLayouts.SaveMarkers(_route, lines.Layouts);

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

            var heights = new ConcurrentDictionary<TileId, float[]>();
            Parallel.ForEach(tiles, id =>
            {
                var reshaped = RouteTile.Load(_route.TileFolder(id)).Heights;
                lines.Conform(id, reshaped, samples);
                ApplyEdits(reshaped, _tiles[id].Edits);
                lines.DrapeMarks(id, reshaped, samples);
                heights[id] = reshaped;
            });

            return new NetworkChange(lines, heights, areas);
        });
    }

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
