using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// What a window of the route's lines holds (CellStore.Read of some cells): the cells it's complete in (every line with a point there or
/// reaching its ground is loaded, and everything anchored there: what can be shown, shaped, edited and written again), every cell read for
/// it (those and the cells the lines loaded reach into, CellStore.Index.Reach), and the complete cells something read refers to a line or a
/// thing outside the window from (Tainted: what's anchored there names something not loaded, so the cell isn't written from it), and
/// what was read from cells it isn't complete in (Foreign, by field and place: never written from the window, wherever its anchor
/// comes out with only the window's lines to go by, a node or a chain on lines not loaded giving none), and what was read from the
/// index's own part (Rest: what has no cell; a window writes only that there, anything else of no cell may be in a cell not loaded).
public sealed record CellWindow(IReadOnlySet<TileId> Complete, IReadOnlySet<TileId> Read, IReadOnlySet<TileId> Tainted, IReadOnlySet<(int Field, long Ordinal)> Foreign,
    IReadOnlySet<(int Field, long Ordinal)> Rest)
{
    /// Whether every one of these cells can be written from the window.
    public bool Writes(IEnumerable<TileId> cells) => cells.All(c => Complete.Contains(c) && !Tainted.Contains(c));
}

internal static partial class CellStore
{
    /// Cells/index.bin: which cells there are, what they were built from and by what, what types they hold, and what's route-wide.
    public sealed class Index
    {
        public required int BuildVersion { get; init; }
        public required ulong Stamp { get; init; }
        public required ulong Rules { get; init; }
        public required int LineCount { get; init; }
        public required List<(string Type, ulong Layout)> Schema { get; init; }
        public required SpreadMeta[] Spread { get; init; }
        public required Dictionary<TileId, (long Size, ulong Hash)> Cells { get; init; }
        /// Each cell's reach: the other cells the lines with a piece in it, or reaching its ground, have pieces in (what a window complete
        /// in it reads too, ReadFor).
        public required Dictionary<TileId, TileId[]> Reach { get; init; }
        /// The index's own part, laid out as a cell: what has no cell, and the route-wide fields.
        public required byte[] Rest { get; init; }

        /// Why the cells can't be read as they are (null: they can): a build version or inputs not theirs, or a type they hold laid
        /// out differently in this build.
        public string? Stale(ulong stamp)
        {
            if (BuildVersion != CellStore.BuildVersion)
                return $"built by build version {BuildVersion}, this is {CellStore.BuildVersion}";
            if (Rules != RulesHash)
                return "written by a build that kept NetworkLines' fields in other places (CellStore.Rules)";
            if (Stamp != stamp)
                return "the map, edits, settings or ground have changed since";
            foreach (var (name, layout) in Schema)
            {
                var type = Type.GetType(name);
                if (type is null)
                    return $"no type {name} in this build";
                if (GraphSnapshot.LayoutOf(type) != layout)
                    return $"{GraphSnapshot.NameOf(type)} is laid out differently in this build";
            }

            return null;
        }
    }

    /// The index, if there's one this build can read (of this format).
    public static Index? ReadIndex(RouteInfo route)
    {
        if (Off || route.Pack is not null)
            return null;
        var path = IndexPath(route);
        if (!File.Exists(path))
        {
            Log.Info("Cells: none yet, working the lines out");
            return null;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(file);
            if (r.ReadInt32() != IndexMagic)
                throw new InvalidDataException("not an index");
            if (r.ReadInt32() is var format && format != FormatVersion)
            {
                Log.Info($"Cells: format {format}, this build reads {FormatVersion}; working the lines out");
                return null;
            }

            var build = r.ReadInt32();
            var stamp = r.ReadUInt64();
            var rules = r.ReadUInt64();
            var lineCount = r.ReadInt32();
            var schema = new List<(string, ulong)>();
            for (var n = r.ReadInt32(); n > 0; n--)
                schema.Add((r.ReadString(), r.ReadUInt64()));
            var spread = new SpreadMeta[r.ReadInt32()];
            for (var i = 0; i < spread.Length; i++)
            {
                var (name, present, count, inner) = (r.ReadString(), r.ReadBoolean(), r.ReadInt32(), r.ReadInt32());
                var counts = inner < 0 ? null : new int[inner];
                for (var k = 0; k < inner; k++)
                    counts![k] = r.ReadInt32();
                spread[i] = new SpreadMeta(name, present, count, counts);
            }

            var cells = new Dictionary<TileId, (long, ulong)>();
            var reach = new Dictionary<TileId, TileId[]>();
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var id = new TileId(r.ReadInt32(), r.ReadInt32());
                cells[id] = (r.ReadInt64(), r.ReadUInt64());
                var to = new TileId[r.ReadInt32()];
                for (var k = 0; k < to.Length; k++)
                    to[k] = new TileId(r.ReadInt32(), r.ReadInt32());
                reach[id] = to;
            }

            var rest = r.ReadBytes(r.ReadInt32());
            return new Index { BuildVersion = build, Stamp = stamp, Rules = rules, LineCount = lineCount, Schema = schema, Spread = spread, Cells = cells, Reach = reach, Rest = rest };
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Cells: index unreadable ({e.Message}), working the lines out");
            return null;
        }
    }

    /// One cell's file, its block A read: its pieces, the things it makes, and the lines reaching its ground from elsewhere.
    private sealed class CellFile
    {
        public required TileId Id;
        public required byte[] Bytes;
        public required Codec Codec;
        public required bool Shuffle;
        public required int OffsetB, PackedB;
        public readonly List<Piece> Pieces = [];
        public readonly List<(int Kind, int Ordinal, Type Type)> Owned = [];
        public readonly List<Element> Elements = [];
        public readonly List<int> ReachLines = [];
    }

    private static CellFile Open(TileId id, byte[] bytes)
    {
        using var header = new BinaryReader(new MemoryStream(bytes, false));
        if (header.ReadInt32() != CellMagic || header.ReadInt32() != FormatVersion || header.ReadInt32() != id.I || header.ReadInt32() != id.J)
            throw new InvalidDataException($"cell {id}: not this cell, or not this format");
        var codec = (Codec)header.ReadByte();
        var shuffle = header.ReadBoolean();
        var (_, packedA, _, packedB) = (header.ReadInt32(), header.ReadInt32(), header.ReadInt32(), header.ReadInt32());
        var offsetA = (int)header.BaseStream.Position;
        var cell = new CellFile { Id = id, Bytes = bytes, Codec = codec, Shuffle = shuffle, OffsetB = offsetA + packedA, PackedB = packedB };

        using var r = new BinaryReader(Decompress(bytes, offsetA, packedA, codec));
        for (var n = r.ReadInt32(); n > 0; n--)
        {
            var (line, lineCount, first, count, key) = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt64());
            var slices = new Slice[r.ReadByte()];
            for (var s = 0; s < slices.Length; s++)
                slices[s] = new Slice(r.ReadByte(), (Cut)r.ReadByte(), r.ReadByte());
            // (Blocks a later format added: skipped.)
            for (var blocks = r.ReadInt32(); blocks > 0; blocks--)
            {
                _ = r.ReadInt32();
                r.ReadBytes(r.ReadInt32());
            }

            cell.Pieces.Add(new Piece(line, lineCount, first, count, key, slices));
        }

        var types = new Type[r.ReadInt32()];
        for (var t = 0; t < types.Length; t++)
            types[t] = Type.GetType(r.ReadString()) ?? throw new InvalidDataException($"cell {id}: a type this build hasn't");
        for (var n = r.ReadInt32(); n > 0; n--)
            cell.Owned.Add((r.ReadInt32(), r.ReadInt32(), types[r.ReadInt32()]));
        for (var n = r.ReadInt32(); n > 0; n--)
            cell.ReachLines.Add(r.ReadInt32());
        return cell;
    }

    /// The cells within rings of a cell (with anything in them or not).
    public static HashSet<TileId> Around(TileId cell, int rings)
    {
        var around = new HashSet<TileId>();
        for (var dj = -rings; dj <= rings; dj++)
            for (var di = -rings; di <= rings; di++)
                around.Add(new TileId(cell.I + di, cell.J + dj));
        return around;
    }

    /// What a window complete in these cells reads: them, one ring round them (the lines there are loaded too, so what's anchored in
    /// the complete cells finds the lines near it), and every cell the lines of those reach into (Index.Reach). All of them for the
    /// whole route.
    public static HashSet<TileId> ReadFor(Index index, IReadOnlySet<TileId> complete)
    {
        if (index.Cells.Keys.All(complete.Contains))
            return [.. index.Cells.Keys];
        var near = Ring(index, complete);
        var read = new HashSet<TileId>(near);
        foreach (var c in near)
            foreach (var r in index.Reach.GetValueOrDefault(c) ?? [])
                if (index.Cells.ContainsKey(r))
                    read.Add(r);
        return read;
    }

    /// These cells and the ring round them (those there are).
    private static HashSet<TileId> Ring(Index index, IReadOnlySet<TileId> cells)
    {
        var near = new HashSet<TileId>();
        foreach (var c in cells)
            for (var dj = -1; dj <= 1; dj++)
                for (var di = -1; di <= 1; di++)
                    if (index.Cells.ContainsKey(new TileId(c.I + di, c.J + dj)))
                        near.Add(new TileId(c.I + di, c.J + dj));
        return near;
    }

    /// The lines in a set of cells, as one NetworkLines (with what Restore puts back still to put back). Every cell in the index: the
    /// whole route, as it was written. Some of them: a window (NetworkLines.Window, docs/cell-routes.md, Streaming), complete in those
    /// cells (cells: they're asked for; any not in the index have nothing in them), reading those, the ring round them and the cells
    /// their lines reach (ReadFor). A window's lines keep their places in the route's set, the places of lines not loaded holding
    /// NetworkLine.Absent. What's read naming a line or thing not loaded: dropped where it's an item of a list or set, or a dictionary's
    /// key, or anywhere in a cell not complete; else the name read as null (its cell is then Tainted: not written from the window).
    public static NetworkLines? Read(RouteInfo route, Index index, IEnumerable<TileId> cells)
    {
        var watch = Stopwatch.StartNew();
        var times = new List<string>();
        void Time(string name)
        {
            times.Add($"{name} {watch.ElapsedMilliseconds}");
            watch.Restart();
        }

        try
        {
            // (A cell with nothing in it, not in the index, is complete in any window that asks for it: there's nothing more to load.)
            var complete = cells.ToHashSet();
            var toRead = ReadFor(index, complete);
            var whole = toRead.Count == index.Cells.Count && index.Cells.Keys.All(complete.Contains);
            var ids = toRead.ToArray();
            var files = new CellFile[ids.Length + 1];
            long bytes = 0;
            Parallel.For(0, ids.Length + 1, k =>
            {
                if (k == ids.Length)
                {
                    files[k] = Open(new TileId(int.MinValue, int.MinValue), index.Rest);
                    return;
                }

                var data = File.ReadAllBytes(CellPath(route, ids[k]));
                if (!index.Cells.TryGetValue(ids[k], out var expected) || expected.Size != data.Length || expected.Hash != HashOf(data))
                    throw new InvalidDataException($"cell {ids[k]} isn't the one the index was written with");
                Interlocked.Add(ref bytes, data.Length);
                files[k] = Open(ids[k], data);
            });
            Time("files");

            // The lines loaded: whole, every one with a piece in the complete cells or the ring round them, or reaching the complete
            // cells' ground. The rest of the places hold Absent.
            var near = whole ? complete : Ring(index, complete);
            var wanted = new bool[index.LineCount];
            foreach (var f in files)
            {
                if (!whole && f.Id.I != int.MinValue && !near.Contains(f.Id))
                    continue;
                foreach (var p in f.Pieces)
                    if (p.Line >= 0 && p.Line < wanted.Length)
                        wanted[p.Line] = true;
                if (whole || complete.Contains(f.Id))
                    foreach (var i in f.ReachLines)
                        if (i >= 0 && i < wanted.Length)
                            wanted[i] = true;
            }

            // Every line and every thing that's referred to, made (empty) before any's read, so a reference finds it whichever cell it's in.
            var lines = new NetworkLine[index.LineCount];
            for (var i = 0; i < lines.Length; i++)
                lines[i] = wanted[i] ? (NetworkLine)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLine)) : NetworkLine.Absent;
            var arrays = new Array[index.LineCount][];
            var pieces = files.SelectMany(f => f.Pieces).Where(p => p.Line >= 0 && p.Line < wanted.Length && wanted[p.Line])
                .GroupBy(p => p.Line).ToDictionary(g => g.Key, g => g.OrderBy(p => p.First).ToList());
            var loaded = wanted.Count(w => w);
            if (pieces.Count != loaded)
                throw new InvalidDataException($"{pieces.Count} lines in the cells read, {loaded} wanted");
            if (whole && loaded != lines.Length && index.Spread.Length > 0)
                Log.Info($"Cells: {lines.Length - loaded} places of {lines.Length} hold no line");
            Parallel.ForEach(pieces, entry =>
            {
                var (i, list) = (entry.Key, entry.Value);
                var head = list[0];
                var at = 0;
                foreach (var p in list)
                {
                    if (p.First != at || p.LineCount != head.LineCount || p.Key != head.Key || !p.Slices.AsSpan().SequenceEqual(head.Slices))
                        throw new InvalidDataException($"line {i}: its pieces don't fit together");
                    at += p.Count;
                }

                if (at != head.LineCount)
                    throw new InvalidDataException($"line {i}: {at} points in its pieces, {head.LineCount} in the line");
                arrays[i] = new Array[head.Slices.Length];
                for (var s = 0; s < head.Slices.Length; s++)
                {
                    var slice = head.Slices[s];
                    var f = LineFields[slice.Field];
                    arrays[i][s] = Array.CreateInstance(f.FieldType.GetElementType()!, slice.Cut == Cut.Points ? head.LineCount * slice.Stride : Math.Max(head.LineCount - 1, 0));
                    f.SetValue(lines[i], arrays[i][s]);
                }
            });

            var root = (NetworkLines)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLines));
            if (index.Spread.Length != SpreadFields.Length || index.Spread.Where((m, fi) => m.Name != SpreadFields[fi].Name).Any())
                throw new InvalidDataException("the spread fields aren't this build's");
            var made = new Dictionary<int, object>[SpreadFields.Length];
            for (var fi = 0; fi < SpreadFields.Length; fi++)
                made[fi] = [];
            foreach (var f in files)
                foreach (var (kind, ordinal, type) in f.Owned)
                    if (!made[kind - 2].TryAdd(ordinal, RuntimeHelpers.GetUninitializedObject(type)))
                        throw new InvalidDataException($"{SpreadFields[kind - 2].Name} {ordinal} in two cells");
            Time("made");

            // What a window's read couldn't find (a line or thing not loaded), by field, and the complete cells that named it.
            var missing = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var foreign = new System.Collections.Concurrent.ConcurrentDictionary<(int, long), byte>();
            var fromRest = new System.Collections.Concurrent.ConcurrentDictionary<(int, long), byte>();
            var tainted = new System.Collections.Concurrent.ConcurrentDictionary<TileId, byte>();
            var lineIndexed = SpreadFields.Select(f => f.Name is "_byTile" or "_pointsByTile" or "_needGround" || KeyIndexFields.Contains(f.Name) || ValueIndexFields.Contains(f.Name)).ToArray();
            // (What only a complete cell's lines can stand behind: the lines' tile indexes, and the detail regions shaped finer from them.)
            var byTileOnly = SpreadFields.Select(f => f.Name is "_byTile" or "_pointsByTile" or "_ballast" or "_detailRegions" or "_detailCells").ToArray();

            // Each cell's arrays, heads and things.
            Parallel.For(0, files.Length, k =>
            {
                var file = files[k];
                var isRest = file.Id.I == int.MinValue;
                var full = whole || isRest || complete.Contains(file.Id);
                using var stream = Decompress(file.Bytes, file.OffsetB, file.PackedB, file.Codec);
                using var r = new BinaryReader(stream);
                var buffer = Array.Empty<byte>();
                foreach (var p in file.Pieces)
                    for (var s = 0; s < p.Slices.Length; s++)
                    {
                        if (arrays[p.Line] is { } into)
                            ReadSlice(stream, into[s], p.Slices[s], p.First, p.Count, p.LineCount, file.Shuffle, ref buffer);
                        else
                            SkipSlice(stream, p.Slices[s], p.First, p.Count, p.LineCount, ref buffer);
                    }

                var unfound = false;
                object? Resolve(int kind, int i)
                {
                    switch (kind)
                    {
                        case 0:
                            return root;
                        case 1:
                            if (i >= 0 && i < lines.Length && !lines[i].IsAbsent)
                                return lines[i];
                            if (whole)
                                throw new InvalidDataException($"line {i} isn't in the cells read");
                            unfound = true;
                            return null;
                        default:
                            if (made[kind - 2].GetValueOrDefault(i) is { } thing)
                                return thing;
                            if (whole)
                                throw new InvalidDataException($"{SpreadFields[kind - 2].Name} {i} isn't in the cells read");
                            unfound = true;
                            return null;
                    }
                }

                void Missed(string what)
                {
                    missing.AddOrUpdate(what, 1, (_, n) => n + 1);
                    if (full && !isRest)
                        tainted.TryAdd(file.Id, 0);
                }

                bool Loaded(int i) => i >= 0 && i < lines.Length && !lines[i].IsAbsent;

                var g = new GraphSnapshot.Reader(r, null, (kind, i) => Resolve(kind, i)!);
                var scratch = (NetworkLine)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLine));
                for (var n = r.ReadInt32(); n > 0; n--)
                {
                    var at = r.ReadInt32();
                    var line = Loaded(at) ? lines[at] : scratch;
                    var mask = r.ReadUInt64();
                    unfound = false;
                    for (var j = 0; j < LineFields.Length; j++)
                        if ((mask & (1UL << j)) == 0 && !LineDropped.Contains(LineFields[j].Name))
                            LineFields[j].SetValue(line, g.Read(LineFields[j].FieldType));
                    if (unfound && line != scratch)
                        Missed("line heads");
                }

                for (var groups = r.ReadInt32(); groups > 0; groups--)
                {
                    var fi = r.ReadInt32();
                    var f = SpreadFields[fi];
                    var (keyType, valueType, itemType) = ElementTypes(f, Rules[f.Name]);
                    var registered = Registered(f);
                    for (var n = r.ReadInt32(); n > 0; n--)
                    {
                        var ordinal = r.ReadInt64();
                        Element? e;
                        bool keyUnfound, valueUnfound = false;
                        unfound = false;
                        if (registered)
                        {
                            object? item;
                            if (r.ReadBoolean())
                                g.ReadInto(item = made[fi][(int)ordinal]);
                            else
                                item = g.Read(itemType!);
                            keyUnfound = unfound || item is null;
                            e = new Element(fi, ordinal, item, null, false);
                        }
                        else if (keyType is not null)
                        {
                            var key = g.Read(keyType);
                            keyUnfound = unfound;
                            unfound = false;
                            object? value;
                            if (RegisteredValues(f) && r.ReadBoolean())
                                g.ReadInto(value = made[fi][(int)ordinal]);
                            else
                                value = g.Read(valueType!);
                            valueUnfound = unfound;
                            // (A list of lines with some not loaded: those taken out.)
                            if (valueUnfound && value is List<NetworkLine> some)
                            {
                                some.RemoveAll(l => l is null);
                                valueUnfound = false;
                                if (full && !isRest)
                                    Missed(f.Name + " (lines in a list)");
                            }

                            e = new Element(fi, ordinal, key, value, false);
                        }
                        else
                        {
                            var item = g.Read(itemType!);
                            keyUnfound = unfound;
                            e = new Element(fi, ordinal, item, null, false);
                        }

                        if (whole)
                        {
                            file.Elements.Add(e.Value);
                            continue;
                        }

                        // (A window's: only what it can stand behind. The lines' tile indexes in complete cells alone; anything naming a
                        // line by its place, only if that line's loaded.)
                        if (byTileOnly[fi] && !full)
                            continue;
                        // (Places are the route's, so a complete cell's are kept as they are, loaded or not; a cell not complete keeps
                        // only those of lines loaded.)
                        if (lineIndexed[fi] && !full && !IndexedLoaded(e.Value.Key, e.Value.Value, Loaded))
                            continue;

                        if (keyUnfound || (valueUnfound && !full))
                        {
                            if (full)
                                Missed(f.Name);
                            continue;
                        }

                        if (valueUnfound)
                            Missed(f.Name + " (kept, naming what's not loaded)");
                        if (!full)
                            foreign.TryAdd((fi, ordinal), 0);
                        else if (isRest)
                            fromRest.TryAdd((fi, ordinal), 0);
                        file.Elements.Add(e.Value);
                    }
                }

                if (isRest)
                    foreach (var f in RootFields.Where(f => RuleOf(f).Place == Place.Route))
                        f.SetValue(root, g.Read(f.FieldType));
            });
            Time("read");

            // The route-wide lists, sets and dictionaries, put together in the order they were in.
            var elements = files.SelectMany(f => f.Elements).GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Ordinal).ToList());
            for (var fi = 0; fi < SpreadFields.Length; fi++)
            {
                var (f, meta, rule) = (SpreadFields[fi], index.Spread[fi], Rules[SpreadFields[fi].Name]);
                if (!meta.Present)
                {
                    f.SetValue(root, null);
                    continue;
                }

                var list = elements.GetValueOrDefault(fi) ?? [];
                if (whole && meta.Count >= 0 && list.Count != meta.Count)
                    throw new InvalidDataException($"{f.Name}: {list.Count} in the cells read, {meta.Count} written");
                var container = Activator.CreateInstance(f.FieldType)!;
                if (rule.Nested)
                {
                    var outer = (IList)container;
                    var innerType = f.FieldType.GetGenericArguments()[0];
                    var rounds = list.GroupBy(e => e.Ordinal >> RoundShift).ToDictionary(g => g.Key, g => g.ToList());
                    for (var o = 0; o < meta.Inner!.Length; o++)
                    {
                        var inner = (IDictionary)Activator.CreateInstance(innerType)!;
                        var round = rounds.GetValueOrDefault(o) ?? [];
                        if (whole && meta.Inner[o] >= 0 && round.Count != meta.Inner[o])
                            throw new InvalidDataException($"{f.Name}[{o}]: {round.Count} read, {meta.Inner[o]} written");
                        foreach (var e in round)
                        {
                            if (e.Value is null)
                                throw new InvalidDataException($"{f.Name}[{o}]: no value for {e.Key}");
                            inner.Add(e.Key!, e.Value);
                        }

                        outer.Add(inner);
                    }
                }
                else
                {
                    for (var n = 1; n < list.Count; n++)
                        if (list[n].Ordinal == list[n - 1].Ordinal)
                            throw new InvalidDataException($"{f.Name}: two at {list[n].Ordinal}");
                    switch (container)
                    {
                        case IDictionary dictionary:
                            foreach (var e in list)
                                if (whole)
                                    dictionary.Add(e.Key!, e.Value);
                                else
                                    dictionary[e.Key!] = e.Value;
                            break;
                        case IList items:
                            foreach (var e in list)
                                items.Add(e.Key);
                            break;
                        default:
                            var add = f.FieldType.GetMethod("Add")!;
                            foreach (var e in list)
                                add.Invoke(container, [e.Key]);
                            break;
                    }
                }

                f.SetValue(root, container);
            }

            RootFields.First(f => f.Name == "_lines").SetValue(root, new List<NetworkLine>(lines));
            if (!whole)
            {
                // (Worked out again when first asked for, from the lines loaded: what's read of them names lines that aren't.)
                foreach (var name in WindowRemade)
                    RootFields.First(f => f.Name == name).SetValue(root, null);
                root.Window = new CellWindow(complete, toRead, tainted.Keys.ToHashSet(), foreign.Keys.ToHashSet(), fromRest.Keys.ToHashSet());
            }

            Time("put together");
            Log.Info($"Cells: {ids.Length} cells read ({bytes / (1 << 20)} MB){(whole ? "" : $", a window complete in {complete.Count}")}, {loaded} lines" +
                     $"{(whole ? "" : $" of {lines.Length} places")}, {elements.Values.Sum(l => l.Count)} things; ms: {string.Join(", ", times)}" +
                     (missing.IsEmpty ? "" : $"; not loaded, named by: {string.Join(", ", missing.OrderByDescending(m => m.Value).Select(m => $"{m.Value} {m.Key}"))}" +
                                              $"{(tainted.IsEmpty ? "" : $" ({tainted.Count} complete cells naming them, not written from the window)")}"));
            return root;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException or TargetInvocationException
                                      or InvalidCastException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException
                                      or AggregateException)
        {
            var inner = e is AggregateException a ? a.Flatten().InnerExceptions[0] : e;
            Log.Warn($"Cells: unreadable ({inner.GetType().Name}: {(inner as TargetInvocationException)?.InnerException?.Message ?? inner.Message}), working the lines out " + inner.StackTrace);
            return null;
        }
    }

    /// Whether every line an entry of a field of line places (ints) names is loaded.
    private static bool IndexedLoaded(object? key, object? value, Func<int, bool> loaded)
    {
        static bool All(object? o, Func<int, bool> loaded) => o switch
        {
            int i => loaded(i),
            List<int> list => list.TrueForAll(i => loaded(i)),
            List<(int Line, int Point)> points => points.TrueForAll(q => loaded(q.Line)),
            ValueTuple<long, int> fitted => loaded(fitted.Item2),
            ValueTuple<int, int> at => loaded(at.Item1),
            _ => true,
        };

        return All(key, loaded) && All(value, loaded);
    }

    /// A slice's bytes passed over (a line not loaded).
    private static void SkipSlice(Stream r, Slice s, int first, int count, int lineCount, ref byte[] buffer)
    {
        var size = SlicedSize(s);
        var (_, n) = Range(s, first, count, lineCount);
        var length = n * size;
        if (buffer.Length < length)
            buffer = new byte[Math.Max(length, buffer.Length * 2)];
        r.ReadExactly(buffer.AsSpan(0, length));
    }

    private static int SlicedSize(Slice s) => SizeOf(LineFields[s.Field].FieldType.GetElementType()!);
}
