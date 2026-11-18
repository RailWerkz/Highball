using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Something the world renderer can draw whole: a mesh and, per material, its textures and PBR factors.
public sealed class RenderModel : IDisposable
{
    /// SeeThrough: drawn after everything solid, over what's behind it (glass), and casting no shadow.
    public readonly record struct Part(int FirstIndex, int IndexCount, TextureBinding Textures, float4 BaseColor, float Metallic, float Roughness, float AlphaCutoff,
        bool SeeThrough = false);

    private readonly List<Part> _parts = [];
    private readonly List<TextureBinding> _bindings = [];
    private readonly IDisposable[] _owned;
    private Texture2D? _white;

    public Mesh Mesh { get; }
    public IReadOnlyList<Part> Parts => _parts;

    /// It has see-through parts (Part.SeeThrough), to be drawn after the solid world.
    public bool HasSeeThrough { get; private set; }

    private readonly List<(RenderModel Model, float From)> _lods = [];

    /// Gives it a lower-detail version to draw from a distance on (metres from the camera), which it then owns.
    public void AddLod(RenderModel lod, float from)
    {
        _lods.Add((lod, from));
        _lods.Sort((a, b) => a.From.CompareTo(b.From));
    }

    /// The version to draw this far from the camera: itself near, its lower-detail ones further out.
    public RenderModel ForDistance(float distance)
    {
        var chosen = this;
        foreach (var (model, from) in _lods)
            if (distance >= from)
                chosen = model;
        return chosen;
    }

    /// Its solid parts as the fewest runs of the index buffer: parts lying end to end merged, so one drawn where materials don't
    /// matter (shadows, motion) is a call for each break between them (a see-through part, or parts stored apart), not one a part.
    /// (See-through parts have no shape there: light passes them.)
    public IReadOnlyList<(int FirstIndex, int IndexCount)> Runs
    {
        get
        {
            if (_runs is not null)
                return _runs;
            var runs = new List<(int FirstIndex, int IndexCount)>();
            foreach (var part in _parts.Where(p => !p.SeeThrough && p.IndexCount > 0).OrderBy(p => p.FirstIndex))
            {
                if (runs.Count > 0 && runs[^1].FirstIndex + runs[^1].IndexCount == part.FirstIndex)
                    runs[^1] = (runs[^1].FirstIndex, runs[^1].IndexCount + part.IndexCount);
                else
                    runs.Add((part.FirstIndex, part.IndexCount));
            }

            return _runs = runs;
        }
    }

    private List<(int FirstIndex, int IndexCount)>? _runs;

    /// Draws every instance of a run in as few calls as the parts allow, materials ignored: the solid parts only.
    public void DrawShape(CommandBuffer cmd, BufferEx instances, int count, int first)
    {
        foreach (var (firstIndex, indexCount) in Runs)
            Mesh.DrawRangeInstanced(cmd, instances, firstIndex, indexCount, count, first);
    }

    /// A loaded model, which this now owns.
    public RenderModel(Model model)
    {
        Mesh = model.Mesh;
        _owned = [model];
        foreach (var sub in model.SubMeshes)
        {
            var surface = model.Surfaces[sub.Material];
            _parts.Add(new Part(sub.FirstIndex, sub.IndexCount, Bind(surface.Texture, surface.MetallicRoughness, surface.Normal, surface.Occlusion), surface.BaseColor, surface.Metallic, surface.Roughness, surface.AlphaCutoff, surface.Blend));
            HasSeeThrough |= surface.Blend;
        }
    }

    /// A generated mesh of one material. It and anything in owned (its texture, say) now belong to this.
    public RenderModel(Mesh mesh, Texture2D? albedo, float4 baseColor, float metallic, float roughness, params IDisposable[] owned)
    {
        Mesh = mesh;
        _owned = [mesh, .. owned];
        _parts.Add(new Part(0, mesh.IndexCount, Bind(albedo, null, null, null), baseColor, metallic, roughness, 0f));
    }

    private TextureBinding Bind(Texture2D? albedo, Texture2D? metallicRoughness, Texture2D? normal, Texture2D? occlusion)
    {
        Texture2D White() => _white ??= Texture2D.Solid(new byte4(255, 255, 255, 255));
        // (Fully lit by the sky where there's no occlusion map: white.)
        var binding = new TextureBinding(albedo ?? White(), metallicRoughness ?? White(), normal ?? White(), occlusion ?? White());
        _bindings.Add(binding);
        return binding;
    }

    /// Each part's textures' slots in the MaterialTable (albedo, metallic-roughness, normal, occlusion) and whether it has a normal
    /// map, taken the first time they're drawn and given back when this goes.
    public (int Albedo, int MetalRough, int Normal, int Occlusion, bool HasNormal)[] Slots
    {
        get
        {
            if (_slots is not null)
                return _slots;
            _slots = new (int, int, int, int, bool)[_parts.Count];
            for (var i = 0; i < _parts.Count; i++)
            {
                var t = _parts[i].Textures.Textures;
                _slots[i] = (MaterialTable.Acquire(t[0]), MaterialTable.Acquire(t[1]), MaterialTable.Acquire(t[2]), MaterialTable.Acquire(t[3]), t[2].Width > 1);
            }

            return _slots;
        }
    }

    private (int Albedo, int MetalRough, int Normal, int Occlusion, bool HasNormal)[]? _slots;

    public void Dispose()
    {
        if (_slots is not null)
            foreach (var part in _parts)
                foreach (var texture in part.Textures.Textures)
                    MaterialTable.Release(texture);
        _slots = null;
        foreach (var (lod, _) in _lods)
            lod.Dispose();
        foreach (var binding in _bindings)
            binding.Dispose();
        foreach (var owned in _owned)
            owned.Dispose();
        _white?.Dispose();
    }
}

/// One draw of a RenderModel: where it stands in world metres, its turn about the vertical (radians) and size. A material
/// override replaces the model's own factors, for test objects sharing one mesh. Rotation, if given, is a whole orientation
/// (a unit quaternion, x y z w) in place of Yaw, for things that pitch and roll (rolling stock on grades). At, if given, is
/// the position in doubles in place of Position, for things that move far from the origin and must not shake.
/// PreviousRotation and PreviousAt, if given, are where it was drawn last frame: it's moving, and temporal anti-aliasing is
/// told how (MotionVectors), so it follows it rather than smearing it into what was behind.
public readonly record struct ModelDraw(RenderModel Model, float3 Position, float Yaw = 0f, float Scale = 1f, float4? BaseColor = null, float? Metallic = null, float? Roughness = null, bool CastsShadow = true,
    float4? Rotation = null, double3? At = null, float4? PreviousRotation = null, double3? PreviousAt = null)
{
    /// It moved since last frame, and says how. Standing stock that's given its last place (the same) isn't: the motion
    /// vectors pass needn't draw it again, and temporal anti-aliasing reprojects it as it does everything still.
    public bool Moving => PreviousAt is { } was && (At is not { } at || !was.Equals(at) || (PreviousRotation is { } turn && !turn.Equals(Orientation)));

    /// The orientation as drawn.
    public float4 Orientation
    {
        get
        {
            if (Rotation is { } rotation)
                return rotation;
            var (sin, cos) = MathF.SinCos(Yaw * 0.5f);
            return new float4(0f, sin, 0f, cos);
        }
    }

    /// Where it stands relative to the camera, worked out in doubles.
    public float3 RelativeTo(double x, double y, double z) => At is { } at
        ? new float3((float)(at.X - x), (float)(at.Y - y), (float)(at.Z - z))
        : new float3((float)(Position.X - x), (float)(Position.Y - y), (float)(Position.Z - z));
}
