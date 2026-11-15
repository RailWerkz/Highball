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
    /// How far past a track's centre its crossing panels reach, how far from a pad's end a road's end counts as joined to it, and
    /// how steeply the ground falls away past a pad's end with no road on.
    private const float PadReach = 1.75f;
    private const float PadJoin = 3f;
    private const float PadEndSlope = 0.12f;
    private const float PadEndReach = 8f;
    /// How far past a pad's ends the ground's filled under the roads running on from it (as far as they're raised to it).
    private const float PadRoadReach = 40f;
    private const float PadSearch = 40f;
    private const float PadTrackGap = 12f;

    /// The road's fill beside the deck, as the crossing's mesh builds it: its top this far over the ground under the deck at the
    /// deck's edge, falling this steeply outward from a little inside the edge, out to PadSideReach from it.
    private const float PadEdgeAbove = 0.04f;
    private const float PadEdgeIn = 0.05f;
    private const float PadSideSlope = 0.39f / 0.65f;
    private const float PadSideReach = 6f;
    /// How far across a pad's road its shaping can reach.
    private const float PadOutside = 9.5f;

    /// A road shorter than this has come to nothing (a scrap whose ends were both snapped to the same pad end): it isn't drawn,
    /// and meets no other road.
    public const float MinRoadLength = 0.05f;

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
        if ((RoadThrough(x, z, dx, dz) ?? RoadAcross(x, z)) is { } road)
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
                if (length < 1e-6 || Math.Abs(denominator) / length < MinPadSine)
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

    /// The shallowest a road can cross a track for a pad to be laid (the sine of its angle to it: about 7 degrees).
    private const double MinPadSine = 0.12;

    /// The road through a crossing at (x, z) from the two roads cut back to its pad: free ends facing each other across it, the
    /// crossing between them. (A road crossing a track at a shallow angle is cut back a long way either side, beyond where
    /// RoadThrough looks; and a crossing laid square to the track, not along its road, wouldn't find the road that way at all.)
    private (double X, double Z, double Dx, double Dz, float Width)? RoadAcross(double x, double z)
    {
        const double reach = 80.0;
        var ends = new List<(double X, double Z, double Ox, double Oz, float Width)>();
        foreach (var road in _lines)
        {
            if (road.Rail || road.Mark || road.Count < 2 || x < road.MinX - reach || x > road.MaxX + reach || z < road.MinZ - reach || z > road.MaxZ + reach)
                continue;
            foreach (var (p, q) in new[] { (0, 1), (road.Count - 1, road.Count - 2) })
            {
                var (ox, oz) = (road.X[p] - road.X[q], road.Z[p] - road.Z[q]);
                var length = Math.Sqrt(ox * ox + oz * oz);
                if (length < 1e-6 || (road.X[p] - x) * (road.X[p] - x) + (road.Z[p] - z) * (road.Z[p] - z) > reach * reach)
                    continue;
                ends.Add((road.X[p], road.Z[p], ox / length, oz / length, road.Width));
            }
        }

        (double, double, double, double, float)? found = null;
        var best = double.MaxValue;
        for (var a = 0; a < ends.Count; a++)
            for (var b = 0; b < ends.Count; b++)
            {
                var (ea, eb) = (ends[a], ends[b]);
                var (vx, vz) = (eb.X - ea.X, eb.Z - ea.Z);
                var length = Math.Sqrt(vx * vx + vz * vz);
                if (a == b || length < 2.0 || length > reach * 2.0)
                    continue;
                (vx, vz) = (vx / length, vz / length);
                // Each end running on toward the other.
                if (ea.Ox * vx + ea.Oz * vz < 0.97 || -(eb.Ox * vx + eb.Oz * vz) < 0.97)
                    continue;
                var t = (x - ea.X) * vx + (z - ea.Z) * vz;
                var aside = Math.Abs((x - ea.X) * -vz + (z - ea.Z) * vx);
                if (t < 0.0 || t > length || aside > MathF.Max(ea.Width, eb.Width) * 0.5 + 2.0 || aside >= best)
                    continue;
                best = aside;
                found = (ea.X + vx * t, ea.Z + vz * t, vx, vz, MathF.Max(ea.Width, eb.Width));
            }

        return found;
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

    /// Roads ending near a pad's end are made to end on it, so the road runs straight onto the deck's ramp with no gap or overlap
    /// between them. A road is cut at a pad's ends when it's fitted, but a pad's ends move when it changes (its width, its tracks),
    /// leaving the road's end short of the ramp (bare ground between them) or running onto it. A road's free end (one no other road
    /// meets: a junction's ends are left where the junction was worked out) on the road (within its half width and a metre) and
    /// within PadJoin of a pad's end, before or past it, is moved along the pad to that end, keeping how far across the road it
    /// was. A scrap left over from an earlier cut that runs onto the pad from a junction just short of it is so cut back to the
    /// pad's end, and carries the road on to the ramp.
    private void SnapRoadsToPads()
    {
        if (_pads.Count == 0)
            return;
        var roads = _lines.Where(l => !l.Rail && !l.Mark && l.Count >= 2).ToList();
        var uses = new Dictionary<long, int>();
        foreach (var road in roads)
            foreach (var (node, _) in road.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;

        foreach (var road in roads.Where(l => !l.Frozen))
        {
            var moved = false;
            foreach (var point in (int[])[0, road.Count - 1])
            {
                if (road.Nodes.Any(n => n.Point == point && uses.GetValueOrDefault(n.Node) > 1))
                    continue;
                foreach (var pad in _pads)
                {
                    var (t, w) = pad.Local(road.X[point], road.Z[point]);
                    if (MathF.Abs(w) > pad.Half + 1f)
                        continue;
                    var end = MathF.Abs(t - pad.From) <= PadJoin ? pad.From : MathF.Abs(t - pad.To) <= PadJoin ? pad.To : float.NaN;
                    if (float.IsNaN(end) || MathF.Abs(t - end) < 1e-3f)
                        continue;
                    (road.X[point], road.Z[point]) = pad.At(end, w);
                    moved = true;
                    break;
                }
            }

            if (!moved)
                continue;
            for (var p = 1; p < road.Count; p++)
                road.S[p] = road.S[p - 1] + (float)Math.Sqrt((road.X[p] - road.X[p - 1]) * (road.X[p] - road.X[p - 1]) + (road.Z[p] - road.Z[p - 1]) * (road.Z[p] - road.Z[p - 1]));
            (road.MinX, road.MaxX, road.MinZ, road.MaxZ) = (road.X.Min(), road.X.Max(), road.Z.Min(), road.Z.Max());
        }
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

    /// Shapes the ground to each pad's crossing once it's laid, so its box sits in the ground (NetworkMeshes.BuildCrossingMesh).
    /// Under the road's deck the ground's brought to a little under it, cut or filled (over a track's bed, left under the bed).
    /// Everywhere else round it, it's only filled, never cut: up to the road's fill falling away from the shoulders, easing down
    /// past each end, but never over a track's ballast bed (UnderBed). Whatever's left between the crossing's top and what's
    /// under it, the crossing's own gravel embankment covers.
    private void ConformPads(float[] heights, int samples, int gx0, int gz0, (int Line, float D, float Y, float Ex, float Ez)[] near)
    {
        foreach (var pad in _pads)
        {
            var outside = pad.Half + PadOutside;
            var corners = new[] { pad.At(pad.From - PadRoadReach, -outside), pad.At(pad.From - PadRoadReach, outside), pad.At(pad.To + PadRoadReach, -outside), pad.At(pad.To + PadRoadReach, outside) };
            // The roads running on from its ends, raised to its level there and easing back to their own (LevelRoadsAtPads).
            var (sx, sz) = pad.Start;
            var (ex, ez) = pad.End;
            var roads = _lines.Where(l => !l.Rail && !l.Mark && l.Count >= 2 && new[] { 0, l.Count - 1 }.Any(p =>
                Math.Min((l.X[p] - sx) * (l.X[p] - sx) + (l.Z[p] - sz) * (l.Z[p] - sz), (l.X[p] - ex) * (l.X[p] - ex) + (l.Z[p] - ez) * (l.Z[p] - ez)) <= PadJoin * PadJoin)).ToList();
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
                    var side = MathF.Abs(w) - pad.Half;
                    if (along > 0f)
                    {
                        // Past an end: under and beside the road running on, its own fill at its own height, falling away from its
                        // shoulders (the road's own shaping can't reach in here, the track having taken the ground near it).
                        var (wx, wz) = ((x + gx0) * (double)_spacing, (z + gz0) * (double)_spacing);
                        var roadFill = float.MinValue;
                        foreach (var road in roads)
                            for (var i = 0; i + 1 < road.Count; i++)
                            {
                                var (ax, az, bx, bz) = (road.X[i], road.Z[i], road.X[i + 1], road.Z[i + 1]);
                                var (dx, dz) = (bx - ax, bz - az);
                                var length2 = dx * dx + dz * dz;
                                if (length2 < 1e-9)
                                    continue;
                                var u = Math.Clamp(((wx - ax) * dx + (wz - az) * dz) / length2, 0.0, 1.0);
                                var d = (float)Math.Sqrt((ax + dx * u - wx) * (ax + dx * u - wx) + (az + dz * u - wz) * (az + dz * u - wz));
                                var off = d - road.Width * 0.5f;
                                if (off > PadSideReach)
                                    continue;
                                var level = road.Y[i] + (road.Y[i + 1] - road.Y[i]) * (float)u - RoadClearance;
                                roadFill = MathF.Max(roadFill, off <= 0f ? level : level + PadEdgeAbove - (off + PadEdgeIn) * PadSideSlope);
                            }

                        if (roadFill > float.MinValue && along <= PadRoadReach)
                        {
                            var n0 = near[z * samples + x];
                            if (n0.Line >= 0)
                                roadFill = MathF.Min(roadFill, UnderBed(_lines[n0.Line], n0.D, n0.Y, 0.71f));
                            if (roadFill > heights[z * samples + x])
                                heights[z * samples + x] = roadFill;
                            if (n0.Line >= 0 && UnderBed(_lines[n0.Line], n0.D, n0.Y, 0.71f) is var under && heights[z * samples + x] > under)
                                heights[z * samples + x] = under;
                        }

                        if (along > PadEndReach)
                            continue;
                    }

                    if (along > PadEndReach || side > PadSideReach)
                        continue;

                    // The crossing's surface carried on outward: flat under the deck, then the shoulders' slope on down.
                    var surface = MathF.Min(top, top + PadEdgeAbove - (side + PadEdgeIn) * PadSideSlope) - along * PadEndSlope;
                    var sample = z * samples + x;
                    var n = near[sample];
                    var bed = n.Line >= 0 ? UnderBed(_lines[n.Line], n.D, n.Y, 0.71f) : float.MaxValue;

                    if (along <= 0f && side <= 0f)
                    {
                        // Under the road's deck: a little under it (cut or filled), and over a bed under the bed too.
                        heights[sample] = bed < surface ? MathF.Min(heights[sample], bed) : surface;
                        continue;
                    }

                    // Off the deck: filled up to the road's fill, never cut, and never over a track's ballast bed: the bed shows whole,
                    // and where the fill can't reach the crossing's top over it, the crossing's own embankment comes down onto it.
                    var fill = MathF.Min(surface, bed);
                    if (fill > heights[sample])
                        heights[sample] = fill;
                    // And cut down to it wherever anything before raised the ground over a bed: the roads running up to the pad
                    // are lifted to its level and fill their sides at their own slope, the bed's shoulders and fade buried under.
                    if (heights[sample] > bed)
                        heights[sample] = bed;
                    // And clear of the bed as it's drawn, by more than its fade looks its shadow up under it.
                    if (BedClearance((x + gx0) * (double)_spacing, (z + gz0) * (double)_spacing) is { } clear && heights[sample] > clear)
                        heights[sample] = clear;
                }
        }
    }
}
