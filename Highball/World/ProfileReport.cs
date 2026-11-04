using System.Globalization;
using System.Text;
using Highball.Routes;

namespace Highball.World;

/// How the rail lines' height profiles came out, for judging the profile fitting (--profilestats): how much of the
/// track is how steep, how far the formation strays from the ground, the steepest places, and the junctions that
/// didn't become switches.
public static class ProfileReport
{
    private const float Window = 20f;

    public static string Of(NetworkLines lines, RouteProjection projection)
    {
        var defects = TrackQuality.Find(lines);
        var quality = new StringBuilder();
        quality.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Track quality: {defects.Count(d => d.Kind == DefectKind.Bend)} bends under {TrackQuality.MinRadius:0} m, {defects.Count(d => d.Kind == DefectKind.HeightStep)} height steps, {defects.Count(d => d.Kind == DefectKind.JunctionStep)} junction steps, {defects.Count(d => d.Kind == DefectKind.Wiggle)} wiggles"));
        foreach (var d in defects.OrderBy(d => d.Kind).ThenBy(d => d.Kind == DefectKind.Bend ? d.Value : -d.Value).Take(120))
        {
            var g = projection.Inverse(new RoutePoint(d.X, -d.Z));
            quality.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  defect {d.Kind} {d.Value:0.00} at {g.Lat:0.00000} {g.Lon:0.00000} line {d.Line}"));
        }

        var (stray, sx, sz) = TrackQuality.Stray(lines);
        stray.Sort();
        if (stray.Count > 0)
        {
            var at = projection.Inverse(new RoutePoint(sx, -sz));
            quality.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Off the map: typically {stray[stray.Count / 2]:0.00} m, worst 10% over {stray[stray.Count * 9 / 10]:0.00} m, worst 1% over {stray[stray.Count * 99 / 100]:0.00} m, most {stray[^1]:0.00} m at {at.Lat:0.00000} {at.Lon:0.00000}"));
        }

        return quality + Report(lines, projection);
    }

    private static string Report(NetworkLines lines, RouteProjection projection)
    {
        var total = 0.0;
        var over = new double[4];
        float[] limits = [0.005f, 0.01f, 0.02f, 0.03f];
        var steepest = new List<(float Grade, double X, double Z)>();
        double deviation = 0, deviationMax = 0;
        var maxBreak = 0f;
        var breaks = new List<(float Change, NetworkLine Line, int Point)>();

        foreach (var line in lines.Lines.Where(l => l.Rail && !l.Disused))
        {
            var j = 0;
            for (var i = 0; i < line.Count - 1; i++)
            {
                var ds = line.S[i + 1] - line.S[i];
                total += ds;
                var dev = MathF.Abs(line.Y[i] - line.Ground[i]);
                deviation += dev * ds;
                deviationMax = Math.Max(deviationMax, dev);

                while (j < line.Count - 1 && line.S[j] - line.S[i] < Window)
                    j++;
                if (line.S[j] - line.S[i] < Window * 0.9f)
                    continue;
                var grade = MathF.Abs(line.Y[j] - line.Y[i]) / (line.S[j] - line.S[i]);
                for (var k = 0; k < limits.Length; k++)
                    if (grade > limits[k])
                        over[k] += ds;
                steepest.Add((grade, line.X[i], line.Z[i]));
            }

            // The sharpest change of grade (per 100 m), what a train would feel as a dip or a hump.
            for (var i = 1; i < line.Count - 1; i++)
            {
                var a = line.S[i] - line.S[i - 1];
                var b = line.S[i + 1] - line.S[i];
                if (a < 1f || b < 1f)
                    continue;
                var change = MathF.Abs((line.Y[i + 1] - line.Y[i]) / b - (line.Y[i] - line.Y[i - 1]) / a) / ((a + b) * 0.5f) * 100f;
                maxBreak = MathF.Max(maxBreak, change);
                breaks.Add((change, line, i));
            }
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Profiles: {total / 1000:0.0} km of rail; steeper than 0.5% {over[0] / total:P1}, 1% {over[1] / total:P1}, 2% {over[2] / total:P1}, 3% {over[3] / total:P1}; ");
        text.Append(CultureInfo.InvariantCulture, $"off the ground {deviation / total:0.00} m on average, {deviationMax:0.0} m at most; sharpest change of grade {maxBreak:0.0}% per 100 m");
        foreach (var (grade, x, z) in steepest.OrderByDescending(s => s.Grade).Take(5))
        {
            var g = projection.Inverse(new RoutePoint(x, -z));
            text.Append(CultureInfo.InvariantCulture, $"\n  steep: {grade * 100:0.0}% at {g.Lat:0.000000} {g.Lon:0.000000}");
        }

        // Tracks side by side: the height difference to the nearest point on another line within 6 m.
        var cells = new Dictionary<(int, int), List<(NetworkLine Line, int Point)>>();
        var rails = lines.Lines.Where(l => l.Rail && !l.Disused).ToList();
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p += 2)
            {
                var key = ((int)Math.Floor(line.X[p] / 6.0), (int)Math.Floor(line.Z[p] / 6.0));
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = [];
                list.Add((line, p));
            }

        var differences = new List<float>();
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p += 5)
            {
                var (cx, cz) = ((int)Math.Floor(line.X[p] / 6.0), (int)Math.Floor(line.Z[p] / 6.0));
                var best = double.MaxValue;
                var difference = 0f;
                for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (cells.TryGetValue((cx + dx, cz + dz), out var list))
                            foreach (var (other, q) in list)
                            {
                                if (other == line)
                                    continue;
                                var d = (other.X[q] - line.X[p]) * (other.X[q] - line.X[p]) + (other.Z[q] - line.Z[p]) * (other.Z[q] - line.Z[p]);
                                if (d < best && d < 36.0)
                                    (best, difference) = (d, MathF.Abs(other.Y[q] - line.Y[p]));
                            }
                if (best < double.MaxValue)
                    differences.Add(difference);
            }

        if (differences.Count > 0)
        {
            differences.Sort();
            text.Append(CultureInfo.InvariantCulture, $"\nTracks side by side: {differences.Average():0.00} m apart in height on average, {differences[(int)(differences.Count * 0.95)]:0.00} m at the 95th percentile, {differences[^1]:0.00} m at most");
        }

        if (Environment.GetEnvironmentVariable("HIGHBALL_PROBE") is { } probe)
        {
            var parts = probe.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var at = projection.Forward(new GeoPoint(parts[0], parts[1]));
            foreach (var line in rails)
                for (var p = 0; p < line.Count; p++)
                    if (Math.Abs(line.X[p] - at.East) < 12 && Math.Abs(line.Z[p] + at.North) < 12)
                        text.Append(CultureInfo.InvariantCulture, $"\n  probe line {line.Index} class {line.Class} point {p}: x {line.X[p] - at.East:0.0} z {line.Z[p] + at.North:0.0} y {line.Y[p]:0.000} ground {line.Ground[p]:0.000} zones {line.TurnoutZones.Count}");
        }

        if (Environment.GetEnvironmentVariable("HIGHBALL_LINE") is { } wanted && rails.FirstOrDefault(l => l.Index == int.Parse(wanted)) is { } probed)
            for (var p = 0; p < probed.Count; p += Math.Max(1, probed.Count / 40))
                text.Append(CultureInfo.InvariantCulture, $"|LINE s {probed.S[p]:0} y {probed.Y[p]:0.00} ground {probed.Ground[p]:0.00} {probed.Structures[Math.Min(p, probed.Structures.Length - 1)]}");

        foreach (var (change, line, point) in breaks.OrderByDescending(b => b.Change).Take(3))
        {
            var g = projection.Inverse(new RoutePoint(line.X[point], -line.Z[point]));
            var structure = point < line.Structures.Length ? line.Structures[point] : Structure.Ground;
            text.Append(CultureInfo.InvariantCulture, $"\n  kink: {change:0.0}% per 100 m on line {line.Index} at {g.Lat:0.000000} {g.Lon:0.000000} ({structure}, point {point} of {line.Count})");
        }

        // Wobble: how often a rail line's curvature (over 10 m either side) swings from one side to the other while
        // curving tighter than 1000 m radius both ways, per km.
        var swings = 0;
        var curved = 0.0;
        foreach (var line in rails)
        {
            var last = 0;
            for (var i = 5; i < line.Count - 5; i += 2)
            {
                double Heading(int a, int b) => Math.Atan2(line.Z[b] - line.Z[a], line.X[b] - line.X[a]);
                var turn = Heading(i, i + 5) - Heading(i - 5, i);
                turn = Math.IEEERemainder(turn, 2 * Math.PI);
                var k = turn / Math.Max(1.0, line.S[i + 5] - line.S[i - 5]);
                var sign = Math.Abs(k) > 0.001 ? Math.Sign(k) : 0;
                if (sign != 0 && last != 0 && sign != last)
                    swings++;
                if (sign != 0)
                    last = sign;
            }

            curved += line.S[^1];
        }

        text.Append(CultureInfo.InvariantCulture, $"\nWobble: {swings / Math.Max(curved / 1000, 1e-6):0.0} swings of curvature per km");

        var skipped = new Dictionary<string, int>();
        Turnout.Find(lines.Lines, lines.Fitted, skipped);
        text.Append(CultureInfo.InvariantCulture, $"\nFitted to a standard turnout: {lines.Fitted.Count} ({string.Join(", ", lines.Fitted.Values.GroupBy(n => n).OrderBy(g => g.Key).Select(g => $"{g.Count()} No. {g.Key}"))}); modelled {lines.Turnouts.Count(t => t.Modelled)}");
        foreach (var t in lines.Turnouts.Where(t => t.Number is not null).Take(4))
        {
            var g = projection.Inverse(new RoutePoint(t.X, -t.Z));
            text.Append(CultureInfo.InvariantCulture, $"\n  No. {t.Number} at {g.Lat:0.000000} {g.Lon:0.000000}, frog {t.FrogNormal:0.0} m");
        }

        text.Append(CultureInfo.InvariantCulture, $"\nSwitches: {lines.Turnouts.Count}; junctions left as they are: {string.Join(", ", skipped.Select(s => $"{s.Value} {s.Key}"))}");

        // The junction scorecard: what each junction was read as, and what's left to fix by hand, by reason.
        var layouts = lines.Layouts;
        var fixable = layouts.Where(l => l.Issue is not null).ToList();
        text.Append(CultureInfo.InvariantCulture, $"\nJunctions: {layouts.Count} ({string.Join(", ", layouts.GroupBy(l => l.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"))}); laid out to a standard design {layouts.Count(l => l.Number is not null)}; to fix {fixable.Count}");
        foreach (var group in fixable.GroupBy(l => l.Issue!.Split(':')[0]).OrderByDescending(g => g.Count()))
            text.Append(CultureInfo.InvariantCulture, $"\n  to fix: {group.Count()} {group.Key}");
        return text.ToString();
    }
}
