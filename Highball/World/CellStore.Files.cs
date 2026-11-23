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

    /// Where what goes into a cell not being written goes (and is let go).
    [ThreadStatic] private static CellData? t_scratch;
    private static CellData Scratch => t_scratch ??= new CellData();

    /// A nested field's element: its round in the high bits of its place, what it is hashed in the rest.
    private const int RoundShift = 48;
    private const long RoundMask = (1L << RoundShift) - 1;

    /// An element's place in its list, set or dictionary, the same whichever cells are written with it and whatever else is in it (so
    /// cells written at different times fit together): what it is, hashed. A dictionary's entry by its key; a set's or list's item by
    /// itself: a line by its name, a registered thing (registered) by what it is (IdentityOf) and all it holds (StableKey). Lists are
    /// read back in this order, sets and dictionaries by it.
    private static long OrdinalOf(object? key, bool registered = false)
    {
        var hash = new StableHash();
        StableKey(ref hash, key, registered ? 0 : 1);
        if (registered && key is not null)
            hash.Add(IdentityOf(key));
        return (long)hash.Value & long.MaxValue;
    }

    private static void StableKey(ref StableHash hash, object? o, int depth)
    {
        switch (o)
        {
            case null:
                hash.Add(-7L);
                return;
            case NetworkLine line:
                hash.Add(line.Name);
                return;
            case string s:
                hash.Add(s);
                return;
            case double d:
                hash.Add(d);
                return;
            case float f:
                hash.Add((long)BitConverter.SingleToInt32Bits(f));
                return;
            case bool b:
                hash.Add(b ? 1L : 0L);
                return;
            case Enum e:
                hash.Add(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case IConvertible c when o.GetType().IsPrimitive:
                hash.Add(c.ToInt64(System.Globalization.CultureInfo.InvariantCulture));
                return;
            case TileId t:
                hash.Add(t.I);
                hash.Add(t.J);
                return;
        }

        var type = o.GetType();
        if (depth > 3)
            return;
        if (depth > 0 && !type.IsValueType && type.Assembly == typeof(CellStore).Assembly)
        {
            // (Another thing in it, by what it is.)
            hash.Add(IdentityOf(o));
            return;
        }

        if (o is Array array && GraphSnapshot.IsBlittable(type.GetElementType()!))
        {
            hash.Add(array.Length);
            foreach (var b in BytesOf(array))
                hash.Add(b);
            return;
        }

        if (o is IEnumerable items)
        {
            foreach (var item in items)
                StableKey(ref hash, item, depth + 1);
            return;
        }

        foreach (var field in GraphSnapshot.FieldsOf(type))
            if (!field.FieldType.IsSubclassOf(typeof(Delegate)) && field.FieldType != typeof(Lock))
                StableKey(ref hash, field.GetValue(o), depth + 1);
    }

    /// What's going into one cell (or into the index: what has no cell).
    private sealed class CellData
    {
        public readonly List<Piece> Pieces = [];
        public readonly List<int> Heads = [];
        public readonly List<Element> Elements = [];
        /// The lines reaching this cell's ground (its tile's _byTile) with no piece in it: a window complete here loads them too.
        public readonly List<int> ReachLines = [];
        /// The other cells the lines in it (with a piece, or reaching its ground) have pieces in: what a window complete here reads too.
        public readonly HashSet<TileId> Reach = [];
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
    /// anything's put in place (null: it is). only: the cells to write (those an edit changed since the last write, CellStore.Merge),
    /// the rest left as they are on disk, their entries in the index kept; null, or no index of this format to keep them from: all.
    /// (Every reference and every element's place in its list, set or dictionary is stable, OrdinalOf, so a cell left as it was still
    /// fits the ones written.)
    /// Returns whether the cells were put in place.
    /// A window of the lines (NetworkLines.Window) writes only cells it's complete in, and only with an index of the rest to keep: never
    /// all of them (null only: every cell it's complete in).
    public static bool Write(RouteInfo route, ulong stamp, NetworkLines lines, Func<bool>? current = null, IReadOnlySet<TileId>? only = null)
    {
        var window = lines.Window;
        if (window is not null)
            only ??= window.Complete.Where(c => !window.Tainted.Contains(c)).ToHashSet();
        var old = only is null ? null : ReadIndex(route);
        if (old is not null && (old.Rules != RulesHash || old.BuildVersion != BuildVersion || old.Cells.Keys.Any(id => !File.Exists(CellPath(route, id)))))
            old = null;
        if (old is null)
            only = null;
        if (route.Pack is not null)
            return false;
        if (window is not null && (old is null || !window.Writes(only!)))
        {
            Log.Warn(old is null
                ? "Cells: not written: the lines are a window over some cells, and there's no index of this format to keep the rest by"
                : $"Cells: not written: {only!.Count(c => !window.Complete.Contains(c))} of the cells to write aren't loaded whole, {only!.Count(window.Tainted.Contains)} name what isn't loaded");
            return false;
        }
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
                if (!all[i].IsAbsent)
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
            CellData At2(TileId? id) => only is null || id is null || only.Contains(id.Value) ? At(id) : Scratch;
            // (Which cells each line has pieces in, for each cell's reach.)
            var lineCells = new HashSet<TileId>?[all.Count];
            for (var i = 0; i < all.Count; i++)
            {
                var line = all[i];
                if (line.IsAbsent)
                    continue;
                var slices = Cuttable.Select(f => (f, CutOf((Array?)LineFields[f].GetValue(line), line.Count))).Where(s => s.Item2.Cut != Cut.None)
                    .Select(s => new Slice(s.f, s.Item2.Cut, s.Item2.Stride)).ToArray();
                if (line.Count == 0)
                {
                    var data = At2(c.CellOf(0, 0));
                    data.Pieces.Add(new Piece(i, 0, 0, 0, line.Key, slices));
                    data.Heads.Add(i);
                    lineCells[i] = [c.CellOf(0, 0)];
                    continue;
                }

                var (start, cell) = (0, c.CellOf(line.X[0], line.Z[0]));
                var mine = lineCells[i] = [];
                At2(cell).Heads.Add(i);
                for (var p = 1; p <= line.Count; p++)
                {
                    var next = p < line.Count ? c.CellOf(line.X[p], line.Z[p]) : default;
                    if (p < line.Count && next == cell)
                        continue;
                    At2(cell).Pieces.Add(new Piece(i, line.Count, start, p - start, line.Key, slices));
                    mine.Add(cell);
                    (start, cell) = (p, next);
                }
            }

            // Each cell's reach: the cells its lines (and those reaching its ground) have pieces in.
            bool Writes(TileId cell) => only is null || only.Contains(cell);
            for (var i = 0; i < all.Count; i++)
                if (lineCells[i] is { } mine)
                    foreach (var cell in mine)
                        if (Writes(cell))
                            At(cell).Reach.UnionWith(mine);
            foreach (var (tile, list) in (Dictionary<TileId, List<int>>)RootFields.First(f => f.Name == "_byTile").GetValue(lines)!)
            {
                if (!Writes(tile))
                    continue;
                var data = At(tile);
                foreach (var i in list)
                    if (i >= 0 && i < all.Count && lineCells[i] is { } mine)
                    {
                        data.Reach.UnionWith(mine);
                        if (!mine.Contains(tile))
                            data.ReachLines.Add(i);
                    }
            }

            foreach (var (cell, data) in cells)
                data.Reach.Remove(cell);

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
                var values = RegisteredValues(f);
                var count = 0;
                var used = new HashSet<long>();
                void Add(long ordinal, object? key, object? v)
                {
                    // (Its place: what it is, hashed, OrdinalOf; one alike already there takes the next.)
                    ordinal = registered || values ? (int)ordinal : ordinal;
                    while (!used.Add(ordinal))
                        ordinal = registered || values ? (int)(ordinal + 1) : ordinal + 1;
                    var own = (registered && key is not null && registry.TryAdd(key, (2 + fi, (int)ordinal))) ||
                              (values && v is not null && registry.TryAdd(v, (2 + fi, (int)ordinal)));
                    var cell = rule.Anchor!(c, key, v);
                    if (cell is null)
                        unplaced[fi]++;
                    if (only is null || cell is null || only.Contains(cell.Value))
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
                        {
                            Add(((long)o << RoundShift) | (OrdinalOf(e.Key) & RoundMask), e.Key, e.Value);
                            k++;
                        }

                        inner[o] = k;
                    }
                }
                else if (value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry e in dictionary)
                        Add(OrdinalOf(e.Key), e.Key, e.Value);
                }
                else
                {
                    foreach (var item in (IEnumerable)value)
                        Add(OrdinalOf(item, registered), item, null);
                }

                // (A window's counts aren't the route's: -1, not checked when read.)
                metas[fi] = window is null ? new SpreadMeta(f.Name, true, count, inner) : new SpreadMeta(f.Name, true, -1, inner is null ? null : [.. inner.Select(_ => -1)]);
            }

            Scratch.Pieces.Clear();
            Scratch.Heads.Clear();
            // (A field that was left out when the cells kept were written, or held there and is left out now, is in cells not written:
            // they're all written. Bridges, found when the meshes are first built, are one.)
            if (old is not null && window is null && metas.Zip(old.Spread).Any(m => m.First.Present != m.Second.Present || m.First.Name != m.Second.Name))
            {
                Log.Info("Cells: a field's come or gone since the cells kept were written, writing them all");
                return Write(route, stamp, lines, current, null);
            }

            var distributed = watch.ElapsedMilliseconds;
            (int, int)? External(object o) => registry.TryGetValue(o, out var at) ? at : null;

            // The cells, written side by side.
            var ids = cells.Keys.Where(id => only is null || only.Contains(id)).OrderBy(t => t.I).ThenBy(t => t.J).ToArray();
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
            var schema = FixedTypes().Concat(results.SelectMany(r => r.Types)).Concat(restTypes)
                .Concat(old?.Schema.Select(s => Type.GetType(s.Type)).OfType<Type>() ?? []).Distinct().Where(t => !t.ContainsGenericParameters)
                .OrderBy(GraphSnapshot.NameOf, StringComparer.Ordinal).ToList();
            // (The cells not written, as they are on disk; those written empty now gone.)
            var keptCells = old is null ? [] : old.Cells.Where(e => !only!.Contains(e.Key)).OrderBy(e => e.Key.I).ThenBy(e => e.Key.J).ToList();
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

                void WriteReach(IReadOnlyCollection<TileId> reach)
                {
                    w.Write(reach.Count);
                    foreach (var r in reach.OrderBy(t => t.I).ThenBy(t => t.J))
                    {
                        w.Write(r.I);
                        w.Write(r.J);
                    }
                }

                w.Write(ids.Length + keptCells.Count);
                for (var k = 0; k < ids.Length; k++)
                {
                    w.Write(ids[k].I);
                    w.Write(ids[k].J);
                    w.Write(results[k].Size);
                    w.Write(results[k].Hash);
                    WriteReach(cells[ids[k]].Reach);
                }

                foreach (var (id, (size, hash)) in keptCells)
                {
                    w.Write(id.I);
                    w.Write(id.J);
                    w.Write(size);
                    w.Write(hash);
                    WriteReach(old!.Reach.GetValueOrDefault(id) ?? []);
                }

                w.Write(restFile.Length);
                w.Write(restFile);
            }

            written.Add((new TileId(int.MinValue, int.MinValue), index, new FileInfo(index).Length, 0));

            if (current is not null && !current())
            {
                Log.Info("Cells: saved again while they were being written, left for the next save");
                return false;
            }

            foreach (var (id, temporary, _, _) in written.Where(x => x.Id.I != int.MinValue))
                File.Move(temporary, CellPath(route, id), true);
            File.Move(index, IndexPath(route), true);
            written.Clear();

            // (Cells no longer in the set, and the compiled route these replace, which nothing reads.)
            var keep = ids.Concat(keptCells.Select(e => e.Key)).Select(id => $"{id}.cell").ToHashSet();
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
            NetworkLines.ForgetSolved(route);

            var sizes = results.Select(r => r.Size).Order().ToArray();
            var biggest = ids.Length == 0 ? default : ids[Array.IndexOf(results.Select(r => r.Size).ToArray(), sizes[^1])];
            Log.Info($"Cells: {ids.Length} cells written{(old is null ? "" : $" of {ids.Length + keptCells.Count} ({only!.Count} asked for, {keptCells.Count} left as they were)")} ({sizes.Sum() / (1 << 20)} MB, from {results.Sum(r => r.RawA + r.RawB) / (1 << 20)} MB; " +
                     $"biggest {(sizes.Length > 0 ? sizes[^1] / 1024 : 0)} KB at {biggest}, median {(sizes.Length > 0 ? sizes[sizes.Length / 2] / 1024 : 0)} KB; " +
                     $"index {new FileInfo(IndexPath(route)).Length / 1024} KB, {schema.Count} types), {all.Count} lines in " +
                     $"{cells.Values.Sum(d => d.Pieces.Count)} pieces, {metas.Sum(m => m.Count)} things spread ({string.Join(", ", unplaced.Select((n, fi) => (n, fi)).Where(x => x.n > 0).Select(x => $"{x.n} of {SpreadFields[x.fi].Name}"))} without a cell, in the index), " +
                     $"{stale} stale removed; ms: spread {distributed}, encode {encoded - distributed}, written {watch.ElapsedMilliseconds - encoded}");
            Log.Info($"Cells: raw bytes by what: {string.Join(", ", bytesBy.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value / 1024} KB"))}");
            measures?.Log();

            // HB_CELLCHECK: what was written read straight back, against the lines it was written from (after an edit as on open).
            if (Check && window is null && ReadIndex(route) is { } again && Read(route, again, again.Cells.Keys) is { } back)
                Compare(back, lines, (int)RootFields.First(f => f.Name == "_quads").GetValue(lines)! + 1, "as written");
            // (A window's: read back as the same window, the cells written and those left as they were together.)
            else if (Check && window is not null && ReadIndex(route) is { } again2 && Read(route, again2, window.Complete) is { } back2)
                Compare(back2, lines, (int)RootFields.First(f => f.Name == "_quads").GetValue(lines)! + 1, "as written, a window",
                    Remade.Append("_byChain").Append("_detailRegions").Append("_detailCells").ToHashSet());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or TargetInvocationException or InvalidCastException)
        {
            Log.Warn($"Cells: couldn't write them ({(e as TargetInvocationException)?.InnerException?.Message ?? e.Message})");
            return false;
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
        static object Thing(Element e) => RegisteredValues(SpreadFields[e.Field]) ? e.Value! : e.Key!;
        var types = owned.Select(e => Thing(e).GetType()).Distinct().ToList();

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
                w.Write(types.IndexOf(Thing(e).GetType()));
            }

            w.Write(d.ReachLines.Count);
            foreach (var i in d.ReachLines)
                w.Write(i);
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
                        if (RegisteredValues(f))
                        {
                            w.Write(e.Own);
                            if (e.Own)
                            {
                                g.WriteFields(e.Value!);
                                continue;
                            }
                        }

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
