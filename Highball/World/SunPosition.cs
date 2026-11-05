namespace Highball.World;

/// Where the sun is from a place and a moment, by the NOAA solar calculator's formulas (good to a fraction of a degree
/// for centuries either side of now). Clock times are standard time for the longitude's time zone, one hour per 15
/// degrees: no daylight saving, and no political borders.
public readonly record struct SunPosition(float Elevation, float Azimuth, double Sunrise, double SolarNoon, double Sunset)
{
    private const double Radians = Math.PI / 180.0;

    /// The sun's height in degrees over the horizon, its bearing in degrees (0 north, 90 east), and the day's sunrise, solar
    /// noon and sunset as hours on the same clock. A day with no sunrise or sunset (near the poles) has NaN for both.
    public static SunPosition At(double latitude, double longitude, int year, int month, int day, double hour)
    {
        var zone = TimeZone(longitude);
        var (declination, equation) = Sun(JulianDay(year, month, day, hour - zone));

        // The hour angle: how far the sun has turned past its highest, in degrees.
        var minutes = hour * 60.0 + equation + 4.0 * longitude - 60.0 * zone;
        var angle = minutes / 4.0 - 180.0;

        var (sinLat, cosLat) = Math.SinCos(latitude * Radians);
        var (sinDec, cosDec) = Math.SinCos(declination * Radians);
        var (sinAngle, cosAngle) = Math.SinCos(angle * Radians);

        var sinElevation = Math.Clamp(sinLat * sinDec + cosLat * cosDec * cosAngle, -1.0, 1.0);
        var elevation = Math.Asin(sinElevation) / Radians;
        // Measured from north, clockwise; the hour angle is positive after noon, when the sun's in the west.
        var azimuth = Math.Atan2(-cosDec * sinAngle, sinDec * cosLat - cosDec * sinLat * cosAngle) / Radians;

        var noon = (720.0 - 4.0 * longitude - equation + 60.0 * zone) / 60.0;
        // The sun's edge at the horizon, allowing for the air bending its light.
        var cosRise = (Math.Sin(-0.833 * Radians) - sinLat * sinDec) / (cosLat * cosDec);
        var half = Math.Abs(cosRise) <= 1.0 ? Math.Acos(cosRise) / Radians / 15.0 : double.NaN;

        return new SunPosition((float)elevation, (float)((azimuth + 360.0) % 360.0), noon - half, noon, noon + half);
    }

    /// The hours from UTC of a longitude's time zone.
    public static int TimeZone(double longitude) => (int)Math.Round(longitude / 15.0);

    private static double JulianDay(int year, int month, int day, double utcHour)
    {
        if (month <= 2)
        {
            year--;
            month += 12;
        }

        var century = year / 100;
        var leap = 2 - century + century / 4;
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + leap - 1524.5 + utcHour / 24.0;
    }

    /// The sun's declination in degrees, and the equation of time in minutes.
    private static (double Declination, double Equation) Sun(double julianDay)
    {
        var t = (julianDay - 2451545.0) / 36525.0;
        var meanLongitude = (280.46646 + t * (36000.76983 + t * 0.0003032)) % 360.0;
        var anomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var m = anomaly * Radians;
        var centre = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(2.0 * m) * (0.019993 - 0.000101 * t) + Math.Sin(3.0 * m) * 0.000289;
        var apparent = meanLongitude + centre - 0.00569 - 0.00478 * Math.Sin((125.04 - 1934.136 * t) * Radians);
        var obliquity = 23.0 + (26.0 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0 + 0.00256 * Math.Cos((125.04 - 1934.136 * t) * Radians);

        var declination = Math.Asin(Math.Sin(obliquity * Radians) * Math.Sin(apparent * Radians)) / Radians;

        var y = Math.Tan(obliquity * Radians * 0.5);
        y *= y;
        var l = meanLongitude * Radians;
        var equation = y * Math.Sin(2.0 * l) - 2.0 * eccentricity * Math.Sin(m) + 4.0 * eccentricity * y * Math.Sin(m) * Math.Cos(2.0 * l)
            - 0.5 * y * y * Math.Sin(4.0 * l) - 1.25 * eccentricity * eccentricity * Math.Sin(2.0 * m);
        return (declination, equation * 4.0 / Radians);
    }
}
