using System.Globalization;
using Highball.Routes;

namespace Highball.World;

/// What a stretch of line is carried on.
public enum Structure : byte { Ground, Bridge, Tunnel }

/// What a track's laid on. Mainline: a full bed of ballast, raised on the ground. Low: a lesser one, for yards and branches. Sunken: no
/// bed to speak of, the ties in the ground and the rock worked into it, for industrial track. Street: rails flush in a pavement.
public enum TrackBed { Mainline, Low, Sunken, Street }

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
    /// A rail line's points as mapped, before any fitting: what the track is measured against.
    public double[] MappedX { get; set; } = [];
    public double[] MappedZ { get; set; } = [];
    /// A rail line's real shape, of which X and Z are samples.
    public Alignment? Alignment { get; set; }
    /// Where turnouts take over its rails and ties (see Turnout).
    public List<TurnoutZone> TurnoutZones { get; } = [];

    /// Where a diamond cuts its rails (line distances, and which rail: +1 its right) and its ties (see Diamonds).
    public List<(float From, float To, int Side)> RailCuts { get; } = [];
    public List<(float From, float To)> TieCuts { get; } = [];

    // Rails.
    public int Tracks { get; init; } = 1;
    public float Gauge { get; init; } = 1.435f;
    public bool Disused { get; init; }
    /// Tram track: rails set into the street, no ballast or ties.
    public bool Embedded { get; init; }
    /// Laid by hand along a guide: already what it should be, so it isn't fitted.
    public bool Exact { get; init; }

    /// A mapped track's mark on the ground: it lies on the ground as it is and doesn't reshape it.
    public bool Mark { get; init; }
    /// A mark's height at MarkPoints places across it, at each point: the finished ground under it.
    public float[] Drape { get; set; } = [];
    public const int MarkPoints = 5;

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

    /// How high a track's bed stands on the ground, as it comes (see TrackBed).
    public static float BedHeightOf(TrackBed bed) => bed switch
    {
        TrackBed.Low => 0.15f,
        TrackBed.Sunken => 0.04f,
        TrackBed.Street => 0f,
        _ => BallastHeight,
    };

    public TrackBed Bed { get; init; }
    public float BedHeight { get; init; } = BallastHeight;

    /// How far under the rails' usual height over the ground it's been let down, at each point. The rails and all that goes with them
    /// stand BallastHeight over Y as ever, so a lower bed is Y let down by this and the ground below it left where it was.
    public float[] Sink { get; set; } = [];

    public float SinkOf(int point) => point < Sink.Length ? Sink[point] : 0f;

    public float SinkAt(float s)
    {
        if (Sink.Length == 0)
            return 0f;
        var at = Array.BinarySearch(S, s);
        if (at >= 0)
            return Sink[at];
        at = ~at;
        if (at <= 0)
            return Sink[0];
        if (at >= Count)
            return Sink[Count - 1];
        return Sink[at - 1] + (Sink[at] - Sink[at - 1]) * (s - S[at - 1]) / MathF.Max(S[at] - S[at - 1], 1e-5f);
    }
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
public sealed partial class NetworkLines
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

    /// The switches where one rail line divides into two, found once the profiles are finished.
    public List<Turnout> Turnouts { get; private set; } = [];

    /// The layouts at the junctions (turnouts, crossovers, diamonds...) and what needs fixing, once FindLayouts has run.
    public List<Layout> Layouts { get; private set; } = [];

    /// Reads the junctions as layouts, with the switches and crossings tagged in OpenStreetMap; after FinishProfiles.
    public void FindLayouts(IReadOnlyDictionary<long, NetworkNode> tagged)
    {
        foreach (var line in _lines)
        {
            line.RailCuts.Clear();
            line.TieCuts.Clear();
        }

        Layouts = Diamonds.Fit(this, TrackLayouts.Find(this, tagged));

        // A switch whose routes take far longer to part than any real one's, because the mapped tracks run almost together.
        foreach (var t in Turnouts.Where(t => t.Number is not null && t.FrogNormal > LongestFrog))
            Layouts.Add(new Layout(JunctionKind.Unknown, t.X, t.Z, [t.Node], t.Number,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Long switch: frog {t.FrogNormal:0} m past the points")));

        // Whatever came out wrong, whatever made it so, is on the list too.
        foreach (var defect in TrackQuality.Find(this).Where(d => d.Kind != DefectKind.Wiggle))
        {
            var line = _lines[defect.Line];
            var nearest = line.Nodes.Count == 0 ? 0L : line.Nodes.MinBy(n => (line.X[n.Point] - defect.X) * (line.X[n.Point] - defect.X) + (line.Z[n.Point] - defect.Z) * (line.Z[n.Point] - defect.Z)).Node;
            var issue = defect.Kind == DefectKind.Bend
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Sharp bend: {defect.Value:0} m radius")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Height step: {defect.Value:0.00} m");
            Layouts.Add(new Layout(JunctionKind.Unknown, defect.X, defect.Z, [nearest], null, issue));
        }
    }

    /// The diamonds' frogs and guard rails (Diamonds), once FindLayouts has run.
    public List<CrossingAssembly> CrossingAssemblies { get; } = [];

    /// A No. 20's frog is 52 m past its points; well past that, a switch wants looking at.
    private const float LongestFrog = 80f;

    /// The switches that are ladder links' (a pair joining converging tracks), by node.
    public HashSet<long> Links { get; private set; } = [];

    /// Why a crossover couldn't be laid out to a standard design, by both its switches' nodes.
    public Dictionary<long, string> CrossoverProblems { get; private set; } = [];

    /// What each switch could be fitted to, by its node (for the Editor's switch picker).
    public Dictionary<long, SwitchOptions> SwitchOptions { get; } = [];

    /// The standard size each fitted switch was laid out to, by its node and its diverging line's index.
    public Dictionary<(long Node, int Line), int> Fitted { get; private set; } = [];

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

    /// The lines for the network's ways on the built tiles: the track as saved with the route, if it was solved from these
    /// same ways (solved and saved if not), with the lines shaped by hand laid over it. Heights come later, from SampleGround
    /// and FinishProfiles.
    public static NetworkLines Build(IEnumerable<NetworkWay> network, RouteInfo route, HashSet<TileId> built, IReadOnlyDictionary<long, Alignment> shapes)
    {
        var grid = route.Grid!;
        var lines = new NetworkLines((float)grid.Spacing, grid.Samples - 1);
        var chosen = SwitchSizes.Load(route);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var curves = SwitchCurves.Load(route);
        var stamp = StampOf(network, built, chosen, curves, grid.Spacing);
        if (lines.LoadSolved(route, stamp))
            RailWerkz.Core.Logging.Log.Info($"Track: {lines._lines.Count} lines loaded as saved in {watch.ElapsedMilliseconds} ms");
        else
        {
            Solve(lines, network, route, built, chosen, curves);
            RailWerkz.Core.Logging.Log.Info($"Track: {lines._lines.Count} lines solved in {watch.ElapsedMilliseconds} ms");
            lines.SaveSolved(route, stamp);
        }

        lines.LayShapes(shapes);
        lines.IndexLines(built);
        return lines;
    }

    /// Joins, smooths and cuts the network's ways to the built tiles, and lays out its track and switches.
    private static void Solve(NetworkLines lines, IEnumerable<NetworkWay> network, RouteInfo route, HashSet<TileId> built, IReadOnlyDictionary<long, int> chosen, IReadOnlyDictionary<long, float> curves)
    {
        var projection = route.Projection!;
        var tileSize = route.Grid!.TileSize;

        // Mapped track isn't built: it's only a guide to lay track along, marked on the ground where the track would go. The marks are
        // lines like any road, so no switch, signal or train ever sees them.
        var marks = network.Where(w => w.Rail && RouteNetwork.IsGuide(w)).Select(w => new NetworkWay
        {
            Id = w.Id, Rail = false, Class = (int)RoadClass.Service, Nodes = w.Nodes, Lat = w.Lat, Lon = w.Lon,
            Tags = new Dictionary<string, string>(w.Tags) { [RouteNetwork.TrackMarkTag] = "yes", ["surface"] = "gravel", ["lanes"] = "1" },
        }).ToList();
        network = network.Where(w => !RouteNetwork.IsGuide(w)).ToList();

        // One track splitting into three at a node becomes switches one after another, as real track does.
        network = FanFitting.Apply(network, route);

        // Crossovers laid out to a real design first, on the ways (their switch points slide along the tracks).
        var crossovers = CrossoverFitting.Apply(network, route, chosen);

        // (Marks don't join anything: each keeps to its own mapped line, at the height of the ground under it.)
        foreach (var chain in Chains(marks))
        {
            var (mx, mz, ms, mn) = Resample(chain, projection);
            var at = -1;
            for (var i = 0; i <= mx.Count; i++)
            {
                var inside = i < mx.Count && built.Contains(new TileId((int)Math.Floor(mx[i] / tileSize), (int)Math.Floor(-mz[i] / tileSize)));
                if (inside && at < 0)
                    at = i;
                else if (!inside && at >= 0)
                {
                    if (i - at >= 2)
                        lines.Add(chain, mx, mz, ms, mn, at, i);
                    at = -1;
                }
            }
        }

        // Nodes more than one chain runs through (junctions) stay exactly where they are when a line is evened out.
        var chains = Chains(crossovers.Ways).ToList();
        var seen = new Dictionary<long, int>();
        foreach (var chain in chains)
            foreach (var node in chain.Nodes.Distinct())
                seen[node] = seen.GetValueOrDefault(node) + 1;
        var pinned = seen.Where(n => n.Value > 1).Select(n => n.Key).ToHashSet();

        foreach (var chain in chains)
        {
            var (xs, zs, structures, nodes) = Resample(chain, projection);
            // (Yard tracks stand a few metres apart, closer than this could move them: they keep the line they were mapped on.)
            if (chain.Rail && chain.Class != (int)RailClass.Yard && !chain.Tags.ContainsKey(RouteNetwork.ExactTag))
                EvenOut(xs, zs, nodes, pinned);

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

        // Switches laid out to a real design before anything is built on the track.
        foreach (var line in lines._lines.Where(l => l.Rail))
            (line.MappedX, line.MappedZ) = ((double[])line.X.Clone(), (double[])line.Z.Clone());

        // (Two tracks crossing each keep their own line through it: neither is bent to carry the other.)
        var crossings = TrackAlignment.Apply(lines._lines, pinned);
        var joined = pinned.Except(crossings).ToHashSet();
        TrackFairing.Prepare(lines._lines, joined);
        var held = new Dictionary<NetworkLine, List<(float From, float To)>>();
        lines.Fitted = TurnoutFitting.Fit(lines._lines, chosen, lines.SwitchOptions, crossovers.Forced, held, curves);
        TrackFairing.Fair(lines._lines, held, joined);

        // The track is its alignment, whatever's been done to the points.
        var (fittedLength, totalLength, stray) = (0.0, 0.0, 0.0);
        foreach (var line in lines._lines.Where(l => l.Alignment is not null && !l.Disused))
        {
            var (x, z) = ((double[])line.X.Clone(), (double[])line.Z.Clone());
            line.Alignment!.Sample(line);
            for (var p = 0; p < line.Count; p++)
                stray = Math.Max(stray, Math.Sqrt((x[p] - line.X[p]) * (x[p] - line.X[p]) + (z[p] - line.Z[p]) * (z[p] - line.Z[p])));
            TurnoutFitting.Measure(line);
            fittedLength += line.Alignment.Pieces.Where(q => q.Fitted).Sum(q => q.Length);
            totalLength += line.Alignment.Length;
        }

        RailWerkz.Core.Logging.Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Alignment: {fittedLength / Math.Max(totalLength, 1.0):P1} of the track is true straights and arcs; points were {stray:0.000} m off it at most"));
        foreach (var (node, options) in crossovers.Options)
            lines.SwitchOptions[node] = options;
        lines.CrossoverProblems = crossovers.Problems;
        lines.Links = crossovers.Links;
    }

    /// The lines shaped by hand, over the solved track. A junction that's since moved under one (a line it meets was solved
    /// again) is where the line goes, not the other way about; a shape for a line that's since changed is dropped.
    private void LayShapes(IReadOnlyDictionary<long, Alignment> shapes)
    {
        if (shapes.Count == 0)
            return;

        var shaped = new HashSet<NetworkLine>();
        var lost = shapes.Count;
        foreach (var line in _lines)
            if (shapes.TryGetValue(line.Key, out var shape) && shape.U.Length == line.Count)
            {
                line.Alignment = Alignment.Restored([.. shape.Pieces], (double[])shape.U.Clone());
                line.Alignment.Sample(line);
                TurnoutFitting.Measure(line);
                shaped.Add(line);
                lost--;
            }

        if (lost > 0)
            RailWerkz.Core.Logging.Log.Warn($"Track edits: {lost} hand-shaped lines no longer match the map data, and are as solved");

        var places = new Dictionary<long, (double X, double Z)>();
        foreach (var line in _lines.Where(l => l.Rail && !shaped.Contains(l)))
            foreach (var (node, point) in line.Nodes)
                places.TryAdd(node, (line.X[point], line.Z[point]));

        foreach (var line in shaped)
        {
            foreach (var (node, point) in line.Nodes)
                if (places.TryGetValue(node, out var place) && Math.Abs(place.X - line.X[point]) + Math.Abs(place.Z - line.Z[point]) > 0.01)
                    line.Alignment!.Snap(line, point, place.X, place.Z);
            TurnoutFitting.Measure(line);
        }
    }

    /// Whether more than one line touches this node; once the profiles are finished.
    public bool IsJunction(long node) => _junctionNodes.Contains(node);

    /// The ends of roads (or of mapped tracks' marks) that meet at this end of a line, this one among them, always in the
    /// same order; just this one if it meets nothing end to end. Bridges and tunnels make their own ends.
    public IReadOnlyList<(NetworkLine Line, int Point)> EndsMeetingAt(NetworkLine line, int point)
    {
        _roadEnds ??= FindRoadEnds();
        return line.Nodes.FirstOrDefault(n => n.Point == point) is { } at && _roadEnds.TryGetValue((at.Node, line.Mark), out var ends) && ends.Exists(e => e.Line == line && e.Point == point)
            ? ends
            : [(line, point)];
    }

    private Dictionary<(long Node, bool Mark), List<(NetworkLine Line, int Point)>> FindRoadEnds()
    {
        var found = new Dictionary<(long, bool), List<(NetworkLine, int)>>();
        foreach (var line in _lines.Where(l => !l.Rail).OrderBy(l => l.Index))
        {
            foreach (var (node, point) in line.Nodes)
            {
                if (point != 0 && point != line.Count - 1)
                    continue;
                if (line.Structures[Math.Min(point, line.Structures.Length - 1)] is Structure.Bridge or Structure.Tunnel)
                    continue;
                if (!found.TryGetValue((node, line.Mark), out var list))
                    found[(node, line.Mark)] = list = [];
                list.Add((line, point));
            }
        }

        return found;
    }

    private Dictionary<(long Node, bool Mark), List<(NetworkLine Line, int Point)>>? _roadEnds;

    /// Which tiles each line reaches and has points on.
    private void IndexLines(HashSet<TileId> built)
    {
        var lines = this;
        foreach (var line in lines._lines)
        {
            if (line.Mark)
                line.Drape = new float[line.Count * NetworkLine.MarkPoints];
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
                    if (next == edge || done.Contains(next.Id) || ShapeKey(next) != key || (edge.Rail && DoublesBack(edge, next, node)))
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

    /// Whether two ways ending at a node leave it the same way, like a switch's two legs with its toe gone: no track runs
    /// from one to the other, so each ends there.
    private static bool DoublesBack(NetworkWay a, NetworkWay b, long node)
    {
        (double North, double East) Away(NetworkWay way)
        {
            var (from, to) = way.Nodes[0] == node ? (0, 1) : (way.Nodes.Length - 1, way.Nodes.Length - 2);
            return (way.Lat[to] - way.Lat[from], (way.Lon[to] - way.Lon[from]) * Math.Cos(way.Lat[from] * Math.PI / 180.0));
        }

        var (p, q) = (Away(a), Away(b));
        return p.North * q.North + p.East * q.East > 0.0;
    }

    private static string ShapeKey(NetworkWay way)
    {
        string Tag(string name) => way.Tags.GetValueOrDefault(name, "");
        return way.Rail
            ? $"R{way.Class}|{Tag("railway")}|{Tag("tracks")}|{Tag("gauge")}|{Tag(RouteNetwork.ExactTag)}|{Tag(RouteNetwork.BedTag)}|{Tag(RouteNetwork.BedHeightTag)}"
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

    // Evening out a rail line's alignment: the stretch the smoothing reaches over, how far from a junction or an end
    // it takes to come in fully, and the most it may move the track.
    /// How wide a mapped track's mark on the ground is: narrow enough that tracks side by side stay apart.
    private const float TrackMarkWidth = 2.8f;

    private const int AlignStride = 5;
    private const int AlignPasses = 30;
    private const double AlignEase = 30.0;
    private const double AlignLimit = 1.2;

    /// Takes the wobble out of a rail line's alignment. OpenStreetMap's nodes, traced off aerial photographs, are each
    /// off by up to a metre or so, and the spline through them wanders by as much every few tens of metres, which no
    /// railway does. Taubin smoothing over neighbours AlignStride points (about 10 m) apart takes out wavelengths
    /// shorter than a hundred metres or so without pulling curves in; junctions and the line's ends stay put (eased into
    /// over AlignEase), and no point moves more than AlignLimit.
    private static void EvenOut(List<double> xs, List<double> zs, List<(long Node, int Point)> nodes, HashSet<long> pinned)
    {
        var n = xs.Count;
        if (n < AlignStride * 4)
            return;

        var s = new double[n];
        for (var i = 1; i < n; i++)
            s[i] = s[i - 1] + Math.Sqrt((xs[i] - xs[i - 1]) * (xs[i] - xs[i - 1]) + (zs[i] - zs[i - 1]) * (zs[i] - zs[i - 1]));

        // How far each point is from the nearest pin, along the line.
        var toPin = new double[n];
        Array.Fill(toPin, double.MaxValue);
        toPin[0] = toPin[n - 1] = 0;
        foreach (var (node, point) in nodes)
            if (pinned.Contains(node))
                toPin[point] = 0;
        for (var i = 1; i < n; i++)
            toPin[i] = Math.Min(toPin[i], toPin[i - 1] + s[i] - s[i - 1]);
        for (var i = n - 2; i >= 0; i--)
            toPin[i] = Math.Min(toPin[i], toPin[i + 1] + s[i + 1] - s[i]);

        var x = xs.ToArray();
        var z = zs.ToArray();
        var nx = new double[n];
        var nz = new double[n];
        for (var pass = 0; pass < AlignPasses; pass++)
        {
            foreach (var factor in (ReadOnlySpan<double>)[0.5, -0.53])
            {
                for (var i = 0; i < n; i++)
                {
                    var a = Math.Max(0, i - AlignStride);
                    var b = Math.Min(n - 1, i + AlignStride);
                    if (a == i || b == i || toPin[i] == 0)
                    {
                        (nx[i], nz[i]) = (x[i], z[i]);
                        continue;
                    }

                    // The neighbours' middle, weighted by distance so uneven spacing doesn't pull the point along.
                    var (da, db) = (s[i] - s[a], s[b] - s[i]);
                    var mx = (x[a] * db + x[b] * da) / (da + db);
                    var mz = (z[a] * db + z[b] * da) / (da + db);
                    (nx[i], nz[i]) = (x[i] + (mx - x[i]) * factor, z[i] + (mz - z[i]) * factor);
                }

                (x, nx) = (nx, x);
                (z, nz) = (nz, z);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var t = Math.Clamp(toPin[i] / AlignEase, 0.0, 1.0);
            var ease = t * t * (3 - 2 * t);
            var (dx, dz) = ((x[i] - xs[i]) * ease, (z[i] - zs[i]) * ease);
            var moved = Math.Sqrt(dx * dx + dz * dz);
            if (moved > AlignLimit)
                (dx, dz) = (dx * AlignLimit / moved, dz * AlignLimit / moved);
            xs[i] += dx;
            zs[i] += dz;
        }
    }

    private void Add(Chain chain, List<double> xs, List<double> zs, List<Structure> structures, List<(long Node, int Point)> nodes, int from, int to)
    {
        var count = to - from;
        var x = xs.GetRange(from, count).ToArray();
        var z = zs.GetRange(from, count).ToArray();
        var s = new float[count];
        for (var i = 1; i < count; i++)
            s[i] = s[i - 1] + (float)Math.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));

        var key = new StableHash();
        key.Add(chain.Rail ? 1L : 0L);
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
            key.Add((long)structure);
        var shape = (long)(key.Value & 0xFFFFFFFF00000000UL) | (uint)count;

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
            var bed = Enum.TryParse<TrackBed>(Tag(RouteNetwork.BedTag), true, out var chosen) ? chosen : railClass switch
            {
                RailClass.Yard => TrackBed.Low,
                RailClass.Industrial => TrackBed.Sunken,
                _ => TrackBed.Mainline,
            };
            var bedHeight = float.TryParse(Tag(RouteNetwork.BedHeightTag), NumberStyles.Float, CultureInfo.InvariantCulture, out var high)
                ? Math.Clamp(high, 0.02f, 1.5f)
                : NetworkLine.BedHeightOf(bed);
            var embedded = Tag("railway") == "tram" || bed == TrackBed.Street;
            var halfTop = (tracks - 1) * 0.5f * NetworkLine.TrackSpacing + NetworkLine.BallastShoulder;
            line = new NetworkLine
            {
                Index = index, Rail = true, Class = chain.Class, X = x, Z = z, Y = new float[count], Ground = new float[count], Key = shape, S = s,
                Structures = structures.GetRange(from, count - 1).ToArray(),
                Nodes = nodes.Where(n => n.Point >= from && n.Point < to).Select(n => (n.Node, n.Point - from)).ToList(),
                Tracks = tracks, Gauge = gauge, Disused = railClass == RailClass.Disused, Embedded = embedded, Exact = chain.Tags.ContainsKey(RouteNetwork.ExactTag),
                Bed = bed, BedHeight = embedded ? 0f : bedHeight,
                Formation = embedded ? 2f : halfTop + (bedHeight + NetworkLine.BallastToe) * NetworkLine.BallastSlope + 1.0f,
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
            var mark = chain.Tags.ContainsKey(RouteNetwork.TrackMarkTag);
            var width = mark ? TrackMarkWidth : roadClass == RoadClass.Service ? (Tag("highway") == "track" ? 3f : 4f) : lanes * laneWidth + shoulders;
            var surface = Tag("surface");
            line = new NetworkLine
            {
                Index = index, Rail = false, Class = chain.Class, X = x, Z = z, Y = new float[count], Ground = new float[count], Key = shape, S = s,
                Structures = structures.GetRange(from, count - 1).ToArray(),
                Nodes = mark ? [] : nodes.Where(n => n.Point >= from && n.Point < to).Select(n => (n.Node, n.Point - from)).ToList(),
                Mark = mark, Width = width, Lanes = lanes, Oneway = oneway,
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
            // (A line shaped by hand has the same key and points, in new places.)
            if (known.TryGetValue(line.Key, out var same) && same.Count == line.Count && same.X.AsSpan().SequenceEqual(line.X) && same.Z.AsSpan().SequenceEqual(line.Z))
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

    /// Turns the sampled ground into each line's formation, then pulls every junction to one height. Bridges and
    /// tunnels are carried straight between their ends; roads follow the ground, smoothed.
    ///
    /// Railways are built far flatter than the ground they cross, and the terrain data has errors a railway doesn't:
    /// road decks over the line, trees and buildings, dips where water or a cutting confused it. So a rail line is
    /// fitted rather than draped: a running median takes out anything shorter than RailMedian (an overpass, a dip),
    /// then every track is smoothed together with the track beside it (RailPoints: tracks side by side in a yard come
    /// out level with each other, a flyover's ramp climbing past them doesn't), held to its class's steepest grade,
    /// and smoothed again so the grade changes over a vertical curve rather than at a point.
    public void FinishProfiles()
    {
        Parallel.ForEach(_lines, line =>
        {
            Array.Copy(line.Ground, line.Y, line.Count);
            if (line.Mark)
            {
                Bridge(line);
                for (var p = 0; p < line.Count; p++)
                    line.Drape.AsSpan(p * NetworkLine.MarkPoints, NetworkLine.MarkPoints).Fill(line.Y[p]);
            }
            else if (line.Rail)
            {
                // Bridged first, so the valley under a bridge doesn't drag its approaches down.
                Bridge(line);
                Median(line, RailMedian);
                Smooth(line, 30f);
            }
            else
            {
                Bridge(line);
                Smooth(line, 12f);
                Smooth(line, 12f);
            }
        });

        var surfaceWatch = System.Diagnostics.Stopwatch.StartNew();
        // Each pass draws tracks close in height closer still; a few make tracks side by side agree to millimetres.
        var wide = new RailPoints(_lines, SurfaceReach);
        for (var pass = 0; pass < 3; pass++)
            wide.Smooth(SurfaceSpread, SurfaceStep);

        Parallel.ForEach(_lines.Where(l => l.Rail), line =>
        {
            Bridge(line);
            LimitGrade(line, SteepestGrade(line));
            Smooth(line, 40f);
            Smooth(line, 40f);
        });

        // Last, tracks right beside each other (and close in height) are made to agree exactly, so one's ballast never
        // stands over the other's ties.
        var close = new RailPoints(_lines, 6.0);
        for (var pass = 0; pass < 4; pass++)
            close.Smooth(1000.0, 0.15f);
        RailWerkz.Core.Logging.Log.Info($"Profiles: fitted in {surfaceWatch.ElapsedMilliseconds} ms");
        Parallel.ForEach(_lines.Where(l => l.Rail), line =>
        {
            // The deck straight between its ends again, then the grade eased onto and off it over a short curve.
            Bridge(line);
            Smooth(line, 15f);
        });

        Junctions();
        FindJunctions();
        LetDownBeds();

        Parallel.ForEach(_lines, line => line.Junction = JunctionDistances(line));
        Layer();
        Turnouts = Turnout.Find(_lines, Fitted);
        LevelRoutes();
    }

    /// A switch's rails meet at its frog and its long timbers carry both routes, so its diverging route runs at the normal
    /// route's height as far as the timbers go, then eases back to its own (no further than the next junction, which stays where it is). (Twice:
    /// a normal route may itself be another switch's diverging one.)
    private void LevelRoutes()
    {
        const float reach = 30f;
        for (var pass = 0; pass < 2; pass++)
            foreach (var t in Turnouts.Where(t => t.Modelled))
            {
                var (normal, reverse) = (t.Normal, t.Reverse);
                var line = reverse.Line;
                var end = t.TimberEnd;
                var nodes = line.Nodes.Where(n => _junctionNodes.Contains(n.Node) && n.Point != reverse.Point).Select(n => n.Point).ToHashSet();

                // Where the routes lie together, and each point's lift to the normal route's height there.
                var lifts = new List<(int Point, float Lift)>();
                var p = reverse.Point + reverse.Direction;
                for (; p >= 0 && p < line.Count && !nodes.Contains(p); p += reverse.Direction)
                {
                    if (Turnout.Offset(normal, line.X[p], line.Z[p]) is not { } at || at.S > end || at.S >= normal.Length - 0.25f)
                        break;
                    var (_, _, y, _, _, _, _) = normal.At(at.S);
                    lifts.Add((p, y - line.Y[p]));
                }

                if (lifts.Count == 0)
                    continue;

                // A junction before the frog's end holds its height: the lift eases out by there instead of after.
                if (p >= 0 && p < line.Count && nodes.Contains(p))
                {
                    var span = MathF.Max(MathF.Abs(line.S[p] - line.S[reverse.Point]), 1f);
                    foreach (var (q, lift) in lifts)
                        line.Y[q] += lift * Ease(MathF.Abs(line.S[p] - line.S[q]) / span);
                    continue;
                }

                foreach (var (q, lift) in lifts)
                    line.Y[q] += lift;
                var last = lifts[^1].Lift;
                var stop = p;
                while (stop >= 0 && stop < line.Count && !nodes.Contains(stop) && MathF.Abs(line.S[stop] - line.S[lifts[^1].Point]) < reach)
                    stop += reverse.Direction;
                var length = MathF.Max(MathF.Abs(line.S[Math.Clamp(stop, 0, line.Count - 1)] - line.S[lifts[^1].Point]), 1f);
                for (var q = p; q != stop && q >= 0 && q < line.Count; q += reverse.Direction)
                    line.Y[q] += last * Ease(1f - MathF.Abs(line.S[q] - line.S[lifts[^1].Point]) / length);
            }
    }

    /// How far apart in height two rail lines whose ballast overlaps are set, so one's ballast and ties lie cleanly over
    /// the other's instead of fighting it in the depth buffer.
    private const float LayerStep = 0.003f;

    /// Rail lines whose ballast tops overlap anywhere (crossings, junctions, lines drawn over each other) are set a few
    /// millimetres apart: each takes the first layer none of the lines it overlaps has taken, mains first, and is
    /// lowered LayerStep per layer below the top.
    private void Layer()
    {
        const double cell = 4.0;
        var rails = _lines.Where(l => l.Rail && !l.Disused).OrderBy(l => l.Class).ThenBy(l => l.Index).ToList();
        var cells = new Dictionary<(long, long), List<(NetworkLine Line, int Point)>>();
        foreach (var line in rails)
            for (var p = 0; p < line.Count; p++)
            {
                var key = ((long)Math.Floor(line.X[p] / cell), (long)Math.Floor(line.Z[p] / cell));
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = [];
                list.Add((line, p));
            }

        var layers = new Dictionary<NetworkLine, int>();
        foreach (var line in rails)
        {
            var taken = new HashSet<int>();
            for (var p = 0; p < line.Count; p++)
            {
                var (cx, cz) = ((long)Math.Floor(line.X[p] / cell), (long)Math.Floor(line.Z[p] / cell));
                for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (cells.TryGetValue((cx + dx, cz + dz), out var list))
                            foreach (var (other, q) in list)
                            {
                                if (other == line || !layers.TryGetValue(other, out var layer))
                                    continue;
                                var reach = line.BallastHalfTop + other.BallastHalfTop;
                                var (ex, ez) = (other.X[q] - line.X[p], other.Z[q] - line.Z[p]);
                                if (ex * ex + ez * ez < reach * reach)
                                    taken.Add(layer);
                            }
            }

            var mine = 0;
            while (taken.Contains(mine))
                mine++;
            layers[line] = mine;
            if (mine > 0)
                for (var p = 0; p < line.Count; p++)
                    line.Y[p] -= mine * LayerStep;
        }

        RailWerkz.Core.Logging.Log.Info($"Profiles: {layers.Values.Count(l => l > 0)} of {rails.Count} rail lines layered, {(layers.Count > 0 ? layers.Values.Max() + 1 : 0)} layers");
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

    /// Anything in the ground under a rail line shorter than this is taken for an error in the terrain, not a hill.
    private const float RailMedian = 60f;

    /// The steepest a line of each class is built, as rise over run.
    private static float SteepestGrade(NetworkLine line) => (RailClass)line.Class switch
    {
        RailClass.Main => 0.02f,
        RailClass.Branch => 0.025f,
        RailClass.Yard => 0.008f,
        RailClass.Transit => 0.04f,
        _ => 0.03f,
    };

    /// A running median over window metres each side, on the ground (bridges and tunnels left as they are).
    private static void Median(NetworkLine line, float window)
    {
        var y = line.Y;
        var s = line.S;
        var result = (float[])y.Clone();
        var values = new List<float>();
        var lo = 0;
        var hi = 0;
        for (var i = 0; i < y.Length; i++)
        {
            while (hi < y.Length && s[hi] <= s[i] + window)
                hi++;
            while (s[lo] < s[i] - window)
                lo++;
            values.Clear();
            for (var k = lo; k < hi; k++)
                if (OnGround(line, k))
                    values.Add(y[k]);
            if (values.Count < 3 || !OnGround(line, i))
                continue;
            values.Sort();
            result[i] = values[values.Count / 2];
        }

        Array.Copy(result, y, y.Length);
    }

    /// Neither segment beside the point is on a bridge or in a tunnel.
    private static bool OnGround(NetworkLine line, int p) =>
        (p == 0 || line.Structures[p - 1] == Structure.Ground) && (p >= line.Structures.Length || line.Structures[p] == Structure.Ground);

    /// Held to a steepest grade: wherever the profile climbs or falls faster, it's eased into a constant grade, taking
    /// as much from the high side as it gives the low (the average of a pass each way, a few times over).
    private static void LimitGrade(NetworkLine line, float grade)
    {
        var y = line.Y;
        var s = line.S;
        var forward = new float[y.Length];
        var backward = new float[y.Length];
        for (var pass = 0; pass < 4; pass++)
        {
            forward[0] = y[0];
            for (var i = 1; i < y.Length; i++)
            {
                var reach = grade * (s[i] - s[i - 1]);
                forward[i] = Math.Clamp(y[i], forward[i - 1] - reach, forward[i - 1] + reach);
            }

            backward[^1] = y[^1];
            for (var i = y.Length - 2; i >= 0; i--)
            {
                var reach = grade * (s[i + 1] - s[i]);
                backward[i] = Math.Clamp(y[i], backward[i + 1] - reach, backward[i + 1] + reach);
            }

            for (var i = 0; i < y.Length; i++)
                y[i] = (forward[i] + backward[i]) * 0.5f;
        }
    }

    // How far apart tracks can be and still be smoothed together, how quickly that falls off with distance, and how
    // different in height they can be (a flyover's ramp, a line in a cutting) before they're kept apart.
    private const double SurfaceReach = 24.0;
    private const double SurfaceSpread = 10.0;
    private const float SurfaceStep = 0.75f;

    /// Every rail point on the ground, laid out cell by cell (cells reach metres across) in flat arrays, so each
    /// point's neighbours are read straight through memory. Built once and smoothed with as many times as wanted.
    private sealed class RailPoints
    {
        private readonly (NetworkLine Line, int Point)[] _points;
        private readonly double[] _x;
        private readonly double[] _z;
        private readonly int[] _cell;
        private readonly Dictionary<int, (int Start, int End)> _cells = [];
        private readonly int _columns;
        private readonly double _reach;

        public RailPoints(List<NetworkLine> lines, double reach)
        {
            _reach = reach;
            var byCell = new Dictionary<(int, int), List<(NetworkLine, int)>>();
            foreach (var line in lines)
            {
                if (!line.Rail || line.Disused)
                    continue;
                for (var p = 0; p < line.Count; p++)
                {
                    if (!OnGround(line, p))
                        continue;
                    var key = ((int)Math.Floor(line.X[p] / reach), (int)Math.Floor(line.Z[p] / reach));
                    if (!byCell.TryGetValue(key, out var list))
                        byCell[key] = list = [];
                    list.Add((line, p));
                }
            }

            // Cells numbered row by row, so a neighbour cell is a fixed step away.
            var minX = byCell.Count > 0 ? byCell.Keys.Min(k => k.Item1) - 1 : 0;
            var minZ = byCell.Count > 0 ? byCell.Keys.Min(k => k.Item2) - 1 : 0;
            _columns = byCell.Count > 0 ? byCell.Keys.Max(k => k.Item1) - minX + 2 : 1;
            var count = byCell.Values.Sum(l => l.Count);
            _points = new (NetworkLine, int)[count];
            _x = new double[count];
            _z = new double[count];
            _cell = new int[count];
            var k = 0;
            foreach (var ((cx, cz), list) in byCell)
            {
                var id = (cz - minZ) * _columns + (cx - minX);
                _cells[id] = (k, k + list.Count);
                foreach (var (line, p) in list)
                {
                    (_points[k], _x[k], _z[k], _cell[k]) = ((line, p), line.X[p], line.Z[p], id);
                    k++;
                }
            }
        }

        /// One pass of the bilateral filter: each point moves to the weighted average of the points within reach,
        /// weighted by how near (spread) and how alike in height (step).
        public void Smooth(double spread, float step)
        {
            var heights = new float[_points.Length];
            for (var k = 0; k < _points.Length; k++)
                heights[k] = _points[k].Line.Y[_points[k].Point];

            var result = new float[_points.Length];
            var reach2 = _reach * _reach;
            var near2 = 1.0 / (2 * spread * spread);
            var alike2 = 1.0 / (2 * step * step);
            Parallel.For(0, _points.Length, k =>
            {
                var (x, z, h) = (_x[k], _z[k], heights[k]);
                double sum = 0, weights = 0;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (!_cells.TryGetValue(_cell[k] + dz * _columns + dx, out var range))
                            continue;
                        for (var j = range.Start; j < range.End; j++)
                        {
                            var d2 = (_x[j] - x) * (_x[j] - x) + (_z[j] - z) * (_z[j] - z);
                            if (d2 > reach2)
                                continue;
                            // Smooth bell-shaped weights without an exponential: near and alike count most.
                            var dh = heights[j] - h;
                            var near = 1.0 / (1.0 + d2 * near2);
                            var alike = 1.0 / (1.0 + dh * dh * alike2);
                            var w = near * near * alike * alike;
                            sum += heights[j] * w;
                            weights += w;
                        }
                    }
                }

                result[k] = weights > 0 ? (float)(sum / weights) : h;
            });

            for (var k = 0; k < _points.Length; k++)
                _points[k].Line.Y[_points[k].Point] = result[k];
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

    /// Where lines share a node, they meet at exactly one height. The heights are solved over the whole network rather
    /// a junction at a time (in a ladder a line has a junction every few tens of metres, and each one's correction would
    /// undo its neighbours'): each node starts at an average weighted towards lines passing through (they can't bend
    /// locally without a bump) and towards rail; then the nodes along every line are pulled together until no stretch
    /// between two is steeper than that line's grade allows. Each line then eases from one node's height to the next, and
    /// past its last node back to its own profile.
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

        var targets = new Dictionary<long, float>();
        foreach (var (node, list) in uses)
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

            targets[node] = (float)(sum / weights);
        }

        // The stretches between nodes along each line, and the most each may climb.
        var stretches = new List<(long A, long B, float Climb)>();
        var along = new Dictionary<NetworkLine, List<(long Node, int Point)>>();
        foreach (var line in _lines)
        {
            var mine = line.Nodes.Where(n => targets.ContainsKey(n.Node)).OrderBy(n => n.Point).ToList();
            along[line] = mine;
            var grade = line.Rail ? SteepestGrade(line) : 0.12f;
            for (var i = 0; i + 1 < mine.Count; i++)
                stretches.Add((mine[i].Node, mine[i + 1].Node, (line.S[mine[i + 1].Point] - line.S[mine[i].Point]) * grade));
        }

        for (var pass = 0; pass < JunctionPasses; pass++)
        {
            var moved = false;
            foreach (var (a, b, climb) in stretches)
            {
                var difference = targets[a] - targets[b];
                var excess = MathF.Abs(difference) - climb;
                if (excess <= 0.001f)
                    continue;
                var half = MathF.CopySign(excess * 0.5f, difference);
                targets[a] -= half;
                targets[b] += half;
                moved = true;
            }

            if (!moved)
                break;
        }

        Parallel.ForEach(_lines, line =>
        {
            var nodes = along[line];
            if (nodes.Count == 0)
                return;

            var deltas = nodes.Select(n => targets[n.Node] - line.Y[n.Point]).ToArray();
            var reach = line.Rail ? 150f : 40f;
            var (first, last) = (nodes[0].Point, nodes[^1].Point);
            for (var p = first - 1; p >= 0 && line.S[first] - line.S[p] < reach; p--)
                line.Y[p] += deltas[0] * Ease(1f - (line.S[first] - line.S[p]) / reach);
            for (var p = last + 1; p < line.Count && line.S[p] - line.S[last] < reach; p++)
                line.Y[p] += deltas[^1] * Ease(1f - (line.S[p] - line.S[last]) / reach);
            for (var i = 0; i + 1 < nodes.Count; i++)
            {
                var (from, to) = (nodes[i].Point, nodes[i + 1].Point);
                var span = MathF.Max(line.S[to] - line.S[from], 1e-3f);
                for (var p = from + 1; p < to; p++)
                    line.Y[p] += deltas[i] + (deltas[i + 1] - deltas[i]) * Ease((line.S[p] - line.S[from]) / span);
            }

            foreach (var (node, point) in nodes)
                line.Y[point] = targets[node];
        });
    }

    /// How many times the nodes along the lines are pulled together; it stops early once none needs it.
    private const int JunctionPasses = 400;

    private static float Ease(float t) => t * t * (3f - 2f * t);

    private float[] JunctionDistances(NetworkLine line)
    {
        var distances = new float[line.Count];
        Array.Fill(distances, float.MaxValue);
        foreach (var (node, point) in line.Nodes)
            if (_junctionNodes.Contains(node) && !_continuing.ContainsKey((line.Index, point)))
                distances[point] = 0f;

        for (var p = 1; p < line.Count; p++)
            distances[p] = Math.Min(distances[p], distances[p - 1] + line.S[p] - line.S[p - 1]);
        for (var p = line.Count - 2; p >= 0; p--)
            distances[p] = Math.Min(distances[p], distances[p + 1] + line.S[p + 1] - line.S[p]);
        return distances;
    }

    /// How steeply a track eases between one bed's height and another's.
    private const float BedRamp = 0.012f;

    /// Lets each track down to its bed's height. Where tracks of different beds meet they meet at the highest of them, and the lower
    /// eases down from there, so every junction stays one height and no train meets a step.
    private void LetDownBeds()
    {
        var atNode = new Dictionary<long, float>();
        foreach (var line in _lines.Where(l => l.Rail))
        {
            // (Rails in a pavement stand at the height of the line itself, not on a bed.)
            var own = line.Embedded ? 0f : NetworkLine.BallastHeight - line.BedHeight;
            line.Sink = new float[line.Count];
            Array.Fill(line.Sink, own);
            foreach (var (node, _) in line.Nodes)
                atNode[node] = atNode.TryGetValue(node, out var lowest) ? Math.Min(lowest, own) : own;
        }

        Parallel.ForEach(_lines.Where(l => l.Rail), line =>
        {
            foreach (var (node, point) in line.Nodes)
            {
                var there = atNode[node];
                for (var p = 0; p < line.Count; p++)
                    line.Sink[p] = Math.Min(line.Sink[p], there + MathF.Abs(line.S[p] - line.S[point]) * BedRamp);
            }

            for (var p = 0; p < line.Count; p++)
                line.Y[p] -= line.Sink[p];
        });
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
        FindContinuing(seen);
    }

    /// Road ends that just carry on into another road: the same road split where its lanes or a tag change, not a junction.
    private readonly Dictionary<(int Line, int Point), (NetworkLine Line, int Point)> _continuing = [];

    /// The road this end of a road carries straight on into, if that's all that happens there.
    public (NetworkLine Line, int Point)? Continuing(NetworkLine line, int point) =>
        _continuing.TryGetValue((line.Index, point), out var other) ? other : null;

    private void FindContinuing(Dictionary<long, int> touching)
    {
        _continuing.Clear();
        var ends = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => !l.Rail && !l.Mark))
        {
            foreach (var (node, point) in line.Nodes)
            {
                if ((point != 0 && point != line.Count - 1) || touching.GetValueOrDefault(node) != 2)
                    continue;
                if (!ends.TryGetValue(node, out var list))
                    ends[node] = list = [];
                list.Add((line, point));
            }
        }

        foreach (var list in ends.Values.Where(l => l.Count == 2))
        {
            var (a, b) = (list[0], list[1]);
            var (ax, az) = Outward(a.Line, a.Point);
            var (bx, bz) = Outward(b.Line, b.Point);
            // Facing opposite ways, within about 35 degrees of straight on.
            if (ax * bx + az * bz > -0.82)
                continue;
            _continuing[(a.Line.Index, a.Point)] = b;
            _continuing[(b.Line.Index, b.Point)] = a;
        }
    }

    /// The direction out of the line at this end of it, across the ground.
    private static (double X, double Z) Outward(NetworkLine line, int point)
    {
        var inner = point == 0 ? Math.Min(1, line.Count - 1) : Math.Max(point - 1, 0);
        var (x, z) = (line.X[point] - line.X[inner], line.Z[point] - line.Z[inner]);
        var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-6);
        return (x / length, z / length);
    }

    // --- Marks on the ground. ---

    /// Takes the drape of every mark that came out the same as one of the previous lines, for those in tiles that aren't reshaped again.
    public void AdoptDrapes(NetworkLines previous)
    {
        var known = new Dictionary<long, NetworkLine>();
        foreach (var line in previous._lines.Where(l => l.Mark))
            known.TryAdd(line.Key, line);
        foreach (var line in _lines.Where(l => l.Mark))
            if (known.TryGetValue(line.Key, out var same) && same.Drape.Length == line.Drape.Length)
                Array.Copy(same.Drape, line.Drape, line.Drape.Length);
    }

    /// Lays the marks with points in a tile on its finished heights (Samples x Samples, rows north to south).
    public void DrapeMarks(TileId tile, float[] heights, int samples)
    {
        if (!_pointsByTile.TryGetValue(tile, out var points))
            return;

        var gx0 = tile.I * _quads;
        var gz0 = -(tile.J + 1) * _quads;
        foreach (var (l, p) in points)
        {
            var line = _lines[l];
            if (!line.Mark)
                continue;

            var a = Math.Max(p - 1, 0);
            var b = Math.Min(p + 1, line.Count - 1);
            var dx = line.X[b] - line.X[a];
            var dz = line.Z[b] - line.Z[a];
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-5);
            for (var k = 0; k < NetworkLine.MarkPoints; k++)
            {
                var u = line.Width * ((double)k / (NetworkLine.MarkPoints - 1) - 0.5);
                var fx = (float)((line.X[p] - dz / length * u) / _spacing - gx0);
                var fz = (float)((line.Z[p] + dx / length * u) / _spacing - gz0);
                var x0 = Math.Clamp((int)MathF.Floor(fx), 0, samples - 2);
                var z0 = Math.Clamp((int)MathF.Floor(fz), 0, samples - 2);
                var tx = Math.Clamp(fx - x0, 0f, 1f);
                var tz = Math.Clamp(fz - z0, 0f, 1f);
                var top = heights[z0 * samples + x0] + (heights[z0 * samples + x0 + 1] - heights[z0 * samples + x0]) * tx;
                var bottom = heights[(z0 + 1) * samples + x0] + (heights[(z0 + 1) * samples + x0 + 1] - heights[(z0 + 1) * samples + x0]) * tx;
                line.Drape[p * NetworkLine.MarkPoints + k] = top + (bottom - top) * tz;
            }
        }
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

        // How near the nearest formation is at each sample: a line's flat formation only takes ground no nearer
        // line's formation has taken, and its slopes only ground no formation has, so tracks side by side don't
        // flatten the ground between them each to its own height.
        var claimed = new float[heights.Length];
        Array.Fill(claimed, float.MaxValue);

        // Every sample within reach of each grounded stretch of the lines, with its distance from the centreline, the
        // formation height there, and the way to the centreline (in samples).
        void Visit(bool railOnly, Action<int, NetworkLine, int, float, float, float, float, float> visit)
        {
            foreach (var index in list)
            {
                var line = _lines[index];
                if (line.Mark || (railOnly && !line.Rail))
                    continue;
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
                    // The ground the bed stands on, not the track's own height. Under a low bed a little lower, or the ground's
                    // own unevenness would come up through the rock.
                    float Under(int p) => line.SinkOf(p) - MathF.Min(line.SinkOf(p) * 0.3f, 0.06f);
                    var ya = line.Y[a] + Under(a);
                    var yb = line.Y[b] + Under(b);
                    for (var z = z0; z <= z1; z++)
                    {
                        for (var x = x0; x <= x1; x++)
                        {
                            var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                            var ex = ax + dx * t - x;
                            var ez = az + dz * t - z;
                            var d = (float)Math.Sqrt(ex * ex + ez * ez) * _spacing;
                            if (d <= reach)
                                visit(index, line, z * samples + x, d, ya + (yb - ya) * (float)t, (float)ex, (float)ez, reach);
                        }
                    }
                }
            }
        }

        // Each line's formation, and its cut and fill slopes; and the nearest rail line at each sample.
        var near = new (int Line, float D, float Y, float Ex, float Ez)[heights.Length];
        Array.Fill(near, (-1, float.MaxValue, 0f, 0f, 0f));
        Visit(false, (index, line, sample, d, target, ex, ez, reach) =>
        {
            if (line.Rail && d < near[sample].D)
                near[sample] = (index, d, target, ex, ez);
            // (Track's flat ground runs a terrain triangle past its gravel, so a slope rising beside it can't bury the gravel's edge.)
            var run = MathF.Max(0f, d - (line.Rail ? line.Formation + _spacing * 1.5f : line.Formation));
            if (run <= 0f ? d >= claimed[sample] : claimed[sample] < float.MaxValue)
                return;
            if (run <= 0f)
                claimed[sample] = d;
            var h = heights[sample];
            var shaped = Math.Clamp(h, target - run * FillSlope, target + run * CutSlope);
            var fade = Ease(Math.Clamp((reach - d) / 10f, 0f, 1f));
            heights[sample] = h + (shaped - h) * fade;
        });

        // The nearest rail line on the far side of each sample from its nearest one.
        var across = new (float D, float Y)[heights.Length];
        Array.Fill(across, (float.MaxValue, 0f));
        Visit(true, (index, _, sample, d, target, ex, ez, _) =>
        {
            var n = near[sample];
            if (index != n.Line && d < across[sample].D && n.Ex * ex + n.Ez * ez < 0)
                across[sample] = (d, target);
        });

        // Between two rail lines near one another (a yard, tracks side by side, a wye's legs), the ground graded flat
        // from one's formation to the other's, as a yard is: each line's own cut and fill would leave a ridge or a
        // trench between them.
        for (var sample = 0; sample < heights.Length; sample++)
        {
            var (a, b) = (near[sample], across[sample]);
            if (a.Line < 0 || b.D == float.MaxValue || a.D + b.D > YardGap || MathF.Abs(a.Y - b.Y) > (a.D + b.D) * 0.06f)
                continue;
            heights[sample] = (a.Y * b.D + b.Y * a.D) / MathF.Max(a.D + b.D, 1e-3f);
        }

        // Under a road the ground follows the road point by point, a little below it: the shaping above works from every
        // ConformStride-th point, and on a road's changing grade the ground between them can rise through its surface.
        var under = new float[heights.Length];
        Array.Fill(under, float.MaxValue);
        foreach (var index in list)
        {
            var line = _lines[index];
            if (line.Rail || line.Mark)
                continue;
            var r = line.Formation / _spacing;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                if (line.Structures[p] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (line.X[p] / _spacing - gx0, line.Z[p] / _spacing - gz0, line.X[p + 1] / _spacing - gx0, line.Z[p + 1] / _spacing - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * _spacing;
                        var sample = z * samples + x;

                        // (Not where a track's formation lies nearer: that's the track's ground.)
                        if (d > line.Formation || d >= under[sample] || d > claimed[sample] + 1f)
                            continue;
                        under[sample] = d;
                        heights[sample] = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t - RoadClearance;
                    }
            }
        }

        foreach (var index in list)
            if (Array.IndexOf(_lines[index].Structures, Structure.Bridge) >= 0)
                ClearBridges(index, heights, samples, gx0, gz0);
    }

    /// Keeps the ground below a bridge's deck: it may rise from under the deck at the cut slope outward, and the requirement
    /// eases off over the last few metres to each end, where a deck meets the ground anyway. Mapped ends and lidar rarely agree
    /// to the metre, so a deck drawn between its ends can otherwise have the ground come up through it.
    private void ClearBridges(int index, float[] heights, int samples, int gx0, int gz0)
    {
        var line = _lines[index];
        var (half, depth) = line.Rail ? (line.BallastHalfTop + 0.9f, 1.8f) : (line.Width * 0.5f + 0.4f, 1.4f);
        var r = (half + BridgeClearReach) / _spacing;
        for (var from = 0; from < line.Structures.Length; from++)
        {
            if (line.Structures[from] != Structure.Bridge)
                continue;
            var to = from;
            while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                to++;

            var (start, end) = (line.S[from], line.S[to + 1]);
            for (var p = from; p <= to; p++)
            {
                var (ax, az, bx, bz) = (line.X[p] / _spacing - gx0, line.Z[p] / _spacing - gz0, line.X[p + 1] / _spacing - gx0, line.Z[p + 1] / _spacing - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * _spacing;
                        var s = line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t;
                        var ease = Math.Clamp(MathF.Min(s - start, end - s) / BridgeEndEase, 0f, 1f);
                        var y = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t;
                        var limit = y - (depth + BridgeClearance) * ease + MathF.Max(0f, d - half) * CutSlope;
                        if (heights[z * samples + x] > limit)
                            heights[z * samples + x] = limit;
                    }
            }

            from = to;
        }
    }

    private const float BridgeClearance = 0.3f;
    private const float BridgeClearReach = 8f;
    private const float BridgeEndEase = 6f;

    /// How far under a road's formation height the ground beneath it lies.
    private const float RoadClearance = 0.02f;

    /// How far apart two rail lines can be for the ground between them to be graded as one.
    private const float YardGap = 45f;

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
