using Highball.Routes;

namespace Highball.World;

/// A grade crossing's pad: the stretch along its road that it takes over, from one end to the other, with the tracks' panels
/// level between Low and High and an approach each side out to From and To. Its roads end at those two ends (the editor cuts
/// them back there) and are brought up to its level there, and the ground under it is shaped to it, so the roads, the deck and
/// the ground all meet. X, Z: the middle of its road over the tracks; (Dx, Dz) the way the road runs; Half: half the road's width.
public sealed record CrossingPad(long Id, double X, double Z, double Dx, double Dz, float Half, float Low, float High, float Level)
{
    /// How far each approach runs past the panels.
    public const float Approach = 6f;

    public float From => Low - Approach;
    public float To => High + Approach;

    /// A place on the pad: t along its road from its middle, w across it (to the right as it runs).
    public (double X, double Z) At(float t, float w = 0f) => (X + Dx * t - Dz * w, Z + Dz * t + Dx * w);

    /// Where a place lies on the pad: along, and across.
    public (float T, float W) Local(double x, double z) => ((float)((x - X) * Dx + (z - Z) * Dz), (float)(-(x - X) * Dz + (z - Z) * Dx));

    /// Its two ends, on its middle line.
    public (double X, double Z) Start => At(From);
    public (double X, double Z) End => At(To);
}

public sealed partial class NetworkLines
{
    /// How far past a track's centre its crossing panels reach, past a road's edge the pad's level ground runs, how far from a
    /// pad's end a road's end counts as joined to it, and how steeply the ground falls away past a pad's end with no road on.
    private const float PadReach = 1.75f;
    private const float PadShoulder = 1.5f;
    private const float PadJoin = 3f;
    private const float PadEndSlope = 0.12f;
    private const float PadEndReach = 8f;
    private const float PadSearch = 40f;
    private const float PadTrackGap = 12f;

    private readonly List<(long Id, double X, double Z, double Heading, float Width)> _crossings = [];
    private readonly List<CrossingPad> _pads = [];

    /// Every grade crossing's pad, worked out with the profiles.
    public IReadOnlyList<CrossingPad> Pads => _pads;

    public CrossingPad? PadOf(long id) => _pads.FirstOrDefault(p => p.Id == id);

    /// The grade crossings placed on the route, for FinishProfiles to make pads of.
    /// The crossings these lines were laid with, to tell an edit to them.
    private List<PlacedObject> _crossingObjects = [];

    /// Whether these lines were laid with these same crossings.
    internal bool SameCrossings(IReadOnlyList<PlacedObject> crossings) => _crossingObjects.SequenceEqual(crossings);

    public void SetCrossings(IEnumerable<PlacedObject> objects, RouteProjection projection)
    {
        _crossingObjects = [.. objects.Where(o => o.Kind == PlacedKind.GradeCrossing)];
        _crossings.Clear();
        foreach (var o in objects.Where(o => o.Kind == PlacedKind.GradeCrossing))
        {
            var p = projection.Forward(new GeoPoint(o.Lat, o.Lon));
            _crossings.Add((o.Id, p.East, -p.North, o.Heading, o.Width));
        }
    }

    private void MakePads()
    {
        _pads.Clear();
        foreach (var (id, x, z, heading, width) in _crossings)
            if (PadFor(id, x, z, heading, width) is { } pad)
                _pads.Add(pad);
    }

    /// The pad a crossing placed here would have: along the road through it if there is one (as wide as it, on its middle), or
    /// along heading at that width; across every track it meets within PadTrackGap of the next; level with the highest rail top.
    public CrossingPad? PadFor(long id, double x, double z, double heading, float width)
    {
        var angle = heading * Math.PI / 180.0;
        var (dx, dz) = (Math.Cos(angle), Math.Sin(angle));
        if (RoadThrough(x, z, dx, dz) is { } road)
        {
            var sign = road.Dx * dx + road.Dz * dz < 0.0 ? -1.0 : 1.0;
            (dx, dz) = (road.Dx * sign, road.Dz * sign);
            var aside = (road.X - x) * -dz + (road.Z - z) * dx;
            (x, z) = (x - dz * aside, z + dx * aside);
            width = road.Width;
        }

        var half = width * 0.5f;
        var (low, high, level) = (float.MaxValue, float.MinValue, float.MinValue);
        var hits = new List<(float T, float Reach, float Y)>();
        foreach (var line in _lines)
        {
            if (!line.Rail || line.Disused || line.Mark || x < line.MinX - PadSearch || x > line.MaxX + PadSearch || z < line.MinZ - PadSearch || z > line.MaxZ + PadSearch)
                continue;
            var outer = (line.Tracks - 1) * 0.5f * NetworkLine.TrackSpacing + PadReach;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var (sx, sz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
                var denominator = dx * sz - dz * sx;
                var length = Math.Sqrt(sx * sx + sz * sz);
                if (length < 1e-6 || Math.Abs(denominator) / length < 0.25)
                    continue;
                var (qx, qz) = (line.X[i] - x, line.Z[i] - z);
                var t = (qx * sz - qz * sx) / denominator;
                var u = (qx * dz - qz * dx) / denominator;
                if (u < 0.0 || u >= 1.0 || Math.Abs(t) > PadSearch)
                    continue;
                // How far along the road the track's panels reach either side of its middle: across the track, and the road's
                // skew carrying its edges further along.
                var sine = (float)(Math.Abs(denominator) / length);
                var cosine = MathF.Sqrt(MathF.Max(0f, 1f - sine * sine));
                hits.Add(((float)t, (outer + half * cosine) / sine, line.Y[i] + (line.Y[i + 1] - line.Y[i]) * (float)u + RailTopAbove));
            }
        }

        if (hits.Count == 0)
            return null;
        hits.Sort((a, b) => a.T.CompareTo(b.T));
        var near = hits.IndexOf(hits.MinBy(h => MathF.Abs(h.T)));
        var (first, last) = (near, near);
        while (first > 0 && hits[first].T - hits[first - 1].T <= PadTrackGap)
            first--;
        while (last + 1 < hits.Count && hits[last + 1].T - hits[last].T <= PadTrackGap)
            last++;
        for (var k = first; k <= last; k++)
            (low, high, level) = (MathF.Min(low, hits[k].T - hits[k].Reach), MathF.Max(high, hits[k].T + hits[k].Reach), MathF.Max(level, hits[k].Y));

        return new CrossingPad(id, x, z, dx, dz, half, low, high, level);
    }

    /// The road running along a crossing at (x, z), if there is one: the nearest place on it, the way it runs there, its width.
    private (double X, double Z, double Dx, double Dz, float Width)? RoadThrough(double x, double z, double dx, double dz)
    {
        (double, double, double, double, float)? found = null;
        var best = 12.0 * 12.0;
        foreach (var road in _lines)
        {
            if (road.Rail || road.Mark || x < road.MinX - 12.0 || x > road.MaxX + 12.0 || z < road.MinZ - 12.0 || z > road.MaxZ + 12.0)
                continue;
            for (var i = 0; i + 1 < road.Count; i++)
            {
                var (sx, sz) = (road.X[i + 1] - road.X[i], road.Z[i + 1] - road.Z[i]);
                var length = Math.Max(Math.Sqrt(sx * sx + sz * sz), 1e-6);
                var (ux, uz) = (sx / length, sz / length);
                if (Math.Abs(ux * dx + uz * dz) < 0.9)
                    continue;
                var t = Math.Clamp((x - road.X[i]) * ux + (z - road.Z[i]) * uz, 0.0, length);
                var (px, pz) = (road.X[i] + ux * t, road.Z[i] + uz * t);
                var away = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (away < best)
                    (best, found) = (away, (px, pz, ux, uz, road.Width));
            }
        }

        return found;
    }

    /// Roads ending at a pad's end come up to its level there, easing back to their own grade further along.
    private void LevelRoadsAtPads()
    {
        if (_pads.Count == 0)
            return;
        Parallel.ForEach(_lines.Where(l => !l.Rail && !l.Mark && l.Count >= 2 && !l.Frozen), road =>
        {
            var length = road.S[road.Count - 1];
            foreach (var (point, inward) in new[] { (0, 1), (road.Count - 1, -1) })
            {
                var (ex, ez) = (road.X[point], road.Z[point]);
                foreach (var pad in _pads)
                {
                    var (sx, sz) = pad.Start;
                    var (fx, fz) = pad.End;
                    var near = Math.Min((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz), (ex - fx) * (ex - fx) + (ez - fz) * (ez - fz));
                    if (near > PadJoin * PadJoin)
                        continue;

                    var change = pad.Level - RoadSurfaceAbove - road.Y[point];
                    var reach = MathF.Min(MathF.Max(25f, MathF.Abs(change) / RoadToRailGrade), length * 0.8f);
                    var from = road.S[point];
                    for (var p = 0; p < road.Count; p++)
                    {
                        var away = Math.Clamp(1f - MathF.Abs(road.S[p] - from) / MathF.Max(reach, 1e-3f), 0f, 1f);
                        road.Y[p] += change * away * away * (3f - 2f * away);
                    }

                    break;
                }
            }
        });
    }

    /// Shapes the ground under and round each pad in the tile: level a little under the deck across the road and its shoulders,
    /// sloping away at the fill slope beside it and gently past each end; but never over a track's ballast (the panels cover
    /// that), and past the ends only filling, where a road joined there takes over.
    private void ConformPads(float[] heights, int samples, int gx0, int gz0, (int Line, float D, float Y, float Ex, float Ez)[] near)
    {
        foreach (var pad in _pads)
        {
            var outside = pad.Half + PadShoulder + MathF.Max(PadEndReach, 6f);
            var corners = new[] { pad.At(pad.From - PadEndReach, -outside), pad.At(pad.From - PadEndReach, outside), pad.At(pad.To + PadEndReach, -outside), pad.At(pad.To + PadEndReach, outside) };
            var x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X) / _spacing) - gx0);
            var x1 = Math.Min(samples - 1, (int)Math.Ceiling(corners.Max(c => c.X) / _spacing) - gx0);
            var z0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Z) / _spacing) - gz0);
            var z1 = Math.Min(samples - 1, (int)Math.Ceiling(corners.Max(c => c.Z) / _spacing) - gz0);
            if (x0 > x1 || z0 > z1)
                continue;

            var top = pad.Level - RoadSurfaceAbove - RoadClearance;
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var (t, w) = pad.Local((x + gx0) * (double)_spacing, (z + gz0) * (double)_spacing);
                    var along = MathF.Max(0f, MathF.Max(pad.From - t, t - pad.To));
                    var side = MathF.Max(0f, MathF.Abs(w) - pad.Half - PadShoulder);
                    if (along > PadEndReach || side > 6f)
                        continue;
                    var target = top - side * FillSlope - along * PadEndSlope;

                    // Over a track's ballast, no higher than its surface.
                    var sample = z * samples + x;
                    var n = near[sample];
                    if (n.Line >= 0)
                    {
                        var rail = _lines[n.Line];
                        if (n.D < rail.BallastHalfTop + 0.25f)
                            target = MathF.Min(target, n.Y - 0.04f + rail.BedHeight);
                    }

                    if (along <= 0f && side <= 0f)
                        heights[sample] = target;
                    else if (target > heights[sample])
                        heights[sample] = target;
                }
        }
    }
}
