using RailWerkz.Vulkan;

namespace Highball.World;

/// The terrain's detail textures from Content/Textures/Terrain, bound for terrain.frag in this order: grass albedo and
/// normals, bare earth albedo and normals, cliff albedo and normals, then the grass, bare earth and cliff maps of ambient
/// occlusion (red) and roughness (green), then desert mountain and beach albedo, normals and those maps, then grass and earth
/// displacement, for blending them by height. The albedos are detail to multiply the land cover's colour by (the shader divides
/// out each one's average), except the beach's, which is its own colour; the normals relief for the light.
public sealed class GroundTextures : IDisposable
{
    public const int Count = 20;

    private static readonly string[] Materials = ["Grass", "BareSand", "Cliffs"];

    private Texture2D[] _textures = [];
    private TextureBinding? _binding;
    private int _skip = -1;

    /// The set of textures, loaded again first if the setting for their detail has changed (it waits: it's only asked for from the menu).
    public TextureBinding Binding
    {
        get
        {
            var wanted = Math.Clamp(RailWerkz.Core.Settings.Settings.Current.TerrainTextureDetail, 0, 2);
            if (wanted != _skip)
                Load(wanted);
            return _binding!;
        }
    }

    public GroundTextures() => Load(Math.Clamp(RailWerkz.Core.Settings.Settings.Current.TerrainTextureDetail, 0, 2));

    private void Load(int skip)
    {
        Release();
        _skip = skip;
        _textures = [.. Materials.SelectMany(m => (Texture2D[])[
            Texture2D.LoadDds($"Terrain/{m}/{m}_Albedo.dds", srgb: true, skip),
            Texture2D.LoadDds($"Terrain/{m}/{m}_Normal.dds", srgb: false, skip),
        ]), .. Materials.Select(m => Texture2D.LoadDds($"Terrain/{m}/{m}_AoRoughMetal.dds", srgb: false, skip)),
        .. ((string[])["DesertMountains", "Beach"]).SelectMany(m => (Texture2D[])[
            Texture2D.LoadDds($"Terrain/{m}/{m}_Albedo.dds", srgb: true, skip),
            Texture2D.LoadDds($"Terrain/{m}/{m}_Normal.dds", srgb: false, skip),
            Texture2D.LoadDds($"Terrain/{m}/{m}_AoRoughMetal.dds", srgb: false, skip),
        ]),
        Texture2D.LoadDds("Terrain/Grass/Grass_Displacement.dds", srgb: false, skip),
        Texture2D.LoadDds("Terrain/BareSand/BareSand_Displacement.dds", srgb: false, skip),
        .. BallastMaps()];
        _binding = new TextureBinding(_textures);
    }

    /// The ballast's rock (TrackMaterials' ballast texture: colour, normal, and the packed maps with its height), for the stones
    /// run out over the ground beside the track.
    private static Texture2D[] BallastMaps()
    {
        if (TrackMaterials.Decode(TrackMaterials.Current.TextureOf(TrackMaterials.Slot.Ballast), "1K") is { } d)
            return [
                Texture2D.FromPixels(d.Width, d.Height, d.Color, repeat: true, mipmaps: true, srgb: true, name: "ground.ballast.color"),
                Texture2D.FromPixels(d.Width, d.Height, d.Normal, repeat: true, mipmaps: true, name: "ground.ballast.normal"),
                Texture2D.FromPixels(d.Width, d.Height, d.Packed, repeat: true, mipmaps: true, name: "ground.ballast.packed"),
            ];
        return [Texture2D.Solid(new byte4(110, 105, 100, 255)), Texture2D.Solid(new byte4(128, 128, 255, 255)), Texture2D.Solid(new byte4(200, 230, 0, 128))];
    }

    private void Release()
    {
        _binding?.Dispose();
        foreach (var texture in _textures)
            texture.Dispose();
    }

    public void Dispose()
    {
        Release();
    }
}
