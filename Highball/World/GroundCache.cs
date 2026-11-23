using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// A 64-bit hash that comes out the same in every run, fed a word at a time (StableHash goes a byte at a time, too slow for a
/// tile's million heights).
internal struct WordHash
{
    public ulong Value { get; private set; } = 0xCBF29CE484222325UL;

    public WordHash()
    {
    }

    public void Add(ulong word)
    {
        var v = (Value ^ word) * 0x9E3779B97F4A7C15UL;
        Value = v ^ (v >> 31);
    }

    public void Add(long word) => Add((ulong)word);
    public void Add(int word) => Add((ulong)(uint)word);
    public void Add(bool word) => Add(word ? 1UL : 0UL);

    /// A float's bits as they are (raw ground, which is either the same or not).
    public void AddExact(ReadOnlySpan<float> values)
    {
        Add(values.Length);
        var words = MemoryMarshal.Cast<float, ulong>(values);
        foreach (var w in words)
            Add(w);
        if ((values.Length & 1) == 1)
            Add(BitConverter.SingleToUInt32Bits(values[^1]));
    }

    /// Rounded to step: a value that moved by less than that (a pass's floating-point noise) hashes the same.
    public void Add(double value, double step) => Add((long)Math.Round(value / step));

    public void Add(ReadOnlySpan<double> values, double step)
    {
        Add(values.Length);
        foreach (var v in values)
            Add(v, step);
    }

    public void Add(ReadOnlySpan<float> values, double step)
    {
        Add(values.Length);
        foreach (var v in values)
            Add(v, step);
    }
}

/// The ground of each tile as Conform shaped it, kept with the route (Cache/Ground), so a route opens without shaping its ground
/// again. Each tile's file holds only the samples shaping changed, and a key: a hash of the tile's ground as it came, and of
/// everything shaping reads there (the lines over it as their profiles came out, their bridge ends, the slope ends' shapes and
/// the crossings' pads round it), and of the passes' build (CellStore.BuildVersion). An edit that changes a line changes the key of the tiles it's
/// on and no others, so only those are shaped again. HB_GROUNDCHECK=1: a tile found cached is shaped again anyway and the two
/// compared, logged. HB_NOGROUNDCACHE=1: never read (still written).
internal static class GroundCache
{
    private const int Magic = 0x444E5247; // "GRND"
    private const int Version = 1;

    /// How finely what shaping reads is told apart: a line moved by less than this shapes the ground as it was. Under the
    /// ground's own 2 m spacing and well under the few centimetres a bed's sunk into it.
    public const double PlaceStep = 0.01;
    public const double HeightStep = 0.002;

    private static readonly ulong Build = BuildStamp();
    public static readonly bool Check = Environment.GetEnvironmentVariable("HB_GROUNDCHECK") == "1";
    private static readonly bool Off = Environment.GetEnvironmentVariable("HB_NOGROUNDCACHE") == "1";

    /// The passes' build, as the cells have it (CellStore.BuildVersion, bumped by hand when a change to shaping should show), not the
    /// build of the game: a new build with the same shaping keeps the ground shaped already.
    private static ulong BuildStamp()
    {
        var hash = new WordHash();
        hash.Add(CellStore.BuildVersion);
        hash.Add(Version);
        return hash.Value;
    }

    /// HB_GROUNDDEBUG=1: each tile's key's parts kept beside it, and a tile whose parts differ from those kept says which.
    private static readonly bool Debug = Environment.GetEnvironmentVariable("HB_GROUNDDEBUG") == "1";

    private static void DebugParts(RouteInfo route, TileId tile, float[] raw, ulong shaping, NetworkLines lines)
    {
        var rawHash = new WordHash();
        rawHash.AddExact(raw);
        var now = $"raw={rawHash.Value:x} shaping={shaping:x} fresh={lines.ShapingKey(tile, raw.Length == 0 ? 0 : (int)Math.Sqrt(raw.Length)):x} {lines.ShapingParts(tile, (int)Math.Sqrt(raw.Length))}";
        var path = PathOf(route, tile) + ".parts.txt";
        if (File.Exists(path) && File.ReadAllText(path) is var was && was != now)
            Log.Info($"Ground debug: tile {tile} key parts changed: was [{was}] now [{now}]");
        File.WriteAllText(path, now);
    }

    internal static string PathOf(RouteInfo route, TileId tile) => Path.Combine(route.Folder, "Cache", "Ground", $"{tile.I}_{tile.J}.bin.gz");

    /// What a tile's shaped ground depends on; null where it isn't kept (a pack's route, read only).
    public static ulong? KeyOf(RouteInfo route, NetworkLines lines, TileId tile, float[] raw, int samples)
    {
        if (route.Pack is not null)
            return null;
        var hash = new WordHash();
        hash.Add(Build);
        hash.Add(samples);
        hash.Add(tile.I);
        hash.Add(tile.J);
        hash.AddExact(raw);
        var shaping = lines.PreparedShapingKey(tile, samples);
        hash.Add(shaping);
        if (Debug)
            DebugParts(route, tile, raw, shaping, lines);
        return hash.Value;
    }

    /// The key a tile's ground is kept under, if it's kept (for logging why it wasn't taken).
    internal static ulong? KeptKey(RouteInfo route, TileId tile)
    {
        try
        {
            using var file = File.OpenRead(PathOf(route, tile));
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(zip);
            return r.ReadInt32() == Magic && r.ReadInt32() == Version ? r.ReadUInt64() : null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The tile's shaped ground read into heights (its ground as it came) if it's kept under this key.
    public static bool TryLoad(RouteInfo route, TileId tile, ulong key, float[] heights)
    {
        if (Off)
            return false;
        var path = PathOf(route, tile);
        if (!File.Exists(path))
            return false;
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(zip, 1 << 16));
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadUInt64() != key || r.ReadInt32() != heights.Length)
                return false;
            // (Read into a copy first: a file cut short mustn't leave the tile half shaped.)
            var shaped = (float[])heights.Clone();
            for (var runs = r.ReadInt32(); runs > 0; runs--)
            {
                var (start, length) = (r.ReadInt32(), r.ReadInt32());
                if (start < 0 || length < 0 || start + length > shaped.Length)
                    return false;
                r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(shaped.AsSpan(start, length)));
            }

            Array.Copy(shaped, heights, heights.Length);
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Keeps a tile's shaped ground: the runs of samples that differ from its ground as it came.
    public static void Save(RouteInfo route, TileId tile, ulong key, float[] raw, float[] shaped)
    {
        var path = PathOf(route, tile);
        var temporary = path + $".{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var runs = new List<(int Start, int Length)>();
            for (var i = 0; i < shaped.Length;)
            {
                if (BitConverter.SingleToUInt32Bits(shaped[i]) == BitConverter.SingleToUInt32Bits(raw[i]))
                {
                    i++;
                    continue;
                }

                var start = i;
                // (Short stretches left alone inside a run are written as they are: cheaper than a new run's header.)
                var same = 0;
                while (i < shaped.Length && same < 4)
                {
                    same = BitConverter.SingleToUInt32Bits(shaped[i]) == BitConverter.SingleToUInt32Bits(raw[i]) ? same + 1 : 0;
                    i++;
                }

                runs.Add((start, i - same - start));
            }

            using (var file = File.Create(temporary))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(zip))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(key);
                w.Write(shaped.Length);
                w.Write(runs.Count);
                foreach (var (start, length) in runs)
                {
                    w.Write(start);
                    w.Write(length);
                    w.Write(MemoryMarshal.AsBytes(shaped.AsSpan(start, length)));
                }
            }

            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Ground cache: couldn't keep tile {tile}: {e.Message}");
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed partial class NetworkLines
{
    private readonly ConcurrentDictionary<NetworkLine, ulong> _shapingHashes = new();
    private ConcurrentDictionary<TileId, ulong>? _preparedKeys;

    /// Every tile's shaping key worked out before any's shaped: shaping a tile leaves things others' keys read worked out (or
    /// changed), and a key made after some tiles were shaped and before others came out differently from run to run.
    public void PrepareShapingKeys(IEnumerable<TileId> tiles, int samples)
    {
        var keys = new ConcurrentDictionary<TileId, ulong>();
        Parallel.ForEach(tiles.Where(_byTile.ContainsKey), tile => keys[tile] = ShapingKey(tile, samples));
        _preparedKeys = keys;
    }

    internal ulong PreparedShapingKey(TileId tile, int samples) =>
        _preparedKeys is { } keys && keys.TryGetValue(tile, out var key) ? key : ShapingKey(tile, samples);

    /// Shapes a tile's ground as Conform does, or takes it as kept from the last time it was shaped from the same things
    /// (GroundCache). Returns whether it was taken as kept.
    public bool ConformKept(RouteInfo route, TileId tile, float[] heights, int samples)
    {
        if (!_byTile.ContainsKey(tile))
            return false;
        var key = GroundCache.KeyOf(route, this, tile, heights, samples);
        if (key is { } k && GroundCache.TryLoad(route, tile, k, heights))
        {
            // (What Conform does besides shaping the ground.)
            _ballastSamples = samples;
            _ballast.TryRemove(tile, out _);
            if (GroundCache.Check)
                CheckKept(tile, heights, samples, route);
            return true;
        }

        var raw = key is null ? null : (float[])heights.Clone();
        Conform(tile, heights, samples);
        if (key is { } save)
            GroundCache.Save(route, tile, save, raw!, heights);
        return false;
    }

    /// HB_GROUNDKEYS=path: what each tile's key was made of, part by part, and each line's parts, written there to compare two runs.
    internal void DumpShapingKeys(string path, int samples)
    {
        var w = new System.Text.StringBuilder();
        foreach (var l in _lines)
        {
            ulong F(ReadOnlySpan<float> v, double step)
            {
                var h = new WordHash();
                h.Add(v, step);
                return h.Value;
            }

            ulong D(ReadOnlySpan<double> v, double step)
            {
                var h = new WordHash();
                h.Add(v, step);
                return h.Value;
            }

            w.AppendLine($"L {l.Key} {l.Index} all={ShapingHash(l):x} X={D(l.X, GroundCache.PlaceStep):x} Z={D(l.Z, GroundCache.PlaceStep):x} Y={F(l.Y, GroundCache.HeightStep):x} " +
                     $"S={F(l.S, GroundCache.PlaceStep):x} Sink={F(l.Sink, GroundCache.HeightStep):x} Shift={F(l.Shift, GroundCache.PlaceStep):x} J={F(l.Junction, GroundCache.PlaceStep):x} " +
                     $"W={l.Width} F={l.Formation} cuts={l.RailCuts.Count}/{l.TieCuts.Count}/{l.BedDrops.Count}");
        }

        foreach (var (tile, list) in _byTile.OrderBy(t => t.Key.I).ThenBy(t => t.Key.J))
        {
            var ends = new WordHash();
            foreach (var index in list)
                foreach (var end in BridgeEndsOf(_lines[index]))
                    AddEnd(ref ends, end);
            w.AppendLine($"T {tile.I} {tile.J} key={ShapingKey(tile, samples):x} ends={ends.Value:x} lines={string.Join(",", list.Select(i => _lines[i].Key))}");
        }

        File.WriteAllText(path, w.ToString());
    }

    /// Shapes a square window of a tile again (its first sample x0, z0 in the tile's samples, side samples a side, kept inside the
    /// tile) from its ground as it came, into shaped: each sample's shaping depends only on its own ground and the lines within
    /// reach of it, so the window comes out as it would with the whole tile shaped.
    public void ConformWindow(TileId tile, float[] raw, float[] shaped, int x0, int z0, int side)
    {
        var samples = _quads + 1;
        side = Math.Min(side, samples);
        (x0, z0) = (Math.Clamp(x0, 0, samples - side), Math.Clamp(z0, 0, samples - side));
        var window = new float[side * side];
        for (var z = 0; z < side; z++)
            Array.Copy(raw, (z0 + z) * samples + x0, window, z * side, side);
        if (_byTile.TryGetValue(tile, out var list))
            ConformCore(list, window, side, tile.I * _quads + x0, -(tile.J + 1) * _quads + z0, null);
        for (var z = 0; z < side; z++)
            Array.Copy(window, z * side, shaped, (z0 + z) * samples + x0, side);
    }

    /// The side of the blocks ConformBlocks shapes a tile in, samples.
    private const int BlockSide = 128;

    /// Shapes a rectangle of a tile again (x0..x1, z0..z1 in its samples) from its ground as it came, into shaped, in blocks side
    /// by side on every core: each is a window (ConformWindow), so they come out as the whole tile shaped would.
    public void ConformBlocks(TileId tile, float[] raw, float[] shaped, int x0, int z0, int x1, int z1)
    {
        var blocks = new List<(int X, int Z)>();
        for (var z = z0; z <= z1; z += BlockSide)
            for (var x = x0; x <= x1; x += BlockSide)
                blocks.Add((x, z));
        // (A block at the tile's edge is moved back inside it, over its neighbour: they write the same heights there.)
        Parallel.ForEach(blocks, b => ConformWindow(tile, raw, shaped, b.X, b.Z, BlockSide));
    }

    /// Shapes a whole tile as Conform does, in blocks on every core (ConformBlocks).
    public void ConformWhole(TileId tile, float[] raw, float[] shaped)
    {
        var samples = _quads + 1;
        ConformBlocks(tile, raw, shaped, 0, 0, samples - 1, samples - 1);
        // (What Conform does besides shaping the ground.)
        _ballastSamples = samples;
        _ballast.TryRemove(tile, out _);
    }

    private void CheckKept(TileId tile, float[] kept, int samples, RouteInfo route)
    {
        var fresh = route.LoadTile(tile).Heights;
        Conform(tile, fresh, samples);
        var (worst, off) = (0f, 0);
        for (var i = 0; i < kept.Length; i++)
        {
            var d = MathF.Abs(kept[i] - fresh[i]);
            if (d > 0.005f)
                off++;
            worst = MathF.Max(worst, d);
        }

        if (off > 0)
            Log.Warn($"Ground cache check: tile {tile} kept is off by {worst:0.000} m at most, {off} samples over 5 mm");
    }

    /// A hash of everything Conform reads for a tile but its ground: the lines over it, their bridge ends (and the lines those
    /// stand over or carry), the walls beside them, the slope ends' shapes and the crossings' pads that reach it.
    internal ulong ShapingKey(TileId tile, int samples)
    {
        var hash = new WordHash();
        if (!_byTile.TryGetValue(tile, out var list))
            return hash.Value;
        var walls = WallsBeside();
        hash.Add(list.Count);
        foreach (var index in list)
        {
            var line = _lines[index];
            hash.Add(ShapingHash(line));
            // (Ends taken in no order: the walls beside a line are gathered from bridge ends found in parallel, in whatever order
            // they came.)
            AddEnds(ref hash, BridgeEndsOf(line));
            AddEnds(ref hash, walls.TryGetValue(line, out var beside) ? beside : []);
        }

        // (Shapes and pads by where they reach, well past the tile's edge: either may be a line's off the tile.)
        const double margin = 200.0;
        var size = (samples - 1) * (double)_spacing;
        var (x0, z0) = (tile.I * size - margin, -(tile.J + 1) * size - margin);
        var (x1, z1) = (x0 + size + 2 * margin, z0 + size + 2 * margin);
        // (Taken in no order: the cells give the shapes back in their own, CellStore.OrdinalOf.)
        var shapes = new List<ulong>();
        foreach (var shape in EndShapes)
        {
            var b = shape.Bounds;
            if (b.X1 < x0 || b.X0 > x1 || b.Z1 < z0 || b.Z0 > z1)
                continue;
            var one = new WordHash();
            one.Add(ShapingHash(shape.Line));
            AddEnd(ref one, shape.End);
            foreach (var v in (ReadOnlySpan<double>)[shape.Ex, shape.Ez, shape.Fx, shape.Fz, b.X0, b.Z0, b.X1, b.Z1])
                one.Add(v, GroundCache.PlaceStep);
            foreach (var v in (ReadOnlySpan<float>)[shape.DeckHalf, shape.DeckY, shape.SeatY, shape.ToeLeft, shape.ToeRight, shape.Reach])
                one.Add(v, GroundCache.HeightStep);
            shapes.Add(one.Value);
        }

        shapes.Sort();
        hash.Add(shapes.Count);
        foreach (var h in shapes)
            hash.Add(h);

        // (Pads by where they reach too, with the roads running on from them.)
        hash.Add(PadsHash(x0, z0, x1, z1));
        return hash.Value;
    }

    internal string ShapingParts(TileId tile, int samples)
    {
        if (!_byTile.TryGetValue(tile, out var list))
            return "";
        var (lineHash, ends) = (new WordHash(), new WordHash());
        foreach (var index in list)
        {
            lineHash.Add(ShapingHash(_lines[index]));
            foreach (var end in BridgeEndsOf(_lines[index]))
                AddEnd(ref ends, end);
        }

        var walls = WallsBeside();
        var beside = new WordHash();
        foreach (var index in list)
            if (walls.TryGetValue(_lines[index], out var bs))
                foreach (var end in bs)
                    AddEnd(ref beside, end);
        var shapes = new WordHash();
        foreach (var shape in EndShapes)
        {
            hashShape(ref shapes, shape);
        }

        static void hashShape(ref WordHash h, BridgeEndShape shape)
        {
            var b = shape.Bounds;
            foreach (var v in (ReadOnlySpan<double>)[shape.Ex, shape.Ez, shape.Fx, shape.Fz, b.X0, b.Z0, b.X1, b.Z1])
                h.Add(v, GroundCache.PlaceStep);
        }

        return $"lines={lineHash.Value:x} ({list.Count}: {string.Join(",", list.Take(6).Select(i => _lines[i].Key))}) ends={ends.Value:x} walls={beside.Value:x} allshapes={shapes.Value:x}/{EndShapes.Count}";
    }

    /// The pads that reach into this box (world metres) and the roads running on from them, in one hash: only those near a tile go
    /// into its key, so a crossing changed in one place doesn't change every tile's.
    private ulong PadsHash(double x0, double z0, double x1, double z1)
    {
        var hash = new WordHash();
        var size = _quads * (double)_spacing;
        var near = _pads.Where(p => p.X + p.Half + 100.0 >= x0 && p.X - p.Half - 100.0 <= x1 && p.Z + p.Half + 100.0 >= z0 && p.Z - p.Half - 100.0 <= z1).ToList();
        hash.Add(near.Count);
        foreach (var pad in near)
        {
            hash.Add(pad.Id);
            foreach (var v in (ReadOnlySpan<double>)[pad.X, pad.Z, pad.Dx, pad.Dz])
                hash.Add(v, GroundCache.PlaceStep);
            foreach (var v in (ReadOnlySpan<float>)[pad.Half, pad.Low, pad.High, pad.Level])
                hash.Add(v, GroundCache.HeightStep);
            var (sx, sz) = pad.Start;
            var (ex, ez) = pad.End;
            // (The roads that could end there: those over the tiles its ends are in.)
            var candidates = new SortedSet<int>();
            foreach (var (px, pz) in (ReadOnlySpan<(double, double)>)[(sx, sz), (ex, ez)])
                foreach (var i in LinesIn(new TileId((int)Math.Floor(px / size), (int)Math.Floor(-pz / size))))
                    candidates.Add(i);
            foreach (var i in candidates)
            {
                var l = _lines[i];
                if (!l.Rail && !l.Mark && l.Count >= 2 && new[] { 0, l.Count - 1 }.Any(p =>
                        Math.Min((l.X[p] - sx) * (l.X[p] - sx) + (l.Z[p] - sz) * (l.Z[p] - sz), (l.X[p] - ex) * (l.X[p] - ex) + (l.Z[p] - ez) * (l.Z[p] - ez)) <= PadJoin * PadJoin))
                    hash.Add(ShapingHash(l));
            }
        }

        return hash.Value;
    }

    private void AddEnds(ref WordHash hash, IReadOnlyList<BridgeEnd> ends)
    {
        var each = new ulong[ends.Count];
        for (var i = 0; i < ends.Count; i++)
        {
            var one = new WordHash();
            AddEnd(ref one, ends[i]);
            each[i] = one.Value;
        }

        Array.Sort(each);
        hash.Add(each.Length);
        foreach (var h in each)
            hash.Add(h);
    }

    private void AddEnd(ref WordHash hash, BridgeEnd end)
    {
        hash.Add(end.Point);
        hash.Add(end.Start);
        hash.Add((int)end.Kind);
        hash.Add(end.Side);
        hash.Add(end.Set);
        foreach (var v in (ReadOnlySpan<float>)[end.Run, end.Floor, end.Wing, end.UnderS, end.Footprint, end.Flat, end.Setback, end.Cap ?? -1e6f])
            hash.Add(v, GroundCache.HeightStep);
        hash.Add(end.Under is { } under ? ShapingHash(under) : 0UL);
        hash.Add(end.Deck is { } deck ? ShapingHash(deck) : 0UL);
    }

    internal ulong ShapingHashOf(NetworkLine line) => ShapingHash(line);

    /// A line as shaping reads it: where it runs, at what height, how wide, what it's carried on.
    private ulong ShapingHash(NetworkLine line) => _shapingHashes.GetOrAdd(line, static l =>
    {
        var hash = new WordHash();
        hash.Add(l.Key);
        hash.Add(l.Name);
        hash.Add(l.Rail);
        hash.Add(l.Mark);
        hash.Add(l.Class);
        hash.Add(l.Tracks);
        hash.Add(l.Disused);
        hash.Add(l.Embedded);
        hash.Add(l.Exact);
        hash.Add(l.Lanes);
        hash.Add(l.Oneway);
        hash.Add(l.Unpaved);
        hash.Add((int)l.Bed);
        foreach (var v in (ReadOnlySpan<float>)[l.Gauge, l.Width, l.Formation, l.BedHeight])
            hash.Add(v, GroundCache.HeightStep);
        hash.Add(l.X, GroundCache.PlaceStep);
        hash.Add(l.Z, GroundCache.PlaceStep);
        hash.Add(l.Y, GroundCache.HeightStep);
        hash.Add(l.S, GroundCache.PlaceStep);
        hash.Add(l.Sink, GroundCache.HeightStep);
        hash.Add(l.Shift, GroundCache.PlaceStep);
        hash.Add(l.Junction, GroundCache.PlaceStep);
        hash.Add(l.Structures.Length);
        foreach (var s in l.Structures)
            hash.Add((int)s);
        hash.Add(l.Layers.Length);
        foreach (var layer in l.Layers)
            hash.Add(layer);
        hash.Add(l.RailCuts.Count);
        foreach (var (from, to, side) in l.RailCuts)
        {
            hash.Add(from, GroundCache.PlaceStep);
            hash.Add(to, GroundCache.PlaceStep);
            hash.Add(side);
        }

        foreach (var list in (ReadOnlySpan<List<(float From, float To)>>)[l.TieCuts, l.BedDrops])
        {
            hash.Add(list.Count);
            foreach (var (from, to) in list)
            {
                hash.Add(from, GroundCache.PlaceStep);
                hash.Add(to, GroundCache.PlaceStep);
            }
        }

        hash.Add(l.TurnoutZones.Count);
        return hash.Value;
    });
}
