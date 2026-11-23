using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The route's lines as they come out finished, kept per cell (a cell is a tile, 2 km square): Cells/I_J.cell for every cell anything
/// is in, and Cells/index.bin saying which there are, what they were built from and by which versions of the passes. A route opens
/// from them rather than working its lines out again, as long as nothing they were worked out from has changed (the inputs stamp,
/// StampOf), and the versions and the layouts of what's in them are this build's. Unlike the compiled route before it, a new build of
/// the game doesn't throw them away: only BuildVersion (bumped by hand, when a change to laying, grading or shaping should show in
/// cells built already), FormatVersion (the files' layout), or a change to the layout of a type they hold (LayoutOf, checked type by
/// type) does. See docs/cell-routes.md, "Cell format".
///
/// What's in a cell:
/// - Pieces: each line's runs of points inside the cell (a line leaving and coming back is two pieces), with its identity (index, key,
///   chain), its first point's index and every per-point (and per-segment) array's slice. The piece holding point 0 is the line's head:
///   the line's per-line fields are written with it.
/// - What's anchored in the cell: turnouts, layouts, crossing pads and assemblies, bridges and their ends, junction heights and pins by
///   node, the lines' tile indexes by tile (see Rules), each with its place (ordinal) in the route-wide list, set or dictionary it's in.
/// References between them (a turnout's lines, a turnout zone's turnout, a bridge's members) are written as (kind, index): the line's
/// index or the thing's place in its list, resolved once every cell's read. Indices are stable within one written set of cells, which
/// the index's hashes keep together: a cell from another set doesn't match its hash and nothing's read.
///
/// HB_CELLCHECK=1: opened from the cells and worked out afresh too, and the two compared, everything, bit for bit (CellStore.Check.cs).
/// HB_NOCELLS=1: never read (still written). HB_CELLMEASURE=1: when written, every cell compressed every way there is, and timed.
internal static partial class CellStore
{
    /// The layout of the files. Bumped when it changes; cells of another are never read.
    public const int FormatVersion = 5;

    /// The passes' build (laying, grading, shaping): bumped by hand when a change to them should show in cells already built. Cells of
    /// another are built again.
    public const int BuildVersion = 2;

    private const int IndexMagic = 0x58444E43; // "CNDX"
    private const int CellMagic = 0x4C4C4543; // "CELL"

    /// How the blocks are compressed (the reader takes what the file says), and whether arrays are written byte plane by byte plane
    /// (all their first bytes, then all their second...), which compresses smooth numbers far better. See "Cell format" for the
    /// measurements these were chosen by.
    private const Codec DefaultCodec = Codec.Brotli;
    private const int BrotliQuality = 5;
    private const bool DefaultShuffle = true;

    public static readonly bool Check = Environment.GetEnvironmentVariable("HB_CELLCHECK") == "1";
    private static readonly bool Off = Environment.GetEnvironmentVariable("HB_NOCELLS") == "1";
    private static readonly bool Measure = Environment.GetEnvironmentVariable("HB_CELLMEASURE") == "1";

    private enum Codec : byte { ZLib = 1, Brotli = 2 }

    public static string FolderOf(RouteInfo route) => Path.Combine(route.Folder, "Cells");
    private static string IndexPath(RouteInfo route) => Path.Combine(FolderOf(route), "index.bin");
    private static string CellPath(RouteInfo route, TileId id) => Path.Combine(FolderOf(route), $"{id}.cell");
    private static readonly Regex CellName = new(@"^-?\d+_-?\d+\.cell$", RegexOptions.Compiled);

    // --- The inputs stamp. ---

    /// The files the lines are worked out from, read whole into the stamp (the network's too: RouteNetwork.StampFiles, its index alone when
    /// it's stored per cell).
    private static readonly string[] Inputs = ["Route.json", "TrackEdits.bin.gz", "Bridges.json", "Switches.json", "SwitchCurves.json"];

    /// What the lines are worked out from: the input files, the solved track (by size and time), each built tile's heights (by size and
    /// time), and the level crossings. Not the build of the game (see FormatVersion, BuildVersion and LayoutOf).
    public static ulong StampOf(RouteInfo route, IEnumerable<TileId> built, IEnumerable<PlacedObject> objects)
    {
        var hash = new WordHash();
        foreach (var name in Inputs.Concat(RouteNetwork.StampFiles(route)))
        {
            var path = Path.Combine(route.Folder, name);
            if (!File.Exists(path))
            {
                hash.Add(-1);
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            hash.Add(bytes.Length);
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            foreach (var word in words)
                hash.Add(word);
            for (var i = words.Length * 8; i < bytes.Length; i++)
                hash.Add((int)bytes[i]);
        }

        foreach (var id in built.OrderBy(t => t.I).ThenBy(t => t.J))
        {
            hash.Add(id.I);
            hash.Add(id.J);
            var heights = new FileInfo(Path.Combine(route.TileFolder(id), "Heights.bin.gz"));
            hash.Add(heights.Exists ? heights.Length : -1);
            hash.Add(heights.Exists ? heights.LastWriteTimeUtc.Ticks : 0);
        }

        foreach (var o in objects.Where(o => o.Kind == PlacedKind.GradeCrossing).OrderBy(o => o.Id))
        {
            hash.Add(o.Id);
            hash.Add(BitConverter.DoubleToUInt64Bits(o.Lat));
            hash.Add(BitConverter.DoubleToUInt64Bits(o.Lon));
            hash.Add(BitConverter.DoubleToUInt64Bits(o.Heading));
            hash.Add(BitConverter.DoubleToUInt64Bits(o.Width));
        }

        return hash.Value;
    }

    // --- Where each of NetworkLines' fields goes. ---

    private enum Place
    {
        /// _lines: as pieces.
        Lines,
        /// Spread over the cells: a list's items, a set's or dictionary's entries, each in the cell of its anchor (none: the index's).
        Spread,
        /// Small and route-wide: in the index.
        Route,
        /// Put back from what the route's opened with (Restore).
        Restored,
        /// Not needed once built (timing a build), or worked out again when first asked for: left at null.
        Dropped,
    }

    /// The cell an item (a list's or set's: key), or a dictionary's entry (key, value), belongs in; null: none, the index's.
    private delegate TileId? Anchor(Context c, object? key, object? value);

    private sealed record Rule(Place Place, Anchor? Anchor = null, bool Nested = false);

    private static string Auto(string property) => $"<{property}>k__BackingField";

    private static TileId? ByTile(Context c, object? key, object? value) => (TileId)key!;
    private static TileId? ByLine(Context c, object? key, object? value) => c.Head((NetworkLine)key!);
    private static TileId? ByNode(Context c, object? key, object? value) => c.Node(key is long node ? node : (long)((ITuple)key!)[0]!);
    private static TileId? ByPlace(Context c, object? key, object? value) => c.Place(key!);

    /// Every field of NetworkLines, and where it goes. A field not here is kept in the index (and said so when written): add it.
    private static readonly Dictionary<string, Rule> Rules = new()
    {
        ["_lines"] = new(Place.Lines),

        // The lines' tile indexes (the order they shape a tile in, which samples of a tile they reach), by tile.
        ["_byTile"] = new(Place.Spread, ByTile),
        ["_pointsByTile"] = new(Place.Spread, ByTile),
        ["_ballast"] = new(Place.Spread, ByTile),
        // (Every tile's ground key, worked out again at every open, PrepareShapingKeys: a cache, kept nowhere.)
        ["_preparedKeys"] = new(Place.Dropped),

        // Things with a place of their own.
        [Auto(nameof(NetworkLines.Turnouts))] = new(Place.Spread, ByPlace),
        [Auto(nameof(NetworkLines.Layouts))] = new(Place.Spread, ByPlace),
        ["_pads"] = new(Place.Spread, ByPlace),
        [Auto(nameof(NetworkLines.CrossingAssemblies))] = new(Place.Spread, ByPlace),
        [Auto(nameof(NetworkLines.Bridges))] = new(Place.Spread, ByPlace),
        ["_endShapes"] = new(Place.Spread, ByPlace),
        ["_detailRegions"] = new(Place.Spread, ByPlace),
        ["_detailCells"] = new(Place.Spread, (c, k, _) => c.Detail((ITuple)k!)),
        ["_wallSpans"] = new(Place.Spread, (c, k, _) => c.Point((NetworkLine)((ITuple)k!)[0]!, (int)((ITuple)k)[1]!)),
        ["_railsNear"] = new(Place.Spread, (c, k, _) => c.Point((NetworkLine)((ITuple)k!)[0]!, (int)((ITuple)k)[1]!)),
        ["_crossings"] = new(Place.Spread, (c, k, _) => c.CellOf((double)((ITuple)k!)[1]!, (double)((ITuple)k)[2]!)),
        ["_railCells"] = new(Place.Spread, (c, k, _) => c.CellOf(((int)((ITuple)k!)[0]! + 0.5) * 25.0, ((int)((ITuple)k)[1]! + 0.5) * 25.0)),

        // Per line: with the line's head (or where on it they are).
        ["_shapingHashes"] = new(Place.Spread, ByLine),
        ["_linePins"] = new(Place.Spread, ByLine),
        ["_pinnedPoints"] = new(Place.Spread, ByLine),
        ["_wallsBeside"] = new(Place.Spread, ByLine),
        ["_bridgeEnds"] = new(Place.Spread, (c, k, v) => v is List<BridgeEnd> { Count: > 0 } ends ? c.Point((NetworkLine)k!, ends[0].Point) : c.Head((NetworkLine)k!)),
        ["_extended"] = new(Place.Spread, (c, k, _) => c.Point((NetworkLine)((ITuple)k!)[0]!, (int)((ITuple)k)[1]!)),
        ["_surfaceShifts"] = new(Place.Spread, (c, k, _) => c.Point((NetworkLine)((ITuple)k!)[0]!, (int)((ITuple)k)[1]!)),
        ["_sideBySide"] = new(Place.Spread, (c, k, _) => c.Along((NetworkLine)((ITuple)k!)[0]!, (float)((ITuple)k)[1]!)),
        ["_continuing"] = new(Place.Spread, (c, k, _) => c.Point(c.Lines[(int)((ITuple)k!)[0]!], (int)((ITuple)k)[1]!)),
        ["_byChain"] = new(Place.Spread, (c, _, v) => v is List<NetworkLine> { Count: > 0 } chain ? c.Head(chain[0]) : null),
        ["_seenChains"] = new(Place.Spread, (c, k, _) => c.Chains.TryGetValue((long)k!, out var line) ? c.Head(line) : null),
        ["_needGround"] = new(Place.Spread, (c, k, _) => (int)k! < c.Lines.Count ? c.Head(c.Lines[(int)k]) : null),

        // By node: where the node is (on a line).
        [Auto(nameof(NetworkLines.Links))] = new(Place.Spread, ByNode),
        [Auto(nameof(NetworkLines.CrossoverProblems))] = new(Place.Spread, ByNode),
        [Auto(nameof(NetworkLines.SwitchOptions))] = new(Place.Spread, ByNode),
        [Auto(nameof(NetworkLines.Fitted))] = new(Place.Spread, ByNode),
        ["_nodePins"] = new(Place.Spread, ByNode),
        ["_spreadNodes"] = new(Place.Spread, ByNode),
        ["_forks"] = new(Place.Spread, ByNode),
        ["_widestAt"] = new(Place.Spread, ByNode),
        ["_junctionNodes"] = new(Place.Spread, ByNode),
        ["_roadEnds"] = new(Place.Spread, ByNode),
        ["_railAt"] = new(Place.Spread, ByNode),
        ["_junctionRounds"] = new(Place.Spread, ByNode, Nested: true),

        // Route-wide, and small.
        ["_spacing"] = new(Place.Route),
        ["_quads"] = new(Place.Route),
        ["_ballastSamples"] = new(Place.Route),
        ["_handSet"] = new(Place.Route),
        ["Sizes"] = new(Place.Route),
        ["Curves"] = new(Place.Route),
        ["_bridgeEndsLock"] = new(Place.Route),
        ["_detailLock"] = new(Place.Route),
        ["_endShapesLock"] = new(Place.Route),

        // What the route's opened with anyway (Restore).
        ["Ways"] = new(Place.Restored),
        ["Shapes"] = new(Place.Restored),
        ["BuiltTiles"] = new(Place.Restored),
        ["_crossingObjects"] = new(Place.Restored),
        ["Before"] = new(Place.Restored),
        [Auto(nameof(NetworkLines.BridgeEdits))] = new(Place.Restored),
        [Auto(nameof(NetworkLines.DetailCache))] = new(Place.Restored),

        // Which cells a window of the lines is complete in (CellStore.Read): set as it's read, carried by a merge.
        [Auto(nameof(NetworkLines.Window))] = new(Place.Dropped),

        // LiftBridges' grid of the lines' segments: only ever used inside FinishProfiles, which lets it go first.
        ["_liftCells"] = new(Place.Dropped),

        // Timing a build (HB_PROFILELAPS), null once it's done.
        ["_lapWatch"] = new(Place.Dropped),
        ["_lapName"] = new(Place.Dropped),
        ["_laps"] = new(Place.Dropped),
    };

    /// NetworkLine's fields left out: its run boxes (made again when first asked for, from its points), and the last rebuild's line a
    /// frozen one's a copy of (a line read is nobody's copy, as a line laid afresh isn't).
    private static readonly HashSet<string> LineDropped = ["_runBoxes", "_origin"];

    private static readonly FieldInfo[] RootFields = GraphSnapshot.FieldsOf(typeof(NetworkLines));
    private static readonly FieldInfo[] LineFields = GraphSnapshot.FieldsOf(typeof(NetworkLine));

    /// Where every field goes, hashed: cells written by a build that put a field elsewhere aren't read (the index says).
    private static readonly ulong RulesHash = HashRules();

    private static ulong HashRules()
    {
        var hash = new WordHash();
        foreach (var f in RootFields.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var ch in f.Name)
                hash.Add((int)ch);
            hash.Add((int)RuleOf(f).Place);
            hash.Add(RuleOf(f).Nested);
        }

        foreach (var name in LineDropped.Order(StringComparer.Ordinal))
            foreach (var ch in name)
                hash.Add((int)ch);
        return hash.Value;
    }

    private static Rule RuleOf(FieldInfo f) => Rules.GetValueOrDefault(f.Name) ?? new Rule(Place.Route);

    /// The fields spread over the cells, in the order they're numbered in the files.
    private static readonly FieldInfo[] SpreadFields = [.. RootFields.Where(f => RuleOf(f).Place == Place.Spread).OrderBy(f => f.Name, StringComparer.Ordinal)];

    /// Whether a spread field's items are objects others refer to (a turnout zone to its turnout): made before any cell's read, so a
    /// reference to one in another cell finds it.
    /// Whether a spread dictionary's values are objects of this game's own that could be one under two keys (a crossover's two switches'
    /// options): made before any cell's read too, so one under two keys in two cells comes back as one.
    private static bool RegisteredValues(FieldInfo f) =>
        !RuleOf(f).Nested && f.FieldType.IsGenericType && f.FieldType.GetGenericArguments() is { Length: 2 } args && !args[1].IsValueType &&
        args[1].Assembly == typeof(CellStore).Assembly;

    private static bool Registered(FieldInfo f) =>
        !RuleOf(f).Nested && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) && f.FieldType.GetGenericArguments()[0] is { IsValueType: false } item && item != typeof(string);

    // --- Where things are. ---

    private sealed class Context
    {
        public required double Size;
        public required float Spacing;
        public required IReadOnlyList<NetworkLine> Lines;
        public readonly Dictionary<long, (double X, double Z)> Nodes = [];
        public readonly Dictionary<long, NetworkLine> Chains = [];

        public TileId CellOf(double x, double z) => new((int)Math.Floor(x / Size), -(int)Math.Floor(z / Size) - 1);

        public TileId? Point(NetworkLine line, int point) =>
            line.Count == 0 ? null : CellOf(line.X[Math.Clamp(point, 0, line.Count - 1)], line.Z[Math.Clamp(point, 0, line.Count - 1)]);

        public TileId? Head(NetworkLine line) => Point(line, 0);

        public TileId? Along(NetworkLine line, float s)
        {
            if (line.Count == 0)
                return null;
            var at = Array.BinarySearch(line.S, s);
            return Point(line, at >= 0 ? at : ~at);
        }

        public TileId? Node(long node) => Nodes.TryGetValue(node, out var at) ? CellOf(at.X, at.Z) : null;

        public TileId? Detail(ITuple key) =>
            CellOf(((int)key[0]! + 0.5) * NetworkLines.CellQuads * Spacing, ((int)key[1]! + 0.5) * NetworkLines.CellQuads * Spacing);

        /// A thing's own place: its X and Z (a turnout, layout, pad, assembly, bridge), a slope end's Ex and Ez, a detail region's cell.
        public TileId? Place(object thing)
        {
            if (thing is NetworkLines.DetailRegion region)
                return CellOf((region.Cx + 0.5) * NetworkLines.CellQuads * Spacing, (region.Cz + 0.5) * NetworkLines.CellQuads * Spacing);
            var (x, z) = PlaceOf.GetOrAdd(thing.GetType(), static t => (Member(t, "X") ?? Member(t, "Ex"), Member(t, "Z") ?? Member(t, "Ez")));
            return x is null || z is null ? null : CellOf(x(thing), z(thing));
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (Func<object, double>?, Func<object, double>?)> PlaceOf = new();

        private static Func<object, double>? Member(Type t, string name)
        {
            if (t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { } p && p.PropertyType == typeof(double))
                return o => (double)p.GetValue(o)!;
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.Public) is { } f && f.FieldType == typeof(double))
                return o => (double)f.GetValue(o)!;
            return null;
        }
    }

    private static Context ContextOf(NetworkLines lines)
    {
        var spacing = (float)RootFields.First(f => f.Name == "_spacing").GetValue(lines)!;
        var quads = (int)RootFields.First(f => f.Name == "_quads").GetValue(lines)!;
        var c = new Context { Size = quads * (double)spacing, Spacing = spacing, Lines = lines.Lines };
        foreach (var line in lines.Lines)
        {
            if (line.IsAbsent)
                continue;
            c.Chains.TryAdd(line.Chain, line);
            foreach (var (node, point) in line.Nodes)
                if (point >= 0 && point < line.Count)
                    c.Nodes.TryAdd(node, (line.X[point], line.Z[point]));
        }

        return c;
    }

    // --- Lines' arrays, as pieces. ---

    /// How a line's array is cut into its pieces: one slice of Stride items per point, or one item per segment (point i to i + 1, with
    /// the piece holding point i). An array of any other length goes whole with the line's head.
    private enum Cut : byte { None = 0, Points = 1, Segments = 2 }

    private static (Cut Cut, int Stride) CutOf(Array? array, int count)
    {
        if (array is null || array.Length == 0)
            return (Cut.None, 0);
        if (count > 0 && array.Length % count == 0 && array.Length / count <= 8)
            return (Cut.Points, array.Length / count);
        if (count > 1 && array.Length == count - 1)
            return (Cut.Segments, 1);
        return (Cut.None, 0);
    }

    /// NetworkLine's fields that may be cut into pieces: arrays of plain numbers.
    private static readonly int[] Cuttable = [.. Enumerable.Range(0, LineFields.Length).Where(i => LineFields[i].FieldType.IsArray && GraphSnapshot.IsBlittable(LineFields[i].FieldType.GetElementType()!))];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> Sizes = new();

    private static int SizeOf(Type t) => Sizes.GetOrAdd(t, static t => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!);

    private static Span<byte> BytesOf(Array array) =>
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(array), array.Length * SizeOf(array.GetType().GetElementType()!));

    private readonly record struct Slice(int Field, Cut Cut, int Stride);

    private static (int From, int Count) Range(Slice s, int first, int count, int lineCount) => s.Cut == Cut.Points
        ? (first * s.Stride, count * s.Stride)
        : (first, Math.Max(0, Math.Min(first + count, lineCount - 1) - first));

    private static void WriteSlice(BinaryWriter w, Array array, Slice s, int first, int count, int lineCount, bool shuffle)
    {
        var size = SizeOf(array.GetType().GetElementType()!);
        var (from, n) = Range(s, first, count, lineCount);
        var bytes = BytesOf(array).Slice(from * size, n * size);
        if (!shuffle || size == 1)
        {
            w.Write(bytes);
            return;
        }

        var planes = new byte[bytes.Length];
        for (var b = 0; b < size; b++)
            for (var i = 0; i < n; i++)
                planes[b * n + i] = bytes[i * size + b];
        w.Write(planes);
    }

    private static void ReadSlice(Stream r, Array array, Slice s, int first, int count, int lineCount, bool shuffle, ref byte[] buffer)
    {
        var size = SizeOf(array.GetType().GetElementType()!);
        var (from, n) = Range(s, first, count, lineCount);
        var bytes = BytesOf(array).Slice(from * size, n * size);
        if (!shuffle || size == 1)
        {
            r.ReadExactly(bytes);
            return;
        }

        if (buffer.Length < bytes.Length)
            buffer = new byte[Math.Max(bytes.Length, buffer.Length * 2)];
        r.ReadExactly(buffer.AsSpan(0, bytes.Length));
        for (var b = 0; b < size; b++)
            for (var i = 0; i < n; i++)
                bytes[i * size + b] = buffer[b * n + i];
    }

    // --- Compression. ---

    private static byte[] Compress(ReadOnlySpan<byte> raw, Codec codec, int quality = BrotliQuality, CompressionLevel level = CompressionLevel.Optimal)
    {
        if (codec == Codec.Brotli)
        {
            var packed = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
            if (!BrotliEncoder.TryCompress(raw, packed, out var written, quality, 22))
                throw new IOException("Cells: couldn't compress");
            return packed[..written];
        }

        using var memory = new MemoryStream(raw.Length / 3 + 64);
        using (var z = new ZLibStream(memory, level, true))
            z.Write(raw);
        return memory.ToArray();
    }

    private static Stream Decompress(byte[] file, int offset, int length, Codec codec)
    {
        var packed = new MemoryStream(file, offset, length, false);
        return codec == Codec.Brotli ? new BrotliStream(packed, CompressionMode.Decompress) : new ZLibStream(packed, CompressionMode.Decompress);
    }

    private static ulong HashOf(ReadOnlySpan<byte> bytes)
    {
        var hash = new WordHash();
        hash.Add(bytes.Length);
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        foreach (var word in words)
            hash.Add(word);
        for (var i = words.Length * 8; i < bytes.Length; i++)
            hash.Add((int)bytes[i]);
        return hash.Value;
    }

    // --- Restoring what isn't kept. ---

    /// What was left out of the cells, put back from what the route's opened with.
    public static void Restore(NetworkLines lines, RouteNetwork network, IReadOnlyDictionary<long, Alignment> shapes, HashSet<TileId> built,
        IReadOnlyDictionary<long, BridgeEdit> bridgeEdits, List<PlacedObject> objects)
    {
        void Set(string name, object? value) => RootFields.First(f => f.Name == name).SetValue(lines, value);
        Set("Ways", network.Ways.Values.ToDictionary(w => w.Id));
        Set("Shapes", shapes);
        Set("BuiltTiles", built);
        Set(Auto(nameof(NetworkLines.DetailCache)), new System.Collections.Concurrent.ConcurrentDictionary<NetworkLines.DetailRegion, Lazy<float[]>>());
        lines.BridgeEdits = bridgeEdits;
        Set("_crossingObjects", objects.Where(o => o.Kind == PlacedKind.GradeCrossing).ToList());
    }

    /// What was left out of the cells, put back as the lines shown have it (a window read again as the camera moves, or grown for an
    /// edit): the ways they were laid from (not the network as it is now: an edit since is still to be laid), the shapes, tiles, bridge
    /// settings and crossings; the detail regions' shaped ground kept (the lines read are those shown, bit for bit, where both have them).
    public static void RestoreFrom(NetworkLines lines, NetworkLines shown)
    {
        foreach (var f in RootFields.Where(f => RuleOf(f).Place == Place.Restored))
            f.SetValue(lines, f.GetValue(shown));
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<NetworkLines.DetailRegion, Lazy<float[]>>(shown.DetailCache);
        RootFields.First(f => f.Name == Auto(nameof(NetworkLines.DetailCache))).SetValue(lines, cache);
    }

    // --- The schema: every type the cells hold, and its layout. ---

    private static IEnumerable<Type> FixedTypes()
    {
        yield return typeof(NetworkLines);
        yield return typeof(NetworkLine);
        foreach (var f in SpreadFields)
        {
            yield return f.FieldType;
            foreach (var arg in f.FieldType.GetGenericArguments())
            {
                yield return arg;
                foreach (var inner in arg.IsGenericType ? arg.GetGenericArguments() : [])
                    yield return inner;
            }
        }

        foreach (var f in LineFields.Concat(RootFields.Where(f => RuleOf(f).Place == Place.Route)))
            yield return f.FieldType;
    }
}
