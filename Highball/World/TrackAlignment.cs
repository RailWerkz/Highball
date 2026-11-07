using RailWerkz.Core.Logging;

namespace Highball.World;

/// Fits the mapped track to what track really is: straights joined by circular curves. OpenStreetMap's points are clicked
/// by hand over aerial photos, so a straight track comes out wandering a metre or so either way; smoothing only makes that
/// a gentler wander, and a train would show every bit of it. So each rail line is cut where it strays from a straight by
/// more than the tolerance, a best-fit line goes through each piece, and a curve joins each line to the next, as big as
/// fits between them. The result is kept as the line's Alignment, the real shape of the track from here on. Junctions on
/// the line move with it. A line is fitted after the lines its ends and other junctions lie on, from where those put them, so
/// nothing is dragged out of shape to follow them. A fit that would bring the track too close to another isn't taken. Tracks side
/// by side are fitted together: each straight takes the direction it shares with the straights around it within a degree
/// or two, so the tracks of a yard come out parallel and the mapper's small differences between them go.
public static class TrackAlignment
{
    /// How far from a straight the mapped track may stray and still be taken for one, tightest last.
    private static readonly float[] Tolerances = [1.25f, 0.75f, 0.4f];
    /// How near a straight's middle another's must be, and how nearly in line, to share its direction.
    private const double ShareReach = 150.0;
    private const double ShareAngle = 0.5 * Math.PI / 180.0;
    private const float ShareLength = 15f;
    /// How close a fit may bring this track to an unconnected one: wide enough for two wagons to pass.
    private const double Clearance = 3.3;
    /// How finely a point's place on a fit is found, and the least it may be from the last point's.
    private const double WalkStep = 0.25;
    private const double MinStep = 0.05;
    /// How far a corner may be from where the line was cut.
    private const double MaxCorner = 4.5;
    /// The shortest straight a stretch is cut into, and the tightest curve a fit may lay.
    private const float MinStraight = 15f;
    private const double MinArc = 80.0;
    /// How many times a stretch that won't fit whole is halved, and the fewest points worth halving.
    private const int Halvings = 3;
    private const int MinHalving = 50;
    private const double CellSize = 8.0;
    /// How far along a road or a disused track it eases into a junction the fit has moved.
    private const float CarryReach = 30f;

    /// Returns the nodes where two tracks cross with no way from one to the other: each is fitted as if the other weren't there.
    public static HashSet<long> Apply(IReadOnlyList<NetworkLine> lines, IReadOnlySet<long> pinned)
    {
        // (Track laid by hand comes first: it isn't moved, so what's fitted has to meet it.)
        var rails = lines.Where(l => l.Rail && !l.Disused).OrderBy(l => l.Exact ? 0 : 1).ThenBy(l => l.Class).ThenBy(l => l.Index).ToList();

        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in lines)
            foreach (var (node, point) in line.Nodes)
            {
                if (!pinned.Contains(node))
                    continue;
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }

        var crossings = uses.Where(u => u.Value.Count == 2 && u.Value.All(l => l.Line.Rail && l.Point > 0 && l.Point < l.Line.Count - 1) &&
            TrackLayouts.Crossing(u.Value.SelectMany(l => new[] { new Leg(l.Line, l.Point, -1), new Leg(l.Line, l.Point, 1) }).ToList())).Select(u => u.Key).ToHashSet();

        // Each junction moves with the first (most important) line running through it.
        var owners = new Dictionary<long, NetworkLine>();
        foreach (var line in rails)
            foreach (var (node, point) in line.Nodes)
                if (pinned.Contains(node) && !crossings.Contains(node) && point > 0 && point < line.Count - 1)
                    owners.TryAdd(node, line);

        var cells = new Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>>();
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p++)
            {
                var key = Cell(line.X[p], line.Z[p]);
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = [];
                list.Add((line.X[p], line.Z[p], line));
            }

        // Every straight there is, for each to take the direction it shares with those around it.
        var straights = new List<(double X, double Z, double Ux, double Uz, float Length, NetworkLine Line)>();
        foreach (var line in rails)
        {
            var pieces = Cuts(line, 0, line.Count - 1, Tolerances[0]);
            for (var i = 0; i + 1 < pieces.Count; i++)
            {
                var ((x, z), (ux, uz)) = BestLine(line, pieces[i], pieces[i + 1]);
                var length = line.S[pieces[i + 1]] - line.S[pieces[i]];
                if (length >= ShareLength)
                    straights.Add((x, z, ux, uz, length, line));
            }
        }

        foreach (var line in lines.Where(l => l.Rail))
            line.Alignment = Alignment.FromPoints(line.X, line.Z);

        var (fitted, refused, tooFar, tooClose) = (0, 0, 0, 0);
        void FitLine(NetworkLine line, bool holdOwn = false)
        {
            if (line.Exact)
                return;

            // Its junctions another line owns go where that line put them.
            var moved = false;
            foreach (var (node, point) in line.Nodes)
                if (owners.TryGetValue(node, out var owner) && owner != line)
                {
                    var at = owner.Nodes.First(n => n.Node == node).Point;
                    if (Math.Abs(owner.X[at] - line.X[point]) + Math.Abs(owner.Z[at] - line.Z[point]) <= 1e-9)
                        continue;
                    // (Eased over, so the line keeps its shape if it can't be fitted.)
                    Carry(line, point, owner.X[at], owner.Z[at], pinned);
                    moved = true;
                }

            if (moved)
                line.Alignment = Alignment.FromPoints(line.X, line.Z);

            // The stretches between what stays put.
            var breaks = new SortedSet<int> { 0, line.Count - 1 };
            foreach (var (node, point) in line.Nodes)
                if (pinned.Contains(node) && !crossings.Contains(node) && (holdOwn || owners.GetValueOrDefault(node) != line))
                    breaks.Add(point);

            // (Tracks joined to this one come together at their junctions, and don't count as neighbours.)
            var joined = line.Nodes.Where(n => uses.ContainsKey(n.Node)).SelectMany(n => uses[n.Node]).Select(u => u.Line).ToHashSet();
            var (oldX, oldZ) = ((double[])line.X.Clone(), (double[])line.Z.Clone());
            var ends = breaks.ToList();
            // A stretch that won't fit whole (one close neighbour shouldn't cost a kilometre of track) is tried in halves.
            void Stretch(int from, int to, int depth)
            {
                if (to - from < 2)
                    return;
                foreach (var tolerance in Tolerances)
                {
                    if (Fit(line, from, to, tolerance, straights, joined) is not { } fit)
                    {
                        tooFar++;
                        continue;
                    }

                    if (!Clear(line, from, fit.Points, joined, cells))
                    {
                        tooClose++;
                        continue;
                    }

                    line.Alignment!.Splice(from, to, fit.Pieces, fit.Along);
                    line.Alignment.Sample(line, from, to);
                    fitted++;
                    return;
                }

                if (depth > 0 && to - from >= MinHalving)
                {
                    Stretch(from, (from + to) / 2, depth - 1);
                    Stretch((from + to) / 2, to, depth - 1);
                }
                else
                    refused++;
            }

            for (var i = 0; i + 1 < ends.Count; i++)
                Stretch(ends[i], ends[i + 1], Halvings);

            TurnoutFitting.Measure(line);

            // Roads and disused track aren't fitted: they bend to follow.
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var others) || owners.GetValueOrDefault(node) != line)
                    continue;
                var (dx, dz) = (line.X[point] - oldX[point], line.Z[point] - oldZ[point]);
                if (dx * dx + dz * dz < 1e-6)
                    continue;
                foreach (var (other, at) in others)
                    if (other != line && (!other.Rail || other.Disused))
                        Carry(other, at, line.X[point], line.Z[point], pinned);
            }
        }

        // Each line after the lines it hangs off; (a ring of them is entered at its most important line).
        var visited = new HashSet<NetworkLine>();
        void Visit(NetworkLine line)
        {
            if (!visited.Add(line))
                return;
            foreach (var (node, _) in line.Nodes)
                if (owners.TryGetValue(node, out var owner) && owner != line)
                    Visit(owner);
            FitLine(line);
        }

        foreach (var line in rails)
            Visit(line);

        // In a ring, a line was fitted before something it hangs off: once more for those, holding the junctions they own so
        // they don't set the ring off again; then whatever's still apart is brought together right at the junction.
        bool Apart(NetworkLine line) => line.Nodes.Any(n => owners.TryGetValue(n.Node, out var owner) && owner != line &&
            Math.Abs(owner.X[owner.Nodes.First(o => o.Node == n.Node).Point] - line.X[n.Point]) + Math.Abs(owner.Z[owner.Nodes.First(o => o.Node == n.Node).Point] - line.Z[n.Point]) > 1e-6);
        foreach (var line in rails.Where(Apart).ToList())
            FitLine(line, true);
        var joinedUp = 0;
        foreach (var line in rails.Where(Apart).ToList())
        {
            joinedUp++;
            foreach (var (node, point) in line.Nodes)
                if (owners.TryGetValue(node, out var owner) && owner != line)
                {
                    var at = owner.Nodes.First(n => n.Node == node).Point;
                    line.Alignment!.Snap(line, point, owner.X[at], owner.Z[at]);
                }

            TurnoutFitting.Measure(line);
        }

        Log.Info($"Alignment: {joinedUp} lines brought together at a junction; refused {tooFar} tries as too far off, {tooClose} as too close to another track");
        Log.Info($"Alignment: {fitted} stretches of track fitted to straights and curves, {refused} left as mapped");
        return crossings;
    }

    private static (int X, int Z) Cell(double x, double z) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));

    /// Moves a line's point to a junction's new place, the line easing to it from each side, as far as its next junction.
    private static void Carry(NetworkLine line, int at, double x, double z, IReadOnlySet<long> pinned)
    {
        var (dx, dz) = (x - line.X[at], z - line.Z[at]);
        var held = line.Nodes.Where(n => pinned.Contains(n.Node) && n.Point != at).Select(n => n.Point).ToList();
        foreach (var direction in (int[])[-1, 1])
        {
            var stop = direction < 0 ? held.Where(p => p < at).DefaultIfEmpty(-1).Max() : held.Where(p => p > at).DefaultIfEmpty(line.Count).Min();
            var reach = CarryReach;
            if (stop >= 0 && stop < line.Count)
                reach = MathF.Min(reach, MathF.Abs(line.S[stop] - line.S[at]));
            for (var p = at + direction; p != stop && p >= 0 && p < line.Count; p += direction)
            {
                var along = MathF.Abs(line.S[p] - line.S[at]);
                if (along >= reach)
                    break;
                var t = 1f - along / reach;
                var weight = t * t * (3f - 2f * t);
                line.X[p] += dx * weight;
                line.Z[p] += dz * weight;
            }
        }

        (line.X[at], line.Z[at]) = (x, z);
        TurnoutFitting.Measure(line);
    }

    /// A path of points fitted to straights and curves, for laying along a guide: its pieces, and its tightest curve. If
    /// it can't be (Fitted false), the path as it is.
    public static (List<TrackPiece> Pieces, double Radius, bool Fitted) FitPath(IReadOnlyList<(double X, double Z)> path)
    {
        var xs = new List<double> { path[0].X };
        var zs = new List<double> { path[0].Z };
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (dx, dz) = (path[i + 1].X - path[i].X, path[i + 1].Z - path[i].Z);
            // (The same place twice, as when the pointer's still on what it just clicked.)
            if (dx * dx + dz * dz < 1e-6)
                continue;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 2.0));
            for (var k = 1; k <= steps; k++)
            {
                xs.Add(path[i].X + dx * k / steps);
                zs.Add(path[i].Z + dz * k / steps);
            }
        }

        if (xs.Count < 2)
            return ([], double.MaxValue, false);

        var (x, z) = (xs.ToArray(), zs.ToArray());
        var s = new float[x.Length];
        for (var i = 1; i < x.Length; i++)
            s[i] = s[i - 1] + (float)Math.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));
        var line = new NetworkLine
        {
            Index = -1, Rail = true, Class = 0, X = x, Z = z, Y = new float[x.Length], Ground = new float[x.Length], Key = 0, S = s,
            Structures = new Structure[x.Length - 1], Nodes = [], MappedX = x, MappedZ = z,
        };

        foreach (var tolerance in Tolerances)
            if (Fit(line, 0, x.Length - 1, tolerance, [], []) is { } fit)
                return (fit.Pieces, fit.Pieces.Where(p => Math.Abs(p.Curvature) > 1e-9).Select(p => 1.0 / Math.Abs(p.Curvature)).DefaultIfEmpty(double.MaxValue).Min(), true);

        return (Alignment.FromPoints(x, z).Pieces, double.MaxValue, false);
    }

    /// Where the stretch strays from a straight by more than the tolerance: its ends, and the points it's cut at.
    private static List<int> Cuts(NetworkLine line, int from, int to, float tolerance)
    {
        var cuts = new List<int> { from, to };
        void Split(int a, int b)
        {
            var (ax, az, bx, bz) = (line.X[a], line.Z[a], line.X[b], line.Z[b]);
            var chord = Math.Max(Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)), 1e-6);
            var (worst, far) = (-1, (double)tolerance);
            for (var p = a + 1; p < b; p++)
            {
                var off = Math.Abs((line.X[p] - ax) * (bz - az) - (line.Z[p] - az) * (bx - ax)) / chord;
                if (off > far)
                    (worst, far) = (p, off);
            }

            if (worst < 0)
                return;
            cuts.Add(worst);
            Split(a, worst);
            Split(worst, b);
        }

        Split(from, to);
        cuts.Sort();

        // No stubs: a straight a few metres long is a mapper's wobble, and a curve fitted to it would be a kink.
        for (var i = 1; i < cuts.Count - 1;)
        {
            if (line.S[cuts[i]] - line.S[cuts[i - 1]] < MinStraight || line.S[cuts[i + 1]] - line.S[cuts[i]] < MinStraight)
                cuts.RemoveAt(i);
            else
                i++;
        }

        return cuts;
    }

    /// The best-fit line through a line's points from a to b, running the way they do.
    private static ((double X, double Z) At, (double X, double Z) Along) BestLine(NetworkLine line, int a, int b)
    {
        double mx = 0, mz = 0;
        for (var p = a; p <= b; p++)
            (mx, mz) = (mx + line.X[p], mz + line.Z[p]);
        (mx, mz) = (mx / (b - a + 1), mz / (b - a + 1));
        double xx = 0, xz = 0, zz = 0;
        for (var p = a; p <= b; p++)
        {
            var (ex, ez) = (line.X[p] - mx, line.Z[p] - mz);
            (xx, xz, zz) = (xx + ex * ex, xz + ex * ez, zz + ez * ez);
        }

        var angle = 0.5 * Math.Atan2(2 * xz, xx - zz);
        var along = (X: Math.Cos(angle), Z: Math.Sin(angle));
        if ((line.X[b] - line.X[a]) * along.X + (line.Z[b] - line.Z[a]) * along.Z < 0)
            along = (-along.X, -along.Z);
        return ((mx, mz), along);
    }

    /// A direction taken together with the straights near a point that run within ShareAngle of it, by their lengths. Not
    /// those of tracks it joins: a track leaving another at a shallow angle has to keep it, or the two never part.
    private static (double X, double Z) Shared(NetworkLine line, (double X, double Z) at, (double X, double Z) along, List<(double X, double Z, double Ux, double Uz, float Length, NetworkLine Line)> straights, HashSet<NetworkLine> joined)
    {
        var (sx, sz) = (0.0, 0.0);
        foreach (var o in straights)
        {
            var (dx, dz) = (o.X - at.X, o.Z - at.Z);
            if (dx * dx + dz * dz > ShareReach * ShareReach || (o.Line != line && joined.Contains(o.Line)))
                continue;
            var dot = o.Ux * along.X + o.Uz * along.Z;
            if (Math.Abs(o.Ux * along.Z - o.Uz * along.X) > Math.Sin(ShareAngle))
                continue;
            var sign = dot >= 0 ? 1.0 : -1.0;
            (sx, sz) = (sx + o.Ux * sign * o.Length, sz + o.Uz * sign * o.Length);
        }

        var length = Math.Sqrt(sx * sx + sz * sz);
        return length > 1e-6 ? (sx / length, sz / length) : along;
    }

    /// The stretch's points fitted to straights and curves, spaced as they were; null if any would move too far.
    private static (List<(double X, double Z)> Points, List<TrackPiece> Pieces, double[] Along)? Fit(NetworkLine line, int from, int to, float tolerance, List<(double X, double Z, double Ux, double Uz, float Length, NetworkLine Line)> straights, HashSet<NetworkLine> joined)
    {
        // Cut it where it strays from a straight.
        var cuts = Cuts(line, from, to, tolerance);

        // The best-fit line through each piece, in the direction it shares with those around it; the first and last start
        // from the ends, which stay put.
        var fits = new List<((double X, double Z) At, (double X, double Z) Along)>();
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (at, along) = BestLine(line, cuts[i], cuts[i + 1]);
            fits.Add((at, Shared(line, at, along, straights, joined)));
        }

        if (fits.Count == 1)
            fits[0] = Through((line.X[from], line.Z[from]), (line.X[to], line.Z[to]));
        else
        {
            fits[0] = Through((line.X[from], line.Z[from]), fits[0].At);
            fits[^1] = Through(fits[^1].At, (line.X[to], line.Z[to]));
        }

        // The corners where each line meets the next.
        var corners = new List<(double X, double Z)> { (line.X[from], line.Z[from]) };
        for (var i = 0; i + 1 < fits.Count; i++)
        {
            var ((ax, az), (ux, uz)) = fits[i];
            var ((bx, bz), (vx, vz)) = fits[i + 1];
            var cross = ux * vz - uz * vx;
            var cut = (line.X[cuts[i + 1]], line.Z[cuts[i + 1]]);
            (double X, double Z)? corner = null;
            if (Math.Abs(cross) > 1e-3)
            {
                var t = ((bx - ax) * vz - (bz - az) * vx) / cross;
                var c = (X: ax + ux * t, Z: az + uz * t);
                if ((c.X - cut.Item1) * (c.X - cut.Item1) + (c.Z - cut.Item2) * (c.Z - cut.Item2) < MaxCorner * MaxCorner)
                    corner = c;
            }

            // (Nearly parallel, or meeting far off: halfway between the two lines at the cut.)
            corners.Add(corner ?? (Midway(fits[i], cut), Midway(fits[i + 1], cut)) switch
            {
                var (p, q) => ((p.X + q.X) / 2, (p.Z + q.Z) / 2),
            });
        }

        corners.Add((line.X[to], line.Z[to]));
        var pieces = Round(corners);
        // (No pieces if the corners all lie in one place.)
        if (pieces.Count == 0 || pieces.Any(q => Math.Abs(q.Curvature) > 1.0 / MinArc))
            return null;

        // Each point goes to the place on the fit nearest it. Too far off, and it isn't a fit.
        var total = Alignment.LengthOf(pieces);
        var points = new List<(double X, double Z)>(to - from + 1);
        var places = new double[to - from + 1];
        var u = 0.0;
        double Off(int p, double at)
        {
            var (x, z) = Alignment.Position(pieces, at);
            return (x - line.X[p]) * (x - line.X[p]) + (z - line.Z[p]) * (z - line.Z[p]);
        }

        for (var p = from; p <= to; p++)
        {
            if (p == from)
                u = 0.0;
            else if (p == to)
                u = total;
            else
            {
                // (Walking on from the last, at least a little, so the points stay in order.)
                var at = Math.Min(u + MinStep, total);
                while (at + WalkStep <= total && Off(p, at + WalkStep) < Off(p, at))
                    at += WalkStep;
                u = at;
            }

            var place = Alignment.Position(pieces, u);
            if (TrackQuality.OffMapped(line, place.X, place.Z, p) > TrackQuality.MaxStray)
                return null;
            places[p - from] = u;
            points.Add(Alignment.Position(pieces, u));
        }

        return (points, pieces, places);
    }

    private static ((double X, double Z) At, (double X, double Z) Along) Through((double X, double Z) a, (double X, double Z) b)
    {
        var length = Math.Max(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)), 1e-9);
        return (a, ((b.X - a.X) / length, (b.Z - a.Z) / length));
    }

    private static (double X, double Z) Midway(((double X, double Z) At, (double X, double Z) Along) line, (double X, double Z) p)
    {
        var t = (p.X - line.At.X) * line.Along.X + (p.Z - line.At.Z) * line.Along.Z;
        return (line.At.X + line.Along.X * t, line.At.Z + line.Along.Z * t);
    }

    /// The corners rounded off: a straight along each side, and a circular arc at each corner, as big as fits with half of
    /// each straight beside it (all of the first and last, which have only the one curve).
    private static List<TrackPiece> Round(List<(double X, double Z)> corners)
    {
        var pieces = new List<TrackPiece>();
        double Length(int i) => Math.Sqrt((corners[i + 1].X - corners[i].X) * (corners[i + 1].X - corners[i].X) + (corners[i + 1].Z - corners[i].Z) * (corners[i + 1].Z - corners[i].Z));
        void Straight((double X, double Z) a, (double X, double Z) b)
        {
            var (dx, dz) = (b.X - a.X, b.Z - a.Z);
            var length = Math.Sqrt(dx * dx + dz * dz);
            if (length > 1e-6)
                pieces.Add(new TrackPiece(a.X, a.Z, Math.Atan2(dz, dx), length, 0.0, true));
        }

        var at = corners[0];
        for (var i = 1; i < corners.Count - 1; i++)
        {
            var (before, after) = (Length(i - 1), Length(i));
            var tangent = Math.Min(i == 1 ? before * 0.9 : before / 2, i == corners.Count - 2 ? after * 0.9 : after / 2);
            var c = corners[i];
            var (ux, uz) = ((c.X - corners[i - 1].X) / Math.Max(before, 1e-9), (c.Z - corners[i - 1].Z) / Math.Max(before, 1e-9));
            var (vx, vz) = ((corners[i + 1].X - c.X) / Math.Max(after, 1e-9), (corners[i + 1].Z - c.Z) / Math.Max(after, 1e-9));
            var turn = Math.Atan2(ux * vz - uz * vx, ux * vx + uz * vz);
            if (Math.Abs(turn) < 1e-4 || tangent < 0.01)
            {
                Straight(at, c);
                at = c;
                continue;
            }

            var start = (X: c.X - ux * tangent, Z: c.Z - uz * tangent);
            Straight(at, start);
            var arc = tangent / Math.Tan(Math.Abs(turn) / 2) * Math.Abs(turn);
            pieces.Add(new TrackPiece(start.X, start.Z, Math.Atan2(uz, ux), arc, turn / arc, true));
            at = (c.X + vx * tangent, c.Z + vz * tangent);
        }

        Straight(at, corners[^1]);
        return pieces;
    }

    /// Whether the fit keeps its distance from other tracks: it doesn't bring any within Clearance that wasn't already, nor closer
    /// if it was. A track joined to this one only stops counting near a junction they meet at: past that, they're two tracks side by side.
    private static bool Clear(NetworkLine line, int from, List<(double X, double Z)> points, HashSet<NetworkLine> joined, Dictionary<(int, int), List<(double X, double Z, NetworkLine Line)>> cells)
    {
        var junctions = line.Nodes.Where(n => n.Point >= 0 && n.Point < line.Count).Select(n => (X: line.X[n.Point], Z: line.Z[n.Point])).ToList();
        bool NearJunction(double x, double z) => junctions.Any(j => (j.X - x) * (j.X - x) + (j.Z - z) * (j.Z - z) < JoinedReach * JoinedReach);

        double Nearest(double x, double z)
        {
            var near = NearJunction(x, z);
            var best = double.MaxValue;
            var (cx, cz) = Cell(x, z);
            for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (cells.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (var q in list)
                            if (q.Line != line && !(near && joined.Contains(q.Line)))
                                best = Math.Min(best, (q.X - x) * (q.X - x) + (q.Z - z) * (q.Z - z));
            return Math.Sqrt(best);
        }

        for (var k = 0; k < points.Count; k++)
        {
            var now = Nearest(points[k].X, points[k].Z);
            if (now < Clearance && now < Nearest(line.X[from + k], line.Z[from + k]) - ClearanceSlack)
                return false;
        }

        return true;
    }

    /// How far from a junction two tracks meeting there may run as close as they have to.
    private const double JoinedReach = 60.0;
    private const double ClearanceSlack = 0.05;
}
