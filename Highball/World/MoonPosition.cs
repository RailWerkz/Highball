namespace Highball.World;

/// Where the moon is from a place and a moment, as a direction (east, up, south), by the Astronomical Almanac's low-precision
/// formulas (good to about a third of a degree). Also how the stars have turned by then.
public readonly record struct MoonPosition(float East, float Up, float South)
{
    private const double Radians = Math.PI / 180.0;

    public static MoonPosition At(double latitude, double longitude, int year, int month, int day, double hour)
    {
        var julianDay = SunPosition.JulianDayAt(longitude, year, month, day, hour);
        var t = (julianDay - 2451545.0) / 36525.0;
        static double Sin(double degrees) => Math.Sin(degrees * Radians);
        static double Cos(double degrees) => Math.Cos(degrees * Radians);

        var along = 218.32 + 481267.881 * t
            + 6.29 * Sin(135.0 + 477198.87 * t) - 1.27 * Sin(259.3 - 413335.36 * t) + 0.66 * Sin(235.7 + 890534.22 * t)
            + 0.21 * Sin(269.9 + 954397.74 * t) - 0.19 * Sin(357.5 + 35999.05 * t) - 0.11 * Sin(186.5 + 966404.03 * t);
        var above = 5.13 * Sin(93.3 + 483202.02 * t) + 0.28 * Sin(228.2 + 960400.89 * t)
            - 0.28 * Sin(318.3 + 6003.15 * t) - 0.17 * Sin(217.6 - 407332.21 * t);
        var parallax = 0.9508 + 0.0518 * Cos(135.0 + 477198.87 * t) + 0.0095 * Cos(259.3 - 413335.36 * t)
            + 0.0078 * Cos(235.7 + 890534.22 * t) + 0.0028 * Cos(269.9 + 954397.74 * t);

        // From the earth's centre, in earth radii, against the stars: x towards the spring equinox, z towards the north pole.
        var distance = 1.0 / Sin(parallax);
        var x = distance * (Cos(above) * Cos(along));
        var y = distance * (0.9175 * Cos(above) * Sin(along) - 0.3978 * Sin(above));
        var z = distance * (0.3978 * Cos(above) * Sin(along) + 0.9175 * Sin(above));

        // From where we stand, a radius away from the centre: it's near enough to shift by up to a degree.
        var (east, up, north) = Local(latitude, longitude, julianDay);
        x -= up.X;
        y -= up.Y;
        z -= up.Z;

        var length = Math.Sqrt(x * x + y * y + z * z);
        return new MoonPosition(
            (float)((x * east.X + y * east.Y + z * east.Z) / length),
            (float)((x * up.X + y * up.Y + z * up.Z) / length),
            (float)(-(x * north.X + y * north.Y + z * north.Z) / length));
    }

    /// The columns of the turn from a direction in the world (x east, y up, z south) to one against the stars.
    public static float[] StarTurn(double latitude, double longitude, int year, int month, int day, double hour)
    {
        var (east, up, north) = Local(latitude, longitude, SunPosition.JulianDayAt(longitude, year, month, day, hour));
        return
        [
            (float)east.X, (float)east.Y, (float)east.Z,
            (float)up.X, (float)up.Y, (float)up.Z,
            (float)-north.X, (float)-north.Y, (float)-north.Z,
        ];
    }

    /// East, up and north at a place, against the stars at a moment.
    private static ((double X, double Y, double Z) East, (double X, double Y, double Z) Up, (double X, double Y, double Z) North) Local(double latitude, double longitude, double julianDay)
    {
        var sidereal = (280.46061837 + 360.98564736629 * (julianDay - 2451545.0) + longitude) * Radians;
        var (sinTurn, cosTurn) = Math.SinCos(sidereal);
        var (sinLat, cosLat) = Math.SinCos(latitude * Radians);
        return (
            (-sinTurn, cosTurn, 0.0),
            (cosLat * cosTurn, cosLat * sinTurn, sinLat),
            (-sinLat * cosTurn, -sinLat * sinTurn, cosLat));
    }
}
