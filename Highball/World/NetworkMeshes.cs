using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

[StructLayout(LayoutKind.Sequential)]
public struct LineVertex(float3 position, float3 normal, float4 data)
{
    public float3 Position = position;
    public float3 Normal = normal;
    /// Across the line (m), along it (m, wrapping every 1200), the material and its style (material + 8 x style), and
    /// a material's own value: the rail centres' offset for ballast, height up the profile for rail, distance to the
    /// nearest junction for asphalt, a random tint for ties.
    public float4 Data = data;

    public static readonly VertexAttribute[] Layout = [new(0, 3, 0), new(1, 3, 12), new(2, 4, 24)];
}

/// One placed part (a tie, a tie plate): where it stands relative to its chunk and a random tint, the way it faces,
/// and how much longer across it is than as made (0: as made; a turnout's long timbers).
[StructLayout(LayoutKind.Sequential)]
public struct TieInstance(float4 positionRandom, float4 forward)
{
    public float4 PositionRandom = positionRandom;
    public float4 Forward = forward;

    public static readonly VertexAttribute[] Layout = [new(3, 4, 0), new(4, 4, 16)];
}

/// A signal's lamps, as flags: any of them may be lit at once.
[Flags]
public enum SignalLamps { None = 0, Red = 1, Yellow = 2, Green = 4 }

/// The materials line.frag knows.
public enum LineMaterial { Ballast, Rail, Asphalt, Gravel, Concrete, Painted, Steel }

/// The roads and rails in 3D, built from NetworkLines in square chunks on worker threads and streamed by distance.
/// Each chunk has a far mesh (ballast beds, roads, bridges: drawn out to FarRadius) and a near one (rails, and the
/// tie and tie plate models placed as instances: out to NearRadius, past which the ballast shader paints the track in).
/// The tie, the plate and the ballast's texture are made in Blender by Tools/Blender/track_parts.py.
///
/// Every shape is a cross-section swept along the line's centreline at its formation height: the ballast a
/// trapezoid with 1:1.5 shoulders, the rail a 136 lb profile, roads a crowned-flat deck with gravel shoulders,
/// bridges a deck on steel girders with railings, on piers and abutments down to the ground (NetworkMeshes.Bridges.cs).
public sealed partial class NetworkMeshes : IDisposable
{
    public const float ChunkSize = 500f;
    public float NearRadius { get; set; } = 1000f;
    /// Out to here ties are the full model and have their plates; past it, a decimated tie and no plates.
    public float DetailRadius { get; set; } = 120f;

    /// How far the full tie plate model's drawn; past it, the plain block (_plateFar) out to TieReach.
    public float PlateDetail { get; set; } = 120f;

    /// How far tie models are drawn at all; past it the ballast paints them (line.frag's TieReach, which must match). As far as
    /// the track's own detail reaches (NearRadius): the chunks that hold the ties go no further.
    public const float TieReach = 1000f;
    public float FarRadius { get; set; } = 6000f;
    private const int MaxUploadsPerFrame = 4;

    private const float TieSpacing = 0.495f;   // 19.5 in
    private const float TieLength = 2.5908f;   // 8 ft 6 in
    /// How long a switch takes to throw, seconds.
    private const float ThrowTime = 3f;
    private const float TieRise = 0.02f;       // tie top above the crib ballast
    private const float PlateDepth = 0.014f;   // the plate's rail seat above the tie
    private const float RoadRise = 0.06f;
    private const float RailHeadHalf = 0.0373f;
    internal const float RailTop = 0.1857f;
    /// The rail head's top over a rail line's formation (NetworkLine.Y): the ballast, a tie, its plate and the rail. Where
    /// wheels run.
    public const float RailTopRise = NetworkLine.BallastHeight + TieRise + PlateDepth + RailTop;
    private const float Wrap = 1200f;
    /// Parts are sorted into Cells x Cells squares of each chunk, so each square can be drawn in the detail its
    /// distance calls for.
    private const int Cells = 10;
    private const float CellSize = ChunkSize / Cells;

    /// 136 lb rail, clockwise from the outside of the foot (x across, y up).
    internal static readonly (float U, float Y)[] RailProfile =
    [
        (-0.0762f, 0f), (-0.0762f, 0.011f), (-0.012f, 0.030f), (-0.0088f, 0.045f), (-0.0088f, 0.125f), (-0.025f, 0.140f),
        (-0.0373f, 0.145f), (-0.0373f, 0.178f), (-0.030f, RailTop), (0.030f, RailTop), (0.0373f, 0.178f), (0.0373f, 0.145f),
        (0.025f, 0.140f), (0.0088f, 0.125f), (0.0088f, 0.045f), (0.012f, 0.030f), (0.0762f, 0.011f), (0.0762f, 0f),
    ];

    private readonly record struct ChunkKey(int X, int Z);

    private readonly record struct Run(int Line, int First, int Last);

    private sealed class Chunk
    {
        public Mesh? Far;
        public Mesh? Near;
        /// How many of the indices at the end of each are its fades into the ground, drawn on their own.
        public int FarFades;
        public int NearFades;
        /// Built, though possibly empty.
        public bool HasFar;
        public bool HasNear;
        public BufferEx Ties;
        public int TieCount;
        public BufferEx Plates;
        public int PlateCount;
        /// Where each cell's parts start in the instance buffers (Cells x Cells + 1 entries).
        public int[] TieCells = [];
        public int[] PlateCells = [];
        public long Used;
    }

    private sealed record Built(ChunkKey Key, bool Near, LineVertex[] Vertices, uint[] Indices, int Fades, TieInstance[] Ties, TieInstance[] Plates,
        int[] TieCells, int[] PlateCells);

    private NetworkLines _lines;
    /// Chunk meshes made from lines an edit has since changed: kept on screen until their rebuild arrives.
    private readonly HashSet<(ChunkKey, bool)> _stale = [];
    private readonly TerrainSource _source;
    private readonly Dictionary<ChunkKey, List<Run>> _runs = [];
    private readonly Dictionary<ChunkKey, Chunk> _chunks = [];
    private readonly HashSet<(ChunkKey, bool)> _building = [];
    private readonly Dictionary<(ChunkKey, bool), double> _wanted = [];
    private readonly JobStream _jobs = new("Line chunks", 0.4);
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly List<(ChunkKey Key, bool Near)> _drawList = [];
    private readonly Model _tie;
    private readonly Model _tieFar;
    private readonly Model _plate;
    /// The tie plate past PlateDetail: a plain block the size of the full one (12 triangles, not 180).
    private readonly Mesh _plateFar;
    private readonly Texture2D _ballast;
    private readonly TrackTextures _trackTextures = new();
    private readonly TextureBinding _tieTexture;
    private readonly TextureBinding _plateTexture;
    private readonly TextureBinding _ballastTexture;
    private readonly Texture2D? _fallback;
    /// The switch kit's parts, right and left hand (Tools/Blender/turnouts.py); none if the files aren't there.
    private readonly Dictionary<int, Dictionary<string, Model.Part>> _kits = [];
    private long _frame;

    // Turnouts: each one's blades (a mesh of their own, made again while they move), and where every switch's blades
    // are (0 normal, 1 reverse) and are going, by node, kept through rebuilds.
    private sealed class Switch
    {
        public required Turnout Turnout { get; set; }
        public required ChunkKey Key { get; set; }
        public Mesh? Blades;
        public float BuiltAt = -1f;
    }

    private readonly Dictionary<long, Switch> _switches = [];
    private readonly Dictionary<ChunkKey, List<Turnout>> _turnoutsByChunk = [];
    private readonly Dictionary<ChunkKey, List<CrossingAssembly>> _assembliesByChunk = [];
    private IReadOnlyList<PlacedObject> _objects = [];
    private RouteProjection? _objectProjection;
    private readonly Dictionary<ChunkKey, List<PlacedAt>> _objectsByChunk = [];
    private readonly Dictionary<long, PlacedObject> _handThrows = [];
    private readonly Dictionary<PlacedKind, Dictionary<string, Model.Part>> _objectModels = [];

    /// A placed object where it is: its height's found on the main thread, where the ground's safe to read.
    /// The stretches of each road (by line) that crossings' decks take the place of, along it.
    private Dictionary<int, List<(float From, float To)>> _crossingGaps = [];

    /// The stretches of each track (by line) under crossings' panels, along it, where it lays no ties.
    private Dictionary<int, List<(float From, float To)>> _crossingCovers = [];

    /// Through a crossover (two switches on side-by-side tracks joined by one track between them), long timbers span both
    /// tracks the whole way from one switch to the other, as they're laid: on one track (Spans, by line: its distances, the
    /// other track, and which side of it that is) its ties reach on under the other, and the other track's own ties and the
    /// crossover's are left out there (Skips, by line).
    private readonly Dictionary<int, List<(float From, float To, Leg Other, int Across)>> _crossoverSpans = [];
    private readonly Dictionary<int, List<(float From, float To)>> _crossoverSkips = [];

    private sealed record PlacedAt(PlacedObject Object, double X, double Z, float Y, CrossingDeck? Deck);

    /// The paints line.frag knows, by the name a placed model's parts use.
    private static readonly string[] Paints = ["Black", "White", "Galv", "Yellow", "Red", "Green", "Concrete", "Wood", "Ballast", "LensRed", "LensYellow", "LensGreen"];

    /// How far on from a lens's paint the same lens lit is (line.frag, LitLensPaint).
    private const int LitLens = 3;

    /// Which of a signal's lamps are lit, by signal: set by hand in the Route Builder for now, as there are no signalling rules to
    /// set them yet, and not saved. A signal not in here stands at stop (red alone). Read by the chunk builders on worker threads.
    private readonly ConcurrentDictionary<long, SignalLamps> _lamps = new();

    /// A signal's lamps, top to bottom on the head: green, yellow, red.
    public static readonly (SignalLamps Lamp, string Part, string Paint, float Height, float3 Light)[] Lamps =
    [
        (SignalLamps.Green, "Fixed_LensGreen", "LensGreen", 5.15f, new float3(0.13f, 2.3f, 1.05f)),
        (SignalLamps.Yellow, "Fixed_LensYellow", "LensYellow", 4.75f, new float3(3f, 1.4f, 0.1f)),
        (SignalLamps.Red, "Fixed_LensRed", "LensRed", 4.35f, new float3(3f, 0.12f, 0.05f)),
    ];

    public SignalLamps LampsOf(long signal) => _lamps.GetValueOrDefault(signal, App.DevTools.Lamps ?? SignalLamps.Red);

    /// Lights a signal's lamps (any of them, all, or none), and builds its chunk again to show it.
    public void SetLamps(PlacedObject signal, SignalLamps lamps)
    {
        if (LampsOf(signal.Id) == lamps || _objectProjection is null)
            return;
        _lamps[signal.Id] = lamps;
        var p = _objectProjection.Forward(new GeoPoint(signal.Lat, signal.Lon));
        var key = KeyAt(p.East, -p.North);
        _stale.Add((key, false));
        _stale.Add((key, true));
    }
    private readonly Dictionary<long, float> _position = [];
    /// Which way each switch is set (the simulation's); the blades swing over to match.
    public Trains.SwitchStates Switches { get; set; } = new();
    private readonly HashSet<ChunkKey> _nearKeys = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastUpdate;

    public int DrawnChunks => _drawList.Count;
    public int Turnouts => _switches.Count;
    public int Building => _building.Count;

    public NetworkMeshes(TerrainSource source)
    {
        _source = source;
        _lines = source.Lines!;
        FindRuns();

        _tie = Model.Load("Track/tie.glb");
        _tieFar = Model.Load("Track/tie_far.glb");
        _plate = Model.Load("Track/plate.glb");
        _plateFar = BlockMesh(_plate.Mesh.BoundsMin, _plate.Mesh.BoundsMax);
        _ballast = Texture2D.Solid(new byte4(128, 128, 128, 255));
        _tieTexture = new TextureBinding(_tie.Surfaces[0].Texture ?? (_fallback = Texture2D.Solid(new byte4(40, 32, 26, 255))));
        _plateTexture = new TextureBinding(_plate.Surfaces[0].Texture ?? (_fallback ??= Texture2D.Solid(new byte4(60, 36, 24, 255))));
        _ballastTexture = new TextureBinding(_ballast);
        try
        {
            _kits[1] = Model.LoadParts("Track/switch_kit_r.glb");
            _kits[-1] = Model.LoadParts("Track/switch_kit_l.glb");
            _objectModels[PlacedKind.Signal] = Model.LoadParts("Track/signal.glb");
            _objectModels[PlacedKind.HandThrow] = Model.LoadParts("Track/hand_throw.glb");
            _objectModels[PlacedKind.GradeCrossing] = Model.LoadParts("Track/crossing_mast.glb");
            if (File.Exists(RailWerkz.Core.IO.ContentPath.Model("Track/bridge_kit.glb")))
                _bridgeKit = Model.LoadParts("Track/bridge_kit.glb");
        }
        catch (Exception e)
        {
            RailWerkz.Core.Logging.Log.Warn($"Switch kit: {e.Message}; switches are built without castings or machines");
        }

        try
        {
            foreach (var kind in Enum.GetValues<PlacedKind>().Where(k => k.IsPole()))
                _objectModels[kind] = Model.LoadParts($"Poles/{kind.PoleModel()}.glb");
        }
        catch (Exception e)
        {
            RailWerkz.Core.Logging.Log.Warn($"Poles: {e.Message}; poles without a model aren't drawn");
        }
    }

    private Dictionary<string, Model.Part>? KitFor(Turnout t) => _kits.GetValueOrDefault(t.Side > 0 ? 1 : -1);

    /// Every segment goes to the chunk its midpoint is in; consecutive ones in the same chunk make a run. Each turnout
    /// goes to the chunk its point of switch is in.
    private void FindRuns()
    {
        _turnoutsByChunk.Clear();
        _assembliesByChunk.Clear();
        foreach (var assembly in _lines.CrossingAssemblies)
        {
            var key = KeyAt(assembly.X, assembly.Z);
            if (!_assembliesByChunk.TryGetValue(key, out var list))
                _assembliesByChunk[key] = list = [];
            list.Add(assembly);
        }

        var seen = new HashSet<long>();
        foreach (var turnout in _lines.Turnouts)
        {
            var key = KeyAt(turnout.X, turnout.Z);
            if (!_turnoutsByChunk.TryGetValue(key, out var list))
                _turnoutsByChunk[key] = list = [];
            list.Add(turnout);
            seen.Add(turnout.Node);
            if (_switches.TryGetValue(turnout.Node, out var existing))
            {
                existing.Turnout = turnout;
                existing.Key = key;
                existing.BuiltAt = -1f;
            }
            else
                _switches[turnout.Node] = new Switch { Turnout = turnout, Key = key };
        }

        foreach (var (node, gone) in _switches.Where(s => !seen.Contains(s.Key)).ToList())
        {
            gone.Blades?.Dispose();
            _switches.Remove(node);
        }

        FindCrossovers();

        _runs.Clear();
        foreach (var line in _lines.Lines)
        {
            Run? run = null;
            ChunkKey runKey = default;
            for (var i = 0; i < line.Count - 1; i++)
            {
                var key = KeyAt((line.X[i] + line.X[i + 1]) * 0.5, (line.Z[i] + line.Z[i + 1]) * 0.5);
                if (run is { } r && key == runKey)
                {
                    run = r with { Last = i };
                    continue;
                }

                if (run is { } done)
                    AddRun(runKey, done);
                run = new Run(line.Index, i, i);
                runKey = key;
            }

            if (run is { } last)
                AddRun(runKey, last);
        }

        FindBridges();
    }

    /// Takes rebuilt lines in: the chunks over the given areas (world metres) are built again, the rest kept, and
    /// chunks no line crosses any more let go.
    public void Replace(NetworkLines lines, IEnumerable<(double X0, double Z0, double X1, double Z1)> areas)
    {
        _lines = lines;
        FindRuns();
        if (_objectProjection is { } projection)
            SetObjects(_objects, [], projection);

        foreach (var (x0, z0, x1, z1) in areas)
        {
            var a = KeyAt(x0 - 20.0, z0 - 20.0);
            var b = KeyAt(x1 + 20.0, z1 + 20.0);
            for (var kz = a.Z; kz <= b.Z; kz++)
            {
                for (var kx = a.X; kx <= b.X; kx++)
                {
                    _stale.Add((new ChunkKey(kx, kz), false));
                    _stale.Add((new ChunkKey(kx, kz), true));
                }
            }
        }

        foreach (var (key, chunk) in _chunks.Where(c => !_runs.ContainsKey(c.Key)).ToList())
        {
            chunk.Far?.Dispose();
            DropNear(chunk);
            _chunks.Remove(key);
        }
    }

    /// Takes the route's placed objects in, and builds again the chunks of those in changed (world metres).
    public void SetObjects(IReadOnlyList<PlacedObject> objects, IEnumerable<(double X, double Z)> changed, RouteProjection projection)
    {
        var gaps = new Dictionary<int, List<(float From, float To)>>();
        var covers = new Dictionary<int, List<(float From, float To)>>();
        (_objects, _objectProjection) = (objects, projection);
        _objectsByChunk.Clear();
        _handThrows.Clear();
        foreach (var o in objects)
        {
            var p = projection.Forward(new GeoPoint(o.Lat, o.Lon));
            var (x, z) = (p.East, -p.North);
            if (o.Kind == PlacedKind.HandThrow && o.Node is { } node)
            {
                _handThrows[node] = o;
                continue;
            }

            var key = KeyAt(x, z);
            if (!_objectsByChunk.TryGetValue(key, out var list))
                _objectsByChunk[key] = list = [];
            var deck = o.Kind == PlacedKind.GradeCrossing ? Deck(o, x, z) : null;
            list.Add(new PlacedAt(o, x, z, _source.HeightAt(x, z), deck));
            if (deck?.Gap is { } gap)
            {
                if (!gaps.TryGetValue(gap.Line, out var ofLine))
                    gaps[gap.Line] = ofLine = [];
                ofLine.Add((gap.From, gap.To));
                ofLine.Sort();
            }

            foreach (var (line, from, to) in deck?.Covered ?? [])
            {
                if (!covers.TryGetValue(line, out var ofLine))
                    covers[line] = ofLine = [];
                ofLine.Add((from, to));
            }
        }

        _crossingGaps = gaps;
        _crossingCovers = covers;
        foreach (var sw in _switches.Values)
            sw.BuiltAt = -1f;
        foreach (var (x, z) in changed)
        {
            var a = KeyAt(x - 80.0, z - 80.0);
            var b = KeyAt(x + 80.0, z + 80.0);
            for (var kz = a.Z; kz <= b.Z; kz++)
                for (var kx = a.X; kx <= b.X; kx++)
                {
                    _stale.Add((new ChunkKey(kx, kz), false));
                    _stale.Add((new ChunkKey(kx, kz), true));
                }
        }
    }

    private void AddRun(ChunkKey key, Run run)
    {
        if (!_runs.TryGetValue(key, out var list))
            _runs[key] = list = [];
        list.Add(run);
    }

    private static ChunkKey KeyAt(double x, double z) => new((int)Math.Floor(x / ChunkSize), (int)Math.Floor(z / ChunkSize));

    private static float3 OriginOf(ChunkKey k) => new(k.X * ChunkSize, 0f, k.Z * ChunkSize);

    // --- Choosing chunks. ---

    public void Update(double cx, double cy, double cz)
    {
        _frame++;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(0.25, now - _lastUpdate);
        _lastUpdate = now;
        for (var n = 0; n < MaxUploadsPerFrame && _built.TryDequeue(out var built); n++)
        {
            _building.Remove((built.Key, built.Near));
            if (!_chunks.TryGetValue(built.Key, out var chunk))
                _chunks[built.Key] = chunk = new Chunk { Used = _frame };

            var mesh = built.Indices.Length > 0 ? Mesh.Create<LineVertex>(built.Vertices, LineVertex.Layout, built.Indices, later: true) : null;
            if (mesh is not null)
                SetBounds(mesh, built.Vertices);
            if (built.Near)
            {
                chunk.Near?.Dispose();
                chunk.Near = mesh;
                chunk.NearFades = built.Fades;
                Release(chunk.Ties);
                chunk.Ties = built.Ties.Length > 0
                    ? Buffers.UploadLater(MemoryMarshal.AsBytes(built.Ties.AsSpan()), VkBufferUsageFlags.VertexBufferBit, "network.ties")
                    : default;
                chunk.TieCount = built.Ties.Length;
                Release(chunk.Plates);
                chunk.Plates = built.Plates.Length > 0
                    ? Buffers.UploadLater(MemoryMarshal.AsBytes(built.Plates.AsSpan()), VkBufferUsageFlags.VertexBufferBit, "network.plates")
                    : default;
                chunk.PlateCount = built.Plates.Length;
                chunk.TieCells = built.TieCells;
                chunk.PlateCells = built.PlateCells;
                chunk.HasNear = true;
            }
            else
            {
                chunk.Far?.Dispose();
                chunk.Far = mesh;
                chunk.FarFades = built.Fades;
                chunk.HasFar = true;
            }
        }

        _drawList.Clear();
        foreach (var key in _runs.Keys)
        {
            var distance = Distance(key, cx, cy, cz);
            if (distance > FarRadius * 1.1)
                continue;

            _chunks.TryGetValue(key, out var chunk);
            if (chunk is not null)
                chunk.Used = _frame;

            // Stale chunks keep drawing what they have until their rebuild comes in.
            if (chunk is null || !chunk.HasFar || _stale.Contains((key, false)))
                Request(key, false, distance);
            if (chunk is { HasFar: true } && distance < FarRadius)
                _drawList.Add((key, false));

            if (distance < NearRadius * 1.2)
            {
                if (chunk is null || !chunk.HasNear || _stale.Contains((key, true)))
                    Request(key, true, distance);
                if (chunk is { HasNear: true } && distance < NearRadius)
                    _drawList.Add((key, true));
            }
            else if (chunk is not null && distance > NearRadius * 1.5)
                DropNear(chunk);
        }

        StartBuilds();
        Evict();
        Swing(dt);
    }

    // --- Switches. ---

    /// Throws the switch nearest (x, z), if there's one within the given distance: its blades swing over to the other
    /// route over ThrowTime. Returns the turnout thrown.
    public Turnout? Throw(double x, double z, double within)
    {
        var nearest = _switches.Values.Select(s => s.Turnout).Where(t => Math.Abs(t.X - x) < within && Math.Abs(t.Z - z) < within)
            .MinBy(t => (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z));
        if (nearest is null)
            return null;
        Switches.Throw(nearest.Node);
        return nearest;
    }

    /// Sets a switch for one route, its blades swinging over if they're not there already: a train running through it
    /// trailing forces them over.
    public void Set(long node, bool reversed) => Switches.Set(node, reversed);

    /// Whether a switch is set (or going) for its reverse route.
    public bool Reversed(long node) => Switches.Reversed(node);

    /// Moves every moving switch's blades on, and makes the blades of the switches in near chunks that need it.
    private void Swing(float dt)
    {
        foreach (var (node, reversed) in Switches.All)
        {
            var target = reversed ? 1f : 0f;
            var position = _position.GetValueOrDefault(node);
            if (position != target)
                _position[node] = target > position ? MathF.Min(target, position + dt / ThrowTime) : MathF.Max(target, position - dt / ThrowTime);
        }

        _nearKeys.Clear();
        foreach (var (key, near) in _drawList)
            if (near)
                _nearKeys.Add(key);

        foreach (var (node, sw) in _switches)
        {
            if (!_nearKeys.Contains(sw.Key))
            {
                if (sw.Blades is not null && !_chunks.ContainsKey(sw.Key))
                {
                    sw.Blades.Dispose();
                    sw.Blades = null;
                    sw.BuiltAt = -1f;
                }

                continue;
            }

            var position = _position.GetValueOrDefault(node);
            if (sw.Blades is not null && sw.BuiltAt == position)
                continue;
            sw.Blades?.Dispose();
            var builder = new Builder { Origin = OriginOf(sw.Key) };
            Blades(builder, sw.Turnout, position);
            if (KitFor(sw.Turnout) is { } parts)
                KitMoving(builder, sw.Turnout, parts, position, _handThrows.GetValueOrDefault(node), _objectModels.GetValueOrDefault(PlacedKind.HandThrow));
            sw.Blades = Mesh.Create<LineVertex>([.. builder.Vertices], LineVertex.Layout, [.. builder.Indices], later: true);
            SetBounds(sw.Blades, builder.Vertices);
            sw.BuiltAt = position;
        }
    }

    private static double Distance(ChunkKey k, double cx, double cy, double cz)
    {
        var x0 = k.X * ChunkSize;
        var z0 = k.Z * ChunkSize;
        var dx = Math.Max(0.0, Math.Max(x0 - cx, cx - (x0 + ChunkSize)));
        var dz = Math.Max(0.0, Math.Max(z0 - cz, cz - (z0 + ChunkSize)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// Asks for a chunk to be built (again), distance metres from the camera.
    private void Request(ChunkKey key, bool near, double distance)
    {
        if (_building.Contains((key, near)))
            return;
        var priority = near ? distance : distance + NearRadius;
        if (!_wanted.TryGetValue((key, near), out var existing) || priority < existing)
            _wanted[(key, near)] = priority;
    }

    /// Hands the nearest wanted chunks to the job system once its last batch is done.
    private void StartBuilds()
    {
        if (_wanted.Count > 0 && !_jobs.Busy)
        {
            var lines = _lines;
            var batch = _wanted.OrderBy(w => w.Value).Take(_jobs.BatchSize).Select(w => w.Key).ToList();
            var jobs = new List<(Brutal.Concurrency.Jobs.IJob, int)>();
            foreach (var (key, near) in batch)
            {
                _building.Add((key, near));
                _stale.Remove((key, near));
                var runs = _runs[key];
                jobs.Add((new Job("line chunk", () => _built.Enqueue(near ? BuildNear(key, runs, lines) : BuildFar(key, runs, lines))), near ? 4 : 1));
            }

            _jobs.TryRun(jobs);
        }

        _wanted.Clear();
    }

    private void DropNear(Chunk chunk)
    {
        chunk.Near?.Dispose();
        chunk.Near = null;
        Release(chunk.Ties);
        chunk.Ties = default;
        chunk.TieCount = 0;
        Release(chunk.Plates);
        chunk.Plates = default;
        chunk.PlateCount = 0;
        chunk.HasNear = false;
    }

    private void Evict()
    {
        // A few a frame: freeing GPU buffers takes a fraction of a millisecond each.
        foreach (var (key, chunk) in _chunks.Where(c => _frame - c.Value.Used > 300).Take(4).ToList())
        {
            chunk.Far?.Dispose();
            DropNear(chunk);
            _chunks.Remove(key);
        }
    }

    // --- Building (worker threads). ---

    private sealed class Builder
    {
        public readonly List<LineVertex> Vertices = [];
        public readonly List<uint> Indices = [];
        public float3 Origin;

        /// position relative to Origin.
        public uint Add(float3 position, float3 normal, float4 data)
        {
            Vertices.Add(new LineVertex(position, normal, data));
            return (uint)(Vertices.Count - 1);
        }

        public void Quad(uint a, uint b, uint c, uint d)
        {
            Indices.AddRange([a, b, c, b, d, c]);
        }

        /// The indices with the triangles of any fade into the ground (Gravel, Data.w below zero) last, and how many those are.
        public (uint[] Indices, int Fades) Sorted()
        {
            bool Fade(uint index)
            {
                var data = Vertices[(int)index].Data;
                return data.W < 0f && ((int)(data.Z + 0.5f) & 7) == (int)LineMaterial.Gravel;
            }

            var solid = new List<uint>(Indices.Count);
            var fades = new List<uint>();
            for (var i = 0; i + 2 < Indices.Count; i += 3)
            {
                var into = Fade(Indices[i]) && Fade(Indices[i + 1]) && Fade(Indices[i + 2]) ? fades : solid;
                into.Add(Indices[i]);
                into.Add(Indices[i + 1]);
                into.Add(Indices[i + 2]);
            }

            return ([.. solid, .. fades], fades.Count);
        }
    }

    /// A centreline frame at a point: the point (relative to origin) and the horizontal right.
    private static (float3 Center, float3 Right) Frame(NetworkLine line, int p, float3 origin)
    {
        var a = Math.Max(p - 1, 0);
        var b = Math.Min(p + 1, line.Count - 1);
        var fx = (float)(line.X[b] - line.X[a]);
        var fz = (float)(line.Z[b] - line.Z[a]);
        var length = MathF.Max(MathF.Sqrt(fx * fx + fz * fz), 1e-5f);
        fx /= length;
        fz /= length;
        var center = new float3((float)(line.X[p] - origin.X), line.Y[p], (float)(line.Z[p] - origin.Z));
        return (center, new float3(-fz, 0f, fx));
    }

    /// How far either side of a road's point the way it runs there is taken over: a few centimetres' wander between neighbouring
    /// points, turned into the direction across, would swing a wide road's edges and lines from side to side.
    private const float RoadFrameReach = 6f;

    /// A road's frame at a point, its direction across taken from RoadFrameReach either side.
    private static (float3 Center, float3 Right) WideFrame(NetworkLine line, int p, float3 origin)
    {
        var (a, b) = (p, p);
        while (a > 0 && line.S[p] - line.S[a] < RoadFrameReach)
            a--;
        while (b < line.Count - 1 && line.S[b] - line.S[p] < RoadFrameReach)
            b++;
        if (a == b)
            return Frame(line, p, origin);
        var fx = (float)(line.X[b] - line.X[a]);
        var fz = (float)(line.Z[b] - line.Z[a]);
        var length = MathF.Max(MathF.Sqrt(fx * fx + fz * fz), 1e-5f);
        var center = new float3((float)(line.X[p] - origin.X), line.Y[p], (float)(line.Z[p] - origin.Z));
        return (center, new float3(-fz / length, 0f, fx / length));
    }

    private static float Code(LineMaterial material, int style = 0) => (int)material + 8 * style;

    /// Sweeps a cross-section (u across, y up from the formation; listed so the solid is on the right) along points
    /// first..last, flat-shaded across and smooth along. material(edge) picks each edge's material; value(edge, u, y)
    /// its Data.w.
    private static void Sweep(Builder builder, NetworkLine line, int first, int last, (float U, float Y)[] section, Func<int, float> code,
        Func<int, float, float, float> value, float offset = 0f, float rise = 0f, bool smooth = false, bool capped = false) =>
        Sweep(builder, line, Enumerable.Range(first, last - first + 1).ToList(), section, code, value, offset, rise, smooth, capped);

    /// As above, with rings at only these points.
    private static void Sweep(Builder builder, NetworkLine line, List<int> points, (float U, float Y)[] section, Func<int, float> code,
        Func<int, float, float, float> value, float offset = 0f, float rise = 0f, bool smooth = false, bool capped = false)
    {
        var edges = section.Length - 1;
        // A closed section (its last corner its first) closed off at each end too, where asked.
        if (capped && points.Count >= 2 && section.Length >= 4 && section[0] == section[^1])
        {
            foreach (var (p, outward) in (ReadOnlySpan<(int, float)>)[(points[0], -1f), (points[^1], 1f)])
            {
                var (center, right) = Frame(line, p, builder.Origin);
                var forward = new float3(right.Z, 0f, -right.X) * outward;
                var corners = section[..^1];
                var first = (uint)builder.Vertices.Count;
                foreach (var (u, y) in corners)
                    builder.Add(center + right * (offset + u) + new float3(0f, rise + y, 0f), forward, new float4(offset + u, line.S[p] % Wrap, code(0), value(0, u, y)));
                // (Both ways round: a cap is seen from outside whichever way the section was drawn.)
                foreach (var (a, b, c) in Ears(corners))
                    builder.Indices.AddRange([first + (uint)a, first + (uint)b, first + (uint)c, first + (uint)a, first + (uint)c, first + (uint)b]);
            }
        }

        var start = (uint)builder.Vertices.Count;
        foreach (var p in points)
        {
            var (center, right) = Frame(line, p, builder.Origin);
            var v = line.S[p] % Wrap;
            for (var e = 0; e < edges; e++)
            {
                var (u0, y0) = section[e];
                var (u1, y1) = section[e + 1];
                var du = u1 - u0;
                var dy = y1 - y0;
                var len = MathF.Max(MathF.Sqrt(du * du + dy * dy), 1e-6f);
                var normal = right * (-dy / len) + new float3(0f, du / len, 0f);
                var (n0, n1) = smooth ? JoinNormals(section, e, right) : (normal, normal);
                builder.Add(center + right * (offset + u0) + new float3(0f, rise + y0, 0f), n0, new float4(offset + u0, v, code(e), value(e, u0, y0)));
                builder.Add(center + right * (offset + u1) + new float3(0f, rise + y1, 0f), n1, new float4(offset + u1, v, code(e), value(e, u1, y1)));
            }
        }

        var ring = (uint)(edges * 2);
        for (var p = 0; p < points.Count - 1; p++)
        {
            for (var e = 0; e < edges; e++)
            {
                var a = start + (uint)p * ring + (uint)e * 2;
                builder.Quad(a, a + 1, a + ring, a + ring + 1);
            }
        }
    }

    /// A simple outline cut into triangles, by its corners' indices (ear clipping).
    private static List<(int A, int B, int C)> Ears((float U, float Y)[] outline)
    {
        var left = Enumerable.Range(0, outline.Length).ToList();
        var area = 0f;
        for (var i = 0; i < outline.Length; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Length]);
            area += a.U * b.Y - b.U * a.Y;
        }

        var turn = MathF.Sign(area);
        static float Cross((float U, float Y) a, (float U, float Y) b, (float U, float Y) c) => (b.U - a.U) * (c.Y - a.Y) - (b.Y - a.Y) * (c.U - a.U);
        var triangles = new List<(int, int, int)>();
        var guard = outline.Length * outline.Length;
        while (left.Count > 3 && guard-- > 0)
        {
            for (var k = 0; k < left.Count; k++)
            {
                var (ia, ib, ic) = (left[(k + left.Count - 1) % left.Count], left[k], left[(k + 1) % left.Count]);
                var (a, b, c) = (outline[ia], outline[ib], outline[ic]);
                if (Cross(a, b, c) * turn <= 1e-6f)
                    continue;
                var inside = false;
                foreach (var j in left)
                {
                    if (j == ia || j == ib || j == ic)
                        continue;
                    var q = outline[j];
                    if (Cross(a, b, q) * turn > 0f && Cross(b, c, q) * turn > 0f && Cross(c, a, q) * turn > 0f)
                    {
                        inside = true;
                        break;
                    }
                }

                if (inside)
                    continue;
                triangles.Add((ia, ib, ic));
                left.RemoveAt(k);
                break;
            }
        }

        if (left.Count == 3)
            triangles.Add((left[0], left[1], left[2]));
        return triangles;
    }

    /// The normals at the ends of a cross-section's edge, each halfway to its neighbour's, so a curved section shades as a curve.
    private static (float3 Start, float3 End) JoinNormals((float U, float Y)[] section, int e, float3 right)
    {
        float3 Of(int i)
        {
            var (du, dy) = (section[i + 1].U - section[i].U, section[i + 1].Y - section[i].Y);
            var len = MathF.Max(MathF.Sqrt(du * du + dy * dy), 1e-6f);
            return right * (-dy / len) + new float3(0f, du / len, 0f);
        }

        var own = Of(e);
        var start = e > 0 ? MathEx.Normalize(own + Of(e - 1)) : own;
        var end = e + 2 < section.Length ? MathEx.Normalize(own + Of(e + 1)) : own;
        return (start, end);
    }

    /// How far out from its toe the ballast fades into the ground, and how far above the ground it ends up, clear of its unevenness.
    private const float Fading = NetworkLine.BedFade;
    private const float GivesOut = 0.04f;

    /// Runs split where the structure changes, each with points first..last (inclusive).
    private static IEnumerable<(int First, int Last, Structure Structure)> Spans(NetworkLine line, Run run)
    {
        var start = run.First;
        for (var s = run.First; s <= run.Last; s++)
        {
            if (s == run.Last || line.Structures[s + 1] != line.Structures[s])
            {
                yield return (start, s + 1, line.Structures[s]);
                start = s + 1;
            }
        }
    }

    private Built BuildFar(ChunkKey key, List<Run> runs, NetworkLines lines)
    {
        var builder = new Builder { Origin = OriginOf(key) };
        if (_objectsByChunk.TryGetValue(key, out var placed))
            foreach (var crossing in placed.Where(p => p.Object.Kind == PlacedKind.GradeCrossing))
                CrossingSurface(builder, crossing);
        foreach (var run in runs)
        {
            var line = lines.Lines[run.Line];
            foreach (var (first, last, structure) in Spans(line, run))
            {
                if (structure == Structure.Tunnel)
                    continue;

                // A mapped track's bridge, once track's been laid across it, isn't needed either.
                if (line.Mark && structure == Structure.Bridge && Enumerable.Range(first, last - first + 1).All(p => lines.UnderTrack(line.X[p], line.Z[p], MarkCovered)))
                    continue;

                if (line.Rail)
                    FarRail(builder, lines, line, first, last, structure == Structure.Bridge);
                else if (line.Mark && structure != Structure.Bridge)
                    Mark(builder, lines, line, first, last);
                else if (line.Mark)
                    // A guide over a bridge: its strip of gravel along the deck (the bridge itself is its whole structure's, below).
                    Sweep(builder, line, first, last, [(-line.Width * 0.5f, 0.07f), (line.Width * 0.5f, 0.07f)], _ => Code(LineMaterial.Gravel), (_, _, _) => -1f);
                else
                    Road(builder, lines, line, first, last, structure == Structure.Bridge);
            }
        }

        if (_bridgesByChunk.TryGetValue(key, out var bridges))
            foreach (var bridge in bridges)
                BuildBridge(builder, bridge);

        if (_turnoutsByChunk.TryGetValue(key, out var turnouts))
            foreach (var turnout in turnouts)
                SwitchBed(builder, turnout, lines.Turnouts);

        var (indices, fades) = builder.Sorted();
        return new Built(key, false, [.. builder.Vertices], indices, fades, [], [], [], []);
    }

    private static void FarRail(Builder builder, NetworkLines lines, NetworkLine line, int first, int last, bool bridge)
    {
        if (line.Embedded)
        {
            // Street track laid by hand has a pavement of its own; a mapped tramway's already in its road.
            if (line.Bed == TrackBed.Street)
                Sweep(builder, line, first, last, [(-1.9f, RoadRise + 0.012f), (1.9f, RoadRise + 0.012f)], _ => Code(LineMaterial.Asphalt, 2 + (int)MathF.Round(1.9f * 8f) * 64), (_, _, _) => 1000f);
            return;
        }

        // On sunken track the rock's only solid between the rails and starts to give way under the ties' ends.
        var sunken = line.Bed == TrackBed.Sunken && !bridge;
        var w = sunken ? line.BallastHalfTop - 0.7f : line.BallastHalfTop;
        var fading = sunken ? Fading * 1.4f : Fading;
        var h = NetworkLine.BallastHeight;
        var railOffset = line.Gauge * 0.5f + RailHeadHalf;
        // (A bed this low lies on the ground: it's told to cast no shadow, see shadow_line.vert.)
        var ballast = Code(LineMaterial.Ballast, line.Tracks + (line.Disused ? 8 : 0) + (!bridge && line.BedHeight < 0.12f ? 16 : 0));

        if (!bridge)
        {
            // The ballast, heaped as loose rock lies: rounded over from the top, down its slope, and easing out onto the ground, where
            // the last of it fades in among the ground's own. The lower the bed, the shorter its slope.
            const float walk = 0.02f;
            const int curve = 8;
            const int edges = curve * 2 + 5;
            // The solid rock goes on from its toe to just under the ground, so there's no step for the ambient occlusion to find.
            const float tapers = 0.6f;
            var gravel = Code(LineMaterial.Gravel);
            float Edge(int e) => e == 0 || e == edges - 1 ? gravel : ballast;

            // From the shoulder (t = 0) to the toe (t = 1): more of the way spent on the long easing out than on rounding over the top.
            static float Fall(float t)
            {
                var eased = MathF.Pow(t, 0.72f);
                return 1f - eased * eased * (3f - 2f * eased);
            }

            (float U, float Y)[] Section(float left, float right, float drop, float sink)
            {
                var toeY = sink + walk;
                var run = MathF.Max(0.35f, (h - drop - toeY) * NetworkLine.BallastSlope * NetworkLine.BedRun);
                // Under what Conform leaves the ground at.
                var underY = sink - MathF.Min(sink * 0.3f, 0.06f) - 0.03f;
                var points = new (float, float)[curve * 2 + 6];
                var n = 0;
                points[n++] = (-w - left - run - fading, sink + GivesOut);
                points[n++] = (-w - left - run - tapers, underY);
                points[n++] = (-w - left - run, toeY);
                for (var i = curve - 1; i >= 0; i--)
                {
                    var t = i / (float)curve;
                    points[n++] = (-w - left - run * t, toeY + (h - drop - toeY) * Fall(t));
                }

                for (var i = 0; i < curve; i++)
                {
                    var t = i / (float)curve;
                    points[n++] = (w + right + run * t, toeY + (h - drop - toeY) * Fall(t));
                }

                points[n++] = (w + right + run, toeY);
                points[n++] = (w + right + run + tapers, underY);
                points[n] = (w + right + run + fading, sink + GivesOut);
                return points;
            }

            // Through a turnout the normal route's bed widens out to the reverse route's far side. The reverse route lays its own
            // bed all the same, just under it where they overlap, so whatever the turnouts around do there's always ballast
            // under the track.
            // (A switch with a surface of its own takes its routes' beds over instead: see SwitchBed.)
            var zones = line.TurnoutZones.Where(z => z.Ballast.To > z.Ballast.From && !SwitchBedOf(z.Turnout).Ok).ToList();
            var cuts = SwitchBedCuts(line);
            var widen = zones.Where(z => z.Normal).ToList();
            var under = zones.Where(z => !z.Normal).Select(z => z.Ballast).ToList();
            var depth = 0.003f * (1 + line.Index % 5);
            var at = new List<float> { line.S[first], line.S[last] };
            for (var p = first + 1; p < last; p++)
                at.Add(line.S[p]);
            foreach (var zone in widen)
            {
                for (var s = zone.Ballast.From; s <= zone.Ballast.To; s += Turnout.SpreadStep)
                    at.Add(s);
                at.Add(zone.Ballast.From - 0.001f);
                at.Add(zone.Ballast.To);
                at.Add(zone.Ballast.To + 0.001f);
            }

            foreach (var (from, to) in under)
                at.AddRange((float[])[from - 0.5f, from, to, to + 0.5f]);

            foreach (var (from, to) in cuts)
                at.AddRange((float[])[from, to]);

            // The bed in pieces, broken where a switch's surface takes over.
            bool Cut(float a, float b) => cuts.Any(c => a < c.To - 1e-3f && b > c.From + 1e-3f);
            var kept = at.Where(s => s >= line.S[first] && s <= line.S[last] && !cuts.Any(c => s > c.From + 1e-3f && s < c.To - 1e-3f)).Order().Distinct().ToList();
            var pieces = new List<List<float>>();
            for (var i = 0; i < kept.Count; i++)
            {
                if (i == 0 || Cut(kept[i - 1], kept[i]))
                    pieces.Add([]);
                pieces[^1].Add(kept[i]);
            }

            foreach (var piece in pieces)
            {
            var start = (uint)builder.Vertices.Count;
            var rings = 0;
            foreach (var s in piece)
            {
                var (left, right) = (0f, 0f);
                foreach (var zone in widen)
                {
                    if (s < zone.Ballast.From || s > zone.Ballast.To)
                        continue;
                    var t = zone.Turnout;
                    var extra = t.SpreadAt(MathF.Abs(s - t.Normal.Start));
                    if (t.Side * t.Normal.Direction > 0)
                        right = MathF.Max(right, extra);
                    else
                        left = MathF.Max(left, extra);
                }

                var drop = 0f;
                foreach (var (from, to) in under)
                    drop = MathF.Max(drop, depth * Math.Clamp(MathF.Min(s - from + 0.5f, to + 0.5f - s) / 0.5f, 0f, 1f));

                var (center, across) = FrameAt(line, s, builder.Origin);
                var profile = Section(left, right, drop, line.SinkAt(s));
                for (var e = 0; e < edges; e++)
                {
                    var (n0, n1) = JoinNormals(profile, e, across);
                    var (u0, y0) = profile[e];
                    var (u1, y1) = profile[e + 1];
                    // The fade runs on in under the taper, as far as the toe: it's drawn first, so the solid rock lies over it.
                    if (e == 0)
                        (u1, y1) = profile[2];
                    else if (e == edges - 1)
                        (u0, y0) = profile[e - 1];
                    // Data.w: the rail centres' offset for the shader's rock, or in the fade how far in from where it gives out.
                    var (w0, w1) = e == 0 ? (-0.3f, -Fading - 0.3f) : e == edges - 1 ? (-Fading - 0.3f, -0.3f) : (railOffset, railOffset);
                    builder.Add(center + across * u0 + new float3(0f, y0, 0f), n0, new float4(u0, s % Wrap, Edge(e), w0));
                    builder.Add(center + across * u1 + new float3(0f, y1, 0f), n1, new float4(u1, s % Wrap, Edge(e), w1));
                }

                rings++;
            }

            var ring = (uint)(edges * 2);
            for (var r = 0; r < rings - 1; r++)
            {
                for (var e = 0; e < edges; e++)
                {
                    var q = start + (uint)r * ring + (uint)e * 2;
                    builder.Quad(q, q + 1, q + ring, q + ring + 1);
                }
            }
            }

            // Where the track stops, the bed's own section's turned round its end, so the cap's the same shape, shade and fade as the bed.
            // (At a bridge it stops against the abutment's retaining walls instead: see Piers.)
            void BedEnd(float at, float way)
            {
                const int steps = 8;
                var (centre, side) = FrameAt(line, at, builder.Origin);
                var ahead = new float3(side.Z, 0f, -side.X) * way;
                var profile = Section(0f, 0f, 0f, line.SinkAt(at));
                var outside = profile.Skip(curve + 3).ToArray();
                var section = new (float U, float Y)[outside.Length + 1];
                section[0] = (0f, outside[0].Y);
                outside.CopyTo(section, 1);
                var count = section.Length - 1;
                var toe = outside[^3];
                // (No tracks to paint on it from afar, unlike the bed.)
                var rock = Code(LineMaterial.Ballast, (line.Disused ? 8 : 0) + (line.BedHeight < 0.12f ? 16 : 0));
                foreach (var hand in (float[])[-1f, 1f])
                {
                    var first2 = (uint)builder.Vertices.Count;
                    for (var i = 0; i <= steps; i++)
                    {
                        var (sin, cos) = MathF.SinCos(MathF.PI * 0.5f * i / steps);
                        var radial = side * (hand * cos) + ahead * sin;
                        for (var e = 0; e < count; e++)
                        {
                            var (n0, n1) = JoinNormals(section, e, radial);
                            var (p0, p1) = (section[e], section[e + 1]);
                            var fade = e == count - 1;
                            // The fade runs on in under the taper, as the bed's does.
                            if (fade)
                                p0 = toe;
                            var (w0, w1) = fade ? (-Fading - 0.3f, -0.3f) : (railOffset, railOffset);
                            var code = fade ? gravel : rock;
                            foreach (var (point, normal, value) in (ReadOnlySpan<((float U, float Y), float3, float)>)[(p0, n0, w0), (p1, n1, w1)])
                            {
                                builder.Add(centre + radial * point.U + new float3(0f, point.Y, 0f), normal,
                                    new float4(hand * point.U * cos, (at + way * point.U * sin) % Wrap, code, value));
                            }
                        }
                    }

                    var round = (uint)(count * 2);
                    for (var r = 0u; r < steps; r++)
                        for (var e = 0u; e < count; e++)
                        {
                            var q = first2 + r * round + e * 2;
                            builder.Quad(q, q + 1, q + round, q + round + 1);
                        }
                }
            }

            if (first == 0 && !lines.TrackContinues(line, line.X[first], line.Z[first]))
                BedEnd(line.S[first], -1f);
            if (last == line.Count - 1 && !lines.TrackContinues(line, line.X[last], line.Z[last]))
                BedEnd(line.S[last], 1f);

            return;
        }

        // Its ballast on a concrete slab as wide as its share of the bridge's deck (the bridge's edges, railings and the deck between
        // tracks are its whole structure's, NetworkMeshes.BridgeGroups.cs), closed off at each end.
        var concrete = Code(LineMaterial.Concrete);
        var slab = BridgePlanner.DeckHalf(line);
        (float, float)[] section =
        [
            (-slab, -0.25f), (-slab, 0.05f), (-w - 0.55f, 0.05f), (-w, h), (w, h), (w + 0.55f, 0.05f), (slab, 0.05f), (slab, -0.25f), (-slab, -0.25f),
        ];
        Sweep(builder, line, first, last, section, e => e is 2 or 3 or 4 ? ballast : concrete, (_, _, _) => railOffset, capped: true);
    }

    /// How near a mark's middle built track has to run for the mark to go, in metres: less than tracks side by side are apart.
    private const double MarkCovered = 3.0;

    /// A mapped track's mark: a strip of gravel lying on the ground, following it across as well as along.
    private static void Mark(Builder builder, NetworkLines lines, NetworkLine line, int first, int last)
    {
        const int across = NetworkLine.MarkPoints;
        var gravel = Code(LineMaterial.Gravel);

        // Where track's been laid along it, there's no more mark: only the points of what's left, and one over at each end of that.
        var covered = new bool[last - first + 1];
        for (var p = first; p <= last; p++)
            covered[p - first] = lines.UnderTrack(line.X[p], line.Z[p], MarkCovered);
        bool Kept(int p) => p >= first && p <= last && !covered[p - first];
        var rings = new uint[last - first + 1];
        for (var p = first; p <= last; p++)
        {
            if (!Kept(p) && !Kept(p - 1) && !Kept(p + 1))
                continue;
            rings[p - first] = (uint)builder.Vertices.Count;
            var (center, right) = Frame(line, p, builder.Origin);
            var forward = new float3(right.Z, 0f, -right.X);
            var (behind, ahead) = (Math.Max(p - 1, 0), Math.Min(p + 1, line.Count - 1));
            var run = MathF.Max(line.S[ahead] - line.S[behind], 1e-3f);
            var rise = (line.Drape[ahead * across + across / 2] - line.Drape[behind * across + across / 2]) / run;
            var tilt = (line.Drape[p * across + across - 1] - line.Drape[p * across]) / MathF.Max(line.Width, 1e-3f);
            var normal = new float3(0f, 1f, 0f) - right * tilt - forward * rise;
            normal /= MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            var v = line.S[p] % Wrap;
            for (var k = 0; k < across; k++)
            {
                var u = line.Width * ((float)k / (across - 1) - 0.5f);
                var position = new float3(center.X + right.X * u, line.Drape[p * across + k] + RoadRise, center.Z + right.Z * u);
                // How far in from the edge, so a mark's edge thins out like the ballast's.
                builder.Add(position, normal, new float4(u, v, gravel, -(line.Width * 0.5f - MathF.Abs(u)) - 0.001f));
            }
        }

        for (var p = first; p < last; p++)
        {
            if (!Kept(p) && !Kept(p + 1))
                continue;
            for (var k = 0; k < across - 1; k++)
            {
                var (a, b) = (rings[p - first] + (uint)k, rings[p + 1 - first] + (uint)k);
                builder.Quad(a, a + 1, b, b + 1);
            }
        }

        if (first == 0 && Kept(0))
            EndPatch(builder, lines, line, 0, gravel);
        if (last == line.Count - 1 && Kept(last))
            EndPatch(builder, lines, line, last, gravel);
    }

    /// What a road's top is drawn in: its pavement with its markings, or gravel.
    private static float RoadSurface(NetworkLine line, int marks) => line.Unpaved
        ? Code(LineMaterial.Gravel)
        : Code(LineMaterial.Asphalt, line.Lanes + (line.Oneway ? 8 : 0) + marks * 16 + (int)MathF.Round(line.Width * 4f) * 64);

    /// A road, but for the stretches a crossing's deck takes the place of.
    private void Road(Builder builder, NetworkLines lines, NetworkLine line, int first, int last, bool bridge)
    {
        var start = first;
        if (!bridge && _crossingGaps.TryGetValue(line.Index, out var gaps))
            foreach (var (from, to) in gaps)
            {
                // From the first point on the deck's ramp to the last, so the road's end lies just under the ramp's.
                var (inFrom, inTo) = (-1, -1);
                for (var p = start; p <= last; p++)
                    if (line.S[p] >= from && line.S[p] <= to)
                        (inFrom, inTo) = (inFrom < 0 ? p : inFrom, p);
                if (inFrom < 0)
                    continue;
                if (inFrom > start)
                    RoadStretch(builder, lines, line, start, inFrom, false);
                start = inTo;
            }

        if (start < last)
            RoadStretch(builder, lines, line, start, last, bridge);
    }

    private static void RoadStretch(Builder builder, NetworkLines lines, NetworkLine line, int first, int last, bool bridge)
    {
        var half = line.Width * 0.5f;
        var taper = RoadTaper(lines, line);
        var marks = line.Unpaved ? 0 : (RoadClass)line.Class switch
        {
            RoadClass.Highway => 3,
            RoadClass.Major => 2,
            RoadClass.Minor => 1,
            _ => 0,
        };
        var surface = RoadSurface(line, marks);
        float Junction(int p) => line.Junction.Length > p ? MathF.Min(line.Junction[p], 1000f) : 1000f;

        // Data.w is per point, so the sweep's value callback can't see it; patch it in after.
        var start = builder.Vertices.Count;
        var rings = RoadRings(line, first, last, Junction, taper);
        if (!bridge)
        {
            RoadSweep(builder, lines, line, rings, taper, h => [(-h, RoadRise), (h, RoadRise)], _ => surface);
        }
        else
        {
            var concrete = Code(LineMaterial.Concrete);
            // A slab with a low curb each side. Its steel's in BridgeSteel.
            var (_, _, curb) = DeckOf(line);
            RoadSweep(builder, lines, line, rings, taper, h =>
            [
                (-h - 0.55f, -0.25f), (-h - 0.55f, curb), (-h, curb), (-h, RoadRise), (h, RoadRise), (h, curb),
                (h + 0.55f, curb), (h + 0.55f, -0.25f), (-h - 0.55f, -0.25f),
            ], e => e == 3 ? surface : concrete);
        }

        var perRing = (builder.Vertices.Count - start) / rings.Count;

        // The shoulders, separate from the surface: they start a little in from its edge and just under it, so they stay under
        // any road that crosses this one.
        if (!bridge)
        {
            const float under = RoadRise - 0.02f;
            var gravel = Code(LineMaterial.Gravel);
            RoadSweep(builder, lines, line, rings, taper, h => [(-h - 0.6f, -0.35f), (-h + 0.05f, under)], _ => gravel);
            RoadSweep(builder, lines, line, rings, taper, h => [(h - 0.05f, under), (h + 0.6f, -0.35f)], _ => gravel);
        }

        for (var r = 0; r < rings.Count; r++)
        {
            var junction = Junction(rings[r]);
            for (var k = 0; k < perRing; k++)
            {
                var index = start + r * perRing + k;
                var v = builder.Vertices[index];
                v.Data.W = junction;
                builder.Vertices[index] = v;
            }
        }

        if (!bridge)
        {
            if (first == 0)
                EndPatch(builder, lines, line, 0, surface);
            if (last == line.Count - 1)
                EndPatch(builder, lines, line, last, surface);
        }
    }

    /// The half width of a road at each point. Where it carries on into a road of another width, both ease to the width halfway
    /// between them over the last stretch, so the edge runs on without a step.
    private static Func<int, float> RoadTaper(NetworkLines lines, NetworkLine line)
    {
        var half = line.Width * 0.5f;
        var length = line.S[line.Count - 1];
        var ends = new List<(float From, float Reach, float Change)>();
        foreach (var point in (int[])[0, line.Count - 1])
        {
            if (lines.Continuing(line, point) is not { } other || MathF.Abs(other.Line.Width - line.Width) < 0.01f)
                continue;
            var change = (other.Line.Width - line.Width) * 0.25f;
            // Long enough to read as a road widening, and it never reaches past the road's middle.
            var reach = MathF.Min(MathF.Max(60f, MathF.Abs(change) * 30f), length * 0.5f);
            ends.Add((line.S[point], reach, change));
        }

        if (ends.Count == 0)
            return _ => half;
        return p =>
        {
            var at = half;
            foreach (var (from, reach, change) in ends)
            {
                var t = Math.Clamp(1f - MathF.Abs(line.S[p] - from) / MathF.Max(reach, 1e-3f), 0f, 1f);
                at += change * t * t * (3f - 2f * t);
            }

            return at;
        };
    }

    /// A road's frame at a point: at an end that carries on into another road, square to the two of them, so they meet edge to edge.
    private static (float3 Center, float3 Right) RoadFrame(NetworkLines lines, NetworkLine line, int p, float3 origin)
    {
        var (center, right) = WideFrame(line, p, origin);
        if (p < line.Shift.Length)
            center += right * line.Shift[p];
        if ((p != 0 && p != line.Count - 1) || lines.Continuing(line, p) is not { } other)
            return (center, right);

        var (_, theirs) = Frame(other.Line, other.Point, origin);
        // Running the same way along the join when one line's end meets the other's start; the opposite when ends meet ends.
        if ((p == 0) == (other.Point == 0))
            theirs = new float3(-theirs.X, 0f, -theirs.Z);
        var sum = right + theirs;
        return (center, sum / MathF.Max(MathF.Sqrt(sum.X * sum.X + sum.Z * sum.Z), 1e-5f));
    }

    /// Sweeps a road's cross-section, made for the half width at each ring. The markings' position across is kept in proportion to
    /// the nominal width, so lane lines narrow with the road rather than running off its edge.
    private static void RoadSweep(Builder builder, NetworkLines lines, NetworkLine line, List<int> points, Func<int, float> halfAt,
        Func<float, (float U, float Y)[]> section, Func<int, float> code)
    {
        var nominal = line.Width * 0.5f;
        // Where the edge lines are painted in from each edge (as line.frag lays them out): the markings are laid out for the
        // nominal width, so where the road's narrower or wider (easing into the next) they're moved and stretched to keep the edge
        // lines that far in from the real edges, and so carry on unbroken into the next road's.
        var marks = line.Unpaved ? 0 : (RoadClass)line.Class switch { RoadClass.Highway => 3, RoadClass.Major => 2, RoadClass.Minor => 1, _ => 0 };
        var (inner, outer) = marks == 3 ? (1.2f, 3.0f) : (0.5f, 0.5f);
        var leftIn = line.Oneway ? inner : outer;
        var start = (uint)builder.Vertices.Count;
        var edges = 0;
        foreach (var p in points)
        {
            var (center, right) = RoadFrame(lines, line, p, builder.Origin);
            var half = halfAt(p);
            var (scale, shift) = (nominal / MathF.Max(half, 0.1f), 0f);
            if (marks >= 2 && half - outer - (-half + leftIn) > 0.5f && nominal - outer - (-nominal + leftIn) > 0.5f)
            {
                scale = (nominal - outer - (-nominal + leftIn)) / (half - outer - (-half + leftIn));
                shift = nominal - outer - scale * (half - outer);
            }
            var ring = section(half);
            edges = ring.Length - 1;
            var v = line.S[p] % Wrap;
            for (var e = 0; e < edges; e++)
            {
                var (u0, y0) = ring[e];
                var (u1, y1) = ring[e + 1];
                var (du, dy) = (u1 - u0, y1 - y0);
                var len = MathF.Max(MathF.Sqrt(du * du + dy * dy), 1e-6f);
                var normal = right * (-dy / len) + new float3(0f, du / len, 0f);
                builder.Add(center + right * u0 + new float3(0f, y0, 0f), normal, new float4(u0 * scale + shift, v, code(e), 0f));
                builder.Add(center + right * u1 + new float3(0f, y1, 0f), normal, new float4(u1 * scale + shift, v, code(e), 0f));
            }
        }

        var rings = (uint)(edges * 2);
        for (var p = 0; p < points.Count - 1; p++)
        {
            for (var e = 0; e < edges; e++)
            {
                var a = start + (uint)p * rings + (uint)e * 2;
                builder.Quad(a, a + 1, a + rings, a + rings + 1);
            }
        }
    }

    /// The points of a road that need a ring: the rest lie on the straight between their neighbours, across the ground and
    /// in height, closely enough not to show, and their markings' distance from a junction and its width run evenly. No span
    /// crosses where the texture's distance wraps.
    private static List<int> RoadRings(NetworkLine line, int first, int last, Func<int, float> junction, Func<int, float> halfAt)
    {
        const double across = 0.03;
        const float height = 0.015f;
        const float marking = 0.25f;
        const int longest = 100;

        var kept = new List<int> { first };
        var from = first;
        for (var to = first + 2; to <= last; to++)
        {
            var (dx, dz) = (line.X[to] - line.X[from], line.Z[to] - line.Z[from]);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
            // Where the width's changing, rings close together: the markings are stretched to it ring by ring, and over a long
            // quad its two triangles would bend them at the diagonal.
            var fits = to - from <= longest && (int)(line.S[to] / Wrap) == (int)(line.S[from] / Wrap) && MathF.Abs(halfAt(to) - halfAt(from)) <= 0.03f;
            for (var q = from + 1; q < to && fits; q++)
            {
                var t = (line.S[q] - line.S[from]) / MathF.Max(line.S[to] - line.S[from], 1e-5f);
                fits = Math.Abs((line.X[q] - line.X[from]) * dz - (line.Z[q] - line.Z[from]) * dx) / length <= across &&
                       MathF.Abs(line.Y[q] - (line.Y[from] + (line.Y[to] - line.Y[from]) * t)) <= height &&
                       MathF.Abs(junction(q) - (junction(from) + (junction(to) - junction(from)) * t)) <= marking &&
                       MathF.Abs(halfAt(q) - (halfAt(from) + (halfAt(to) - halfAt(from)) * t)) <= 0.04f;
            }

            if (fits)
                continue;
            from = to - 1;
            kept.Add(from);
        }

        if (kept[^1] != last)
            kept.Add(last);
        return kept;
    }

    /// The surface between road ends meeting at a node, where they'd otherwise leave a wedge: the outline around their end
    /// corners, as few triangles as it has corners less two, and nothing at all where they meet in line or a road just stops.
    /// Laid by the first of the ends, a hair below the roads so their own surfaces win; no markings (junction distance 0).
    private static void EndPatch(Builder builder, NetworkLines lines, NetworkLine line, int point, float code)
    {
        var ends = lines.EndsMeetingAt(line, point);
        if (ends.Count < 2 || ends[0].Line != line || ends[0].Point != point || lines.Continuing(line, point) is not null)
            return;

        var corners = new List<float3>(ends.Count * 2);
        var rise = RoadRise - 0.01f;
        foreach (var (other, p) in ends)
        {
            var (center, right) = Frame(other, p, builder.Origin);
            if (p < other.Shift.Length)
                center += right * other.Shift[p];
            var half = other.Width * 0.5f;
            var (near, far) = other.Mark
                ? (other.Drape[p * NetworkLine.MarkPoints], other.Drape[p * NetworkLine.MarkPoints + NetworkLine.MarkPoints - 1])
                : (center.Y, center.Y);
            corners.Add(new float3(center.X - right.X * half, near + rise, center.Z - right.Z * half));
            corners.Add(new float3(center.X + right.X * half, far + rise, center.Z + right.Z * half));
        }

        var hull = Outline(corners);
        var area = 0f;
        for (var i = 1; i + 1 < hull.Count; i++)
            area += MathF.Abs((hull[i].X - hull[0].X) * (hull[i + 1].Z - hull[0].Z) - (hull[i].Z - hull[0].Z) * (hull[i + 1].X - hull[0].X)) * 0.5f;
        if (hull.Count < 3 || area < 0.05f)
            return;

        var up = new float3(0f, 1f, 0f);
        var data = new float4(0f, line.S[point] % Wrap, code, 0f);
        var first = builder.Add(hull[0], up, data);
        var previous = builder.Add(hull[1], up, data);
        for (var i = 2; i < hull.Count; i++)
        {
            var next = builder.Add(hull[i], up, data);
            builder.Indices.AddRange([first, previous, next]);
            previous = next;
        }
    }

    /// The convex outline around points on the ground, from +X towards +Z.
    private static List<float3> Outline(List<float3> points)
    {
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        static float Turn(float3 a, float3 b, float3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        var hull = new List<float3>();
        foreach (var pass in (IEnumerable<float3>[])[sorted, Enumerable.Reverse(sorted)])
        {
            var floor = hull.Count + 1;
            foreach (var p in pass)
            {
                while (hull.Count > floor && Turn(hull[^2], hull[^1], p) <= 1e-4f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// Piers every 25 m or so under a bridge, wherever the deck stands well clear of the ground.
    private void Piers(Builder builder, NetworkLine line, int first, int last)
    {
        var deckWidth = line.Rail ? line.BallastHalfTop * 2f + 1.8f : line.Width + 0.8f;
        var deckDepth = line.Rail ? 1.8f : 1.4f;
        var concrete = new float4(0f, 0f, Code(LineMaterial.Concrete), 0f);
        var span = 25f;
        var next = MathF.Ceiling(line.S[first] / span) * span;

        // An abutment under each end of the deck: a seat for the girders, a wall behind them up to the deck, and a wing wall each side
        // going back and out into the bank, its top falling to the ground.
        void Abutment(int p, int inward)
        {
            var (ax, az) = (line.X[p], line.Z[p]);
            var fx = (float)(line.X[inward] - ax);
            var fz = (float)(line.Z[inward] - az);
            var length = MathF.Max(MathF.Sqrt(fx * fx + fz * fz), 1e-5f);
            var forward = new float3(fx / length, 0f, fz / length);
            var ground = MathF.Min(_source.HeightAt(ax, az), _source.HeightAt(ax + forward.X * 4.0, az + forward.Z * 4.0));
            // (Always, however little there seems to be under the deck: the bed needs something to stop against.)
            var top = line.Y[p] - deckDepth;
            ground = MathF.Min(ground, top - 0.5f);
            var right = new float3(-forward.Z, 0f, forward.X);
            var center = new float3((float)(ax - builder.Origin.X), 0f, (float)(az - builder.Origin.Z));
            var half = deckWidth * 0.5f + 0.1f;
            Solid(builder, center + forward * 0.6f, right * half, forward * 0.9f, ground - 1.5f, top, concrete);
            Solid(builder, center - forward * 0.35f, right * half, forward * 0.35f, ground - 1.5f, line.Y[p] - 0.05f, concrete);

            // A retaining wall out from each side, holding the bank and the bed's rock back from the gap: its top falls from the deck's edge
            // to the ground. On a railway it goes as far as the bed's rock does.
            var (_, _, edge) = DeckOf(line);
            var reach = line.Rail
                ? MathF.Max(line.BallastHalfTop + MathF.Max(0.35f, line.BedHeight * NetworkLine.BallastSlope * NetworkLine.BedRun) + NetworkLine.BedFade - half, 1f)
                : Math.Clamp((line.Y[p] - ground) * 1.2f, 1.5f, 6f);
            var settled = line.Y[p] + (line.Rail ? line.SinkOf(p) : RoadRise) + 0.1f;
            foreach (var side in (float[])[-1f, 1f])
            {
                var a = center - forward * 0.1f + right * (side * half);
                var b = a + right * (side * reach);
                var low = MathF.Min(ground, _source.HeightAt(b.X + builder.Origin.X, b.Z + builder.Origin.Z)) - 1.5f;
                Wing(builder, a, b, 0.4f, low, line.Y[p] + edge, settled, concrete);
            }
        }

        if (first == 0 || line.Structures[first - 1] != Structure.Bridge)
            Abutment(first, Math.Min(first + 1, line.Count - 1));
        if (last == line.Count - 1 || line.Structures[last] != Structure.Bridge)
            Abutment(last, Math.Max(last - 1, 0));

        for (var p = first; p < last; p++)
        {
            while (next < line.S[p + 1])
            {
                var t = (next - line.S[p]) / MathF.Max(line.S[p + 1] - line.S[p], 1e-4f);
                next += span;
                var x = line.X[p] + (line.X[p + 1] - line.X[p]) * t;
                var z = line.Z[p] + (line.Z[p + 1] - line.Z[p]) * t;
                var top = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * t - deckDepth;
                var ground = _source.HeightAt(x, z);
                if (top - ground < 2f)
                    continue;

                var fx = (float)(line.X[p + 1] - line.X[p]);
                var fz = (float)(line.Z[p + 1] - line.Z[p]);
                var length = MathF.Max(MathF.Sqrt(fx * fx + fz * fz), 1e-5f);
                var forward = new float3(fx / length, 0f, fz / length);
                var right = new float3(-forward.Z, 0f, forward.X);
                var center = new float3((float)(x - builder.Origin.X), 0f, (float)(z - builder.Origin.Z));
                // A wall of a pier under a railway, a column under a road, and a cap across for the girders to sit on.
                Solid(builder, center, right * (deckWidth * (line.Rail ? 0.42f : 0.2f)), forward * 0.65f, ground - 1f, top - 0.8f, concrete);
                Solid(builder, center, right * (deckWidth * 0.48f), forward * 0.8f, top - 0.8f, top, concrete);
            }
        }
    }

    /// A box's four sides from bottom to top: center on the ground plane, half extents along right and forward.
    private static void Box(Builder builder, float3 center, float3 right, float3 forward, float bottom, float top, float4 data)
    {
        float3[] corners = [center - right - forward, center + right - forward, center + right + forward, center - right + forward];
        for (var i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var edge = b - a;
            var normal = MathEx.Normalize(new float3(edge.Z, 0f, -edge.X));
            var v0 = builder.Add(new float3(a.X, bottom, a.Z), normal, data);
            var v1 = builder.Add(new float3(b.X, bottom, b.Z), normal, data);
            var v2 = builder.Add(new float3(a.X, top, a.Z), normal, data);
            var v3 = builder.Add(new float3(b.X, top, b.Z), normal, data);
            builder.Quad(v0, v1, v2, v3);
        }
    }

    private Built BuildNear(ChunkKey key, List<Run> runs, NetworkLines lines)
    {
        var builder = new Builder { Origin = OriginOf(key) };
        var ties = new List<TieInstance>();
        var plates = new List<TieInstance>();
        if (_turnoutsByChunk.TryGetValue(key, out var turnouts))
            foreach (var turnout in turnouts)
            {
                TurnoutRails(builder, turnout);
                if (KitFor(turnout) is { } parts)
                    KitFixed(builder, turnout, parts, _handThrows.GetValueOrDefault(turnout.Node), _objectModels.GetValueOrDefault(PlacedKind.HandThrow));
            }
        if (_assembliesByChunk.TryGetValue(key, out var assemblies))
            foreach (var assembly in assemblies)
                DiamondBuild(builder, assembly);
        if (_objectsByChunk.TryGetValue(key, out var placed))
            foreach (var o in placed)
                PlacedModel(builder, o);
        foreach (var run in runs)
        {
            var line = lines.Lines[run.Line];
            if (!line.Rail || line.Disused)
                continue;

            foreach (var (first, last, structure) in Spans(line, run))
            {
                if (structure == Structure.Tunnel)
                    continue;

                var railOffset = line.Gauge * 0.5f + RailHeadHalf;
                var baseY = line.Embedded ? RoadRise + 0.005f - RailTop : NetworkLine.BallastHeight + TieRise + PlateDepth;
                var rail = Code(LineMaterial.Rail);
                for (var track = 0; track < line.Tracks; track++)
                {
                    var o = line.TrackOffset(track);
                    foreach (var side in (int[])[-1, 1])
                    {
                        // A turnout's own rail is left out where it takes over (single track only, so track 0).
                        var cuts = line.TurnoutZones.Where(z => z.RailSide == side || z.RailSide == 0).Select(z => z.Rails)
                            .Concat(line.RailCuts.Where(c => c.Side == side).Select(c => (c.From, c.To))).ToList();
                        if (cuts.Count == 0)
                            Sweep(builder, line, first, last, RailProfile, _ => rail, (_, _, y) => y, o + side * railOffset, baseY);
                        else
                            foreach (var (from, to) in Pieces(line.S[first], line.S[last], cuts))
                                SweepLine(builder, line, from, to, o + side * railOffset, baseY);
                    }

                    if (!line.Embedded)
                        Ties(ties, plates, line, first, last, o, railOffset, builder.Origin, track,
                            [.. _crossingCovers.GetValueOrDefault(line.Index) ?? [], .. _crossoverSkips.GetValueOrDefault(line.Index) ?? []],
                            _crossoverSpans.GetValueOrDefault(line.Index));
                }
            }
        }

        var (sortedTies, tieCells) = ByCell(ties);
        var (sortedPlates, plateCells) = ByCell(plates);
        var (indices, fades) = builder.Sorted();
        return new Built(key, true, [.. builder.Vertices], indices, fades, sortedTies, sortedPlates, tieCells, plateCells);
    }

    /// What's left of [from, to] once the cuts are taken out.
    private static List<(float From, float To)> Pieces(float from, float to, List<(float From, float To)> cuts)
    {
        var pieces = new List<(float, float)> { (from, to) };
        foreach (var (a, b) in cuts)
        {
            var next = new List<(float, float)>();
            foreach (var (p, q) in pieces)
            {
                if (b <= p || a >= q)
                {
                    next.Add((p, q));
                    continue;
                }

                if (a > p)
                    next.Add((p, a));
                if (b < q)
                    next.Add((b, q));
            }

            pieces = next;
        }

        return pieces.Where(p => p.Item2 - p.Item1 > 0.05f).ToList();
    }

    /// A centreline frame anywhere along a line: the point (relative to origin) and the horizontal right.
    private static (float3 Center, float3 Right) FrameAt(NetworkLine line, float s, float3 origin)
    {
        var leg = new Leg(line, 0, 1);
        var (x, z, y, _, _, rx, rz) = leg.At(s);
        return (new float3((float)(x - origin.X), y, (float)(z - origin.Z)), new float3(rx, 0f, rz));
    }

    /// A rail from one distance along a line to another, offset across it, the ends exactly where asked.
    private static void SweepLine(Builder builder, NetworkLine line, float from, float to, float offset, float rise)
    {
        var rings = new List<(float3, float3, float, float, (float, float)[])>();
        void Ring(float s)
        {
            var (center, right) = FrameAt(line, s, builder.Origin);
            rings.Add((center, right, s % Wrap, offset, RailProfile));
        }

        Ring(from);
        foreach (var s in line.S)
            if (s > from + 0.01f && s < to - 0.01f)
                Ring(s);
        Ring(to);
        SweepRings(builder, rings, Code(LineMaterial.Rail), rise);
    }

    /// Sweeps a section through rings: each its centre (relative to the origin), right, distance along (for the
    /// texture), offset across, and section (all with the same number of points), rise above the centre.
    private static void SweepRings(Builder builder, List<(float3 Center, float3 Right, float V, float Offset, (float U, float Y)[] Section)> rings, float code, float rise) =>
        SweepRings(builder, rings, _ => code, (_, y) => y, rise);

    /// As above, code(edge) picking each edge's material and value(u, y) its Data.w.
    private static void SweepRings(Builder builder, List<(float3 Center, float3 Right, float V, float Offset, (float U, float Y)[] Section)> rings,
        Func<int, float> code, Func<float, float, float> value, float rise, bool smooth = false)
    {
        if (rings.Count < 2)
            return;
        var edges = rings[0].Section.Length - 1;
        var start = (uint)builder.Vertices.Count;
        foreach (var (center, right, v, offset, section) in rings)
        {
            for (var e = 0; e < edges; e++)
            {
                var (u0, y0) = section[e];
                var (u1, y1) = section[e + 1];
                var du = u1 - u0;
                var dy = y1 - y0;
                var len = MathF.Max(MathF.Sqrt(du * du + dy * dy), 1e-6f);
                var normal = right * (-dy / len) + new float3(0f, du / len, 0f);
                var (n0, n1) = smooth ? JoinNormals(section, e, right) : (normal, normal);
                builder.Add(center + right * (offset + u0) + new float3(0f, rise + y0, 0f), n0, new float4(offset + u0, v, code(e), value(u0, y0)));
                builder.Add(center + right * (offset + u1) + new float3(0f, rise + y1, 0f), n1, new float4(offset + u1, v, code(e), value(u1, y1)));
            }
        }

        var ring = (uint)(edges * 2);
        for (var p = 0; p < rings.Count - 1; p++)
        {
            for (var e = 0; e < edges; e++)
            {
                var a = start + (uint)p * ring + (uint)e * 2;
                builder.Quad(a, a + 1, a + ring, a + ring + 1);
            }
        }
    }

    private static readonly float RailRise = NetworkLine.BallastHeight + TieRise + PlateDepth;

    /// Rings along a leg from one distance to another about every step metres, offset across it by offset(s), with
    /// section(s).
    private static List<(float3, float3, float, float, (float, float)[])> LegRings(Leg leg, float from, float to, float step, float3 origin,
        Func<float, float> offset, Func<float, (float U, float Y)[]> section)
    {
        var rings = new List<(float3, float3, float, float, (float, float)[])>();
        var count = Math.Max(1, (int)MathF.Ceiling((to - from) / step));
        for (var k = 0; k <= count; k++)
        {
            var s = from + (to - from) * k / count;
            var (x, z, y, _, _, rx, rz) = leg.At(s);
            rings.Add((new float3((float)(x - origin.X), y, (float)(z - origin.Z)), new float3(rx, 0f, rz), leg.LineS(s) % Wrap, offset(s), section(s)));
        }

        return rings;
    }

    /// A turnout's fixed rails, swept along its routes: the closure rails from the blades' heels to the frog, each running
    /// on as a wing rail past the crossing (from where its gauge face comes within a flangeway of the other route's gauge
    /// line, it follows that line a flangeway off, flaring away at its end); the frog's two point rails, from where the gauge
    /// lines are the frog point's width apart, planed down to one V between the gauge lines until they're full rails at the
    /// crossing; and a guard rail inside each route's outer rail facing the frog, its ends flared in.
    private static void TurnoutRails(Builder builder, Turnout t)
    {
        var rail = Code(LineMaterial.Rail);
        var h = Turnout.RailOffset;
        var side = t.Side;
        var gauge = 2f * (h - RailHeadHalf);
        var (fn, fr) = (t.FrogNormal, t.FrogReverse);
        // How far apart the routes' centrelines are at a point on either: measured there, square to the other route, so the
        // wing rail beside each point rail always stands a flangeway off it (a distance along one route scaled to the other's
        // drifts on a curved diverging route, and the gap closes).
        float SpreadOn(float s) => t.SpreadAt(s);
        float SpreadOff(float s)
        {
            var (x, z, _, _, _, _, _) = t.Reverse.At(s);
            return Turnout.Offset(t.Normal, x, z) is { } at ? MathF.Max(0f, at.Offset * side) : t.SpreadAt(s * fn / fr);
        }
        float Wing(float spread, float past) => MathF.Max(0f, spread - gauge + Turnout.Flangeway) + 0.03f * MathF.Pow(MathF.Max(0f, past - 1f) / 0.5f, 2f);

        SweepRings(builder, LegRings(t.Normal, t.BladeLength, fn + 1.5f, 0.5f, builder.Origin,
            s => side * (h + Wing(SpreadOn(s), s - fn)), _ => RailProfile), rail, RailRise);
        SweepRings(builder, LegRings(t.Reverse, t.BladeLength, fr + 1.5f, 0.5f, builder.Origin,
            s => -side * (h + Wing(SpreadOff(s), s - fr)), _ => RailProfile), rail, RailRise);

        var point = t.BladeLength;
        while (point < fn && SpreadOn(point) < gauge + FrogPoint)
            point += 0.1f;
        (float U, float Y)[] Planed(float spread, int keep)
        {
            var k = Math.Clamp((spread - gauge) / (2f * RailHeadHalf), 0.05f, 1f);
            return [.. RailProfile.Select(p => (p.U * k + keep * RailHeadHalf * (1f - k), p.Y))];
        }

        SweepRings(builder, LegRings(t.Normal, point, fn + Turnout.FrogGapAfter, 0.2f, builder.Origin, _ => side * h, s => Planed(SpreadOn(s), -side)), rail, RailRise);
        SweepRings(builder, LegRings(t.Reverse, point * fr / fn, fr + Turnout.FrogGapAfter, 0.2f, builder.Origin, _ => -side * h, s => Planed(SpreadOff(s), side)), rail, RailRise);

        var half = Turnout.GuardLength * 0.5f;
        float Flare(float s, float centre) => 0.05f * MathF.Max(0f, (MathF.Abs(s - centre) - (half - 0.6f)) / 0.6f);
        var guard = RailProfile.Select(p => (p.U, p.Y * 0.92f)).ToArray();
        SweepRings(builder, LegRings(t.Normal, fn - half, fn + half, 0.3f, builder.Origin,
            s => -side * (h - Turnout.GuardOffset - Flare(s, fn)), _ => guard), rail, RailRise);
        SweepRings(builder, LegRings(t.Reverse, fr - half, fr + half, 0.3f, builder.Origin,
            s => side * (h - Turnout.GuardOffset - Flare(s, fr)), _ => guard), rail, RailRise);
    }

    /// The frog point's blunt tip: how far apart the gauge lines are where the point rails begin.
    private const float FrogPoint = 0.0127f;

    /// A blade's section t of the way from its tip (0) to its heel (1): planed thin and low at the tip, the full rail at
    /// the heel. away: which way (across) is its stock rail, which the planing keeps the blade's back to.
    private static (float U, float Y)[] BladeSection(float t, int away)
    {
        var thin = 0.3f + 0.7f * MathF.Sqrt(t);
        var low = 0.82f + 0.18f * t;
        return [.. RailProfile.Select(p => (p.U * thin + away * 0.0373f * (1f - thin), p.Y * low))];
    }

    /// A turnout's two blades with the switch at position (0 normal, 1 reverse): the straight blade inside the curved
    /// stock rail, closed against it for the normal route, and the curved blade inside the straight stock rail, closed
    /// for the reverse; each pivots at its heel, swinging Throw at its tip.
    private static void Blades(Builder builder, Turnout t, float position)
    {
        var rail = Code(LineMaterial.Rail);
        var h = Turnout.RailOffset;
        var side = t.Side;
        var length = t.BladeLength;
        SweepRings(builder, LegRings(t.Normal, 0f, length, 0.4f, builder.Origin,
            s => side * (h - (Turnout.Inset + Turnout.Throw * position) * (1f - s / length)), s => BladeSection(s / length, side)), rail, RailRise);
        SweepRings(builder, LegRings(t.Reverse, 0f, length, 0.4f, builder.Origin,
            s => -side * (h - (Turnout.Inset + Turnout.Throw * (1f - position)) * (1f - s / length)), s => BladeSection(s / length, -side)), rail, RailRise);
    }

    // --- The switch kit: parts from Blender, fitted to each switch's routes. ---

    private const float KitRailBase = TieRise + PlateDepth;

    /// Where the stretchers are along the blades, the machine across from its route, and the first stretcher's the rod goes to.
    private static readonly float[] Stretchers = [0.6f, 1.5f];
    private const float MachineOff = 0.85f;

    /// A point on a route, offset across it, on its ballast.
    private static float3 On(Leg leg, float s, float offset, float3 origin)
    {
        var (x, z, y, _, _, rx, rz) = leg.At(s);
        return new float3((float)(x + rx * offset - origin.X), y + NetworkLine.BallastHeight, (float)(z + rz * offset - origin.Z));
    }

    /// What doesn't move: the point machine, and the frog casting across the V the two routes' rails make.
    private static void KitFixed(Builder builder, Turnout t, Dictionary<string, Model.Part> parts, PlacedObject? handThrow, Dictionary<string, Model.Part>? handModel)
    {
        if (handThrow is not null && handModel is not null)
        {
            var (at, face) = HandThrowAt(t, handThrow, builder.Origin);
            foreach (var (name, part) in handModel.Where(p => p.Key.StartsWith("Fixed_")))
                Upright(builder, part, at, face, PaintOf(name), 0f);
        }
        else if (parts.TryGetValue("Machine", out var machine))
            Stand(builder, t, machine, Code(LineMaterial.Concrete));
        if (parts.TryGetValue("FrogCasting", out var casting))
            Casting(builder, t, casting, Code(LineMaterial.Rail));
    }

    /// What moves with the blades at position (0 normal, 1 reverse): each stretcher between the two blades, and the throw
    /// rod from the machine to the first of them.
    private static void KitMoving(Builder builder, Turnout t, Dictionary<string, Model.Part> parts, float position, PlacedObject? handThrow, Dictionary<string, Model.Part>? handModel)
    {
        var machineSide = handThrow is { Opposite: true } && handModel is not null ? -1 : 1;
        if (handThrow is not null && handModel is not null)
        {
            var (at, face) = HandThrowAt(t, handThrow, builder.Origin);
            foreach (var (name, part) in handModel.Where(p => p.Key.StartsWith("Moving_")))
                Upright(builder, part, at, face, PaintOf(name), position * MathF.PI * 0.5f);
        }

        var rail = Code(LineMaterial.Rail);
        var h = Turnout.RailOffset;
        var side = t.Side;
        float3 Blade(Leg leg, int across, float s, float opened) =>
            On(leg, s, across * (h - (Turnout.Inset + Turnout.Throw * opened) * (1f - s / t.BladeLength)), builder.Origin);

        foreach (var s in Stretchers)
            if (parts.TryGetValue("Stretcher", out var stretcher))
                Bar(builder, stretcher, rail, Blade(t.Normal, side, s, position), Blade(t.Reverse, -side, s, 1f - position));
        if (parts.TryGetValue("ThrowRod", out var rod))
            Bar(builder, rod, rail, On(t.Normal, Stretchers[0], -side * machineSide * (h + MachineOff), builder.Origin), Blade(t.Normal, side, Stretchers[0], position));
    }

    /// Where a switch's hand throw stands: beside its first stretcher, on the side away from the diverging route unless it's
    /// set opposite, facing across the track.
    private static (float3 At, float3 Face) HandThrowAt(Turnout t, PlacedObject o, float3 origin)
    {
        var side = t.Side * (o.Opposite ? 1 : -1);
        var (_, _, _, _, _, rx, rz) = t.Normal.At(Stretchers[0]);
        return (On(t.Normal, Stretchers[0], side * (Turnout.RailOffset + MachineOff), origin) - new float3(0f, NetworkLine.BallastHeight, 0f), new float3(rx * side, 0f, rz * side));
    }

    private static float PaintOf(string name)
    {
        var at = Array.IndexOf(Paints, name[(name.IndexOf('_') + 1)..]);
        return Code(LineMaterial.Painted, Math.Max(at, 0));
    }

    /// A placed model's part stood upright at a place, its +z along face, turned about its own origin by spin.
    private static void Upright(Builder builder, Model.Part part, float3 at, float3 face, float code, float spin)
    {
        var (cos, sin) = (MathF.Cos(spin), MathF.Sin(spin));
        var right = new float3(face.Z, 0f, -face.X);
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var (p, n) = (vertex.Position, vertex.Normal);
            var (px, pz) = (p.X * cos - p.Z * sin, p.X * sin + p.Z * cos);
            var (nx, nz) = (n.X * cos - n.Z * sin, n.X * sin + n.Z * cos);
            builder.Add(at + right * px + face * pz + new float3(0f, p.Y, 0f), right * nx + face * nz + new float3(0f, n.Y, 0f), new float4(vertex.Uv.X, vertex.Uv.Y, code, 0f));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    private void PlacedModel(Builder builder, PlacedAt placed)
    {
        var o = placed.Object;
        var angle = o.Heading * Math.PI / 180.0;
        var face = new float3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
        var at = new float3((float)(placed.X - builder.Origin.X), placed.Y, (float)(placed.Z - builder.Origin.Z));
        if (!_objectModels.TryGetValue(o.Kind, out var parts))
            return;
        if (o.Kind != PlacedKind.GradeCrossing)
        {
            foreach (var (name, part) in parts)
            {
                var paint = PaintOf(name);
                if (o.Kind == PlacedKind.Signal && Array.FindIndex(Lamps, l => l.Part == name) is var lamp and >= 0 && LampsOf(o.Id).HasFlag(Lamps[lamp].Lamp))
                    paint = Code(LineMaterial.Painted, Array.IndexOf(Paints, Lamps[lamp].Paint) + LitLens);
                Upright(builder, part, at, face, paint, 0f);
            }
            return;
        }

        // A grade crossing's masts. (Its deck's in the far mesh, as the road it takes the place of is.)
        if (placed.Deck is not { } deck)
            return;
        foreach (var (mx, mz, y, toward) in deck.Masts)
        {
            var mast = new float3((float)(mx - builder.Origin.X), y, (float)(mz - builder.Origin.Z));
            foreach (var (name, part) in parts)
                Upright(builder, part, mast, toward, PaintOf(name), o.Turn * MathF.PI / 180f);
        }
    }

    private static void CrossingSurface(Builder builder, PlacedAt placed)
    {
        if (placed.Deck is not { } deck)
            return;
        var from = new float3((float)(placed.X - builder.Origin.X), 0f, (float)(placed.Z - builder.Origin.Z));
        var surface = Code(LineMaterial.Asphalt);
        foreach (var q in deck.Quads)
        {
            if (q.Paint >= 0f)
                Face(builder, [from + q.A, from + q.B, from + q.C, from + q.D], from + q.Inside, q.Paint, 0f);
            else if (deck.Road is not { } road)
                Face(builder, [from + q.A, from + q.B, from + q.C, from + q.D], from + q.Inside, surface, 0f);
            else if (q.Shoulder)
                Face(builder, [from + q.A, from + q.B, from + q.C, from + q.D], from + q.Inside, Code(LineMaterial.Gravel), 0f);
            else
            {
                // The road's own surface carries on over it, markings and all.
                var normal = Cross(q.B - q.A, q.D - q.A);
                normal *= (normal.Y < 0f ? -1f : 1f) / MathF.Max(MathF.Sqrt(Dot(normal, normal)), 1e-6f);
                var start = (uint)builder.Vertices.Count;
                foreach (var corner in (float3[])[q.A, q.B, q.C, q.D])
                    builder.Add(from + corner, normal, new float4(Dot(corner, road.Across) - road.AcrossAt, (Dot(corner, road.Along) + road.AlongAt) % Wrap, road.Code, 1000f));
                builder.Indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
            }
        }
    }

    /// A part stood as it is on a turnout's normal route: its across (model x) along the route's right, its along (model -z)
    /// as distance along the route, its up on the route's ballast, so it follows the track.
    private static void Stand(Builder builder, Turnout t, Model.Part part, float code)
    {
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var p = vertex.Position;
            var (x, z, y, fx, fz, rx, rz) = t.Normal.At(MathF.Max(-p.Z, 0f));
            var (right, forward) = (new float3(rx, 0f, rz), new float3(fx, 0f, fz));
            var position = new float3((float)(x - builder.Origin.X), y + NetworkLine.BallastHeight + p.Y, (float)(z - builder.Origin.Z)) + right * p.X;
            var n = vertex.Normal;
            builder.Add(position, right * n.X + new float3(0f, n.Y, 0f) - forward * n.Z, new float4(p.X, t.Normal.LineS(MathF.Max(-p.Z, 0f)) % Wrap, code, p.Y - KitRailBase));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    /// A part from -1 to 1 in model x, stretched from one point to the other; its y and z as they are.
    private static void Bar(Builder builder, Model.Part part, float code, float3 a, float3 b)
    {
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-3f);
        var across = new float3(dx / length, 0f, dz / length);
        var forward = new float3(across.Z, 0f, -across.X);
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var p = vertex.Position;
            var t = (p.X + 1f) * 0.5f;
            var position = a + (b - a) * t + new float3(0f, p.Y, 0f) - forward * p.Z;
            var n = vertex.Normal;
            builder.Add(position, across * n.X + new float3(0f, n.Y, 0f) - forward * n.Z, new float4(p.X, 0f, code, p.Y - KitRailBase));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    /// The frog casting: its along (model -z) along the line between the two routes from the frog, its x from -1 to 1 across
    /// as wide as the rails on it have parted there, so it fits the frog's angle.
    private static void Casting(Builder builder, Turnout t, Model.Part part, float code)
    {
        var h = Turnout.RailOffset;
        var start = (uint)builder.Vertices.Count;
        foreach (var vertex in part.Vertices)
        {
            var p = vertex.Position;
            var l = -p.Z;
            var a = On(t.Normal, t.FrogNormal + l, t.Side * h, builder.Origin);
            var b = On(t.Reverse, t.FrogReverse + l, -t.Side * h, builder.Origin);
            var (_, _, _, _, _, nx, nz) = t.Normal.At(t.FrogNormal + l);
            var (_, _, _, _, _, ox, oz) = t.Reverse.At(t.FrogReverse + l);
            var (ax, az) = (nx + ox, nz + oz);
            var length = MathF.Max(MathF.Sqrt(ax * ax + az * az), 1e-3f);
            var across = new float3(ax / length, 0f, az / length);
            var forward = new float3(across.Z, 0f, -across.X);
            var middle = (a + b) * 0.5f;
            var (ex, ez) = (a.X - b.X, a.Z - b.Z);
            var width = CastingEdge + MathF.Sqrt(ex * ex + ez * ez) * 0.5f;
            var n = vertex.Normal;
            builder.Add(middle + across * (p.X * width) + new float3(0f, p.Y, 0f), across * n.X + new float3(0f, n.Y, 0f) - forward * n.Z,
                new float4(p.X * width, t.Normal.LineS(t.FrogNormal + l) % Wrap, code, p.Y - KitRailBase));
        }

        foreach (var index in part.Indices)
            builder.Indices.Add(start + index);
    }

    /// How far the frog casting reaches past the rails on it, either side.
    private const float CastingEdge = 0.16f;

    /// Parts sorted by the cell of the chunk they stand in, and where each cell's run starts.
    private static (TieInstance[] Sorted, int[] Starts) ByCell(List<TieInstance> parts)
    {
        static int CellOf(TieInstance part) =>
            Math.Clamp((int)(part.PositionRandom.X / CellSize), 0, Cells - 1) + Cells * Math.Clamp((int)(part.PositionRandom.Z / CellSize), 0, Cells - 1);

        var counts = new int[Cells * Cells + 1];
        foreach (var part in parts)
            counts[CellOf(part) + 1]++;
        for (var c = 1; c < counts.Length; c++)
            counts[c] += counts[c - 1];

        var sorted = new TieInstance[parts.Count];
        var next = (int[])counts.Clone();
        foreach (var part in parts)
            sorted[next[CellOf(part)]++] = part;
        return (sorted, counts);
    }

    /// Ties along a track, and a plate under each rail on each (turned round on the left rail, so both plates' field
    /// shoulders face outward).
    /// What a line's tie at s is under a turnout: a long timber if the line's that turnout's normal route, carried by the normal
    /// route's timber if it's the reverse route and that timber really is there there (the normal route may itself be giving way to
    /// another turnout's), or a plain tie of its own (null) otherwise. So a tie's only ever left out for one that exists.
    internal static TurnoutZone? TieZone(NetworkLine line, float s, int depth = 0)
    {
        TurnoutZone? normal = null;
        foreach (var zone in line.TurnoutZones)
        {
            if (s < zone.Timbers.From || s > zone.Timbers.To)
                continue;
            if (zone.Normal)
                normal ??= zone;
            else if (depth < 4 && Carried(zone, line, s, depth))
                return zone;
        }

        return normal;
    }

    private static bool Carried(TurnoutZone zone, NetworkLine line, float s, int depth)
    {
        var t = zone.Turnout;
        var (x, z, _, _, _, _, _) = t.Reverse.At(MathF.Abs(s - t.Reverse.Start));
        if (Turnout.Offset(t.Normal, x, z) is not { } at)
            return false;
        var along = t.Normal.LineS(at.S);
        return !t.Normal.Line.TieCuts.Any(c => along >= c.From && along <= c.To) && TieZone(t.Normal.Line, along, depth + 1) is { Normal: true } carrier && carrier.Turnout == t;
    }

    /// Crossovers: pairs of switches whose diverging routes are the same track, on normal routes running side by side
    /// within a couple of track spacings. The one with the lower node carries the timbers.
    private void FindCrossovers()
    {
        _crossoverSpans.Clear();
        _crossoverSkips.Clear();
        void Skip(NetworkLine line, float a, float b)
        {
            if (!_crossoverSkips.TryGetValue(line.Index, out var list))
                _crossoverSkips[line.Index] = list = [];
            list.Add((MathF.Min(a, b), MathF.Max(a, b)));
        }

        var turnouts = _lines.Turnouts;
        var switchNodes = turnouts.Select(t => t.Node).ToHashSet();
        foreach (var a in turnouts)
            foreach (var b in turnouts)
            {
                if (a.Node >= b.Node || a.Reverse.Line != b.Reverse.Line || a.Normal.Line == b.Normal.Line)
                    continue;
                // Facing each other along the crossover, on tracks running the same way close by.
                var (ha, hb) = (Turnout.Heading(a.Normal), Turnout.Heading(b.Normal));
                if (ha.X * hb.X + ha.Z * hb.Z > -0.98f)
                    continue;
                if (Turnout.Offset(a.Normal, b.X, b.Z) is not { } bOnA || Turnout.Offset(b.Normal, a.X, a.Z) is not { } aOnB)
                    continue;
                var apart = MathF.Abs(bOnA.Offset);
                if (apart < 2.5f || apart > 2.2f * NetworkLine.TrackSpacing || bOnA.S < 5f || aOnB.S < 5f)
                    continue;
                // The crossover between them must be just that: the two switch points on the one line, nothing between.
                var (sa, sb) = (a.Reverse.LineS(0f), b.Reverse.LineS(0f));
                var line = a.Reverse.Line;
                if (line.Nodes.Any(n => n.Node != a.Node && n.Node != b.Node && switchNodes.Contains(n.Node) && line.S[n.Point] > MathF.Min(sa, sb) + 0.5f && line.S[n.Point] < MathF.Max(sa, sb) - 0.5f))
                    continue;

                var (from, to) = (a.Normal.LineS(0f), a.Normal.LineS(bOnA.S));
                if (!_crossoverSpans.TryGetValue(a.Normal.Line.Index, out var spans))
                    _crossoverSpans[a.Normal.Line.Index] = spans = [];
                spans.Add((MathF.Min(from, to), MathF.Max(from, to), b.Normal, a.Side * a.Normal.Direction));
                Skip(b.Normal.Line, b.Normal.LineS(0f), b.Normal.LineS(aOnB.S));
                Skip(line, sa, sb);
            }
    }

    private static void Ties(List<TieInstance> ties, List<TieInstance> plates, NetworkLine line, int first, int last, float offset, float railOffset,
        float3 origin, int track, List<(float From, float To)>? covered = null, List<(float From, float To, Leg Other, int Across)>? spans = null)
    {
        var s = MathF.Ceiling(line.S[first] / TieSpacing) * TieSpacing;
        for (var p = first; p < last; p++)
        {
            var length = line.S[p + 1] - line.S[p];
            if (length <= 1e-4f)
                continue;

            var dx = (float)(line.X[p + 1] - line.X[p]);
            var dz = (float)(line.Z[p + 1] - line.Z[p]);
            var dy = line.Y[p + 1] - line.Y[p];
            var forward = MathEx.Normalize(new float3(dx, dy, dz));
            var right = MathEx.Normalize(new float3(-dz, 0f, dx));

            // The last segment of a span takes its end point too, so spans meeting at a point don't both lay a tie there.
            while (s < line.S[p + 1] || (p == last - 1 && s <= line.S[p + 1]))
            {
                var t = (s - line.S[p]) / length;
                var x = (float)(line.X[p] - origin.X + (line.X[p + 1] - line.X[p]) * t);
                var z = (float)(line.Z[p] - origin.Z + (line.Z[p + 1] - line.Z[p]) * t);
                var y = line.Y[p] + dy * t + NetworkLine.BallastHeight;
                var position = new float3(x, y, z) + right * offset;
                var random = MathEx.Hash((uint)line.Index * 7919u + (uint)track, (uint)MathF.Round(s / TieSpacing));
                var seat = position + new float3(0f, TieRise, 0f);

                // Through a turnout: the normal route's ties are long timbers reaching under the reverse route too,
                // with its plates; the reverse route lays none of its own.
                var zone = TieZone(line, s);
                if (zone is { Normal: false } || line.TieCuts.Any(c => s >= c.From && s <= c.To) || (covered?.Any(c => s >= c.From && s <= c.To) ?? false))
                {
                    s += TieSpacing;
                    continue;
                }

                if (spans?.FirstOrDefault(c => s >= c.From && s <= c.To) is { Other.Line: not null } span &&
                    Turnout.Offset(span.Other, line.X[p] + (line.X[p + 1] - line.X[p]) * t, line.Z[p] + (line.Z[p + 1] - line.Z[p]) * t) is { } other)
                {
                    // A crossover's timber, out under the other track's rails too.
                    var spread = MathF.Abs(other.Offset);
                    var across = right * span.Across;
                    ties.Add(new TieInstance(new float4(position + across * (spread * 0.5f), random), new float4(forward, (TieLength + spread) / TieLength)));
                    foreach (var u in (ReadOnlySpan<float>)[spread - railOffset, spread + railOffset])
                        plates.Add(new TieInstance(new float4(seat + across * u, random), new float4(u > spread ? forward * span.Across : -forward * span.Across, 0f)));
                }
                else if (zone?.Turnout is { } timbered)
                {
                    var spread = timbered.SpreadAt(MathF.Abs(s - timbered.Normal.Start));
                    var across = right * (timbered.Side * timbered.Normal.Direction);
                    ties.Add(new TieInstance(new float4(position + across * (spread * 0.5f), random), new float4(forward, (TieLength + spread) / TieLength)));
                    foreach (var u in (ReadOnlySpan<float>)[spread - railOffset, spread + railOffset])
                        if (MathF.Abs(u - railOffset) > 0.25f && MathF.Abs(u + railOffset) > 0.25f)
                            plates.Add(new TieInstance(new float4(seat + across * u, random), new float4(u > spread ? forward * (timbered.Side * timbered.Normal.Direction) : -forward * (timbered.Side * timbered.Normal.Direction), 0f)));
                }
                else
                    ties.Add(new TieInstance(new float4(position, random), new float4(forward, 0f)));

                plates.Add(new TieInstance(new float4(seat + right * railOffset, random), new float4(forward, 0f)));
                plates.Add(new TieInstance(new float4(seat - right * railOffset, random), new float4(-forward, 0f)));
                s += TieSpacing;
            }
        }
    }

    // --- Drawing. ---

    /// Draws the far meshes and rails with the line pipeline bound; push writes each chunk's origin relative to the
    /// camera.
    /// The beds' fades into the ground: to be drawn with their own pipeline, ahead of DrawLines.
    /// A chunk mesh's offset from the camera, if any of it is in view.
    private static bool InView(Mesh mesh, ChunkKey key, double cx, double cy, double cz, ViewCone view, out float3 offset)
    {
        var origin = OriginOf(key);
        offset = new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz));
        return view.Sees(offset + (mesh.BoundsMin + mesh.BoundsMax) * 0.5f, MathEx.Length(mesh.BoundsMax - mesh.BoundsMin) * 0.5f);
    }

    public void DrawFades(CommandBuffer cmd, double cx, double cy, double cz, ViewCone view, Action<float3> push)
    {
        foreach (var (key, near) in _drawList)
        {
            var chunk = _chunks[key];
            var mesh = near ? chunk.Near : chunk.Far;
            var fades = near ? chunk.NearFades : chunk.FarFades;
            if (mesh is null || fades == 0 || !InView(mesh, key, cx, cy, cz, view, out var offset))
                continue;

            push(offset);
            mesh.DrawRange(cmd, mesh.IndexCount - fades, fades);
        }
    }

    public void DrawLines(CommandBuffer cmd, double cx, double cy, double cz, ViewCone view, Action<float3> push)
    {
        foreach (var (key, near) in _drawList)
        {
            var chunk = _chunks[key];
            var mesh = near ? chunk.Near : chunk.Far;
            if (mesh is null || !InView(mesh, key, cx, cy, cz, view, out var offset))
                continue;

            push(offset);
            mesh.DrawRange(cmd, 0, mesh.IndexCount - (near ? chunk.NearFades : chunk.FarFades));
        }

        foreach (var sw in _switches.Values)
        {
            if (sw.Blades is null || !_nearKeys.Contains(sw.Key))
                continue;
            var origin = OriginOf(sw.Key);
            push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));
            sw.Blades.Draw(cmd);
        }
    }

    /// Where a mesh's vertices reach, for leaving it out of the shadow cascades it can't touch.
    private static void SetBounds(Mesh mesh, IEnumerable<LineVertex> vertices)
    {
        var (min, max) = (new float3(float.MaxValue), new float3(float.MinValue));
        foreach (var vertex in vertices)
        {
            var p = vertex.Position;
            min = new float3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
            max = new float3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
        }

        (mesh.BoundsMin, mesh.BoundsMax) = (min, max);
    }

    /// The last shadow cascade thin things are worth drawing in: rails and ties are a fraction of a texel in the wider ones, and
    /// would only speckle. Beds, roads and bridges go in all of them.
    private const int LastThinCascade = 1;

    /// Draws the lines' shapes into a shadow cascade, with a pipeline bound that takes just their positions.
    /// thinOnly: the track's own cascade (SunShadows.TrackReach), which takes the rails and the rest of the near meshes but not the
    /// beds and roads of the far ones.
    public void DrawLineCasters(CommandBuffer cmd, double cx, double cy, double cz, int cascade, Func<float3, float, bool> reaches, Action<float3> push,
        bool thinOnly = false)
    {
        foreach (var (key, near) in _drawList)
        {
            if ((near && cascade > LastThinCascade) || (thinOnly && !near))
                continue;

            var chunk = _chunks[key];
            var mesh = near ? chunk.Near : chunk.Far;
            if (mesh is null)
                continue;

            var origin = OriginOf(key);
            var offset = new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz));
            if (!reaches(offset + (mesh.BoundsMin + mesh.BoundsMax) * 0.5f, MathEx.Length(mesh.BoundsMax - mesh.BoundsMin) * 0.5f))
                continue;

            push(offset);
            mesh.Draw(cmd);
        }

        if (cascade > LastThinCascade)
            return;

        foreach (var sw in _switches.Values)
        {
            if (sw.Blades is null || !_nearKeys.Contains(sw.Key))
                continue;
            var origin = OriginOf(sw.Key);
            push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));
            sw.Blades.Draw(cmd);
        }
    }

    /// Draws the ties and, close by, the plates into a shadow cascade, cell by cell, with the part caster pipeline bound.
    /// coarse: the track's own cascade, which reaches far enough that the plates go in as plain blocks.
    public void DrawPartCasters(CommandBuffer cmd, double cx, double cy, double cz, int cascade, Func<float3, float, bool> reaches, Action<float3> push,
        bool coarse = false)
    {
        if (cascade > LastThinCascade)
            return;

        DrawCellCasters(cmd, cx, cy, cz, reaches, push, c => (c.Ties, c.TieCells), _tieFar.Mesh);
        if (cascade == 0)
            DrawCellCasters(cmd, cx, cy, cz, reaches, push, c => (c.Plates, c.PlateCells), coarse ? _plateFar : _plate.Mesh);
    }

    private void DrawCellCasters(CommandBuffer cmd, double cx, double cy, double cz, Func<float3, float, bool> reaches, Action<float3> push, Func<Chunk, (BufferEx, int[])> instances, Mesh mesh)
    {
        foreach (var (key, near) in _drawList)
        {
            if (!near)
                continue;

            var chunk = _chunks[key];
            var (buffer, cells) = instances(chunk);
            if (cells.Length == 0 || cells[^1] == 0 || chunk.Near is not { } lines)
                continue;

            var origin = OriginOf(key);
            var height = (lines.BoundsMin.Y + lines.BoundsMax.Y) * 0.5f - (float)cy;
            var radius = CellSize * 0.75f + (lines.BoundsMax.Y - lines.BoundsMin.Y) * 0.5f + 3f;
            var pushed = false;
            var runStart = -1;
            for (var c = 0; c <= Cells * Cells; c++)
            {
                var wanted = c < Cells * Cells && reaches(
                    new float3((float)(origin.X + (c % Cells + 0.5f) * CellSize - cx), height, (float)(origin.Z + (c / Cells + 0.5f) * CellSize - cz)), radius);
                if (wanted)
                {
                    if (runStart < 0)
                        runStart = c;
                    continue;
                }

                if (runStart >= 0 && cells[c] > cells[runStart])
                {
                    if (!pushed)
                    {
                        push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));
                        pushed = true;
                    }

                    mesh.DrawInstanced(cmd, buffer, cells[c] - cells[runStart], cells[runStart]);
                }

                runStart = -1;
            }
        }
    }

    /// Where the signals placed on the route stand, the way each faces (degrees from +X towards +Z), and which lamps it has lit.
    /// Where each kind of light pole's lamp is in its model: the middle of its lens part's underside (Fixed_White).
    private readonly Dictionary<PlacedKind, float3> _lampOffsets = [];

    private float3? LampOffset(PlacedKind kind)
    {
        if (_lampOffsets.TryGetValue(kind, out var known))
            return known;
        if (!_objectModels.TryGetValue(kind, out var parts) || !parts.TryGetValue("Fixed_White", out var lens) || lens.Vertices.Length == 0)
            return null;
        var (min, max) = (new float3(float.MaxValue), new float3(float.MinValue));
        foreach (var v in lens.Vertices)
        {
            min = new float3(MathF.Min(min.X, v.Position.X), MathF.Min(min.Y, v.Position.Y), MathF.Min(min.Z, v.Position.Z));
            max = new float3(MathF.Max(max.X, v.Position.X), MathF.Max(max.Y, v.Position.Y), MathF.Max(max.Z, v.Position.Z));
        }

        var offset = new float3((min.X + max.X) * 0.5f, min.Y - 0.02f, (min.Z + max.Z) * 0.5f);
        _lampOffsets[kind] = offset;
        return offset;
    }

    /// Every placed light pole's lamp in the world, turned as its pole's placed (as PlacedModel turns the model).
    public IEnumerable<(double X, double Y, double Z, PlacedKind Kind)> LightPoleLamps()
    {
        foreach (var list in _objectsByChunk.Values)
            foreach (var placed in list)
            {
                if (!placed.Object.Kind.IsLightPole() || LampOffset(placed.Object.Kind) is not { } lamp)
                    continue;
                var angle = placed.Object.Heading * Math.PI / 180.0;
                var (fx, fz) = (Math.Cos(angle), Math.Sin(angle));
                // Model x runs along right (face turned a quarter), z along face.
                var (rx, rz) = (fz, -fx);
                yield return (placed.X + rx * lamp.X + fx * lamp.Z, placed.Y + lamp.Y, placed.Z + rz * lamp.X + fz * lamp.Z, placed.Object.Kind);
            }
    }

    public IEnumerable<(double X, float Y, double Z, double Heading, SignalLamps Lamps)> Signals()
    {
        foreach (var list in _objectsByChunk.Values)
        {
            foreach (var placed in list)
            {
                if (placed.Object.Kind == PlacedKind.Signal)
                    yield return (placed.X, placed.Y, placed.Z, placed.Object.Heading, LampsOf(placed.Object.Id));
            }
        }
    }

    /// Binds the ballast's texture for the line pipeline.
    public void BindBallast(CommandBuffer cmd, VkPipelineLayout layout)
    {
        _trackTextures.Refresh();
        _ballastTexture.Bind(cmd, layout);
        _trackTextures.BindLines(cmd, layout);
    }

    /// Draws the ties and tie plates with the part pipeline bound, cell by cell: the full tie and its plates out to
    /// DetailRadius, then the plain tie out to TieReach, past which the ballast paints them.
    public void DrawParts(CommandBuffer cmd, VkPipelineLayout layout, double cx, double cy, double cz, ViewCone view, Action<float3> push)
    {
        _tieTexture.Bind(cmd, layout);
        _trackTextures.BindTies(cmd, layout);
        DrawInstances(cmd, cx, cy, cz, view, push, c => (c.Ties, c.TieCells), d => d < DetailRadius ? _tie.Mesh : _tieFar.Mesh);
        _plateTexture.Bind(cmd, layout);
        _trackTextures.BindPlates(cmd, layout);
        DrawInstances(cmd, cx, cy, cz, view, push, c => (c.Plates, c.PlateCells), d => d < PlateDetail ? _plate.Mesh : _plateFar);
        _trackTextures.BindNone(cmd, layout);
    }

    private void DrawInstances(CommandBuffer cmd, double cx, double cy, double cz, ViewCone view, Action<float3> push, Func<Chunk, (BufferEx, int[])> instances,
        Func<double, Mesh?> meshFor)
    {
        foreach (var (key, near) in _drawList)
        {
            if (!near)
                continue;

            var chunk = _chunks[key];
            var (buffer, cells) = instances(chunk);
            if (cells.Length == 0 || cells[^1] == 0 || chunk.Near is not { } lines)
                continue;

            var origin = OriginOf(key);
            // Each cell's sphere, as the shadow casters take it: the cell's square, as high as the chunk's track reaches.
            var height = (lines.BoundsMin.Y + lines.BoundsMax.Y) * 0.5f - (float)cy;
            var radius = CellSize * 0.75f + (lines.BoundsMax.Y - lines.BoundsMin.Y) * 0.5f + 3f;
            var pushed = false;

            // Runs of cells wanting the same mesh go in one draw.
            Mesh? runMesh = null;
            var runStart = 0;
            for (var c = 0; c <= Cells * Cells; c++)
            {
                Mesh? mesh = null;
                if (c < Cells * Cells)
                {
                    var x0 = origin.X + c % Cells * CellSize;
                    var z0 = origin.Z + c / Cells * CellSize;
                    var dx = Math.Max(0.0, Math.Max(x0 - cx, cx - (x0 + CellSize)));
                    var dz = Math.Max(0.0, Math.Max(z0 - cz, cz - (z0 + CellSize)));
                    var distance = Math.Sqrt(dx * dx + dz * dz);
                    var centre = new float3((float)(x0 + CellSize * 0.5f - cx), height, (float)(z0 + CellSize * 0.5f - cz));
                    mesh = distance < TieReach && view.Sees(centre, radius) ? meshFor(distance) : null;
                }

                if (mesh == runMesh)
                    continue;

                if (runMesh is not null && cells[c] > cells[runStart])
                {
                    if (!pushed)
                    {
                        push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));
                        pushed = true;
                    }

                    runMesh.DrawInstanced(cmd, buffer, cells[c] - cells[runStart], cells[runStart]);
                }
                runMesh = mesh;
                runStart = c;
            }
        }
    }

    /// A plain block filling a box (model space), its faces each mapped over the whole texture.
    private static Mesh BlockMesh(float3 min, float3 max)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        void Face(float3 a, float3 b, float3 c, float3 d, float3 n)
        {
            var first = (uint)vertices.Count;
            vertices.Add(new Vertex(a, n, new float2(0f, 1f)));
            vertices.Add(new Vertex(b, n, new float2(1f, 1f)));
            vertices.Add(new Vertex(c, n, new float2(1f, 0f)));
            vertices.Add(new Vertex(d, n, new float2(0f, 0f)));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        var (l, h) = (min, max);
        Face(new(l.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, l.Z), new(l.X, h.Y, l.Z), new(0f, 1f, 0f));
        Face(new(l.X, l.Y, h.Z), new(h.X, l.Y, h.Z), new(h.X, h.Y, h.Z), new(l.X, h.Y, h.Z), new(0f, 0f, 1f));
        Face(new(h.X, l.Y, l.Z), new(l.X, l.Y, l.Z), new(l.X, h.Y, l.Z), new(h.X, h.Y, l.Z), new(0f, 0f, -1f));
        Face(new(h.X, l.Y, h.Z), new(h.X, l.Y, l.Z), new(h.X, h.Y, l.Z), new(h.X, h.Y, h.Z), new(1f, 0f, 0f));
        Face(new(l.X, l.Y, l.Z), new(l.X, l.Y, h.Z), new(l.X, h.Y, h.Z), new(l.X, h.Y, l.Z), new(-1f, 0f, 0f));
        Face(new(l.X, l.Y, l.Z), new(h.X, l.Y, l.Z), new(h.X, l.Y, h.Z), new(l.X, l.Y, h.Z), new(0f, -1f, 0f));
        return Mesh.Create(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
    }

    private static void Release(BufferEx buffer)
    {
        if (!buffer.IsNull())
            Gpu.Defer(buffer.Dispose);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        foreach (var sw in _switches.Values)
            sw.Blades?.Dispose();
        foreach (var chunk in _chunks.Values)
        {
            chunk.Far?.Dispose();
            DropNear(chunk);
        }

        _chunks.Clear();
        _tieTexture.Dispose();
        _plateTexture.Dispose();
        _ballastTexture.Dispose();
        _tie.Dispose();
        _tieFar.Dispose();
        _plate.Dispose();
        _plateFar.Dispose();
        _ballast.Dispose();
        _trackTextures.Dispose();
        _fallback?.Dispose();
    }
}
