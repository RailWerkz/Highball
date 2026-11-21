using Highball.Routes;

namespace Highball.Builder;

/// Route, Auto Classify Track: each piece of laid track given the class of the mapped (OpenStreetMap) track it runs along,
/// as RouteNetwork.Rail read it from the map's tags. Only Main, Branch, Industrial and Yard, either way; laid track that
/// runs along no mapped track for most of its length is left as it is.
public static class TrackClassify
{
    /// The classes it reads and sets.
    private static readonly RailClass[] Classes = [RailClass.Main, RailClass.Branch, RailClass.Industrial, RailClass.Yard];

    /// How near the mapped track a stretch of laid track has to run to be on it, metres: under the spacing of parallel
    /// tracks, so a neighbour's class isn't taken.
    private const double Within = 3.0;

    /// How far laid track may run off the mapped track's direction and still be along it, degrees.
    private const double Along = 15.0;

    /// How much of a piece's length has to be along mapped track to take its class.
    private const double Covered = 0.5;

    /// Laid track's sampled this often along it, metres.
    private const double Step = 4.0;

    private const double Cell = 25.0;

    /// The laid ways whose class differs from the mapped track they run along, and that class; and how many laid ways were
    /// looked at, and how many ran along mapped track at all.
    public static (List<(long Way, RailClass Class)> Changes, int Looked, int Matched) Find(RouteNetwork network, RouteProjection projection)
    {
        (double X, double Z) At(NetworkWay way, int i)
        {
            var p = projection.Forward(new GeoPoint(way.Lat[i], way.Lon[i]));
            return (p.East, -p.North);
        }

        // The mapped track's segments, by grid cell.
        var segments = new List<(double AX, double AZ, double BX, double BZ, RailClass Class)>();
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var way in network.Ways.Values)
        {
            if (!way.Rail || !RouteNetwork.IsGuide(way) || !Classes.Contains((RailClass)way.Class) || way.Nodes.Length < 2)
                continue;
            var last = At(way, 0);
            for (var i = 1; i < way.Nodes.Length; i++)
            {
                var next = At(way, i);
                var index = segments.Count;
                segments.Add((last.X, last.Z, next.X, next.Z, (RailClass)way.Class));
                var (x0, x1) = ((int)Math.Floor((Math.Min(last.X, next.X) - Within) / Cell), (int)Math.Floor((Math.Max(last.X, next.X) + Within) / Cell));
                var (z0, z1) = ((int)Math.Floor((Math.Min(last.Z, next.Z) - Within) / Cell), (int)Math.Floor((Math.Max(last.Z, next.Z) + Within) / Cell));
                for (var cz = z0; cz <= z1; cz++)
                    for (var cx = x0; cx <= x1; cx++)
                    {
                        if (!grid.TryGetValue((cx, cz), out var list))
                            grid[(cx, cz)] = list = [];
                        list.Add(index);
                    }

                last = next;
            }
        }

        var cosAlong = Math.Cos(Along * Math.PI / 180.0);

        // The class of the nearest mapped track running alongside a place heading (ux, uz), if any is near enough.
        RailClass? Under(double x, double z, double ux, double uz)
        {
            if (!grid.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var list))
                return null;
            (RailClass Class, double Distance)? best = null;
            foreach (var index in list)
            {
                var (ax, az, bx, bz, cls) = segments[index];
                var (dx, dz) = (bx - ax, bz - az);
                var length = Math.Sqrt(dx * dx + dz * dz);
                if (length < 1e-6 || Math.Abs((dx * ux + dz * uz) / length) < cosAlong)
                    continue;
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / (length * length), 0.0, 1.0);
                var d = Math.Sqrt((ax + dx * t - x) * (ax + dx * t - x) + (az + dz * t - z) * (az + dz * t - z));
                if (d <= Within && (best is null || d < best.Value.Distance))
                    best = (cls, d);
            }

            return best?.Class;
        }

        var changes = new List<(long, RailClass)>();
        var (looked, matched) = (0, 0);
        foreach (var way in network.Ways.Values)
        {
            if (!way.Rail || RouteNetwork.IsGuide(way) || !Classes.Contains((RailClass)way.Class) || way.Nodes.Length < 2)
                continue;
            looked++;

            // Each class's share of the way's length, from samples along it.
            var votes = new Dictionary<RailClass, double>();
            var total = 0.0;
            var last = At(way, 0);
            for (var i = 1; i < way.Nodes.Length; i++)
            {
                var next = At(way, i);
                var (dx, dz) = (next.X - last.X, next.Z - last.Z);
                var length = Math.Sqrt(dx * dx + dz * dz);
                if (length > 1e-6)
                {
                    var steps = Math.Max(1, (int)Math.Ceiling(length / Step));
                    for (var k = 0; k < steps; k++)
                    {
                        var t = (k + 0.5) / steps;
                        var piece = length / steps;
                        total += piece;
                        if (Under(last.X + dx * t, last.Z + dz * t, dx / length, dz / length) is { } cls)
                            votes[cls] = votes.GetValueOrDefault(cls) + piece;
                    }
                }

                last = next;
            }

            if (total <= 0.0 || votes.Values.Sum() < Covered * total)
                continue;
            matched++;
            var winner = votes.MaxBy(v => v.Value).Key;
            if ((RailClass)way.Class != winner)
                changes.Add((way.Id, winner));
        }

        return (changes, looked, matched);
    }
}
