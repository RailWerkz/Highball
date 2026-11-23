using System.Collections;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

internal static partial class CellStore
{
    /// HB_LOCALCHECK=1 after a rebuild of some cells: the merged lines against the same edit laid in full, cell by cell.
    /// - In the dirty cells: every line by name (points in plan and height, worst and where), and the things anchored there by what
    ///   they are (IdentityOf), each field's count and its worst difference in place.
    /// - Outside the cells it touched: every line is the object it was before the edit, and every spread entry anchored there is
    ///   previous's own (or exactly alike), so those cells are what they were, bit for bit.
    /// - Every line and registered thing anything in the merged lines points at is in them (nothing left pointing at what's gone).
    public static void CheckMerge(NetworkLines previous, Merged merged, NetworkLines full, LocalRebuild local)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = merged.Lines;
        var dirty = local.Dirty!;
        var touched = merged.Touched;

        // Lines in the dirty cells, by name.
        var fullByName = full.Lines.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.ToList());
        var rByName = r.Lines.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.ToList());
        var dupes = rByName.Count(g => g.Value.Count > 1);
        var (compared, missing, counted, offPlan, offHeight) = (0, 0, 0, 0, 0);
        var (worstPlan, worstHeight) = (0.0, 0.0);
        string? worstPlanAt = null, worstHeightAt = null;
        var inDirty = r.Lines.Where(l => NetworkLines.TouchesDirty(l, local)).ToList();
        foreach (var line in inDirty)
        {
            if (!fullByName.TryGetValue(line.Name, out var others) || others.Count != 1)
            {
                missing++;
                continue;
            }

            var other = others[0];
            if (other.Count != line.Count)
            {
                counted++;
                continue;
            }

            compared++;
            var (plan, height, at) = (0.0, 0.0, 0);
            var hAt = 0;
            for (var i = 0; i < line.Count; i++)
            {
                var d = Math.Sqrt((line.X[i] - other.X[i]) * (line.X[i] - other.X[i]) + (line.Z[i] - other.Z[i]) * (line.Z[i] - other.Z[i]));
                if (d > plan)
                    (plan, at) = (d, i);
                var h = Math.Abs(line.Y[i] - other.Y[i]);
                if (h > height)
                    (height, hAt) = (h, i);
            }

            if (plan > 0.01)
                offPlan++;
            if (height > 0.01)
                offHeight++;
            string Where(int i) => $"line {line.Name} ({(line.Rail ? "rail" : line.Mark ? "mark" : "road")}, {(merged.Made.Contains(line) ? "laid again" : "kept")}) point {i} at ({line.X[i]:0}, {line.Z[i]:0}) in {local.CellOf(line.X[i], line.Z[i])}";
            if (plan > worstPlan)
                (worstPlan, worstPlanAt) = (plan, Where(at));
            if (height > worstHeight)
                (worstHeight, worstHeightAt) = (height, Where(hAt));
        }

        var fullInDirty = full.Lines.Count(l => NetworkLines.TouchesDirty(l, local));
        var onlyFull = full.Lines.Where(l => NetworkLines.TouchesDirty(l, local)).Count(l => !rByName.ContainsKey(l.Name));
        Log.Info($"Local check, lines in the {dirty.Count} dirty cells: {inDirty.Count} merged, {fullInDirty} in full; {compared} compared, {offPlan} over 1 cm apart in plan, " +
                 $"{offHeight} in height; worst in plan {worstPlan:0.0000} m ({worstPlanAt ?? "-"}), in height {worstHeight:0.0000} m ({worstHeightAt ?? "-"}); " +
                 $"{missing} not in full by name, {counted} with other point counts, {onlyFull} only in full; {dupes} names twice in the merged");

        // Things in the dirty cells.
        var cR = ContextOf(r);
        var cF = ContextOf(full);
        var cP = ContextOf(previous);
        var notes = new List<string>();
        for (var fi = 0; fi < SpreadFields.Length; fi++)
        {
            var f = SpreadFields[fi];
            var rule = Rules[f.Name];
            if (Remade.Contains(f.Name) || rule.Nested || f.Name is "_preparedKeys" or "_shapingHashes" or "_ballast" or "_byChain" or "_seenChains" or "_wallSpans" or "_railsNear" or "_detailCells")
                continue;
            if (f.GetValue(r) is not IEnumerable rv || f.GetValue(full) is not IEnumerable fv)
                continue;
            int InCells(IEnumerable v, Context c, Func<TileId, bool> where)
            {
                var n = 0;
                foreach (var item in v)
                {
                    var (key, value) = item is DictionaryEntry e ? (e.Key, e.Value) : (item, null);
                    if (rule.Anchor!(c, key, value) is { } at && where(at))
                        n++;
                }

                return n;
            }

            var (a, b) = (InCells(rv, cR, dirty.Contains), InCells(fv, cF, dirty.Contains));
            if (a != b)
                notes.Add($"{f.Name} {a}/{b}");

            if (Registered(f))
            {
                var fullThings = new Dictionary<long, object>();
                foreach (var item in (IList)fv)
                    if (item is not null && rule.Anchor!(cF, item, null) is { } at && dirty.Contains(at))
                        fullThings.TryAdd(IdentityOf(item), item);
                var (unmatched, worst) = (0, 0.0);
                foreach (var item in (IList)rv)
                {
                    if (item is null || rule.Anchor!(cR, item, null) is not { } at || !dirty.Contains(at))
                        continue;
                    if (!fullThings.TryGetValue(IdentityOf(item), out var other))
                    {
                        unmatched++;
                        continue;
                    }

                    if (MemberOf(item.GetType(), "X") is null && cR.Place(item) is null)
                        continue;
                }

                if (unmatched > 0)
                    notes.Add($"{f.Name}: {unmatched} not matched in full");
                _ = worst;
            }
        }

        Log.Info($"Local check, things in the dirty cells (merged/full where they differ): {(notes.Count == 0 ? "all alike in count" : string.Join(", ", notes))}");

        // Outside the cells touched: what's there is what was.
        var (linesOff, entriesOff, entriesChecked) = (0, 0, 0);
        var madeSet = new HashSet<NetworkLine>(merged.Made, ReferenceEqualityComparer.Instance);
        foreach (var line in r.Lines)
        {
            if (madeSet.Contains(line))
                continue;
            if (!ReferenceEquals(previous.Lines.ElementAtOrDefault(line.Index), line) && !merged.Moved.Any(m => m.To == line.Index))
                linesOff++;
        }

        var byName = previous.Lines.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
        var off = new Dictionary<string, int>();
        for (var fi = 0; fi < SpreadFields.Length; fi++)
        {
            var f = SpreadFields[fi];
            var rule = Rules[f.Name];
            if (Remade.Contains(f.Name) || rule.Nested || f.Name is "_preparedKeys" or "_shapingHashes" or "_ballast")
                continue;
            if (f.GetValue(previous) is not IEnumerable pv || f.GetValue(r) is not IEnumerable rv)
                continue;
            if (pv is IDictionary pd && rv is IDictionary rd)
            {
                foreach (DictionaryEntry e in pd)
                {
                    if (rule.Anchor!(cP, e.Key, e.Value) is not { } at || touched.Contains(at))
                        continue;
                    entriesChecked++;
                    if (!rd.Contains(e.Key) || !(ReferenceEquals(rd[e.Key], e.Value) || Equals(rd[e.Key], e.Value) || Alike(rd[e.Key]!, e.Value!, byName)))
                    {
                        entriesOff++;
                        off[f.Name] = off.GetValueOrDefault(f.Name) + 1;
                    }
                }
            }
            else
            {
                var have = new HashSet<object>(rv.Cast<object>(), f.FieldType.IsGenericType && f.FieldType.GetGenericArguments()[0].IsValueType ? EqualityComparer<object>.Default : ReferenceEqualityComparer.Instance);
                foreach (var item in pv)
                {
                    if (item is null || rule.Anchor!(cP, item, null) is not { } at || touched.Contains(at))
                        continue;
                    entriesChecked++;
                    if (!have.Contains(item))
                    {
                        entriesOff++;
                        off[f.Name] = off.GetValueOrDefault(f.Name) + 1;
                    }
                }
            }
        }

        Log.Info($"Local check, outside the {touched.Count} cells touched: {linesOff} lines not as they were, {entriesOff} of {entriesChecked} things not as they were" +
                 (off.Count == 0 ? "" : $" ({string.Join(", ", off.Select(o => $"{o.Value} of {o.Key}"))})"));

        // Nothing pointing at what isn't there.
        var registered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var f in SpreadFields.Where(Registered))
            if (f.GetValue(r) is IList list)
                foreach (var item in list)
                    if (item is not null)
                        registered.Add(item);
        var (strayLines, strayThings) = (0, 0);
        var strayAt = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o, string from, int depth)
        {
            if (o is null || depth > 10)
                return;
            if (o is NetworkLine line)
            {
                if (depth > 0)
                {
                    if (line.Index < 0 || line.Index >= r.Lines.Count || !ReferenceEquals(r.Lines[line.Index], line))
                    {
                        strayLines++;
                        if (strayAt.Count < 8)
                            strayAt.Add($"{from}: line {line.Name} at {line.X[0]:0}, {line.Z[0]:0}");
                    }

                    return;
                }
            }

            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || (o is Array arr && GraphSnapshot.IsBlittable(arr.GetType().GetElementType()!)))
                return;
            if (!t.IsValueType && !seen.Add(o))
                return;
            if (depth > 0 && t.Assembly == typeof(CellStore).Assembly && !t.IsValueType && SpreadFields.Any(f => Registered(f) && f.FieldType.GetGenericArguments()[0] == t) && !registered.Contains(o))
            {
                strayThings++;
                if (strayAt.Count < 8)
                    strayAt.Add($"{from}: {t.Name}");
                return;
            }

            if (o is IEnumerable items)
            {
                foreach (var item in items)
                {
                    if (item is DictionaryEntry e)
                    {
                        Walk(e.Key, from, depth + 1);
                        Walk(e.Value, from, depth + 1);
                    }
                    else
                        Walk(item, from, depth + 1);
                }

                return;
            }

            foreach (var f in GraphSnapshot.FieldsOf(t))
                if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !(t == typeof(NetworkLine) && f.Name == "_origin"))
                    Walk(f.GetValue(o), from, depth + 1);
        }

        foreach (var f in SpreadFields)
            if (!Remade.Contains(f.Name) && f.Name is not "_shapingHashes")
                Walk(f.GetValue(r), f.Name, 0);
        foreach (var line in merged.Made)
            Walk(line, "a line laid again", 0);
        Log.Info($"Local check, references: {strayLines} to lines not in the merged lines, {strayThings} to things not in them{(strayAt.Count == 0 ? "" : $" (e.g. {string.Join("; ", strayAt)})")}; checked in {watch.ElapsedMilliseconds} ms");
    }
}
