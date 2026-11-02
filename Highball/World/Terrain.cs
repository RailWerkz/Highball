using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex(float3 position, float3 normal, float4 cover, float4 materials)
{
    public float3 Position = position;
    public float3 Normal = normal;
    /// Ground colour from the land cover (linear) and how much of it is tree canopy.
    public float4 Cover = cover;
    /// How much of each ground texture: grass, bare earth, water.
    public float4 Materials = materials;

    public static readonly VertexAttribute[] Layout = [new(0, 3, 0), new(1, 3, 12), new(2, 4, 24), new(3, 4, 40)];
}

/// The route's ground as a quadtree of patches over the global sample grid. Every patch is 64 x 64 quads; a patch at
/// level L spaces its vertices 2^L samples apart (2 m at level 0, 512 m at level 8), and patches nearer the camera
/// than DetailFactor of their own vertex spacings split into four. Levels 0 to 2 need the tiles' full heights, so
/// they only appear once those have loaded; the rest read the coarse copy every tile keeps.
///
/// Neighbouring patches of different levels are stitched as in DeathFromAbove: along an edge facing a coarser patch,
/// the finer one keeps only the vertices the coarse one also has and fans the row of quads inside onto them, so both
/// sides of the seam are the same straight segments. Since every level's grid nests in the next, that works for any
/// difference in level, and the stitched index lists are shared by every patch with the same edge ratios.
public sealed class Terrain : IDisposable
{
    public const int Quads = 64;
    public const int MaxLevel = 8;
    private const int FullLevels = 3;
    private const int MaxUploadsPerFrame = 12;

    private readonly record struct Key(int Level, int X, int Z);

    private sealed class Patch
    {
        public required Key Key { get; init; }
        public required float3 Origin { get; init; }
        public required Mesh Mesh { get; init; }
        /// Vertices outside the built tiles, or null when every vertex is inside.
        public bool[]? Valid { get; init; }
        public Dictionary<int, (BufferEx Buffer, int Count)>? OwnIndices;
        public long Used;
    }

    private sealed record Built(Key Key, float3 Origin, TerrainVertex[] Vertices, bool[]? Valid);

    private readonly TerrainSource _source;
    private readonly Dictionary<Key, Patch> _patches = [];
    private readonly HashSet<Key> _building = [];
    /// Patches whose ground has been edited since they were built: kept on screen until their rebuild arrives.
    private readonly HashSet<Key> _stale = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly Dictionary<int, (BufferEx Buffer, int Count)> _sharedIndices = [];
    private readonly Dictionary<Key, int> _leaves = [];
    private readonly List<Key> _drawList = [];
    private long _frame;

    /// How many vertex spacings away a patch must be before it's drawn that coarse; higher is finer.
    public float DetailFactor { get; set; } = 140f;

    public TerrainSource Source => _source;
    public int PatchCount => _patches.Count;
    public int DrawnPatches => _drawList.Count;
    public int Building => _building.Count;

    public Terrain(TerrainSource source) => _source = source;

    private float SpacingOf(int level) => _source.Spacing * (1 << level);

    private static int SamplesOf(int level) => Quads << level;

    /// The patch's sample rectangle.
    private static (int X0, int Z0, int X1, int Z1) Rect(Key k)
    {
        var size = SamplesOf(k.Level);
        return (k.X * size, k.Z * size, (k.X + 1) * size, (k.Z + 1) * size);
    }

    // --- Choosing patches. ---

    /// Picks the patches to draw from the camera (world metres), asks for what's missing, and uploads what's built.
    public void Update(double cx, double cy, double cz)
    {
        _frame++;
        _source.Want(cx, cz, DetailFactor * SpacingOf(FullLevels) * 1.6);

        for (var n = 0; n < MaxUploadsPerFrame && _built.TryDequeue(out var built); n++)
        {
            _building.Remove(built.Key);
            if (_patches.Remove(built.Key, out var old))
                Release(old);

            var mesh = Mesh.Create<TerrainVertex>(built.Vertices, TerrainVertex.Layout);
            _patches[built.Key] = new Patch { Key = built.Key, Origin = built.Origin, Mesh = mesh, Valid = built.Valid, Used = _frame };
        }

        _leaves.Clear();
        if (_source.Tiles.Count > 0)
        {
            var rootSize = SamplesOf(MaxLevel);
            for (var z = TerrainSource.FloorDiv(_source.MinGz, rootSize); z * rootSize < _source.MaxGz; z++)
                for (var x = TerrainSource.FloorDiv(_source.MinGx, rootSize); x * rootSize < _source.MaxGx; x++)
                    Select(new Key(MaxLevel, x, z), cx, cy, cz);
        }

        _drawList.Clear();
        foreach (var key in _leaves.Keys)
            if (_patches.ContainsKey(key))
                _drawList.Add(key);

        Evict();
    }

    private double Distance(Key k, double cx, double cy, double cz)
    {
        var (x0, z0, x1, z1) = Rect(k);
        var (min, max) = _source.HeightRange(x0, z0, x1, z1);
        var s = _source.Spacing;
        var dx = Math.Max(0.0, Math.Max(x0 * s - cx, cx - x1 * s));
        var dy = Math.Max(0.0, Math.Max(min - cy, cy - max));
        var dz = Math.Max(0.0, Math.Max(z0 * s - cz, cz - z1 * s));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void Select(Key k, double cx, double cy, double cz)
    {
        var (x0, z0, x1, z1) = Rect(k);
        if (!_source.AnyBuilt(x0, z0, x1, z1))
            return;

        var distance = Distance(k, cx, cy, cz);
        var wantSplit = k.Level > 0 && distance < DetailFactor * SpacingOf(k.Level);

        // Children are built a little before they're needed, so splitting rarely waits.
        if (k.Level > 0 && distance < DetailFactor * SpacingOf(k.Level) * 1.5)
            foreach (var child in Children(k))
                if (CanBuild(child))
                    Request(child);

        if (wantSplit && Children(k).All(c => !Covers(c) || (_patches.ContainsKey(c) && CanBuild(c))))
        {
            foreach (var child in Children(k))
                Select(child, cx, cy, cz);
            return;
        }

        if (_patches.TryGetValue(k, out var patch))
        {
            patch.Used = _frame;
            if (_stale.Contains(k))
                Request(k);
        }
        else
            Request(k);

        _leaves[k] = k.Level;
    }

    private bool Covers(Key k)
    {
        var (x0, z0, x1, z1) = Rect(k);
        return _source.AnyBuilt(x0, z0, x1, z1);
    }

    private static Key[] Children(Key k) =>
    [
        new(k.Level - 1, k.X * 2, k.Z * 2),
        new(k.Level - 1, k.X * 2 + 1, k.Z * 2),
        new(k.Level - 1, k.X * 2, k.Z * 2 + 1),
        new(k.Level - 1, k.X * 2 + 1, k.Z * 2 + 1),
    ];

    /// Coarse levels can always be built; the fine ones once every tile they read (normals reach one step past the
    /// edge) has its full heights.
    private bool CanBuild(Key k)
    {
        if (k.Level >= FullLevels)
            return true;

        var (x0, z0, x1, z1) = Rect(k);
        var step = 1 << k.Level;
        return _source.FullReady(x0 - step, z0 - step, x1 + step, z1 + step);
    }

    private void Request(Key k)
    {
        if ((_patches.ContainsKey(k) && !_stale.Contains(k)) || !_building.Add(k))
            return;

        _stale.Remove(k);
        Task.Run(() => _built.Enqueue(Build(k)));
    }

    private void Evict()
    {
        if (_patches.Count < 1500)
            return;

        foreach (var patch in _patches.Values.Where(p => _frame - p.Used > 120).OrderBy(p => p.Used).Take(_patches.Count - 1200).ToList())
        {
            Release(patch);
            _patches.Remove(patch.Key);
        }
    }

    private static void Release(Patch patch)
    {
        patch.Mesh.Dispose();
        if (patch.OwnIndices is not null)
            foreach (var (buffer, _) in patch.OwnIndices.Values)
                Release(buffer);
    }

    /// Marks every patch touching the sample rectangle (and one step round it, which its normals read) to be built
    /// again from the edited ground.
    public void Invalidate(int gx0, int gz0, int gx1, int gz1)
    {
        foreach (var key in _patches.Keys)
        {
            var (x0, z0, x1, z1) = Rect(key);
            var step = 1 << key.Level;
            if (x1 + step >= gx0 && x0 - step <= gx1 && z1 + step >= gz0 && z0 - step <= gz1)
                _stale.Add(key);
        }
    }

    // --- Building a patch (worker threads). ---

    private Built Build(Key k)
    {
        var (x0, z0, _, _) = Rect(k);
        var step = 1 << k.Level;
        var spacing = SpacingOf(k.Level);
        var width = Quads + 1;
        var origin = new float3(x0 * _source.Spacing, 0f, z0 * _source.Spacing);
        var vertices = new TerrainVertex[width * width];
        bool[]? valid = null;

        for (var z = 0; z < width; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var gx = x0 + x * step;
                var gz = z0 + z * step;
                if (!_source.TryHeight(gx, gz, out var h))
                {
                    valid ??= Enumerable.Repeat(true, width * width).ToArray();
                    valid[z * width + x] = false;
                    continue;
                }

                // Central differences one vertex spacing out, one-sided where the route ends.
                var hl = _source.TryHeight(gx - step, gz, out var l) ? l : h;
                var hr = _source.TryHeight(gx + step, gz, out var r) ? r : h;
                var hu = _source.TryHeight(gx, gz - step, out var u) ? u : h;
                var hd = _source.TryHeight(gx, gz + step, out var d) ? d : h;
                var normal = Normalize(new float3(hl - hr, 2f * spacing, hu - hd));

                vertices[z * width + x] = new TerrainVertex(new float3(x * spacing, h, z * spacing), normal, _source.Cover(gx, gz, spacing),
                    _source.Materials(gx, gz, spacing));
            }
        }

        return new Built(k, origin, vertices, valid);
    }

    private static float3 Normalize(float3 v) => v / MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    // --- Stitched index lists. ---

    /// How many of this patch's quads one quad of the patch across each edge spans: 1 when it's as fine or finer
    /// (it does the stitching), 2^n when it's n levels coarser. Packed 3 bits an edge: north, south, west, east.
    private int Ratios(Key k)
    {
        var packed = 0;
        packed |= Log2Ratio(k, 0, -1) << 0;
        packed |= Log2Ratio(k, 0, 1) << 3;
        packed |= Log2Ratio(k, -1, 0) << 6;
        packed |= Log2Ratio(k, 1, 0) << 9;
        return packed;
    }

    private int Log2Ratio(Key k, int dx, int dz)
    {
        for (var level = k.Level; level <= MaxLevel; level++)
        {
            var shift = level - k.Level;
            if (_leaves.ContainsKey(new Key(level, (k.X + dx) >> shift, (k.Z + dz) >> shift)))
                return Math.Min(shift, 6);
        }

        return 0;
    }

    /// The triangles of a 64 x 64 grid with its edges stitched to the given ratios, skipping any that touch an invalid
    /// vertex. Counter-clockwise seen from above.
    private static uint[] Triangulate(int ratios, bool[]? valid)
    {
        const int width = Quads + 1;
        var indices = new List<uint>(Quads * Quads * 6);
        var north = 1 << (ratios & 7);
        var south = 1 << ((ratios >> 3) & 7);
        var west = 1 << ((ratios >> 6) & 7);
        var east = 1 << ((ratios >> 9) & 7);

        uint At(int x, int z) => (uint)(z * width + x);

        // Seen from above (+Y), with x east and z south, counter-clockwise is a negative turn in x/z.
        void Triangle(int ax, int az, int bx, int bz, int cx, int cz)
        {
            var turn = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (turn == 0)
                return;
            if (valid is not null && (!valid[az * width + ax] || !valid[bz * width + bx] || !valid[cz * width + cx]))
                return;
            if (turn < 0)
                indices.AddRange([At(ax, az), At(bx, bz), At(cx, cz)]);
            else
                indices.AddRange([At(ax, az), At(cx, cz), At(bx, bz)]);
        }

        var x0 = west > 1 ? 1 : 0;
        var x1 = east > 1 ? Quads - 1 : Quads;
        var z0 = north > 1 ? 1 : 0;
        var z1 = south > 1 ? Quads - 1 : Quads;

        for (var z = z0; z < z1; z++)
        {
            for (var x = x0; x < x1; x++)
            {
                Triangle(x, z, x, z + 1, x + 1, z);
                Triangle(x + 1, z, x, z + 1, x + 1, z + 1);
            }
        }

        // DeathFromAbove's zip: walk the coarse vertices along the edge and the grid row one in together, advancing
        // whichever side's next segment is centred first.
        void Stitch(int ratio, Func<int, int, (int X, int Z)> place, int innerFrom, int innerTo)
        {
            var edge = new List<int>();
            for (var t = 0; t <= Quads; t += ratio)
                edge.Add(t);

            var i = 0;
            var j = innerFrom;
            while (i < edge.Count - 1 || j < innerTo)
            {
                var advanceEdge = j >= innerTo || (i < edge.Count - 1 && edge[i] + edge[i + 1] <= j + j + 1);
                var (ex, ez) = place(edge[i], 0);
                var (ix, iz) = place(j, 1);

                if (advanceEdge)
                {
                    var (nx, nz) = place(edge[i + 1], 0);
                    Triangle(ex, ez, ix, iz, nx, nz);
                    i++;
                }
                else
                {
                    var (nx, nz) = place(j + 1, 1);
                    Triangle(ex, ez, ix, iz, nx, nz);
                    j++;
                }
            }
        }

        if (north > 1) Stitch(north, (t, d) => (t, d), x0, x1);
        if (south > 1) Stitch(south, (t, d) => (t, Quads - d), x0, x1);
        if (west > 1) Stitch(west, (t, d) => (d, t), z0, z1);
        if (east > 1) Stitch(east, (t, d) => (Quads - d, t), z0, z1);

        return [.. indices];
    }

    // --- Drawing. ---

    /// Draws the chosen patches. The pipeline is bound; push writes each patch's origin relative to the camera.
    public void Draw(CommandBuffer cmd, double cx, double cy, double cz, Action<float3> push)
    {
        foreach (var key in _drawList)
        {
            var patch = _patches[key];
            var ratios = Ratios(key);
            BufferEx indices;
            int count;

            if (patch.Valid is null)
            {
                if (!_sharedIndices.TryGetValue(ratios, out var shared))
                {
                    var list = Triangulate(ratios, null);
                    shared = (Buffers.Upload(MemoryMarshal.AsBytes(list.AsSpan()), VkBufferUsageFlags.IndexBufferBit, "terrain.indices"), list.Length);
                    _sharedIndices[ratios] = shared;
                }

                (indices, count) = shared;
            }
            else
            {
                patch.OwnIndices ??= [];
                if (!patch.OwnIndices.TryGetValue(ratios, out var own))
                {
                    var list = Triangulate(ratios, patch.Valid);
                    own = list.Length > 0 ? (Buffers.Upload(MemoryMarshal.AsBytes(list.AsSpan()), VkBufferUsageFlags.IndexBufferBit, "terrain.indices"), list.Length) : (default, 0);
                    patch.OwnIndices[ratios] = own;
                }

                (indices, count) = own;
            }

            if (count == 0)
                continue;

            push(new float3((float)(patch.Origin.X - cx), (float)-cy, (float)(patch.Origin.Z - cz)));
            patch.Mesh.DrawWith(cmd, indices, count);
        }
    }

    private static void Release(BufferEx buffer)
    {
        if (!buffer.IsNull())
            Gpu.Defer(buffer.Dispose);
    }

    public void Dispose()
    {
        foreach (var patch in _patches.Values)
        {
            patch.Mesh.Dispose();
            if (patch.OwnIndices is not null)
                foreach (var (buffer, _) in patch.OwnIndices.Values)
                    Release(buffer);
        }

        foreach (var (buffer, _) in _sharedIndices.Values)
            Release(buffer);

        _patches.Clear();
        _sharedIndices.Clear();
    }
}
