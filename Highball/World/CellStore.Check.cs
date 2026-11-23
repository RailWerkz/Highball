using System.Collections;
using System.Reflection;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

internal static partial class CellStore
{
    /// HB_CELLCHECK: the lines as read from the cells against the same worked out afresh: every field of NetworkLines and of everything
    /// it holds (every line's every array, every turnout, layout, pad, assembly, bridge and bridge end, the indexes by tile and node),
    /// bit for bit, references to lines and things checked to come back to the same ones; then every line's shaping hash and every
    /// tile's shaping key (what the ground cache is keyed by). Left out: what's put back from what the route's opened with (Restore),
    /// and what's left out of the cells (Dropped, LineDropped).
    public static void Compare(NetworkLines read, NetworkLines fresh, int samples, string label = "worked out")
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var c = new Comparer();
        c.Pair(read, fresh);
        var order = RootFields.Where(f => RuleOf(f).Place is Place.Lines or Place.Spread or Place.Route)
            .OrderBy(f => RuleOf(f).Place == Place.Lines ? 0 : Registered(f) ? 1 : 2).ThenBy(f => f.Name, StringComparer.Ordinal);
        // (Spread lists are read back in the order of their elements' places, OrdinalOf, not the order they were made in; sets and
        // dictionaries are compared by what's in them.)
        object? Ordered(FieldInfo f, object? v) => RuleOf(f).Place == Place.Spread && !RuleOf(f).Nested && v is IList list && f.FieldType.IsGenericType
            ? list.Cast<object>().OrderBy(o => OrdinalOf(o, Registered(f))).ToList()
            : v;
        foreach (var f in order)
            c.Same(Ordered(f, f.GetValue(read)), Ordered(f, f.GetValue(fresh)), f.Name);

        var hashes = 0;
        var first = "";
        for (var i = 0; i < Math.Min(read.Lines.Count, fresh.Lines.Count); i++)
            if (read.ShapingHashOf(read.Lines[i]) != fresh.ShapingHashOf(fresh.Lines[i]))
            {
                hashes++;
                if (first.Length == 0)
                    first = $" (first: line {i}, key {read.Lines[i].Key})";
            }

        IEnumerable<TileId> Shaped(NetworkLines l) => ((IDictionary)RootFields.First(f => f.Name == "_byTile").GetValue(l)!).Keys.Cast<TileId>();
        var tiles = Shaped(read).Union(Shaped(fresh)).ToList();
        var keys = tiles.Count(t => read.ShapingKey(t, samples) != fresh.ShapingKey(t, samples));

        var total = c.Differences.Values.Sum(d => d.Count);
        Log.Info($"Cell check: {read.Lines.Count} lines read, {fresh.Lines.Count} {label}; {c.Compared} values compared, {total} differ" +
                 (total == 0 ? "" : " XX: " + string.Join("; ", c.Differences.OrderByDescending(d => d.Value.Count).Take(20).Select(d => $"{d.Key} {d.Value.Count} (first {d.Value.First})"))) +
                 $"; shaping hashes {hashes} of {read.Lines.Count} differ{first}{(hashes > 0 ? " XX" : "")}; shaping keys {keys} of {tiles.Count} tiles differ{(keys > 0 ? " XX" : "")}; " +
                 $"turnouts {read.Turnouts.Count}/{fresh.Turnouts.Count}, layouts {read.Layouts.Count}/{fresh.Layouts.Count}, pads {read.Pads.Count}/{fresh.Pads.Count}, " +
                 $"bridges {read.Bridges?.Count}/{fresh.Bridges?.Count}, assemblies {read.CrossingAssemblies.Count}/{fresh.CrossingAssemblies.Count}; {watch.ElapsedMilliseconds} ms");
    }

    private sealed class Comparer
    {
        private readonly Dictionary<object, object> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _path = [];
        public readonly Dictionary<string, (int Count, string First)> Differences = [];
        public long Compared;

        public void Pair(object a, object b) => _seen[a] = b;

        /// A key as the other side has it: a reference in it (a line) swapped for the one it was paired with.
        private object? Counterpart(object? key)
        {
            if (key is null)
                return null;
            if (!key.GetType().IsValueType)
                return _seen.TryGetValue(key, out var mapped) ? mapped : key;
            if (key is System.Runtime.CompilerServices.ITuple tuple && key.GetType().IsGenericType)
            {
                var items = new object?[tuple.Length];
                for (var i = 0; i < items.Length; i++)
                    items[i] = Counterpart(tuple[i]);
                return Activator.CreateInstance(key.GetType(), items);
            }

            return key;
        }

        private void Differ(string what)
        {
            var key = string.Join(".", _path.Where(p => !p.StartsWith('[')).Take(2));
            var at = string.Join(".", _path).Replace(".[", "[", StringComparison.Ordinal);
            Differences[key] = Differences.TryGetValue(key, out var d) ? (d.Count + 1, d.First) : (1, $"{at}: {what}");
        }

        public void Same(object? a, object? b, string name)
        {
            _path.Add(name);
            Walk(a, b);
            _path.RemoveAt(_path.Count - 1);
        }

        private void Walk(object? a, object? b)
        {
            Compared++;
            if (a is null || b is null)
            {
                if (a is null != b is null)
                    Differ(a is null ? "null read" : "null worked out");
                return;
            }

            var t = a.GetType();
            if (t != b.GetType())
            {
                Differ($"{t.Name} read, {b.GetType().Name} worked out");
                return;
            }

            if (t.IsPrimitive || t.IsEnum)
            {
                var same = a switch
                {
                    float x => BitConverter.SingleToUInt32Bits(x) == BitConverter.SingleToUInt32Bits((float)b),
                    double x => BitConverter.DoubleToUInt64Bits(x) == BitConverter.DoubleToUInt64Bits((double)b),
                    _ => a.Equals(b),
                };
                if (!same)
                    Differ($"{a} read, {b} worked out");
                return;
            }

            if (a is string s)
            {
                if (s != (string)b)
                    Differ($"\"{s}\" read, \"{b}\" worked out");
                return;
            }

            if (!t.IsValueType)
            {
                if (_seen.TryGetValue(a, out var had))
                {
                    if (!ReferenceEquals(had, b))
                        Differ("refers to another one");
                    return;
                }

                _seen[a] = b;
            }

            if (a is Delegate || a is System.Diagnostics.Stopwatch || t == typeof(object) || t == typeof(Lock) ||
                (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Lazy<>)))
                return;

            switch (a)
            {
                case Array x:
                {
                    var y = (Array)b;
                    if (x.Length != y.Length)
                    {
                        Differ($"{x.Length} long read, {y.Length} worked out");
                        return;
                    }

                    var element = t.GetElementType()!;
                    if (GraphSnapshot.IsBlittable(element))
                    {
                        if (!BytesOf(x).SequenceEqual(BytesOf(y)))
                            Differ($"differs from item {FirstDifference(BytesOf(x), BytesOf(y)) / SizeOf(element)}");
                        Compared += x.Length;
                        return;
                    }

                    for (var i = 0; i < x.Length; i++)
                        Same(x.GetValue(i), y.GetValue(i), $"[{i}]");
                    return;
                }
                case IList x when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>):
                {
                    var y = (IList)b;
                    if (x.Count != y.Count)
                    {
                        Differ($"{x.Count} items read, {y.Count} worked out");
                        return;
                    }

                    for (var i = 0; i < x.Count; i++)
                        Same(x[i], y[i], $"[{i}]");
                    return;
                }
                case IDictionary x:
                {
                    var y = (IDictionary)b;
                    if (x.Count != y.Count)
                    {
                        Differ($"{x.Count} entries read, {y.Count} worked out");
                        return;
                    }

                    // (By key: a dictionary's order is what was added when (or its buckets'), and cells are read in their places' order.)
                    foreach (DictionaryEntry e in x)
                    {
                        var key = Counterpart(e.Key)!;
                        if (!y.Contains(key))
                        {
                            Differ($"{e.Key} not worked out");
                            continue;
                        }

                        Same(e.Value, y[key], $"[{e.Key}]");
                    }

                    return;
                }
                case IEnumerable x when t.IsGenericType && t.GetGenericTypeDefinition() is var d && (d == typeof(HashSet<>) || d == typeof(SortedSet<>)):
                {
                    var (lx, ly) = (x.Cast<object>().ToList(), ((IEnumerable)b).Cast<object>().ToList());
                    if (lx.Count != ly.Count)
                    {
                        Differ($"{lx.Count} items read, {ly.Count} worked out");
                        return;
                    }

                    var other = ((IEnumerable)b).Cast<object>().ToHashSet();
                    foreach (var item in lx)
                        if (!other.Contains(Counterpart(item)!))
                            Differ($"{item} not worked out");
                    Compared += lx.Count;
                    return;
                }
            }

            foreach (var f in GraphSnapshot.FieldsOf(t))
            {
                if (t == typeof(NetworkLine) && LineDropped.Contains(f.Name))
                    continue;
                Same(f.GetValue(a), f.GetValue(b), f.Name);
            }
        }

        private static int FirstDifference(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
        {
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i])
                    return i;
            return -1;
        }
    }
}
