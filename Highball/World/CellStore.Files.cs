using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

internal static partial class CellStore
{
    /// A piece of a line: its points first..first + count in one cell, and how each of its arrays is cut.
    private sealed record Piece(int Line, int LineCount, int First, int Count, long Key, Slice[] Slices);

    /// Something spread over the cells: the field it's of (SpreadFields), its place there, and itself (a list's or set's item: Key).
    private readonly record struct Element(int Field, long Ordinal, object? Key, object? Value, bool Own);

    /// What's going into one cell (or into the index: what has no cell).
    private sealed class CellData
    {
        public readonly List<Piece> Pieces = [];
        public readonly List<int> Heads = [];
        public readonly List<Element> Elements = [];
    }

    /// A spread field as it was: whether it was there at all (null), how many it held, and for a list of dictionaries, how many each.
    public sealed record SpreadMeta(string Name, bool Present, int Count, int[]? Inner);

    private static (Type? Key, Type? Value, Type? Item) ElementTypes(FieldInfo f, Rule rule)
    {
        var t = rule.Nested ? f.FieldType.GetGenericArguments()[0] : f.FieldType;
        var args = t.GetGenericArguments();
        return args.Length == 2 ? (args[0], args[1], null) : (null, null, args[0]);
    }

    // --- Writing. ---

    /// Every cell written, and the index last. current: whether what the stamp was taken from is still what's saved, asked before
    /// anything's put in place (null: it is).
    public static void Write(RouteInfo route, ulong stamp, NetworkLines lines, Func<bool>? current = null)
    {
        if (route.Pack is not null)
            return;
        var watch = Stopwatch.StartNew();
        var folder = FolderOf(route);
        var written = new List<(TileId Id, string Temporary, long Size, ulong Hash)>();
        try
        {
            Directory.CreateDirectory(folder);
            var c = ContextOf(lines);
            var all = lines.Lines;
            if (LineFields.Length > 64)
                throw new NotSupportedException("Cells: NetworkLine has more fields than a head's mask holds");
            var unknown = RootFields.Where(f => !Rules.ContainsKey(f.Name)).Select(f => f.Name).ToList();
            if (unknown.Count > 0)
                Log.Warn($"Cells: fields with no rule, kept in the index: {string.Join(", ", unknown)} (add them to CellStore.Rules)");

            var registry = new Dictionary<object, (int Kind, int Index)>(ReferenceEqualityComparer.Instance) { [lines] = (0, 0) };
            for (var i = 0; i < all.Count; i++)
                registry[all[i]] = (1, i);

            var cells = new Dictionary<TileId, CellData>();
            var rest = new CellData();
            CellData At(TileId? id)
            {
                if (id is not { } cell)
                    return rest;
                if (!cells.TryGetValue(cell, out var data))
                    cells[cell] = data = new CellData();
                return data;
            }

            // The lines, cut into pieces at the cells' borders.
            for (var i = 0; i < all.Count; i++)
            {
                var line = all[i];
                var slices = Cuttable.Select(f => (f, CutOf((Array?)LineFields[f].GetValue(line), line.Count))).Where(s => s.Item2.Cut != Cut.None)
                    .Select(s => new Slice(s.f, s.Item2.Cut, s.Item2.Stride)).ToArray();
                if (line.Count == 0)
                {
                    var data = At(c.CellOf(0, 0));
                    data.Pieces.Add(new Piece(i, 0, 0, 0, line.Key, slices));
                    data.Heads.Add(i);
                    continue;
                }

                var (start, cell) = (0, c.CellOf(line.X[0], line.Z[0]));
                At(cell).Heads.Add(i);
                for (var p = 1; p <= line.Count; p++)
                {
                    var next = p < line.Count ? c.CellOf(line.X[p], line.Z[p]) : default;
                    if (p < line.Count && next == cell)
                        continue;
                    At(cell).Pieces.Add(new Piece(i, line.Count, start, p - start, line.Key, slices));
                    (start, cell) = (p, next);
                }
            }

            // Everything else spread over them, each where it's anchored.
            var metas = new SpreadMeta[SpreadFields.Length];
            var unplaced = new int[SpreadFields.Length];
            for (var fi = 0; fi < SpreadFields.Length; fi++)
            {
                var f = SpreadFields[fi];
                var rule = Rules[f.Name];
                var value = f.GetValue(lines);
                if (value is null)
                {
                    metas[fi] = new SpreadMeta(f.Name, false, 0, null);
                    continue;
                }

                var registered = Registered(f);
                var count = 0;
                void Add(long ordinal, object? key, object? v)
                {
                    var own = registered && key is not null && registry.TryAdd(key, (2 + fi, (int)ordinal));
                    var cell = rule.Anchor!(c, key, v);
                    if (cell is null)
                        unplaced[fi]++;
                    At(cell).Elements.Add(new Element(fi, ordinal, key, v, own));
                    count++;
                }

                int[]? inner = null;
                if (rule.Nested)
                {
                    var outer = (IList)value;
                    inner = new int[outer.Count];
                    for (var o = 0; o < outer.Count; o++)
                    {
                        var k = 0;
                        foreach (DictionaryEntry e in (IDictionary)outer[o]!)
                            Add(((long)o << 32) | (uint)k++, e.Key, e.Value);
                        inner[o] = k;
                    }
                }
                else if (value is IDictionary dictionary)
                {
                    var k = 0;
                    foreach (DictionaryEntry e in dictionary)
                        Add(k++, e.Key, e.Value);
                }
                else
                {
                    var k = 0;
                    foreach (var item in (IEnumerable)value)
                        Add(k++, item, null);
                }

                metas[fi] = new SpreadMeta(f.Name, true, count, inner);
            }

            var distributed = watch.ElapsedMilliseconds;
            (int, int)? External(object o) => registry.TryGetValue(o, out var at) ? at : null;

            // The cells, written side by side.
            var ids = cells.Keys.OrderBy(t => t.I).ThenBy(t => t.J).ToArray();
            var results = new (string Temporary, long Size, ulong Hash, Type[] Types, object[]? Objects, long RawA, long RawB)[ids.Length];
            var measures = Measure ? new MeasureTotals() : null;
            var bytesBy = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
            Parallel.For(0, ids.Length, k =>
            {
                var (a, b, types, objects) = Encode(cells[ids[k]], lines, External, DefaultShuffle, false, bytesBy);
                var file = Pack(ids[k], a, b, DefaultCodec, DefaultShuffle);
                measures?.Add(a, b, Measure ? Encode(cells[ids[k]], lines, External, false, false).B : null);
                var temporary = CellPath(route, ids[k]) + ".tmp";
                File.WriteAllBytes(temporary, file);
                results[k] = (temporary, file.Length, HashOf(file), types, Check ? objects : null, a.Length, b.Length);
            });
            for (var k = 0; k < ids.Length; k++)
                written.Add((ids[k], results[k].Temporary, results[k].Size, results[k].Hash));

            var (restA, restB, restTypes, restObjects) = Encode(rest, lines, External, DefaultShuffle, true, bytesBy);
            var restFile = Pack(new TileId(int.MinValue, int.MinValue), restA, restB, DefaultCodec, DefaultShuffle);
            var encoded = watch.ElapsedMilliseconds;

            if (Check)
                FindShared(results.Select((r, k) => (ids[k].ToString(), r.Objects!)).Append(("index", restObjects)));

            // The index: what the cells are, what they're built from, and what they hold.
            var schema = FixedTypes().Concat(results.SelectMany(r => r.Types)).Concat(restTypes).Distinct().Where(t => !t.ContainsGenericParameters)
                .OrderBy(GraphSnapshot.NameOf, StringComparer.Ordinal).ToList();
            var index = IndexPath(route) + ".tmp";
            using (var file = new FileStream(index, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(file))
            {
                w.Write(IndexMagic);
                w.Write(FormatVersion);
                w.Write(BuildVersion);
                w.Write(stamp);
                w.Write(RulesHash);
                w.Write(all.Count);
                w.Write(schema.Count);
                foreach (var t in schema)
                {
                    w.Write(t.AssemblyQualifiedName!);
                    w.Write(GraphSnapshot.LayoutOf(t));
                }

                w.Write(metas.Length);
                foreach (var m in metas)
                {
                    w.Write(m.Name);
                    w.Write(m.Present);
                    w.Write(m.Count);
                    w.Write(m.Inner?.Length ?? -1);
                    foreach (var n in m.Inner ?? [])
                        w.Write(n);
                }

                w.Write(ids.Length);
                for (var k = 0; k < ids.Length; k++)
                {
                    w.Write(ids[k].I);
                    w.Write(ids[k].J);
                    w.Write(results[k].Size);
                    w.Write(results[k].Hash);
                }

                w.Write(restFile.Length);
                w.Write(restFile);
            }

            written.Add((new TileId(int.MinValue, int.MinValue), index, new FileInfo(index).Length, 0));

            if (current is not null && !current())
            {
                Log.Info("Cells: saved again while they were being written, left for the next save");
                return;
            }

            foreach (var (id, temporary, _, _) in written.Where(x => x.Id.I != int.MinValue))
                File.Move(temporary, CellPath(route, id), true);
            File.Move(index, IndexPath(route), true);
            written.Clear();

            // (Cells no longer in the set, and the compiled route these replace, which nothing reads.)
            var keep = ids.Select(id => $"{id}.cell").ToHashSet();
            var stale = 0;
            foreach (var path in Directory.EnumerateFiles(folder))
                if (CellName.IsMatch(Path.GetFileName(path)) && !keep.Contains(Path.GetFileName(path)))
                {
                    File.Delete(path);
                    stale++;
                }

            var compiled = Path.Combine(route.Folder, "Cache", "Lines.bin");
            if (File.Exists(compiled))
                File.Delete(compiled);

            var sizes = results.Select(r => r.Size).Order().ToArray();
            var biggest = ids.Length == 0 ? default : ids[Array.IndexOf(results.Select(r => r.Size).ToArray(), sizes[^1])];
            Log.Info($"Cells: {ids.Length} cells written ({sizes.Sum() / (1 << 20)} MB, from {results.Sum(r => r.RawA + r.RawB) / (1 << 20)} MB; " +
                     $"biggest {(sizes.Length > 0 ? sizes[^1] / 1024 : 0)} KB at {biggest}, median {(sizes.Length > 0 ? sizes[sizes.Length / 2] / 1024 : 0)} KB; " +
                     $"index {new FileInfo(IndexPath(route)).Length / 1024} KB, {schema.Count} types), {all.Count} lines in " +
                     $"{cells.Values.Sum(d => d.Pieces.Count)} pieces, {metas.Sum(m => m.Count)} things spread ({string.Join(", ", unplaced.Select((n, fi) => (n, fi)).Where(x => x.n > 0).Select(x => $"{x.n} of {SpreadFields[x.fi].Name}"))} without a cell, in the index), " +
                     $"{stale} stale removed; ms: spread {distributed}, encode {encoded - distributed}, written {watch.ElapsedMilliseconds - encoded}");
            Log.Info($"Cells: raw bytes by what: {string.Join(", ", bytesBy.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value / 1024} KB"))}");
            measures?.Log();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or TargetInvocationException or InvalidCastException)
        {
            Log.Warn($"Cells: couldn't write them ({(e as TargetInvocationException)?.InnerException?.Message ?? e.Message})");
        }
        finally
        {
            foreach (var (_, temporary, _, _) in written)
            {
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

    /// A cell's two blocks: A, what's in it (its pieces and the things it makes, read first, before anything's made); B, the pieces'
    /// arrays, the lines' heads and the things spread into it. route: the index's own, which carries the route-wide fields too.
    private static (byte[] A, byte[] B, Type[] Types, object[] Objects) Encode(CellData d, NetworkLines lines, Func<object, (int, int)?> external, bool shuffle, bool route,
        System.Collections.Concurrent.ConcurrentDictionary<string, long>? sizes = null)
    {
        var owned = d.Elements.Where(e => e.Own).ToList();
        var types = owned.Select(e => e.Key!.GetType()).Distinct().ToList();

        using var a = new MemoryStream();
        using (var w = new BinaryWriter(a, System.Text.Encoding.UTF8, true))
        {
            w.Write(d.Pieces.Count);
            foreach (var p in d.Pieces)
            {
                w.Write(p.Line);
                w.Write(p.LineCount);
                w.Write(p.First);
                w.Write(p.Count);
                w.Write(p.Key);
                w.Write((byte)p.Slices.Length);
                foreach (var s in p.Slices)
                {
                    w.Write((byte)s.Field);
                    w.Write((byte)s.Cut);
                    w.Write((byte)s.Stride);
                }

                // (Blocks of its own a later format adds to a piece, each a tag and a length: none yet. Stage 2's border points go here.)
                w.Write(0);
            }

            w.Write(types.Count);
            foreach (var t in types)
                w.Write(t.AssemblyQualifiedName!);
            w.Write(owned.Count);
            foreach (var e in owned)
            {
                w.Write(2 + e.Field);
                w.Write((int)e.Ordinal);
                w.Write(types.IndexOf(e.Key!.GetType()));
            }
        }

        using var b = new MemoryStream();
        GraphSnapshot.Writer g;
        using (var w = new BinaryWriter(b, System.Text.Encoding.UTF8, true))
        {
            var all = lines.Lines;
            foreach (var p in d.Pieces)
                foreach (var s in p.Slices)
                {
                    var at = b.Position;
                    WriteSlice(w, (Array)LineFields[s.Field].GetValue(all[p.Line])!, s, p.First, p.Count, p.LineCount, shuffle);
                    Count(sizes, (route ? "index " : "") + "line." + LineFields[s.Field].Name, b.Position - at);
                }

            var headsFrom = b.Position;

            g = new GraphSnapshot.Writer(w, null, external);
            var heads = d.Pieces.Where(p => p.First == 0).ToDictionary(p => p.Line);
            w.Write(d.Heads.Count);
            foreach (var i in d.Heads)
            {
                var line = all[i];
                var mask = 0UL;
                foreach (var s in heads[i].Slices)
                    mask |= 1UL << s.Field;
                w.Write(i);
                w.Write(mask);
                for (var j = 0; j < LineFields.Length; j++)
                    if ((mask & (1UL << j)) == 0 && !LineDropped.Contains(LineFields[j].Name))
                        g.Write(LineFields[j].FieldType, LineFields[j].GetValue(line));
            }

            Count(sizes, (route ? "index " : "") + "line heads", b.Position - headsFrom);
            var groups = d.Elements.GroupBy(e => e.Field).ToList();
            w.Write(groups.Count);
            foreach (var group in groups)
            {
                var f = SpreadFields[group.Key];
                var (keyType, valueType, itemType) = ElementTypes(f, Rules[f.Name]);
                var registered = Registered(f);
                var groupFrom = b.Position;
                w.Write(group.Key);
                w.Write(group.Count());
                foreach (var e in group)
                {
                    w.Write(e.Ordinal);
                    if (registered)
                    {
                        w.Write(e.Own);
                        if (e.Own)
                            g.WriteFields(e.Key!);
                        else
                            g.Write(itemType!, e.Key);
                    }
                    else if (keyType is not null)
                    {
                        g.Write(keyType, e.Key);
                        g.Write(valueType!, e.Value);
                    }
                    else
                        g.Write(itemType!, e.Key);
                }

                Count(sizes, (route ? "index " : "") + f.Name, b.Position - groupFrom);
            }

            if (route)
                foreach (var f in RootFields.Where(f => RuleOf(f).Place == Place.Route))
                {
                    var at = b.Position;
                    g.Write(f.FieldType, f.GetValue(lines));
                    Count(sizes, "index " + f.Name, b.Position - at);
                }
        }

        return (a.ToArray(), b.ToArray(), [.. g.Types], [.. g.Written]);
    }

    private static void Count(System.Collections.Concurrent.ConcurrentDictionary<string, long>? sizes, string what, long bytes)
    {
        if (sizes is not null)
            sizes.AddOrUpdate(what, bytes, (_, had) => had + bytes);
    }

    private static byte[] Pack(TileId id, byte[] a, byte[] b, Codec codec, bool shuffle)
    {
        var (pa, pb) = (Compress(a, codec), Compress(b, codec));
        using var memory = new MemoryStream(pa.Length + pb.Length + 40);
        using (var w = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
        {
            w.Write(CellMagic);
            w.Write(FormatVersion);
            w.Write(id.I);
            w.Write(id.J);
            w.Write((byte)codec);
            w.Write(shuffle);
            w.Write(a.Length);
            w.Write(pa.Length);
            w.Write(b.Length);
            w.Write(pb.Length);
            w.Write(pa);
            w.Write(pb);
        }

        return memory.ToArray();
    }

    /// HB_CELLCHECK: objects written whole into more than one cell, which come back as two (a reference shared across cells that isn't
    /// a line's or a spread thing's). Strings don't matter.
    private static void FindShared(IEnumerable<(string Cell, object[] Objects)> cells)
    {
        var owner = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var shared = new Dictionary<string, int>();
        foreach (var (cell, objects) in cells)
            foreach (var o in objects)
            {
                if (o is string || o is Array { Length: 0 })
                    continue;
                if (!owner.TryAdd(o, cell) && owner[o] != cell)
                    shared[GraphSnapshot.NameOf(o.GetType())] = shared.GetValueOrDefault(GraphSnapshot.NameOf(o.GetType())) + 1;
            }

        Log.Info(shared.Count == 0
            ? $"Cell check: no object written into two cells ({owner.Count} objects)"
            : $"Cell check: XX objects written into two cells, read back as two: {string.Join(", ", shared.Select(s => $"{s.Value} {s.Key}"))}");
    }

    /// HB_CELLMEASURE: every cell's blocks compressed every way, sizes and times summed.
    private sealed class MeasureTotals
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (long Size, long Pack, long Unpack)> _totals = [];
        private long _raw, _rawPlain;

        private static readonly (string Name, Func<byte[], byte[]> Pack, Codec Codec)[] Ways =
        [
            ("zlib fastest", r => Compress(r, Codec.ZLib, level: System.IO.Compression.CompressionLevel.Fastest), Codec.ZLib),
            ("zlib optimal", r => Compress(r, Codec.ZLib, level: System.IO.Compression.CompressionLevel.Optimal), Codec.ZLib),
            ("zlib smallest", r => Compress(r, Codec.ZLib, level: System.IO.Compression.CompressionLevel.SmallestSize), Codec.ZLib),
            ("brotli 1", r => Compress(r, Codec.Brotli, 1), Codec.Brotli),
            ("brotli 4", r => Compress(r, Codec.Brotli, 4), Codec.Brotli),
            ("brotli 5", r => Compress(r, Codec.Brotli, 5), Codec.Brotli),
            ("brotli 6", r => Compress(r, Codec.Brotli, 6), Codec.Brotli),
            ("brotli 9", r => Compress(r, Codec.Brotli, 9), Codec.Brotli),
        ];

        public void Add(byte[] a, byte[] b, byte[]? plainB)
        {
            foreach (var (label, raw) in new[] { ("", Join(a, b)), (" unshuffled", plainB is null ? null : Join(a, plainB)) })
            {
                if (raw is null)
                    continue;
                foreach (var (name, pack, codec) in Ways)
                {
                    var watch = Stopwatch.StartNew();
                    var packed = pack(raw);
                    var packing = watch.ElapsedTicks;
                    watch.Restart();
                    using (var s = Decompress(packed, 0, packed.Length, codec))
                        s.ReadExactly(new byte[raw.Length]);
                    var unpacking = watch.ElapsedTicks;
                    lock (_lock)
                    {
                        var t = _totals.GetValueOrDefault(name + label);
                        _totals[name + label] = (t.Size + packed.Length, t.Pack + packing, t.Unpack + unpacking);
                    }
                }

                lock (_lock)
                {
                    if (label.Length == 0)
                        _raw += raw.Length;
                    else
                        _rawPlain += raw.Length;
                }
            }
        }

        private static byte[] Join(byte[] a, byte[] b) => [.. a, .. b];

        public void Log()
        {
            var ms = 1000.0 / Stopwatch.Frequency;
            RailWerkz.Core.Logging.Log.Info($"Cell measure: raw {_raw / (1 << 20)} MB; " + string.Join("; ", _totals.OrderBy(t => t.Key).Select(t =>
                $"{t.Key}: {t.Value.Size / 1024 / 1024.0:0.0} MB, pack {t.Value.Pack * ms:0} ms, unpack {t.Value.Unpack * ms:0} ms (one thread, summed)")));
        }
    }
}
