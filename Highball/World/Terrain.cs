using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Vulkan;

namespace Highball.World;

[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex(float3 position, float3 normal, float4 cover, float4 materials, float ballast = 0f)
{
    public float3 Position = position;
    public float3 Normal = normal;
    /// Ground colour from the land cover (linear) and how much of it is tree canopy.
    public float4 Cover = cover;
    /// How much of each ground texture: grass, bare earth, water.
    public float4 Materials = materials;
    /// How much of the ground here is a track's ballast run out over it, 0 to 1 (NetworkLines.BallastAt): terrain.frag lays its
    /// stones into the ground by it, thinning out away from the track.
    public float Ballast = ballast;

    public static readonly VertexAttribute[] Layout = [new(0, 3, 0), new(1, 3, 12), new(2, 4, 24), new(3, 4, 40), new(4, 1, 56)];
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
///
/// Patches are built on BRUTAL's job system (a JobStream), nearest first: each batch is the nearest of what's wanted
/// when the last one finishes, so what's no longer wanted is never built. Ahead of a moving camera, the patches and full
/// heights it's heading into are asked for too, as far ahead as it'll be in LookAhead seconds.
public sealed class Terrain : IDisposable
{
    public const int Quads = 64;
    public const int MaxLevel = 8;
    private const int FullLevels = 3;
    private const int MinUploadsPerFrame = 4;
    private const double UploadBudgetMs = 3.0;
    private const double LookAhead = 2.5;
    private const double MaxLookAhead = 6000.0;
    /// Patches held before the least recently used start going, a few a frame: freeing a GPU buffer takes a fraction
    /// of a millisecond, so letting hundreds go at once stalls a frame.
    private const int MaxPatches = 1200;
    private const int EvictionsPerFrame = 8;

    private readonly record struct Key(int Level, int X, int Z);

    private sealed class Patch
    {
        public required Key Key { get; init; }
        public required float3 Origin { get; init; }
        public required Mesh Mesh { get; init; }
        /// The middle of its vertices, relative to Origin, and the distance from there to the furthest.
        public required float3 Centre { get; init; }
        public required float Radius { get; init; }
        /// Vertices outside the built tiles, or null when every vertex is inside.
        public bool[]? Valid { get; init; }
        /// The quads split the other way, to lie along the slope; null when none are.
        public bool[]? Flips { get; init; }
        public Dictionary<int, (BufferEx Buffer, int Count)>? OwnIndices;
        /// The detail inserts over its holes (finest patches only): the ground round a structure shaped finer than its samples.
        public Mesh? Insert;
        public BufferEx InsertIndices;
        public int InsertCount;
        public long Used;
    }

    /// Epoch: the edit count when the build started, so an edit made while it ran still leaves the patch stale.
    private sealed record Built(Key Key, float3 Origin, TerrainVertex[] Vertices, bool[]? Valid, bool[]? Flips, int Epoch,
        TerrainVertex[]? InsertVertices = null, uint[]? InsertIndices = null);

    private readonly TerrainSource _source;
    private readonly Dictionary<Key, Patch> _patches = [];
    /// Patches whose ground has been edited since they were built, with the edit count then: kept on screen until a
    /// rebuild started after that edit arrives.
    private readonly Dictionary<Key, int> _stale = [];
    private int _epoch;
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly Dictionary<int, (BufferEx Buffer, int Count)> _sharedIndices = [];
    private readonly Dictionary<Key, int> _leaves = [];
    private readonly List<(Patch Patch, BufferEx Indices, int Count)> _drawList = [];

    /// This frame's wanted patches, each with how soon it's needed (metres from the camera, plus a margin for the ones
    /// asked for early), and the ones being built (until what's built is taken in).
    private readonly Dictionary<Key, double> _requests = [];
    private readonly HashSet<Key> _inFlight = [];
    private readonly JobStream _jobs = new("Terrain", 0.6);
    private int _waiting;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private (double X, double Y, double Z, double Time)? _last;
    private (double X, double Y, double Z) _velocity;
    private (double X, double Y, double Z) _camera;
    private long _frame;

    /// How many vertex spacings away a patch must be before it's drawn that coarse; higher is finer.
    public float DetailFactor { get; set; } = 70f;

    /// How far off, in metres, ground stops being drawn or built. 0: no limit.
    public double ViewLimit { get; set; }

    public TerrainSource Source => _source;
    public int PatchCount => _patches.Count;
    public int DrawnPatches => _drawList.Count;
    /// Patches drawn coarser than the camera wants, waiting for their children to be built.
    public int Waiting { get; private set; }

    public int Building => _inFlight.Count + _waiting;

    public Terrain(TerrainSource source) => _source = source;

    /// Goes up whenever the ground changes shape.
    public int Epoch => _epoch;

    private const int MaxEditsKept = 256;
    private readonly List<(int Epoch, int X0, int Z0, int X1, int Z1)> _edits = [];

    /// The sample rectangles edited since that epoch, or null if it's too long ago to say.
    public IEnumerable<(int X0, int Z0, int X1, int Z1)>? EditsSince(int epoch)
    {
        if (_epoch - epoch > _edits.Count)
            return null;
        return _edits.Where(e => e.Epoch > epoch).Select(e => (e.X0, e.Z0, e.X1, e.Z1));
    }

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
        _camera = (cx, cy, cz);
        var (ax, ay, az) = Ahead(cx, cy, cz);
        _source.Want(cx, cz, ax, az, DetailFactor * SpacingOf(FullLevels) * 1.6);

        var budget = Stopwatch.StartNew();
        for (var n = 0; (n < MinUploadsPerFrame || budget.Elapsed.TotalMilliseconds < UploadBudgetMs) && _built.TryDequeue(out var built); n++)
        {
            _inFlight.Remove(built.Key);
            if (built.Vertices is null)
                continue;
            if (_stale.TryGetValue(built.Key, out var edited) && edited <= built.Epoch)
                _stale.Remove(built.Key);
            if (_patches.Remove(built.Key, out var old))
                Release(old);

            var mesh = Mesh.Create<TerrainVertex>(built.Vertices, TerrainVertex.Layout, later: true);
            var (centre, radius) = Bounds(built.Vertices);
            var patch = new Patch { Key = built.Key, Origin = built.Origin, Mesh = mesh, Centre = centre, Radius = radius, Valid = built.Valid, Flips = built.Flips, Used = _frame };
            if (built.InsertVertices is { Length: > 0 } insert && built.InsertIndices is { Length: > 0 } insertIndices)
            {
                patch.Insert = Mesh.Create<TerrainVertex>(insert, TerrainVertex.Layout, later: true);
                patch.InsertIndices = Buffers.UploadLater(MemoryMarshal.AsBytes(insertIndices.AsSpan()), VkBufferUsageFlags.IndexBufferBit, "terrain.insert");
                patch.InsertCount = insertIndices.Length;
            }

            _patches[built.Key] = patch;
        }

        _leaves.Clear();
        Waiting = 0;
        if (_source.Tiles.Count > 0)
        {
            var rootSize = SamplesOf(MaxLevel);
            for (var z = TerrainSource.FloorDiv(_source.MinGz, rootSize); z * rootSize < _source.MaxGz; z++)
            {
                for (var x = TerrainSource.FloorDiv(_source.MinGx, rootSize); x * rootSize < _source.MaxGx; x++)
                {
                    Select(new Key(MaxLevel, x, z), cx, cy, cz, false);
                    if (ax != cx || az != cz)
                        Select(new Key(MaxLevel, x, z), ax, ay, az, true);
                }
            }
        }

        _waiting = _requests.Keys.Count(k => !_inFlight.Contains(k));
        if (!_jobs.Busy && _waiting > 0)
        {
            var batch = _requests.Where(r => !_inFlight.Contains(r.Key)).OrderBy(r => r.Value).Take(_jobs.BatchSize).Select(r => r.Key).ToList();
            _inFlight.UnionWith(batch);
            var epoch = _epoch;
            _jobs.TryRun(batch.Select(k => ((Brutal.Concurrency.Jobs.IJob)new Job("terrain patch", () => BuildInto(k, epoch)), 1)));
        }

        _requests.Clear();

        _drawList.Clear();
        foreach (var key in _leaves.Keys)
            if (_patches.TryGetValue(key, out var patch) && Indices(patch) is { Count: > 0 } indices)
                _drawList.Add((patch, indices.Buffer, indices.Count));

        Evict();
    }

    /// Where the camera will be in LookAhead seconds at its recent speed, at most MaxLookAhead away. A jump (placing
    /// the camera, not flying it) starts the speed again from rest.
    private (double X, double Y, double Z) Ahead(double cx, double cy, double cz)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_last is { } last && now > last.Time)
        {
            var dt = now - last.Time;
            var (vx, vy, vz) = ((cx - last.X) / dt, (cy - last.Y) / dt, (cz - last.Z) / dt);
            if (Math.Sqrt(vx * vx + vz * vz) * dt > 2000.0)
                _velocity = default;
            else
            {
                var t = Math.Min(1.0, dt * 4.0);
                _velocity = (_velocity.X + (vx - _velocity.X) * t, _velocity.Y + (vy - _velocity.Y) * t, _velocity.Z + (vz - _velocity.Z) * t);
            }
        }

        _last = (cx, cy, cz, now);
        var (ox, oy, oz) = (_velocity.X * LookAhead, _velocity.Y * LookAhead, _velocity.Z * LookAhead);
        var length = Math.Sqrt(ox * ox + oz * oz);
        if (length < 50.0)
            return (cx, cy, cz);
        var scale = Math.Min(1.0, MaxLookAhead / length);
        return (cx + ox * scale, cy + oy * scale, cz + oz * scale);
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

    /// Walks the quadtree from k for a camera at (cx, cy, cz). Ahead: for where the camera's heading, so it only asks
    /// for patches (and keeps the ones it would use) and draws nothing.
    private void Select(Key k, double cx, double cy, double cz, bool ahead)
    {
        var (x0, z0, x1, z1) = Rect(k);
        if (!_source.AnyBuilt(x0, z0, x1, z1))
            return;

        // Every patch the walk passes through is kept, not just the ones drawn: a parent is needed again the moment
        // its children are too far away to want.
        if (_patches.TryGetValue(k, out var patch))
            patch.Used = _frame;

        var distance = Distance(k, cx, cy, cz);
        if (ViewLimit > 0.0 && distance > ViewLimit)
            return;

        var wantSplit = k.Level > 0 && distance < DetailFactor * SpacingOf(k.Level);

        // Children are built a little before they're needed, so splitting rarely waits.
        if (k.Level > 0 && distance < DetailFactor * SpacingOf(k.Level) * 1.5)
            foreach (var child in Children(k))
                if (CanBuild(child))
                    Request(child, DetailFactor * SpacingOf(child.Level) * 0.5);

        if (wantSplit && (ahead || Children(k).All(c => !Covers(c) || (_patches.ContainsKey(c) && CanBuild(c)))))
        {
            foreach (var child in Children(k))
                if (!ahead || CanBuild(child))
                    Select(child, cx, cy, cz, ahead);
            return;
        }

        Request(k, ahead ? DetailFactor * SpacingOf(k.Level) * 0.5 : 0.0);

        if (ahead)
            return;
        _leaves[k] = k.Level;
        if (wantSplit)
            Waiting++;
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

    /// Asks for k to be built (or built again, if its ground was edited), as if it were margin metres further from the
    /// camera than it is.
    private void Request(Key k, double margin)
    {
        if (_patches.ContainsKey(k) && !_stale.ContainsKey(k))
            return;

        var priority = Distance(k, _camera.X, _camera.Y, _camera.Z) + margin;
        if (!_requests.TryGetValue(k, out var existing) || priority < existing)
            _requests[k] = priority;
    }

    /// A job: builds a patch, handing back an empty one if that fails so it's asked for again.
    private void BuildInto(Key key, int epoch)
    {
        try
        {
            _built.Enqueue(Build(key, epoch));
        }
        catch
        {
            _built.Enqueue(new Built(key, default, null!, null, null, epoch));
            throw;
        }
    }

    private void Evict()
    {
        if (_patches.Count <= MaxPatches)
            return;

        var count = Math.Min(EvictionsPerFrame, _patches.Count - MaxPatches);
        foreach (var patch in _patches.Values.Where(p => p.Used != _frame).OrderBy(p => p.Used).Take(count).ToList())
        {
            Release(patch);
            _patches.Remove(patch.Key);
        }
    }

    private static void Release(Patch patch)
    {
        patch.Mesh.Dispose();
        patch.Insert?.Dispose();
        Release(patch.InsertIndices);
        if (patch.OwnIndices is not null)
            foreach (var (buffer, _) in patch.OwnIndices.Values)
                Release(buffer);
    }

    /// Marks every patch touching the sample rectangle (and one step round it, which its normals read) to be built
    /// again from the edited ground.
    public void Invalidate(int gx0, int gz0, int gx1, int gz1)
    {
        var epoch = ++_epoch;
        if (_edits.Count == MaxEditsKept)
            _edits.RemoveAt(0);
        _edits.Add((epoch, gx0, gz0, gx1, gz1));
        foreach (var key in _patches.Keys)
        {
            var (x0, z0, x1, z1) = Rect(key);
            var step = 1 << key.Level;
            if (x1 + step >= gx0 && x0 - step <= gx1 && z1 + step >= gz0 && z0 - step <= gz1)
                _stale[key] = epoch;
        }
    }

    // --- Building a patch (worker threads). ---

    private Built Build(Key k, int epoch)
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

                var (cover, materials) = _source.Ground(gx, gz, spacing);
                vertices[z * width + x] = new TerrainVertex(new float3(x * spacing, h, z * spacing), normal, cover, materials, _source.BallastAt(gx, gz));
            }
        }

        var (insertVertices, insertIndices) = k.Level == 0 ? Inserts(x0, z0, ref valid) : (null, null);
        return new Built(k, origin, vertices, valid, Diagonals(vertices, spacing), epoch, insertVertices, insertIndices);
    }

    /// How far in from a detail region's edge its insert eases from the tiles' ground to its own, metres.
    private const float InsertFeather = 2f;

    /// A finest patch's detail inserts: for each detail region over it, its samples inside the region's edge made holes (so every grid
    /// square in the region goes, and none outside it), and over those squares a grid DetailSub times finer, from the region's ground
    /// shaped at that spacing. At the region's edge it's the tiles' ground exactly (along each square's side, straight between its
    /// two samples, as the terrain draws it), easing to its own over InsertFeather, so the two meet without a seam.
    private (TerrainVertex[]? Vertices, uint[]? Indices) Inserts(int x0, int z0, ref bool[]? valid)
    {
        const int width = Quads + 1;
        const int cell = NetworkLines.CellQuads;
        List<TerrainVertex>? vertices = null;
        List<uint>? indices = null;
        var spacing = _source.Spacing;
        foreach (var region in _source.DetailRegions)
        {
            if (!region.Touches(x0, z0, x0 + Quads, z0 + Quads))
                continue;
            var sub = region.Sub;
            var fine = _source.DetailHeights(region, out var n);
            if (fine is null)
                continue;

            for (var gz = Math.Max(region.Gz0 + 1, z0); gz <= Math.Min(region.Gz1 - 1, z0 + Quads); gz++)
                for (var gx = Math.Max(region.Gx0 + 1, x0); gx <= Math.Min(region.Gx1 - 1, x0 + Quads); gx++)
                {
                    valid ??= Enumerable.Repeat(true, width * width).ToArray();
                    valid[(gz - z0) * width + gx - x0] = false;
                }

            // The cells round it: how much finer each is (0: the tiles' ground), for where it meets them. A cell in another patch counts
            // as the tiles' ground: that patch may be drawn coarser, without its inserts, so the two can only meet on the tiles' ground.
            const int perPatch = Quads / cell;
            var round = new int[3, 3];
            for (var j = -1; j <= 1; j++)
                for (var i = -1; i <= 1; i++)
                {
                    var (cx, cz) = (region.Cx + i, region.Cz + j);
                    var samePatch = TerrainSource.FloorDiv(cx, perPatch) == TerrainSource.FloorDiv(region.Cx, perPatch) &&
                                    TerrainSource.FloorDiv(cz, perPatch) == TerrainSource.FloorDiv(region.Cz, perPatch);
                    round[i + 1, j + 1] = samePatch ? _source.DetailSubAt(cx, cz) : 0;
                }

            // A cell's own ground at a place in the tiles' grid, straight between its finer samples (on its edge, straight along the
            // edge between the samples it has there).
            float CellAt(NetworkLines.DetailRegion other, double gxf, double gzf)
            {
                var heights = other == region ? fine : _source.DetailHeights(other, out _);
                if (heights is null)
                    return 0f;
                var m = cell * other.Sub + 1;
                var (fu, fv) = ((gxf - other.Gx0) * other.Sub, (gzf - other.Gz0) * other.Sub);
                var (u, v) = (Math.Clamp((int)Math.Floor(fu), 0, m - 2), Math.Clamp((int)Math.Floor(fv), 0, m - 2));
                var (tu, tv) = ((float)Math.Clamp(fu - u, 0.0, 1.0), (float)Math.Clamp(fv - v, 0.0, 1.0));
                var top = heights[v * m + u] + (heights[v * m + u + 1] - heights[v * m + u]) * tu;
                var bottom = heights[(v + 1) * m + u] + (heights[(v + 1) * m + u + 1] - heights[(v + 1) * m + u]) * tu;
                return top + (bottom - top) * tv;
            }

            float Bilinear(double gxf, double gzf, Func<int, int, float> at)
            {
                var (gx, gz) = ((int)Math.Floor(gxf), (int)Math.Floor(gzf));
                var (fx, fz) = ((float)(gxf - gx), (float)(gzf - gz));
                var top = at(gx, gz) + (at(gx + 1, gz) - at(gx, gz)) * fx;
                var bottom = at(gx, gz + 1) + (at(gx + 1, gz + 1) - at(gx, gz + 1)) * fx;
                return top + (bottom - top) * fz;
            }

            // The ground drawn at a finer sample (u, v) of the cell. Where it meets the tiles' ground (a side, or a corner, with no
            // cell past it) it's theirs exactly, easing to its own over InsertFeather. On a side or corner it shares with a coarser
            // cell, it's that one's, so the two meet without a crack between their samples.
            float Height(int u, int v)
            {
                u = Math.Clamp(u, 0, n - 1);
                v = Math.Clamp(v, 0, n - 1);
                var (gxf, gzf) = (region.Gx0 + u / (double)sub, region.Gz0 + v / (double)sub);
                var unit = spacing / sub;
                var (w0, e0, n0, s0) = (u * unit, (n - 1 - u) * unit, v * unit, (n - 1 - v) * unit);
                var edge = float.MaxValue;
                if (round[0, 1] == 0) edge = MathF.Min(edge, w0);
                if (round[2, 1] == 0) edge = MathF.Min(edge, e0);
                if (round[1, 0] == 0) edge = MathF.Min(edge, n0);
                if (round[1, 2] == 0) edge = MathF.Min(edge, s0);
                if (round[0, 0] == 0) edge = MathF.Min(edge, MathF.Sqrt(w0 * w0 + n0 * n0));
                if (round[2, 0] == 0) edge = MathF.Min(edge, MathF.Sqrt(e0 * e0 + n0 * n0));
                if (round[0, 2] == 0) edge = MathF.Min(edge, MathF.Sqrt(w0 * w0 + s0 * s0));
                if (round[2, 2] == 0) edge = MathF.Min(edge, MathF.Sqrt(e0 * e0 + s0 * s0));
                var w = Math.Clamp(edge / InsertFeather, 0f, 1f);
                var coarse = Bilinear(gxf, gzf, (a, b) => _source.TryHeight(a, b, out var h) ? h : 0f);
                if (w <= 0f)
                    return coarse;

                // (Of the cells sharing this place, the coarsest.)
                var owner = region;
                var (onW, onE, onN, onS) = (u == 0, u == n - 1, v == 0, v == n - 1);
                if (onW || onE || onN || onS)
                    for (var j = onN ? -1 : 0; j <= (onS ? 1 : 0); j++)
                        for (var i = onW ? -1 : 0; i <= (onE ? 1 : 0); i++)
                            if (round[i + 1, j + 1] is > 0 and var other && other < owner.Sub)
                                owner = new NetworkLines.DetailRegion(region.Cx + i, region.Cz + j, other);
                var own = (owner == region ? fine[v * n + u] : CellAt(owner, gxf, gzf)) + Bilinear(gxf, gzf, _source.EditAt);
                return coarse + (own - coarse) * w;
            }

            // The squares of the cell in this patch, in finer samples from the cell's corner.
            var (qx0, qz0) = (Math.Max(region.Gx0, x0), Math.Max(region.Gz0, z0));
            var (qx1, qz1) = (Math.Min(region.Gx1, x0 + Quads), Math.Min(region.Gz1, z0 + Quads));
            if (qx1 <= qx0 || qz1 <= qz0)
                continue;
            var (u0, v0, u1, v1) = ((qx0 - region.Gx0) * sub, (qz0 - region.Gz0) * sub, (qx1 - region.Gx0) * sub, (qz1 - region.Gz0) * sub);

            vertices ??= [];
            indices ??= [];
            var first = (uint)vertices.Count;
            var columns = u1 - u0 + 1;
            var fineSpacing = spacing / sub;
            var grounds = new Dictionary<(int, int), (float4 Cover, float4 Materials, float Ballast)>();
            (float4 Cover, float4 Materials, float Ballast) GroundAt(int gx, int gz)
            {
                if (!grounds.TryGetValue((gx, gz), out var g))
                {
                    var (c, m) = _source.Ground(gx, gz, spacing);
                    grounds[(gx, gz)] = g = (c, m, _source.BallastAt(gx, gz));
                }

                return g;
            }

            // (Each row's heights, and the rows either side, worked out once.)
            var row = new float[columns];
            var above = new float[columns];
            var below = new float[columns];
            for (var v = v0; v <= v1; v++)
            {
                for (var u = u0; u <= u1; u++)
                    (row[u - u0], above[u - u0], below[u - u0]) = (Height(u, v), Height(u, v - 1), Height(u, v + 1));

                for (var u = u0; u <= u1; u++)
                {
                    var h = row[u - u0];
                    var left = u > u0 ? row[u - u0 - 1] : Height(u - 1, v);
                    var right = u < u1 ? row[u - u0 + 1] : Height(u + 1, v);
                    var normal = Normalize(new float3(left - right, 2f * fineSpacing, above[u - u0] - below[u - u0]));
                    var (gx, tx) = Math.DivRem(u, sub);
                    var (gz, tz) = Math.DivRem(v, sub);
                    var (fx, fz) = (tx / (float)sub, tz / (float)sub);
                    var (x, z) = (region.Gx0 + gx, region.Gz0 + gz);
                    var (a, b, c, d) = (GroundAt(x, z), GroundAt(x + 1, z), GroundAt(x, z + 1), GroundAt(x + 1, z + 1));
                    var (wa, wb, wc, wd) = ((1f - fx) * (1f - fz), fx * (1f - fz), (1f - fx) * fz, fx * fz);
                    var position = new float3((x - x0 + fx) * spacing, h, (z - z0 + fz) * spacing);
                    vertices.Add(new TerrainVertex(position, normal,
                        a.Cover * wa + b.Cover * wb + c.Cover * wc + d.Cover * wd,
                        a.Materials * wa + b.Materials * wb + c.Materials * wc + d.Materials * wd,
                        a.Ballast * wa + b.Ballast * wb + c.Ballast * wc + d.Ballast * wd));
                }
            }

            // Counter-clockwise seen from above, as Triangulate's.
            for (var v = 0; v < v1 - v0; v++)
                for (var u = 0; u < u1 - u0; u++)
                {
                    var i = first + (uint)(v * columns + u);
                    var (right, down) = (i + 1, i + (uint)columns);
                    indices.AddRange([i, down, right, right, down, down + 1]);
                }
        }

        return (vertices?.ToArray(), indices?.ToArray());
    }

    /// How much further one way across a quad has to climb than the other, per metre of vertex spacing, before the quad's
    /// split is turned to the gentler one.
    public const float FlipGain = 0.5f;

    /// Fewer quads than this needing it isn't worth the patch its own index list.
    public const int FewestFlips = 16;

    /// Which quads to split along their gentler diagonal. Split the same way everywhere, a steep slope running across the
    /// diagonals comes out as a sawtooth. Null when no quad needs it, so the patch can share the common index list.
    private static bool[]? Diagonals(TerrainVertex[] vertices, float spacing)
    {
        const int width = Quads + 1;
        bool[]? flips = null;
        var count = 0;
        for (var z = 0; z < Quads; z++)
            for (var x = 0; x < Quads; x++)
            {
                var (a, b, c, d) = (vertices[z * width + x].Position.Y, vertices[z * width + x + 1].Position.Y, vertices[(z + 1) * width + x].Position.Y, vertices[(z + 1) * width + x + 1].Position.Y);
                if (MathF.Abs(b - c) - MathF.Abs(a - d) <= FlipGain * spacing)
                    continue;
                flips ??= new bool[Quads * Quads];
                flips[z * Quads + x] = true;
                count++;
            }

        return count >= FewestFlips ? flips : null;
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

    /// The patch's index list for its neighbours this frame: shared by every whole patch with the same edge ratios, or
    /// its own when some of its vertices are off the route. Made when first needed and uploaded with the frame.
    private (BufferEx Buffer, int Count) Indices(Patch patch)
    {
        var ratios = Ratios(patch.Key);
        var cache = patch.Valid is null && patch.Flips is null ? _sharedIndices : patch.OwnIndices ??= [];
        if (cache.TryGetValue(ratios, out var indices))
            return indices;

        var list = Triangulate(ratios, patch.Valid, patch.Flips);
        indices = list.Length > 0 ? (Buffers.UploadLater(MemoryMarshal.AsBytes(list), VkBufferUsageFlags.IndexBufferBit, "terrain.indices"), list.Length) : (default, 0);
        cache[ratios] = indices;
        return indices;
    }

    /// Room for any stitching: the full grid, plus the fans along all four edges.
    private readonly uint[] _triangles = new uint[Quads * Quads * 6 + 4 * (Quads + 1) * 6];

    /// The triangles of a 64 x 64 grid with its edges stitched to the given ratios, skipping any that touch an invalid
    /// vertex, written into _triangles (main thread). Counter-clockwise seen from above.
    private ReadOnlySpan<uint> Triangulate(int ratios, bool[]? valid, bool[]? flips)
    {
        const int width = Quads + 1;
        var indices = _triangles;
        var count = 0;
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
            indices[count++] = At(ax, az);
            indices[count++] = turn < 0 ? At(bx, bz) : At(cx, cz);
            indices[count++] = turn < 0 ? At(cx, cz) : At(bx, bz);
        }

        var x0 = west > 1 ? 1 : 0;
        var x1 = east > 1 ? Quads - 1 : Quads;
        var z0 = north > 1 ? 1 : 0;
        var z1 = south > 1 ? Quads - 1 : Quads;

        for (var z = z0; z < z1; z++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (flips is not null && flips[z * Quads + x])
                {
                    Triangle(x, z, x, z + 1, x + 1, z + 1);
                    Triangle(x, z, x + 1, z + 1, x + 1, z);
                }
                else
                {
                    Triangle(x, z, x, z + 1, x + 1, z);
                    Triangle(x + 1, z, x, z + 1, x + 1, z + 1);
                }
            }
        }

        // DeathFromAbove's zip: walk the coarse vertices along the edge (every ratio-th one) and the grid row one in
        // together, advancing whichever side's next segment is centred first.
        void Stitch(int ratio, Func<int, int, (int X, int Z)> place, int innerFrom, int innerTo)
        {
            var edges = Quads / ratio + 1;
            var i = 0;
            var j = innerFrom;
            while (i < edges - 1 || j < innerTo)
            {
                var advanceEdge = j >= innerTo || (i < edges - 1 && ratio * i + ratio * (i + 1) <= j + j + 1);
                var (ex, ez) = place(ratio * i, 0);
                var (ix, iz) = place(j, 1);

                if (advanceEdge)
                {
                    var (nx, nz) = place(ratio * (i + 1), 0);
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

        return indices.AsSpan(0, count);
    }

    // --- Drawing. ---

    /// How many of the chosen patches the last Draw drew, the rest being out of view.
    public int PatchesInView { get; private set; }

    /// Draws the chosen patches in view. The pipeline is bound; push writes each patch's origin relative to the camera.
    public void Draw(CommandBuffer cmd, double cx, double cy, double cz, ViewCone view, Action<float3> push)
    {
        PatchesInView = 0;
        foreach (var (patch, indices, count) in _drawList)
        {
            var offset = new float3((float)(patch.Origin.X - cx), (float)-cy, (float)(patch.Origin.Z - cz));
            if (!view.Sees(offset + patch.Centre, patch.Radius + 2f))
                continue;

            PatchesInView++;
            push(offset);
            patch.Mesh.DrawWith(cmd, indices, count);
            if (patch.Insert is { } insert)
                insert.DrawWith(cmd, patch.InsertIndices, patch.InsertCount);
        }
    }

    /// Draws the patches that pass the test (a patch's middle relative to the camera, and its radius), for a shadow.
    /// What DrawCasters would draw for this test, as one number that changes whenever it would draw anything differently (a
    /// patch built again, a level of detail changed): shadows cached from the ground are drawn again when it changes.
    public int CasterSignature(double cx, double cy, double cz, Func<float3, float, bool> reaches)
    {
        var hash = new HashCode();
        foreach (var (patch, indices, count) in _drawList)
        {
            var offset = new float3((float)(patch.Origin.X - cx), (float)-cy, (float)(patch.Origin.Z - cz));
            if (!reaches(offset + patch.Centre, patch.Radius))
                continue;
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(patch.Mesh));
            hash.Add(indices.VkBuffer);
            hash.Add(count);
            if (patch.Insert is { } insert)
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(insert));
        }

        return hash.ToHashCode();
    }

    public void DrawCasters(CommandBuffer cmd, double cx, double cy, double cz, Func<float3, float, bool> reaches, Action<float3> push)
    {
        foreach (var (patch, indices, count) in _drawList)
        {
            var offset = new float3((float)(patch.Origin.X - cx), (float)-cy, (float)(patch.Origin.Z - cz));
            if (!reaches(offset + patch.Centre, patch.Radius))
                continue;

            push(offset);
            patch.Mesh.DrawWith(cmd, indices, count);
            if (patch.Insert is { } insert)
                insert.DrawWith(cmd, patch.InsertIndices, patch.InsertCount);
        }
    }

    private static (float3 Centre, float Radius) Bounds(TerrainVertex[] vertices)
    {
        var min = new float3(float.MaxValue);
        var max = new float3(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = float3.Min(min, vertex.Position);
            max = float3.Max(max, vertex.Position);
        }

        return ((min + max) * 0.5f, RailWerkz.Core.Utils.MathEx.Length(max - min) * 0.5f);
    }

    private static void Release(BufferEx buffer)
    {
        if (!buffer.IsNull())
            Gpu.Defer(buffer.Dispose);
    }

    public void Dispose()
    {
        _jobs.Dispose();

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
