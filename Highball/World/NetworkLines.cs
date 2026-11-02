using System.Globalization;
using Highball.Routes;

namespace Highball.World;

/// What a stretch of line is carried on.
public enum Structure : byte { Ground, Bridge, Tunnel }

/// One road or rail line in world metres (x east, z south), cut to the built tiles: a smooth centreline every couple
/// of metres, the height it runs at (its formation: the ground it's laid on, smoothed, carried straight over bridges
/// and through tunnels, and agreeing with every line it meets), and how wide it is.
public sealed class NetworkLine
{
    public required int Index { get; init; }
    public required bool Rail { get; init; }
    public required int Class { get; init; }
    public required double[] X { get; init; }
    public required double[] Z { get; init; }
    /// Formation height at each point.
    public required float[] Y { get; init; }
    /// The ground as built under each point, which the profile is worked out from.
    public required float[] Ground { get; init; }
    /// Its shape, the same whenever the line comes out the same, so a rebuild after an edit can keep the ground already
    /// sampled under every line the edit didn't touch.
    public required long Key { get; init; }
    /// The built tiles its reshaping of the ground reaches into.
    public HashSet<TileId> Tiles { get; } = [];
    /// Distance along the line at each point.
    public required float[] S { get; init; }
    /// Per segment (point i to i + 1).
    public required Structure[] Structures { get; init; }
    /// Distance along the line to the nearest junction with another line, at each point.
    public float[] Junction { get; set; } = [];
    /// OpenStreetMap nodes this line passes through, with the point each falls on.
    public required List<(long Node, int Point)> Nodes { get; init; }

    // Rails.
    public int Tracks { get; init; } = 1;
    public float Gauge { get; init; } = 1.435f;
    public bool Disused { get; init; }
    /// Tram track: rails set into the street, no ballast or ties.
    public bool Embedded { get; init; }

    // Roads.
    public float Width { get; init; }
    public int Lanes { get; init; }
    public bool Oneway { get; init; }
    public bool Unpaved { get; init; }

    /// Half the width of the flat ground the line is laid on: the ballast and a walkway, or the road and its shoulders.
    public float Formation { get; init; }

    public double MinX, MinZ, MaxX, MaxZ;

    public int Count => X.Length;

    public const float TrackSpacing = 4.1f;
    /// Ballast top beyond the outermost rails' centreline: the tie ends and a foot of shoulder.
    public const float BallastShoulder = 1.6f;
    public const float BallastHeight = 0.40f;
    public const float BallastSlope = 1.5f;
    public const float BallastToe = 0.15f;

    public float TrackOffset(int track) => (track - (Tracks - 1) * 0.5f) * TrackSpacing;

    /// Half the ballast top's width.
    public float BallastHalfTop => (Tracks - 1) * 0.5f * TrackSpacing + BallastShoulder;
}

/// The route's roads and rails as NetworkLines, and the ground reshaped to carry them: every built tile's heights
/// are flattened under each line's formation and run out from it in cut and fill slopes, the way a real line is built.
///
/// Ways are first joined into the longest runs OpenStreetMap allows (where exactly two ways meet end to end and they
/// share their kind), so a line split at every bridge or change of tags still gets one smooth profile.
public sealed class NetworkLines
{
    private const double PointSpacing = 2.0;
    private const float FillSlope = 1f / 1.5f;
    private const float CutSlope = 1f;
    /// How far past the formation a line may reshape the ground; slopes reaching further fade out over the last 10 m.
    private const float MaxReach = 30f;
    /// Points between the corners used for reshaping the ground (a 10 m chord is within centimetres of the curve).
    private const int ConformStride = 5;

    private readonly List<NetworkLine> _lines = [];
    private readonly Dictionary<TileId, List<int>> _byTile = [];
    private readonly Dictionary<TileId, List<(int Line, int Point)>> _pointsByTile = [];
    private readonly float _spacing;
    private readonly int _quads;

    public IReadOnlyList<NetworkLine> Lines => _lines;

    /// Lines whose reshaping reaches into this tile, in the order they're applied.
    public IReadOnlyList<int> LinesIn(TileId tile) => _byTile.TryGetValue(tile, out var list) ? list : [];

    public IEnumerable<TileId> TilesWithPoints => _pointsByTile.Keys;

    private NetworkLines(float spacing, int quads)
    {
        _spacing = spacing;
        _quads = quads;
    }

    // --- Building the lines. ---

    private sealed class Chain
    {
        public required bool Rail;
        public required int Class;
        public required Dictionary<string, string> Tags;
        public readonly List<long> Nodes = [];
        public readonly List<GeoPoint> Points = [];
        /// Per segment.
        public readonly List<Structure> Structures = [];
    }

    /// Joins, smooths and cuts the network's ways to the built tiles. Heights come later, from SampleGround and
    /// FinishProfiles.
    public static NetworkLines Build(IEnumerable<NetworkWay> network, RouteInfo route, HashSet<TileId> built)
    {
        var grid = route.Grid!;
        var lines = new NetworkLines((float)grid.Spacing, grid.Samples - 1);
        var projection = route.Projection!;
        var tileSize = grid.TileSize;

        foreach (var chain in Chains(network))
        {
            var (xs, zs, structures, nodes) = Resample(chain, projection);

            // Cut into runs over built tiles.
            var start = -1;
            for (var i = 0; i <= xs.Count; i++)
            {
                var inside = i < xs.Count && built.Contains(new TileId((int)Math.Floor(xs[i] / tileSize), (int)Math.Floor(-zs[i] / tileSize)));
                if (inside && start < 0)
                    start = i;
                else if (!inside && start >= 0)
                {
                    if (i - start >= 2)
                        lines.Add(chain, xs, zs, structures, nodes, start, i);
                    start = -1;
                }
            }
        }

        foreach (var line in lines._lines)
        {
            var reach = line.Formation + MaxReach;
            var size = lines._quads * lines._spacing;
            for (var j = (int)Math.Floor(-(line.MaxZ + reach) / size); j <= (int)Math.Floor(-(line.MinZ - reach) / size); j++)
            {
                for (var i = (int)Math.Floor((line.MinX - reach) / size); i <= (int)Math.Floor((line.MaxX + reach) / size); i++)
                {
                    var id = new TileId(i, j);
                    if (!built.Contains(id))
                        continue;
                    line.Tiles.Add(id);
                    if (!lines._byTile.TryGetValue(id, out var list))
                        lines._byTile[id] = list = [];
                    list.Add(line.Index);
                }
            }

            for (var p = 0; p < line.Count; p++)
            {
                var id = new TileId((int)Math.Floor(line.X[p] / size), (int)Math.Floor(-line.Z[p] / size));
                if (!lines._pointsByTile.TryGetValue(id, out var points))
                    lines._pointsByTile[id] = points = [];
                points.Add((line.Index, p));
            }
        }

        // Roads first, smallest first, then rails: what's applied last wins where lines cross.
        foreach (var list in lines._byTile.Values)
            list.Sort((a, b) => Priority(lines._lines[a]).CompareTo(Priority(lines._lines[b])));

        return lines;
    }

    /// Classes count up from the biggest, so lower numbers go later; the index keeps the order the same in every tile.
    private static (int, int) Priority(NetworkLine line) => ((line.Rail ? 100 : 0) + 10 - line.Class, line.Index);

    private static IEnumerable<Chain> Chains(IEnumerable<NetworkWay> network)
    {
        var ways = network.Where(w => w.Nodes.Length >= 2 && w.Lat.Length == w.Nodes.Length).OrderBy(w => w.Id).ToList();

        // How often each node is used, and the way ends at it.
        var uses = new Dictionary<long, int>();
        var ends = new Dictionary<long, List<NetworkWay>>();
        foreach (var way in ways)
        {
            foreach (var node in way.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;
            foreach (var node in (long[])[way.Nodes[0], way.Nodes[^1]])
            {
                if (!ends.TryGetValue(node, out var list))
                    ends[node] = list = [];
                list.Add(way);
            }
        }

        var done = new HashSet<long>();
        foreach (var seed in ways)
        {
            if (!done.Add(seed.Id))
                continue;

            var key = ShapeKey(seed);
            var parts = new LinkedList<(NetworkWay Way, bool Reversed)>();
            parts.AddFirst((seed, false));

            // Extend forwards from the last part's end, then backwards from the first part's start.
            for (var forward = 0; forward < 2; forward++)
            {
                while (true)
                {
                    var (edge, reversed) = forward == 0 ? parts.Last!.Value : parts.First!.Value;
                    var node = (forward == 0) != reversed ? edge.Nodes[^1] : edge.Nodes[0];
                    if (uses.GetValueOrDefault(node) != 2 || ends[node].Count != 2)
                        break;

                    var next = ends[node][0] == edge ? ends[node][1] : ends[node][0];
                    if (next == edge || done.Contains(next.Id) || ShapeKey(next) != key)
                        break;

                    // Forwards, the next way should start at the node; backwards, end at it. One-way roads can't flip.
                    var nextReversed = forward == 0 ? next.Nodes[0] != node : next.Nodes[^1] != node;
                    if (nextReversed && IsOneway(next))
                        break;

                    done.Add(next.Id);
                    if (forward == 0)
                        parts.AddLast((next, nextReversed));
                    else
                        parts.AddFirst((next, nextReversed));
                }
            }

            var chain = new Chain { Rail = seed.Rail, Class = seed.Class, Tags = seed.Tags };
            foreach (var (way, reversed) in parts)
            {
                var structure = StructureOf(way);
                for (var n = 0; n < way.Nodes.Length; n++)
                {
                    var k = reversed ? way.Nodes.Length - 1 - n : n;
                    if (n == 0 && chain.Nodes.Count > 0)
                        continue; // the shared node, already added by the previous way
                    if (chain.Nodes.Count > 0)
                        chain.Structures.Add(structure);
                    chain.Nodes.Add(way.Nodes[k]);
                    chain.Points.Add(new GeoPoint(way.Lat[k], way.Lon[k]));
                }
            }

            yield return chain;
        }
    }

    private static string ShapeKey(NetworkWay way)
    {
        string Tag(string name) => way.Tags.GetValueOrDefault(name, "");
        return way.Rail
            ? $"R{way.Class}|{Tag("railway")}|{Tag("tracks")}|{Tag("gauge")}"
            : $"H{way.Class}|{Tag("lanes")}|{Tag("oneway")}|{Tag("surface")}|{Tag("highway").Replace("_link", "")}";
    }

    private static bool IsOneway(NetworkWay way) => !way.Rail && way.Tags.GetValueOrDefault("oneway") is "yes" or "1" or "true" or "-1";

    private static Structure StructureOf(NetworkWay way)
    {
        if (way.Tags.TryGetValue("tunnel", out var tunnel) && tunnel != "no")
            return Structure.Tunnel;
        if (way.Tags.TryGetValue("bridge", out var bridge) && bridge != "no")
            return Structure.Bridge;
        return Structure.Ground;
    }

    /// A centripetal Catmull-Rom spline through the chain's nodes, sampled about every PointSpacing metres.
    private static (List<double> X, List<double> Z, List<Structure> Structures, List<(long, int)> Nodes) Resample(Chain chain, RouteProjection projection)
    {
        var px = new List<double>();
        var pz = new List<double>();
        var nodeIds = new List<long>();
        var structures = new List<Structure>();
        for (var i = 0; i < chain.Points.Count; i++)
        {
            var p = projection.Forward(chain.Points[i]);
            // Drop repeated points; they'd give the spline zero-length knots.
            if (px.Count > 0 && Math.Abs(px[^1] - p.East) + Math.Abs(pz[^1] + p.North) < 0.05)
                continue;
            if (px.Count > 0)
                structures.Add(chain.Structures[i - 1]);
            px.Add(p.East);
            pz.Add(-p.North);
            nodeIds.Add(chain.Nodes[i]);
        }

        var xs = new List<double>();
        var zs = new List<double>();
        var segs = new List<Structure>();
        var nodes = new List<(long, int)>();
        var n = px.Count;
        if (n < 2)
            return (xs, zs, segs, nodes);

        (double X, double Z) P(int i)
        {
            if (i < 0) return (2 * px[0] - px[1], 2 * pz[0] - pz[1]);
            if (i >= n) return (2 * px[n - 1] - px[n - 2], 2 * pz[n - 1] - pz[n - 2]);
            return (px[i], pz[i]);
        }

        for (var i = 0; i < n - 1; i++)
        {
            nodes.Add((nodeIds[i], xs.Count));
            var p0 = P(i - 1);
            var p1 = P(i);
            var p2 = P(i + 1);
            var p3 = P(i + 2);
            static double Knot(double t, (double X, double Z) a, (double X, double Z) b) =>
                t + Math.Max(Math.Sqrt(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z))), 1e-4);
            var t0 = 0.0;
            var t1 = Knot(t0, p0, p1);
            var t2 = Knot(t1, p1, p2);
            var t3 = Knot(t2, p2, p3);

            var length = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Z - p1.Z) * (p2.Z - p1.Z));
            var steps = Math.Max(1, (int)Math.Ceiling(length / PointSpacing));
            for (var s = 0; s < steps; s++)
            {
                var t = t1 + (t2 - t1) * s / steps;
                (double X, double Z) Mix((double X, double Z) a, (double X, double Z) b, double ta, double tb) =>
                    ((tb - t) / (tb - ta) * a.X + (t - ta) / (tb - ta) * b.X, (tb - t) / (tb - ta) * a.Z + (t - ta) / (tb - ta) * b.Z);
                var a1 = Mix(p0, p1, t0, t1);
                var a2 = Mix(p1, p2, t1, t2);
                var a3 = Mix(p2, p3, t2, t3);
                var b1 = Mix(a1, a2, t0, t2);
                var b2 = Mix(a2, a3, t1, t3);
                var c = Mix(b1, b2, t1, t2);
                xs.Add(c.X);
                zs.Add(c.Z);
                segs.Add(structures[i]);
            }
        }

        nodes.Add((nodeIds[n - 1], xs.Count));
        xs.Add(px[n - 1]);
        zs.Add(pz[n - 1]);
        return (xs, zs, segs, nodes);
    }

    private void Add(Chain chain, List<double> xs, List<double> zs, List<Structure> structures, List<(long Node, int Point)> nodes, int from, int to)
    {
        var count = to - from;
        var x = xs.GetRange(from, count).ToArray();
        var z = zs.GetRange(from, count).ToArray();
        var s = new float[count];
        for (var i = 1; i < count; i++)
            s[i] = s[i - 1] + (float)Math.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));

        var key = new HashCode();
        key.Add(chain.Rail);
        key.Add(chain.Class);
        foreach (var (k, v) in chain.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            key.Add(k);
            key.Add(v);
        }
        for (var i = 0; i < count; i++)
        {
            key.Add(Math.Round(x[i], 3));
            key.Add(Math.Round(z[i], 3));
        }
        foreach (var structure in structures.GetRange(from, count - 1))
            key.Add(structure);
        var shape = (long)key.ToHashCode() << 32 | (uint)count;

        string Tag(string name) => chain.Tags.GetValueOrDefault(name, "");
        int Int(string name, int fallback) => int.TryParse(Tag(name).Split(';')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

        var index = _lines.Count;
        NetworkLine line;
        if (chain.Rail)
        {
            var railClass = (RailClass)chain.Class;
            var gauge = railClass == RailClass.NarrowGauge ? 0.914f : 1.435f;
            if (int.TryParse(Tag("gauge").Split(';')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) && mm is > 500 and < 2000)
                gauge = mm / 1000f;
            var tracks = Math.Clamp(Int("tracks", 1), 1, 4);
            var embedded = Tag("railway") == "tram";
            var halfTop = (tracks - 1) * 0.5f * NetworkLine.TrackSpacing + NetworkLine.BallastShoulder;
            line = new NetworkLine
            {
                Index = index, Rail = true, Class = chain.Class, X = x, Z = z, Y = new float[count], Ground = new float[count], Key = shape, S = s,
                Structures = structures.GetRange(from, count - 1).ToArray(),
                Nodes = nodes.Where(n => n.Point >= from && n.Point < to).Select(n => (n.Node, n.Point - from)).ToList(),
                Tracks = tracks, Gauge = gauge, Disused = railClass == RailClass.Disused, Embedded = embedded,
                Formation = embedded ? 2f : halfTop + (NetworkLine.BallastHeight + NetworkLine.BallastToe) * NetworkLine.BallastSlope + 1.0f,
            };
        }
        else
        {
            var roadClass = (RoadClass)chain.Class;
            var oneway = Tag("oneway") is "yes" or "1" or "true" or "-1";
            var lanes = Math.Clamp(Int("lanes", roadClass switch
            {
                RoadClass.Highway => oneway ? 2 : 4,
                RoadClass.Major => 2,
                _ => oneway ? 1 : 2,
            }), 1, 7);
            var laneWidth = roadClass switch { RoadClass.Highway => 3.6f, RoadClass.Major => 3.4f, RoadClass.Minor => 3.2f, RoadClass.Local => 3.0f, _ => 2.5f };
            var shoulders = roadClass switch { RoadClass.Highway => 4.2f, RoadClass.Major => 1.0f, RoadClass.Minor => 0.6f, _ => 0f };
            var width = roadClass == RoadClass.Service ? (Tag("highway") == "track" ? 3f : 4f) : lanes * laneWidth + shoulders;
            var surface = Tag("surface");
            line = new NetworkLine
            {
                Index = index, Rail = false, Class = chain.Class, X = x, Z = z, Y = new float[count], Ground = new float[count], Key = shape, S = s,
                Structures = structures.GetRange(from, count - 1).ToArray(),
                Nodes = nodes.Where(n => n.Point >= from && n.Point < to).Select(n => (n.Node, n.Point - from)).ToList(),
                Width = width, Lanes = lanes, Oneway = oneway,
                Unpaved = Tag("highway") == "track" || surface is "unpaved" or "gravel" or "dirt" or "ground" or "compacted" or "fine_gravel" or "grass" or "sand" or "earth",
                Formation = width * 0.5f + 1.5f,
            };
        }

        line.MinX = x.Min();
        line.MaxX = x.Max();
        line.MinZ = z.Min();
        line.MaxZ = z.Max();
        _lines.Add(line);
    }

    // --- Profiles. ---

    private HashSet<int>? _needGround;

    /// After a rebuild, takes the ground already sampled under every line that came out the same as one of the previous
    /// lines. Returns the tiles whose heights are still needed for the rest (see SampleGround).
    public IEnumerable<TileId> AdoptGround(NetworkLines previous)
    {
        var known = new Dictionary<long, NetworkLine>();
        foreach (var line in previous._lines)
            known.TryAdd(line.Key, line);

        _needGround = [];
        foreach (var line in _lines)
        {
            if (known.TryGetValue(line.Key, out var same) && same.Count == line.Count)
                Array.Copy(same.Ground, line.Ground, line.Count);
            else
                _needGround.Add(line.Index);
        }

        return _pointsByTile.Where(p => p.Value.Any(q => _needGround.Contains(q.Line))).Select(p => p.Key).ToList();
    }

    /// The points that fall in a tile, to have their ground height filled in while its heights are loaded (after
    /// AdoptGround, only those of lines that still need it).
    public void SampleGround(TileId tile, float[] heights, int samples)
    {
        if (!_pointsByTile.TryGetValue(tile, out var points))
            return;

        var gx0 = tile.I * _quads;
        var gz0 = -(tile.J + 1) * _quads;
        foreach (var (l, p) in points)
        {
            if (_needGround is not null && !_needGround.Contains(l))
                continue;
            var line = _lines[l];
            var fx = (float)(line.X[p] / _spacing - gx0);
            var fz = (float)(line.Z[p] / _spacing - gz0);
            var x0 = Math.Clamp((int)MathF.Floor(fx), 0, samples - 2);
            var z0 = Math.Clamp((int)MathF.Floor(fz), 0, samples - 2);
            var tx = Math.Clamp(fx - x0, 0f, 1f);
            var tz = Math.Clamp(fz - z0, 0f, 1f);
            var top = heights[z0 * samples + x0] + (heights[z0 * samples + x0 + 1] - heights[z0 * samples + x0]) * tx;
            var bottom = heights[(z0 + 1) * samples + x0] + (heights[(z0 + 1) * samples + x0 + 1] - heights[(z0 + 1) * samples + x0]) * tx;
            line.Ground[p] = top + (bottom - top) * tz;
        }
    }

    /// Turns the sampled ground into each line's formation: bridges and tunnels carried straight between their ends,
    /// the rest smoothed (railways far more than roads, as they're built), then every junction pulled to one height.
    public void FinishProfiles()
    {
        Parallel.ForEach(_lines, line =>
        {
            Array.Copy(line.Ground, line.Y, line.Count);
            Bridge(line);
            var window = line.Rail ? 30f : 12f;
            Smooth(line, window);
            Smooth(line, window);
        });

        Junctions();
        FindJunctions();

        Parallel.ForEach(_lines, line => line.Junction = JunctionDistances(line));
    }

    /// Straight across every run of bridge or tunnel segments, from the ground at one end to the other.
    private static void Bridge(NetworkLine line)
    {
        var i = 0;
        while (i < line.Structures.Length)
        {
            if (line.Structures[i] == Structure.Ground)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < line.Structures.Length && line.Structures[i] != Structure.Ground)
                i++;
            var end = i; // points start..end span the run

            var y0 = line.Y[start];
            var y1 = line.Y[end];
            if (start == 0) y0 = y1;
            if (end == line.Count - 1 && start > 0) y1 = y0;
            var s0 = line.S[start];
            var length = Math.Max(line.S[end] - s0, 1e-3f);
            for (var p = start; p <= end; p++)
                line.Y[p] = y0 + (y1 - y0) * (line.S[p] - s0) / length;
        }
    }

    /// A moving average over window metres each side, by distance along the line.
    private static void Smooth(NetworkLine line, float window)
    {
        var y = line.Y;
        var s = line.S;
        var result = new float[y.Length];
        var lo = 0;
        var hi = 0;
        double sum = 0;
        for (var i = 0; i < y.Length; i++)
        {
            while (hi < y.Length && s[hi] <= s[i] + window)
                sum += y[hi++];
            while (s[lo] < s[i] - window)
                sum -= y[lo++];
            result[i] = (float)(sum / (hi - lo));
        }

        Array.Copy(result, y, y.Length);
    }

    /// Where lines share a node, they meet at one height: an average weighted towards lines passing through (they
    /// can't bend locally without a bump) and towards rail. Each line eases to it over a stretch either side.
    private void Junctions()
    {
        var uses = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines)
        {
            foreach (var (node, point) in line.Nodes)
            {
                if (!uses.TryGetValue(node, out var list))
                    uses[node] = list = [];
                list.Add((line, point));
            }
        }

        var deltas = new List<(NetworkLine Line, int Point, float Delta)>();
        foreach (var list in uses.Values)
        {
            if (list.Count < 2)
                continue;

            double sum = 0, weights = 0;
            foreach (var (line, point) in list)
            {
                var through = point > 0 && point < line.Count - 1;
                var weight = (line.Rail ? 100.0 : 1.0) * (through ? 10.0 : 1.0);
                sum += line.Y[point] * weight;
                weights += weight;
            }

            var target = (float)(sum / weights);
            foreach (var (line, point) in list)
                deltas.Add((line, point, target - line.Y[point]));
        }

        foreach (var (line, point, delta) in deltas)
        {
            var reach = line.Rail ? 150f : 40f;
            var s0 = line.S[point];
            for (var p = point; p >= 0 && s0 - line.S[p] < reach; p--)
                line.Y[p] += delta * Ease(1f - (s0 - line.S[p]) / reach);
            for (var p = point + 1; p < line.Count && line.S[p] - s0 < reach; p++)
                line.Y[p] += delta * Ease(1f - (line.S[p] - s0) / reach);
        }
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);

    private float[] JunctionDistances(NetworkLine line)
    {
        var distances = new float[line.Count];
        Array.Fill(distances, float.MaxValue);
        foreach (var (node, point) in line.Nodes)
            if (_junctionNodes.Contains(node))
                distances[point] = 0f;

        for (var p = 1; p < line.Count; p++)
            distances[p] = Math.Min(distances[p], distances[p - 1] + line.S[p] - line.S[p - 1]);
        for (var p = line.Count - 2; p >= 0; p--)
            distances[p] = Math.Min(distances[p], distances[p + 1] + line.S[p + 1] - line.S[p]);
        return distances;
    }

    private HashSet<long> _junctionNodes = [];

    /// Nodes more than one line touches.
    private void FindJunctions()
    {
        var seen = new Dictionary<long, int>();
        foreach (var line in _lines)
            foreach (var (node, _) in line.Nodes)
                seen[node] = seen.GetValueOrDefault(node) + 1;
        _junctionNodes = seen.Where(p => p.Value > 1).Select(p => p.Key).ToHashSet();
    }

    // --- Reshaping the ground. ---

    /// Flattens a tile's heights (Samples x Samples, rows north to south) under every line's formation, with cut and
    /// fill slopes out to where they meet the ground. Bridges and tunnels leave the ground alone.
    public void Conform(TileId tile, float[] heights, int samples)
    {
        if (!_byTile.TryGetValue(tile, out var list))
            return;

        var gx0 = tile.I * _quads;
        var gz0 = -(tile.J + 1) * _quads;
        foreach (var index in list)
        {
            var line = _lines[index];
            var reach = line.Formation + MaxReach;
            var corners = Corners(line);
            for (var c = 0; c < corners.Count - 1; c++)
            {
                var a = corners[c];
                var b = corners[c + 1];
                if (!Grounded(line, a, b))
                    continue;

                var ax = line.X[a] / _spacing - gx0;
                var az = line.Z[a] / _spacing - gz0;
                var bx = line.X[b] / _spacing - gx0;
                var bz = line.Z[b] / _spacing - gz0;
                var r = reach / _spacing;
                var x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r));
                var x1 = Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r));
                var z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r));
                var z1 = Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r));
                if (x0 > x1 || z0 > z1)
                    continue;

                var dx = bx - ax;
                var dz = bz - az;
                var length2 = dx * dx + dz * dz;
                var ya = line.Y[a];
                var yb = line.Y[b];
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                        var ex = ax + dx * t - x;
                        var ez = az + dz * t - z;
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * _spacing;
                        if (d > reach)
                            continue;

                        var target = ya + (yb - ya) * (float)t;
                        var run = MathF.Max(0f, d - line.Formation);
                        var h = heights[z * samples + x];
                        var shaped = Math.Clamp(h, target - run * FillSlope, target + run * CutSlope);
                        var fade = Ease(Math.Clamp((reach - d) / 10f, 0f, 1f));
                        heights[z * samples + x] = h + (shaped - h) * fade;
                    }
                }
            }
        }
    }

    /// Every ConformStride-th point, the ends, and both ends of each bridge or tunnel.
    private static List<int> Corners(NetworkLine line)
    {
        var corners = new List<int>();
        for (var p = 0; p < line.Count; p++)
        {
            var edge = p > 0 && p < line.Count - 1 && line.Structures[p - 1] != line.Structures[p];
            if (p % ConformStride == 0 || p == line.Count - 1 || edge)
                corners.Add(p);
        }

        return corners;
    }

    private static bool Grounded(NetworkLine line, int a, int b)
    {
        for (var s = a; s < b; s++)
            if (line.Structures[s] != Structure.Ground)
                return false;
        return true;
    }
}
