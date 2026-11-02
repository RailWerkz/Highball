using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
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

/// One placed part (a tie, a tie plate): where it stands relative to its chunk and a random tint, and the way it faces.
[StructLayout(LayoutKind.Sequential)]
public struct TieInstance(float4 positionRandom, float4 forward)
{
    public float4 PositionRandom = positionRandom;
    public float4 Forward = forward;

    public static readonly VertexAttribute[] Layout = [new(3, 4, 0), new(4, 4, 16)];
}

/// The materials line.frag knows.
public enum LineMaterial { Ballast, Rail, Asphalt, Gravel, Concrete }

/// The roads and rails in 3D, built from NetworkLines in square chunks on worker threads and streamed by distance.
/// Each chunk has a far mesh (ballast beds, roads, bridges: drawn out to FarRadius) and a near one (rails, and the
/// tie and tie plate models placed as instances: out to NearRadius, past which the ballast shader paints the track in).
/// The tie, the plate and the ballast's texture are made in Blender by Tools/Blender/track_parts.py.
///
/// Every shape is a cross-section swept along the line's centreline at its formation height: the ballast a
/// trapezoid with 1:1.5 shoulders, the rail a 136 lb profile, roads a crowned-flat deck with gravel shoulders,
/// bridges a concrete deck with parapets and piers down to the ground.
public sealed class NetworkMeshes : IDisposable
{
    public const float ChunkSize = 500f;
    public float NearRadius { get; set; } = 1000f;
    /// Out to here ties are the full model and have their plates; past it, a decimated tie and no plates.
    public float DetailRadius { get; set; } = 120f;
    public float FarRadius { get; set; } = 6000f;
    private const int MaxUploadsPerFrame = 4;

    private const float TieSpacing = 0.495f;   // 19.5 in
    private const float TieRise = 0.02f;       // tie top above the crib ballast
    private const float PlateDepth = 0.014f;   // the plate's rail seat above the tie
    private const float RoadRise = 0.06f;
    private const float RailHeadHalf = 0.0373f;
    private const float RailTop = 0.1857f;
    private const float Wrap = 1200f;
    /// Parts are sorted into Cells x Cells squares of each chunk, so each square can be drawn in the detail its
    /// distance calls for.
    private const int Cells = 10;
    private const float CellSize = ChunkSize / Cells;

    /// 136 lb rail, clockwise from the outside of the foot (x across, y up).
    private static readonly (float U, float Y)[] RailProfile =
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

    private sealed record Built(ChunkKey Key, bool Near, LineVertex[] Vertices, uint[] Indices, TieInstance[] Ties, TieInstance[] Plates,
        int[] TieCells, int[] PlateCells);

    private NetworkLines _lines;
    /// Chunk meshes made from lines an edit has since changed: kept on screen until their rebuild arrives.
    private readonly HashSet<(ChunkKey, bool)> _stale = [];
    private readonly TerrainSource _source;
    private readonly Dictionary<ChunkKey, List<Run>> _runs = [];
    private readonly Dictionary<ChunkKey, Chunk> _chunks = [];
    private readonly HashSet<(ChunkKey, bool)> _building = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly List<(ChunkKey Key, bool Near)> _drawList = [];
    private readonly Model _tie;
    private readonly Model _tieFar;
    private readonly Model _plate;
    private readonly Texture2D _ballast;
    private readonly TextureBinding _tieTexture;
    private readonly TextureBinding _plateTexture;
    private readonly TextureBinding _ballastTexture;
    private readonly Texture2D? _fallback;
    private long _frame;

    public int DrawnChunks => _drawList.Count;
    public int Building => _building.Count;

    public NetworkMeshes(TerrainSource source)
    {
        _source = source;
        _lines = source.Lines!;
        FindRuns();

        _tie = Model.Load("Track/tie.glb");
        _tieFar = Model.Load("Track/tie_far.glb");
        _plate = Model.Load("Track/plate.glb");
        _ballast = Texture2D.Load("Track/ballast.png", srgb: true);
        _tieTexture = new TextureBinding(_tie.Surfaces[0].Texture ?? (_fallback = Texture2D.Solid(new byte4(40, 32, 26, 255))));
        _plateTexture = new TextureBinding(_plate.Surfaces[0].Texture ?? (_fallback ??= Texture2D.Solid(new byte4(60, 36, 24, 255))));
        _ballastTexture = new TextureBinding(_ballast);
    }

    /// Every segment goes to the chunk its midpoint is in; consecutive ones in the same chunk make a run.
    private void FindRuns()
    {
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
    }

    /// Takes rebuilt lines in: the chunks over the given areas (world metres) are built again, the rest kept, and
    /// chunks no line crosses any more let go.
    public void Replace(NetworkLines lines, IEnumerable<(double X0, double Z0, double X1, double Z1)> areas)
    {
        _lines = lines;
        FindRuns();

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
        for (var n = 0; n < MaxUploadsPerFrame && _built.TryDequeue(out var built); n++)
        {
            _building.Remove((built.Key, built.Near));
            if (!_chunks.TryGetValue(built.Key, out var chunk))
                _chunks[built.Key] = chunk = new Chunk { Used = _frame };

            var mesh = built.Indices.Length > 0 ? Mesh.Create<LineVertex>(built.Vertices, LineVertex.Layout, built.Indices) : null;
            if (built.Near)
            {
                chunk.Near?.Dispose();
                chunk.Near = mesh;
                Release(chunk.Ties);
                chunk.Ties = built.Ties.Length > 0
                    ? Buffers.Upload(MemoryMarshal.AsBytes(built.Ties.AsSpan()), VkBufferUsageFlags.VertexBufferBit, "network.ties")
                    : default;
                chunk.TieCount = built.Ties.Length;
                Release(chunk.Plates);
                chunk.Plates = built.Plates.Length > 0
                    ? Buffers.Upload(MemoryMarshal.AsBytes(built.Plates.AsSpan()), VkBufferUsageFlags.VertexBufferBit, "network.plates")
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
                Request(key, false);
            if (chunk is { HasFar: true } && distance < FarRadius)
                _drawList.Add((key, false));

            if (distance < NearRadius * 1.2)
            {
                if (chunk is null || !chunk.HasNear || _stale.Contains((key, true)))
                    Request(key, true);
                if (chunk is { HasNear: true } && distance < NearRadius)
                    _drawList.Add((key, true));
            }
            else if (chunk is not null && distance > NearRadius * 1.5)
                DropNear(chunk);
        }

        Evict();
    }

    private static double Distance(ChunkKey k, double cx, double cy, double cz)
    {
        var x0 = k.X * ChunkSize;
        var z0 = k.Z * ChunkSize;
        var dx = Math.Max(0.0, Math.Max(x0 - cx, cx - (x0 + ChunkSize)));
        var dz = Math.Max(0.0, Math.Max(z0 - cz, cz - (z0 + ChunkSize)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private void Request(ChunkKey key, bool near)
    {
        if (!_building.Add((key, near)))
            return;

        _stale.Remove((key, near));
        var runs = _runs[key];
        var lines = _lines;
        Task.Run(() => _built.Enqueue(near ? BuildNear(key, runs, lines) : BuildFar(key, runs, lines)));
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
        foreach (var (key, chunk) in _chunks.Where(c => _frame - c.Value.Used > 300).ToList())
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

    private static float Code(LineMaterial material, int style = 0) => (int)material + 8 * style;

    /// Sweeps a cross-section (u across, y up from the formation; listed so the solid is on the right) along points
    /// first..last, flat-shaded across and smooth along. material(edge) picks each edge's material; value(edge, u, y)
    /// its Data.w.
    private static void Sweep(Builder builder, NetworkLine line, int first, int last, (float U, float Y)[] section, Func<int, float> code,
        Func<int, float, float, float> value, float offset = 0f, float rise = 0f)
    {
        var edges = section.Length - 1;
        var start = (uint)builder.Vertices.Count;
        for (var p = first; p <= last; p++)
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
                builder.Add(center + right * (offset + u0) + new float3(0f, rise + y0, 0f), normal, new float4(offset + u0, v, code(e), value(e, u0, y0)));
                builder.Add(center + right * (offset + u1) + new float3(0f, rise + y1, 0f), normal, new float4(offset + u1, v, code(e), value(e, u1, y1)));
            }
        }

        var ring = (uint)(edges * 2);
        for (var p = 0; p < last - first; p++)
        {
            for (var e = 0; e < edges; e++)
            {
                var a = start + (uint)p * ring + (uint)e * 2;
                builder.Quad(a, a + 1, a + ring, a + ring + 1);
            }
        }
    }

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
        foreach (var run in runs)
        {
            var line = lines.Lines[run.Line];
            foreach (var (first, last, structure) in Spans(line, run))
            {
                if (structure == Structure.Tunnel)
                    continue;

                if (line.Rail)
                    FarRail(builder, line, first, last, structure == Structure.Bridge);
                else
                    Road(builder, line, first, last, structure == Structure.Bridge);

                if (structure == Structure.Bridge)
                    Piers(builder, line, first, last);
            }
        }

        return new Built(key, false, [.. builder.Vertices], [.. builder.Indices], [], [], [], []);
    }

    private static void FarRail(Builder builder, NetworkLine line, int first, int last, bool bridge)
    {
        if (line.Embedded)
            return;

        var w = line.BallastHalfTop;
        var h = NetworkLine.BallastHeight;
        var railOffset = line.Gauge * 0.5f + RailHeadHalf;
        var ballast = Code(LineMaterial.Ballast, line.Tracks + (line.Disused ? 8 : 0));

        if (!bridge)
        {
            // The ballast, and a gravel walkway either side out to the edge of the flattened ground (between tracks
            // a few metres apart, the walkways overlap into one bed).
            const float walk = 0.02f;
            var slope = (h - walk) * NetworkLine.BallastSlope;
            var f = MathF.Max(line.Formation - 0.5f, w + slope + 0.3f);
            var gravel = Code(LineMaterial.Gravel);
            Sweep(builder, line, first, last, [(-f, walk), (-w - slope, walk), (-w, h), (w, h), (w + slope, walk), (f, walk)],
                e => e is 0 or 4 ? gravel : ballast, (_, _, _) => railOffset);
            return;
        }

        // A ballasted concrete deck with low parapets.
        const float deck = 1.8f;
        var wall = h + 0.5f;
        var concrete = Code(LineMaterial.Concrete);
        (float, float)[] section =
        [
            (-w - 0.9f, -deck), (-w - 0.9f, wall), (-w - 0.6f, wall), (-w - 0.6f, 0f), (-w, h), (w, h), (w + 0.6f, 0f),
            (w + 0.6f, wall), (w + 0.9f, wall), (w + 0.9f, -deck), (-w - 0.9f, -deck),
        ];
        Sweep(builder, line, first, last, section, e => e is 3 or 4 or 5 ? ballast : concrete, (_, _, _) => railOffset);
    }

    private static void Road(Builder builder, NetworkLine line, int first, int last, bool bridge)
    {
        var half = line.Width * 0.5f;
        var marks = line.Unpaved ? 0 : (RoadClass)line.Class switch
        {
            RoadClass.Highway => 3,
            RoadClass.Major => 2,
            RoadClass.Minor => 1,
            _ => 0,
        };
        var surface = line.Unpaved
            ? Code(LineMaterial.Gravel)
            : Code(LineMaterial.Asphalt, line.Lanes + (line.Oneway ? 8 : 0) + marks * 16 + (int)MathF.Round(line.Width * 4f) * 64);
        float Junction(int p) => line.Junction.Length > p ? MathF.Min(line.Junction[p], 1000f) : 1000f;

        // Data.w is per point, so the sweep's value callback can't see it; patch it in after.
        var start = builder.Vertices.Count;
        if (!bridge)
        {
            var gravel = Code(LineMaterial.Gravel);
            Sweep(builder, line, first, last, [(-half - 0.6f, -0.35f), (-half, RoadRise), (half, RoadRise), (half + 0.6f, -0.35f)],
                e => e == 1 ? surface : gravel, (_, _, _) => 0f);
        }
        else
        {
            const float deck = 1.4f;
            const float wall = 0.9f;
            var concrete = Code(LineMaterial.Concrete);
            (float, float)[] section =
            [
                (-half - 0.4f, -deck), (-half - 0.4f, wall), (-half, wall), (-half, RoadRise), (half, RoadRise), (half, wall),
                (half + 0.4f, wall), (half + 0.4f, -deck), (-half - 0.4f, -deck),
            ];
            Sweep(builder, line, first, last, section, e => e == 3 ? surface : concrete, (_, _, _) => 0f);
        }

        var perRing = (builder.Vertices.Count - start) / (last - first + 1);

        // Round caps where the line ends, so ways meeting end to end at an angle overlap instead of leaving notches.
        // A hair below the road so its own surface wins where they overlap; no markings (junction distance 0).
        if (!bridge)
        {
            if (first == 0)
                Cap(builder, line, 0, half, surface);
            if (last == line.Count - 1)
                Cap(builder, line, last, half, surface);
        }

        for (var p = first; p <= last; p++)
        {
            var junction = Junction(p);
            for (var k = 0; k < perRing; k++)
            {
                var index = start + (p - first) * perRing + k;
                var v = builder.Vertices[index];
                v.Data.W = junction;
                builder.Vertices[index] = v;
            }
        }
    }

    private static void Cap(Builder builder, NetworkLine line, int p, float radius, float code)
    {
        const int segments = 16;
        var (center, _) = Frame(line, p, builder.Origin);
        center += new float3(0f, RoadRise - 0.01f, 0f);
        var up = new float3(0f, 1f, 0f);
        var v = line.S[p] % Wrap;
        var hub = builder.Add(center, up, new float4(0f, v, code, 0f));
        for (var i = 0; i < segments; i++)
        {
            var a0 = MathF.Tau * i / segments;
            var a1 = MathF.Tau * (i + 1) / segments;
            var e0 = builder.Add(center + new float3(MathF.Cos(a0), 0f, MathF.Sin(a0)) * radius, up, new float4(0f, v, code, 0f));
            var e1 = builder.Add(center + new float3(MathF.Cos(a1), 0f, MathF.Sin(a1)) * radius, up, new float4(0f, v, code, 0f));
            builder.Indices.AddRange([hub, e0, e1]);
        }
    }

    /// Piers every 25 m or so under a bridge, wherever the deck stands well clear of the ground.
    private void Piers(Builder builder, NetworkLine line, int first, int last)
    {
        var deckWidth = line.Rail ? line.BallastHalfTop * 2f + 1.8f : line.Width + 0.8f;
        var deckDepth = line.Rail ? 1.8f : 1.4f;
        var concrete = new float4(0f, 0f, Code(LineMaterial.Concrete), 0f);
        var span = 25f;
        var next = MathF.Ceiling(line.S[first] / span) * span;
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
                Box(builder, center, right * (deckWidth * 0.3f), forward * 0.75f, ground - 1f, top, concrete);
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
                    foreach (var side in (float[])[-1f, 1f])
                        Sweep(builder, line, first, last, RailProfile, _ => rail, (_, _, y) => y, o + side * railOffset, baseY);

                    if (!line.Embedded)
                        Ties(ties, plates, line, first, last, o, railOffset, builder.Origin, track);
                }
            }
        }

        var (sortedTies, tieCells) = ByCell(ties);
        var (sortedPlates, plateCells) = ByCell(plates);
        return new Built(key, true, [.. builder.Vertices], [.. builder.Indices], sortedTies, sortedPlates, tieCells, plateCells);
    }

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
    private static void Ties(List<TieInstance> ties, List<TieInstance> plates, NetworkLine line, int first, int last, float offset, float railOffset,
        float3 origin, int track)
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
                ties.Add(new TieInstance(new float4(position, random), new float4(forward, 0f)));
                var seat = position + new float3(0f, TieRise, 0f);
                plates.Add(new TieInstance(new float4(seat + right * railOffset, random), new float4(forward, 0f)));
                plates.Add(new TieInstance(new float4(seat - right * railOffset, random), new float4(-forward, 0f)));
                s += TieSpacing;
            }
        }
    }

    // --- Drawing. ---

    /// Draws the far meshes and rails with the line pipeline bound; push writes each chunk's origin relative to the
    /// camera.
    public void DrawLines(CommandBuffer cmd, double cx, double cy, double cz, Action<float3> push)
    {
        foreach (var (key, near) in _drawList)
        {
            var chunk = _chunks[key];
            var mesh = near ? chunk.Near : chunk.Far;
            if (mesh is null)
                continue;

            var origin = OriginOf(key);
            push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));
            mesh.Draw(cmd);
        }
    }

    /// Binds the ballast's texture for the line pipeline.
    public void BindBallast(CommandBuffer cmd, VkPipelineLayout layout) => _ballastTexture.Bind(cmd, layout);

    /// Draws the ties and tie plates with the part pipeline bound: the full models out to DetailRadius, decimated ties
    /// past it, cell by cell.
    public void DrawParts(CommandBuffer cmd, VkPipelineLayout layout, double cx, double cy, double cz, Action<float3> push)
    {
        _tieTexture.Bind(cmd, layout);
        DrawInstances(cmd, cx, cy, cz, push, c => (c.Ties, c.TieCells), detail => detail ? _tie.Mesh : _tieFar.Mesh);
        _plateTexture.Bind(cmd, layout);
        DrawInstances(cmd, cx, cy, cz, push, c => (c.Plates, c.PlateCells), detail => detail ? _plate.Mesh : null);
    }

    private void DrawInstances(CommandBuffer cmd, double cx, double cy, double cz, Action<float3> push, Func<Chunk, (BufferEx, int[])> instances,
        Func<bool, Mesh?> meshFor)
    {
        foreach (var (key, near) in _drawList)
        {
            if (!near)
                continue;

            var (buffer, cells) = instances(_chunks[key]);
            if (cells.Length == 0 || cells[^1] == 0)
                continue;

            var origin = OriginOf(key);
            push(new float3((float)(origin.X - cx), (float)-cy, (float)(origin.Z - cz)));

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
                    mesh = distance < NearRadius ? meshFor(distance < DetailRadius) : null;
                }

                if (mesh == runMesh)
                    continue;

                if (runMesh is not null && cells[c] > cells[runStart])
                    runMesh.DrawInstanced(cmd, buffer, cells[c] - cells[runStart], cells[runStart]);
                runMesh = mesh;
                runStart = c;
            }
        }
    }

    private static void Release(BufferEx buffer)
    {
        if (!buffer.IsNull())
            Gpu.Defer(buffer.Dispose);
    }

    public void Dispose()
    {
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
        _ballast.Dispose();
        _fallback?.Dispose();
    }
}
