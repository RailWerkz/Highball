using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

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
            for (var n = r.ReadInt32(); n > 0; n--)
                cells[new TileId(r.ReadInt32(), r.ReadInt32())] = (r.ReadInt64(), r.ReadUInt64());
            var rest = r.ReadBytes(r.ReadInt32());
            return new Index { BuildVersion = build, Stamp = stamp, Rules = rules, LineCount = lineCount, Schema = schema, Spread = spread, Cells = cells, Rest = rest };
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Cells: index unreadable ({e.Message}), working the lines out");
            return null;
        }
    }

    /// One cell's file, its block A read: its pieces, and the things it makes.
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
        return cell;
    }

    /// The lines in a set of cells, as one NetworkLines (with what Restore puts back still to put back). For now the set has to be
    /// every cell in the index (null otherwise: a line or a thing with a piece in a cell not read can't be put together yet; streaming,
    /// stage 4, makes a window of them).
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
            var ids = cells.ToArray();
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

            // Every line and every thing that's referred to, made (empty) before any's read, so a reference finds it whichever cell it's in.
            var lines = new NetworkLine[index.LineCount];
            for (var i = 0; i < lines.Length; i++)
                lines[i] = (NetworkLine)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLine));
            var arrays = new Array[index.LineCount][];
            var pieces = files.SelectMany(f => f.Pieces).GroupBy(p => p.Line).ToDictionary(g => g.Key, g => g.OrderBy(p => p.First).ToList());
            if (pieces.Count != lines.Length || pieces.Keys.Any(i => i < 0 || i >= lines.Length))
                throw new InvalidDataException($"{pieces.Count} lines in the cells read, {lines.Length} in the index");
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
            object Resolve(int kind, int i) => kind switch
            {
                0 => root,
                1 => lines[i],
                _ => made[kind - 2].GetValueOrDefault(i) ?? throw new InvalidDataException($"{SpreadFields[kind - 2].Name} {i} isn't in the cells read"),
            };
            Time("made");

            // Each cell's arrays, heads and things.
            Parallel.For(0, files.Length, k =>
            {
                var file = files[k];
                using var stream = Decompress(file.Bytes, file.OffsetB, file.PackedB, file.Codec);
                using var r = new BinaryReader(stream);
                var buffer = Array.Empty<byte>();
                foreach (var p in file.Pieces)
                    for (var s = 0; s < p.Slices.Length; s++)
                        ReadSlice(stream, arrays[p.Line][s], p.Slices[s], p.First, p.Count, p.LineCount, file.Shuffle, ref buffer);

                var g = new GraphSnapshot.Reader(r, null, Resolve);
                for (var n = r.ReadInt32(); n > 0; n--)
                {
                    var line = lines[r.ReadInt32()];
                    var mask = r.ReadUInt64();
                    for (var j = 0; j < LineFields.Length; j++)
                        if ((mask & (1UL << j)) == 0 && !LineDropped.Contains(LineFields[j].Name))
                            LineFields[j].SetValue(line, g.Read(LineFields[j].FieldType));
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
                        if (registered)
                        {
                            object? item;
                            if (r.ReadBoolean())
                                g.ReadInto(item = made[fi][(int)ordinal]);
                            else
                                item = g.Read(itemType!);
                            file.Elements.Add(new Element(fi, ordinal, item, null, false));
                        }
                        else if (keyType is not null)
                        {
                            var key = g.Read(keyType);
                            object? value;
                            if (RegisteredValues(f) && r.ReadBoolean())
                                g.ReadInto(value = made[fi][(int)ordinal]);
                            else
                                value = g.Read(valueType!);
                            file.Elements.Add(new Element(fi, ordinal, key, value, false));
                        }
                        else
                            file.Elements.Add(new Element(fi, ordinal, g.Read(itemType!), null, false));
                    }
                }

                if (k == files.Length - 1)
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
                if (list.Count != meta.Count)
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
                        if (round.Count != meta.Inner[o])
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
                                dictionary.Add(e.Key!, e.Value);
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
            Time("put together");
            Log.Info($"Cells: {ids.Length} cells read ({bytes / (1 << 20)} MB), {lines.Length} lines, {index.Spread.Sum(m => m.Count)} things; ms: {string.Join(", ", times)}");
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
}
