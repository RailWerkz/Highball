namespace Highball.World;

/// Where one track simply carries on into another (two rail lines ending on the same node, nothing else there: the same
/// track split where its class, bed or tags change), each is fitted on its own and their ends needn't point quite the
/// same way, which leaves a kink in the rails right at the join. Each end is eased round to the direction halfway between
/// the two over its last few metres, so the track runs on through the join as one smooth line. Run every time the track
/// is opened (solved or loaded), after any hand shaping.
public sealed partial class NetworkLines
{
    /// How far back from a join an end is eased, at most, and at most this share of its line.
    private const double JoinEase = 12.0;
    private const double JoinEaseShare = 0.4;

    /// Ends meeting at more than this angle aren't one track carrying on, and are left alone.
    private const double JoinMostDegrees = 20.0;

    private void EaseJoins()
    {
        var ends = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        var uses = new Dictionary<long, int>();
        foreach (var line in _lines.Where(l => l.Rail && !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                uses[node] = uses.GetValueOrDefault(node) + 1;
                if (point != 0 && point != line.Count - 1)
                    continue;
                if (!ends.TryGetValue(node, out var list))
                    ends[node] = list = [];
                list.Add((line, point));
            }

        var eased = 0;
        foreach (var (node, list) in ends)
        {
            if (list.Count != 2 || uses[node] != 2 || list[0].Line == list[1].Line || list.Any(e => e.Line.Count < 3))
                continue;

            var (a, b) = (list[0], list[1]);
            var u = Inward(a.Line, a.Point);
            var v = Inward(b.Line, b.Point);
            // Through the join, from a into b; each end should leave the join straight back along it.
            var (tx, tz) = (v.X - u.X, v.Z - u.Z);
            var length = Math.Sqrt(tx * tx + tz * tz);
            if (length < 1e-9)
                continue;
            (tx, tz) = (tx / length, tz / length);
            var kink = Math.Acos(Math.Clamp(-(u.X * v.X + u.Z * v.Z), -1.0, 1.0)) * 180.0 / Math.PI;
            if (kink < 0.01 || kink > JoinMostDegrees)
                continue;

            Ease(a.Line, a.Point, u, (-tx, -tz));
            Ease(b.Line, b.Point, v, (tx, tz));
            eased++;
        }

        if (eased > 0)
            RailWerkz.Core.Logging.Log.Info($"Track: {eased} joins eased");
    }

    /// The way into a line from its end at point: along its first segment, which is what the rails at the join follow.
    private static (double X, double Z) Inward(NetworkLine line, int point)
    {
        var q = point == 0 ? 1 : point - 1;
        var (dx, dz) = (line.X[q] - line.X[point], line.Z[q] - line.Z[point]);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
        return (dx / length, dz / length);
    }

    /// Bends a line's end so its first segment leaves the end point along want instead of along now: each point back to
    /// JoinEase metres is moved across by k d (1 - d/reach)^2, d its distance from the end, which turns the end without
    /// moving it and fades out smoothly (in position and direction) by the reach; k is chosen so the first segment comes
    /// out exactly along want.
    private static void Ease(NetworkLine line, int point, (double X, double Z) now, (double X, double Z) want)
    {
        var turn = Math.Atan2(now.X * want.Z - now.Z * want.X, now.X * want.X + now.Z * want.Z);
        var reach = Math.Min(JoinEase, line.S[^1] * JoinEaseShare);
        if (reach < 1.0)
            return;
        var (ax, az) = (-now.Z, now.X); // now turned a quarter the way a positive turn goes
        var step = point == 0 ? 1 : -1;
        var first = Math.Abs(line.S[point + step] - line.S[point]);
        if (first >= reach * 0.5)
            return;
        var tan = Math.Tan(turn) / ((1.0 - first / reach) * (1.0 - first / reach));
        for (var q = point + step; q >= 0 && q < line.Count; q += step)
        {
            var d = Math.Abs(line.S[q] - line.S[point]);
            if (d >= reach)
                break;
            var shift = tan * d * (1.0 - d / reach) * (1.0 - d / reach);
            line.X[q] += ax * shift;
            line.Z[q] += az * shift;
        }
    }
}
