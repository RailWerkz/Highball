using System.Runtime.InteropServices;
using RailWerkz.Vulkan;

namespace Highball.World;

/// A straight length of track on open ground, for scenes with no route under them (the lighting test, the consist
/// editor's preview): grass, a ballast bed, ties with their plates, and two rails along z. Rail top is y = 0, as in the
/// rolling stock's files, so a vehicle drawn at the origin stands on it.
public sealed class DisplayTrack : IDisposable
{
    public const float GroundY = -0.6f;
    private const float BallastY = -0.22f;
    private const float TieTopY = -0.2f; // the rail's foot, less the tie plate's depth
    private const float TieSpacing = 0.6f;
    private const float RailOffset = 1.435f * 0.5f + 0.0373f;

    private readonly List<IDisposable> _owned = [];
    private readonly List<ModelDraw> _draws = [];
    /// The ground, bed and rails, without the ties: what DrawsNear starts from.
    private readonly List<ModelDraw> _fixed = [];
    private RenderModel? _tie, _plate;
    private float _tieHeight, _plateHeight;

    /// Metres either side of z = 0.
    public float HalfLength { get; }

    /// Everything to draw, in place.
    public IReadOnlyList<ModelDraw> Draws => _draws;

    public DisplayTrack(float halfLength = 60f)
    {
        HalfLength = halfLength;

        var grass = Texture2D.LoadDds("Terrain/Grass/Grass_Albedo.dds", srgb: true);
        var ballast = TrackTextures.RockColor();
        // The grass map is detail for a land cover's colour to tint (see GroundTextures), so it brings its own here.
        var ground = Own(new RenderModel(Plane(1500f, 4f), grass, new float4(0.14f, 0.17f, 0.08f, 1f), 0f, 0.95f, grass));
        var bed = Own(new RenderModel(Ballast(), ballast, new float4(1f), 0f, 0.9f, ballast));
        _draws.Add(new ModelDraw(ground, default, CastsShadow: false));
        _draws.Add(new ModelDraw(bed, default, CastsShadow: false));

        // The rail head is worn bright; everything under it has rusted.
        var head = Own(new RenderModel(RailMesh(top: true), null, new float4(0.62f, 0.62f, 0.63f, 1f), 1f, 0.28f));
        var web = Own(new RenderModel(RailMesh(top: false), null, new float4(0.20f, 0.11f, 0.07f, 1f), 0f, 0.8f));
        foreach (var side in (float[])[-1f, 1f])
        {
            _draws.Add(new ModelDraw(head, new float3(side * RailOffset, 0f, 0f)));
            _draws.Add(new ModelDraw(web, new float3(side * RailOffset, 0f, 0f)));
        }

        var tie = _tie = Own(new RenderModel(Model.Load("Track/tie.glb")));
        var plate = _plate = Own(new RenderModel(Model.Load("Track/plate.glb")));
        _tieHeight = tie.Mesh.BoundsMax.Y;
        _plateHeight = plate.Mesh.BoundsMax.Y;
        _fixed.AddRange(_draws);
        // A short track has all its ties in Draws; a long one (a long consist on show) asks DrawsNear for those near the eye.
        if (HalfLength <= 150f)
            AddTies(_draws, -HalfLength, HalfLength);
    }

    private void AddTies(List<ModelDraw> into, float from, float to)
    {
        var first = MathF.Ceiling(Math.Max(from, -HalfLength) / TieSpacing) * TieSpacing;
        for (var z = first; z <= Math.Min(to, HalfLength); z += TieSpacing)
        {
            into.Add(new ModelDraw(_tie!, new float3(0f, TieTopY - _tieHeight, z)));
            foreach (var side in (float[])[-1f, 1f])
                into.Add(new ModelDraw(_plate!, new float3(side * RailOffset, -NetworkMeshes.RailTop - _plateHeight, z)));
        }
    }

    /// The ground, bed and rails, and the ties within reach of z: for a track too long to draw every tie of.
    public void DrawsNear(List<ModelDraw> into, float z, float reach)
    {
        into.AddRange(_fixed);
        AddTies(into, z - reach, z + reach);
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        _owned.Clear();
        _draws.Clear();
    }

    private T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    private static Mesh Plane(float half, float tile)
    {
        var y = GroundY;
        Vertex Corner(float x, float z) => new(new float3(x, y, z), new float3(0f, 1f, 0f), new float2(x / tile, z / tile));
        return Mesh.Create([Corner(-half, -half), Corner(half, -half), Corner(half, half), Corner(-half, half)], [0u, 1u, 2u, 0u, 2u, 3u]);
    }

    /// The track bed: a flat top with sloping shoulders, its stone a metre and a half to the tile.
    private Mesh Ballast()
    {
        (float X, float Y)[] section = [(-3.0f, GroundY), (-1.7f, BallastY), (1.7f, BallastY), (3.0f, GroundY)];
        return Sweep(section, 1.5f);
    }

    /// One rail, foot at -RailTop and head at 0, in two pieces so the head can be a different material.
    private Mesh RailMesh(bool top)
    {
        var profile = NetworkMeshes.RailProfile;
        var section = profile.Select(p => (X: p.U, Y: p.Y - NetworkMeshes.RailTop)).ToArray();
        return top ? Sweep(section[7..11], 1f) : Sweep(section[..8].Concat(section[10..]).ToArray(), 1f, skip: 7);
    }

    /// A section swept along the track, each flat of it shaded on its own. skip leaves out the flat starting at that point.
    private Mesh Sweep((float X, float Y)[] section, float tile, int skip = -1)
    {
        var half = HalfLength;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var along = 0f;
        for (var i = 0; i + 1 < section.Length; i++)
        {
            var (a, b) = (section[i], section[i + 1]);
            var length = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (i != skip)
            {
                var normal = new float3(b.Y - a.Y, a.X - b.X, 0f) / MathF.Max(length, 1e-5f);
                if (normal.Y < 0f || (normal.Y == 0f && (a.X + b.X) * normal.X < 0f))
                    normal = -normal;

                var first = (uint)vertices.Count;
                vertices.Add(new Vertex(new float3(a.X, a.Y, -half), normal, new float2(along / tile, -half / tile)));
                vertices.Add(new Vertex(new float3(b.X, b.Y, -half), normal, new float2((along + length) / tile, -half / tile)));
                vertices.Add(new Vertex(new float3(b.X, b.Y, half), normal, new float2((along + length) / tile, half / tile)));
                vertices.Add(new Vertex(new float3(a.X, a.Y, half), normal, new float2(along / tile, half / tile)));
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }

            along += length;
        }

        return Mesh.Create(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }
}
