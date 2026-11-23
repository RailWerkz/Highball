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
    public required int Index { get => _index; init => _index = value; }

    private int _index;

    /// Its place in the set of lines, changed only when a local rebuild moves it to fill a gap (CellStore.Merge).
    internal void MoveTo(int index) => _index = index;

    /// What stands in a place of the set of lines with no line in it: a line not loaded (the lines are a window over some cells,
    /// CellStore.Read), or one gone and not replaced (CellStore.Merge). No points, no nodes, bounds that take in nothing; the one object
    /// stands in every such place, so a place keeps its number whatever's loaded (docs/cell-routes.md, Streaming).
    public static readonly NetworkLine Absent = new()
    {
        Index = -1, Rail = false, Class = 0, X = [], Z = [], Y = [], Ground = [], Key = 0, S = [], Structures = [], Nodes = [],
        MinX = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxZ = double.MinValue, Chain = long.MinValue, Name = long.MinValue,
    };

    /// Whether this is the place holder (Absent), not a line.
    public bool IsAbsent => ReferenceEquals(this, Absent);
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
    /// Where its bed lies just under another's, crossing it at a diamond at the same height (see Diamonds), so the two tops don't fight.
    public List<(float From, float To)> BedDrops { get; } = [];

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
    public float Width { get; set; }
    public int Lanes { get; init; }
    public bool Oneway { get; init; }
    public bool Unpaved { get; init; }
    /// Per segment: which level of an interchange it's on (OpenStreetMap's layer).
    public int[] Layers { get; init; } = [];
    /// At each point, how far the road's surface stands to the right of its line (a fork's extra lanes on one side).
    public float[] Shift { get; set; } = [];

    /// Half the width of the flat ground the line is laid on: the ballast and a walkway, or the road and its shoulders.
    public float Formation { get; set; }

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
    /// How much longer a bed's flank runs than the plain slope (1: a straight cut at BallastSlope), and how far out from its toe
    /// it fades into the ground (none: the bed stops at its toe).
    public const float BedRun = 1f;
    public const float BedFade = 0f;

    public float TrackOffset(int track) => (track - (Tracks - 1) * 0.5f) * TrackSpacing;

    /// Half the ballast top's width.
    public float BallastHalfTop => (Tracks - 1) * 0.5f * TrackSpacing + BallastShoulder;

    /// The chain of ways it was laid from (its nodes, kind and tags, hashed): the same line comes from the same chain.
    public long Chain { get; set; }

    /// The line's name, stable across rebuilds: its chain and where along the chain it starts (lines are a chain's runs over built
    /// tiles). What cells and rebuilds know a line by (docs/cell-routes.md, Identity); its index is only its place in this set.
    public long Name { get; set; }

    /// A road's or mark's points as laid, before the roads were shaped: what a local rebuild shapes the roads near the edit
    /// against, as a full one would (see ShapeRoads).
    public double[] RawX { get; set; } = [];
    public double[] RawZ { get; set; } = [];

    /// Taken as it was from the last rebuild, finished, because the edit was too far off to change it (a local rebuild, see
    /// NetworkLines.Build). The passes leave it be, reading it as the lines round it are worked out; and whatever one does to it
    /// anyway is undone when they're done (Restore), so it comes out exactly as it was.
    public bool Frozen => _origin is not null && !_halo;

    /// Laid again round the lines near an edit, for them to be worked out against, and put back as it was after (see
    /// LocalRebuild.Halo): every pass works on it as on any line.
    public bool Halo => _halo;

    private bool _halo;

    /// Marks a line laid again as part of a local rebuild's halo, to be put back as previous is once the passes are done.
    public void HaloOf(NetworkLine previous) => (_origin, _halo) = (previous, true);

    /// How many segments each of RunBoxes covers.
    public const int RunLength = 32;

    private (double X0, double Z0, double X1, double Z1)[]? _runBoxes;

    /// The bounds of each RunLength segments in turn (run r: points r * RunLength to (r + 1) * RunLength): what's looked for near a
    /// place skips the runs nowhere near it, not every segment of a line whose bounds reach it. Made when first asked for.
    public (double X0, double Z0, double X1, double Z1)[] RunBoxes => _runBoxes ??= MakeRunBoxes();

    private (double X0, double Z0, double X1, double Z1)[] MakeRunBoxes()
    {
        var runs = new (double, double, double, double)[Math.Max(0, (Count - 2) / RunLength + 1)];
        for (var r = 0; r < runs.Length; r++)
        {
            var (x0, z0, x1, z1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            for (var p = r * RunLength; p <= Math.Min((r + 1) * RunLength, Count - 1); p++)
                (x0, z0, x1, z1) = (Math.Min(x0, X[p]), Math.Min(z0, Z[p]), Math.Max(x1, X[p]), Math.Max(z1, Z[p]));
            runs[r] = (x0, z0, x1, z1);
        }

        return runs;
    }

    /// Kept exactly as previous is, heights and all (frozen, see Frozen): a rail line a local rebuild laid lying just as it did,
    /// all of it too far from the edit for its grade to change.
    public void KeepAs(NetworkLine previous)
    {
        _origin = previous;
        Restore();
    }

    /// The last rebuild's line this one's a frozen copy of.
    private NetworkLine? _origin;

    /// The last rebuild's line this one's a frozen copy of, if it is one.
    public NetworkLine? Origin => _origin;

    /// This line, finished, as one of a new set of lines at index: frozen (see Frozen), with arrays of its own (the last lines'
    /// are still shown meanwhile, and mustn't change under them).
    public NetworkLine FrozenAt(int index)
    {
        static T[] Own<T>(T[] array) => array.Length == 0 ? array : (T[])array.Clone();
        var copy = new NetworkLine
        {
            Index = index, Rail = Rail, Class = Class, X = Own(X), Z = Own(Z), Y = Own(Y), Ground = Own(Ground), Key = Key, S = Own(S),
            Structures = Own(Structures), Nodes = Nodes, Junction = Own(Junction), MappedX = MappedX, MappedZ = MappedZ, Alignment = Alignment,
            Tracks = Tracks, Gauge = Gauge, Disused = Disused, Embedded = Embedded, Exact = Exact, Mark = Mark, Drape = Own(Drape),
            Width = Width, Lanes = Lanes, Oneway = Oneway, Unpaved = Unpaved, Layers = Layers, Shift = Own(Shift), Formation = Formation,
            MinX = MinX, MinZ = MinZ, MaxX = MaxX, MaxZ = MaxZ, Bed = Bed, BedHeight = BedHeight, Sink = Own(Sink), Chain = Chain, Name = Name, _origin = this,
            RawX = RawX, RawZ = RawZ,
        };
        copy.TurnoutZones.AddRange(TurnoutZones);
        copy.RailCuts.AddRange(RailCuts);
        copy.TieCuts.AddRange(TieCuts);
        copy.BedDrops.AddRange(BedDrops);
        return copy;
    }

    /// A frozen road taken back to its points as laid, for the roads near an edit to be shaped against (see ShapeRoads); Shaped
    /// puts it back.
    public void Unshaped()
    {
        if (!Frozen || RawX.Length != Count)
            return;
        Array.Copy(RawX, X, Count);
        Array.Copy(RawZ, Z, Count);
        for (var p = 1; p < Count; p++)
            S[p] = S[p - 1] + (float)Math.Sqrt((X[p] - X[p - 1]) * (X[p] - X[p - 1]) + (Z[p] - Z[p - 1]) * (Z[p] - Z[p - 1]));
    }

    public void Shaped()
    {
        if (!Frozen || _origin is not { } o)
            return;
        Array.Copy(o.X, X, Count);
        Array.Copy(o.Z, Z, Count);
        Array.Copy(o.S, S, Count);
    }

    /// A frozen line put back exactly as the line it's a copy of, whatever's been done to it. Returns whether anything had.
    public bool Restore()
    {
        if (_origin is not { } o || o.Count != Count)
            return false;
        var changed = !X.AsSpan().SequenceEqual(o.X) || !Z.AsSpan().SequenceEqual(o.Z) || !Y.AsSpan().SequenceEqual(o.Y) || !S.AsSpan().SequenceEqual(o.S) ||
                      !Structures.AsSpan().SequenceEqual(o.Structures) || Width != o.Width || Formation != o.Formation ||
                      !Shift.AsSpan().SequenceEqual(o.Shift) || !Sink.AsSpan().SequenceEqual(o.Sink) || !Junction.AsSpan().SequenceEqual(o.Junction);
        Array.Copy(o.X, X, X.Length);
        Array.Copy(o.Z, Z, Z.Length);
        Array.Copy(o.Y, Y, Y.Length);
        Array.Copy(o.S, S, S.Length);
        Array.Copy(o.Ground, Ground, Ground.Length);
        Array.Copy(o.Structures, Structures, Structures.Length);
        (Width, Formation) = (o.Width, o.Formation);
        Shift = o.Shift.Length == 0 ? o.Shift : (float[])o.Shift.Clone();
        Sink = o.Sink.Length == 0 ? o.Sink : (float[])o.Sink.Clone();
        Junction = o.Junction.Length == 0 ? o.Junction : (float[])o.Junction.Clone();
        Drape = o.Drape.Length == 0 ? o.Drape : (float[])o.Drape.Clone();
        (MinX, MinZ, MaxX, MaxZ) = (o.MinX, o.MinZ, o.MaxX, o.MaxZ);
        return changed;
    }
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

    /// How much of each sample near a track is its ballast run out over the ground (0 to 255), by tile: from the bed's toe,
    /// where it's all stone, out across the fade to none. Worked out as each tile's ground is shaped (Conform).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TileId, Dictionary<int, byte>> _ballast = new();
    private int _ballastSamples;

    /// How much of sample (sx, sz) of a tile is ballast run out over the ground, 0 to 1.
    public float BallastAt(TileId tile, int sx, int sz) =>
        _ballast.TryGetValue(tile, out var map) && map.TryGetValue(sz * _ballastSamples + sx, out var b) ? b / 255f : 0f;

    /// Takes on the ballast worked out by the lines this replaces, for the tiles whose ground isn't being shaped again.
    public void InheritBallast(NetworkLines previous)
    {
        _ballastSamples = previous._ballastSamples;
        foreach (var (tile, map) in previous._ballast)
            _ballast.TryAdd(tile, map);
    }
    private readonly Dictionary<TileId, List<(int Line, int Point)>> _pointsByTile = [];
    private readonly float _spacing;
    /// The sample spacing the ground's being shaped at: the tiles' own, or a finer grid's while ConformFine shapes one (this thread only).
    [ThreadStatic] private static float t_fineSpacing;
    private float Sp => t_fineSpacing > 0f ? t_fineSpacing : _spacing;
    private readonly int _quads;

    public IReadOnlyList<NetworkLine> Lines => _lines;

    /// The cells these lines are complete in, if they're a window over some of the route's (CellStore.Read): every line with a point in
    /// them or reaching their ground is loaded, and everything anchored in them; elsewhere a line may be loaded (one reaching in from
    /// them) or not (its place holds NetworkLine.Absent). Null: the whole route. See docs/cell-routes.md, Streaming.
    internal CellWindow? Window { get; set; }

    /// The indexes made when first asked for (the detail regions, the rail cells, the roads' ends, the lines by chain), made now: for a
    /// window read in the background, so the main thread taking it in doesn't make them.
    internal void Warm()
    {
        _ = BridgeEndsOf(_lines.Count > 0 ? _lines[0] : NetworkLine.Absent);
        WallsBeside();
        _ = EndShapes;
        HeightsAt(0);
        _ = DetailRegions;
        UnderTrack(0, 0, 3.0);
        _roadEnds ??= FindRoadEnds();
        LinesOfChain(0);
    }

    /// Whether these lines are all there is of the route there (not a window, or a cell of its).
    internal bool Complete(TileId cell) => Window is not { } window || window.Complete.Contains(cell);

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
            line.BedDrops.Clear();
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

    /// The bridges as structures (BridgePlanner.Find), found once for these lines: in the background as track's laid again, so
    /// the main thread needn't, and before the lines are compared with the last ones (finding them levels the guides' bridged
    /// stretches to the decks, so lines found and not would always look changed). Null until found.
    public List<BridgeGroup>? Bridges { get; set; }

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

    /// The points (line, point) that fall in a tile.
    internal IReadOnlyList<(int Line, int Point)>? PointsIn(TileId tile) => _pointsByTile.GetValueOrDefault(tile);

    private NetworkLines(float spacing, int quads)
    {
        _spacing = spacing;
        _quads = quads;
    }

    // --- Building the lines. ---

    private sealed class Chain
    {
        /// Its nodes, kind and tags, hashed (NetworkLine.Chain).
        public long Id;
        public required bool Rail;
        public required int Class;
        public required Dictionary<string, string> Tags;
        public readonly List<long> Nodes = [];
        public readonly List<GeoPoint> Points = [];
        /// Per segment.
        public readonly List<Structure> Structures = [];
        /// Per segment: its OpenStreetMap layer (which level of an interchange it's on), and the same once resampled.
        public readonly List<int> Layers = [];
        public List<int> ResampledLayers = [];
    }

    /// The lines for the network's ways on the built tiles: the track as saved with the route, if it was solved from these
    /// same ways (solved and saved if not), with the lines shaped by hand laid over it. Heights come later, from SampleGround
    /// and FinishProfiles.
    /// local: a local rebuild after an edit to track only (see LocalRebuild): roads and marks far enough from it are taken
    /// finished from the last lines rather than laid again. It's done in full if anything else has changed since.
    public static NetworkLines Build(IEnumerable<NetworkWay> network, RouteInfo route, HashSet<TileId> built, IReadOnlyDictionary<long, Alignment> shapes,
        LocalRebuild? local = null)
    {
        var grid = route.Grid!;
        var lines = new NetworkLines((float)grid.Spacing, grid.Samples - 1);
        var chosen = SwitchSizes.Load(route);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var curves = SwitchCurves.Load(route);
        network = network as IReadOnlyCollection<NetworkWay> ?? network.ToList();
        (lines.Ways, lines.Shapes, lines.BuiltTiles, lines.Sizes, lines.Curves) = (network.ToDictionary(w => w.Id), shapes, built, chosen, curves);
        if (local is not null && !local.Previous.SameInputs(shapes, built, chosen, curves))
        {
            var p = local.Previous;
            RailWerkz.Core.Logging.Log.Info("Local rebuild: not this time (" + string.Join(", ", new[]
            {
                p.Shapes is { } s && s.Count == shapes.Count && shapes.All(x => s.TryGetValue(x.Key, out var a) && ReferenceEquals(a, x.Value)) ? null : "track shaped by hand changed",
                p.BuiltTiles is { } t && t.SetEquals(built) ? null : "built tiles changed",
                p.Sizes is { } z && z.Count == chosen.Count && chosen.All(x => z.TryGetValue(x.Key, out var v) && v == x.Value) ? null : "switch sizes changed",
                p.Curves is { } c && c.Count == curves.Count && curves.All(x => c.TryGetValue(x.Key, out var v) && v == x.Value) ? null : "switch curves changed",
            }.OfType<string>()) + "), laying it all");
            local = null;
        }
        if (local is not null)
        {
            Solve(lines, network, route, built, chosen, curves, local);
            lines.MarkHalo(local);
            var context = local.Dirty is null ? 0 : lines.AddContext(local);
            RailWerkz.Core.Logging.Log.Info($"Track: {lines._lines.Count} lines laid again near the edit in {watch.ElapsedMilliseconds} ms ({lines._lines.Count(l => l.Frozen)} kept as they were" +
                                            (local.Dirty is { } d ? $", {context} of them round it; {d.Count} cells laid again, {local.Context!.Count} held round them)" : ")"));
        }
        else
        {
            // (Always solved: the cells keep the lines as built, so a full build is only ever an open with no cells to read, or a
            // rebuild that can't be local; the solved track, Track.bin.gz, was only good until the network's next edit.)
            Solve(lines, network, route, built, chosen, curves, null);
            RailWerkz.Core.Logging.Log.Info($"Track: {lines._lines.Count} lines solved in {watch.ElapsedMilliseconds} ms");
        }

        lines.Lap("ShapeRoads");
        lines.ShapeRoads();
        lines.Lap("LayShapes");
        lines.LayShapes(shapes);
        lines.Lap("EaseJoins");
        lines.EaseJoins();
        lines.Lap("FreezeFarRail");
        // (Rail's taken as it was only once it's finished laying: a line eased at a join, or shaped by hand, then lies as it did.)
        if (local is not null)
            RailWerkz.Core.Logging.Log.Info($"Track: {lines.FreezeFarRail(local)} rail lines far from the edit kept as they were, heights and all");
        lines.Lap("IndexLines");
        lines.IndexLines(built);
        lines.Lap(null);
        return lines;
    }

    /// Joins, smooths and cuts the network's ways to the built tiles, and lays out its track and switches.
    private static void Solve(NetworkLines lines, IEnumerable<NetworkWay> network, RouteInfo route, HashSet<TileId> built, IReadOnlyDictionary<long, int> chosen, IReadOnlyDictionary<long, float> curves,
        LocalRebuild? local)
    {
        var projection = route.Projection!;
        var tileSize = route.Grid!.TileSize;
        var solveWatch = System.Diagnostics.Stopwatch.StartNew();
        var solveStages = new List<string>();
        void SolveStage(string name)
        {
            solveStages.Add($"{name} {solveWatch.ElapsedMilliseconds}");
            solveWatch.Restart();
        }


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
        SolveStage("fans");
        var crossovers = CrossoverFitting.Apply(network, route, chosen);
        SolveStage("crossovers");

        // (Marks don't join anything: each keeps to its own mapped line, at the height of the ground under it.)
        foreach (var chain in Chains(marks))
        {
            if (Outside(chain, local, projection) || lines.Keep(chain, local))
                continue;
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

        // Where each rail chain ends, so a line laid by hand can carry on into the track it meets there.
        var endsAt = new Dictionary<long, List<(Chain Chain, bool AtStart)>>();
        foreach (var chain in chains)
            if (chain.Rail && chain.Nodes.Count >= 2)
                foreach (var (node, atStart) in (ReadOnlySpan<(long, bool)>)[(chain.Nodes[0], true), (chain.Nodes[^1], false)])
                {
                    if (!endsAt.TryGetValue(node, out var list))
                        endsAt[node] = list = [];
                    list.Add((chain, atStart));
                }

        // Which way the track runs on past an end of a line, away from it: the chain ending there that carries straight on
        // (within about 25 degrees), if one does.
        (double X, double Z)? Beyond(Chain self, long node, (double X, double Z) at, (double X, double Z) inward)
        {
            (double X, double Z)? best = null;
            var straightest = -0.9;
            foreach (var (other, atStart) in endsAt.GetValueOrDefault(node) ?? [])
            {
                if (other == self)
                    continue;
                var next = projection.Forward(atStart ? other.Points[1] : other.Points[^2]);
                var (dx, dz) = (next.East - at.X, -next.North - at.Z);
                var length = Math.Sqrt(dx * dx + dz * dz);
                if (length < 1e-6)
                    continue;
                var dot = (dx * inward.X + dz * inward.Z) / length;
                if (dot < straightest)
                    (straightest, best) = (dot, (dx / length, dz / length));
            }

            return best;
        }

        SolveStage("marks and chains");
        var laid = new List<Laid>();
        foreach (var chain in chains)
        {
            // (Rail's laid however far it is: every track meeting a chain through the cells is handed, so each one's switches and
            // crossovers are laid out with all their tracks. What's laid of it outside the cells is put back as it was: AddContext.)
            if ((!chain.Rail && Outside(chain, local, projection)) || lines.Keep(chain, local))
                continue;
            var (xs, zs, structures, nodes) = Resample(chain, projection);
            // (Yard tracks stand a few metres apart, closer than this could move them: they keep the line they were mapped on.)
            if (chain.Rail && chain.Class != (int)RailClass.Yard && !chain.Tags.ContainsKey(RouteNetwork.ExactTag))
                EvenOut(xs, zs, nodes, pinned);
            // Track laid by hand goes through its control points as they are, but without the spline's wobble between them.
            else if (chain.Rail && chain.Tags.ContainsKey(RouteNetwork.ExactTag) && !chain.Tags.ContainsKey(AlignmentTrack.Tag) && xs.Count >= 2)
            {
                static (double X, double Z) Unit(double x, double z)
                {
                    var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-9);
                    return (x / length, z / length);
                }

                var inStart = Unit(xs[1] - xs[0], zs[1] - zs[0]);
                var inEnd = Unit(xs[^2] - xs[^1], zs[^2] - zs[^1]);
                ExactFairing.Fair(xs, zs, nodes.Select(n => n.Item2),
                    Beyond(chain, chain.Nodes[0], (xs[0], zs[0]), inStart), Beyond(chain, chain.Nodes[^1], (xs[^1], zs[^1]), inEnd));
            }

            laid.Add(new Laid(chain, xs, zs, structures, nodes));
        }

        SolveStage("resample and even out");
        foreach (var (chain, xs, zs, structures, nodes, _) in JoinThrough(laid, seen))
        {
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

        SolveStage("join and cut");
        // Switches laid out to a real design before anything is built on the track.
        foreach (var line in lines._lines.Where(l => l.Rail))
            (line.MappedX, line.MappedZ) = ((double[])line.X.Clone(), (double[])line.Z.Clone());

        // (Two tracks crossing each keep their own line through it: neither is bent to carry the other.)
        SolveStage("mapped copies");
        var crossings = TrackAlignment.Apply(lines._lines, pinned);
        SolveStage("alignment");
        var joined = pinned.Except(crossings).ToHashSet();
        TrackFairing.Prepare(lines._lines, joined);
        var held = new Dictionary<NetworkLine, List<(float From, float To)>>();
        SolveStage("fairing prepared");
        lines.Fitted = TurnoutFitting.Fit(lines._lines, chosen, lines.SwitchOptions, crossovers.Forced, held, curves);
        SolveStage("turnouts");
        TrackFairing.Fair(lines._lines, held, joined);
        SolveStage("fairing");

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
        SolveStage("sampled");
        RailWerkz.Core.Logging.Log.Info($"Track solve, ms: {string.Join(", ", solveStages)}");
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
        foreach (var line in _lines.Where(l => !l.Rail).OrderBy(l => l.Name))
        {
            // A scrap of road come to nothing (SnapRoadsToPads) meets nothing.
            if (line.Count >= 2 && line.S[line.Count - 1] - line.S[0] < MinRoadLength)
                continue;
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

    private const double RailCell = 25.0;
    private Dictionary<(int X, int Z), List<(NetworkLine Line, int Segment)>>? _railCells;

    /// Whether another track carries on from this place, so that a line ending here isn't the end of the track.
    public bool TrackContinues(NetworkLine line, double x, double z)
    {
        UnderTrack(x, z, 3.0);
        if (!_railCells!.TryGetValue(((int)Math.Floor(x / RailCell), (int)Math.Floor(z / RailCell)), out var near))
            return false;
        foreach (var (other, s) in near)
        {
            if (ReferenceEquals(other, line))
                continue;
            var (dx, dz) = (other.X[s + 1] - other.X[s], other.Z[s + 1] - other.Z[s]);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
            var along = ((x - other.X[s]) * dx + (z - other.Z[s]) * dz) / length;
            var aside = Math.Abs((x - other.X[s]) * dz - (z - other.Z[s]) * dx) / length;
            if (aside < 1.0 && along > -1.0 && along < length + 1.0)
                return true;
        }

        return false;
    }

    /// Whether built track runs through this place, within so many metres to either side, for a mapped track's mark to give way to it.
    /// Past the track's end it's clear again: it doesn't reach round the end. Safe from the workers building chunks.
    public bool UnderTrack(double x, double z, double within)
    {
        var cells = Volatile.Read(ref _railCells);
        if (cells is null)
        {
            cells = [];
            foreach (var line in _lines.Where(l => l.Rail))
                for (var s = 0; s + 1 < line.Count; s++)
                {
                    var (x0, x1) = (Math.Min(line.X[s], line.X[s + 1]) - within, Math.Max(line.X[s], line.X[s + 1]) + within);
                    var (z0, z1) = (Math.Min(line.Z[s], line.Z[s + 1]) - within, Math.Max(line.Z[s], line.Z[s + 1]) + within);
                    for (var j = (int)Math.Floor(z0 / RailCell); j <= (int)Math.Floor(z1 / RailCell); j++)
                        for (var i = (int)Math.Floor(x0 / RailCell); i <= (int)Math.Floor(x1 / RailCell); i++)
                        {
                            if (!cells.TryGetValue((i, j), out var list))
                                cells[(i, j)] = list = [];
                            list.Add((line, s));
                        }
                }

            Volatile.Write(ref _railCells, cells);
        }

        if (!cells.TryGetValue(((int)Math.Floor(x / RailCell), (int)Math.Floor(z / RailCell)), out var near))
            return false;
        foreach (var (line, s) in near)
        {
            var (dx, dz) = (line.X[s + 1] - line.X[s], line.Z[s + 1] - line.Z[s]);
            var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
            var along = ((x - line.X[s]) * dx + (z - line.Z[s]) * dz) / length;
            var aside = Math.Abs((x - line.X[s]) * dz - (z - line.Z[s]) * dx) / length;
            if (aside < within && along > -0.5 && along < length + 0.5)
                return true;
        }

        return false;
    }

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

    /// Classes count up from the biggest, so lower numbers go later; the name keeps the order the same in every tile, whatever
    /// place the line has in the set (a rebuild of some cells moves lines' places, not their order).
    private static (int, long) Priority(NetworkLine line) => ((line.Rail ? 100 : 0) + 10 - line.Class, line.Name);

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
                    // (Track laid by straights and curves is each way its own line: its corners and their curves are its own.)
                    if (next == edge || done.Contains(next.Id) || ShapeKey(next) != key || (edge.Rail && DoublesBack(edge, next, node)) ||
                        edge.Tags.ContainsKey(AlignmentTrack.Tag) || next.Tags.ContainsKey(AlignmentTrack.Tag))
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
                var layer = int.TryParse(way.Tags.GetValueOrDefault("layer", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? Math.Clamp(l, -5, 9) : structure == Structure.Bridge ? 1 : structure == Structure.Tunnel ? -1 : 0;
                for (var n = 0; n < way.Nodes.Length; n++)
                {
                    var k = reversed ? way.Nodes.Length - 1 - n : n;
                    if (n == 0 && chain.Nodes.Count > 0)
                        continue; // the shared node, already added by the previous way
                    if (chain.Nodes.Count > 0)
                    {
                        chain.Structures.Add(structure);
                        chain.Layers.Add(layer);
                    }
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

    private static string ShapeKey(NetworkWay way) => ShapeKey(way.Rail, way.Class, way.Tags);

    private static string ShapeKey(bool rail, int railOrRoadClass, Dictionary<string, string> tags)
    {
        string Tag(string name) => tags.GetValueOrDefault(name, "");
        return rail
            ? $"R{railOrRoadClass}|{Tag("railway")}|{Tag("tracks")}|{Tag("gauge")}|{Tag(RouteNetwork.ExactTag)}|{Tag(RouteNetwork.BedTag)}|{Tag(RouteNetwork.BedHeightTag)}"
            : $"H{railOrRoadClass}|{Tag("lanes")}|{Tag("oneway")}|{Tag("surface")}|{Tag("highway").Replace("_link", "")}";
    }

    /// A chain as laid out, before it's cut to the built tiles.
    /// Aligned: has track laid by straights and curves in it (a joined line loses the tag, so it's carried here).
    private readonly record struct Laid(Chain Chain, List<double> X, List<double> Z, List<Structure> Structures, List<(long Node, int Point)> Nodes, bool Aligned = false)
    {
        public bool HasAlignment => Aligned || Chain.Tags.ContainsKey(AlignmentTrack.Tag);
    }

    /// The most two pieces of track may turn where they meet and still be laid as one line.
    private const double JoinThroughDegrees = 0.5;
    /// How far short of a piece's end a switch is looked for, whose frog might lie on past it.
    private const double SwitchRunsOnReach = 60.0;

    /// Track laid by straights and curves is each way its own chain (its corners and curves are built exactly), but where a
    /// piece of it simply carries straight on into the next piece of track (the two end on the same node, nothing else
    /// meets there), it's one track: the two are laid as one line. Otherwise a switch put in a few metres short of where
    /// such a piece starts would find its leg ending there, before the routes have parted, and couldn't be laid out.
    /// Joined as laid, so each piece keeps the exact shape it was built to.
    private static List<Laid> JoinThrough(List<Laid> laid, Dictionary<long, int> seen)
    {
        var result = new List<Laid>(laid);
        while (true)
        {
            var ends = new Dictionary<long, List<(int Index, bool AtStart)>>();
            for (var i = 0; i < result.Count; i++)
            {
                var l = result[i];
                if (!l.Chain.Rail || l.X.Count < 2 || l.Nodes.Count < 2 || l.Nodes[0].Point != 0 || l.Nodes[^1].Point != l.X.Count - 1)
                    continue;
                foreach (var (node, atStart) in (ReadOnlySpan<(long, bool)>)[(l.Nodes[0].Node, true), (l.Nodes[^1].Node, false)])
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add((i, atStart));
                }
            }

            // Whether a switch on this piece, a little way short of its end, still has its two routes within a rail spacing of
            // each other at the end: its frog lies on past it, so the track it runs on into has to be the same line.
            bool SwitchRunsOn(int index, bool atStart)
            {
                var l = result[index];
                var (ex, ez) = atStart ? (l.X[0], l.Z[0]) : (l.X[^1], l.Z[^1]);
                foreach (var (node, point) in l.Nodes)
                {
                    if (seen.GetValueOrDefault(node) < 2 || point == (atStart ? 0 : l.X.Count - 1))
                        continue;
                    var along = 0.0;
                    for (var p = Math.Min(point, atStart ? 0 : l.X.Count - 1); p < Math.Max(point, atStart ? 0 : l.X.Count - 1); p++)
                        along += Math.Sqrt((l.X[p + 1] - l.X[p]) * (l.X[p + 1] - l.X[p]) + (l.Z[p + 1] - l.Z[p]) * (l.Z[p + 1] - l.Z[p]));
                    if (along > SwitchRunsOnReach)
                        continue;
                    for (var k = 0; k < result.Count; k++)
                    {
                        var other = result[k];
                        if (k == index || !other.Chain.Rail || !other.Nodes.Any(n => n.Node == node))
                            continue;
                        for (var p = 0; p + 1 < other.X.Count; p++)
                        {
                            var (ax, az, dx, dz) = (other.X[p], other.Z[p], other.X[p + 1] - other.X[p], other.Z[p + 1] - other.Z[p]);
                            var length2 = dx * dx + dz * dz;
                            var t = length2 > 0 ? Math.Clamp(((ex - ax) * dx + (ez - az) * dz) / length2, 0, 1) : 0;
                            var (px, pz) = (ax + dx * t - ex, az + dz * t - ez);
                            if (px * px + pz * pz < 4.0 * Turnout.RailOffset * Turnout.RailOffset)
                                return true;
                        }
                    }
                }

                return false;
            }

            (int A, int B, long Node)? pick = null;
            foreach (var (node, list) in ends)
            {
                if (list.Count != 2 || list[0].Index == list[1].Index || seen.GetValueOrDefault(node) != 2)
                    continue;
                var (a, b) = (result[list[0].Index], result[list[1].Index]);
                if (!a.HasAlignment && !b.HasAlignment)
                    continue; // (any other two would have been one chain already, if they're one track)
                if (ShapeKey(true, a.Chain.Class, a.Chain.Tags) != ShapeKey(true, b.Chain.Class, b.Chain.Tags))
                    continue;

                // Each one's way out of the node, along its end segment: one track carries straight on if they're opposite.
                (double X, double Z) Away(Laid l, bool atStart)
                {
                    var (p, q) = atStart ? (0, 1) : (l.X.Count - 1, l.X.Count - 2);
                    var (dx, dz) = (l.X[q] - l.X[p], l.Z[q] - l.Z[p]);
                    var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
                    return (dx / length, dz / length);
                }

                var (u, v) = (Away(a, list[0].AtStart), Away(b, list[1].AtStart));
                if (-(u.X * v.X + u.Z * v.Z) < Math.Cos(JoinThroughDegrees * Math.PI / 180.0))
                    continue;
                if (!SwitchRunsOn(list[0].Index, list[0].AtStart) && !SwitchRunsOn(list[1].Index, list[1].AtStart))
                    continue;
                pick = (list[0].Index, list[1].Index, node);
                break;
            }

            if (pick is not { } join)
                return result;

            // a runs into the node, b out of it.
            var first = result[join.A];
            var second = result[join.B];
            if (first.Nodes[0].Node == join.Node)
                first = Reversed(first);
            if (second.Nodes[^1].Node == join.Node)
                second = Reversed(second);

            var offset = first.X.Count - 1;
            var tags = new Dictionary<string, string>(second.Chain.Tags.ContainsKey(AlignmentTrack.Tag) ? first.Chain.Tags : second.Chain.Tags);
            tags.Remove(AlignmentTrack.Tag);
            var chain = new Chain { Rail = true, Class = first.Chain.Class, Tags = tags };
            var id = new StableHash();
            id.Add(first.Chain.Id);
            id.Add(second.Chain.Id);
            chain.Id = (long)id.Value;
            chain.ResampledLayers = [.. first.Chain.ResampledLayers, .. second.Chain.ResampledLayers];
            var joined = new Laid(chain, [.. first.X, .. second.X.Skip(1)], [.. first.Z, .. second.Z.Skip(1)], [.. first.Structures, .. second.Structures],
                [.. first.Nodes, .. second.Nodes.Skip(1).Select(n => (n.Node, n.Point + offset))], true);

            result[Math.Min(join.A, join.B)] = joined;
            result.RemoveAt(Math.Max(join.A, join.B));
        }

        static Laid Reversed(Laid l)
        {
            var last = l.X.Count - 1;
            var chain = new Chain { Rail = l.Chain.Rail, Class = l.Chain.Class, Tags = l.Chain.Tags, Id = l.Chain.Id };
            chain.ResampledLayers = Enumerable.Reverse(l.Chain.ResampledLayers).ToList();
            return new Laid(chain, Enumerable.Reverse(l.X).ToList(), Enumerable.Reverse(l.Z).ToList(), Enumerable.Reverse(l.Structures).ToList(),
                Enumerable.Reverse(l.Nodes).Select(n => (n.Node, last - n.Point)).ToList(), l.Aligned);
        }
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
        var layers = new List<int>();
        for (var i = 0; i < chain.Points.Count; i++)
        {
            var p = projection.Forward(chain.Points[i]);
            // Drop repeated points; they'd give the spline zero-length knots.
            if (px.Count > 0 && Math.Abs(px[^1] - p.East) + Math.Abs(pz[^1] + p.North) < 0.05)
                continue;
            if (px.Count > 0)
            {
                structures.Add(chain.Structures[i - 1]);
                layers.Add(chain.Layers.Count >= i ? chain.Layers[i - 1] : 0);
            }
            px.Add(p.East);
            pz.Add(-p.North);
            nodeIds.Add(chain.Nodes[i]);
        }

        var xs = new List<double>();
        var zs = new List<double>();
        var segs = new List<Structure>();
        var segLayers = new List<int>();
        chain.ResampledLayers = segLayers;
        var nodes = new List<(long, int)>();
        var n = px.Count;
        if (n < 2)
            return (xs, zs, segs, nodes);

        // Track laid by straights and curves is built from its corners exactly, not splined through them.
        if (chain.Rail && n == chain.Points.Count && AlignmentTrack.Decode(chain.Tags.GetValueOrDefault(AlignmentTrack.Tag)) is { } curves && curves.Count == n - 2)
        {
            var corners = Enumerable.Range(1, n - 2).Select(i => new Corner(px[i], pz[i], curves[i - 1].Radius, curves[i - 1].Easing)).ToList();
            if (AlignmentTrack.Build((px[0], pz[0]), corners, (px[^1], pz[^1]), PointSpacing) is { } built)
            {
                foreach (var (x, z) in built.Points)
                {
                    xs.Add(x);
                    zs.Add(z);
                }

                for (var i = 0; i + 1 < built.Points.Count; i++)
                {
                    var leg = Math.Min(built.Legs[i + 1], structures.Count - 1);
                    segs.Add(structures[leg]);
                    segLayers.Add(layers[leg]);
                }

                nodes.Add((nodeIds[0], 0));
                for (var c = 0; c < n - 2; c++)
                    nodes.Add((nodeIds[c + 1], built.Middles[c]));
                nodes.Add((nodeIds[^1], xs.Count - 1));
                return (xs, zs, segs, nodes);
            }
        }

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
                segLayers.Add(layers[i]);
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
                // (The ground's flat out past the whole of the bed's fade, which lies on it.)
                Formation = embedded ? 2f : halfTop + MathF.Max(0.35f, bedHeight * NetworkLine.BallastSlope * NetworkLine.BedRun) + NetworkLine.BedFade + 0.3f,
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
                Layers = chain.ResampledLayers.Count >= to - 1 ? chain.ResampledLayers.GetRange(from, count - 1).ToArray() : new int[count - 1],
                Unpaved = Tag("highway") == "track" || surface is "unpaved" or "gravel" or "dirt" or "ground" or "compacted" or "fine_gravel" or "grass" or "sand" or "earth",
                Formation = width * 0.5f + 1.5f,
            };
        }

        line.MinX = x.Min();
        line.MaxX = x.Max();
        line.MinZ = z.Min();
        line.MaxZ = z.Max();
        line.Chain = chain.Id;
        var name = new StableHash();
        name.Add(chain.Id);
        name.Add(from);
        line.Name = (long)name.Value;
        if (!line.Rail)
            (line.RawX, line.RawZ) = ((double[])x.Clone(), (double[])z.Clone());
        _lines.Add(line);
    }

    // --- Local rebuilds. ---

    /// What these lines were laid from, to tell what an edit changed: the ways (by id), the shapes laid by hand, the built tiles,
    /// the switch sizes and curves picked. Null for lines read as saved.
    internal Dictionary<long, NetworkWay>? Ways;
    private IReadOnlyDictionary<long, Alignment>? Shapes;
    private HashSet<TileId>? BuiltTiles;
    private IReadOnlyDictionary<long, int>? Sizes;
    private IReadOnlyDictionary<long, float>? Curves;

    /// Every chain these lines were laid from (whether or not it gave a line on the built tiles), and the lines each gave.
    private readonly HashSet<long> _seenChains = [];
    private Dictionary<long, List<NetworkLine>>? _byChain;

    /// Whether these lines were laid from the same shapes, tiles, switch sizes and curves.
    private bool SameInputs(IReadOnlyDictionary<long, Alignment> shapes, HashSet<TileId> built, IReadOnlyDictionary<long, int> sizes, IReadOnlyDictionary<long, float> curves) =>
        Shapes is { } was && was.Count == shapes.Count && shapes.All(s => was.TryGetValue(s.Key, out var a) && ReferenceEquals(a, s.Value)) &&
        BuiltTiles is { } tiles && tiles.SetEquals(built) &&
        Sizes is { } sz && sz.Count == sizes.Count && sizes.All(s => sz.TryGetValue(s.Key, out var v) && v == s.Value) &&
        Curves is { } cv && cv.Count == curves.Count && curves.All(c => cv.TryGetValue(c.Key, out var v) && v == c.Value);

    /// The chains laid from (see _seenChains) that gave a line near any of these places (world metres, within reach), for a
    /// local rebuild to lay again.
    internal HashSet<long> ChainsNear(IReadOnlyList<(double X, double Z)> places, double reach, IEnumerable<NetworkLine>? among = null)
    {
        var cells = new HashSet<(int, int)>();
        foreach (var (x, z) in places)
            foreach (var cell in new GridCells(x - reach, z - reach, x + reach, z + reach, LocalCell))
                cells.Add(cell);
        var near = new HashSet<long>();
        foreach (var line in among ?? _lines)
        {
            if (line.Rail || near.Contains(line.Chain))
                continue;
            for (var p = 0; p < line.Count; p++)
                if (cells.Contains(((int)Math.Floor(line.X[p] / LocalCell), (int)Math.Floor(line.Z[p] / LocalCell))))
                {
                    near.Add(line.Chain);
                    break;
                }
        }

        return near;
    }

    /// Every frozen line put back exactly as it was (see NetworkLine.Restore), once the passes are done. Returns how many a pass
    /// had changed (each such is a pass that should leave frozen lines be).
    public int RestoreFrozen()
    {
        var touched = 0;
        foreach (var line in _lines)
            if (line.Restore() && line.Frozen)
                touched++;
        return touched;
    }

    /// The chains meeting (sharing a node with) these chains' lines, but not them: a local rebuild's halo (LocalRebuild.Halo).
    internal HashSet<long> ChainsMeeting(HashSet<long> chains, IReadOnlyCollection<NetworkLine>? among = null)
    {
        among ??= _lines;
        var nodes = among.Where(l => chains.Contains(l.Chain) && !l.Rail).SelectMany(l => l.Nodes.Select(n => n.Node)).ToHashSet();
        var meeting = new HashSet<long>();
        foreach (var line in among)
            if (!line.Rail && !chains.Contains(line.Chain) && line.Nodes.Any(n => nodes.Contains(n.Node)))
                meeting.Add(line.Chain);
        return meeting;
    }

    /// Once a local rebuild's lines are made: each one laid again in its halo told which of the last lines to be put back as.
    internal void MarkHalo(LocalRebuild local)
    {
        var before = local.Seen.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var line in _lines)
            if (!line.Rail && local.Halo.Contains(line.Chain) && before.TryGetValue(line.Key, out var was) && was.Count == line.Count)
                line.HaloOf(was);
    }

    /// The lines laid from a chain (a local rebuild's, of the lines it's rebuilding from).
    internal IReadOnlyList<NetworkLine> LinesOfChain(long chain)
    {
        var byChain = Volatile.Read(ref _byChain);
        if (byChain is null)
        {
            byChain = _lines.Where(l => !l.IsAbsent).GroupBy(l => l.Chain).ToDictionary(g => g.Key, g => g.ToList());
            Volatile.Write(ref _byChain, byChain);
        }

        return byChain.GetValueOrDefault(chain) ?? [];
    }

    /// Whether a point is within RailOuter of an edit (EditCells), to a cell.
    internal static bool NearEdit(Dictionary<(int, int), double> cells, double x, double z) =>
        cells.TryGetValue(((int)Math.Floor(x / HoldCell), (int)Math.Floor(z / HoldCell)), out var d) && d <= RailOuter + HoldCell;

    /// How far from where an edit changed track a rail line's heights can change, metres: within RailInner they're worked out as
    /// any rebuild would, past RailOuter they're kept exactly as they were, and between the two they're eased from one to the other.
    /// (A grade's worked out along the whole of a line, and smoothed with the track beside it: without this, laying a siding moved
    /// heights by millimetres along every line it reached, and the ground was shaped again under all of them.)
    private const double RailInner = 600.0, RailOuter = 1000.0;

    /// The size of the cells HoldFarRail looks up how far the edit is in, metres.
    private const double HoldCell = 100.0;

    /// How far from the edit's places each cell within RailOuter of one is (to its centre, so within HoldCell of the truth).
    internal static Dictionary<(int, int), double> EditCells(List<(double X, double Z)> places)
    {
        var cells = new Dictionary<(int, int), double>();
        foreach (var (x, z) in places)
            foreach (var c in new GridCells(x - RailOuter - HoldCell, z - RailOuter - HoldCell, x + RailOuter + HoldCell, z + RailOuter + HoldCell, HoldCell))
            {
                var (cx, cz) = ((c.Item1 + 0.5) * HoldCell, (c.Item2 + 0.5) * HoldCell);
                var d = Math.Sqrt((cx - x) * (cx - x) + (cz - z) * (cz - z));
                if (!cells.TryGetValue(c, out var had) || d < had)
                    cells[c] = d;
            }

        return cells;
    }

    /// How much of a point's last height it keeps: 0 within RailInner of the edit, 1 past RailOuter, eased between.
    private static double HoldAt(Dictionary<(int, int), double> cells, List<(double X, double Z)> places, double x, double z)
    {
        if (!cells.TryGetValue(((int)Math.Floor(x / HoldCell), (int)Math.Floor(z / HoldCell)), out var rough) || rough > RailOuter + HoldCell)
            return 1.0;
        if (rough < RailInner - HoldCell)
            return 0.0;
        var d2 = double.MaxValue;
        foreach (var (px, pz) in places)
            d2 = Math.Min(d2, (px - x) * (px - x) + (pz - z) * (pz - z));
        var t = Math.Clamp((Math.Sqrt(d2) - RailInner) / (RailOuter - RailInner), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// The last lines' rail line this one lies exactly as (same key, same points), if any.
    private static NetworkLine? LyingAs(Dictionary<long, List<NetworkLine>> before, NetworkLine line)
    {
        if (!before.TryGetValue(line.Key, out var was))
            return null;
        foreach (var w in was)
            if (w.Count == line.Count && w.X.AsSpan().SequenceEqual(line.X) && w.Z.AsSpan().SequenceEqual(line.Z))
                return w;
        return null;
    }

    private static Dictionary<long, List<NetworkLine>> RailByKey(IEnumerable<NetworkLine> lines) =>
        lines.Where(l => l.Rail).GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.ToList());

    /// A local rebuild, once the track's laid and before any heights are worked out: every rail line lying just as one of the last
    /// lines did, all of it past RailOuter from the edit, is kept as that one is, heights and all. Returns how many were.
    internal int FreezeFarRail(LocalRebuild local)
    {
        var cells = EditCells(local.Places);
        var before = RailByKey(local.Seen);
        var kept = 0;
        // (Rail laid outside the cells laid again is there only so the track in them meets it as in a full rebuild: it's put back as
        // it was, by name, its own far junctions not having been laid with all their tracks.)
        var named = local.Dirty is null ? null : local.Seen.Where(l => l.Rail).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var line in _lines)
        {
            if (!line.Rail || line.Origin is not null)
                continue;
            if (named is not null && named.TryGetValue(line.Name, out var was) && was.Count == line.Count && !TouchesDirty(line, local))
            {
                line.KeepAs(was);
                kept++;
                continue;
            }

            if (LyingAs(before, line) is not { } same)
                continue;
            var far = true;
            for (var p = 0; p < line.Count && far; p++)
                far = !cells.TryGetValue(((int)Math.Floor(line.X[p] / HoldCell), (int)Math.Floor(line.Z[p] / HoldCell)), out var d) || d > RailOuter + HoldCell;
            if (!far)
                continue;
            line.KeepAs(same);
            kept++;
        }

        return kept;
    }

    /// A local rebuild, once the heights are worked out: every rail point not kept (FreezeFarRail) that lies where the last lines had
    /// rail (within a centimetre, in plan) keeps that height past RailOuter, and is eased onto it between RailInner and RailOuter.
    /// By where it lies, not by line: a line split by a new switch, or joined, is new lines over the same track far from it.
    /// Returns how many points it held.
    public int HoldFarRail(LocalRebuild local)
    {
        var cells = EditCells(local.Places);
        const double cell = 20.0;
        var index = new Dictionary<(int, int), List<(NetworkLine Line, int Segment)>>();
        foreach (var line in local.Seen)
        {
            if (!line.Rail)
                continue;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                // (Only what's near enough the edit to be held other than whole: a line wholly past it is kept as it was.)
                var (x0, z0, x1, z1) = (Math.Min(line.X[p], line.X[p + 1]), Math.Min(line.Z[p], line.Z[p + 1]), Math.Max(line.X[p], line.X[p + 1]), Math.Max(line.Z[p], line.Z[p + 1]));
                foreach (var c in new GridCells(x0, z0, x1, z1, cell))
                {
                    if (!index.TryGetValue(c, out var list))
                        index[c] = list = [];
                    list.Add((line, p));
                }
            }
        }

        // The last lines' rail under a point: the line, segment and how far along it, preferring a line of the same key, then the nearest.
        (NetworkLine Line, int Q, float T)? Under(NetworkLine line, int p)
        {
            var (x, z) = (line.X[p], line.Z[p]);
            if (!index.TryGetValue(((int)Math.Floor(x / cell), (int)Math.Floor(z / cell)), out var near))
                return null;
            (NetworkLine Line, int Q, float T)? best = null;
            var (bestKey, bestD) = (false, double.MaxValue);
            foreach (var (other, q) in near)
            {
                var (ax, az, dx, dz) = (other.X[q], other.Z[q], other.X[q + 1] - other.X[q], other.Z[q + 1] - other.Z[q]);
                var length2 = dx * dx + dz * dz;
                var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                var d = ex * ex + ez * ez;
                if (d > 0.01 * 0.01)
                    continue;
                var key = other.Key == line.Key;
                if (best is null || (key && !bestKey) || (key == bestKey && d < bestD))
                    (best, bestKey, bestD) = ((other, q, (float)t), key, d);
            }

            return best;
        }

        if (Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
            foreach (var line in _lines.Where(l => l.Rail && !l.Frozen))
            {
                var (far, matched, worst) = (0, 0, 0.0);
                for (var p = 0; p < line.Count; p++)
                {
                    if (HoldAt(cells, local.Places, line.X[p], line.Z[p]) <= 0.0)
                        continue;
                    far++;
                    if (Under(line, p) is not null)
                        matched++;
                    else
                    {
                        var best = double.MaxValue;
                        foreach (var o in local.Seen)
                            if (o.Rail && o.MinX - 50 <= line.X[p] && line.X[p] <= o.MaxX + 50 && o.MinZ - 50 <= line.Z[p] && line.Z[p] <= o.MaxZ + 50)
                                for (var q = 0; q < o.Count; q++)
                                    best = Math.Min(best, Math.Sqrt((o.X[q] - line.X[p]) * (o.X[q] - line.X[p]) + (o.Z[q] - line.Z[p]) * (o.Z[q] - line.Z[p])));
                        worst = Math.Max(worst, best);
                    }
                }

                if (far > 0)
                    RailWerkz.Core.Logging.Log.Info($"Hold debug: line {line.Key} ({line.Count} points, {line.S[^1]:0} m): {far} far, {matched} lying on old rail, unmatched up to {worst:0.000} m off the nearest old point");
            }

        var held = 0;
        Parallel.ForEach(_lines.Where(l => l.Rail && !l.Frozen), line =>
        {
            var mine = 0;
            for (var p = 0; p < line.Count; p++)
            {
                var w = HoldAt(cells, local.Places, line.X[p], line.Z[p]);
                if (w <= 0.0 || Under(line, p) is not { } under)
                    continue;
                var (o, q, t) = under;
                var y = o.Y[q] + (o.Y[q + 1] - o.Y[q]) * t;
                if (w < 1.0)
                {
                    line.Y[p] += (y - line.Y[p]) * (float)w;
                    mine++;
                    continue;
                }

                line.Y[p] = y;
                var at = t < 0.5f ? q : q + 1;
                if (line.Sink.Length == line.Count && o.Sink.Length == o.Count)
                    line.Sink[p] = o.Sink[q] + (o.Sink[q + 1] - o.Sink[q]) * t;
                if (line.Shift.Length == line.Count && o.Shift.Length == o.Count)
                    line.Shift[p] = o.Shift[at];
                if (line.Junction.Length == line.Count && o.Junction.Length == o.Count)
                    line.Junction[p] = o.Junction[at];
                mine++;
            }

            Interlocked.Add(ref held, mine);
        });

        return held;
    }

    /// A local rebuild, once every line far from the edit is back as it was: a crossing's pad far from it is the last lines' pad,
    /// as the track and road it's laid over are. (The passes nudge kept lines before they're put back, so a pad made from them
    /// comes out a few millimetres off, and the ground round every crossing on the route was shaped again.) Returns how many.
    public int KeepFarPads(LocalRebuild local)
    {
        var cells = EditCells(local.Places);
        var before = local.Previous._pads.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var kept = 0;
        for (var i = 0; i < _pads.Count; i++)
        {
            var pad = _pads[i];
            if (cells.TryGetValue(((int)Math.Floor(pad.X / HoldCell), (int)Math.Floor(pad.Z / HoldCell)), out var d) && d <= RailOuter + HoldCell)
                continue;
            if (!before.TryGetValue(pad.Id, out var was) || was == pad)
                continue;
            _pads[i] = was;
            kept++;
        }

        return kept;
    }

    /// The most two rail lines differ in height by at a node they share, metres: a step at a junction.
    public float WorstRailStep()
    {
        var at = new Dictionary<long, (float Y, NetworkLine Line)>();
        var worst = 0f;
        var steps = new List<string>();
        foreach (var line in _lines.Where(l => l.Rail))
            foreach (var (node, point) in line.Nodes)
            {
                if (at.TryGetValue(node, out var first))
                {
                    var step = MathF.Abs(first.Y - line.Y[point]);
                    worst = MathF.Max(worst, step);
                    if (step > 0.002f)
                        steps.Add($"node {node} at ({line.X[point]:0}, {line.Z[point]:0}) {step:0.000} m between {Kind(first.Line)} {first.Line.Key} and {Kind(line)} {line.Key}");
                }
                else
                    at[node] = (line.Y[point], line);
            }

        static string Kind(NetworkLine l) => l.Frozen ? "kept" : l.Origin is not null ? "halo" : "laid";
        if (steps.Count > 0 && Environment.GetEnvironmentVariable("HB_HOLDDEBUG") == "1")
            RailWerkz.Core.Logging.Log.Info($"Rail steps: {string.Join("; ", steps.Take(20))}");
        return worst;
    }

    /// The size of the cells ChainsNear marks what's near in, metres.
    private const double LocalCell = 100.0;

    /// Whether a chain's lines are taken finished from the last lines instead of laid (a local rebuild, a road or mark the last
    /// lines were laid from too, not near the edit). Notes the chain as laid from either way.
    private bool Keep(Chain chain, LocalRebuild? local)
    {
        chain.Id = ChainIdOf(chain);
        _seenChains.Add(chain.Id);
        if (local is null || chain.Rail || local.Near.Contains(chain.Id) || local.Halo.Contains(chain.Id) || !local.Previous._seenChains.Contains(chain.Id))
            return false;
        var previous = local.Previous;
        previous._byChain ??= previous._lines.Where(l => !l.IsAbsent).GroupBy(l => l.Chain).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in previous._byChain.GetValueOrDefault(chain.Id) ?? [])
            _lines.Add(line.FrozenAt(_lines.Count));
        return true;
    }

    /// A local rebuild of some cells (LocalRebuild.Dirty): whether a chain has none of its length in them, so isn't laid (what it gave
    /// is taken from the last lines, AddContext).
    private static bool Outside(Chain chain, LocalRebuild? local, RouteProjection projection)
    {
        if (local?.Dirty is not { } dirty)
            return false;
        (double X, double Z)? last = null;
        foreach (var g in chain.Points)
        {
            var p = projection.Forward(g);
            var (x, z) = (p.East, -p.North);
            if (dirty.Contains(local.CellOf(x, z)))
                return false;
            if (last is { } l)
            {
                // (A long segment's cells between its ends.)
                var steps = (int)(Math.Sqrt((x - l.X) * (x - l.X) + (z - l.Z) * (z - l.Z)) / (local.Size * 0.25));
                for (var k = 1; k <= steps; k++)
                    if (dirty.Contains(local.CellOf(l.X + (x - l.X) * k / (steps + 1), l.Z + (z - l.Z) * k / (steps + 1))))
                        return false;
            }

            last = (x, z);
        }

        return true;
    }

    /// How far inside the dirty cells a line laid again is eased onto the last line's points held outside them, metres.
    internal const double BorderEase = 600.0;

    /// A rebuild of some cells, once every pass is done: each line laid again that reaches out of the dirty cells, and was laid before
    /// under its name with as many points, is held there: its points outside the cells are the last line's exactly (every per-point
    /// array, and the segments between two of them), and those inside within BorderEase of the border are eased onto them. So the line
    /// outside the cells is what it was, by construction, and what's anchored along it there stays good (CellStore.Merge points it at
    /// the line laid). Returns how many lines were held.
    internal int HoldAtBorder(LocalRebuild local)
    {
        if (local.Dirty is null)
            return 0;
        var named = local.Seen.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
        var held = 0;
        foreach (var line in _lines)
        {
            if (line.Origin is not null || !named.TryGetValue(line.Name, out var was) || was.Count != line.Count)
                continue;
            var n = line.Count;
            var inside = new bool[n];
            var any = false;
            for (var p = 0; p < n; p++)
                any |= !(inside[p] = local.InDirty(line.X[p], line.Z[p]));
            if (!any)
                continue;

            // How far along the line each point is from the nearest point outside.
            var away = new double[n];
            Array.Fill(away, double.MaxValue);
            for (var p = 0; p < n; p++)
                away[p] = !inside[p] ? 0 : p > 0 ? away[p - 1] + Math.Abs(line.S[p] - line.S[p - 1]) : double.MaxValue;
            for (var p = n - 2; p >= 0; p--)
                away[p] = Math.Min(away[p], away[p + 1] + Math.Abs(line.S[p + 1] - line.S[p]));

            void Doubles(double[] mine, double[] old, int stride)
            {
                if (mine.Length != n * stride || old.Length != mine.Length || ReferenceEquals(mine, old))
                    return;
                for (var p = 0; p < n; p++)
                {
                    if (away[p] >= BorderEase)
                        continue;
                    var t = away[p] / BorderEase;
                    var keep = 1.0 - t * t * (3.0 - 2.0 * t);
                    for (var k = 0; k < stride; k++)
                        mine[p * stride + k] = !inside[p] ? old[p * stride + k] : mine[p * stride + k] + (old[p * stride + k] - mine[p * stride + k]) * keep;
                }
            }

            void Floats(float[] mine, float[] old, int stride)
            {
                if (mine.Length != n * stride || old.Length != mine.Length || ReferenceEquals(mine, old))
                    return;
                for (var p = 0; p < n; p++)
                {
                    if (away[p] >= BorderEase)
                        continue;
                    var t = (float)(away[p] / BorderEase);
                    var keep = 1f - t * t * (3f - 2f * t);
                    for (var k = 0; k < stride; k++)
                        mine[p * stride + k] = !inside[p] ? old[p * stride + k] : mine[p * stride + k] + (old[p * stride + k] - mine[p * stride + k]) * keep;
                }
            }

            Doubles(line.X, was.X, 1);
            Doubles(line.Z, was.Z, 1);
            Doubles(line.RawX, was.RawX, 1);
            Doubles(line.RawZ, was.RawZ, 1);
            Doubles(line.MappedX, was.MappedX, 1);
            Doubles(line.MappedZ, was.MappedZ, 1);
            Floats(line.Y, was.Y, 1);
            Floats(line.Ground, was.Ground, 1);
            Floats(line.S, was.S, 1);
            Floats(line.Junction, was.Junction, 1);
            Floats(line.Shift, was.Shift, 1);
            Floats(line.Sink, was.Sink, 1);
            Floats(line.Drape, was.Drape, NetworkLine.MarkPoints);
            for (var s = 0; s + 1 < n; s++)
                if (!inside[s] && !inside[s + 1])
                {
                    if (s < line.Structures.Length && s < was.Structures.Length)
                        line.Structures[s] = was.Structures[s];
                    if (s < line.Layers.Length && s < was.Layers.Length)
                        line.Layers[s] = was.Layers[s];
                }

            (line.MinX, line.MinZ, line.MaxX, line.MaxZ) = (line.X.Min(), line.Z.Min(), line.X.Max(), line.Z.Max());
            held++;
        }

        return held;
    }

    /// Whether a line's points outside a rebuild's dirty cells are another's exactly (as HoldAtBorder leaves them).
    internal static bool HeldOutside(NetworkLine line, NetworkLine was, LocalRebuild local)
    {
        if (line.Count != was.Count)
            return false;
        for (var p = 0; p < line.Count; p++)
            if (!local.InDirty(line.X[p], line.Z[p]) && (line.X[p] != was.X[p] || line.Z[p] != was.Z[p] || line.Y[p] != was.Y[p] ||
                    (p < line.Sink.Length && p < was.Sink.Length && line.Sink[p] != was.Sink[p])))
                return false;
        return true;
    }

    /// Whether any of a line's points is in a local rebuild's dirty cells.
    internal static bool TouchesDirty(NetworkLine line, LocalRebuild local)
    {
        for (var p = 0; p < line.Count; p++)
            if (local.InDirty(line.X[p], line.Z[p]))
                return true;
        return false;
    }

    /// A local rebuild of some cells, once the chains through them are laid: the last lines over the cells round them (the context)
    /// that weren't laid again, taken as they were (frozen), for the passes to work the lines laid against as a full rebuild would.
    /// A last line with a point in a dirty cell whose chain wasn't laid again is gone (its ways changed). Sets local.Seen. Returns
    /// how many were taken.
    internal int AddContext(LocalRebuild local)
    {
        var previous = local.Previous;
        var names = _lines.Select(l => l.Name).ToHashSet();
        var over = new List<NetworkLine>();
        var picked = new HashSet<int>();
        foreach (var tile in local.Context!)
            foreach (var i in previous.LinesIn(tile))
                if (picked.Add(i))
                    over.Add(previous._lines[i]);
        previous._byChain ??= previous._lines.Where(l => !l.IsAbsent).GroupBy(l => l.Chain).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new List<NetworkLine>(over);
        foreach (var chain in _seenChains)
            foreach (var line in previous._byChain.GetValueOrDefault(chain) ?? [])
                if (picked.Add(line.Index))
                    seen.Add(line);
        local.Seen = seen;

        var added = 0;
        foreach (var line in over)
        {
            if (names.Contains(line.Name) || _seenChains.Contains(line.Chain) || TouchesDirty(line, local))
                continue;
            _lines.Add(line.FrozenAt(_lines.Count));
            names.Add(line.Name);
            added++;
        }

        return added;
    }

    private static long ChainIdOf(Chain chain)
    {
        var hash = new StableHash();
        hash.Add(chain.Rail ? 1L : 0L);
        hash.Add(chain.Class);
        foreach (var node in chain.Nodes)
            hash.Add(node);
        foreach (var (key, value) in chain.Tags.Where(t => t.Key != RouteNetwork.HeightsTag).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            hash.Add(key);
            hash.Add(value);
        }

        return (long)hash.Value;
    }

    // --- Profiles. ---

    private HashSet<int>? _needGround;

    /// After a rebuild, takes the ground already sampled under every line that came out the same as one of the previous
    /// lines. Returns the tiles whose heights are still needed for the rest (see SampleGround).
    public IEnumerable<TileId> AdoptGround(IEnumerable<NetworkLine> previous)
    {
        var known = new Dictionary<long, NetworkLine>();
        foreach (var line in previous)
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
            var fx = (float)(line.X[p] / Sp - gx0);
            var fz = (float)(line.Z[p] / Sp - gz0);
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
    private void Watch(string stage)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("HB_WATCH"), out var index) || _lines.FirstOrDefault(l => l.Index == index) is not { } l)
            return;
        RailWerkz.Core.Logging.Log.Info($"Watch {index} {stage}: max {l.Y.Max():0.0}, y at mid {l.Y[l.Count / 2]:0.0}");
    }

    /// HB_PROFILELAPS=1: how long each step of FinishProfiles takes, logged at its end.
    private static readonly bool ProfileLaps = Environment.GetEnvironmentVariable("HB_PROFILELAPS") == "1";
    private System.Diagnostics.Stopwatch? _lapWatch;
    private string? _lapName;
    private List<string>? _laps;

    private void Lap(string? next)
    {
        if (!ProfileLaps)
            return;
        if (_lapWatch is { } w && _lapName is { } name)
            (_laps ??= []).Add($"{name} {w.ElapsedMilliseconds}");
        if (next is null)
        {
            RailWerkz.Core.Logging.Log.Info($"Profile laps, ms: {string.Join(" | ", _laps ?? [])}");
            (_laps, _lapWatch, _lapName) = (null, null, null);
            return;
        }

        _lapName = next;
        _lapWatch = System.Diagnostics.Stopwatch.StartNew();
    }

    public void FinishProfiles()
    {
        // (Bridges lengthened by hand first: the profiles carry each deck straight between its ends.)
        Lap("ExtendBridges();");
        ExtendBridges();
        _liftCells = null;
        _junctionRounds.Clear();
        Lap("FindPins();");
        FindPins();

        // (Kept lines, in a local rebuild, are finished: every pass leaves them be, and reads them as the lines round them are
        // worked out.)
        Lap("Parallel.ForEach(_lines.Where(l => !l.Fr");
        Parallel.ForEach(_lines.Where(l => !l.Frozen), line =>
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
                ApplyPins(line);
            }
            else
            {
                // A road's ground spikes and steps wherever the terrain does (a cutting's edge, a bank, a lidar seam): the steps
                // go first (a running median), then it's held to its class's grade and smoothed, more for faster roads.
                var road = (Routes.RoadClass)line.Class;
                Bridge(line);
                Median(line, RoadMedian(road));
                LimitGrade(line, RoadGrade(road));
                Smooth(line, RoadEasing(road));
                Smooth(line, RoadEasing(road));
                Bridge(line);
            }
        });

        Lap("LiftBridges();");
        LiftBridges();
        Watch("lift");
        Lap("SpreadForks();");
        SpreadForks();
        _liftCells = null; // (SpreadForks moves lines: LiftBridges' cells are made again.)
        Lap("ShiftSurfaces();");
        ShiftSurfaces();

        Lap("var surfaceWatch = System.Diagnostics.St");
        var surfaceWatch = System.Diagnostics.Stopwatch.StartNew();
        // Each pass draws tracks close in height closer still; a few make tracks side by side agree to millimetres.
        var wide = new RailPoints(_lines, SurfaceReach);
        for (var pass = 0; pass < 3; pass++)
            wide.Smooth(SurfaceSpread, SurfaceStep);

        Lap("Parallel.ForEach(_lines.Where(l => l.Rai");
        Parallel.ForEach(_lines.Where(l => l.Rail && !l.Frozen), line =>
        {
            Bridge(line);
            LimitGrade(line, SteepestGrade(line));
            Smooth(line, 40f);
            Smooth(line, 40f);
            ApplyPins(line);
        });

        // Last, tracks right beside each other (and close in height) are made to agree exactly, so one's ballast never
        // stands over the other's ties.
        Lap("var close = new RailPoints(_lines, 6.0);");
        var close = new RailPoints(_lines, 6.0);
        for (var pass = 0; pass < 4; pass++)
            close.Smooth(1000.0, 0.15f);
        RailWerkz.Core.Logging.Log.Info($"Profiles: fitted in {surfaceWatch.ElapsedMilliseconds} ms");
        Lap("Parallel.ForEach(_lines.Where(l => l.Rai");
        Parallel.ForEach(_lines.Where(l => l.Rail && !l.Frozen), line =>
        {
            // The deck straight between its ends again, then the grade eased onto and off it over a short curve (and a pinned
            // grade's ends rounded the same way).
            Bridge(line);
            ApplyPins(line);
            Smooth(line, 15f);
        });

        Watch("surface");
        // Where lines meet, they're brought to one height, which pulls a deck towards the roads it meets; it's lifted to clear what's
        // under it again, and the meeting heights worked out again round that, until they agree.
        Lap("for (var round = 0; round < 3; round++)");
        for (var round = 0; round < 3; round++)
        {
            Junctions();
            Watch($"junctions {round}");
            LiftBridges();
            Watch($"lifted {round}");
        }

        Lap("Junctions();");
        Junctions();
        Lap("SmoothJoins();");
        SmoothJoins();
        // (After the joins and junctions have shaped the lines they leave: a siding takes its line's height as it's finished, and
        // meets it at its switches exactly.)
        Lap("FollowBeside();");
        FollowBeside();
        // (Pinned track as it was pinned again, after sidings were drawn to their lines, before the junctions are read.)
        Lap("foreach (var line in _linePins.Keys)");
        foreach (var line in _linePins.Keys)
            ApplyPins(line, ease: false);
        // (The whole network's heights eased together, before the bridges are lifted clear a last time: the kinks the lines'
        // fitting one by one leaves at their joins smoothed out.)
        Lap("SmoothRailNetwork();");
        SmoothRailNetwork();
        // The last word goes to headroom: a deck lifted clear of what's under it, its junctions already all but agreed.
        Lap("LiftBridges();");
        LiftBridges();
        Watch("junctions");
        Lap("FindJunctions();");
        FindJunctions();
        Lap("LetDownBeds();");
        LetDownBeds();
        Lap("LevelRoadsAtRails();");
        LevelRoadsAtRails();
        Lap("MakePads();");
        MakePads();
        Lap("SnapRoadsToPads();");
        SnapRoadsToPads();
        Lap("LevelRoadsAtPads();");
        LevelRoadsAtPads();
        Watch("pads");
        Lap("CrestDecks();");
        CrestDecks();
        // Cresting a bridge smooths its whole road, which can pull a road's end on a pad back down off the pad's level (leaving a
        // step where it meets the crossing's deck): they're brought back up to it.
        Lap("LevelRoadsAtPads();");
        LevelRoadsAtPads();
        // (Once the decks' heights are known: an end that nearly has room for a slope under it is given it.)
        Lap("ExtendBridges(toFitSlopes: true);");
        ExtendBridges(toFitSlopes: true);

        Lap("_widestAt.Clear();");
        _widestAt.Clear();
        foreach (var road in _lines.Where(l => !l.Rail && !l.Mark))
            foreach (var (node, _) in road.Nodes)
                _widestAt[node] = MathF.Max(_widestAt.GetValueOrDefault(node), road.Width * 0.5f);
        Lap("Parallel.ForEach(_lines.Where(l => !l.Fr");
        Parallel.ForEach(_lines.Where(l => !l.Frozen), line => line.Junction = JunctionDistances(line));
        Watch("crest");
        Lap("Layer();");
        Layer();
        Lap("Turnouts = Turnout.Find(_lines, Fitted);");
        Turnouts = Turnout.Find(_lines, Fitted);
        Lap("LevelRoutes();");
        LevelRoutes();
        Before = null;

        Lap(null);
    }

    /// How far either side of a join between two rail lines their heights are fitted together, metres.
    private const float JoinReach = 100f;

    /// Where one rail line runs straight on into another (laid as two, ending on the same node with nothing else there), each
    /// was smoothed on its own, its end following the ground with nothing past it to average with, and the two were then made to
    /// meet at one height: a little hump or dip at the join. Here the profile JoinReach either side is fitted as one (a
    /// parabola, so a crest or sag carries through) and each side eased onto that fit, wholly at the join and not at all
    /// JoinReach away. A line kept from the last rebuild stays as it was, and the other side meets it.
    private void SmoothJoins()
    {
        var ends = new Dictionary<long, List<(NetworkLine Line, bool AtStart)>>();
        var through = new HashSet<long>();
        foreach (var line in _lines)
        {
            if (!line.Rail || line.Mark || line.Count < 2)
                continue;
            foreach (var (node, point) in line.Nodes)
            {
                if (point == 0 || point == line.Count - 1)
                {
                    if (!ends.TryGetValue(node, out var list))
                        ends[node] = list = [];
                    list.Add((line, point == 0));
                }
                else
                    through.Add(node);
            }
        }

        // How many rail lines touch each node: a side of a join stops short of any other junction on its line.
        var touching = new Dictionary<long, int>();
        foreach (var line in _lines.Where(l => l.Rail && !l.Mark))
            foreach (var node in line.Nodes.Select(n => n.Node).Distinct())
                touching[node] = touching.GetValueOrDefault(node) + 1;

        var joined = 0;
        foreach (var (node, list) in ends)
        {
            if (list.Count != 2 || through.Contains(node) || list[0].Line == list[1].Line)
                continue;
            var (a, b) = (list[0], list[1]);
            if (a.Line.Frozen && b.Line.Frozen)
                continue;

            // Each side's points within reach of the join, by distance from it (negative along a, positive along b), on the
            // ground only (a bridge deck is carried as it is), and how far that side reaches: JoinReach, but no further than half
            // its line (its other end has a join of its own to keep) or the next junction on it (agreed with the lines there).
            List<(NetworkLine Line, int Point, float S, float Reach)> Side((NetworkLine Line, bool AtStart) end, float sign)
            {
                var (line, atStart) = end;
                var from = atStart ? 0 : line.Count - 1;
                var reach = MathF.Min(JoinReach, line.S[^1] * 0.5f);
                foreach (var (other, q) in line.Nodes)
                    if (q != from && touching.GetValueOrDefault(other) > 1)
                        reach = MathF.Min(reach, MathF.Abs(line.S[q] - line.S[from]));
                var result = new List<(NetworkLine, int, float, float)>();
                for (var p = from; p >= 0 && p < line.Count; p += atStart ? 1 : -1)
                {
                    var away = MathF.Abs(line.S[p] - line.S[from]);
                    if (away > reach || !OnGround(line, p))
                        break;
                    result.Add((line, p, sign * away, MathF.Max(reach, 1f)));
                }

                return result;
            }

            var points = Side(a, -1f).Concat(Side(b, 1f)).ToList();
            if (points.Count < 6)
                continue;

            // y = c0 + c1 s + c2 s^2, by least squares.
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
            foreach (var (line, p, at, _) in points)
            {
                double x = at, y = line.Y[p];
                s0 += 1; s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x;
                y0 += y; y1 += x * y; y2 += x * x * y;
            }

            double Det(double a1, double b1, double c1, double a2, double b2, double c2, double a3, double b3, double c3) =>
                a1 * (b2 * c3 - b3 * c2) - b1 * (a2 * c3 - a3 * c2) + c1 * (a2 * b3 - a3 * b2);
            var det = Det(s0, s1, s2, s1, s2, s3, s2, s3, s4);
            if (Math.Abs(det) < 1e-9)
                continue;
            var c0 = Det(y0, s1, s2, y1, s2, s3, y2, s3, s4) / det;
            var c1 = Det(s0, y0, s2, s1, y1, s3, s2, y2, s4) / det;
            var c2 = Det(s0, s1, y0, s1, s2, y1, s2, s3, y2) / det;
            double Fit(float at) => c0 + c1 * at + c2 * at * at;

            // A side kept as it was holds the join's height: the fit's moved to meet it there.
            var shift = a.Line.Frozen ? a.Line.Y[a.AtStart ? 0 : a.Line.Count - 1] - Fit(0f)
                : b.Line.Frozen ? b.Line.Y[b.AtStart ? 0 : b.Line.Count - 1] - Fit(0f) : 0.0;
            foreach (var (line, p, at, reach) in points)
            {
                if (line.Frozen)
                    continue;
                var weight = 0.5f * (1f + MathF.Cos(MathF.PI * MathF.Min(MathF.Abs(at) / reach, 1f)));
                line.Y[p] += weight * (float)(Fit(at) + shift - line.Y[p]);
            }

            joined++;
        }

        if (joined > 0)
            RailWerkz.Core.Logging.Log.Info($"Profiles: {joined} joins between rail lines smoothed over {JoinReach:0} m");
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
                // As far as the normal route's ballast widens to carry it (past the timbers, to where the two beds part), so the
                // widened bed's end never stands proud of, or sinks under, a diverging route already climbing away.
                var end = MathF.Max(t.TimberEnd, t.BallastNormal);
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
                        Lift(line, q, lift * Ease(MathF.Abs(line.S[p] - line.S[q]) / span));
                    continue;
                }

                foreach (var (q, lift) in lifts)
                    Lift(line, q, lift);
                var last = lifts[^1].Lift;
                var stop = p;
                while (stop >= 0 && stop < line.Count && !nodes.Contains(stop) && MathF.Abs(line.S[stop] - line.S[lifts[^1].Point]) < reach)
                    stop += reverse.Direction;
                var length = MathF.Max(MathF.Abs(line.S[Math.Clamp(stop, 0, line.Count - 1)] - line.S[lifts[^1].Point]), 1f);
                for (var q = p; q != stop && q >= 0 && q < line.Count; q += reverse.Direction)
                    Lift(line, q, last * Ease(1f - MathF.Abs(line.S[q] - line.S[lifts[^1].Point]) / length));
            }

        // A diverging route raised to a higher bed's height (a low yard track off a main) is raised on a taller bed, not
        // lifted bodily with its ground: its toe stays on the ground and its ballast meets the normal route's at the top,
        // so neither buries the other's ties. (Lowered, its bed's let down the same way.)
        static void Lift(NetworkLine line, int q, float by)
        {
            line.Y[q] += by;
            if (q < line.Sink.Length && !line.Embedded)
                line.Sink[q] = Math.Clamp(line.Sink[q] - by, 0f, NetworkLine.BallastHeight);
        }
    }

    /// How far apart in height two rail lines whose ballast overlaps are set, so one's ballast and ties lie cleanly over
    /// the other's instead of fighting it in the depth buffer.
    private const float LayerStep = 0.003f;

    /// How far from where tracks join (a switch, one track carrying on as another) a line's layer comes in: at the join every
    /// line's at its own height, so the rails meet there without a step.
    private const float LayerEase = 15f;

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

        // The nodes where tracks join: a rail line ends there and another touches it. (Not where two only cross: a diamond keeps
        // its layers right through.)
        var touching = new Dictionary<long, int>();
        var ending = new HashSet<long>();
        foreach (var line in rails)
            foreach (var (node, point) in line.Nodes)
            {
                touching[node] = touching.GetValueOrDefault(node) + 1;
                if (point == 0 || point == line.Count - 1)
                    ending.Add(node);
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
            if (mine == 0)
                continue;
            var joins = line.Nodes.Where(n => ending.Contains(n.Node) && touching[n.Node] > 1).Select(n => line.S[n.Point]).ToArray();
            for (var p = 0; p < line.Count; p++)
            {
                var near = LayerEase;
                foreach (var at in joins)
                    near = MathF.Min(near, MathF.Abs(line.S[p] - at));
                line.Y[p] -= mine * LayerStep * Ease(near / LayerEase);
            }
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

    /// How far either side a road's ground is taken the median of, and the steepest it's built, by class.
    private static float RoadMedian(Routes.RoadClass road) => road switch
    {
        Routes.RoadClass.Highway => 30f,
        Routes.RoadClass.Major => 20f,
        _ => 12f,
    };

    /// How far either side a road's heights are averaged over, twice, by class: its vertical curves.
    private static float RoadEasing(Routes.RoadClass road) => road switch
    {
        Routes.RoadClass.Highway => 25f,
        Routes.RoadClass.Major => 16f,
        Routes.RoadClass.Minor => 12f,
        _ => 8f,
    };

    private static float RoadGrade(Routes.RoadClass road) => road switch
    {
        Routes.RoadClass.Highway => 0.05f,
        Routes.RoadClass.Major => 0.07f,
        Routes.RoadClass.Minor => 0.09f,
        Routes.RoadClass.Local => 0.12f,
        _ => 0.15f,
    };

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
    /// How near a hand-laid track has to run to the line it leaves to take its height (FollowBeside), and how far out from there
    /// it eases over to its own.
    private const float BesideReach = 6.5f;
    private const float BesideEase = 3f;
    /// How far from a junction with track it doesn't follow a siding comes into following its own (FollowBeside).
    private const float FollowHold = 20f;

    /// Track laid by hand (exact) that leaves a line at a switch, a siding or a track laid beside it, is at that line's height
    /// wherever it runs beside it: a siding's laid level with its main line. Fitted to the ground on its own, as mapped track is, a
    /// siding a few metres off a main line on a bank or in a cutting sits on natural ground a metre or more higher or lower, too
    /// far apart in height for the smoothing that levels tracks side by side to draw them together (it's kept from levelling a
    /// flyover's ramp climbing past a line), and humps or dips beside it. Mapped track is left as the smoothing has it.
    private void FollowBeside()
    {
        // Every rail line at each node, and where on it.
        var at = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => l.Rail && !l.Disused && l.Count >= 2))
            foreach (var (node, point) in line.Nodes)
            {
                if (!at.TryGetValue(node, out var list))
                    at[node] = list = [];
                list.Add((line, point));
            }

        // The tracks each siding follows, first; then each laid after the tracks it follows (and theirs before them), so a siding off
        // a siding reads the one it leaves as that's been laid, and meets it at their switch exactly. (In the lines' own order, a
        // siding laid before the track it left was moved would be left standing a few centimetres off it at the switch.)
        var follows = new Dictionary<NetworkLine, HashSet<NetworkLine>>();
        foreach (var line in _lines.Where(l => l.Rail && l.Exact && !l.Frozen && !l.Disused && l.Count >= 2))
        {
            // The lines it leaves: at a switch at either of its ends where it's the diverging route, the toe and the straight route
            // (as the turnouts read them, Turnout.Arrange). Never the other way about: a main line doesn't follow its sidings.
            var partners = new HashSet<NetworkLine>();
            foreach (var (node, point) in line.Nodes)
            {
                if ((point != 0 && point != line.Count - 1) || !at.TryGetValue(node, out var meeting))
                    continue;
                var legs = new List<Leg>();
                foreach (var (other, q) in meeting)
                {
                    if (q > 0)
                        legs.Add(new Leg(other, q, -1));
                    if (q < other.Count - 1)
                        legs.Add(new Leg(other, q, 1));
                }

                if (legs.Count == 3 && Turnout.Arrange(legs, out var toe, out var normal, out var reverse, out _) && reverse.Line == line)
                {
                    partners.Add(toe.Line);
                    partners.Add(normal.Line);
                }
            }

            if (partners.Count == 0)
                continue;

            // And the rest of each of those tracks: the lines carrying on from them end to end, with nothing else at the join (a
            // track laid in pieces, or cut where its kind changes), which a long siding runs beside as much as the line it left.
            var open = new Queue<NetworkLine>(partners);
            while (open.Count > 0 && partners.Count < 64)
            {
                var track = open.Dequeue();
                foreach (var (node, point) in track.Nodes)
                {
                    if ((point != 0 && point != track.Count - 1) || !at.TryGetValue(node, out var joined) || joined.Count != 2)
                        continue;
                    var (next, q) = joined[0].Line == track ? joined[1] : joined[0];
                    if (next != line && (q == 0 || q == next.Count - 1) && partners.Add(next))
                        open.Enqueue(next);
                }
            }

            follows[line] = partners;
        }

        var order = new List<NetworkLine>();
        var visited = new HashSet<NetworkLine>();
        void Visit(NetworkLine line)
        {
            if (!visited.Add(line))
                return;
            foreach (var other in follows[line])
                if (follows.ContainsKey(other))
                    Visit(other);
            order.Add(line);
        }

        foreach (var line in follows.Keys)
            Visit(line);

        foreach (var line in order)
        {
            var partners = follows[line];
            // Where it meets track it neither follows nor is followed by (a switch at its far end, onto another line): the
            // junctions have agreed its height there with that track's, so it's held there and eases into following away from it.
            var held = line.Nodes.Where(n => at.TryGetValue(n.Node, out var meeting) &&
                    meeting.Any(m => m.Line != line && !partners.Contains(m.Line) && !(follows.TryGetValue(m.Line, out var theirs) && theirs.Contains(line))))
                .Select(n => line.S[n.Point]).ToArray();
            // How far each point is drawn to the track it follows, worked out point by point, then smoothed along the line before it's
            // applied: taken straight, it steps wherever the nearest track changes, and where a pinned span or a bridge (held as they
            // are) meets track that follows, a few centimetres in one segment.
            var pull = new float[line.Count];
            var still = new bool[line.Count];
            for (var p = 0; p < line.Count; p++)
            {
                if (!OnGround(line, p) || Pinned(line, p))
                {
                    still[p] = true;
                    continue;
                }

                var (x, z) = (line.X[p], line.Z[p]);
                (double Away, float Y)? best = null;
                foreach (var other in partners)
                {
                    if (x < other.MinX - BesideReach - BesideEase || x > other.MaxX + BesideReach + BesideEase ||
                        z < other.MinZ - BesideReach - BesideEase || z > other.MaxZ + BesideReach + BesideEase)
                        continue;
                    for (var i = 0; i + 1 < other.Count; i++)
                    {
                        var (dx, dz) = (other.X[i + 1] - other.X[i], other.Z[i + 1] - other.Z[i]);
                        var t = Math.Clamp(((x - other.X[i]) * dx + (z - other.Z[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                        var (ex, ez) = (other.X[i] + dx * t - x, other.Z[i] + dz * t - z);
                        var away = Math.Sqrt(ex * ex + ez * ez);
                        if (best is null || away < best.Value.Away)
                            best = (away, other.Y[i] + (other.Y[i + 1] - other.Y[i]) * (float)t);
                    }
                }

                if (best is not { } found || found.Away > BesideReach + BesideEase)
                    continue;
                var weight = 1f - Math.Clamp(((float)found.Away - BesideReach) / BesideEase, 0f, 1f);
                foreach (var s in held)
                    weight *= Ease(Math.Clamp(MathF.Abs(line.S[p] - s) / FollowHold, 0f, 1f));
                pull[p] = (found.Y - line.Y[p]) * weight;
            }

            SmoothPull(line, pull, still);
            for (var p = 0; p < line.Count; p++)
                line.Y[p] += pull[p];
        }
    }

    /// How far either side FollowBeside's pull is averaged along a line, metres (twice over: a vertical curve, not a corner).
    private const float PullEase = 12f;

    /// A pull along a line averaged over PullEase either side, twice, with the points that are held kept at none. The window
    /// narrows to nothing at the line's ends, so it still meets the track it follows at their switch exactly.
    private static void SmoothPull(NetworkLine line, float[] pull, bool[] still)
    {
        var s = line.S;
        var count = line.Count;
        var sum = new double[count + 1];
        var result = new float[count];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < count; i++)
                sum[i + 1] = sum[i] + pull[i];
            int lo = 0, hi = 0;
            for (var i = 0; i < count; i++)
            {
                if (still[i])
                {
                    result[i] = 0f;
                    continue;
                }

                var window = MathF.Min(PullEase, MathF.Min(s[i] - s[0], s[^1] - s[i]));
                while (lo < i && s[lo] < s[i] - window)
                    lo++;
                while (lo > 0 && s[lo - 1] >= s[i] - window)
                    lo--;
                hi = Math.Max(hi, i + 1);
                while (hi < count && s[hi] <= s[i] + window)
                    hi++;
                while (hi > i + 1 && s[hi - 1] > s[i] + window)
                    hi--;
                result[i] = (float)((sum[hi] - sum[lo]) / (hi - lo));
            }

            Array.Copy(result, pull, count);
        }
    }

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
        // (In a local rebuild, a node a kept line runs through is held where the last rebuild had it at this same round: what a
        // full rebuild would make of it, the edit being too far off to change it. The lines laid again meet it there.)
        var round = _junctionRounds.Count;
        var before = _previous?._junctionRounds.ElementAtOrDefault(round);
        var held = new HashSet<long>();
        foreach (var (node, list) in uses)
        {
            // (A pinned node's held where it's pinned, even on one line alone: so the line's eased between its junctions round it.)
            if (_nodePins.TryGetValue(node, out var pin) && list.Any(u => u.Line.Rail && !u.Line.Frozen))
            {
                targets[node] = pin;
                held.Add(node);
                continue;
            }

            if (list.Count < 2)
                continue;

            if (list.Any(u => u.Line.Frozen))
            {
                targets[node] = before is not null && before.TryGetValue(node, out var was) ? was : (float)list.Where(u => u.Line.Frozen).Average(u => u.Line.Y[u.Point]);
                held.Add(node);
                continue;
            }

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
                var (holdA, holdB) = (held.Contains(a), held.Contains(b));
                if (holdA && holdB)
                    continue;
                var half = MathF.CopySign(excess * 0.5f, difference);
                // (One end held: the other moves the whole way.)
                targets[a] -= holdA ? 0f : holdB ? half * 2f : half;
                targets[b] += holdB ? 0f : holdA ? half * 2f : half;
                moved = true;
            }

            if (!moved)
                break;
        }

        _junctionRounds.Add(targets);
        Parallel.ForEach(_lines, line =>
        {
            var nodes = along[line];
            if (nodes.Count == 0 || line.Frozen)
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

    /// Each round of Junctions' heights at the nodes, in order, for a local rebuild after this one to hold its kept nodes to.
    private readonly List<Dictionary<long, float>> _junctionRounds = [];

    /// The last lines, during a local rebuild's FinishProfiles (let go after, so old lines don't pile up).
    internal NetworkLines? Before;
    private NetworkLines? _previous => Before;

    /// How many times the nodes along the lines are pulled together; it stops early once none needs it.
    private const int JunctionPasses = 400;

    private static float Ease(float t) => t * t * (3f - 2f * t);

    /// Nodes where a road splits in two: its markings run right up to them.
    private readonly HashSet<long> _forks = [];

    /// Where a wide road splits into two narrower ones, as a dual carriageway begins, they're mapped from one point down its middle. Each
    /// is moved over to its own side of the wide road instead, so their outer edges carry on from its edges, leaving it running straight
    /// on from the wide road and easing onto its mapped way.
    /// The wide roads' surfaces moved off their lines at forks: which line, which end, and by how much to its right there.
    private readonly List<(NetworkLine Line, int Point, float By)> _surfaceShifts = [];

    /// The way to a line's right at a point (the way it runs, by its points, turned a quarter).
    private static (double X, double Z) RightAt(NetworkLine line, int p)
    {
        var (a, b) = (Math.Max(p - 1, 0), Math.Min(p + 1, line.Count - 1));
        var (fx, fz) = (line.X[b] - line.X[a], line.Z[b] - line.Z[a]);
        var length = Math.Max(Math.Sqrt(fx * fx + fz * fz), 1e-9);
        return (-fz / length, fx / length);
    }

    /// Each line's surface shift at every point, from its forks' ends: held at the end and eased away over a lane taper, or
    /// between the two ends' when both have one.
    private void ShiftSurfaces()
    {
        foreach (var group in _surfaceShifts.GroupBy(f => f.Line))
        {
            var line = group.Key;
            if (line.Frozen)
                continue;
            var shift = new float[line.Count];
            var at = group.ToDictionary(f => f.Point, f => f.By);
            var length = line.S[^1];
            for (var p = 0; p < line.Count; p++)
            {
                if (at.TryGetValue(0, out var v0) && at.TryGetValue(line.Count - 1, out var v1))
                {
                    var t = line.S[p] / MathF.Max(length, 1e-3f);
                    shift[p] = v0 + (v1 - v0) * t * t * (3f - 2f * t);
                    continue;
                }

                foreach (var (point, by) in at)
                {
                    var reach = MathF.Min(TaperLength(line, by), length);
                    var t = Math.Clamp(1f - MathF.Abs(line.S[p] - line.S[point]) / MathF.Max(reach, 1e-3f), 0f, 1f);
                    shift[p] += by * t * t * (3f - 2f * t);
                }
            }

            line.Shift = shift;
            line.Formation += shift.Max(MathF.Abs);
        }
    }

    private void SpreadForks()
    {
        _forks.Clear();
        _surfaceShifts.Clear();
        var touching = new Dictionary<long, int>();
        var ends = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines)
            foreach (var (node, point) in line.Nodes)
            {
                touching[node] = touching.GetValueOrDefault(node) + 1;
                if (line.Rail || line.Mark || (point != 0 && point != line.Count - 1) || line.Structures[Math.Min(point, line.Structures.Length - 1)] != Structure.Ground)
                    continue;
                if (!ends.TryGetValue(node, out var list))
                    ends[node] = list = [];
                list.Add((line, point));
            }

        foreach (var (node, list) in ends)
        {
            if (list.Count != 3 || touching[node] != 3)
                continue;
            // (A divided road's carriageways already spread apart when the roads were shaped.)
            if (_spreadNodes.Contains(node))
            {
                _forks.Add(node);
                continue;
            }
            var ordered = list.OrderByDescending(e => e.Line.Width).ToList();
            var (main, a, b) = (ordered[0], ordered[1], ordered[2]);
            var width = main.Line.Width;
            // A fork when the road's wider than either branch and near enough as wide as both: an exit or an entry taking a lane
            // or two off the side (not a side road at a corner, where the road's no wider than the one carrying on).
            if (width < a.Line.Width + 0.5f || width < (a.Line.Width + b.Line.Width) * 0.6f)
                continue;

            var (ox, oz) = Outward(main.Line, main.Point);
            bool Onward((NetworkLine Line, int Point) e)
            {
                var (x, z) = Outward(e.Line, e.Point);
                return x * ox + z * oz < -0.7;
            }

            if (!Onward(a) || !Onward(b))
                continue;

            // Which side each goes to: by where it's got to a little way on.
            var (sx, sz) = (-oz, ox);
            double Side((NetworkLine Line, int Point) e)
            {
                var from = e.Line.S[e.Point];
                var far = Enumerable.Range(0, e.Line.Count).FirstOrDefault(p => MathF.Abs(e.Line.S[p] - from) > 40f, e.Point == 0 ? e.Line.Count - 1 : 0);
                return (e.Line.X[far] - e.Line.X[e.Point]) * sx + (e.Line.Z[far] - e.Line.Z[e.Point]) * sz;
            }

            var (sideA, sideB) = (Side(a), Side(b));
            if (Math.Abs(sideA - sideB) < 0.5)
                continue;

            // As built: the through lanes run straight on, and the wide road is the through road with the exit's (or entry's) lanes
            // added on one side. So the branch carrying straightest on (the wider, if near) stays as mapped; the wide road's surface
            // sits off its line towards the other branch, by the lanes it has over the through road, so its through edge carries
            // straight on into the through road's; and the other branch is moved out to lie beside the through road there.
            float Straightness((NetworkLine Line, int Point) e)
            {
                var (x, z) = Outward(e.Line, e.Point);
                return (float)-(x * ox + z * oz) + e.Line.Width * 0.01f;
            }

            var (through, peel, peelSide) = Straightness(a) >= Straightness(b) ? (a, b, sideB > sideA ? 1f : -1f) : (b, a, sideA > sideB ? 1f : -1f);
            var surface = (width - through.Line.Width) * 0.5f;
            if (!peel.Line.Frozen)
                MoveOver(peel, (ox, oz), (sx, sz), peelSide * (width - (through.Line.Width + peel.Line.Width) * 0.5f));
            var (_, mainRight) = (0, RightAt(main.Line, main.Point));
            _surfaceShifts.Add((main.Line, main.Point, surface * peelSide * (float)(sx * mainRight.X + sz * mainRight.Z)));
            if (Environment.GetEnvironmentVariable("HB_WATCH") == main.Line.Index.ToString())
                RailWerkz.Core.Logging.Log.Info($"Fork at {main.Line.Index} end {main.Point}: wide {width:0.0}, through {through.Line.Index} {through.Line.Width:0.0} straight {Straightness(through):0.00}, peel {peel.Line.Index} {peel.Line.Width:0.0} straight {Straightness(peel):0.00} side {peelSide}, surface {surface:0.0}, dot {sx * mainRight.X + sz * mainRight.Z:0.00}");
            _forks.Add(node);
        }
    }

    private static void MoveOver((NetworkLine Line, int Point) end, (double X, double Z) onward, (double X, double Z) side, float by)
    {
        var (line, point) = end;
        var (nx, nz) = (line.X[point], line.Z[point]);
        var reach = MathF.Min(TaperLength(line, by), line.S[line.Count - 1] * 0.8f);
        var from = line.S[point];
        for (var p = 0; p < line.Count; p++)
        {
            var d = MathF.Abs(line.S[p] - from);
            if (d >= reach)
                continue;
            var t = d / reach;
            var ease = t * t * (3f - 2f * t);
            var (x, z) = (nx + side.X * by + onward.X * d, nz + side.Z * by + onward.Z * d);
            line.X[p] = x + (line.X[p] - x) * ease;
            line.Z[p] = z + (line.Z[p] - z) * ease;
        }

        for (var p = 1; p < line.Count; p++)
            line.S[p] = line.S[p - 1] + (float)Math.Sqrt((line.X[p] - line.X[p - 1]) * (line.X[p] - line.X[p - 1]) + (line.Z[p] - line.Z[p - 1]) * (line.Z[p] - line.Z[p - 1]));
        line.MinX = line.X.Min();
        line.MaxX = line.X.Max();
        line.MinZ = line.Z.Min();
        line.MaxZ = line.Z.Max();
    }

    /// The half width of the widest road at each node.
    private readonly Dictionary<long, float> _widestAt = [];

    private float[] JunctionDistances(NetworkLine line)
    {
        var distances = new float[line.Count];
        Array.Fill(distances, float.MaxValue);
        // At a junction the distance starts below nothing by how much wider the widest road there is than this one, so this one's
        // markings stop at that road's edge, not its own width from the node (which, on a wide road, is over its lanes).
        foreach (var (node, point) in line.Nodes)
            if (_junctionNodes.Contains(node) && !_continuing.ContainsKey((line.Index, point)) && !_forks.Contains(node))
                distances[point] = line.Rail ? 0f : MathF.Min(0f, line.Width * 0.5f - _widestAt.GetValueOrDefault(node));

        for (var p = 1; p < line.Count; p++)
            distances[p] = Math.Min(distances[p], distances[p - 1] + line.S[p] - line.S[p - 1]);
        for (var p = line.Count - 2; p >= 0; p--)
            distances[p] = Math.Min(distances[p], distances[p + 1] + line.S[p + 1] - line.S[p]);
        return distances;
    }

    /// How far a rail's top stands above the line's height, and how far a road's surface above its own. How gently a road eases to it.
    private const float RailTopAbove = NetworkLine.BallastHeight + 0.18f;
    private const float RoadSurfaceAbove = 0.06f;
    private const float RoadToRailGrade = 0.02f;
    /// The most a road's lifted or lowered to meet rails it crosses on the level.
    private const float LevelCrossingMost = 1.5f;

    /// Where a road crosses a track on the level, it comes to the rails' height there, so the two can be one surface. Its grade eases to
    /// that gently, and stays at its own height at its ends, which other roads meet.
    private void LevelRoadsAtRails()
    {
        // The rails' segments on the ground, filed by the grid cells they cross, so a road's segment only looks at the rail near it.
        // (A long line's bounds take in most of the route: looking through all of it for every road was most of a rebuild.)
        var segments = new List<(NetworkLine Rail, int R)>();
        var cells = new Dictionary<(int X, int Z), List<int>>();
        foreach (var rail in _lines.Where(l => l.Rail && !l.Disused))
            for (var r = 0; r + 1 < rail.Count; r++)
            {
                if (rail.Structures[r] != Structure.Ground)
                    continue;
                var id = segments.Count;
                segments.Add((rail, r));
                foreach (var cell in CellsOf(rail.X[r], rail.Z[r], rail.X[r + 1], rail.Z[r + 1]))
                {
                    if (!cells.TryGetValue(cell, out var list))
                        cells[cell] = list = [];
                    list.Add(id);
                }
            }

        Parallel.ForEach(_lines.Where(l => !l.Rail && !l.Mark && !l.Frozen), road =>
        {
            var hits = new List<(float S, float Y)>();
            var looked = new HashSet<int>();
            for (var i = 0; i + 1 < road.Count; i++)
            {
                if (road.Structures[i] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (road.X[i], road.Z[i], road.X[i + 1], road.Z[i + 1]);
                looked.Clear();
                foreach (var cell in CellsOf(ax, az, bx, bz))
                {
                    if (!cells.TryGetValue(cell, out var near))
                        continue;
                    foreach (var id in near)
                    {
                        // (A segment in more than one of the cells is met once.)
                        if (!looked.Add(id))
                            continue;
                        var (rail, r) = segments[id];
                        var (ux, uz, vx, vz) = (bx - ax, bz - az, rail.X[r + 1] - rail.X[r], rail.Z[r + 1] - rail.Z[r]);
                        var cross = ux * vz - uz * vx;
                        if (Math.Abs(cross) < 1e-6)
                            continue;
                        var (wx, wz) = (rail.X[r] - ax, rail.Z[r] - az);
                        var (t, q) = ((wx * vz - wz * vx) / cross, (wx * uz - wz * ux) / cross);
                        if (t < 0.0 || t >= 1.0 || q < 0.0 || q >= 1.0)
                            continue;
                        var y = rail.Y[r] + (rail.Y[r + 1] - rail.Y[r]) * (float)q;
                        hits.Add((road.S[i] + (road.S[i + 1] - road.S[i]) * (float)t, y + RailTopAbove - RoadSurfaceAbove));
                    }
                }
            }

            var length = road.S[road.Count - 1];
            foreach (var (at, target) in hits.OrderBy(h => h.S))
            {
                var here = 0f;
                for (var p = 0; p + 1 < road.Count; p++)
                    if (road.S[p + 1] >= at)
                    {
                        here = road.Y[p] + (road.Y[p + 1] - road.Y[p]) * Math.Clamp((at - road.S[p]) / MathF.Max(road.S[p + 1] - road.S[p], 1e-4f), 0f, 1f);
                        break;
                    }

                var change = target - here;
                // More than a little apart and it's not a level crossing at all: the track's on a bank or in a cutting, over or
                // under the road, and the road stays as it is.
                if (MathF.Abs(change) > LevelCrossingMost)
                    continue;
                var reach = MathF.Min(MathF.Max(25f, MathF.Abs(change) / RoadToRailGrade), MathF.Max(MathF.Min(at, length - at), 1f));
                for (var p = 0; p < road.Count; p++)
                {
                    var away = Math.Clamp(1f - MathF.Abs(road.S[p] - at) / reach, 0f, 1f);
                    road.Y[p] += change * away * away * (3f - 2f * away);
                }
            }
        });
    }

    /// The size of LevelRoadsAtRails' grid cells, metres.
    private const double CrossingCell = 64.0;

    /// The grid cells a segment's bounds cover.
    private static GridCells CellsOf(double ax, double az, double bx, double bz) => new(ax, az, bx, bz, CrossingCell);

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
    public void AdoptDrapes(IEnumerable<NetworkLine> previous)
    {
        var known = new Dictionary<long, NetworkLine>();
        foreach (var line in previous.Where(l => l.Mark))
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
                var fx = (float)((line.X[p] - dz / length * u) / Sp - gx0);
                var fz = (float)((line.Z[p] + dx / length * u) / Sp - gz0);
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

        ConformCore(list, heights, samples, tile.I * _quads, -(tile.J + 1) * _quads, tile);
    }

    /// Shapes a square of ground finer than the tiles' (the base heights at that spacing, samples a side, its first sample at
    /// (x0, z0) world metres, which are whole multiples of the spacing) to carry the lines as Conform does a tile's: where a
    /// tile's samples are too far apart for what's there (a wall's face against the bank it holds), the terrain draws this
    /// instead (Terrain's detail inserts).
    public void ConformFine(float[] heights, int samples, double x0, double z0, float spacing)
    {
        var size = _quads * _spacing;
        var extent = (samples - 1) * spacing;
        var list = new SortedSet<int>();
        for (var i = (int)Math.Floor(x0 / size); i <= (int)Math.Floor((x0 + extent) / size); i++)
            for (var k = (int)Math.Floor(z0 / size); k <= (int)Math.Floor((z0 + extent) / size); k++)
                if (_byTile.TryGetValue(new TileId(i, -k - 1), out var lines))
                    list.UnionWith(lines);
        if (list.Count == 0)
            return;

        t_fineSpacing = spacing;
        try
        {
            ConformCore([.. list], heights, samples, (int)Math.Round(x0 / spacing), (int)Math.Round(z0 / spacing), null);
        }
        finally
        {
            t_fineSpacing = 0f;
        }
    }

    private void ConformCore(List<int> list, float[] heights, int samples, int gx0, int gz0, TileId? tile)
    {
        // The ground as it came, before any shaping: where a bridge end's bank is cut back to its cone, a hillside's left standing.
        var natural = (float[])heights.Clone();


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

                    var ax = line.X[a] / Sp - gx0;
                    var az = line.Z[a] / Sp - gz0;
                    var bx = line.X[b] / Sp - gx0;
                    var bz = line.Z[b] / Sp - gz0;
                    var r = reach / Sp;
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
                    // (Not on round past an end where a bridge with a slope or a wall under it starts: the bank there is that end's,
                    // which shapes it to its wing walls. Fanned out round the end, a track's fill beside another's left a ridge and a
                    // trench beside the deck.)
                    bool Shaped(int point) => BridgeEndAt(line, point) is { Kind: BridgeEndKind.Slope or BridgeEndKind.Wall };
                    var stopAtA = a > 0 && line.Structures[a - 1] == Structure.Bridge && Shaped(a);
                    var stopAtB = b < line.Structures.Length && line.Structures[b] == Structure.Bridge && Shaped(b);
                    for (var z = z0; z <= z1; z++)
                    {
                        for (var x = x0; x <= x1; x++)
                        {
                            var along = length2 > 0 ? ((x - ax) * dx + (z - az) * dz) / length2 : 0.0;
                            if ((stopAtA && along < 0.0) || (stopAtB && along > 1.0))
                                continue;
                            var t = Math.Clamp(along, 0.0, 1.0);
                            var ex = ax + dx * t - x;
                            var ez = az + dz * t - z;
                            var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
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
            var run = MathF.Max(0f, d - (line.Rail ? line.Formation + Sp * 1.5f : line.Formation));
            if (run <= 0f ? d >= claimed[sample] : claimed[sample] < float.MaxValue)
                return;
            if (run <= 0f)
                claimed[sample] = d;
            var h = heights[sample];
            var shaped = Math.Clamp(h, target - run * FillSlope, target + run * CutSlope);
            var fade = Ease(Math.Clamp((reach - d) / 10f, 0f, 1f));
            heights[sample] = h + (shaped - h) * fade;
        });

        // No ballast is run out over the ground: the bed stops at its toe, a straight cut.
        if (tile is { } id)
        {
            _ballastSamples = samples;
            _ballast.TryRemove(id, out _);
        }

        // The nearest rail line on the far side of each sample from its nearest one.
        var across = new (float D, float Y, float Cos)[heights.Length];
        Array.Fill(across, (float.MaxValue, 0f, 0f));
        Visit(true, (index, _, sample, d, target, ex, ez, _) =>
        {
            var n = near[sample];
            var dot = n.Ex * ex + n.Ez * ez;
            if (index != n.Line && d < across[sample].D && dot < 0)
                across[sample] = (d, target, dot / MathF.Max(MathF.Sqrt((n.Ex * n.Ex + n.Ez * n.Ez) * (ex * ex + ez * ez)), 1e-6f));
        });

        // Between two rail lines near one another (a yard, tracks side by side, a wye's legs), the ground graded flat
        // from one's formation to the other's, as a yard is: each line's own cut and fill would leave a ridge or a
        // trench between them.
        for (var sample = 0; sample < heights.Length; sample++)
        {
            var (a, b) = (near[sample], across[sample]);
            if (a.Line < 0 || b.D == float.MaxValue || a.D + b.D > YardGap || MathF.Abs(a.Y - b.Y) > (a.D + b.D) * 0.06f)
                continue;
            // (Eased out as the two get further apart: past a track's end, the other's distance grows round it, and graded all or
            // nothing the ground stopped in a step where it no longer counted.)
            var graded = (a.Y * b.D + b.Y * a.D) / MathF.Max(a.D + b.D, 1e-3f);
            // (And as the other stops being squarely across from it, the ways to them no longer opposite: round a track's end.)
            var weight = Math.Clamp((YardGap - (a.D + b.D)) / YardFade, 0f, 1f) * Math.Clamp((-b.Cos - 0.2f) / 0.6f, 0f, 1f);
            heights[sample] += (graded - heights[sample]) * weight;
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
            var r = line.Formation / Sp;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                if (line.Structures[p] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (line.X[p] / Sp - gx0, line.Z[p] / Sp - gz0, line.X[p + 1] / Sp - gx0, line.Z[p + 1] / Sp - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var t = length2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0) : 0.0;
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
                        var sample = z * samples + x;

                        // (Not where a track's formation lies nearer: that's the track's ground. But a road's own paving, and a
                        // little past it, is always its own: another road's bank nearer by doesn't bury it.)
                        var paving = d <= PavedHalf(line, p) + PavingKept && (near[sample].Line < 0 || near[sample].D > _lines[near[sample].Line].Formation);
                        if (d > line.Formation || d >= under[sample] || (d > claimed[sample] + 1f && !paving))
                            continue;
                        under[sample] = d;
                        heights[sample] = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t - RoadClearance;
                    }
            }
        }

        // At a level crossing the road rises to the rails, but the track keeps its own flat ground beside it lower down, so the road
        // and its deck would stand on a slab. Lift that ground to the road and run fill slopes out from it; but never over the
        // ballast's own surface (so the gravel still shows), dipping to the bed's toe and rising from there at the fill slope.
        // Each sample takes its lift from the stretch of the road nearest it alone: a stretch's end reaches past it, and on a road
        // falling away from the crossing the higher stretch's end would heap the ground up through the lower one's surface.
        var lifts = new Dictionary<int, (float D, float Target)>();
        foreach (var index in list)
        {
            var line = _lines[index];
            if (line.Rail || line.Mark)
                continue;
            lifts.Clear();
            var r = (line.Formation + CrossingFillReach) / Sp;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                if (line.Structures[p] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (line.X[p] / Sp - gx0, line.Z[p] / Sp - gz0, line.X[p + 1] / Sp - gx0, line.Z[p + 1] / Sp - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                if (length2 <= 0)
                    continue;
                var length = Math.Sqrt(length2);
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var sample = z * samples + x;
                        var n = near[sample];
                        if (n.Line < 0)
                            continue;
                        var rail = _lines[n.Line];
                        if (n.D > rail.Formation + CrossingFillReach)
                            continue;
                        // Only a road heading across the track: one alongside it keeps to its own shaping.
                        var toward = MathF.Sqrt(n.Ex * n.Ex + n.Ez * n.Ez);
                        if (toward > 1e-3f && Math.Abs((dx * n.Ex + dz * n.Ez) / (length * toward)) < 0.5)
                            continue;

                        var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0);
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
                        if (d > line.Formation + CrossingFillReach)
                            continue;
                        var lift = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t - RoadClearance - MathF.Max(0f, d - line.Formation) * FillSlope;

                        // The track's bed runs on through the crossing, the crossing's surface built on it: the road's fill comes up
                        // to it, never over it, its shoulders nor its fade (the crossing's own embankment covers what's between).
                        var cap = UnderBed(rail, n.D, n.Y);
                        if (!lifts.TryGetValue(sample, out var best) || d < best.D)
                            lifts[sample] = (d, MathF.Min(lift, cap));
                    }
            }

            foreach (var (sample, (_, target)) in lifts)
                if (target > heights[sample])
                    heights[sample] = target;
        }

        ConformPads(heights, samples, gx0, gz0, near);

        TrenchWalls(list, heights, samples, gx0, gz0);
        // Every bridge's say first, then all of it at once: the least each sample may be (a bank brought up) and the most (kept clear
        // under a deck), the second winning. Shaped a deck at a time, two decks side by side undid each other, one cutting down
        // where the other had just brought the bank up.
        var least = new float[heights.Length];
        var most = new float[heights.Length];
        // (And of those, what's under a deck itself: kept clear of it over everything after, the bed's slopes too.)
        var deck = new float[heights.Length];
        Array.Fill(least, float.MinValue);
        Array.Fill(most, float.MaxValue);
        Array.Fill(deck, float.MaxValue);
        foreach (var index in list)
            if (Array.IndexOf(_lines[index].Structures, Structure.Bridge) >= 0)
                ClearBridges(index, heights, natural, least, most, deck, samples, gx0, gz0);
        for (var sample = 0; sample < heights.Length; sample++)
            heights[sample] = MathF.Min(MathF.Max(heights[sample], least[sample]), most[sample]);
        // Each slope end's ground as its shape has it, over all of that.
        var stamped = new bool[heights.Length];
        StampEnds(heights, natural, stamped, samples, gx0, gz0);
        // (Last: the bed's slopes over whatever the bank's been cut back to round a bridge's end, up to the deck's end and no further.)
        if (t_fineSpacing > 0f)
        {
            // (The ground's no longer laid over the bed's slopes, BallastFlanks: the bed's a straight cut, drawn as it is, and the
            // ground stays under it.)
            // (Not where an end's shape has the ground: it keeps clear of its own deck.)
            for (var sample = 0; sample < heights.Length; sample++)
                if (!stamped[sample])
                    heights[sample] = MathF.Min(heights[sample], deck[sample]);
        }

    }

    /// How far over a ballast bed's slope the ground's laid, so it's drawn and the bed's own slope under it isn't.
    private const float FlankOver = 0.10f;

    /// In ground shaped finer than the tiles' (ConformFine), each grounded track's ballast slopes are the ground's own: from just past
    /// the top's shoulder out to the toe it's brought up to the bed's section (NetworkMeshes.BedSection), a hair over it, and the
    /// terrain lays the ballast's rock into it there (BallastAt). Two beds side by side meet as one heap, the higher of the two, not two
    /// meshes through each other; a bed's end, or where it runs onto a bridge, is ground, not a sheet over nothing. (The tiles' ground,
    /// drawn further off, is too coarse to: the bed's own slopes are drawn there.)
    private void BallastFlanks(List<int> list, float[] heights, int samples, int gx0, int gz0)
    {
        foreach (var index in list)
        {
            var line = _lines[index];
            if (!line.Rail || line.Mark || line.Embedded || line.Count < 2)
                continue;
            var section = NetworkMeshes.BedSection(line, false, 0f, 0f, 0f, 0f);
            // (The right half, from the shoulder out to the toe.)
            var half = line.Bed == TrackBed.Sunken ? line.BallastHalfTop - 0.7f : line.BallastHalfTop;
            var right = section.Where(q => q.U >= half - 1e-3f).ToArray();
            // (Its toe: the section's last three points are the toe, the taper under the ground and the fade's far edge, which the
            // ground's own ballast blend has; only the heap itself, out to the toe, is laid.)
            var toe = right[^3].U;
            right = right[..^2];
            float Profile(float u)
            {
                for (var k = 0; k + 1 < right.Length; k++)
                    if (u <= right[k + 1].U)
                    {
                        var t = Math.Clamp((u - right[k].U) / MathF.Max(right[k + 1].U - right[k].U, 1e-4f), 0f, 1f);
                        return right[k].Y + (right[k + 1].Y - right[k].Y) * t;
                    }

                return right[^1].Y;
            }

            var r = (toe + 0.5f) / Sp;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                if (line.Structures[p] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (line.X[p] / Sp - gx0, line.Z[p] / Sp - gz0, line.X[p + 1] / Sp - gx0, line.Z[p + 1] / Sp - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                if (x0 > x1 || z0 > z1)
                    continue;
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                if (length2 <= 0)
                    continue;
                // (Past a segment's end only where the bed goes on, or rounds its end as the bed's cap does.)
                var openA = p == 0 || line.Structures[p - 1] == Structure.Ground;
                var openB = p + 2 >= line.Count || (p + 1 < line.Structures.Length && line.Structures[p + 1] == Structure.Ground);
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var along = ((x - ax) * dx + (z - az) * dz) / length2;
                        // (Past a segment's end only at the line's own ends, round its cap: elsewhere the next segment's, and
                        // measured to its end a segment's slopes reached on round a bridge's end, heaped under the deck.)
                        if ((along < -0.02 && (p > 0 || !openA)) || (along > 1.02 && (p + 2 < line.Count || !openB)))
                            continue;
                        var t = Math.Clamp(along, 0.0, 1.0);
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
                        if (d <= half + 0.15f || d >= toe)
                            continue;
                        var q = (int)Math.Round(p + t);
                        var y = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t + line.SinkOf(q) + Profile(d) + FlankOver;
                        var sample = z * samples + x;
                        if (heights[sample] < y)
                            heights[sample] = y;
                    }
            }
        }
    }

    /// Keeps the ground below a bridge's deck: it may rise from under the deck at the cut slope outward, and the requirement
    /// eases off over the last few metres to each end, where a deck meets the ground anyway. Mapped ends and lidar rarely agree
    /// to the metre, so a deck drawn between its ends can otherwise have the ground come up through it.
    private void ClearBridges(int index, float[] heights, float[] natural, float[] least, float[] most, float[] deck, int samples, int gx0, int gz0)
    {
        var line = _lines[index];
        var (half, depth) = line.Rail ? (line.BallastHalfTop + 0.9f, 1.8f) : (line.Width * 0.5f + 0.4f, 1.4f);
        var ends = BridgeEndsOf(line);
        // (Out far enough for a slope end's corners, which run off its sides at SpillRun to one for the deck's height.)
        // (And for the bank coned round its wing walls, from the track's level down at the fill slope, easing out over ConeEase.)
        var corner = ends.Where(e => e.Kind == BridgeEndKind.Slope).Select(e => (line.Y[e.Point] - e.Floor) * SpillRun + 4f + ConeEase).DefaultIfEmpty(0f).Max();
        var reachMetres = half + MathF.Max(BridgeClearReach, corner);
        var r = reachMetres / Sp;
        for (var from = 0; from < line.Structures.Length; from++)
        {
            if (line.Structures[from] != Structure.Bridge)
                continue;
            var to = from;
            while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                to++;

            var (start, end) = (line.S[from], line.S[to + 1]);
            var bank = BridgeBank;
            var startEnd = ends.FirstOrDefault(e => e.Point == from && e.Start);
            var endEnd = ends.FirstOrDefault(e => e.Point == to + 1 && !e.Start);
            for (var p = from; p <= to; p++)
            {
                var (ax, az, bx, bz) = (line.X[p] / Sp - gx0, line.Z[p] / Sp - gz0, line.X[p + 1] / Sp - gx0, line.Z[p + 1] / Sp - gz0);
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var rawT = length2 > 0 ? ((x - ax) * dx + (z - az) * dz) / length2 : 0.0;
                        var t = Math.Clamp(rawT, 0.0, 1.0);
                        var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                        var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
                        var s = line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t;
                        var y = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t;
                        var sample = z * samples + x;
                        // (Behind the deck's end: the approach, on its own embankment. A sample off a segment's end is the next
                        // segment's to shape, or past the deck's end, not the deck's at all: measured to the end of a segment further
                        // in, a sample behind the deck's end was kept down as if under it, and cut the approach's bank away.)
                        if ((rawT < -0.02 && p > from) || (rawT > 1.02 && p < to))
                            continue;
                        var behind = (p == from && rawT < 0.0) || (p == to && rawT > 1.0);
                        // The nearer end, and how far into the gap from it.
                        var (near, into) = s - start <= end - s ? (startEnd, s - start) : (endEnd, end - s);
                        var ease = near.Kind == BridgeEndKind.Joined ? 1f : Math.Clamp((into - bank) / BridgeEndEase, 0f, 1f);
                        if (near.Kind == BridgeEndKind.Wall && near.Under is not null)
                        {
                            // Behind a wall's face the deck sits on the abutment, not over a gap (on a skew, the wedge between its square
                            // end and the wall along the road): the backfill comes up under it, kept only under its beams.
                            var (wx, wz) = (x * (double)Sp + gx0 * (double)Sp, z * (double)Sp + gz0 * (double)Sp);
                            if (near.FromUnder(wx, wz) > WallFaceOff(near))
                            {
                                // (Only under the deck itself; past its end, the approach's own ground.)
                                var seat = y - BridgePlanner.DeckDepth(line) - BridgeClearance;
                                if (s > start && s < end && d <= BridgePlanner.DeckHalf(line) + 0.3f)
                                {
                                    most[sample] = MathF.Min(most[sample], seat);
                                    deck[sample] = MathF.Min(deck[sample], seat);
                                }
                                continue;
                            }
                        }

                        var limit = y - (depth + BridgeClearance) * ease + MathF.Max(0f, d - half) * CutSlope;
                        // A slope end's ground is its shape's (BridgeEndShape, NetworkLines.StampEnds): behind its deck's end, nothing
                        // here; under it, only kept clear of the deck.
                        if (near.Kind == BridgeEndKind.Slope && behind)
                            continue;

                        most[sample] = MathF.Min(most[sample], limit);
                        if (!behind)
                            deck[sample] = MathF.Min(deck[sample], limit);
                    }
            }

            from = to;
        }
    }

    /// How far past a road's paving its own ground is kept at its own height, whatever else is near.
    public const float PavingKept = 1.2f;

    /// Half a road's paved width at a point, its surface's shift out to one side and all.
    public static float PavedHalf(NetworkLine line, int point) => line.Width * 0.5f + (point < line.Shift.Length ? MathF.Abs(line.Shift[point]) : 0f);

    private const float BridgeClearance = 0.3f;
    /// How far past a deck's side a slope end's plane carries on before the ground may rise from it.
    private const float SlopeCarriesOn = 4f;
    private const float BridgeClearReach = 8f;
    /// How far, at the edge of what a slope end's bank shapes beside its deck, it eases back to the ground as it was.
    private const float ConeEase = 6f;
    /// How thick a slope end's wing walls are, standing just outside the deck's edge.
    public const float SlopeWingThick = 0.6f;
    /// How far short of YardGap the ground between two tracks is graded wholly, easing off to none at it.
    private const float YardFade = 6f;
    private const float BridgeEndEase = 3f;
    /// How far under each end of a deck the bank stays as it is: past the abutment, whose seat's there, before the ground's cut away.
    private const float BridgeBank = 0.5f;

    /// How far under a road's formation height the ground beneath it lies.
    private const float RoadClearance = 0.02f;

    /// How far past a road's formation, and a track's, the ground's lifted to meet a road raised to a level crossing; and how far
    /// under the ballast's surface that ground stays.
    private const float CrossingFillReach = 6f;
    private const float CrossingUnderBallast = 0.04f;

    /// The highest the ground may come at d across from a track's centre (its formation at y): just under its ballast bed, out
    /// over its top and down its shoulders to the toe, the shoulder rounded as the bed's own (NetworkMeshes.BedSection), then
    /// level on under the bed's fade and a grid square's diagonal (or `level` grid squares) past it, and only then rising, no
    /// steeper than a fill's
    /// slope. (Round a crossing, whose own embankment covers the ground there, half that: a wider level stretch leaves a pit
    /// between the ballast and the road's fill.) The ground's drawn
    /// as flat triangles between samples a grid square apart: a sample let rise right from the toe would carry the triangle from
    /// it up through the bed's shoulder, nearer in. Whatever's built up round a track (a crossing's road and fill) comes up to the
    /// bed, never over it, so the bed shows whole wherever it runs.
    private float UnderBed(NetworkLine rail, float d, float y, float level = 1.42f)
    {
        var flank = MathF.Max(0.35f, rail.BedHeight * NetworkLine.BallastSlope * NetworkLine.BedRun);
        var toe = rail.BallastHalfTop + flank;
        // (Taken half a grid square further out than d, where the shoulder's lower: so a triangle from a sample on the shoulder to
        // one further out stays under the shoulder's hollow toward its toe, between them.)
        var t = Math.Clamp((d + Sp * 0.71f - rail.BallastHalfTop) / flank, 0f, 1f);
        var bed = rail.BedHeight * (1f - t);
        // (Level on out under the whole of the bed's fade too, the loose stone laid over the ground past its toe: ground come up
        // through it buries it, and the fade, its shadow looked up a little under its surface, goes dark.)
        return y - CrossingUnderBallast + MathF.Max(bed, (d - toe - NetworkLine.BedFade - Sp * level) * FillSlope);
    }

    /// How far under a track's drawn ballast bed (its rock and the fade past it) the ground's kept, where it's kept off it: more
    /// than the fade looks its shadow up under its surface (line.frag), or ground just under it still shades it.
    public const float BedClear = 0.08f;

    /// The highest the ground may come at (x, z) and stay BedClear under the nearest track's ballast bed as it's drawn
    /// (NetworkMeshes.BedSection: its rock, its shoulders and the fade past them), or null where no track's bed reaches.
    public float? BedClearance(double x, double z)
    {
        float? best = null;
        var bestD = float.MaxValue;
        foreach (var line in _lines)
        {
            if (!line.Rail || line.Embedded || line.Count < 2 || x < line.MinX - 12 || x > line.MaxX + 12 || z < line.MinZ - 12 || z > line.MaxZ + 12)
                continue;
            for (var p = 0; p + 1 < line.Count; p++)
            {
                if (line.Structures[p] != Structure.Ground)
                    continue;
                var (ax, az, bx, bz) = (line.X[p], line.Z[p], line.X[p + 1], line.Z[p + 1]);
                var (dx, dz) = (bx - ax, bz - az);
                var length2 = dx * dx + dz * dz;
                if (length2 < 1e-9)
                    continue;
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0);
                var (ex, ez) = (x - (ax + dx * t), z - (az + dz * t));
                var d = (float)Math.Sqrt(ex * ex + ez * ez);
                if (d >= bestD || d > 12f)
                    continue;
                var length = Math.Sqrt(length2);
                // Across it to its right, as the bed's swept (Leg.At: right is (-fz, fx)).
                var u = (float)((ex * -dz + ez * dx) / length);
                var s = line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t;
                var section = NetworkMeshes.BedSection(line, false, 0f, 0f, 0f, line.SinkAt(s));
                var n = section.Length;
                if (u <= section[0].U || u >= section[n - 1].U)
                    continue;
                var h = section[n - 1].Y;
                for (var i = 0; i + 1 < n; i++)
                    if (u <= section[i + 1].U)
                    {
                        var span = section[i + 1].U - section[i].U;
                        h = span < 1e-5f ? section[i + 1].Y : section[i].Y + (section[i + 1].Y - section[i].Y) * (u - section[i].U) / span;
                        break;
                    }

                // (The fade's laid over the taper, which goes in under it: the surface is the higher of the two there.)
                if (u < section[2].U)
                    h = MathF.Max(h, section[0].Y + (section[2].Y - section[0].Y) * (u - section[0].U) / (section[2].U - section[0].U));
                else if (u > section[n - 3].U)
                    h = MathF.Max(h, section[n - 3].Y + (section[n - 1].Y - section[n - 3].Y) * (u - section[n - 3].U) / (section[n - 1].U - section[n - 3].U));
                bestD = d;
                best = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t + h - BedClear;
            }
        }

        return best;
    }

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
