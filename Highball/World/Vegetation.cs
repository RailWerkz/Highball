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
/// The plants: grass and flowers cut out of grass_1k.glb's sample layout (GrassTypes), the forest pack's twelve trees (ForestFile), with
/// full and reduced bark, and its seven bushes and saplings in the shrub slots; past their meshes, each tree is an octahedral
/// impostor baked from its full model at load (ImpostorBaker). Every level of a plant draws the same instances; plant.vert
/// collapses each outside its level's distances, dithering one into the next across the change (plant_fade.glsl).
public sealed unsafe class Vegetation : IDisposable
{
    private const float NearCell = 32f;
    private const float NearReach = 110f;
    private const float FarCell = 128f;
    private const float FarReach = 1500f;
    private const int UploadsPerFrame = 6;

    /// The grass and flowers: Georgeous's "grass" (Sketchfab, under Sketchfab's Standard licence, so kept out of git), whose GrassBase
    /// node is a sample layout of eight kinds of grass and flower, some placed several times. GrassPieces cuts it apart: each of
    /// GrassTypes is the group of its pieces whose middle is nearest its X, Z in the layout (glTF metres).
    private const string GrassFile = "Vegetation/grass_1k.glb";
    private const string GrassNode = "Grass_Reeds_LOD0_GrassBase_0";

    /// One kind of grass or flower: where it stands in the layout, how much of its own size it's drawn at, and how many of it to try
    /// for in a near cell (32 m square). Flowers keep their own colours; the grasses take the land cover's (GrassTint).
    private readonly record struct GrassType(string Name, float X, float Z, float Scale, int Attempts, bool Flower);

    /// Tussocks, tall grass, wispy grass and short cover (the layout's A, F, G and E), then three flowers, sparse (B, D and H).
    /// Not its C (fine and pale) or its reeds.
    private static readonly GrassType[] GrassTypes =
    [
        new("Tussocks", -4.53f, 0.40f, 0.65f, 520, false), new("Tall grass", -4.34f, -4.13f, 0.55f, 170, false),
        new("Wispy grass", -4.10f, 3.99f, 0.65f, 260, false), new("Short cover", 3.79f, -0.02f, 0.7f, 320, false),
        new("Orange and purple flowers", -0.52f, 0.39f, 0.6f, 22, true), new("Flowering grass", 5.12f, 5.35f, 0.6f, 55, true),
        new("Yellow flowers", 0.26f, -4.36f, 0.6f, 22, true),
    ];

    /// The slot GrassTypes[i] is drawn in: the first in Grass, the rest after the trees.
    private static int GrassSlot(int i) => i == 0 ? Grass : FirstMoreGrass + i - 1;
    /// The trees: Dari's Various Forest Assets Pack (Sketchfab, CC BY 4.0), each tree a bark mesh and a leaf-card mesh, with no
    /// levels of detail of its own. ForestReducedFile is the same with its bark cut to a fifth (Tools/Blender/tree_lods.py), for the
    /// middle distance; without it the full trees are drawn there too.
    private const string ForestFile = "Vegetation/various_forest_assets_pack_1k.glb";
    private const string ForestReducedFile = "Vegetation/various_forest_assets_pack_1k_reduced.glb";

    /// The tree slots' trees in ForestFile, by their nodes (bark, leaves): the three commonest first, the three that also stray out of
    /// the woods last (TreeAttempts, Build's small). Not its plum (purple leaves), its orange birches, its dead trees and logs, or
    /// its boulders; its shrubs and saplings are the bushes (ForestShrubs).
    private static readonly (string Name, string Bark, string Leaves)[] ForestTrees =
    [
        ("Oak", "Object_70", "Object_71"), ("Maple", "Object_67", "Object_68"), ("Pine", "Object_52", "Object_53"),
        ("Ash", "Object_7", "Object_8"), ("Birch", "Object_55", "Object_56"), ("Tall birch", "Object_58", "Object_59"),
        ("Common birch", "Object_73", "Object_74"), ("Fir", "Object_46", "Object_47"), ("Deciduous", "Object_86", "Object_87"),
        ("Apple", "Object_4", "Object_5"), ("Cherry", "Object_10", "Object_11"), ("Noble fir", "Object_49", "Object_50"),
    ];

    // What a cell's instances are of: each slot is one plant or stone, every level of detail of it drawing the same instances.
    private const int Grass = 0;
    private const int Pebble = 1;
    private const int Boulder = 2;

    /// The shrub slots (ForestShrubs).
    private const int FirstShrub = 3;
    private const int ShrubCount = 7;

    /// The tree slots (ForestTrees): 7 to 20 m.
    private const int FirstTree = FirstShrub + ShrubCount;

    /// The shrub slots' bushes and saplings in ForestFile (2.5 to 8 m), by their nodes (bark, leaves), with how many of each to try
    /// for in a far cell: scattered over open ground and thickening at the woods' edges.
    private static readonly (string Name, string Bark, string Leaves, int Attempts)[] ForestShrubs =
    [
        ("Holly", "Object_16", "Object_17", 4), ("Raspberry", "Object_19", "Object_20", 4), ("Deciduous shrub", "Object_76", "Object_77", 4),
        ("Coniferous shrub", "Object_43", "Object_44", 3), ("Coniferous sapling", "Object_40", "Object_41", 3),
        ("Deciduous sapling", "Object_89", "Object_90", 2), ("Deciduous sapling 2", "Object_92", "Object_93", 2),
    ];

    /// How far the bushes reach: too small to need an impostor, they thin out to nothing by here instead.
    private const float ShrubFade = 450f;

    private const int TreeCount = 12;
    private const int FirstMoreGrass = FirstTree + TreeCount;
    private const int SlotCount = FirstMoreGrass + 6;

    /// How many of each tree to try for in a far cell (128 m square, where forest is thick): oak, maple and pine the commonest.
    private static readonly int[] TreeAttempts = [6, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3, 3];

    /// Where a plant's detail changes, in metres from the camera (each instance's own up to a tenth either way): from its full model
    /// to its reduced one, then to its impostor (the grass, its fewest-bladed clump).
    private static readonly (float Reduced, float Far) TreeLevels = (55f, 300f);
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

        /// Drawn as an octahedral impostor (ImpostorBaker, impostor.vert): Mesh is a quad, Textures its atlases, and its shadow is always
        /// its FarCaster's. Its bounding sphere's centre height and radius, model space at a size of 1, go to the shader in Band's zw.
        public bool Impostor;
        public float ImpostorCentre;
        public float ImpostorRadius;

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

        /// Seconds between last frame's wind phases and these, so plant.vert can say how far each plant swayed since (its motion).
        public float Delta;
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

    /// What the impostors are drawn with in the camera's view (impostor.vert, impostor_depth.frag and impostor.frag), made by the
    /// world renderer for its target as the plants' own pipelines are: their depth, then shaded.
    public VkPipeline ImpostorDepthPipeline { get; set; }
    public VkPipeline ImpostorPipeline { get; set; }
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
    /// Peak and Background are balanced so about as many grow in view (out to the trees' 1.4 km) as when they were spread evenly:
    /// tighter to the line than they were (110 m, to 320 m, 4.2 and 0.15), half again as many within 150 m of it, half as many far off.
    public const float TracksideDense = 70;
    public const float TracksideFalloff = 200f;
    public const float TracksidePeak = 7.05f;
    public const float Background = 0.06f;

    /// The belt of trees along the railway that grows whatever the land cover (Place): its chance near the line, from TracksideBeltNear
    /// metres from the track's edge easing out by TracksideBeltReach.
    public const float TracksideBelt = 0.9f;
    public const float TracksideBeltNear = 25f;
    public const float TracksideBeltReach = 120f;

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
                var fromTrack = crowd ? TrackDistance(field!, x, z) : float.MaxValue;
                if (crowd && random.Next() * cellPeak > TracksideDensity(fromTrack))
                    continue;
                var (cover, ground) = Sample(u, v);
                if (ground.Z + ground.W > 0.15f)
                    continue;

                var clear = Clearance(x, z);
                if (clear < minClear)
                    continue;

                var patches = Patches(x, z, key.Far ? 90f : 14f);
                // Along the railway trees grow whatever the land cover says (the map's forest is coarse, and a line's verges and
                // fence rows are wooded through farmland): a belt TracksideBelt strong near it, gone by TracksideBeltReach, on any
                // ground but water and bare rock, clumped as the rest are.
                var belt = crowd ? TracksideBelt * (1f - MathEx.SmoothStep(TracksideBeltNear, TracksideBeltReach, fromTrack)) * (0.4f + patches) * (ground.X + 0.5f * ground.Y) : 0f;
                if (roll > MathF.Max(chance(cover, ground, patches, clear, pick), belt * MathEx.SmoothStep(minClear, minClear * 2f, clear)))
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

        // Drier and yellower than the ground it grows from.
        static float3 Dry(float4 cover, float pick) =>
            new float3(cover.X * 2.4f + 0.13f, cover.Y * 2.2f + 0.12f, cover.Z * 2.0f + 0.07f) * (0.75f + 0.5f * pick);

        // The grasses' maps are green of their own: half the way to the land cover's dried colour (as bright as they are), so the grass
        // still yellows where the cover says it's dry.
        static float3 GrassTint(float4 cover, float pick)
        {
            var dry = Dry(cover, pick);
            var hue = dry / MathF.Max((dry.X + dry.Y + dry.Z) / 3f, 0.01f);
            return (new float3(1f) + hue) * 0.5f * (0.85f + 0.3f * pick);
        }

        // The trees' and bushes' maps have their own colours: only a little variety from one to the next.
        static float3 Leafy(float pick) => new float3(0.85f + 0.3f * pick, 0.85f + 0.25f * pick, 0.8f + 0.2f * pick);

        if (!key.Far)
        {
            // The grasses overlapping into a sward where the cover's grassy, each in its own part of the soft patches (tall grass in
            // their thick middles, wispy grass round their thin edges, tussocks between), the short cover thickest beside the line, and
            // the flowers sparse through the middling grass.
            for (var g = 0; g < GrassTypes.Length; g++)
            {
                var type = GrassTypes[g];
                Func<float, float, float> where = g switch
                {
                    0 => (patches, _) => MathEx.SmoothStep(0.15f, 0.45f, patches),
                    1 => (patches, _) => MathEx.SmoothStep(0.55f, 0.8f, patches),
                    2 => (patches, _) => MathEx.SmoothStep(0.05f, 0.25f, patches) * (1f - MathEx.SmoothStep(0.45f, 0.65f, patches)),
                    3 => (_, clear) => 0.15f + 0.85f * (1f - MathEx.SmoothStep(0.5f, 5f, clear)),
                    4 => (patches, _) => MathEx.SmoothStep(0.35f, 0.5f, patches) * (1f - MathEx.SmoothStep(0.6f, 0.75f, patches)),
                    5 => (patches, _) => MathEx.SmoothStep(0.25f, 0.4f, patches) * (1f - MathEx.SmoothStep(0.55f, 0.7f, patches)),
                    _ => (patches, _) => MathEx.SmoothStep(0.45f, 0.6f, patches) * (1f - MathEx.SmoothStep(0.7f, 0.85f, patches)),
                };
                Place(GrassSlot(g), 1u + (uint)g * 977u, type.Attempts,
                    (_, ground, patches, clear, _) => (ground.X * 0.8f + ground.Y * 0.25f) * where(patches, clear) * MathEx.SmoothStep(0f, 1.5f, clear),
                    (pick, cover, _, _) => (type.Scale * (0.8f + 0.4f * pick), type.Flower ? Leafy(pick) : GrassTint(cover, pick)), 0.2f, 0f, 0.8f);
            }

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
            // The bushes: scattered over open ground and thickening at the woods' edges (where the trees take over), kept off the
            // track as the trees are; thickest along the railway too.
            for (var b = 0; b < ShrubCount; b++)
            {
                Place(FirstShrub + b, 3u + (uint)b * 101u, ForestShrubs[b].Attempts,
                    (cover, ground, patches, clear, _) => (0.25f * ground.X + MathEx.SmoothStep(0.1f, 0.5f, cover.W)) * (0.3f + patches) * MathEx.SmoothStep(TreeClearance, TreeClearanceSoft, clear),
                    (pick, _, _, _) => (0.7f + 0.5f * pick, Leafy(pick)), TreeClearance, 0f, 0.7f, trackside: true);
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
    /// textures from the models' own maps.
    private void LoadPlants()
    {
        _plantsLoaded = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var grassPieces = GrassPieces(Model.LoadParts(GrassFile)[GrassNode]);
        var forestParts = Model.LoadParts(ForestFile);
        var reducedParts = File.Exists(RailWerkz.Core.IO.ContentPath.Model(ForestReducedFile)) ? Model.LoadParts(ForestReducedFile) : null;

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


        var solid = Own(Texture2D.Solid(new byte4(255, 255, 255, 255)));

        // A model's part's colour map (its cut-out in its alpha), by its node.
        TextureBinding MapOf(string file, string node)
        {
            var model = Own(Model.Load(file, nodeName: node));
            var map = model.Surfaces[model.SubMeshes[0].Material].Texture ?? solid;
            return Own(new TextureBinding(map, map, map, map));
        }

        var pending = new List<(Kind Kind, int First, int Count)>();

        // The impostors to bake once the mesh is made: each model once (several tree slots share one), with the pieces of its full
        // model, and its kinds (one a slot) that draw it.
        var bakes = new Dictionary<string, (List<ImpostorBaker.Part> Parts, float3 Min, float3 Max, List<Kind> Kinds)>();

        // How far from (0, y, 0) the furthest of a range's vertices is.
        float Reach(int first, int count, float y)
        {
            var most = 0f;
            for (var i = first; i < first + count; i++)
            {
                var p = vertices[(int)indices[i]].Position;
                most = MathF.Max(most, MathF.Sqrt(p.X * p.X + (p.Y - y) * (p.Y - y) + p.Z * p.Z));
            }
            return most;
        }

        Kind ImpostorOf(string model, int slot, float near, float fade, IEnumerable<ImpostorBaker.Part> parts, float3 min, float3 max)
        {
            var kind = new Kind { Mesh = null!, Textures = null!, Slot = slot, Near = near, SoftNormal = 0.35f, Fade = fade, LastCascade = 3, Impostor = true };
            if (!bakes.TryGetValue(model, out var bake))
                bakes[model] = bake = (parts.ToList(), min, max, []);
            bake.Kinds.Add(kind);
            return kind;
        }

        // The grass and flowers: each its every blade close to, then half of them, then a quarter (GrassLevel). They cast no shadow:
        // too fine for the shadow map. One map for all, its cut-out in its alpha.
        var grassMap = MapOf(GrassFile, GrassNode);
        for (var g = 0; g < GrassTypes.Length; g++)
        {
            var slot = GrassSlot(g);
            foreach (var (keep, near, far) in new[] { (1f, 0f, GrassLevels.Reduced), (0.5f, GrassLevels.Reduced, GrassLevels.Far), (0.25f, GrassLevels.Far, float.PositiveInfinity) })
            {
                var name = $"{GrassTypes[g].Name} {keep}";
                var piece = Add(new Dictionary<string, Model.Part> { [name] = GrassLevel(name, grassPieces[g], keep) }, name);
                _heights[slot] = MathF.Max(_heights[slot], piece.Max.Y);
                pending.Add((new Kind
                {
                    Mesh = null!, Textures = grassMap, Slot = slot, Near = near, Far = far, Wind = 0.07f, SoftNormal = 0.8f, Fade = NearReach * 0.75f, Cutout = 0f,
                    LastCascade = -1,
                }, piece.First, piece.Count));
            }
        }

        // The trees: each its full bark and leaves close to, its reduced bark (and the same leaves) further off, then its impostor,
        // baked from the full tree. Their maps' cut-outs are in the colour maps' alpha.
        var canopies = new List<(Kind Kind, float3 Min, float3 Max)>();
        for (var t = 0; t < TreeCount; t++)
        {
            var slot = FirstTree + t;
            var (name, barkNode, leafNode) = ForestTrees[t];
            var (reduced, far) = TreeLevels;
            var trunk = Add(forestParts, barkNode);
            var crown = Add(forestParts, leafNode);
            var lesser = reducedParts is not null && reducedParts.ContainsKey(barkNode) ? Add(reducedParts, barkNode) : trunk;
            var height = MathF.Max(MathF.Max(trunk.Max.Y, crown.Max.Y), 1f);
            _heights[slot] = height;

            // A hundredth of its height at the top in the strongest gust, every level of it alike.
            var wind = 0.01f / height;
            var fade = FarReach * 0.95f;
            var (barkMap, leafMap) = (MapOf(ForestFile, barkNode), MapOf(ForestFile, leafNode));

            foreach (var (bark, near, to) in new[] { (trunk, 0f, reduced), (lesser, reduced, far) })
            {
                var bole = new Kind
                {
                    Mesh = null!, Textures = barkMap, Slot = slot, Near = near, Far = to, Wind = wind, Fade = fade, Cutout = 0f, LastCascade = 3, NoFarCaster = true,
                };
                var canopy = new Kind { Mesh = null!, Textures = leafMap, Slot = slot, Near = near, Far = to, Wind = wind, SoftNormal = 0.35f, Fade = fade, Cutout = 0f, LastCascade = 3 };
                pending.Add((bole, bark.First, bark.Count));
                pending.Add((canopy, crown.First, crown.Count));
                canopies.Add((canopy, crown.Min, crown.Max));
            }

            ImpostorOf(name, slot, far, fade, [new(trunk.First, trunk.Count, barkMap, 0f), new(crown.First, crown.Count, leafMap, 0f)],
                float3.Min(trunk.Min, crown.Min), float3.Max(trunk.Max, crown.Max));
        }

        // The bushes: full bark close to, reduced further off, as the trees; no impostor (ShrubFade).
        for (var b = 0; b < ShrubCount; b++)
        {
            var (_, barkNode, leafNode, _) = ForestShrubs[b];
            var slot = FirstShrub + b;
            var trunk = Add(forestParts, barkNode);
            var crown = Add(forestParts, leafNode);
            var lesser = reducedParts is not null && reducedParts.ContainsKey(barkNode) ? Add(reducedParts, barkNode) : trunk;
            _heights[slot] = MathF.Max(MathF.Max(trunk.Max.Y, crown.Max.Y), 1f);
            var wind = 0.01f / _heights[slot];
            var (barkMap, leafMap) = (MapOf(ForestFile, barkNode), MapOf(ForestFile, leafNode));
            foreach (var (bark, near, to) in new[] { (trunk, 0f, TreeLevels.Reduced), (lesser, TreeLevels.Reduced, float.PositiveInfinity) })
            {
                pending.Add((new Kind { Mesh = null!, Textures = barkMap, Slot = slot, Near = near, Far = to, Wind = wind, Fade = ShrubFade, Cutout = 0f, LastCascade = 3, NoFarCaster = true },
                    bark.First, bark.Count));
                var canopy = new Kind { Mesh = null!, Textures = leafMap, Slot = slot, Near = near, Far = to, Wind = wind, SoftNormal = 0.35f, Fade = ShrubFade, Cutout = 0f, LastCascade = 3 };
                pending.Add((canopy, crown.First, crown.Count));
                canopies.Add((canopy, crown.Min, crown.Max));
            }
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

        // The impostors, baked from the full models, each drawn as one quad; their shadows always their canopy's stand-in.
        var bakeWatch = System.Diagnostics.Stopwatch.StartNew();
        var quad = Own(Mesh.Create(
            [
                new Vertex(new float3(-1f, -1f, 0f), new float3(0f, 0f, 1f), new float2(0f, 1f)), new Vertex(new float3(1f, -1f, 0f), new float3(0f, 0f, 1f), new float2(1f, 1f)),
                new Vertex(new float3(1f, 1f, 0f), new float3(0f, 0f, 1f), new float2(1f, 0f)), new Vertex(new float3(-1f, 1f, 0f), new float3(0f, 0f, 1f), new float2(0f, 0f)),
            ],
            [0u, 1u, 2u, 0u, 2u, 3u]));
        using (var baker = new ImpostorBaker())
        {
            foreach (var (model, (parts, min, max, kinds)) in bakes)
            {
                var centreY = (min.Y + max.Y) * 0.5f;
                var radius = parts.Max(p => Reach(p.FirstIndex, p.IndexCount, centreY)) * 1.02f;
                var (albedo, normal) = baker.Bake(mesh, parts, new float3(0f, centreY, 0f), radius, model);
                var textures = Own(new TextureBinding(Own(albedo), Own(normal), normal, normal));
                var caster = Own(CanopyMesh(new float3(min.X, MathF.Max(min.Y, max.Y * 0.25f), min.Z), max));
                foreach (var kind in kinds)
                {
                    (kind.Mesh, kind.FirstIndex, kind.IndexCount, kind.Textures, kind.FarCaster) = (quad, 0, 6, textures, caster);
                    (kind.ImpostorCentre, kind.ImpostorRadius) = (centreY, radius);
                    _kinds.Add(kind);
                }
            }
        }

        RailWerkz.Core.Logging.Log.Info($"Vegetation: {bakes.Count} impostors baked in {bakeWatch.ElapsedMilliseconds} ms");

        RailWerkz.Core.Logging.Log.Info($"Vegetation: {GrassTypes.Length} grasses and flowers, {TreeCount} forest-pack trees and {ShrubCount} bushes{(reducedParts is null ? " (no reduced bark: " + ForestReducedFile + " missing)" : "")}, {_kinds.Count} draws, {indices.Count / 3} triangles, loaded in {watch.ElapsedMilliseconds} ms");
    }

    /// GrassBase cut into GrassTypes: its triangles into the pieces they join up into (a blade, a card), the pieces into the groups
    /// that stand together (a clump: pieces within 0.9 m of one another), and for each type the group whose middle is nearest its
    /// place, each piece's triangles moved to stand on the group's middle at its lowest point.
    private static List<Vertex[]>[] GrassPieces(Model.Part layout)
    {
        var (vertices, indices) = (layout.Vertices, layout.Indices);
        var parent = Enumerable.Range(0, vertices.Length).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        for (var t = 0; t < indices.Length; t += 3)
        {
            var a = Find((int)indices[t]);
            parent[Find((int)indices[t + 1])] = a;
            parent[Find((int)indices[t + 2])] = a;
        }

        var byRoot = new Dictionary<int, List<Vertex>>();
        for (var t = 0; t < indices.Length; t += 3)
        {
            var root = Find((int)indices[t]);
            if (!byRoot.TryGetValue(root, out var tris))
                byRoot[root] = tris = [];
            tris.Add(vertices[indices[t]]);
            tris.Add(vertices[indices[t + 1]]);
            tris.Add(vertices[indices[t + 2]]);
        }

        var pieces = byRoot.Values.Select(v => v.ToArray()).ToList();
        var middles = pieces.Select(p => new float2(p.Average(v => v.Position.X), p.Average(v => v.Position.Z))).ToArray();
        var group = Enumerable.Repeat(-1, pieces.Count).ToArray();
        var groups = 0;
        for (var i = 0; i < pieces.Count; i++)
        {
            if (group[i] >= 0)
                continue;
            var stack = new Stack<int>([i]);
            group[i] = groups;
            while (stack.TryPop(out var q))
            {
                for (var m = 0; m < pieces.Count; m++)
                {
                    var d = middles[m] - middles[q];
                    if (group[m] < 0 && d.X * d.X + d.Y * d.Y < 0.81f)
                    {
                        group[m] = groups;
                        stack.Push(m);
                    }
                }
            }

            groups++;
        }

        var result = new List<Vertex[]>[GrassTypes.Length];
        for (var g = 0; g < GrassTypes.Length; g++)
        {
            var (best, bestDistance, bestMiddle) = (-1, float.MaxValue, default(float3));
            for (var k = 0; k < groups; k++)
            {
                var (min, max) = (new float3(float.MaxValue), new float3(float.MinValue));
                for (var i = 0; i < pieces.Count; i++)
                    if (group[i] == k)
                        foreach (var v in pieces[i])
                            (min, max) = (float3.Min(min, v.Position), float3.Max(max, v.Position));
                var (cx, cz) = ((min.X + max.X) * 0.5f, (min.Z + max.Z) * 0.5f);
                var (dx, dz) = (cx - GrassTypes[g].X, cz - GrassTypes[g].Z);
                if (dx * dx + dz * dz < bestDistance)
                    (best, bestDistance, bestMiddle) = (k, dx * dx + dz * dz, new float3(cx, min.Y, cz));
            }

            if (best < 0 || bestDistance > 1f)
                throw new InvalidDataException($"{GrassFile} has no {GrassTypes[g].Name} near ({GrassTypes[g].X}, {GrassTypes[g].Z})");
            result[g] = Enumerable.Range(0, pieces.Count).Where(i => group[i] == best)
                .Select(i => pieces[i].Select(v => v with { Position = v.Position - bestMiddle }).ToArray()).ToList();
        }

        return result;
    }

    /// A share of a grass type's pieces, as a part to add: every piece close to, fewer further off, the same ones each time (spread
    /// evenly through the clump by the golden ratio). A clump of a few pieces keeps them all.
    private static Model.Part GrassLevel(string name, List<Vertex[]> pieces, float keep)
    {
        var kept = pieces.Count <= 4 ? pieces : pieces.Where((_, i) => i * 0.618034f % 1f < keep).ToList();
        var vertices = kept.SelectMany(p => p).ToArray();
        return new Model.Part(name, vertices, [.. Enumerable.Range(0, vertices.Length).Select(i => (uint)i)], default);
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
    public void Draw(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, float aspect, VkPipeline depthPipeline, VkPipeline colorPipeline, int culled = -1,
        PlantPass pass = PlantPass.Both)
    {
        if (pass != PlantPass.Color)
            DrawnInstances = 0;
        if (!Enabled)
            return;

        if (culled >= 0)
        {
            var gpuPush = new Push { Matrix = camera.JitteredViewProjection(aspect), Globals = Globals(camera), Delta = _phasesDelta };
            if (pass != PlantPass.Color)
                DrawCulled(cmd, layout, ref gpuPush, culled, -1, (depthPipeline, ImpostorDepthPipeline));

            if (pass != PlantPass.Depth)
                DrawCulled(cmd, layout, ref gpuPush, culled, -1, (colorPipeline, ImpostorPipeline));

            return;
        }

        var view = camera.View(aspect);
        var push = new Push { Matrix = camera.JitteredViewProjection(aspect), Globals = Globals(camera), Delta = _phasesDelta };
        while (_gathered.Count < _kinds.Count)
            _gathered.Add([]);

        bool? impostorBound = null;
        void Use(Kind kind, VkPipeline plain, VkPipeline impostor)
        {
            if (impostorBound == kind.Impostor)
                return;
            impostorBound = kind.Impostor;
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, kind.Impostor ? impostor : plain);
        }

        for (var k = 0; pass != PlantPass.Color && k < _kinds.Count; k++)
        {
            var kind = _kinds[k];
            _gathered[k].Clear();
            if (!Gather(camera, kind, (centre, radius, _) => view.Sees(centre, radius)))
                continue;

            Use(kind, depthPipeline, ImpostorDepthPipeline);
            _gathered[k].AddRange(_visible);
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, kind.Cutout);
            push.Band = Band(kind);
            kind.Textures.Bind(cmd, layout);
            DrawVisible(cmd, layout, ref push, kind.Mesh, kind.FirstIndex, kind.Count);

            foreach (var visible in _visible)
                DrawnInstances += visible.Count;
        }

        if (pass == PlantPass.Depth)
            return;
        impostorBound = null;
        for (var k = 0; k < _kinds.Count; k++)
        {
            if (_gathered[k].Count == 0)
                continue;

            var kind = _kinds[k];
            Use(kind, colorPipeline, ImpostorPipeline);
            _visible.Clear();
            _visible.AddRange(_gathered[k]);
            push.Shape = new float4(kind.Wind, kind.SoftNormal, kind.Fade * Quality.PlantReach, kind.Cutout);
            push.Band = Band(kind);
            kind.Textures.Bind(cmd, layout);
            DrawVisible(cmd, layout, ref push, kind.Mesh, kind.FirstIndex, kind.Count);
        }
    }

    /// Draw's halves: the alpha-tested depth (drawn before the ground, so ground under plants isn't shaded), then the shaded
    /// plants (an equal depth test, after the track's fades so they cover them), or both at once.
    public enum PlantPass { Both, Depth, Color }

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

            var standIn = (last || kind.Impostor) && kind.FarCaster is not null;
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
    private static float4 Band(Kind kind) =>
        new(kind.Near, float.IsPositiveInfinity(kind.Far) ? 1e9f : kind.Far, kind.Impostor ? kind.ImpostorCentre : 0f, kind.Impostor ? kind.ImpostorRadius : 0f);

    /// What every cell drawn this frame shares: the ground below the camera and the wind's phases.
    /// The wind's phases are taken once a frame: the plants' depth and their shaded pass (drawn apart, PlantPass) and every shadow
    /// must bend them the same, or the equal depth test misses.
    private static float4 Globals(Camera camera)
    {
        if (_phasesFrame != Gpu.FrameNumber)
        {
            var now = Time.Now;
            // (The first frame, or after a long stall: no sway to follow.)
            _phasesDelta = _phasesFrame < 0 ? 0f : (float)Math.Clamp(now - _phasesTime, 0.0, 0.25);
            (_phasesFrame, _phases, _phasesTime) = (Gpu.FrameNumber, WindPhases(), now);
        }
        return new float4(-(float)camera.Y, _phases.Z, _phases.X, _phases.Y);
    }

    private static long _phasesFrame = -1;
    private static float3 _phases;
    private static double _phasesTime;
    private static float _phasesDelta;

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

            // Its nearest and furthest from the camera, against the kind's distances (which each instance moves by up to a tenth, and
            // hands over to the next level across another 6% either side: plant_fade.glsl).
            var (lowY, highY) = (cell.Low - (float)camera.Y, cell.High - (float)camera.Y);
            var nx = MathF.Max(MathF.Max((float)-ox - size, (float)ox), 0f);
            var nz = MathF.Max(MathF.Max((float)-oz - size, (float)oz), 0f);
            var ny = MathF.Max(MathF.Max(lowY, -highY), 0f);
            var nearest = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nearest >= kind.Far * 1.2f)
                continue;
            var fx = MathF.Max(MathF.Abs((float)ox), MathF.Abs((float)ox + size));
            var fz = MathF.Max(MathF.Abs((float)oz), MathF.Abs((float)oz + size));
            var fy = MathF.Max(MathF.Abs(lowY), MathF.Abs(highY));
            if (MathF.Sqrt(fx * fx + fy * fy + fz * fz) < kind.Near * 0.8f)
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
    private const int CullSlotStarts = 29;

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
                var stand = cascade >= 0 && (last || kind.Impostor) && kind.FarCaster is not null;
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

    /// Draws a culled pass's kinds: each kind one call of as many commands as the GPU kept. With the pipeline bound (a shadow
    /// cascade's, whose impostors are drawn as their stand-ins), or for the camera's view, binding pipelines' plain one or their
    /// impostors' as each kind needs.
    private void DrawCulled(CommandBuffer cmd, VkPipelineLayout layout, ref Push push, int index, int cascade, (VkPipeline Plain, VkPipeline Impostor)? pipelines = null)
    {
        bool? impostorBound = null;
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
            if (pipelines is var (plain, impostor) && impostorBound != kind.Impostor)
            {
                impostorBound = kind.Impostor;
                cmd.BindPipeline(VkPipelineBindPoint.Graphics, kind.Impostor ? impostor : plain);
            }

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
