using RailWerkz.Vulkan;

namespace Highball.World;

/// The terrain's detail textures from Content/Textures/Terrain, bound for terrain.frag in this order: grass albedo and
/// normals, bare earth albedo and normals, cliff albedo and normals, then the grass, bare earth and cliff maps of ambient
/// occlusion (red) and roughness (green), then desert mountain and beach albedo, normals and those maps, then grass and earth
/// displacement, for blending them by height. The albedos are detail to multiply the land cover's colour by (the shader divides
/// out each one's average), except the beach's, which is its own colour; the normals relief for the light.
public sealed class GroundTextures : IDisposable
{
    public const int Count = 17;

    private static readonly string[] Materials = ["Grass", "BareSand", "Cliffs"];

    private readonly Texture2D[] _textures;

    public TextureBinding Binding { get; }

    public GroundTextures()
    {
        _textures = [.. Materials.SelectMany(m => (Texture2D[])[
            Texture2D.LoadDds($"Terrain/{m}/{m}_Albedo.dds", srgb: true),
            Texture2D.LoadDds($"Terrain/{m}/{m}_Normal.dds", srgb: false),
        ]), .. Materials.Select(m => Texture2D.LoadDds($"Terrain/{m}/{m}_AoRoughMetal.dds", srgb: false)),
        .. ((string[])["DesertMountains", "Beach"]).SelectMany(m => (Texture2D[])[
            Texture2D.LoadDds($"Terrain/{m}/{m}_Albedo.dds", srgb: true),
            Texture2D.LoadDds($"Terrain/{m}/{m}_Normal.dds", srgb: false),
            Texture2D.LoadDds($"Terrain/{m}/{m}_AoRoughMetal.dds", srgb: false),
        ]),
        Texture2D.LoadDds("Terrain/Grass/Grass_Displacement.dds", srgb: false),
        Texture2D.LoadDds("Terrain/BareSand/BareSand_Displacement.dds", srgb: false)];
        Binding = new TextureBinding(_textures);
    }

    public void Dispose()
    {
        Binding.Dispose();
        foreach (var texture in _textures)
            texture.Dispose();
    }
}
