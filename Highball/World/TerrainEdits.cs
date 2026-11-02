using System.IO.Compression;
using System.Runtime.InteropServices;
using Brutal.StbApi;
using Highball.Routes;

namespace Highball.World;

/// The ways a brush changes the ground.
public enum SculptMode { Raise, Lower, Smooth, Flatten }

/// One brush stroke's changes, kept so it can be undone and redone: each touched sample's height offset and each
/// touched cover cell, before and after.
public sealed class TerrainStroke
{
    internal readonly Dictionary<(TerrainSource.Tile Tile, int Index), (float Before, float After)> Heights = [];
    internal readonly Dictionary<(TerrainSource.Tile Tile, int Index), (byte Before, byte After)> Cover = [];

    public bool IsEmpty => Heights.Count == 0 && Cover.Count == 0;
}

/// Hand edits on top of the built ground: sculpting, as a height offset per sample (Tiles/I_J/Edits.bin.gz, added
/// after the ground has been reshaped for the lines, so the hand always has the last word), and painting land cover
/// straight into the tile's Cover.png. Only tiles with their full heights in memory (those near the camera) can be
/// sculpted.
public sealed partial class TerrainSource
{
    private const string EditsFile = "Edits.bin.gz";
    private static readonly (int, int)[] Corners = [(0, 0), (-1, 0), (0, -1), (-1, -1)];

    private static float[]? LoadEdits(string folder, int samples)
    {
        var path = Path.Combine(folder, EditsFile);
        if (!File.Exists(path))
            return null;

        using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        var edits = MemoryMarshal.Cast<byte, float>(output.ToArray()).ToArray();
        return edits.Length == samples * samples ? edits : null;
    }

    private static void ApplyEdits(float[] heights, float[]? edits)
    {
        if (edits is null)
            return;
        for (var i = 0; i < heights.Length; i++)
            heights[i] += edits[i];
    }

    /// The tiles holding a sample: one inside a tile, two on an edge, four at a corner. Each keeps its own copy.
    private IEnumerable<(Tile Tile, int Sx, int Sz)> Holders(int gx, int gz)
    {
        var i = FloorDiv(gx, Quads);
        var k = FloorDiv(gz, Quads);
        var sx = gx - i * Quads;
        var sz = gz - k * Quads;
        foreach (var (di, dk) in Corners)
        {
            if ((di < 0 && sx != 0) || (dk < 0 && sz != 0))
                continue;
            if (_tiles.TryGetValue(new TileId(i + di, -(k + dk) - 1), out var tile))
                yield return (tile, sx - di * Quads, sz - dk * Quads);
        }
    }

    /// One brush application at (x, z) world metres: radius metres across with a soft edge, strength in metres (raise
    /// and lower) or as a fraction of the way to the target (smooth and flatten, towards flattenTo). Returns the
    /// sample rectangle it changed, for the terrain to rebuild, or null if nothing could be changed.
    public (int X0, int Z0, int X1, int Z1)? Sculpt(double x, double z, float radius, SculptMode mode, float strength, float flattenTo, TerrainStroke stroke)
    {
        var gx0 = (int)Math.Floor((x - radius) / Spacing);
        var gx1 = (int)Math.Ceiling((x + radius) / Spacing);
        var gz0 = (int)Math.Floor((z - radius) / Spacing);
        var gz1 = (int)Math.Ceiling((z + radius) / Spacing);
        if (!FullReady(gx0 - 1, gz0 - 1, gx1 + 1, gz1 + 1))
            return null;

        // Work out every new height from the old ones first, so smoothing doesn't read its own output.
        var changes = new List<(int Gx, int Gz, float Delta)>();
        for (var gz = gz0; gz <= gz1; gz++)
        {
            for (var gx = gx0; gx <= gx1; gx++)
            {
                var dx = gx * Spacing - x;
                var dz = gz * Spacing - z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz) / radius;
                if (d >= 1f || !TryHeight(gx, gz, out var h))
                    continue;

                var weight = 1f - d * d * (3f - 2f * d);
                var target = mode switch
                {
                    SculptMode.Raise => h + strength,
                    SculptMode.Lower => h - strength,
                    SculptMode.Flatten => h + (flattenTo - h) * strength,
                    _ => h + (Neighbourhood(gx, gz, h) - h) * strength,
                };
                var delta = (target - h) * weight;
                if (MathF.Abs(delta) > 1e-5f)
                    changes.Add((gx, gz, delta));
            }
        }

        if (changes.Count == 0)
            return null;

        foreach (var (gx, gz, delta) in changes)
            foreach (var (tile, sx, sz) in Holders(gx, gz))
                Offset(tile, sx, sz, delta, stroke);

        return (gx0, gz0, gx1, gz1);
    }

    /// The mean of a sample's eight neighbours.
    private float Neighbourhood(int gx, int gz, float fallback)
    {
        var sum = 0f;
        var count = 0;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dz != 0) && TryHeight(gx + dx, gz + dz, out var h))
                {
                    sum += h;
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : fallback;
    }

    private void Offset(Tile tile, int sx, int sz, float delta, TerrainStroke stroke)
    {
        var full = tile.Full;
        if (full is null)
            return;

        var index = sz * Samples + sx;
        tile.Edits ??= new float[Samples * Samples];
        var before = tile.Edits[index];
        tile.Edits[index] = before + delta;
        var key = (tile, index);
        stroke.Heights[key] = (stroke.Heights.TryGetValue(key, out var was) ? was.Before : before, before + delta);
        SetHeight(tile, sx, sz, full[index] + delta);
        tile.EditsDirty = true;
    }

    /// Writes a height into a tile's full heights, its coarse copy where the sample is one of those, and its range.
    private void SetHeight(Tile tile, int sx, int sz, float height)
    {
        var full = tile.Full;
        if (full is null)
            return;

        full[sz * Samples + sx] = height;
        if (sx % CoarseStride == 0 && sz % CoarseStride == 0)
            tile.Coarse[sz / CoarseStride * CoarseSamples + sx / CoarseStride] = height;
        tile.MinHeight = MathF.Min(tile.MinHeight, height);
        tile.MaxHeight = MathF.Max(tile.MaxHeight, height);
    }

    /// Paints a land cover class onto every 10 m cell whose centre is within radius metres of (x, z). Returns the
    /// sample rectangle it changed.
    public (int X0, int Z0, int X1, int Z1)? Paint(double x, double z, float radius, byte cover, TerrainStroke stroke)
    {
        var cellSize = Quads * Spacing / CoverCells;
        var changed = false;
        var size = Quads * Spacing;
        foreach (var tile in _tiles.Values)
        {
            var x0 = tile.Id.I * size;
            var z0 = -(tile.Id.J + 1) * size;
            if (x + radius < x0 || x - radius > x0 + size || z + radius < z0 || z - radius > z0 + size)
                continue;

            var c0 = Math.Max(0, (int)Math.Floor((x - radius - x0) / cellSize));
            var c1 = Math.Min(CoverCells - 1, (int)Math.Ceiling((x + radius - x0) / cellSize));
            var r0 = Math.Max(0, (int)Math.Floor((z - radius - z0) / cellSize));
            var r1 = Math.Min(CoverCells - 1, (int)Math.Ceiling((z + radius - z0) / cellSize));
            for (var r = r0; r <= r1; r++)
            {
                for (var c = c0; c <= c1; c++)
                {
                    var dx = x0 + (c + 0.5) * cellSize - x;
                    var dz = z0 + (r + 0.5) * cellSize - z;
                    var index = r * CoverCells + c;
                    if (dx * dx + dz * dz > radius * radius || tile.Cover[index] == cover)
                        continue;

                    var key = (tile, index);
                    stroke.Cover[key] = (stroke.Cover.TryGetValue(key, out var was) ? was.Before : tile.Cover[index], cover);
                    tile.Cover[index] = cover;
                    tile.CoverDirty = true;
                    changed = true;
                }
            }
        }

        if (!changed)
            return null;

        return ((int)Math.Floor((x - radius - cellSize) / Spacing), (int)Math.Floor((z - radius - cellSize) / Spacing),
            (int)Math.Ceiling((x + radius + cellSize) / Spacing), (int)Math.Ceiling((z + radius + cellSize) / Spacing));
    }

    /// Puts a stroke's samples and cells back as they were (undo) or as it left them (redo). Returns the sample
    /// rectangle it changed.
    public (int X0, int Z0, int X1, int Z1)? Apply(TerrainStroke stroke, bool undo)
    {
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        void Grow(Tile tile, int sx, int sz)
        {
            var gx = tile.Id.I * Quads + sx;
            var gz = -(tile.Id.J + 1) * Quads + sz;
            x0 = Math.Min(x0, gx);
            z0 = Math.Min(z0, gz);
            x1 = Math.Max(x1, gx);
            z1 = Math.Max(z1, gz);
        }

        foreach (var ((tile, index), (before, after)) in stroke.Heights)
        {
            var value = undo ? before : after;
            tile.Edits ??= new float[Samples * Samples];
            var delta = value - tile.Edits[index];
            tile.Edits[index] = value;
            tile.EditsDirty = true;
            var sx = index % Samples;
            var sz = index / Samples;
            if (tile.Full is { } full)
                SetHeight(tile, sx, sz, full[index] + delta);
            Grow(tile, sx, sz);
        }

        var cellSamples = Quads / CoverCells;
        foreach (var ((tile, index), (before, after)) in stroke.Cover)
        {
            tile.Cover[index] = undo ? before : after;
            tile.CoverDirty = true;
            Grow(tile, (index % CoverCells) * cellSamples, (index / CoverCells) * cellSamples);
            Grow(tile, (index % CoverCells + 1) * cellSamples, (index / CoverCells + 1) * cellSamples);
        }

        return x0 <= x1 ? (x0, z0, x1, z1) : null;
    }

    /// Whether any edit hasn't been written yet.
    public bool Unsaved => _tiles.Values.Any(t => t.EditsDirty || t.CoverDirty);

    /// Writes every changed tile's height offsets and land cover.
    public void SaveEdits()
    {
        foreach (var tile in _tiles.Values)
        {
            var folder = _route.TileFolder(tile.Id);
            if (tile.EditsDirty && tile.Edits is { } edits)
            {
                using var file = File.Create(Path.Combine(folder, EditsFile));
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                gzip.Write(MemoryMarshal.AsBytes(edits.AsSpan()));
                tile.EditsDirty = false;
            }

            if (tile.CoverDirty)
            {
                Stb.WritePng(Path.Combine(folder, "Cover.png"), CoverCells, CoverCells, 1, tile.Cover);
                tile.CoverDirty = false;
            }
        }
    }
}
