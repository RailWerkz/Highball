using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

internal static partial class CellStore
{
    /// What a local rebuild of some cells came to: the route's lines with the window's put in (Lines), the lines laid differently or
    /// new (Made, as they are in Lines), the last lines gone or replaced by them (Gone), the lines that kept their objects but moved
    /// to another place in the set (Moved: from, to), the cells whose content changed (Touched: the dirty cells, and any cell a line
    /// laid differently or gone crosses or reaches), and Spill: why the window wasn't enough (a line laid differently outside the
    /// dirty cells), null if it was.
    public sealed record Merged(NetworkLines Lines, List<NetworkLine> Made, List<NetworkLine> Gone, List<(int From, int To)> Moved, HashSet<TileId> Touched, string? Spill);

    /// The fields whose entries hold line indices (ints), not lines: remapped as lines take new places.
    private static readonly HashSet<string> KeyIndexFields = [Auto(nameof(NetworkLines.Fitted)), "_continuing"];
    private static readonly HashSet<string> ValueIndexFields = ["_pointsByTile"];

    /// Worked out again when first asked for, from the lines (or by the passes as they run): left for that rather than merged.
    private static readonly HashSet<string> Remade = ["_railCells", "_roadEnds", "_needGround"];

    /// What's made whole when first asked for, from every line there is: a window can't make it whole, so it's left out of a window's
    /// read (made again from the lines loaded) and written absent by a window (made again from the lines read, whatever's read).
    private static readonly HashSet<string> WindowRemade = [.. Remade, "_byChain", "_endShapes", "_wallsBeside", "_detailRegions", "_detailCells", "_bridgeEnds", "_railAt"];

    /// A rebuild of some cells (local.Dirty) put in place of the last lines' there (docs/cell-routes.md, Edits): in a copy of previous,
    /// every line the window laid differently takes the place of the one of its name (or a free place), the lines gone go, and every
    /// spread thing anchored in a dirty cell is the window's, every other one previous's. The window's things are pointed at the lines
    /// and things they'll be with (a frozen copy's at the line it's a copy of). Nothing of previous's is changed but the places
    /// (Index) of lines moved to fill a gap. ways: every way, for what the new lines say they were laid from.
    public static Merged Merge(NetworkLines previous, NetworkLines window, LocalRebuild local, Dictionary<long, NetworkWay> ways)
    {
        var watch = Stopwatch.StartNew();
        var times = new List<string>();
        void Time(string name)
        {
            times.Add($"{name} {watch.ElapsedMilliseconds}");
            watch.Restart();
        }

        var dirty = local.Dirty!;
        var pLines = previous.Lines;
        var byName = new Dictionary<long, NetworkLine>(pLines.Count);
        foreach (var line in pLines)
            if (!line.IsAbsent)
                byName.TryAdd(line.Name, line);

        // Which of the window's registered things are previous's as they were: anchored alike, and alike in every field (the lines they
        // name compared by name; a turnout's lines laid differently show in its own fields, its spread, its frog).
        var cW = ContextOf(window);
        var cP = ContextOf(previous);
        Time("contexts");
        var things = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        var wThings = new List<(object Item, bool InDirty)>();
        for (var fi = 0; fi < SpreadFields.Length; fi++)
        {
            var f = SpreadFields[fi];
            if (!Registered(f) || f.GetValue(window) is not IList wList || f.GetValue(previous) is not IList pList)
                continue;
            var rule = Rules[f.Name];
            var mine = new Dictionary<long, List<object>>();
            foreach (var item in pList)
                if (item is not null)
                    Bucket(mine, IdentityOf(item), item);
            foreach (var item in wList)
            {
                if (item is null)
                    continue;
                wThings.Add((item, rule.Anchor!(cW, item, null) is { } at && dirty.Contains(at)));
                if (mine.TryGetValue(IdentityOf(item), out var same) && same.FirstOrDefault(p => Alike(item, p, byName)) is { } match)
                    things[item] = match;
            }
        }

        Time("things");

        // The window's lines: one just as previous's of its name (a frozen or halo one is, unless the turnouts it's marked by aren't) is
        // that; the rest are made. And a thing can't be taken as previous's if it's of a line made (its zones point at it, it at the
        // lines it's on): until nothing more changes.
        var map = new Dictionary<NetworkLine, NetworkLine>(ReferenceEqualityComparer.Instance);
        var differ = new Dictionary<string, int>();
        var made = new List<NetworkLine>();
        var madeSet = new HashSet<NetworkLine>(ReferenceEqualityComparer.Instance);
        // (Everything but the zones compared once; the zones each round, as things are let go.)
        // (A window's line with no point in a dirty cell is previous's, whatever the window made of it: it's context, worked out with
        // less round it than it had, and the cells it's in aren't being laid again.)
        var first = new Dictionary<NetworkLine, (NetworkLine? P, string? Why)>(ReferenceEqualityComparer.Instance);
        var outside = new HashSet<NetworkLine>(ReferenceEqualityComparer.Instance);
        var dropped = new HashSet<NetworkLine>(ReferenceEqualityComparer.Instance);
        foreach (var w in window.Lines)
        {
            var p = w.Origin ?? (byName.TryGetValue(w.Name, out var named) ? named : null);
            if (!NetworkLines.TouchesDirty(w, local))
            {
                // (One with no line of its name, or another count, is the window's own making at its edge, a chain whose far ways weren't
                // handed: left out, previous's lines there being what they were.)
                if (p is not null && p.Count == w.Count)
                {
                    outside.Add(w);
                    first[w] = (p, null);
                }
                else
                    dropped.Add(w);
                continue;
            }

            first[w] = (p, p is null ? "new" : SameLine(w, p, things, zones: false));
        }

        for (var round = 0; ; round++)
        {
            map.Clear();
            made.Clear();
            madeSet.Clear();
            foreach (var w in window.Lines)
            {
                if (dropped.Contains(w))
                {
                    map[w] = w;
                    continue;
                }

                var (p, why) = first[w];
                if (why is null && !outside.Contains(w) && !SameZones(w.TurnoutZones, p!.TurnoutZones, things))
                    why = "TurnoutZones";
                if (why is null)
                    map[w] = p!;
                else
                {
                    if (round == 0)
                        differ[why] = differ.GetValueOrDefault(why) + 1;
                    map[w] = w;
                    made.Add(w);
                    madeSet.Add(w);
                }
            }

            var unmatched = 0;
            foreach (var (w, _) in things.ToList())
                if (Refers(w, madeSet.Contains))
                {
                    things.Remove(w);
                    unmatched++;
                }

            if (unmatched == 0 || round > 8)
                break;
        }

        Time("lines");

        // Previous's lines with points in the dirty cells, and which of them aren't the window's any more.
        var touching = new HashSet<int>();
        foreach (var cell in dirty)
            if (previous.PointsIn(cell) is { } points)
                foreach (var (l, _) in points)
                    touching.Add(l);
        var kept = new HashSet<NetworkLine>(map.Values, ReferenceEqualityComparer.Instance);
        var gone = touching.Select(i => pLines[i]).Where(l => !kept.Contains(l)).ToList();
        foreach (var w in made)
            if (byName.TryGetValue(w.Name, out var p) && !kept.Contains(p) && !gone.Contains(p))
                gone.Add(p);
        // The lines made that reach out of the dirty cells and were held there (NetworkLines.HoldAtBorder): what's anchored along them
        // outside is previous's, pointed at them. One that wasn't held means the cells weren't enough.
        var held = new Dictionary<NetworkLine, NetworkLine>(ReferenceEqualityComparer.Instance);
        var unheld = new List<NetworkLine>();
        foreach (var w in made)
        {
            var outsideAny = false;
            for (var q = 0; q < w.Count && !outsideAny; q++)
                outsideAny = !local.InDirty(w.X[q], w.Z[q]);
            if (!outsideAny)
                continue;
            if (byName.TryGetValue(w.Name, out var was) && NetworkLines.HeldOutside(w, was, local))
                held[was] = w;
            else
                unheld.Add(w);
        }

        var heldW = new HashSet<NetworkLine>(held.Values, ReferenceEqualityComparer.Instance);
        string? spill = null;
        if (wThings.FirstOrDefault(t => !t.InDirty && !things.ContainsKey(t.Item) && Refers(t.Item, l => madeSet.Contains(l) && !heldW.Contains(l))) is { Item: { } stray })
        {
            NetworkLine? named = null;
            Refers(stray, l =>
            {
                if (!madeSet.Contains(l))
                    return false;
                named = l;
                return true;
            });
            spill = $"a {stray.GetType().Name} outside the dirty cells changed (at {cW.Place(stray)}, of line {named?.Name} " +
                    $"({(named is null ? "" : named.Rail ? "rail" : named.Mark ? "mark" : "road")}, {named?.Count} points from {named?.X[0]:0}, {named?.Z[0]:0}, " +
                    $"{(named is not null && NetworkLines.TouchesDirty(named, local) ? "in the dirty cells" : "outside them")}, made for {(named is null ? "" : first.GetValueOrDefault(named).Why)}))";
        }
        if (unheld.FirstOrDefault() is { } w0)
            spill ??= $"line {w0.Name} ({(w0.Rail ? "rail" : w0.Mark ? "mark" : "road")}, at {w0.X[0]:0}, {w0.Z[0]:0}) laid differently outside the dirty cells, not held";

        // Places: a made line takes the place of the gone one of its name, then any gone one's, then a new one; places left over are filled
        // from the end.
        var lines = new List<NetworkLine>(pLines);
        var free = new SortedSet<int>(gone.Select(l => l.Index));
        foreach (var w in made)
            if (byName.TryGetValue(w.Name, out var p) && free.Remove(p.Index))
            {
                w.MoveTo(p.Index);
                lines[p.Index] = w;
            }
            else
                w.MoveTo(-1);
        foreach (var w in made.Where(w => w.Index < 0))
        {
            if (free.Count > 0)
            {
                var at = free.Min;
                free.Remove(at);
                w.MoveTo(at);
                lines[at] = w;
            }
            else
            {
                w.MoveTo(lines.Count);
                lines.Add(w);
            }
        }

        // (Places left over hold no line: every other line keeps its place, so a window's places stay the route's, and the cells of lines
        // that would have moved to fill them aren't written again.)
        var moved = new List<(int From, int To)>();
        foreach (var at in free)
            lines[at] = NetworkLine.Absent;

        var movedTo = moved.ToDictionary(m => m.From, m => m.To);
        int IndexOf(NetworkLine l) => movedTo.TryGetValue(l.Index, out var to) && ReferenceEquals(lines[to], l) ? to : l.Index;
        int FromWindow(int i) => dropped.Contains(window.Lines[i]) ? -1 : IndexOf(map[window.Lines[i]]);
        var goneIndex = gone.Select(l => l.Index).ToHashSet();
        Time("places");

        // The window's things pointed at what they'll be with.
        var remap = new Remapper(map, things);

        // Previous's things outside the cells naming a line held are copies naming the line laid; a turnout copied so is on lines whose
        // zones name it, which are copied too (sharing their arrays: they're as they were), and so on.
        var lineMap = new Dictionary<NetworkLine, NetworkLine>(held, ReferenceEqualityComparer.Instance);
        var copiedLines = new List<NetworkLine>();
        var copiedTurnouts = new HashSet<Turnout>(ReferenceEqualityComparer.Instance);
        var zonesField = GraphSnapshot.FieldsOf(typeof(NetworkLine)).First(x => x.Name == Auto(nameof(NetworkLine.TurnoutZones)));
        for (var more = true; more && held.Count > 0;)
        {
            more = false;
            foreach (var t in previous.Turnouts)
            {
                if (copiedTurnouts.Contains(t) || (cP.Place(t) is { } at && dirty.Contains(at)))
                    continue;
                if (!lineMap.ContainsKey(t.Toe.Line) && !lineMap.ContainsKey(t.Normal.Line) && !lineMap.ContainsKey(t.Reverse.Line))
                    continue;
                copiedTurnouts.Add(t);
                foreach (var leg in (ReadOnlySpan<NetworkLine>)[t.Toe.Line, t.Normal.Line, t.Reverse.Line])
                {
                    if (lineMap.ContainsKey(leg) || gone.Contains(leg) || !leg.TurnoutZones.Any(z => ReferenceEquals(z.Turnout, t)))
                        continue;
                    var copy = (NetworkLine)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLine));
                    foreach (var x in GraphSnapshot.FieldsOf(typeof(NetworkLine)))
                        x.SetValue(copy, x.GetValue(leg));
                    zonesField.SetValue(copy, new List<TurnoutZone>(leg.TurnoutZones));
                    lineMap[leg] = copy;
                    copiedLines.Add(copy);
                    lines[leg.Index] = copy;
                    more = true;
                }
            }
        }

        var repoint = new Repointer(lineMap);

        var root = (NetworkLines)RuntimeHelpers.GetUninitializedObject(typeof(NetworkLines));
        // (The spread fields side by side: previous's part of each is most of the work. The window's objects are pointed at what
        // they'll be with one field at a time, Remapper not being made for more.)
        Parallel.ForEach(RootFields.Where(f => RuleOf(f).Place == Place.Spread), f =>
            f.SetValue(root, MergeField(f, RuleOf(f), previous, window, cP, cW, dirty, remap, FromWindow, IndexOf, goneIndex, made, gone, lines, moved, repoint)));
        foreach (var f in RootFields)
        {
            var rule = RuleOf(f);
            switch (rule.Place)
            {
                case Place.Lines:
                    f.SetValue(root, lines);
                    break;
                case Place.Route:
                    f.SetValue(root, f.GetValue(f.Name is "_ballastSamples" or "_handSet" ? previous : window));
                    break;
                case Place.Restored:
                    f.SetValue(root, f.GetValue(window));
                    break;
                case Place.Dropped:
                    f.SetValue(root, null);
                    break;
                case Place.Spread:
                    break;
            }
        }

        Time("fields");

        // Things that keep the lines they were made among (a slope end's shape) keep the merged ones: the window's in place, previous's
        // copied (previous's own are still shown meanwhile).
        foreach (var f in SpreadFields.Where(Registered))
        {
            var holds = GraphSnapshot.FieldsOf(f.FieldType.GetGenericArguments()[0]).Where(x => x.FieldType == typeof(NetworkLines)).ToArray();
            if (holds.Length == 0 || f.GetValue(root) is not IList items)
                continue;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is not { } item || holds.All(x => ReferenceEquals(x.GetValue(item), root)))
                    continue;
                if (holds.Any(x => ReferenceEquals(x.GetValue(item), previous)))
                {
                    var copy = RuntimeHelpers.GetUninitializedObject(item.GetType());
                    foreach (var x in GraphSnapshot.FieldsOf(item.GetType()))
                        x.SetValue(copy, x.GetValue(item));
                    items[i] = item = copy;
                }

                foreach (var x in holds)
                    x.SetValue(item, root);
            }
        }

        // The window's lines made point at the turnouts they'll be with (previous's, copied where they name a line held), and so do the
        // lines copied.
        foreach (var w in made)
            for (var z = 0; z < w.TurnoutZones.Count; z++)
                if (things.TryGetValue(w.TurnoutZones[z].Turnout, out var p))
                    w.TurnoutZones[z] = w.TurnoutZones[z] with { Turnout = (Turnout)repoint.Map(p)! };
        foreach (var c in copiedLines)
            for (var z = 0; z < c.TurnoutZones.Count; z++)
                c.TurnoutZones[z] = c.TurnoutZones[z] with { Turnout = (Turnout)repoint.Map(c.TurnoutZones[z].Turnout)! };
        RootFields.First(f => f.Name == "Ways").SetValue(root, ways);
        root.Window = previous.Window;
        RootFields.First(f => f.Name == Auto(nameof(NetworkLines.DetailCache))).SetValue(root, new System.Collections.Concurrent.ConcurrentDictionary<NetworkLines.DetailRegion, Lazy<float[]>>());

        // The cells whose content changed: the dirty ones, and every one a line made or gone crosses or reaches.
        var touched = new HashSet<TileId>(dirty);
        foreach (var l in made.Concat(gone))
            touched.UnionWith(l.Tiles);

        // (Lines kept as objects but moved: what's anchored along them names their new place; the lines themselves are told it as the
        // change is taken in, on the main thread (TerrainSource.Apply), the last lines being shown till then.)
        foreach (var (from, to) in moved)
            touched.UnionWith(lines[to].Tiles);

        Time("put together");
        Log.Info($"Cells merged: {dropped.Count} of the window's lines outside the cells left out; {made.Count} lines laid differently or new ({held.Count} reaching out of the cells, held there; {copiedLines.Count} lines and " +
                 $"{repoint.Copies} things outside pointed at them), {gone.Count} gone, {moved.Count} moved, {things.Count} things as they were; " +
                 $"{dirty.Count} dirty cells, {touched.Count} touched{(spill is null ? "" : $"; SPILL: {spill}")}; made for: {string.Join(", ", differ.Select(d => $"{d.Value} {d.Key}"))}; ms: {string.Join(", ", times)}");
        return new Merged(root, made, gone, moved, touched, spill);
    }

    private static readonly FieldInfo[] SameFields = [.. LineFields.Where(f => f.Name is not ("_origin" or "_runBoxes" or "_halo" or "_index"))];

    /// Whether a (window's) line is just as a (previous's) one, bit for bit, its zones marked by the turnouts taken as previous's: null if
    /// it is, else the first field that isn't.
    private static string? SameLine(NetworkLine w, NetworkLine p, Dictionary<object, object> things, bool zones = true)
    {
        if (w.Count != p.Count)
            return "count";
        foreach (var f in SameFields)
        {
            if (!zones && f.FieldType == typeof(List<TurnoutZone>))
                continue;
            var (a, b) = (f.GetValue(w), f.GetValue(p));
            if (ReferenceEquals(a, b))
                continue;
            var same = (a, b) switch
            {
                (null, _) or (_, null) => false,
                (Array x, Array y) when GraphSnapshot.IsBlittable(x.GetType().GetElementType()!) => x.Length == y.Length && BytesOf(x).SequenceEqual(BytesOf(y)),
                (HashSet<TileId> x, HashSet<TileId> y) => x.SetEquals(y),
                (List<(long, int)> x, List<(long, int)> y) => x.SequenceEqual(y),
                (List<(float, float, int)> x, List<(float, float, int)> y) => x.SequenceEqual(y),
                (List<(float, float)> x, List<(float, float)> y) => x.SequenceEqual(y),
                (List<TurnoutZone> x, List<TurnoutZone> y) => SameZones(x, y, things),
                (Alignment x, Alignment y) => x.Pieces.SequenceEqual(y.Pieces) && BytesOf(x.U).SequenceEqual(BytesOf(y.U)),
                (float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
                (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
                _ => a.Equals(b),
            };
            if (!same)
                return f.Name.Replace("k__BackingField", "").Trim('<', '>');
        }

        return null;
    }

    private static bool SameZones(List<TurnoutZone> x, List<TurnoutZone> y, Dictionary<object, object> things) =>
        x.Count == y.Count && x.Zip(y).All(z =>
            ReferenceEquals(things.GetValueOrDefault(z.First.Turnout, z.First.Turnout), z.Second.Turnout) && z.First with { Turnout = z.Second.Turnout } == z.Second);

    private static void Bucket(Dictionary<long, List<object>> map, long key, object item)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(item);
    }

    /// One spread field of the merged lines.
    private static object? MergeField(FieldInfo f, Rule rule, NetworkLines previous, NetworkLines window, Context cP, Context cW, HashSet<TileId> dirty,
        Remapper remap, Func<int, int> fromWindow, Func<NetworkLine, int> indexOf, HashSet<int> goneIndex, List<NetworkLine> made, List<NetworkLine> gone,
        List<NetworkLine> lines, List<(int From, int To)> moved, Repointer repoint)
    {
        var p = f.GetValue(previous);
        var w = f.GetValue(window);
        if (Remade.Contains(f.Name))
            return null;
        if (p is null && w is null)
            return null;
        switch (f.Name)
        {
            case "_byTile":
                return MergeByTile((Dictionary<TileId, List<int>>)p!, (Dictionary<TileId, List<int>>)w!, dirty, fromWindow, indexOf, goneIndex, made, gone, lines, moved);
            case "_pointsByTile":
                return MergePoints((Dictionary<TileId, List<(int Line, int Point)>>)p!, (Dictionary<TileId, List<(int Line, int Point)>>)w!, dirty, fromWindow, goneIndex, made, gone, lines, moved, cP.Size);
            case "_byChain":
                return MergeByChain((Dictionary<long, List<NetworkLine>>?)p, made, gone, previous);
            case "_seenChains":
                var chains = new HashSet<long>((HashSet<long>)p!);
                foreach (var l in gone)
                    chains.Remove(l.Chain);
                chains.UnionWith((HashSet<long>)w!);
                return chains;
            case "_shapingHashes":
                var hashes = new System.Collections.Concurrent.ConcurrentDictionary<NetworkLine, ulong>(ReferenceEqualityComparer.Instance);
                var goneSet = new HashSet<NetworkLine>(gone, ReferenceEqualityComparer.Instance);
                foreach (var (line, hash) in (System.Collections.Concurrent.ConcurrentDictionary<NetworkLine, ulong>)p!)
                    if (!goneSet.Contains(line))
                        hashes[line] = hash;
                return hashes;
            case "_preparedKeys":
                if (p is not System.Collections.Concurrent.ConcurrentDictionary<TileId, ulong> prepared)
                    return null;
                // (Dropped for the cells touched, worked out again when asked for; kept as they are everywhere else, where the cells
                // kept on disk have them so.)
                var reach = new HashSet<TileId>(dirty);
                foreach (var l in made.Concat(gone))
                    reach.UnionWith(l.Tiles);
                var left = new System.Collections.Concurrent.ConcurrentDictionary<TileId, ulong>();
                foreach (var (tile, key) in prepared)
                    if (!reach.Contains(tile))
                        left[tile] = key;
                return left;
        }

        var (keys, values) = (KeyIndexFields.Contains(f.Name), ValueIndexFields.Contains(f.Name));
        var movedTo = moved.ToDictionary(m => m.From, m => m.To);
        object? MovedP(object? o) => movedTo.Count > 0 ? Reindex(o, i => movedTo.GetValueOrDefault(i, i)) : o;
        object FromP(object? o) => keys ? MovedP(o)! : repoint.Map(o)!;
        object? FromW(object? o)
        {
            if (keys)
                return Reindex(o, fromWindow);
            lock (remap)
                return remap.Map(o);
        }

        object? MapW(object? o)
        {
            lock (remap)
                return remap.Map(o);
        }

        // (Previous's in a dirty cell too, if it's of no line laid differently or gone, and the window has none like it: what the passes
        // leave be on frozen lines, a bridge extended by hand on one, a fork, a shifted surface, isn't worked out again in the window.)
        var changed = new HashSet<NetworkLine>(made.Concat(gone), ReferenceEqualityComparer.Instance);
        bool Changed(object? o) => o is NetworkLine l ? changed.Contains(l) : o is not null && Refers(o, changed.Contains);
        HashSet<long>? wHave = null;
        bool InP(object? key, object? value)
        {
            // (An entry naming a gone line by its place, anywhere: the place may be another line's now.)
            if (keys && key is System.Runtime.CompilerServices.ITuple { Length: 2 } named &&
                goneIndex.Contains(named[0] is int line ? line : named[1] is int line2 ? line2 : -1))
                return false;
            if (rule.Anchor!(cP, key, value) is not { } at || !dirty.Contains(at))
                return true;
            // (The things with places of their own are all found again in the window: its are those.)
            if (Registered(f) || Changed(key) || Changed(value))
                return false;
            if (w is IDictionary || rule.Nested)
                return true; // (one of the window's under the same key replaces it)
            if (wHave is null)
            {
                wHave = [];
                if (w is IEnumerable all)
                    foreach (var item in all)
                        if (InW(item, null))
                            wHave.Add(OrdinalOf(item, Registered(f)));
            }

            return !wHave.Contains(OrdinalOf(key, Registered(f)));
        }

        bool InW(object? key, object? value) => rule.Anchor!(cW, key, value) is { } at && dirty.Contains(at);

        if (rule.Nested)
        {
            var (pr, wr) = ((IList)p!, (IList)w!);
            var outer = (IList)Activator.CreateInstance(f.FieldType)!;
            var inner = f.FieldType.GetGenericArguments()[0];
            for (var o = 0; o < Math.Max(pr.Count, wr.Count); o++)
            {
                var d = (IDictionary)Activator.CreateInstance(inner)!;
                if (o < pr.Count)
                    foreach (DictionaryEntry e in (IDictionary)pr[o]!)
                        if (InP(e.Key, e.Value))
                            d[FromP(e.Key)] = repoint.Map(e.Value);
                if (o < wr.Count)
                    foreach (DictionaryEntry e in (IDictionary)wr[o]!)
                        if (InW(e.Key, e.Value))
                            d[FromW(e.Key)!] = e.Value;
                outer.Add(d);
            }

            return outer;
        }

        var container = Activator.CreateInstance(f.FieldType)!;
        switch (container)
        {
            case IDictionary dictionary:
                if (p is IDictionary pd)
                    foreach (DictionaryEntry e in pd)
                        if (InP(e.Key, e.Value))
                            dictionary[FromP(e.Key)] = values ? MovedP(e.Value) : repoint.Map(e.Value);
                if (w is IDictionary wd)
                    foreach (DictionaryEntry e in wd)
                        if (InW(e.Key, e.Value))
                            dictionary[FromW(e.Key)!] = values ? Reindex(e.Value, fromWindow) : MapW(e.Value);
                break;
            case IList list:
                if (p is IEnumerable pl)
                    foreach (var item in pl)
                        if (InP(item, null))
                            list.Add(FromP(item));
                if (w is IEnumerable wl)
                    foreach (var item in wl)
                        if (InW(item, null))
                            list.Add(remap.Things.TryGetValue(item!, out var same) ? same : FromW(item));
                break;
            default:
                var add = f.FieldType.GetMethod("Add")!;
                if (p is IEnumerable ps)
                    foreach (var item in ps)
                        if (InP(item, null))
                            add.Invoke(container, [FromP(item)]);
                if (w is IEnumerable ws)
                    foreach (var item in ws)
                        if (InW(item, null))
                            add.Invoke(container, [FromW(item)]);
                break;
        }

        return container;
    }

    /// The lines over each tile, in the order they shape it: the window's in the dirty cells; elsewhere previous's, without the lines
    /// gone, with the lines made that reach the tile, and moved lines at their new places.
    private static Dictionary<TileId, List<int>> MergeByTile(Dictionary<TileId, List<int>> p, Dictionary<TileId, List<int>> w, HashSet<TileId> dirty,
        Func<int, int> fromWindow, Func<NetworkLine, int> indexOf, HashSet<int> goneIndex, List<NetworkLine> made, List<NetworkLine> gone,
        List<NetworkLine> lines, List<(int From, int To)> moved)
    {
        var result = new Dictionary<TileId, List<int>>(p);
        var changed = new HashSet<TileId>();
        foreach (var l in gone.Concat(made))
            changed.UnionWith(l.Tiles);
        var movedTo = moved.ToDictionary(m => m.From, m => m.To);
        foreach (var (from, _) in moved)
            changed.UnionWith(lines[movedTo[from]].Tiles);
        foreach (var tile in dirty)
        {
            result.Remove(tile);
            if (w.TryGetValue(tile, out var list))
                result[tile] = list.Select(fromWindow).Where(i => i >= 0).ToList();
        }

        static (int, long) Priority(NetworkLine line) => ((line.Rail ? 100 : 0) + 10 - line.Class, line.Name);
        foreach (var tile in changed.Where(t => !dirty.Contains(t)))
        {
            var list = (p.GetValueOrDefault(tile) ?? []).Where(i => !goneIndex.Contains(i)).Select(i => movedTo.GetValueOrDefault(i, i)).ToList();
            list.AddRange(made.Where(l => l.Tiles.Contains(tile)).Select(l => l.Index));
            list = list.Distinct().ToList();
            list.Sort((a, b) => Priority(lines[a]).CompareTo(Priority(lines[b])));
            if (list.Count > 0)
                result[tile] = list;
            else
                result.Remove(tile);
        }

        return result;
    }

    /// The points over each tile: the window's in the dirty cells; elsewhere previous's, without the lines gone, with the points of the
    /// lines made that fall there, and moved lines' at their new places.
    private static Dictionary<TileId, List<(int Line, int Point)>> MergePoints(Dictionary<TileId, List<(int Line, int Point)>> p,
        Dictionary<TileId, List<(int Line, int Point)>> w, HashSet<TileId> dirty, Func<int, int> fromWindow, HashSet<int> goneIndex,
        List<NetworkLine> made, List<NetworkLine> gone, List<NetworkLine> lines, List<(int From, int To)> moved, double cellSize)
    {
        var result = new Dictionary<TileId, List<(int Line, int Point)>>(p);
        foreach (var tile in dirty)
        {
            result.Remove(tile);
            if (w.TryGetValue(tile, out var list))
                result[tile] = list.Select(q => (fromWindow(q.Line), q.Point)).Where(q => q.Item1 >= 0).ToList();
        }

        var movedTo = moved.ToDictionary(m => m.From, m => m.To);
        // (Which tiles the lines made and gone have points in, from the points themselves.)
        var changed = new Dictionary<TileId, List<(int Line, int Point)>>();
        foreach (var l in gone.Concat(moved.Select(m => lines[m.To])))
            for (var q = 0; q < l.Count; q++)
                changed.TryAdd(new TileId((int)Math.Floor(l.X[q] / cellSize), (int)Math.Floor(-l.Z[q] / cellSize)), []);
        foreach (var l in made)
            for (var q = 0; q < l.Count; q++)
            {
                var tile = new TileId((int)Math.Floor(l.X[q] / cellSize), (int)Math.Floor(-l.Z[q] / cellSize));
                if (!changed.TryGetValue(tile, out var add))
                    changed[tile] = add = [];
                add.Add((l.Index, q));
            }

        foreach (var (tile, add) in changed)
        {
            if (dirty.Contains(tile))
                continue;
            var list = (p.GetValueOrDefault(tile) ?? []).Where(q => !goneIndex.Contains(q.Line)).Select(q => (movedTo.GetValueOrDefault(q.Line, q.Line), q.Point)).ToList();
            list.AddRange(add);
            if (list.Count > 0)
                result[tile] = list;
            else
                result.Remove(tile);
        }

        return result;
    }

    private static Dictionary<long, List<NetworkLine>>? MergeByChain(Dictionary<long, List<NetworkLine>>? p, List<NetworkLine> made, List<NetworkLine> gone, NetworkLines previous)
    {
        if (p is null)
            return null;
        var result = new Dictionary<long, List<NetworkLine>>(p);
        var goneSet = new HashSet<NetworkLine>(gone, ReferenceEqualityComparer.Instance);
        foreach (var chain in gone.Select(l => l.Chain).Concat(made.Select(l => l.Chain)).Distinct())
        {
            var list = (p.GetValueOrDefault(chain) ?? []).Where(l => !goneSet.Contains(l)).Concat(made.Where(l => l.Chain == chain)).ToList();
            if (list.Count > 0)
                result[chain] = list;
            else
                result.Remove(chain);
        }

        return result;
    }

    /// An entry of a field of line indices with each index mapped: an int, a list of them, a tuple whose first item is one.
    private static object Reindex(object? o, Func<int, int> map) => o switch
    {
        int i => map(i),
        List<int> list => list.Select(map).ToList(),
        List<(int Line, int Point)> points => points.Select(q => (map(q.Line), q.Point)).ToList(),
        ValueTuple<long, int> fitted => (fitted.Item1, map(fitted.Item2)),
        ValueTuple<int, int> at => (map(at.Item1), at.Item2),
        _ => o!,
    };

    /// What a thing is, to find it again among previous's: its node, id or key and its place, whichever it has.
    private static long IdentityOf(object thing)
    {
        var hash = new StableHash();
        hash.Add(thing.GetType().FullName ?? "");
        foreach (var name in (ReadOnlySpan<string>)["Node", "Id", "Key", "First", "Last", "X", "Z", "Ex", "Ez", "Cx", "Cz", "Sub", "Kind"])
            if (MemberOf(thing.GetType(), name) is { } get && get(thing) is { } v)
                hash.Add(v switch
                {
                    double d => BitConverter.DoubleToInt64Bits(d),
                    float s => BitConverter.SingleToInt32Bits(s),
                    IConvertible c => c.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
                    _ => 0L,
                });
        return (long)hash.Value;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Func<object, object?>?> Members = new();

    private static Func<object, object?>? MemberOf(Type t, string name) => Members.GetOrAdd((t, name), static k =>
    {
        var (t, name) = k;
        if (t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { } p && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum))
            return o => p.GetValue(o);
        if (t.GetField(name, BindingFlags.Instance | BindingFlags.Public) is { } f && (f.FieldType.IsPrimitive || f.FieldType.IsEnum))
            return o => f.GetValue(o);
        return null;
    });

    /// Whether a (window's) thing or line is just as a (previous's) one: field by field, all the way down, a line in one where the line of
    /// its name is in the other, a registered thing where it's taken as the other's (things), never the line's own copy-of or run boxes.
    private static bool Alike(object a, object b, Dictionary<long, NetworkLine> byName, Dictionary<object, object>? things = null) =>
        Alike(a, b, byName, things, new HashSet<(object, object)>(PairComparer.Instance), 0);

    private sealed class PairComparer : IEqualityComparer<(object, object)>
    {
        public static readonly PairComparer Instance = new();
        public bool Equals((object, object) x, (object, object) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((object, object) p) => HashCode.Combine(RuntimeHelpers.GetHashCode(p.Item1), RuntimeHelpers.GetHashCode(p.Item2));
    }

    private static bool Alike(object? a, object? b, Dictionary<long, NetworkLine> byName, Dictionary<object, object>? things, HashSet<(object, object)> seen, int depth)
    {
        if (a is null || b is null)
            return a is null && b is null;
        var t = a.GetType();
        if (t != b.GetType())
            return false;
        if (t.IsPrimitive || t.IsEnum || a is string || a is decimal)
            return t == typeof(double) ? BitConverter.DoubleToInt64Bits((double)a) == BitConverter.DoubleToInt64Bits((double)b)
                : t == typeof(float) ? BitConverter.SingleToInt32Bits((float)a) == BitConverter.SingleToInt32Bits((float)b)
                : a.Equals(b);
        if (a is NetworkLines)
            return true;
        if (depth > 0 && a is NetworkLine la)
            return byName.TryGetValue(la.Name, out var named) && ReferenceEquals(named, b) || ReferenceEquals(la.Origin, b);
        if (depth > 0 && things is not null && things.TryGetValue(a, out var taken))
            return ReferenceEquals(taken, b);
        if (!t.IsValueType && !seen.Add((a, b)))
            return true;
        if (a is Array aa)
        {
            var ba = (Array)b;
            if (aa.Length != ba.Length)
                return false;
            if (t.GetElementType() is { } et && GraphSnapshot.IsBlittable(et))
                return BytesOf(aa).SequenceEqual(BytesOf(ba));
            for (var i = 0; i < aa.Length; i++)
                if (!Alike(aa.GetValue(i), ba.GetValue(i), byName, things, seen, depth + 1))
                    return false;
            return true;
        }

        if (a is IList al)
        {
            var bl = (IList)b;
            if (al.Count != bl.Count)
                return false;
            for (var i = 0; i < al.Count; i++)
                if (!Alike(al[i], bl[i], byName, things, seen, depth + 1))
                    return false;
            return true;
        }

        if (a is IDictionary ad)
        {
            var bd = (IDictionary)b;
            if (ad.Count != bd.Count)
                return false;
            var be = bd.GetEnumerator();
            foreach (DictionaryEntry e in ad)
            {
                be.MoveNext();
                if (!Alike(e.Key, be.Key, byName, things, seen, depth + 1) || !Alike(e.Value, be.Value, byName, things, seen, depth + 1))
                    return false;
            }

            return true;
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
        {
            var (ea, eb) = (((IEnumerable)a).Cast<object>().ToList(), ((IEnumerable)b).Cast<object>().ToList());
            return ea.Count == eb.Count && ea.ToHashSet().SetEquals(eb);
        }

        foreach (var f in GraphSnapshot.FieldsOf(t))
        {
            if (f.FieldType.IsPointer || (t == typeof(NetworkLine) && f.Name is "_origin" or "_runBoxes" or "_halo" || f.Name == "_index"))
                continue;
            if (f.FieldType.IsSubclassOf(typeof(Delegate)) || typeof(Lock) == f.FieldType)
                continue;
            if (!Alike(f.GetValue(a), f.GetValue(b), byName, things, seen, depth + 1))
                return false;
        }

        return true;
    }

    /// Whether anything in a thing (all the way down) is a line this holds for.
    private static bool Refers(object thing, Func<NetworkLine, bool> which)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool Walk(object? o, int depth)
        {
            // (A thing's set of lines, a slope end's: not walked, it's every line there is.)
            if (o is null || depth > 12 || o is NetworkLines)
                return false;
            if (o is NetworkLine line)
                return depth > 0 && which(line);
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || (o is Array arr && arr.GetType().GetElementType() is { } et && GraphSnapshot.IsBlittable(et)))
                return false;
            if (!t.IsValueType && !seen.Add(o))
                return false;
            if (o is IEnumerable items and not string)
            {
                foreach (var item in items)
                    if (Walk(item is DictionaryEntry e ? e.Key : item, depth + 1) || (item is DictionaryEntry v && Walk(v.Value, depth + 1)))
                        return true;
                return false;
            }

            foreach (var f in GraphSnapshot.FieldsOf(t))
                if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && Walk(f.GetValue(o), depth + 1))
                    return true;
            return false;
        }

        return Walk(thing, 0);
    }

    /// Previous's things that name a line replaced (a line held outside the dirty cells, NetworkLines.HoldAtBorder, or a line copied
    /// because a turnout on it was): copies naming its replacement (previous's own are still shown meanwhile). Anything not naming one
    /// comes back as it is. Thread safe (the fields are merged side by side).
    private sealed class Repointer(Dictionary<NetworkLine, NetworkLine> lines)
    {
        private readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);
        private readonly Lock _lock = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> Holding = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> MayHold = new();

        public int Copies => _copies.Count;

        public object? Map(object? o)
        {
            if (lines.Count == 0 || o is null)
                return o;
            if (o is NetworkLine line)
                return lines.GetValueOrDefault(line, line);
            if (!Touches(o, 0))
                return o;
            lock (_lock)
                return Copy(o, 0);
        }

        private static bool Holds(Type t) => MayHold.GetOrAdd(t, static t =>
            !(t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(TileId) || t == typeof(NetworkLines) ||
              (t.IsArray && GraphSnapshot.IsBlittable(t.GetElementType()!))));

        private static FieldInfo[] FieldsHolding(Type t) => Holding.GetOrAdd(t, static t =>
            [.. GraphSnapshot.FieldsOf(t).Where(f => Holds(f.FieldType) && !f.FieldType.IsSubclassOf(typeof(Delegate)) && f.FieldType != typeof(Lock))]);

        private bool Touches(object? o, int depth)
        {
            switch (o)
            {
                case null:
                    return false;
                case NetworkLine line:
                    return lines.ContainsKey(line);
                case NetworkLines:
                    return false;
            }

            var t = o.GetType();
            if (depth > 4 || !Holds(t))
                return false;
            if (o is IEnumerable items)
            {
                foreach (var item in items)
                    if (item is DictionaryEntry e ? Touches(e.Key, depth + 1) || Touches(e.Value, depth + 1) : Touches(item, depth + 1))
                        return true;
                return false;
            }

            foreach (var f in FieldsHolding(t))
                if (Touches(f.GetValue(o), depth + 1))
                    return true;
            return false;
        }

        private object? Copy(object? o, int depth)
        {
            switch (o)
            {
                case null:
                    return null;
                case NetworkLine line:
                    return lines.GetValueOrDefault(line, line);
                case NetworkLines:
                    return o;
            }

            if (_copies.TryGetValue(o, out var done))
                return done;
            var t = o.GetType();
            if (!Holds(t) || !Touches(o, depth))
                return o;
            switch (o)
            {
                case Array array:
                {
                    var copy = (Array)array.Clone();
                    for (var i = 0; i < copy.Length; i++)
                        copy.SetValue(Copy(copy.GetValue(i), depth + 1), i);
                    return copy;
                }
                case IDictionary dictionary:
                {
                    var copy = (IDictionary)Activator.CreateInstance(t)!;
                    foreach (DictionaryEntry e in dictionary)
                        copy[Copy(e.Key, depth + 1)!] = Copy(e.Value, depth + 1);
                    return copy;
                }
                case IList list:
                {
                    var copy = (IList)Activator.CreateInstance(t)!;
                    foreach (var item in list)
                        copy.Add(Copy(item, depth + 1));
                    return copy;
                }
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                var copy = Activator.CreateInstance(t)!;
                var add = t.GetMethod("Add")!;
                foreach (var item in (IEnumerable)o)
                    add.Invoke(copy, [Copy(item, depth + 1)]);
                return copy;
            }

            // (A value's boxed copy is its own already; an object's copied field by field.)
            var made = t.IsValueType ? o : RuntimeHelpers.GetUninitializedObject(t);
            if (!t.IsValueType)
            {
                _copies[o] = made;
                foreach (var f in GraphSnapshot.FieldsOf(t))
                    f.SetValue(made, f.GetValue(o));
            }

            foreach (var f in FieldsHolding(t))
                f.SetValue(made, Copy(f.GetValue(o), depth + 1));
            return made;
        }
    }

    /// Points the window's objects at what they'll be with: every line at the line it maps to, every registered thing taken as
    /// previous's at that. Changes the window's objects in place (they're the window's own: nothing else has them); a value (a tuple, a
    /// struct, a list's or dictionary's key) comes back made again if anything in it changed.
    private sealed class Remapper(Dictionary<NetworkLine, NetworkLine> lines, Dictionary<object, object> things)
    {
        public Dictionary<object, object> Things => things;
        private readonly HashSet<object> _done = new(ReferenceEqualityComparer.Instance);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> MayRefer = new();

        public object? Map(object? o)
        {
            switch (o)
            {
                case null:
                    return null;
                case NetworkLine line:
                    return lines.GetValueOrDefault(line, line);
                case NetworkLines:
                    return o;
            }

            if (things.TryGetValue(o, out var taken))
                return taken;
            var t = o.GetType();
            if (!Refers(t))
                return o;
            if (!t.IsValueType && !_done.Add(o))
                return o;
            switch (o)
            {
                case Array array:
                    for (var i = 0; i < array.Length; i++)
                        array.SetValue(Map(array.GetValue(i)), i);
                    return o;
                case IDictionary dictionary:
                    var entries = new List<DictionaryEntry>();
                    foreach (DictionaryEntry e in dictionary)
                        entries.Add(e);
                    dictionary.Clear();
                    foreach (var e in entries)
                        dictionary[Map(e.Key)!] = Map(e.Value);
                    return o;
                case IList list:
                    for (var i = 0; i < list.Count; i++)
                        list[i] = Map(list[i]);
                    return o;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                var items = ((IEnumerable)o).Cast<object>().ToList();
                t.GetMethod("Clear")!.Invoke(o, null);
                var add = t.GetMethod("Add")!;
                foreach (var item in items)
                    add.Invoke(o, [Map(item)]);
                return o;
            }

            // (A value's fields set on its boxed copy, which comes back.)
            foreach (var f in GraphSnapshot.FieldsOf(t))
                if (Refers(f.FieldType) || f.FieldType == typeof(object))
                    f.SetValue(o, Map(f.GetValue(o)));
            return o;
        }

        /// Whether a value of this type could hold a line or a thing.
        private static bool Refers(Type t) => MayRefer.GetOrAdd(t, static t =>
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(TileId))
                return false;
            if (t.IsArray)
                return !GraphSnapshot.IsBlittable(t.GetElementType()!) || t.GetElementType() == typeof(object);
            if (t.IsGenericType)
                return t.GetGenericArguments().Any(a => Walk(a, []));
            return Walk(t, []);

            static bool Walk(Type t, HashSet<Type> path)
            {
                if (t == typeof(NetworkLine) || t == typeof(object) || t.IsInterface || t.IsAbstract)
                    return true;
                if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
                    return false;
                if (!path.Add(t))
                    return false;
                if (t.IsArray)
                    return Walk(t.GetElementType()!, path);
                if (t.IsGenericType && t.GetGenericArguments().Any(a => Walk(a, path)))
                    return true;
                if (!t.IsValueType && t.Assembly == typeof(CellStore).Assembly)
                    return true;
                return GraphSnapshot.FieldsOf(t).Any(f => Walk(f.FieldType, path));
            }
        });
    }
}
