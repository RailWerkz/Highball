using RailWerkz.Vulkan;

namespace Highball.World;

/// How high the land rises round each point, in eight directions, over a wide square round the camera. It takes moments to work out
/// and is kept while the camera stays near, so it's only a lookup for the sun: that's the shadows of mountains past the cascades' reach,
/// and the shade of a far ridge at sunset, at any time of day.
public sealed class TerrainHorizon : IDisposable
{
    public const int Directions = 8;

    private const int MapSize = 512;
    private const int HeightSize = 768;
    private const double Cell = 80.0;
    private const double MoveLimit = 6000.0;
    private const double EarthRadius = 6371000.0;

    private sealed record Built(byte[] East, byte[] West, double X, double Z, TerrainSource Source, int Epoch);

    private Task<Built>? _building;
    private TerrainSource? _source;
    private int _epoch = -1;
    private int _seenEpoch = -1;
    private double _editedAt = double.MinValue;
    private const double EditRest = 0.75;
    private double _x;
    private double _z;

    public Texture2D? Sectors0 { get; private set; }
    public Texture2D? Sectors1 { get; private set; }

    /// Where the map's middle is, in world metres.
    public double CentreX { get; private set; }
    public double CentreZ { get; private set; }

    /// How wide the map is, in metres.
    public static float Width => (float)(MapSize * Cell);

    /// Goes up with each new map.
    public int Version { get; private set; }

    /// Before the frame's shadows. Starts a new map when the camera's strayed or the ground's changed, and takes in a finished one.
    public void Update(TerrainSource source, int epoch, double cameraX, double cameraZ)
    {
        // Ground being edited waits until the brush has rested: a map this coarse has nothing to gain from every frame of a stroke,
        // and each one taken in holds up the frame.
        var now = RailWerkz.Core.Time.Time.Now;
        if (epoch != _seenEpoch)
            (_seenEpoch, _editedAt) = (epoch, now);
        if (ReferenceEquals(source, _source) && epoch != _epoch && now - _editedAt < EditRest)
            epoch = _epoch;

        if (_building is { IsCompleted: true } done)
        {
            _building = null;
            if (done.IsCompletedSuccessfully)
                TakeIn(done.Result);
        }

        var dx = cameraX - _x;
        var dz = cameraZ - _z;
        var stale = !ReferenceEquals(source, _source) || epoch != _epoch || dx * dx + dz * dz > MoveLimit * MoveLimit;
        if (!stale || _building is not null)
            return;

        (_source, _epoch, _x, _z) = (source, epoch, cameraX, cameraZ);
        var (x, z) = (Math.Round(cameraX / Cell) * Cell, Math.Round(cameraZ / Cell) * Cell);
        _building = Task.Run(() => Build(source, epoch, x, z));
    }

    private void TakeIn(Built built)
    {
        // A map of ground that's since been swapped for another route is no use.
        if (!ReferenceEquals(built.Source, _source))
            return;

        var (old0, old1) = (Sectors0, Sectors1);
        Sectors0 = Texture2D.FromPixels(MapSize, MapSize, built.East, repeat: false, mipmaps: false, name: "horizon.0");
        Sectors1 = Texture2D.FromPixels(MapSize, MapSize, built.West, repeat: false, mipmaps: false, name: "horizon.1");
        (CentreX, CentreZ) = (built.X, built.Z);
        Version++;
        old0?.Dispose();
        old1?.Dispose();
    }

    private static Built Build(TerrainSource source, int epoch, double x, double z)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var heights = new float[HeightSize * HeightSize];
        var first = HeightSize * 0.5 - 0.5;
        Parallel.For(0, HeightSize, row =>
        {
            for (var column = 0; column < HeightSize; column++)
                heights[row * HeightSize + column] = source.HeightAt(x + (column - first) * Cell, z + (row - first) * Cell);
        });

        var steps = new (float Dx, float Dz)[Directions];
        for (var d = 0; d < Directions; d++)
        {
            var (sin, cos) = MathF.SinCos(d * MathF.PI * 2f / Directions);
            steps[d] = (cos, sin);
        }

        // Steps that grow, close together where the land near a point matters most.
        var distances = new List<float>();
        for (var along = 1.0f; along < HeightSize; along = MathF.Max(along + 1f, along * 1.12f))
            distances.Add(along);
        var drops = distances.Select(d => (float)(d * Cell * (d * Cell) / (2.0 * EarthRadius))).ToArray();
        var reach = distances.Select(d => d * (float)Cell).ToArray();

        var east = new byte[MapSize * MapSize * 4];
        var west = new byte[MapSize * MapSize * 4];
        var margin = (HeightSize - MapSize) / 2;
        Parallel.For(0, MapSize, row =>
        {
            for (var column = 0; column < MapSize; column++)
            {
                var (hx, hz) = (column + margin, row + margin);
                // A little over the ground, so a point doesn't shade itself on its own slope at this coarse size.
                var height = heights[hz * HeightSize + hx] + 3f;
                for (var d = 0; d < Directions; d++)
                {
                    var (dx, dz) = steps[d];
                    var best = 0f;
                    for (var s = 0; s < reach.Length; s++)
                    {
                        var (sx, sz) = ((int)(hx + dx * distances[s] + 0.5f), (int)(hz + dz * distances[s] + 0.5f));
                        if (sx < 0 || sz < 0 || sx >= HeightSize || sz >= HeightSize)
                            break;

                        var rise = (heights[sz * HeightSize + sx] - height - drops[s]) / reach[s];
                        if (rise > best)
                            best = rise;
                    }

                    // The sine of the angle, which the sun's height is directly.
                    var sine = best / MathF.Sqrt(1f + best * best);
                    (d < 4 ? east : west)[(row * MapSize + column) * 4 + d % 4] = (byte)MathF.Min(MathF.Ceiling(sine * 255f), 255f);
                }
            }
        });

        RailWerkz.Core.Logging.Log.Info($"Horizon map: {clock.ElapsedMilliseconds} ms");
        return new Built(east, west, x, z, source, epoch);
    }

    public void Dispose()
    {
        Sectors0?.Dispose();
        Sectors1?.Dispose();
    }
}
