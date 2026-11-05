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
/// override replaces the model's own factors, for test objects sharing one mesh.
public readonly record struct ModelDraw(RenderModel Model, float3 Position, float Yaw = 0f, float Scale = 1f, float4? BaseColor = null, float? Metallic = null, float? Roughness = null, bool CastsShadow = true);
