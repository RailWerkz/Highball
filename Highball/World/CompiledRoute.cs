using System.Reflection;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The route's lines as they come out finished (Cache/Lines.bin): solved, the roads shaped, the ground sampled, every profile,
/// bridge, pad, junction and layout worked out. A route opens from this rather than working it all out again, as long as
/// nothing it was worked out from has changed: the map data, the track shaped by hand, the bridges' and switches' settings,
/// the level crossings, the tiles' ground, the solved track, and the build of the game. Written whenever a route's opened and
/// it wasn't there or was out of date. HB_COMPILECHECK=1: opened from it and worked out afresh too, the two compared, logged.
/// HB_NOCOMPILED=1: never read (still written).
internal static class CompiledRoute
{
    private const int Magic = 0x4C504D43; // "CMPL"
    private const int Version = 1;

    public static readonly bool Check = Environment.GetEnvironmentVariable("HB_COMPILECHECK") == "1";
    private static readonly bool Off = Environment.GetEnvironmentVariable("HB_NOCOMPILED") == "1";

    private static string PathOf(RouteInfo route) => Path.Combine(route.Folder, "Cache", "Lines.bin");

    /// The files the lines are worked out from, read whole into the stamp.
    private static readonly string[] Inputs = ["Route.json", "Network.json.gz", "Nodes.json.gz", "TrackEdits.bin.gz", "Bridges.json", "Switches.json", "SwitchCurves.json"];

    /// Fields left out: what the route's opened with anyway, put back from that (Restore).
    private static bool Skip(FieldInfo f) => (f.DeclaringType == typeof(NetworkLine) && f.Name == "_origin") || f.DeclaringType == typeof(NetworkLines) && f.Name is "Ways" or "Shapes" or "BuiltTiles" or "_crossingObjects" or "Before"
        or "<BridgeEdits>k__BackingField" or "<DetailCache>k__BackingField";

    public static ulong StampOf(RouteInfo route, IEnumerable<TileId> built, IEnumerable<PlacedObject> objects)
    {
        var hash = new WordHash();
        var mvid = typeof(CompiledRoute).Assembly.ManifestModule.ModuleVersionId.ToByteArray();
        hash.Add(BitConverter.ToUInt64(mvid, 0));
        hash.Add(BitConverter.ToUInt64(mvid, 8));
        hash.Add(Version);
        foreach (var name in Inputs)
        {
            var path = Path.Combine(route.Folder, name);
            if (!File.Exists(path))
            {
                hash.Add(-1);
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            hash.Add(bytes.Length);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes);
            foreach (var word in words)
                hash.Add(word);
            for (var i = words.Length * 8; i < bytes.Length; i++)
                hash.Add((int)bytes[i]);
        }

        // (The solved track by when it was written: it's 100 MB and more, and only ever written whole.)
        var solved = new FileInfo(Path.Combine(route.Folder, "Track.bin.gz"));
        hash.Add(solved.Exists ? solved.Length : -1);
        hash.Add(solved.Exists ? solved.LastWriteTimeUtc.Ticks : 0);

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

    public static NetworkLines? TryLoad(RouteInfo route, ulong stamp)
    {
        if (Off || route.Pack is not null)
            return null;
        var path = PathOf(route);
        if (!File.Exists(path))
        {
            Log.Info("Compiled route: none yet, working the lines out");
            return null;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(file);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadUInt64() != stamp)
            {
                Log.Info("Compiled route: out of date (the map, edits, settings, ground or game have changed since), working the lines out");
                return null;
            }

            var lines = (NetworkLines)new GraphSnapshot.Reader(r, Skip).ReadRoot();
            Log.Info($"Compiled route: {lines.Lines.Count} lines read in {watch.ElapsedMilliseconds} ms ({file.Length / (1 << 20)} MB)");
            return lines;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or TargetInvocationException or InvalidCastException or ArgumentException)
        {
            Log.Warn($"Compiled route: unreadable ({e.Message}), working the lines out");
            return null;
        }
    }

    /// current: whether what the stamp was taken from is still what's saved (checked before the file's put in place); null: it is.
    public static void Save(RouteInfo route, ulong stamp, NetworkLines lines, Func<bool>? current = null)
    {
        if (route.Pack is not null)
            return;
        var path = PathOf(route);
        var temporary = path + ".tmp";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int objects;
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(file))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(stamp);
                var writer = new GraphSnapshot.Writer(w, Skip);
                writer.WriteRoot(lines);
                objects = writer.Objects;
            }

            if (current is not null && !current())
            {
                File.Delete(temporary);
                Log.Info("Compiled route: saved again while it was being written, left for the next save");
                return;
            }

            File.Move(temporary, path, true);
            Log.Info($"Compiled route: {lines.Lines.Count} lines ({objects} objects, {new FileInfo(path).Length / (1 << 20)} MB) written in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or TargetInvocationException)
        {
            Log.Warn($"Compiled route: couldn't write it ({(e as TargetInvocationException)?.InnerException?.Message ?? e.Message})");
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    /// What was left out of the file, put back from what the route's opened with.
    public static void Restore(NetworkLines lines, RouteNetwork network, IReadOnlyDictionary<long, Alignment> shapes, HashSet<TileId> built,
        IReadOnlyDictionary<long, BridgeEdit> bridgeEdits, List<PlacedObject> objects, RouteProjection projection)
    {
        var type = typeof(NetworkLines);
        void Set(string name, object? value) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(lines, value);
        Set("Ways", network.Ways.Values.ToDictionary(w => w.Id));
        Set("Shapes", shapes);
        Set("BuiltTiles", built);
        Set("<DetailCache>k__BackingField", new System.Collections.Concurrent.ConcurrentDictionary<NetworkLines.DetailRegion, Lazy<float[]>>());
        lines.BridgeEdits = bridgeEdits;
        Set("_crossingObjects", objects.Where(o => o.Kind == PlacedKind.GradeCrossing).ToList());
    }

    /// HB_COMPILECHECK: the lines as read against the same worked out afresh.
    public static void Compare(NetworkLines read, NetworkLines fresh)
    {
        var (off, missing) = (0, 0);
        var worst = "";
        var byKey = fresh.Lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in read.Lines)
        {
            if (!byKey.TryGetValue(line.Key, out var others) || others.FirstOrDefault(o => o.Index == line.Index) is not { } other)
            {
                missing++;
                continue;
            }

            if (read.ShapingHashOf(line) != fresh.ShapingHashOf(other))
            {
                off++;
                if (worst.Length == 0)
                    worst = $" (first: line {line.Key}, {line.Count} points)";
            }
        }

        Log.Info($"Compiled route check: {read.Lines.Count} lines read, {fresh.Lines.Count} worked out; {off} differ{worst}, {missing} not found; " +
                 $"turnouts {read.Turnouts.Count}/{fresh.Turnouts.Count}, layouts {read.Layouts.Count}/{fresh.Layouts.Count}, pads {read.Pads.Count}/{fresh.Pads.Count}");
    }
}
