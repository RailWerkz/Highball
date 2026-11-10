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
/// to, shrubs, boulders and trees further out, in cells built on background threads. All of it is the same wherever it's seen from,
/// since each cell is seeded by its place. It keeps off track and roads.
public sealed unsafe class Vegetation : IDisposable
{
    private const float NearCell = 32f;
    private const float NearReach = 110f;
    private const float FarCell = 256f;
    private const float FarReach = 1500f;
    private const int UploadsPerFrame = 6;

    /// The tree models, with bark and leaves as two pieces drawn at the same place.
    /// The plants' models: Shapespark's low-poly exterior plants kit (each plant a node, its trunk and branches each a material).
    private const string KitFile = "Vegetation/shapespark_low_poly_exterior_plants_kit.glb";
    /// The kit's trees used, big and small of each of its three. Each loads as its trunk (the first material) and its branches.
    private static readonly string[] TreeNodes = ["Tree-01-1_0", "Tree-01-3_2", "Tree-02-2_5", "Tree-02-4_7", "Tree-03-1_8", "Tree-03-3_10"];

    /// The kit's bush the scrub's drawn with.
    private const string ShrubNode = "Bush-03_15";

    private sealed class Kind
    {
        public Mesh? Mesh;
        public TextureBinding? Textures;
        /// A model's materials, each drawn with its own textures (the kit's plants); null: Mesh whole with Textures.
        public List<RenderModel.Part>? Parts;
        public float Wind;
        public float SoftNormal;
        public float Fade;
        /// Where its cut-out comes from: 0 its colour map's alpha, 1 a map of its own, 2 none (a solid thing).
        public float Cutout;
        public int LastCascade;
        public float3 Shift;
        /// Drawn instead of Mesh into the far shadow cascades (from FarCasterFrom out, past 250 m, where a texel is most of a
        /// metre): a solid shape the size of the thing. Null draws Mesh there.
        public Mesh? FarCaster;
        /// Left out of the far cascades, its partner's FarCaster standing for it (a tree's bark, inside its canopy's).
        public bool NoFarCaster;
    }

    /// The first shadow cascade things are drawn into as their FarCaster.
    private const int FarCasterFrom = 3;

    private const int Grass = 0;
    private const int Pebble = 1;
    private const int Shrub = 2;
    private const int Boulder = 3;
    private const int FirstTree = 4;
    private const int KindCount = FirstTree + 12;

    /// Exactly what plant.vert's push constants hold.
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4x4 Matrix;
        public float4 Offset;
        public float4 Shape;
        public float4 Shift;
        public float4 Wind;
    }

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
        public required BufferEx Buffer;
        public required int[] Starts;
        public required float Low;
        public required float High;
        public required int Total;

        /// Made this frame: its buffer's copy goes in at the top of the next (Buffers.UploadLater), and Update runs after
        /// this frame's went in, so until then it holds whatever was last in that memory. What it replaced draws meanwhile.
        public bool Fresh = true;
        public Cell? Replaced;

        /// What to draw for this cell this frame, if anything.
        public Cell? Drawn => Fresh ? Replaced : this;
    }

    private readonly Kind[] _kinds = new Kind[KindCount];
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<Key, Cell> _cells = [];
    private readonly HashSet<Key> _inFlight = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly JobStream _jobs = new("Vegetation", 0.3);
    private readonly List<(Key Key, float Distance)> _wanted = [];

    private TerrainSource? _source;
    private NetworkLines? _lines;
    private volatile Dictionary<(int, int), List<(double Ax, double Az, double Bx, double Bz, float Clear)>>? _clearances;
    private int _generation;
    private int _terrainEpoch = -1;

    /// Cells whose ground or lines have changed, which keep drawing until the rebuild comes in, and the count of changes when each was
    /// marked: one that changed again while being rebuilt stays marked.
    private readonly Dictionary<Key, int> _stale = [];
    private int _stamp;
    private volatile bool _treesWanted;
    private bool _treesLoaded;

    public bool Enabled { get; set; } = true;
    public int Instances { get; private set; }
    public int DrawnInstances { get; private set; }

    public Vegetation()
    {
        var grassAlbedo = Own(Texture2D.LoadDds("Terrain/GroundClutter/Grass_Diffuse.dds", srgb: true));
        var grassOpacity = Own(Texture2D.LoadDds("Terrain/GroundClutter/Grass_Opacity.dds", srgb: false));
        var rock = Own(Texture2D.LoadDds("Terrain/Cliffs/Cliffs_Albedo.dds", srgb: true));
        var twigs = Own(TwigTexture());

        _kinds[Grass] = new Kind
        {
            Mesh = Own(GrassMesh()), Textures = Own(new TextureBinding(grassAlbedo, grassOpacity, grassOpacity)), Wind = 0.07f, SoftNormal = 0.8f, Fade = NearReach * 0.92f, Cutout = 1f, LastCascade = 0,
        };
        // Pebbles are a few centimetres across, so once split is plenty.
        _kinds[Pebble] = new Kind { Mesh = Own(RockMesh(11, splits: 1)), Textures = Own(new TextureBinding(rock, rock, rock)), Fade = NearReach * 0.9f, Cutout = 2f, LastCascade = 0 };
        _kinds[Shrub] = new Kind
        {
            Mesh = Own(ShrubMesh()), Textures = Own(new TextureBinding(twigs, twigs, twigs)), Wind = 0.02f, SoftNormal = 0.55f, Fade = FarReach * 0.9f, LastCascade = 3,
        };
        _kinds[Boulder] = new Kind
        {
            Mesh = Own(RockMesh(29)), FarCaster = Own(RockMesh(29, splits: 0)), Textures = Own(new TextureBinding(rock, rock, rock)), Fade = FarReach * 0.7f,
            Cutout = 2f, LastCascade = 3,
        };
        for (var i = FirstTree; i < KindCount; i++)
            _kinds[i] = new Kind { Fade = FarReach * 0.95f, LastCascade = 3 };
    }

    private T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    // --- Streaming. ---

    /// Brings the cells round the camera up to date. Before the frame's world is recorded.
    public void Update(Terrain terrain, Camera camera)
    {
        var source = terrain.Source;
        if (!Enabled)
            return;

        if (!_treesLoaded)
            LoadTrees();

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

        // Last frame's cells have their data now; what they stood in for can go.
        foreach (var cell in _cells.Values)
        {
            if (!cell.Fresh)
                continue;
            cell.Fresh = false;
            if (cell.Replaced is { } old)
                Release(old);
            cell.Replaced = null;
        }

        for (var n = 0; n < UploadsPerFrame && _built.TryDequeue(out var built); n++)
        {
            _inFlight.Remove(built.Key);
            if (built.Generation != _generation)
                continue;

            if (_stale.TryGetValue(built.Key, out var marked) && marked <= built.Stamp)
                _stale.Remove(built.Key);
            Cell? stand = null;
            if (_cells.Remove(built.Key, out var replaced))
            {
                // (Rebuilt twice in a frame: the one never drawn goes, the one on screen stays standing in.)
                stand = replaced.Fresh ? replaced.Replaced : replaced;
                if (replaced.Fresh)
                    Release(replaced, keepReplaced: true);
            }

            var buffer = Buffers.UploadLater(
                MemoryMarshal.AsBytes(built.Instances.Length > 0 ? built.Instances.AsSpan() : new PlantInstance[1].AsSpan()), VkBufferUsageFlags.VertexBufferBit, "vegetation.cell");
            _cells[built.Key] = new Cell { Key = built.Key, Buffer = buffer, Starts = built.Starts, Low = built.Low, High = built.High, Total = built.Instances.Length, Replaced = stand };
            Instances += built.Instances.Length;
        }

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
            if (ReferenceEquals(lines, _lines))
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

    /// Lets a cell's buffer go once the GPU's done with it, and what it was standing in for unless that's kept.
    private void Release(Cell cell, bool keepReplaced = false)
    {
        Instances -= cell.Total;
        var buffer = cell.Buffer;
        Gpu.Defer(buffer.Dispose);
        if (!keepReplaced && cell.Replaced is { } old)
            Release(old);
        cell.Replaced = null;
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
            {
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
        var perKind = new List<PlantInstance>[KindCount];
        for (var i = 0; i < KindCount; i++)
            perKind[i] = [];

        var (originX, originZ) = (key.X * (double)size, key.Z * (double)size);
        var source = _source!;
        var low = float.MaxValue;
        var high = float.MinValue;

        // The land cover's costly to read, so it's sampled on a grid and blended.
        var grid = key.Far ? 9 : 3;
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

        void Place(int kind, uint salt, int attempts, Func<float4, float4, float, float, float, float> chance, Func<float, float4, float4, Random32, (float Scale, float3 Tint)> look, float minClear, float sink, float steepest)
        {
            var random = new Random32(Hash(key.X, key.Z, salt + (key.Far ? 1000u : 0u)));
            for (var n = 0; n < attempts; n++)
            {
                var (u, v, roll, pick) = (random.Next(), random.Next(), random.Next(), random.Next());
                var (x, z) = (originX + u * size, originZ + v * size);
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
                high = MathF.Max(high, y + scale * 2f);
                perKind[kind].Add(new PlantInstance(new float4((float)(x - originX), y - sink * scale, (float)(z - originZ), scale), new float4(tint, pick)));
                if (kind >= FirstTree && kind % 2 == 0)
                    perKind[kind + 1].Add(new PlantInstance(new float4((float)(x - originX), y - sink * scale, (float)(z - originZ), scale), new float4(new float3(tint.X, tint.Y, tint.Z), pick)));
            }
        }

        // Soft grey cards, so colour comes all from here: drier and yellower than the ground it grows from.
        static float3 Dry(float4 cover, float pick) =>
            new float3(cover.X * 2.4f + 0.13f, cover.Y * 2.2f + 0.12f, cover.Z * 2.0f + 0.07f) * (0.75f + 0.5f * pick);

        if (!key.Far)
        {
            Place(Grass, 1u, 420,
                (_, ground, patches, clear, _) => (ground.X * 0.75f + ground.Y * 0.22f) * MathEx.SmoothStep(0.25f, 0.65f, patches) * MathEx.SmoothStep(0f, 1.5f, clear),
                (pick, cover, _, _) => (0.45f + 0.6f * pick, Dry(cover, pick)), 0.2f, 0.03f, 0.8f);

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
            // Spaced as scrub is: every few metres in shrubland, thinner on bare ground, none on the track's shoulders.
            Place(Shrub, 3u, 1800,
                (cover, ground, patches, clear, _) => (ground.X * (0.3f + cover.W * 1.2f) + ground.Y * 0.3f) * MathEx.SmoothStep(0.15f, 0.6f, patches) * MathEx.SmoothStep(0.5f, 3f, clear),
                (pick, _, _, _) => (0.6f + 1.0f * pick, new float3(0.55f + 0.2f * pick, 0.62f + 0.2f * pick, 0.5f + 0.15f * pick)), 1f, 0.05f, 0.72f);

            Place(Boulder, 4u, 60,
                (_, ground, patches, clear, _) => (ground.Y * 0.5f + ground.X * 0.1f) * MathEx.SmoothStep(0.45f, 0.8f, patches) * MathEx.SmoothStep(1f, 4f, clear),
                (pick, cover, _, _) =>
                {
                    var size2 = 0.3f + 1.6f * pick * pick * pick;
                    return (size2, new float3(cover.X * 2.4f + 0.22f, cover.Y * 2.2f + 0.2f, cover.Z * 2f + 0.18f));
                }, 2f, 0.3f, 0.5f);

            // Trees only where the map says there's forest; the desert has none to speak of.
            for (var t = 0; t < TreeNodes.Length; t++)
            {
                var which = t;
                Place(FirstTree + which * 2, 10u + (uint)which, 320 / TreeNodes.Length,
                    (cover, _, patches, clear, _) => MathEx.SmoothStep(0.35f, 0.75f, cover.W) * 0.8f * (0.3f + patches) * MathEx.SmoothStep(2f, 6f, clear),
                    (pick, _, _, _) => (0.8f + 0.7f * pick, new float3(0.85f + 0.3f * pick, 0.85f + 0.25f * pick, 0.8f + 0.2f * pick)), 3f, 0.02f, 0.7f);
            }

            for (var t = FirstTree; t < KindCount; t++)
                if (perKind[t].Count > 0)
                    _treesWanted = true;
        }

        // All kinds in one buffer, each sorted so the ones furthest fading are first.
        var total = perKind.Sum(p => p.Count);
        var instances = new PlantInstance[total];
        var starts = new int[KindCount + 1];
        var at = 0;
        for (var i = 0; i < KindCount; i++)
        {
            starts[i] = at;
            perKind[i].Sort((a, b) => b.TintAndRandom.W.CompareTo(a.TintAndRandom.W));
            perKind[i].CopyTo(instances, at);
            at += perKind[i].Count;
        }

        starts[KindCount] = at;
        _built.Enqueue(new Built(key, generation, stamp, instances, starts, total > 0 ? low : 0f, total > 0 ? high : 0f));
    }

    private void LoadTrees()
    {
        _treesLoaded = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < TreeNodes.Length; i++)
        {
            // Trunk and branches share one mesh; each kind draws its own materials of it.
            var tree = Own(new RenderModel(Model.Load(KitFile, nodeName: TreeNodes[i])));
            var (min, max) = (tree.Mesh.BoundsMin, tree.Mesh.BoundsMax);
            var shift = new float3(-(min.X + max.X) * 0.5f, -min.Y, -(min.Z + max.Z) * 0.5f);

            var (b, l) = (_kinds[FirstTree + i * 2], _kinds[FirstTree + i * 2 + 1]);
            (b.Mesh, b.Parts, b.Textures, b.Shift) = (tree.Mesh, [tree.Parts[0]], tree.Parts[0].Textures, shift);
            var leaves = Merged(tree.Parts.Skip(1));
            (l.Mesh, l.Parts, l.Textures, l.Shift) = (tree.Mesh, [leaves], leaves.Textures, shift);
            // (The canopy starts a quarter of the way up: the trunk below it is bare.)
            l.FarCaster = Own(CanopyMesh(new float3(min.X, min.Y + (max.Y - min.Y) * 0.25f, min.Z), max));
            b.NoFarCaster = true;

            // A hundredth of its height at the top in the strongest gust, trunk and leaves together.
            b.Wind = l.Wind = 0.01f / MathF.Max(max.Y - min.Y, 1f);
        }

        var bush = Own(new RenderModel(Model.Load(KitFile, nodeName: ShrubNode)));
        var (bmin, bmax) = (bush.Mesh.BoundsMin, bush.Mesh.BoundsMax);
        var shrub = _kinds[Shrub];
        var twigs = Merged(bush.Parts);
        (shrub.Mesh, shrub.Parts, shrub.Textures) = (bush.Mesh, [twigs], twigs.Textures);
        shrub.Shift = new float3(-(bmin.X + bmax.X) * 0.5f, -bmin.Y, -(bmin.Z + bmax.Z) * 0.5f);
        RailWerkz.Core.Logging.Log.Info($"Vegetation: {TreeNodes.Length} trees and a bush loaded from the plants kit in {watch.ElapsedMilliseconds} ms");
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
    public void Draw(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, float aspect)
    {
        DrawnInstances = 0;
        if (!Enabled)
            return;

        var view = camera.View(aspect);
        var push = new Push { Matrix = camera.JitteredViewProjection(aspect) };
        var phases = WindPhases();

        for (var k = 0; k < KindCount; k++)
        {
            var kind = _kinds[k];
            if (kind.Mesh is null)
                continue;

            var bound = false;
            foreach (var entry in _cells.Values)
            {
                if (entry.Drawn is not { } cell)
                    continue;
                var count = cell.Starts[k + 1] - cell.Starts[k];
                if (count == 0)
                    continue;

                var size = cell.Key.Far ? FarCell : NearCell;
                var (ox, oz) = (cell.Key.X * (double)size - camera.X, cell.Key.Z * (double)size - camera.Z);
                var centre = new float3((float)ox + size * 0.5f, (cell.Low + cell.High) * 0.5f - (float)camera.Y, (float)oz + size * 0.5f);
                var radius = MathF.Sqrt(size * size * 0.5f + (cell.High - cell.Low) * (cell.High - cell.Low) * 0.25f) + 12f;

                if (!view.Sees(centre, radius))
                    continue;

                var nearest = MathF.Max(MathEx.Length(centre) - radius, 0f);
                var showing = Showing(count, nearest, kind.Fade * Quality.PlantReach);
                if (showing == 0)
                    continue;

                if (!bound && (kind.Parts is null || kind.Parts.Count == 1))
                {
                    kind.Textures!.Bind(cmd, layout);
                    bound = true;
                }

                push.Offset = new float4((float)ox, -(float)camera.Y, (float)oz, phases.Z);
                push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, kind.Cutout);
                push.Shift = new float4(kind.Shift, (float)camera.Y);
                push.Wind = new float4(WrapCell(cell.Key.X, size), WrapCell(cell.Key.Z, size), phases.X, phases.Y);
                cmd.PushConstants(layout, Stages, ByteSize.Zero, push);
                DrawPlants(cmd, layout, kind, kind.Mesh, cell.Buffer, showing, cell.Starts[k]);
                DrawnInstances += showing;
            }
        }
    }

    /// Draws what falls in one shadow cascade, with the pipeline bound.
    public void DrawCasters(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, float4x4 light, int cascade, Func<float3, float, bool> reaches)
    {
        if (!Enabled)
            return;

        var push = new Push { Matrix = light };
        var phases = WindPhases();
        var last = cascade >= FarCasterFrom;
        for (var k = 0; k < KindCount; k++)
        {
            var kind = _kinds[k];
            if (kind.Mesh is null || cascade > kind.LastCascade || (last && kind.NoFarCaster))
                continue;

            var standIn = last && kind.FarCaster is not null;
            var mesh = standIn ? kind.FarCaster! : kind.Mesh;

            var bound = false;
            foreach (var entry in _cells.Values)
            {
                if (entry.Drawn is not { } cell)
                    continue;
                var count = cell.Starts[k + 1] - cell.Starts[k];
                if (count == 0)
                    continue;

                var size = cell.Key.Far ? FarCell : NearCell;
                var (ox, oz) = (cell.Key.X * (double)size - camera.X, cell.Key.Z * (double)size - camera.Z);
                var centre = new float3((float)ox + size * 0.5f, (cell.Low + cell.High) * 0.5f - (float)camera.Y, (float)oz + size * 0.5f);
                var radius = MathF.Sqrt(size * size * 0.5f + (cell.High - cell.Low) * (cell.High - cell.Low) * 0.25f) + 12f;
                if (!reaches(centre, radius))
                    continue;

                var showing = Showing(count, MathF.Max(MathEx.Length(centre) - radius, 0f), kind.Fade * Quality.PlantReach);
                if (showing == 0)
                    continue;

                if (!bound && (standIn || kind.Parts is null || kind.Parts.Count == 1))
                {
                    kind.Textures!.Bind(cmd, layout);
                    bound = true;
                }

                push.Offset = new float4((float)ox, -(float)camera.Y, (float)oz, phases.Z);
                push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, standIn ? 2f : kind.Cutout);
                push.Shift = new float4(kind.Shift, (float)camera.Y);
                push.Wind = new float4(WrapCell(cell.Key.X, size), WrapCell(cell.Key.Z, size), phases.X, phases.Y);
                cmd.PushConstants(layout, Stages, ByteSize.Zero, push);
                if (standIn)
                    mesh.DrawInstanced(cmd, cell.Buffer, showing, cell.Starts[k]);
                else
                    DrawPlants(cmd, layout, kind, mesh, cell.Buffer, showing, cell.Starts[k]);
            }
        }
    }

    /// A model's materials (in a row in its index buffer) as one, drawn with the first's textures: the kit's branches and
    /// twigs are all cards of leaves alike, and one draw for them all is a fifth of the cost of one each.
    private static RenderModel.Part Merged(IEnumerable<RenderModel.Part> parts)
    {
        var list = parts.OrderBy(p => p.FirstIndex).ToList();
        var end = list.Max(p => p.FirstIndex + p.IndexCount);
        return list[0] with { IndexCount = end - list[0].FirstIndex };
    }

    /// A kind's instances: its whole mesh, its one material of it (its textures already bound), or each of its materials with
    /// its own textures.
    private static void DrawPlants(CommandBuffer cmd, VkPipelineLayout layout, Kind kind, Mesh mesh, BufferEx buffer, int count, int first)
    {
        if (kind.Parts is null)
        {
            mesh.DrawInstanced(cmd, buffer, count, first);
            return;
        }

        if (kind.Parts.Count == 1)
        {
            mesh.DrawRangeInstanced(cmd, buffer, kind.Parts[0].FirstIndex, kind.Parts[0].IndexCount, count, first);
            return;
        }

        foreach (var part in kind.Parts)
        {
            part.Textures.Bind(cmd, layout);
            mesh.DrawRangeInstanced(cmd, buffer, part.FirstIndex, part.IndexCount, count, first);
        }
    }

    // --- Meshes and textures made here. ---

    private static void Card(List<Vertex> vertices, List<uint> indices, float3 centre, float3 across, float height, float3 lean, float3 normal)
    {
        var first = (uint)vertices.Count;
        var bottom = centre;
        var top = centre + new float3(0f, height, 0f) + lean;
        vertices.Add(new Vertex(bottom - across, normal, new float2(0f, 1f)));
        vertices.Add(new Vertex(bottom + across, normal, new float2(1f, 1f)));
        vertices.Add(new Vertex(top + across, normal, new float2(1f, 0f)));
        vertices.Add(new Vertex(top - across, normal, new float2(0f, 0f)));
        indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }

    /// Three cards of grass crossing at the middle.
    private static Mesh GrassMesh()
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        for (var i = 0; i < 3; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.PI / 3f + 0.3f);
            var across = new float3(cos * 0.55f, 0f, sin * 0.55f);
            Card(vertices, indices, default, across, 1f, new float3(sin * 0.08f, 0f, -cos * 0.08f), new float3(0f, 1f, 0f));
        }

        return Mesh.Create(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }

    /// A rounded bush: cards round the middle, leaning out, with their normals pointing away from it so it lights like a solid dome.
    private static Mesh ShrubMesh()
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var random = new Random32(99u);
        for (var i = 0; i < 9; i++)
        {
            var angle = i * MathF.PI * 2f * 0.381966f;
            var (sin, cos) = MathF.SinCos(angle);
            var out1 = 0.15f + 0.3f * random.Next();
            var outward = new float3(sin, 0f, -cos);
            var across = new float3(cos * (0.55f + 0.2f * random.Next()), 0f, sin * (0.55f + 0.2f * random.Next()));
            var height = 0.8f + 0.5f * random.Next();
            var normal = MathEx.Normalize(outward + new float3(0f, 0.8f, 0f));
            Card(vertices, indices, outward * out1, across, height, outward * 0.3f, normal);
        }

        return Mesh.Create(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }

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

    /// A card of twiggy desert shrub, drawn here: branching dark stems from the bottom middle, flecked with small pale leaves. Greys
    /// only, so each shrub's tint gives it its colour. The clear parts keep a leaf's grey, so filtering doesn't darken the edges.
    private static Texture2D TwigTexture()
    {
        const int size = 512;
        var grey = new byte[size * size];
        var alpha = new byte[size * size];
        Array.Fill(grey, (byte)150);
        var random = new Random32(4242u);

        void Dot(float x, float y, float r, byte shade)
        {
            var (x0, x1) = ((int)(x - r - 1f), (int)(x + r + 1f));
            var (y0, y1) = ((int)(y - r - 1f), (int)(y + r + 1f));
            for (var py = Math.Max(y0, 0); py <= Math.Min(y1, size - 1); py++)
            {
                for (var px = Math.Max(x0, 0); px <= Math.Min(x1, size - 1); px++)
                {
                    var (dx, dy) = (px + 0.5f - x, py + 0.5f - y);
                    var a = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                    if (a <= 0f)
                        continue;
                    var i = py * size + px;
                    grey[i] = (byte)(grey[i] + (shade - grey[i]) * a);
                    alpha[i] = Math.Max(alpha[i], (byte)(a * 255f));
                }
            }
        }

        void Branch(float x, float y, float angle, float length, float width, int depth)
        {
            var steps = (int)(length / 2f);
            for (var s = 0; s < steps; s++)
            {
                angle += (random.Next() - 0.5f) * 0.12f;
                x += MathF.Sin(angle) * 2f;
                y -= MathF.Cos(angle) * 2f;
                var w = width * (1f - 0.5f * s / steps);
                Dot(x, y, w, 60);
                if (depth < 3 && random.Next() < 0.7f)
                {
                    var (ox, oy) = ((random.Next() - 0.5f) * 13f, (random.Next() - 0.5f) * 13f);
                    Dot(x + ox, y + oy, 1.6f + random.Next() * 2.4f, (byte)(120 + random.Next() * 110f));
                }
            }

            if (depth <= 0)
                return;

            var children = 2 + (int)(random.Next() * 2f);
            for (var c = 0; c < children; c++)
                Branch(x, y, angle + (random.Next() - 0.5f) * 1.5f, length * (0.55f + random.Next() * 0.2f), width * 0.65f, depth - 1);
        }

        for (var stem = 0; stem < 9; stem++)
            Branch(size * 0.5f + (stem - 4) * 12f, size - 2f, (stem - 4) * 0.2f + (random.Next() - 0.5f) * 0.25f, 110f + random.Next() * 40f, 4f, 3);

        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = grey[i];
            rgba[i * 4 + 3] = alpha[i];
        }

        return Texture2D.FromPixels(size, size, rgba, repeat: false, mipmaps: true, srgb: true, name: "shrub.twigs");
    }

    public void Dispose()
    {
        _jobs.Dispose();
        ClearCells();
        foreach (var thing in Enumerable.Reverse(_owned))
            thing.Dispose();
    }
}
