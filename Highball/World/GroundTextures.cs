using RailWerkz.Vulkan;

namespace Highball.World;

/// The terrain's detail textures from Content/Terrain, bound for terrain.frag in this order: grass albedo and
/// normals, bare earth albedo and normals, cliff albedo and normals, then the grass, bare earth and cliff maps of ambient
/// occlusion (red) and roughness (green), then desert mountain and beach albedo, normals and those maps, then grass and earth
/// displacement, for blending them by height. The albedos are detail to multiply the land cover's colour by (the shader divides
/// out each one's average), except the beach's, which is its own colour; the normals relief for the light. Then the ballast's
/// three maps, and last the broad noise (BroadNoise).
public sealed class GroundTextures : IDisposable
{
    public const int Count = 21;

    /// How many of the noise's lattice cells the broad noise texture holds across before it repeats (terrain.frag's NoisePeriod).
    public const int NoisePeriod = 32;
    private const int NoiseSize = 1024;

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
        .. BallastMaps(), BroadNoise()];
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

    /// fbm of value noise (noise.glsl's, five octaves, each half the size and half the weight of the last) that repeats every
    /// NoisePeriod cells, for terrain.frag's broad patterns: read from a texture, the GPU's mips fade the finer octaves out with
    /// distance as the shader's fbm did by hand, at one read instead of twenty hashes. Repeating every 2.7 km and 8 km at the
    /// scales it's laid at, it can't be seen to. Made once, here (about 20 ms on a few cores).
    private static Texture2D BroadNoise()
    {
        var pixels = new byte[NoiseSize * NoiseSize * 4];
        Parallel.For(0, NoiseSize, y =>
        {
            for (var x = 0; x < NoiseSize; x++)
            {
                var i = (y * NoiseSize + x) * 4;
                // Red and green: two of it, unlike each other.
                for (var channel = 0; channel < 2; channel++)
                {
                    var (px, py) = ((double)x / NoiseSize * NoisePeriod, (double)y / NoiseSize * NoisePeriod);
                    double sum = 0, total = 0, amplitude = 0.5;
                    for (var octave = 0; octave < 5; octave++)
                    {
                        var period = NoisePeriod << octave;
                        sum += Noise(px, py, period, (uint)(octave + channel * 16)) * amplitude;
                        total += amplitude;
                        (px, py) = (px * 2.0, py * 2.0);
                        amplitude *= 0.5;
                    }

                    pixels[i + channel] = (byte)Math.Clamp(Math.Round(sum / total * 255.0), 0, 255);
                }

                pixels[i + 3] = 255;
            }
        });
        return Texture2D.FromPixels(NoiseSize, NoiseSize, pixels, repeat: true, mipmaps: true, name: "ground.broadnoise");
    }

    /// Value noise whose lattice wraps every period cells (smoothstep between the corners' hashes, as noise.glsl's).
    private static double Noise(double x, double y, int period, uint seed)
    {
        var (cx, cy) = (Math.Floor(x), Math.Floor(y));
        var (fx, fy) = (x - cx, y - cy);
        var (ux, uy) = (fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy));
        var (ix, iy) = ((int)cx, (int)cy);
        double H(int a, int b) => Hash((uint)(((a % period) + period) % period), (uint)(((b % period) + period) % period), seed);
        var bottom = H(ix, iy) + (H(ix + 1, iy) - H(ix, iy)) * ux;
        var top = H(ix, iy + 1) + (H(ix + 1, iy + 1) - H(ix, iy + 1)) * ux;
        return bottom + (top - bottom) * uy;
    }

    /// noise.glsl's integer hash, with a seed for each octave.
    private static double Hash(uint x, uint y, uint seed)
    {
        var (vx, vy) = (x * 1664525u + 1013904223u + seed * 374761393u, y * 1664525u + 1013904223u);
        vx += vy * 1664525u;
        vy += vx * 1664525u;
        vx ^= vx >> 16;
        vy ^= vy >> 16;
        vx += vy * 1664525u;
        vy += vx * 1664525u;
        vx ^= vx >> 16;
        vy ^= vy >> 16;
        return (vx ^ vy) * (1.0 / 4294967296.0);
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
