using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Brutal.StbApi;

namespace Highball.Routes;

/// One built tile: Tiles/I_J/Tile.json (what it is and how it was made), Heights.bin.gz (metres above sea level,
/// Samples x Samples, rows north to south) and Cover.png (ESA WorldCover classes, one byte per 10 m cell).
public sealed class RouteTile
{
    public sealed class Info
    {
        public int I { get; set; }
        public int J { get; set; }
        public int Samples { get; set; }
        public double Spacing { get; set; }
        public int CoverCells { get; set; }
        public float MinHeight { get; set; }
        public float MaxHeight { get; set; }
        public string Roads { get; set; } = "";
        public string Rails { get; set; } = "";
        public string Built { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public Info Meta { get; }
    public float[] Heights { get; }
    public byte[] Cover { get; }

    public RouteTile(Info meta, float[] heights, byte[] cover)
    {
        Meta = meta;
        Heights = heights;
        Cover = cover;
    }

    /// Heights as gzipped decimetres above the tile's lowest point (6.5 km of relief, more than any tile has).
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var packed = new ushort[Heights.Length];
        for (var i = 0; i < packed.Length; i++)
            packed[i] = (ushort)Math.Clamp(MathF.Round((Heights[i] - Meta.MinHeight) * 10f), 0f, 65535f);

        using (var file = File.Create(Path.Combine(folder, "Heights.bin.gz")))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(MemoryMarshal.AsBytes(packed.AsSpan()));

        Stb.WritePng(Path.Combine(folder, "Cover.png"), Meta.CoverCells, Meta.CoverCells, 1, Cover);
        File.WriteAllText(Path.Combine(folder, "Tile.json"), JsonSerializer.Serialize(Meta, Options));
    }

    /// Just Tile.json: what the tile is and how it was made.
    public static Info LoadInfo(string folder) => JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(folder, "Tile.json")))!;

    public static RouteTile Load(string folder)
    {
        var meta = JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(folder, "Tile.json")))!;

        using var input = new GZipStream(File.OpenRead(Path.Combine(folder, "Heights.bin.gz")), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        var packed = MemoryMarshal.Cast<byte, ushort>(output.ToArray());
        var heights = new float[meta.Samples * meta.Samples];
        for (var i = 0; i < heights.Length && i < packed.Length; i++)
            heights[i] = meta.MinHeight + packed[i] / 10f;

        using var image = Stb.Load(Path.Combine(folder, "Cover.png"), 1);
        return new RouteTile(meta, heights, image.AsSpan().ToArray());
    }
}
