using RailWerkz.Vulkan;

namespace Highball.World;

/// The terrain's detail textures from Content/Textures/Terrain, bound for terrain.frag in this order: grass albedo and
/// normals, bare earth albedo and normals, cliff albedo and normals. The albedos are detail to multiply the land
/// cover's colour by (the shader divides out each one's average), the normals relief for the light.
public sealed class GroundTextures : IDisposable
{
    public const int Count = 6;

    private static readonly string[] Materials = ["Grass", "BareSand", "Cliffs"];

    private readonly Texture2D[] _textures;

    public TextureBinding Binding { get; }

    public GroundTextures()
    {
        _textures = [.. Materials.SelectMany(m => (Texture2D[])[
            Texture2D.LoadDds($"Terrain/{m}/{m}_Albedo.dds", srgb: true),
            Texture2D.LoadDds($"Terrain/{m}/{m}_Normal.dds", srgb: false),
        ])];
        Binding = new TextureBinding(_textures);
    }

    public void Dispose()
    {
        Binding.Dispose();
        foreach (var texture in _textures)
            texture.Dispose();
    }
}
