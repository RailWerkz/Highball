using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using RailWerkz.Core.Time;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// One plant or rock: where it stands relative to its cell, its size, its colour, and a random number. Each cell's are sorted by
/// that number, biggest first, so that drawing fewer of them leaves out the same ones plant.vert has already shrunk away.
[StructLayout(LayoutKind.Sequential)]
public struct PlantInstance(float4 placeAndScale, float4 tintAndRandom)
{
    public float4 PlaceAndScale = placeAndScale;
    public float4 TintAndRandom = tintAndRandom;

    public static readonly VertexAttribute[] Layout = [new(3, 4, 0), new(4, 4, 16)];
}

/// What the world has growing and lying on it, worked out from the land cover round the camera as it moves: grass and pebbles close
/// to, bushes, boulders and trees further out, in cells built on background threads. All of it is the same wherever it's seen from,
/// since each cell is seeded by its place. It keeps off track and roads.
///
/// The plants are two models: Grass.glb's clump of blades and EarthTrees.glb's fifteen trees and bushes, each with its levels of
/// detail (full and reduced trunk and leaves, then a few crossed cards of the whole tree for the far distance), all textured from
/// Terrain/GroundClutter. Every level of a plant draws the same instances; plant.vert collapses each outside its level's distances.
public sealed unsafe class Vegetation : IDisposable
{
    private const float NearCell = 32f;
    private const float NearReach = 110f;
    private const float FarCell = 128f;
    private const float FarReach = 1500f;
    private const int UploadsPerFrame = 6;

    private const string GrassFile = "Vegetation/Grass.glb";
    private const string TreeFile = "Vegetation/EarthTrees.glb";
    private const string Clutter = "Terrain/GroundClutter/";

    // What a cell's instances are of: each slot is one plant or stone, every level of detail of it drawing the same instances.
    private const int Grass = 0;
    private const int Pebble = 1;
    private const int Boulder = 2;

    /// EarthTrees' last three (TreeType13 to 15): knee-high bushes.
    private const int FirstShrub = 3;
    private const int ShrubCount = 3;

    /// EarthTrees' TreeType1 to 12: three forest giants of 30 m, six of 10 to 18 m and three of about 4 m.
    private const int FirstTree = FirstShrub + ShrubCount;
    private const int TreeCount = 12;
    private const int SlotCount = FirstTree + TreeCount;

    /// How many of each tree to try for in a far cell (128 m square, where forest is thick): the giants rarest.
    private static readonly int[] TreeAttempts = [2, 2, 2, 5, 5, 5, 5, 5, 5, 5, 5, 5];

    /// Where a plant's detail changes, in metres from the camera (each instance's own up to a tenth either way): from its full model
    /// to its reduced one, then to its cards (the grass, its fewest-bladed clump).
    private static readonly (float Reduced, float Far) TreeLevels = (55f, 300f);
    private static readonly (float Reduced, float Far) ShrubLevels = (30f, 120f);
    private static readonly (float Reduced, float Far) GrassLevels = (10f, 35f);

    /// One draw of a slot's instances: a range of a mesh with its textures, drawn from Near to Far metres from the camera.
    private sealed class Kind
    {
        public required Mesh Mesh;
        public required TextureBinding Textures;
        public required int Slot;
        public int FirstIndex;
        public int IndexCount = -1;
        public float Near;
        public float Far = float.PositiveInfinity;
        public float Wind;
        public float SoftNormal;
        public float Fade;

        /// Where its cut-out comes from: 0 its colour map's alpha, 1 a map of its own, 2 none (a solid thing).
        public float Cutout = 1f;

        /// The last shadow cascade it's drawn into; below 0, none.
        public int LastCascade;

        /// Drawn instead of Mesh into the far shadow cascades (from FarCasterFrom out, past 250 m, where a texel is most of a
        /// metre): a solid shape the size of the thing. Null draws Mesh there.
        public Mesh? FarCaster;

        /// Left out of the far cascades, its partner's FarCaster standing for it (a tree's bark, inside its canopy's).
        public bool NoFarCaster;

        public int Count => IndexCount < 0 ? Mesh.IndexCount : IndexCount;
    }

    /// The first shadow cascade things are drawn into as their FarCaster.
    private const int FarCasterFrom = 3;

    /// plant.vert's and shadow_plant.vert's push constants (all 128 bytes of them). Where each draw's cell is comes from Cells, a
    /// table on the GPU, indexed by Base plus the draw's place in its indirect call.
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4x4 Matrix;
        public float4 Shape;

        /// xy: the distances it's drawn from and to.
        public float4 Band;

        /// x: minus the camera's height; y: the flutter's phase; zw: the gusts' phases.
        public float4 Globals;
        public ulong Cells;
        public uint Base;
        private uint _pad;
    }

    /// A cell drawn in this pass: its row in the cell table (where it is, relative to the camera, and its wrapped place),
    /// and its instances of the kind being drawn.
    private readonly record struct Visible(float4 Cell, int First, int Count, float Distance);

    /// The gusts' and flutter's phases now: (gust, gust, flutter). A gust 69 m long passing at 4 m/s, one 37.5 m long at 4.5 m/s.
    private static float3 WindPhases()
    {
        static float Phase(double hertz) => (float)(Time.Now * hertz % 1.0) * MathF.Tau;
        return new float3(Phase(4.0 / 68.9), Phase(4.5 / 37.5), Phase(0.4));
    }

    /// The cell's place wrapped to the 1024 m plant_wind.glsl's waves repeat over; both sizes of cell divide it.
    private static float WrapCell(int cell, float size) => cell * (int)size & 1023;

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly record struct Key(bool Far, int X, int Z);

    private sealed record Built(Key Key, int Generation, int Stamp, PlantInstance[] Instances, int[] Starts, float Low, float High);

    private sealed class Cell
    {
        public required Key Key;

        /// Where its instances start in the shared arena.
        public required int Offset;
        public required int[] Starts;
        public required float Low;
        public required float High;
        public required int Total;
    }

    /// Every cell's instances, in one buffer, so a kind's cells are drawn from one binding in one indirect call.
    private readonly BufferArena _arena = new(Marshal.SizeOf<PlantInstance>(), 1 << 18, VkBufferUsageFlags.VertexBufferBit, "vegetation.instances");
    private readonly FrameRing<VkDrawIndexedIndirectCommand> _commands = new(VkBufferUsageFlags.IndirectBufferBit, "vegetation.commands");
    private readonly FrameRing<float4> _cellTable = new(VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "vegetation.cells");
    private readonly List<Visible> _visible = [];
    private VkDrawIndexedIndirectCommand[] _scratch = new VkDrawIndexedIndirectCommand[256];
    private float4[] _rows = new float4[256];

    private readonly List<Kind> _kinds = [];

    /// How tall each slot's model is at a size of 1, for its cells' bounds.
    private readonly float[] _heights = new float[SlotCount];
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<Key, Cell> _cells = [];
    private readonly HashSet<Key> _inFlight = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly JobStream _jobs = new("Vegetation", 0.3);
    private readonly List<(Key Key, float Distance)> _wanted = [];

    private TerrainSource? _source;
    private NetworkLines? _lines;
    private volatile Dictionary<(int, int), List<(double Ax, double Az, double Bx, double Bz, float Clear)>>? _clearances;
    private volatile Dictionary<(int, int), float>? _trackField;
    private int _generation;
    private int _terrainEpoch = -1;

    /// Cells whose ground or lines have changed, which keep drawing until the rebuild comes in, and the count of changes when each was
    /// marked: one that changed again while being rebuilt stays marked.
    private readonly Dictionary<Key, int> _stale = [];
    private int _stamp;
    private bool _plantsLoaded;

    public bool Enabled { get; set; } = true;
    public int Instances { get; private set; }
    public int DrawnInstances { get; private set; }

    public Vegetation()
    {
        Array.Fill(_heights, 2f);
        var rock = Own(Texture2D.LoadDds("Terrain/Cliffs/Cliffs_Albedo.dds", srgb: true));
        var rocks = Own(new TextureBinding(rock, rock, rock, rock));

        // Pebbles are a few centimetres across, so once split is plenty.
        _kinds.Add(new Kind { Mesh = Own(RockMesh(11, splits: 1)), Textures = rocks, Slot = Pebble, Fade = NearReach * 0.9f, Cutout = 2f, LastCascade = 0 });
        _kinds.Add(new Kind
        {
            Mesh = Own(RockMesh(29)), FarCaster = Own(RockMesh(29, splits: 0)), Textures = rocks, Slot = Boulder, Fade = FarReach * 0.7f, Cutout = 2f,
            LastCascade = 3,
        });
    }

    private T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    // --- Streaming. ---

    /// Brings the cells round the camera up to date. Before the frame's world is recorded.
    public void Update(CommandBuffer cmd, Terrain terrain, Camera camera)
    {
        var source = terrain.Source;
        if (!Enabled)
            return;

        if (!_plantsLoaded)
            LoadPlants();

        // Other ground starts the cells again. New lines or edited ground only mark what they touched, which draws as it was until
        // it's been rebuilt, in the background.
        if (!ReferenceEquals(source, _source))
        {
            _source = source;
            _lines = source.Lines;
            _terrainEpoch = terrain.Epoch;
            _generation++;
            _stale.Clear();
            ClearCells();
            LayOutClearances();
        }
        else if (!ReferenceEquals(source.Lines, _lines))
        {
            _lines = source.Lines;
            _terrainEpoch = terrain.Epoch;
            _stamp++;
            foreach (var key in _cells.Keys)
                _stale[key] = _stamp;
            LayOutClearances();
        }
        else if (terrain.Epoch != _terrainEpoch)
        {
            _stamp++;
            if (terrain.EditsSince(_terrainEpoch) is { } edits)
                foreach (var (x0, z0, x1, z1) in edits)
                    MarkEdited(source.Spacing, x0, z0, x1, z1);
            else
                foreach (var key in _cells.Keys)
                    _stale[key] = _stamp;
            _terrainEpoch = terrain.Epoch;
        }

        for (var n = 0; n < UploadsPerFrame && _built.TryDequeue(out var built); n++)
        {
            _inFlight.Remove(built.Key);
            if (built.Generation != _generation)
                continue;

            if (_stale.TryGetValue(built.Key, out var marked) && marked <= built.Stamp)
                _stale.Remove(built.Key);
            // Uploaded into the arena in this frame's commands, so it draws at once in place of what it replaces.
            if (_cells.Remove(built.Key, out var replaced))
                Release(replaced);
            var offset = _arena.Upload(cmd, MemoryMarshal.AsBytes(built.Instances.AsSpan()), built.Instances.Length);
            _cells[built.Key] = new Cell { Key = built.Key, Offset = offset, Starts = built.Starts, Low = built.Low, High = built.High, Total = built.Instances.Length };
            Instances += built.Instances.Length;
        }

        _arena.EndUploads(cmd);

        Evict(camera);

        if (_clearances is null || _jobs.Busy)
            return;

        _wanted.Clear();
        Want(false, NearCell, NearReach * Quality.PlantReach, camera);
        Want(true, FarCell, FarReach * Quality.PlantReach, camera);
        if (_wanted.Count == 0)
            return;

        _wanted.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var (generation, stamp) = (_generation, _stamp);
        var batch = _wanted.Take(_jobs.BatchSize).Select(w => w.Key).ToList();
        _inFlight.UnionWith(batch);
        _jobs.TryRun(batch.Select(k => ((Brutal.Concurrency.Jobs.IJob)new Job("vegetation cell", () => Build(k, generation, stamp)), 1)));
    }

    /// One worker at a time isn't needed: a layout for lines that have since been replaced is just dropped.
    private void LayOutClearances()
    {
        _clearances = null;
        var lines = _lines;
        Task.Run(() =>
        {
            var grid = BuildClearances(lines);
            var field = BuildTrackField(lines);
            if (!ReferenceEquals(lines, _lines))
                return;
            _trackField = field;
            _clearances = grid;
        });
    }

    private void MarkEdited(float spacing, int x0, int z0, int x1, int z1)
    {
        var (left, right, top, bottom) = (x0 * (double)spacing, x1 * (double)spacing, z0 * (double)spacing, z1 * (double)spacing);
        foreach (var key in _cells.Keys)
        {
            var size = key.Far ? FarCell : NearCell;
            if ((key.X + 1) * (double)size >= left && key.X * (double)size <= right && (key.Z + 1) * (double)size >= top && key.Z * (double)size <= bottom)
                _stale[key] = _stamp;
        }
    }

    private void Want(bool far, float size, float reach, Camera camera)
    {
        var source = _source!;
        var x0 = (int)Math.Floor((camera.X - reach) / size);
        var x1 = (int)Math.Floor((camera.X + reach) / size);
        var z0 = (int)Math.Floor((camera.Z - reach) / size);
        var z1 = (int)Math.Floor((camera.Z + reach) / size);
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var key = new Key(far, x, z);
                if ((_cells.ContainsKey(key) && !_stale.ContainsKey(key)) || _inFlight.Contains(key))
                    continue;

                var dx = (x + 0.5) * size - camera.X;
                var dz = (z + 0.5) * size - camera.Z;
                var distance = (float)Math.Sqrt(dx * dx + dz * dz);
                if (distance > reach + size * 0.71f)
                    continue;

                // Only over ground that's come in, or there's nothing to stand them on.
                var s = source.Spacing;
                var (gx0, gz0) = ((int)Math.Floor(x * size / s), (int)Math.Floor(z * size / s));
                var (gx1, gz1) = ((int)Math.Ceiling((x + 1) * size / s), (int)Math.Ceiling((z + 1) * size / s));
                if (far ? source.AnyBuilt(gx0, gz0, gx1, gz1) : source.FullReady(gx0, gz0, gx1, gz1))
                    _wanted.Add((key, distance));
            }
        }
    }

    private void Evict(Camera camera)
    {
        List<Key>? gone = null;
        foreach (var (key, _) in _cells)
        {
            var (size, reach) = key.Far ? (FarCell, FarReach * Quality.PlantReach) : (NearCell, NearReach * Quality.PlantReach);
            var dx = (key.X + 0.5) * size - camera.X;
            var dz = (key.Z + 0.5) * size - camera.Z;
            if (Math.Sqrt(dx * dx + dz * dz) > reach * 1.25 + size)
                (gone ??= []).Add(key);
        }

        if (gone is null)
            return;
        foreach (var key in gone)
        {
            _stale.Remove(key);
            if (_cells.Remove(key, out var cell))
                Release(cell);
        }
    }

    /// Gives a cell's instances back to the arena, once the GPU's done with them.
    private void Release(Cell cell)
    {
        Instances -= cell.Total;
        _arena.Free(cell.Offset, cell.Total);
    }

    private void ClearCells()
    {
        foreach (var cell in _cells.Values)
            Release(cell);
        _cells.Clear();
    }

    // --- Where things go. ---

    /// How far past the edge of every track and road a point is, by a grid of the segments near each square.
    private static Dictionary<(int, int), List<(double, double, double, double, float)>> BuildClearances(NetworkLines? lines)
    {
        var grid = new Dictionary<(int, int), List<(double, double, double, double, float)>>();
        if (lines is null)
            return grid;

        foreach (var line in lines.Lines)
            for (var i = 0; i + 1 < line.X.Length; i++)
                Segment(line.X[i], line.Z[i], line.X[i + 1], line.Z[i + 1], Math.Max(line.Formation, 2f));

        // A grade crossing's pad, end to end, as wide as its road and shoulders.
        foreach (var pad in lines.Pads)
        {
            var ((ax, az), (bx, bz)) = (pad.Start, pad.End);
            Segment(ax, az, bx, bz, pad.Half + 1.5f);
        }

        return grid;

        void Segment(double ax, double az, double bx, double bz, float clear)
        {
            var spread = (int)Math.Ceiling((clear + TracksideReach) / NearCell);
            var length = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            var steps = Math.Max(1, (int)Math.Ceiling(length / (NearCell * 0.5)));
            (int, int)? last = null;
            for (var s = 0; s <= steps; s++)
            {
                var t = (double)s / steps;
                var cx = (int)Math.Floor((ax + (bx - ax) * t) / NearCell);
                var cz = (int)Math.Floor((az + (bz - az) * t) / NearCell);
                if (last == (cx, cz))
                    continue;
                last = (cx, cz);
                for (var z = cz - spread; z <= cz + spread; z++)
                {
                    for (var x = cx - spread; x <= cx + spread; x++)
                    {
                        if (!grid.TryGetValue((x, z), out var list))
                            grid[(x, z)] = list = [];
                        if (list.Count == 0 || list[^1] != (ax, az, bx, bz, clear))
                            list.Add((ax, az, bx, bz, clear));
                    }
                }
            }
        }
    }

    private const float TracksideReach = 12f;

    /// How far past the nearest track or road's edge a point is, up to TracksideReach.
    private float Clearance(double x, double z)
    {
        var grid = _clearances;
        if (grid is null || !grid.TryGetValue(((int)Math.Floor(x / NearCell), (int)Math.Floor(z / NearCell)), out var list))
            return TracksideReach;

        var best = (double)TracksideReach;
        foreach (var (ax, az, bx, bz, clear) in list)
        {
            var (dx, dz) = (bx - ax, bz - az);
            var lengthSquared = dx * dx + dz * dz;
            var t = lengthSquared > 1e-6 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / lengthSquared, 0.0, 1.0) : 0.0;
            var (px, pz) = (ax + dx * t - x, az + dz * t - z);
            best = Math.Min(best, Math.Sqrt(px * px + pz * pz) - clear);
        }

        return (float)best;
    }

    // --- Trees by the line. ---

    /// Trees and bushes crowd along the railway and thin out away from it: TracksidePeak times what the land cover alone would
    /// grow out to TracksideDense metres from the track's edge, easing down to Background by TracksideFalloff and staying there.
    /// Peak and Background are balanced so about as many grow in view as when they were spread evenly.
    public const float TracksideDense = 110f;
    public const float TracksideFalloff = 320f;
    public const float TracksidePeak = 4.2f;
    public const float Background = 0.15f;

    /// How far a tree's trunk keeps from the track bed's edge (ballast and walkway), and where it's no longer thinned by it.
    public const float TreeClearance = 5f;
    public const float TreeClearanceSoft = 10f;

    /// The track distance field's squares: the distance from a square's centre to the nearest rail line's edge, for squares within
    /// TracksideFalloff of one. Coarse, since the density changes over hundreds of metres; the stems' clearance is exact.
    private const float TrackCell = 32f;

    /// Null for a route without railway, whose trees stay spread evenly.
    private static Dictionary<(int, int), float>? BuildTrackField(NetworkLines? lines)
    {
        if (lines is null)
            return null;

        var field = new Dictionary<(int, int), float>();
        var reach = TracksideFalloff + TrackCell;
        var any = false;
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Embedded)
                continue;
            any = true;
            var edge = Math.Max(line.Formation, 2f);
            for (var i = 0; i + 1 < line.X.Length; i++)
            {
                var (ax, az, bx, bz) = (line.X[i], line.Z[i], line.X[i + 1], line.Z[i + 1]);
                var x0 = (int)Math.Floor((Math.Min(ax, bx) - reach) / TrackCell);
                var x1 = (int)Math.Floor((Math.Max(ax, bx) + reach) / TrackCell);
                var z0 = (int)Math.Floor((Math.Min(az, bz) - reach) / TrackCell);
                var z1 = (int)Math.Floor((Math.Max(az, bz) + reach) / TrackCell);
                var (dx, dz) = (bx - ax, bz - az);
                var lengthSquared = dx * dx + dz * dz;
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var (cx, cz) = ((x + 0.5) * TrackCell, (z + 0.5) * TrackCell);
                        var t = lengthSquared > 1e-6 ? Math.Clamp(((cx - ax) * dx + (cz - az) * dz) / lengthSquared, 0.0, 1.0) : 0.0;
                        var (px, pz) = (ax + dx * t - cx, az + dz * t - cz);
                        var d = (float)(Math.Sqrt(px * px + pz * pz) - edge);
                        if (d > reach)
                            continue;
                        if (!field.TryGetValue((x, z), out var best) || d < best)
                            field[(x, z)] = d;
                    }
                }
            }
        }

        return any ? field : null;
    }

    /// Roughly how far a point is from the nearest rail line's edge, blended between the field's squares; well off, past its reach.
    private static float TrackDistance(Dictionary<(int, int), float> field, double x, double z)
    {
        const float far = TracksideFalloff + TrackCell * 2f;
        var fx = x / TrackCell - 0.5;
        var fz = z / TrackCell - 0.5;
        var (ix, iz) = ((int)Math.Floor(fx), (int)Math.Floor(fz));
        var (tx, tz) = ((float)(fx - ix), (float)(fz - iz));
        float At(int a, int b) => field.TryGetValue((a, b), out var d) ? d : far;
        var top = At(ix, iz) + (At(ix + 1, iz) - At(ix, iz)) * tx;
        var bottom = At(ix, iz + 1) + (At(ix + 1, iz + 1) - At(ix, iz + 1)) * tx;
        return top + (bottom - top) * tz;
    }

    /// How many times the land cover's own density of trees and bushes grows this far from the track.
    private static float TracksideDensity(float distance) =>
        Background + (TracksidePeak - Background) * (1f - MathEx.SmoothStep(TracksideDense, TracksideFalloff, distance));

    private static uint Hash(int a, int b, uint salt)
    {
        var h = (uint)a * 0x8DA6B343u ^ (uint)b * 0xD8163841u ^ salt * 0xCB1AB31Fu;
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        return h ^ (h >> 15);
    }

    private struct Random32(uint seed)
    {
        private uint _state = seed == 0u ? 1u : seed;

        public float Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state & 0xFFFFFF) / 16777216f;
        }
    }

    /// Soft patches of plants and bare ground, the same from any cell.
    private static float Patches(double x, double z, float size)
    {
        var fx = x / size;
        var fz = z / size;
        var (ix, iz) = ((int)Math.Floor(fx), (int)Math.Floor(fz));
        var (tx, tz) = ((float)(fx - ix), (float)(fz - iz));
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        float Corner(int a, int b) => (Hash(a, b, 7u) & 0xFFFF) / 65535f;
        var top = Corner(ix, iz) + (Corner(ix + 1, iz) - Corner(ix, iz)) * tx;
        var bottom = Corner(ix, iz + 1) + (Corner(ix + 1, iz + 1) - Corner(ix, iz + 1)) * tx;
        return top + (bottom - top) * tz;
    }

    private void Build(Key key, int generation, int stamp)
    {
        var size = key.Far ? FarCell : NearCell;
        var perSlot = new List<PlantInstance>[SlotCount];
        for (var i = 0; i < SlotCount; i++)
            perSlot[i] = [];

        var (originX, originZ) = (key.X * (double)size, key.Z * (double)size);
        var source = _source!;
        var low = float.MaxValue;
        var high = float.MinValue;

        // The land cover's costly to read, so it's sampled on a grid and blended.
        var grid = key.Far ? 5 : 3;
        var covers = new float4[grid * grid];
        var materials = new float4[grid * grid];
        for (var j = 0; j < grid; j++)
        {
            for (var i = 0; i < grid; i++)
            {
                var gx = (int)Math.Round((originX + size * i / (grid - 1)) / source.Spacing);
                var gz = (int)Math.Round((originZ + size * j / (grid - 1)) / source.Spacing);
                (covers[j * grid + i], materials[j * grid + i]) = source.Ground(gx, gz, 0f);
            }
        }

        (float4 Cover, float4 Materials) Sample(float u, float v)
        {
            var fx = u * (grid - 1);
            var fz = v * (grid - 1);
            var ix = Math.Min((int)fx, grid - 2);
            var iz = Math.Min((int)fz, grid - 2);
            var (tx, tz) = (fx - ix, fz - iz);
            float4 Mix(float4[] a)
            {
                var top = a[iz * grid + ix] + (a[iz * grid + ix + 1] - a[iz * grid + ix]) * tx;
                var bottom = a[(iz + 1) * grid + ix] + (a[(iz + 1) * grid + ix + 1] - a[(iz + 1) * grid + ix]) * tx;
                return top + (bottom - top) * tz;
            }

            return (Mix(covers), Mix(materials));
        }

        // How thick trees and bushes grow along the track here: the most anywhere in the cell sets how many are tried for, each then
        // kept by its own place's share of that.
        var field = _trackField;
        var cellPeak = 1f;
        if (key.Far && field is not null)
        {
            var nearest = float.MaxValue;
            for (var j = 0; j <= 4; j++)
                for (var i = 0; i <= 4; i++)
                    nearest = MathF.Min(nearest, TrackDistance(field, originX + size * i / 4, originZ + size * j / 4));
            cellPeak = TracksideDensity(nearest - TrackCell);
        }

        void Place(int slot, uint salt, int attempts, Func<float4, float4, float, float, float, float> chance, Func<float, float4, float4, Random32, (float Scale, float3 Tint)> look, float minClear, float sink, float steepest, bool trackside = false)
        {
            var random = new Random32(Hash(key.X, key.Z, salt + (key.Far ? 1000u : 0u)));
            var crowd = trackside && field is not null;
            if (crowd)
                attempts = (int)MathF.Ceiling(attempts * cellPeak);
            for (var n = 0; n < attempts; n++)
            {
                var (u, v, roll, pick) = (random.Next(), random.Next(), random.Next(), random.Next());
                var (x, z) = (originX + u * size, originZ + v * size);
                if (crowd && random.Next() * cellPeak > TracksideDensity(TrackDistance(field!, x, z)))
                    continue;
                var (cover, ground) = Sample(u, v);
                if (ground.Z + ground.W > 0.15f)
                    continue;

                var clear = Clearance(x, z);
                if (clear < minClear)
                    continue;

                var patches = Patches(x, z, key.Far ? 90f : 14f);
                if (roll > chance(cover, ground, patches, clear, pick))
                    continue;

                var fx = x / source.Spacing;
                var fz = z / source.Spacing;
                if (!source.TryHeight((int)Math.Floor(fx), (int)Math.Floor(fz), out _))
                    continue;

                var y = source.HeightAt(x, z);
                var step = source.Spacing;
                var (sx, sz) = (source.HeightAt(x + step, z) - y, source.HeightAt(x, z + step) - y);
                var upness = step / MathF.Sqrt(sx * sx + sz * sz + step * step);
                if (upness < steepest)
                    continue;

                var (scale, tint) = look(pick, cover, ground, random);
                low = MathF.Min(low, y);
                high = MathF.Max(high, y + scale * _heights[slot]);
                perSlot[slot].Add(new PlantInstance(new float4((float)(x - originX), y - sink * scale, (float)(z - originZ), scale), new float4(tint, pick)));
            }
        }

        // Drier and yellower than the ground it grows from: the grass's map is pale, so its colour comes mostly from here.
        static float3 Dry(float4 cover, float pick) =>
            new float3(cover.X * 2.4f + 0.13f, cover.Y * 2.2f + 0.12f, cover.Z * 2.0f + 0.07f) * (0.75f + 0.5f * pick);

        // The trees' and bushes' maps have their own colours: only a little variety from one to the next.
        static float3 Leafy(float pick) => new float3(0.85f + 0.3f * pick, 0.85f + 0.25f * pick, 0.8f + 0.2f * pick);

        if (!key.Far)
        {
            // Clumps a metre and a half to two and a half across, overlapping into a sward where the cover's grassy. (Each covers a
            // few times what the old cards did: many more than this and the overlapping blades cost far more to draw than they add.)
            Place(Grass, 1u, 1600,
                (_, ground, patches, clear, _) => (ground.X * 0.8f + ground.Y * 0.25f) * MathEx.SmoothStep(0.15f, 0.55f, patches) * MathEx.SmoothStep(0f, 1.5f, clear),
                (pick, cover, _, _) => (0.4f + 0.35f * pick, Dry(cover, pick)), 0.2f, 0f, 0.8f);

            // Stones, thickest beside the line where the ground's been worked and ballast has strayed.
            Place(Pebble, 2u, 260,
                (_, ground, patches, clear, _) => (ground.Y * 0.45f + ground.X * 0.08f + (1f - MathEx.SmoothStep(0f, 3.5f, clear)) * 0.8f) * (0.4f + patches),
                (pick, cover, _, _) =>
                {
                    var size2 = 0.05f + 0.22f * pick * pick;
                    var grey = 0.7f + 0.5f * pick;
                    return (size2, new float3(cover.X * 2.4f + 0.22f, cover.Y * 2.2f + 0.2f, cover.Z * 2f + 0.18f) * grey);
                }, 0.05f, 0.2f, 0.65f);
        }
        else
        {
            // Spaced as scrub is: every few metres in shrubland, thinner on bare ground, none on the track's shoulders; thickest
            // along the railway, as the trees are.
            for (var b = 0; b < ShrubCount; b++)
            {
                Place(FirstShrub + b, 3u + (uint)b * 101u, 50,
                    (cover, ground, patches, clear, _) => (ground.X * (0.3f + cover.W * 1.2f) + ground.Y * 0.3f) * MathEx.SmoothStep(0.15f, 0.6f, patches) * MathEx.SmoothStep(0.5f, 3f, clear),
                    (pick, _, _, _) => (0.7f + 0.7f * pick, Leafy(pick)), 1f, 0f, 0.72f, trackside: true);
            }

            Place(Boulder, 4u, 15,
                (_, ground, patches, clear, _) => (ground.Y * 0.5f + ground.X * 0.1f) * MathEx.SmoothStep(0.45f, 0.8f, patches) * MathEx.SmoothStep(1f, 4f, clear),
                (pick, cover, _, _) =>
                {
                    var size2 = 0.3f + 1.6f * pick * pick * pick;
                    return (size2, new float3(cover.X * 2.4f + 0.22f, cover.Y * 2.2f + 0.2f, cover.Z * 2f + 0.18f));
                }, 2f, 0.3f, 0.5f);

            // Trees only where the map says there's forest (the small ones straying a little further out); the desert has none
            // to speak of. Most crowd along the railway, a few scattered further off (see TracksideDensity).
            for (var t = 0; t < TreeCount; t++)
            {
                var small = t >= 9;
                Place(FirstTree + t, 10u + (uint)t, TreeAttempts[t],
                    (cover, _, patches, clear, _) => (small ? MathEx.SmoothStep(0.2f, 0.6f, cover.W) : MathEx.SmoothStep(0.35f, 0.75f, cover.W)) * 0.8f * (0.3f + patches) * MathEx.SmoothStep(TreeClearance, TreeClearanceSoft, clear),
                    (pick, _, _, _) => (0.75f + 0.45f * pick, Leafy(pick)), TreeClearance, 0f, 0.7f, trackside: true);
            }
        }

        // All slots in one buffer, each sorted so the ones furthest fading are first.
        var total = perSlot.Sum(p => p.Count);
        var instances = new PlantInstance[total];
        var starts = new int[SlotCount + 1];
        var at = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            starts[i] = at;
            perSlot[i].Sort((a, b) => b.TintAndRandom.W.CompareTo(a.TintAndRandom.W));
            perSlot[i].CopyTo(instances, at);
            at += perSlot[i].Count;
        }

        starts[SlotCount] = at;
        _built.Enqueue(new Built(key, generation, stamp, instances, starts, total > 0 ? low : 0f, total > 0 ? high : 0f));
    }

    // --- The plants' models and textures. ---

    /// Loads the grass and trees: every level of each into one mesh, each piece moved to stand on its own node's origin, and their
    /// textures from Terrain/GroundClutter.
    private void LoadPlants()
    {
        _plantsLoaded = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var grassParts = Model.LoadParts(GrassFile);
        var treeParts = Model.LoadParts(TreeFile);

        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        // A piece's range of the shared index buffer, and its bounds once moved onto its origin.
        (int First, int Count, float3 Min, float3 Max) Add(Dictionary<string, Model.Part> parts, string name)
        {
            if (!parts.TryGetValue(name, out var part))
                throw new InvalidDataException($"The vegetation models have no {name}");
            var (first, start) = (indices.Count, (uint)vertices.Count);
            var (min, max) = (new float3(float.MaxValue), new float3(float.MinValue));
            foreach (var v in part.Vertices)
            {
                var p = v.Position - part.Origin;
                (min, max) = (float3.Min(min, p), float3.Max(max, p));
                vertices.Add(v with { Position = p });
            }

            foreach (var i in part.Indices)
                indices.Add(start + i);
            return (first, part.Indices.Length, min, max);
        }

        Texture2D Map(string name, bool srgb) => Own(Texture2D.LoadKtx2(Clutter + name, srgb));
        TextureBinding Bind(Texture2D albedo, Texture2D opacity) => Own(new TextureBinding(albedo, opacity, opacity, opacity));

        // The bark has no cut-out: a white map stands in for one.
        var solid = Own(Texture2D.Solid(new byte4(255, 255, 255, 255)));
        var bark = Bind(Map("Trunk_Diffuse.ktx2", true), solid);
        var leaves = Bind(Map("Leaves_Diffuse.ktx2", true), Map("Leaves_Opacity.ktx2", false));
        var grassAlbedo = Own(Texture2D.LoadDds(Clutter + "Grass_Diffuse.dds", srgb: true));
        var grass = Bind(grassAlbedo, Own(Texture2D.LoadDds(Clutter + "Grass_Opacity.dds", srgb: false)));

        var pending = new List<(Kind Kind, int First, int Count)>();

        // The grass: its full clump close to, then fewer and fewer blades. It casts no shadow (its material says so, and it's
        // too fine for the shadow map anyway).
        var grassLevels = new[] { ("Grass_Lod0", 0f, GrassLevels.Reduced), ("Grass_Lod2", GrassLevels.Reduced, GrassLevels.Far), ("Grass_Lod3", GrassLevels.Far, float.PositiveInfinity) };
        foreach (var (name, near, far) in grassLevels)
        {
            var piece = Add(grassParts, name);
            _heights[Grass] = MathF.Max(_heights[Grass], piece.Max.Y);
            pending.Add((new Kind
            {
                Mesh = null!, Textures = grass, Slot = Grass, Near = near, Far = far, Wind = 0.07f, SoftNormal = 0.8f, Fade = NearReach * 0.75f, LastCascade = -1,
            }, piece.First, piece.Count));
        }

        // TreeType1 to 12 are the trees, 13 to 15 the bushes; each has its own cards' maps, Tree0Cards for TreeType1 and so on.
        var canopies = new List<(Kind Kind, float3 Min, float3 Max)>();
        for (var type = 1; type <= TreeCount + ShrubCount; type++)
        {
            var shrub = type > TreeCount;
            var slot = shrub ? FirstShrub + type - TreeCount - 1 : FirstTree + type - 1;
            var (reduced, far) = shrub ? ShrubLevels : TreeLevels;
            var cards = Bind(Map($"Tree{type - 1}Cards_Diffuse.ktx2", true), Map($"Tree{type - 1}Cards_Opacity.ktx2", false));
            var full = Add(treeParts, $"TreeType{type}_Lod0_Leaves");
            var height = MathF.Max(full.Max.Y, 1f);
            _heights[slot] = height;

            // A hundredth of its height at the top in the strongest gust, every level of it alike.
            var wind = 0.01f / height;
            var fade = shrub ? FarReach * 0.6f : FarReach * 0.95f;

            foreach (var (level, near, to) in new[] { (0, 0f, reduced), (2, reduced, far) })
            {
                var trunk = Add(treeParts, $"TreeType{type}_Lod{level}_Trunk");
                var crown = level == 0 ? full : Add(treeParts, $"TreeType{type}_Lod{level}_Leaves");
                var bole = new Kind
                {
                    Mesh = null!, Textures = bark, Slot = slot, Near = near, Far = to, Wind = wind, Fade = fade, LastCascade = 3, NoFarCaster = !shrub,
                };
                var canopy = new Kind { Mesh = null!, Textures = leaves, Slot = slot, Near = near, Far = to, Wind = wind, SoftNormal = 0.35f, Fade = fade, LastCascade = 3 };
                pending.Add((bole, trunk.First, trunk.Count));
                pending.Add((canopy, crown.First, crown.Count));
                if (!shrub)
                    canopies.Add((canopy, crown.Min, crown.Max));
            }

            var card = Add(treeParts, $"TreeType{type}_Lod3_Cards");
            pending.Add((new Kind { Mesh = null!, Textures = cards, Slot = slot, Near = far, Wind = wind, SoftNormal = 0.5f, Fade = fade, LastCascade = 3 }, card.First, card.Count));
        }

        var mesh = Own(Mesh.Create(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices)));
        foreach (var (kind, first, count) in pending)
        {
            (kind.Mesh, kind.FirstIndex, kind.IndexCount) = (mesh, first, count);
            _kinds.Add(kind);
        }

        // (Each canopy's stand-in in the far cascades: its trunk's bare below a quarter of the way up.)
        foreach (var (kind, min, max) in canopies)
            kind.FarCaster = Own(CanopyMesh(new float3(min.X, MathF.Max(min.Y, max.Y * 0.25f), min.Z), max));

        RailWerkz.Core.Logging.Log.Info($"Vegetation: grass and {TreeCount} trees and {ShrubCount} bushes, {_kinds.Count} draws, {indices.Count / 3} triangles, loaded in {watch.ElapsedMilliseconds} ms");
    }

    // --- Drawing. ---

    /// A cell's how-many of a kind are big enough to see at this distance. plant.vert shrinks each to nothing by a distance from its
    /// random number; these are sorted by it, so the rest needn't be drawn at all.
    private static int Showing(int count, float distance, float fade)
    {
        // plant.vert: an instance is gone past fade * mix(0.25, 1, random).
        var needed = (distance / fade - 0.25f) / 0.75f;
        return needed <= 0f ? count : (int)MathF.Ceiling(count * Math.Clamp(1f - needed, 0f, 1f));
    }

    /// Draws everything in view. Inside the world's pass, with the pipeline, sky, shadow and cloud sets already bound.
    /// Draws what's in view in two passes: every kind's depth with depthPipeline (cut out, unshaded), then every kind shaded with
    /// colorPipeline, which tests for equal depth, so overlapping plants are lit once a pixel. The descriptor sets other than
    /// the kinds' textures are bound by the caller.
    public void Draw(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, float aspect, VkPipeline depthPipeline, VkPipeline colorPipeline, int culled = -1)
    {
        DrawnInstances = 0;
        if (!Enabled)
            return;

        if (culled >= 0)
        {
            var gpuPush = new Push { Matrix = camera.JitteredViewProjection(aspect), Globals = Globals(camera) };
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, depthPipeline);
            DrawCulled(cmd, layout, ref gpuPush, culled, -1);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, colorPipeline);
            DrawCulled(cmd, layout, ref gpuPush, culled, -1);
            return;
        }

        var view = camera.View(aspect);
        var push = new Push { Matrix = camera.JitteredViewProjection(aspect), Globals = Globals(camera) };
        while (_gathered.Count < _kinds.Count)
            _gathered.Add([]);

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, depthPipeline);
        for (var k = 0; k < _kinds.Count; k++)
        {
            var kind = _kinds[k];
            _gathered[k].Clear();
            if (!Gather(camera, kind, (centre, radius, _) => view.Sees(centre, radius)))
                continue;

            _gathered[k].AddRange(_visible);
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, kind.Cutout);
            push.Band = Band(kind);
            kind.Textures.Bind(cmd, layout);
            DrawVisible(cmd, layout, ref push, kind.Mesh, kind.FirstIndex, kind.Count);

            foreach (var visible in _visible)
                DrawnInstances += visible.Count;
        }

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, colorPipeline);
        for (var k = 0; k < _kinds.Count; k++)
        {
            if (_gathered[k].Count == 0)
                continue;

            var kind = _kinds[k];
            _visible.Clear();
            _visible.AddRange(_gathered[k]);
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, kind.Cutout);
            push.Band = Band(kind);
            kind.Textures.Bind(cmd, layout);
            DrawVisible(cmd, layout, ref push, kind.Mesh, kind.FirstIndex, kind.Count);
        }
    }

    /// Each kind's cells gathered for the depth pass, drawn again by the shaded one.
    private readonly List<List<Visible>> _gathered = [];

    /// Draws what falls in one shadow cascade, with the pipeline bound.
    public void DrawCasters(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, float4x4 light, int cascade, SunShadows.CasterTest reaches, int culled = -1)
    {
        if (!Enabled)
            return;

        var push = new Push { Matrix = light, Globals = Globals(camera) };
        if (culled >= 0)
        {
            DrawCulled(cmd, layout, ref push, culled, cascade);
            return;
        }

        var last = cascade >= FarCasterFrom;
        foreach (var kind in _kinds)
        {
            if (cascade > kind.LastCascade || (last && kind.NoFarCaster) || !Gather(camera, kind, reaches))
                continue;

            var standIn = last && kind.FarCaster is not null;
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, standIn ? 2f : kind.Cutout);
            push.Band = Band(kind);
            kind.Textures.Bind(cmd, layout);
            if (standIn)
                DrawVisible(cmd, layout, ref push, kind.FarCaster!, 0, kind.FarCaster!.IndexCount);
            else
                DrawVisible(cmd, layout, ref push, kind.Mesh, kind.FirstIndex, kind.Count);
        }
    }

    /// The distances a kind's drawn between, for plant.vert (which can't take an infinity).
    private static float4 Band(Kind kind) => new(kind.Near, float.IsPositiveInfinity(kind.Far) ? 1e9f : kind.Far, 0f, 0f);

    /// What every cell drawn this frame shares: the ground below the camera and the wind's phases.
    private static float4 Globals(Camera camera)
    {
        var phases = WindPhases();
        return new float4(-(float)camera.Y, phases.Z, phases.X, phases.Y);
    }

    /// The cells with any of a kind to draw in this pass (those within its distances, and that keep takes by their sphere relative
    /// to the camera), and how many of it each shows at its distance, into _visible. False if there are none.
    private bool Gather(Camera camera, Kind kind, SunShadows.CasterTest keep)
    {
        _visible.Clear();
        var slot = kind.Slot;
        foreach (var cell in _cells.Values)
        {
            var count = cell.Starts[slot + 1] - cell.Starts[slot];
            if (count == 0)
                continue;

            var size = cell.Key.Far ? FarCell : NearCell;
            var (ox, oz) = (cell.Key.X * (double)size - camera.X, cell.Key.Z * (double)size - camera.Z);

            // Its nearest and furthest from the camera, against the kind's distances (which each instance moves by up to a tenth).
            var (lowY, highY) = (cell.Low - (float)camera.Y, cell.High - (float)camera.Y);
            var nx = MathF.Max(MathF.Max((float)-ox - size, (float)ox), 0f);
            var nz = MathF.Max(MathF.Max((float)-oz - size, (float)oz), 0f);
            var ny = MathF.Max(MathF.Max(lowY, -highY), 0f);
            var nearest = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nearest >= kind.Far * 1.1f)
                continue;
            var fx = MathF.Max(MathF.Abs((float)ox), MathF.Abs((float)ox + size));
            var fz = MathF.Max(MathF.Abs((float)oz), MathF.Abs((float)oz + size));
            var fy = MathF.Max(MathF.Abs(lowY), MathF.Abs(highY));
            if (MathF.Sqrt(fx * fx + fy * fy + fz * fz) < kind.Near * 0.9f)
                continue;

            var centre = new float3((float)ox + size * 0.5f, (lowY + highY) * 0.5f, (float)oz + size * 0.5f);
            var radius = MathF.Sqrt(size * size * 0.5f + (highY - lowY) * (highY - lowY) * 0.25f) + 12f;
            // (The top: no plant stands taller than SunShadows.TallestCaster over the cell's highest ground.)
            if (!keep(centre, radius, highY + SunShadows.TallestCaster))
                continue;

            var showing = Showing(count, nearest, kind.Fade * Quality.PlantReach);
            if (showing == 0)
                continue;
            _visible.Add(new Visible(new float4((float)ox, (float)oz, WrapCell(cell.Key.X, size), WrapCell(cell.Key.Z, size)), cell.Offset + cell.Starts[slot], showing, nearest));
        }

        // Nearest first: what's drawn first fills the depth that hides what's behind it, so less is shaded only to be covered.
        _visible.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return _visible.Count > 0;
    }

    /// The gathered cells' instances of a range of mesh's index buffer, in one indirect call: a command and a cell-table row
    /// for each, written for this frame. Drawn one at a time where the GPU can't make many draws from one call.
    private void DrawVisible(CommandBuffer cmd, VkPipelineLayout layout, ref Push push, Mesh mesh, int firstIndex, int indexCount)
    {
        var n = _visible.Count;
        if (_scratch.Length < n)
        {
            _scratch = new VkDrawIndexedIndirectCommand[Math.Max(n, _scratch.Length * 2)];
            _rows = new float4[_scratch.Length];
        }

        long triangles = 0;
        for (var i = 0; i < n; i++)
        {
            var visible = _visible[i];
            _scratch[i] = new VkDrawIndexedIndirectCommand
            {
                IndexCount = indexCount, InstanceCount = visible.Count, FirstIndex = firstIndex, VertexOffset = 0, FirstInstance = visible.First,
            };
            _rows[i] = visible.Cell;
            triangles += indexCount / 3 * (long)visible.Count;
        }

        var (commands, first) = _commands.Append(_scratch.AsSpan(0, n));
        var (table, row) = _cellTable.Append(_rows.AsSpan(0, n));
        push.Cells = Raw(table.GetDeviceAddress());
        const int stride = 20; // a VkDrawIndexedIndirectCommand
        if (Gpu.MultiDraw)
        {
            push.Base = (uint)row;
            cmd.PushConstants(layout, Stages, ByteSize.Zero, push);
            mesh.DrawIndexedIndirect(cmd, _arena.Buffer, commands, (long)first * stride, n, triangles);
            return;
        }

        for (var i = 0; i < n; i++)
        {
            push.Base = (uint)(row + i);
            cmd.PushConstants(layout, Stages, ByteSize.Zero, push);
            mesh.DrawIndexedIndirect(cmd, _arena.Buffer, commands, (long)(first + i) * stride, 1, indexCount / 3 * (long)_visible[i].Count);
        }
    }

    // --- Culling on the GPU. ---

    /// How many slot starts plant_cull.comp's CellData holds (its Slots): must be SlotCount + 1.
    private const int CullSlotStarts = 19;

    /// A buffer's address on the GPU as a plain number, to offset and push.
    private static ulong Raw(VkDeviceAddress address) => System.Runtime.CompilerServices.Unsafe.As<VkDeviceAddress, ulong>(ref address);

    /// A cell as plant_cull.comp reads it.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CellData
    {
        public int X, Z, Size, Offset;
        public float Low, High;
        public fixed int Starts[SlotCount + 1];
    }

    /// A kind as plant_cull.comp reads it, for one pass.
    [StructLayout(LayoutKind.Sequential)]
    private struct KindData
    {
        public float Near, Far, Fade;
        public int Slot;
        public int IndexCount, FirstIndex, Enabled, Pad;
    }

    /// One pass's view, as plant_cull.comp reads it (PassData there).
    [StructLayout(LayoutKind.Sequential)]
    public struct CullView
    {
        public float4 Plane0, Plane1, Plane2, Plane3, Plane4, Plane5;
        public float4 Box;
        public float4 Right;
        public float4 Up;
        public float4 Sun;
        public float4 Reach;
        public int CameraX, CameraZ, Pad0, Pad1;
        public float4 Camera;

        public void SetPlanes(ReadOnlySpan<(float3 N, float D)> planes)
        {
            Plane0 = new float4(planes[0].N, planes[0].D);
            Plane1 = new float4(planes[1].N, planes[1].D);
            Plane2 = new float4(planes[2].N, planes[2].D);
            Plane3 = new float4(planes[3].N, planes[3].D);
            Plane4 = new float4(planes[4].N, planes[4].D);
            Plane5 = new float4(planes[5].N, planes[5].D);
        }

        public void SetCamera(Camera camera)
        {
            var (x, z) = (Math.Floor(camera.X), Math.Floor(camera.Z));
            (CameraX, CameraZ) = ((int)x, (int)z);
            Camera = new float4((float)(camera.X - x), (float)(camera.Z - z), (float)camera.Y, 0f);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CullPush
    {
        public ulong Cells, Kinds, Pass, Commands, Rows, Counts;
        public uint CellCount, KindCount;
        public float Tallest, Slack;
    }

    /// A pass culled this frame: where its commands, rows and counts are, and which kinds it draws (and as their stand-ins).
    private readonly record struct Culled(BufferEx Buffer, long Commands, long Rows, long Counts, int CellCount, bool[] Enabled, bool[] StandIn);

    private readonly GpuRing _cullRing = new(VkBufferUsageFlags.IndirectBufferBit, "vegetation.culled");
    private readonly FrameRing<CellData> _cellRing = new(VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "vegetation.cellData");
    private readonly FrameRing<KindData> _kindRing = new(VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "vegetation.kindData");
    private readonly FrameRing<CullView> _passRing = new(VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "vegetation.passes");
    private readonly List<Culled> _culled = [];
    private CellData[] _cellData = [];
    private KindData[] _kindData = [];
    private long _cellsFrame = -1;
    private ulong _cellsAddress;
    private int _cellCount;
    private VkPipelineLayout _cullLayout;
    private VkPipeline _cullPipeline;

    /// Whether passes are culled on the GPU (Cull) rather than by Gather.
    public bool CullsOnGpu => Gpu.IndirectCount && Enabled;

    /// Every cell, uploaded once a frame for the passes to cull.
    private unsafe void UploadCells()
    {
        if (_cellsFrame == Gpu.FrameNumber)
            return;
        _cellsFrame = Gpu.FrameNumber;
        if (_culled.Count > 0)
            _culled.Clear();

        if (_cellData.Length < _cells.Count)
            _cellData = new CellData[Math.Max(_cells.Count, _cellData.Length * 2)];
        var n = 0;
        foreach (var cell in _cells.Values)
        {
            ref var data = ref _cellData[n++];
            var size = cell.Key.Far ? (int)FarCell : (int)NearCell;
            (data.X, data.Z, data.Size, data.Offset, data.Low, data.High) = (cell.Key.X, cell.Key.Z, size, cell.Offset, cell.Low, cell.High);
            for (var s = 0; s <= SlotCount; s++)
                data.Starts[s] = cell.Starts[s];
        }

        _cellCount = n;
        var (buffer, first) = _cellRing.Append(_cellData.AsSpan(0, n));
        _cellsAddress = Raw(buffer.GetDeviceAddress()) + (ulong)(first * sizeof(CellData));
    }

    private long _cameraFrame = -1;
    private int _cameraPass = -1;

    /// The camera's own view as plant_cull.comp tests it.
    private static CullView CameraView(Camera camera, float aspect)
    {
        var (forward, right, up) = (camera.Forward, camera.Right, camera.Up);
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        var view = new CullView { Box = new float4(0f, 0f, 0f, -1f), Reach = new float4(0f, -1e30f, 0f, 0f) };
        view.SetPlanes(
        [
            (MathEx.Normalize(right - forward * tanX), 0f),
            (MathEx.Normalize(-right - forward * tanX), 0f),
            (MathEx.Normalize(up - forward * tanY), 0f),
            (MathEx.Normalize(-up - forward * tanY), 0f),
            (-forward, 0f),
            (forward, -1e9f),
        ]);
        view.SetCamera(camera);
        return view;
    }

    /// The camera's own view, culled on the GPU: what Draw's culled takes. Outside any render pass. Culled already this frame along
    /// with the shadow cascades (CullAll) where those were drawn. -1 when culling's on the CPU.
    public int CullCamera(CommandBuffer cmd, Camera camera, float aspect)
    {
        if (!CullsOnGpu)
            return -1;
        if (_cameraFrame == Gpu.FrameNumber)
            return _cameraPass;
        return CullAll(cmd, camera, aspect, [])[0];
    }

    /// Every pass this frame culled at once: the given shadow cascades' casters (each view's planes and reach set as
    /// SunShadows.DrawCasters' Reaches tests) and, after them, the camera's own view. One dispatch and one wait, rather than the
    /// GPU stopping between cascades for each. Returns each cascade's pass for DrawCasters' culled, then the camera's for Draw's
    /// (-1 each when culling's on the CPU). Outside any render pass.
    public int[] CullAll(CommandBuffer cmd, Camera camera, float aspect, IReadOnlyList<(int Cascade, CullView View)> cascades)
    {
        var result = new int[cascades.Count + 1];
        Array.Fill(result, -1);
        if (!CullsOnGpu)
            return result;

        var views = new CullView[cascades.Count + 1];
        var which = new int[cascades.Count + 1];
        for (var i = 0; i < cascades.Count; i++)
        {
            views[i] = cascades[i].View;
            views[i].SetCamera(camera);
            which[i] = cascades[i].Cascade;
        }

        views[^1] = CameraView(camera, aspect);
        which[^1] = -1;
        var first = Cull(cmd, views, which);
        if (first >= 0)
            for (var i = 0; i < result.Length; i++)
                result[i] = first + i;
        _cameraFrame = Gpu.FrameNumber;
        _cameraPass = result[^1];
        return result;
    }

    /// Culls the passes (cascade below 0: the camera's view) in one dispatch, one row of workgroups each; returns the first's index
    /// in _culled, the rest following.
    private unsafe int Cull(CommandBuffer cmd, CullView[] views, int[] cascades)
    {
        UploadCells();
        var n = _cellCount;
        var k = _kinds.Count;
        var passes = views.Length;
        if (n == 0 || k == 0)
            return -1;

        if (_cullPipeline.IsNull())
        {
            if (SlotCount + 1 != CullSlotStarts)
                throw new InvalidOperationException($"plant_cull.comp reads {CullSlotStarts} slot starts a cell; Vegetation has {SlotCount + 1}");
            _cullLayout = Pipelines.ComputeLayout(sizeof(CullPush));
            using var shader = Shader.Load("plant_cull.comp");
            _cullPipeline = Pipelines.CreateCompute(shader, _cullLayout);
        }

        if (_kindData.Length < k * passes)
            _kindData = new KindData[k * passes];
        var enabled = new bool[passes][];
        var standIn = new bool[passes][];
        for (var p = 0; p < passes; p++)
        {
            var cascade = cascades[p];
            var last = cascade >= FarCasterFrom;
            enabled[p] = new bool[k];
            standIn[p] = new bool[k];
            for (var i = 0; i < k; i++)
            {
                var kind = _kinds[i];
                var on = cascade < 0 || !(cascade > kind.LastCascade || (last && kind.NoFarCaster));
                var stand = cascade >= 0 && last && kind.FarCaster is not null;
                (enabled[p][i], standIn[p][i]) = (on, stand);
                _kindData[p * k + i] = new KindData
                {
                    Near = kind.Near, Far = float.IsPositiveInfinity(kind.Far) ? 1e9f : kind.Far, Fade = kind.Fade * Quality.PlantReach, Slot = kind.Slot,
                    IndexCount = stand ? kind.FarCaster!.IndexCount : kind.Count, FirstIndex = stand ? 0 : kind.FirstIndex, Enabled = on ? 1 : 0,
                };
            }
        }

        var (kinds, kindFirst) = _kindRing.Append(_kindData.AsSpan(0, k * passes));
        var (pass, passFirst) = _passRing.Append(views);
        const int commandSize = 20;
        var perPass = (long)k * n;
        var commandBytes = passes * perPass * commandSize;
        var rowBytes = passes * perPass * 16;
        var countBytes = passes * k * 4L;
        var (buffer, start) = _cullRing.Reserve(commandBytes + rowBytes + countBytes + 512);
        var commands = start;
        var rows = (commands + commandBytes + 255) / 256 * 256;
        var counts = (rows + rowBytes + 255) / 256 * 256;

        cmd.FillBuffer(buffer.VkBuffer, new ByteSize64((ulong)counts), new ByteSize64((ulong)countBytes), 0);
        var filled = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [filled], [], []);

        var address = Raw(buffer.GetDeviceAddress());
        var push = new CullPush
        {
            Cells = _cellsAddress,
            Kinds = Raw(kinds.GetDeviceAddress()) + (ulong)(kindFirst * sizeof(KindData)),
            Pass = Raw(pass.GetDeviceAddress()) + (ulong)(passFirst * sizeof(CullView)),
            Commands = address + (ulong)commands,
            Rows = address + (ulong)rows,
            Counts = address + (ulong)counts,
            CellCount = (uint)n,
            KindCount = (uint)k,
            Tallest = SunShadows.TallestCaster,
            Slack = SunShadows.CullSlack,
        };
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _cullPipeline);
        cmd.PushConstants(_cullLayout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.Dispatch((n + 63) / 64, passes, 1);

        var written = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.IndirectCommandReadBit | VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.DrawIndirectBit | VkPipelineStageFlags.VertexShaderBit,
            VkDependencyFlags.None, [written], [], []);

        var firstIndex = _culled.Count;
        for (var p = 0; p < passes; p++)
            _culled.Add(new Culled(buffer, commands + p * perPass * commandSize, rows + p * perPass * 16, counts + p * k * 4L, n, enabled[p], standIn[p]));
        return firstIndex;
    }

    /// Draws a culled pass's kinds, with the pipeline bound: each kind one call of as many commands as the GPU kept.
    private void DrawCulled(CommandBuffer cmd, VkPipelineLayout layout, ref Push push, int index, int cascade)
    {
        var culled = _culled[index];
        var n = culled.CellCount;
        const int commandSize = 20;
        push.Cells = Raw(culled.Buffer.GetDeviceAddress()) + (ulong)culled.Rows;
        for (var k = 0; k < _kinds.Count; k++)
        {
            if (!culled.Enabled[k])
                continue;
            var kind = _kinds[k];
            var standIn = culled.StandIn[k];
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, standIn ? 2f : kind.Cutout);
            push.Band = Band(kind);
            push.Base = (uint)(k * n);
            kind.Textures.Bind(cmd, layout);
            cmd.PushConstants(layout, Stages, ByteSize.Zero, push);
            var mesh = standIn ? kind.FarCaster! : kind.Mesh;
            mesh.DrawIndexedIndirectCount(cmd, _arena.Buffer, culled.Buffer, culled.Commands + (long)k * n * commandSize, culled.Buffer, culled.Counts + k * 4L, n);
        }
    }


    // --- Meshes made here. ---

    /// A lumpy stone, flat underneath: an icosahedron split splits times (80 triangles once, 320 twice) and pushed about by noise.
    private static Mesh RockMesh(int seed, int splits = 2)
    {
        var (points, faces) = Icosphere(splits);
        var positions = new float3[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var d = MathEx.Normalize(points[i]);
            var s = (float)seed;
            var bump = 0.72f + 0.4f * (MathF.Sin(d.X * 3.1f + s) * MathF.Cos(d.Y * 2.7f + s * 0.7f) * 0.5f + 0.5f) + 0.14f * MathF.Sin(d.Z * 7.3f + d.X * 5.1f + s);
            var p = d * bump;
            positions[i] = new float3(p.X * 1.1f, MathF.Max(p.Y * 0.75f, -0.15f) + 0.15f, p.Z * 0.9f);
        }

        return SmoothMesh(positions, faces);
    }

    /// How much smaller than its leaves' box a canopy's stand-in is: the leaves are cut out and let light through, the stand-in's
    /// solid, so one the full size would cast too big and too dark a shadow.
    private const float CanopyFill = 0.8f;

    /// A tree's canopy as one solid lump filling its leaves' box (model space), for the far shadow cascade.
    private static Mesh CanopyMesh(float3 min, float3 max)
    {
        var (points, faces) = Icosphere(1);
        var centre = (min + max) * 0.5f;
        var half = (max - min) * (0.5f * CanopyFill);
        var positions = new float3[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var d = MathEx.Normalize(points[i]);
            positions[i] = centre + new float3(d.X * half.X, d.Y * half.Y, d.Z * half.Z);
        }

        return SmoothMesh(positions, faces);
    }

    private static Mesh SmoothMesh(float3[] positions, List<(int A, int B, int C)> faces)
    {
        var normals = new float3[positions.Length];
        foreach (var (a, b, c) in faces)
        {
            var n = MathEx.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }

        var vertices = new Vertex[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            vertices[i] = new Vertex(p, MathEx.Normalize(normals[i]), new float2(p.X + p.Y * 0.6f, p.Z - p.Y * 0.6f));
        }

        var indices = new uint[faces.Count * 3];
        for (var i = 0; i < faces.Count; i++)
            (indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]) = ((uint)faces[i].A, (uint)faces[i].B, (uint)faces[i].C);
        return Mesh.Create(vertices.AsSpan(), indices.AsSpan());
    }

    /// An icosahedron's corners and faces, each face split into four splits times (20, 80, 320 triangles).
    private static (List<float3> Points, List<(int A, int B, int C)> Faces) Icosphere(int splits)
    {
        var t = (1f + MathF.Sqrt(5f)) * 0.5f;
        var points = new List<float3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        var faces = new List<(int A, int B, int C)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };

        for (var level = 0; level < splits; level++)
        {
            var middles = new Dictionary<(int, int), int>();
            int Middle(int a, int b)
            {
                var edge = a < b ? (a, b) : (b, a);
                if (middles.TryGetValue(edge, out var found))
                    return found;
                points.Add((points[a] + points[b]) * 0.5f);
                return middles[edge] = points.Count - 1;
            }

            var next = new List<(int, int, int)>();
            foreach (var (a, b, c) in faces)
            {
                var (ab, bc, ca) = (Middle(a, b), Middle(b, c), Middle(c, a));
                next.AddRange([(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]);
            }

            faces = next;
        }

        return (points, faces);
    }

    public void Dispose()
    {
        _cullRing.Dispose();
        _cellRing.Dispose();
        _kindRing.Dispose();
        _passRing.Dispose();
        if (!_cullPipeline.IsNull())
            Pipelines.Destroy(_cullPipeline, _cullLayout);
        _jobs.Dispose();
        ClearCells();
        _arena.Dispose();
        _commands.Dispose();
        _cellTable.Dispose();
        foreach (var thing in Enumerable.Reverse(_owned))
            thing.Dispose();
    }
}
