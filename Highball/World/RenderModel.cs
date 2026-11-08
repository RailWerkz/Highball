using RailWerkz.Vulkan;

namespace Highball.World;

/// Something the world renderer can draw whole: a mesh and, per material, its textures and PBR factors.
public sealed class RenderModel : IDisposable
{
    public readonly record struct Part(int FirstIndex, int IndexCount, TextureBinding Textures, float4 BaseColor, float Metallic, float Roughness, float AlphaCutoff);

    private readonly List<Part> _parts = [];
    private readonly List<TextureBinding> _bindings = [];
    private readonly IDisposable[] _owned;
    private Texture2D? _white;

    public Mesh Mesh { get; }
    public IReadOnlyList<Part> Parts => _parts;

    /// A loaded model, which this now owns.
    public RenderModel(Model model)
    {
        Mesh = model.Mesh;
        _owned = [model];
        foreach (var sub in model.SubMeshes)
        {
            var surface = model.Surfaces[sub.Material];
            _parts.Add(new Part(sub.FirstIndex, sub.IndexCount, Bind(surface.Texture, surface.MetallicRoughness, surface.Normal), surface.BaseColor, surface.Metallic, surface.Roughness, surface.AlphaCutoff));
        }
    }

    /// A generated mesh of one material. It and anything in owned (its texture, say) now belong to this.
    public RenderModel(Mesh mesh, Texture2D? albedo, float4 baseColor, float metallic, float roughness, params IDisposable[] owned)
    {
        Mesh = mesh;
        _owned = [mesh, .. owned];
        _parts.Add(new Part(0, mesh.IndexCount, Bind(albedo, null, null), baseColor, metallic, roughness, 0f));
    }

    private TextureBinding Bind(Texture2D? albedo, Texture2D? metallicRoughness, Texture2D? normal)
    {
        Texture2D White() => _white ??= Texture2D.Solid(new byte4(255, 255, 255, 255));
        var binding = new TextureBinding(albedo ?? White(), metallicRoughness ?? White(), normal ?? White());
        _bindings.Add(binding);
        return binding;
    }

    public void Dispose()
    {
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
    /// It moved since last frame, and says how.
    public bool Moving => PreviousAt is not null;

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
