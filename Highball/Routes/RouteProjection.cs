namespace Highball.Routes;

/// A point on the Earth in degrees (WGS84).
public readonly record struct GeoPoint(double Lat, double Lon);

/// Metres on a route's plane: east and north of its origin.
public readonly record struct RoutePoint(double East, double North)
{
    public static RoutePoint operator +(RoutePoint a, RoutePoint b) => new(a.East + b.East, a.North + b.North);
}

/// A Transverse Mercator projection (Krüger's series on WGS84, good to well under a millimetre within a few hundred
/// kilometres of the central meridian). Every tile of a route is laid out on this one plane, which is what lets areas
/// built at different times meet exactly. The 3D world uses x = east, z = -north.
///
/// Two kinds: a UTM zone (the world: 6 degrees wide, central meridian at x = 0, ZoneNorth metres north of the equator at
/// z = 0, so the same place always lands on the same metres and the same tiles), or the older one centred on a route's
/// own origin, kept so routes made before zones still open.
public sealed class RouteProjection
{
    /// Where z = 0 is in a zone: metres north of the equator (about 36 degrees north). Keeps the lower 48 within about
    /// 1,400 km of it, so positions stay small enough for the shaders' floats.
    public const double ZoneNorth = 4_000_000.0;

    /// The UTM zone, 1 to 60; 0 for a projection centred on a route's origin.
    public int Zone { get; }

    /// The UTM zone a longitude falls in (no Norway or Svalbard exceptions: North America has none).
    public static int ZoneOf(double longitude) => Math.Clamp((int)Math.Floor((longitude + 180.0) / 6.0) + 1, 1, 60);

    /// A zone's central meridian, in degrees.
    public static double CentralMeridian(int zone) => zone * 6.0 - 183.0;

    public RouteProjection(int zone)
    {
        if (zone is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "UTM zones run 1 to 60");
        Zone = zone;
        _lon0 = CentralMeridian(zone) * Deg;
        _north0 = ZoneNorth;
        Origin = new GeoPoint(0.0, CentralMeridian(zone));
    }

    /// How far true north is turned from the plane's north at a place: degrees clockwise. A bearing from true north
    /// (the sun's, a compass's) is this much more on the plane.
    public double GridNorth(GeoPoint p)
    {
        var a = Forward(p);
        var b = Forward(p with { Lat = p.Lat + 0.001 });
        return Math.Atan2(b.East - a.East, b.North - a.North) / Deg;
    }

    private const double A = 6378137.0;
    private const double F = 1.0 / 298.257223563;
    private static readonly double N = F / (2.0 - F);
    private static readonly double Rect = A / (1.0 + N) * (1.0 + N * N / 4.0 + N * N * N * N / 64.0);
    private static readonly double[] Alpha = [N / 2.0 - 2.0 / 3.0 * N * N + 5.0 / 16.0 * N * N * N, 13.0 / 48.0 * N * N - 3.0 / 5.0 * N * N * N, 61.0 / 240.0 * N * N * N];
    private static readonly double[] Beta = [N / 2.0 - 2.0 / 3.0 * N * N + 37.0 / 96.0 * N * N * N, 1.0 / 48.0 * N * N + 1.0 / 15.0 * N * N * N, 17.0 / 480.0 * N * N * N];
    private static readonly double[] Delta = [2.0 * N - 2.0 / 3.0 * N * N - 2.0 * N * N * N, 7.0 / 3.0 * N * N - 8.0 / 5.0 * N * N * N, 56.0 / 15.0 * N * N * N];
    private static readonly double E2N = 2.0 * Math.Sqrt(N) / (1.0 + N);
    private const double Deg = Math.PI / 180.0;

    private readonly double _lon0;
    private readonly double _north0;

    public GeoPoint Origin { get; }

    public RouteProjection(GeoPoint origin)
    {
        Origin = origin;
        _lon0 = origin.Lon * Deg;
        _north0 = Raw(origin).North;
    }

    private RoutePoint Raw(GeoPoint p)
    {
        var phi = p.Lat * Deg;
        var lambda = p.Lon * Deg - _lon0;
        var sin = Math.Sin(phi);
        var t = Math.Sinh(Math.Atanh(sin) - E2N * Math.Atanh(E2N * sin));
        var xi = Math.Atan2(t, Math.Cos(lambda));
        var eta = Math.Atanh(Math.Sin(lambda) / Math.Sqrt(1.0 + t * t));

        var east = eta;
        var north = xi;
        for (var j = 1; j <= 3; j++)
        {
            east += Alpha[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            north += Alpha[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
        }

        return new RoutePoint(Rect * east, Rect * north);
    }

    public RoutePoint Forward(GeoPoint p)
    {
        var raw = Raw(p);
        return new RoutePoint(raw.East, raw.North - _north0);
    }

    public GeoPoint Inverse(RoutePoint p)
    {
        var xi = (p.North + _north0) / Rect;
        var eta = p.East / Rect;
        var xi1 = xi;
        var eta1 = eta;
        for (var j = 1; j <= 3; j++)
        {
            xi1 -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            eta1 -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }

        var chi = Math.Asin(Math.Sin(xi1) / Math.Cosh(eta1));
        var phi = chi;
        for (var j = 1; j <= 3; j++)
            phi += Delta[j - 1] * Math.Sin(2 * j * chi);

        var lambda = _lon0 + Math.Atan2(Math.Sinh(eta1), Math.Cos(xi1));
        return new GeoPoint(phi / Deg, lambda / Deg);
    }
}

/// A tile of a route: the square [I, I+1) x [J, J+1) tile sizes east and north of the origin.
public readonly record struct TileId(int I, int J)
{
    public override string ToString() => $"{I}_{J}";

    public static bool TryParse(string text, out TileId id)
    {
        id = default;
        var parts = text.Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var i) || !int.TryParse(parts[1], out var j))
            return false;
        id = new TileId(i, j);
        return true;
    }
}

/// How a route's plane is cut up: square tiles of TileSize metres, each with heights every Spacing metres. Samples
/// fall on the same route-wide lattice in every tile, so a tile's edge row is its neighbour's edge row.
public sealed class RouteGrid
{
    public double TileSize { get; set; } = 2000.0;
    public double Spacing { get; set; } = 2.0;

    /// Heights per side, edges included.
    public int Samples => (int)Math.Round(TileSize / Spacing) + 1;

    /// Land-cover cells per side (WorldCover is 10 m).
    public int CoverCells => (int)Math.Round(TileSize / 10.0);

    public TileId TileAt(RoutePoint p) => new((int)Math.Floor(p.East / TileSize), (int)Math.Floor(p.North / TileSize));

    public RoutePoint Corner(TileId t) => new(t.I * TileSize, t.J * TileSize);

    /// The sample at column x (east) and row z (south from the tile's north edge, matching image order).
    public RoutePoint Sample(TileId t, int x, int z) => new(t.I * TileSize + x * Spacing, (t.J + 1) * TileSize - z * Spacing);
}
