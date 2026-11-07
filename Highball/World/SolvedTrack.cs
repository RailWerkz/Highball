using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// A hash that comes out the same in every run of the game (.NET's own is seeded afresh each time): FNV-1a, 64 bit.
internal struct StableHash
{
    public ulong Value { get; private set; } = 14695981039346656037UL;

    public StableHash()
    {
    }

    public void Add(long value)
    {
        for (var i = 0; i < 8; i++)
        {
            Value = (Value ^ ((ulong)value >> (i * 8) & 0xFF)) * 1099511628211UL;
        }
    }

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    public void Add(string value)
    {
        foreach (var c in value)
            Add((long)c);
        Add(-1L);
    }
}

/// The track as solved, kept with the route (Track.bin.gz): every line's shape, its straights and arcs, and what was
/// worked out about its switches. Fitting the mapped track is done once; after that the route opens to what's saved, so
/// track only moves when it's asked to, not because the fitting code changed. It's stamped with what it was solved from
/// (the ways, the sizes picked by hand, the tiles built), and solved again if any of those has changed.
public sealed partial class NetworkLines
{
    /// Bumped when the file's layout changes; an older file is solved again.
    private const int SolvedVersion = 23;

    private static string SolvedPath(RouteInfo route) => Path.Combine(route.Folder, "Track.bin.gz");

    /// Throws the saved track away, so the next open or SolveAgain works it out afresh.
    public static void ForgetSolved(RouteInfo route)
    {
        try
        {
            File.Delete(SolvedPath(route));
        }
        catch (IOException e)
        {
            Log.Warn($"Track: couldn't remove the saved track: {e.Message}");
        }
    }

    private static ulong StampOf(IEnumerable<NetworkWay> network, HashSet<TileId> built, IReadOnlyDictionary<long, int> chosen, IReadOnlyDictionary<long, float> curves, double spacing)
    {
        var hash = new StableHash();
        hash.Add(spacing);
        foreach (var way in network.OrderBy(w => w.Id))
        {
            hash.Add(way.Id);
            hash.Add(way.Rail ? 1L : 0L);
            hash.Add(way.Class);
            foreach (var node in way.Nodes)
                hash.Add(node);
            foreach (var lat in way.Lat)
                hash.Add(lat);
            foreach (var lon in way.Lon)
                hash.Add(lon);
            foreach (var (key, value) in way.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                hash.Add(key);
                hash.Add(value);
            }
        }

        foreach (var (node, size) in chosen.OrderBy(c => c.Key))
        {
            hash.Add(node);
            hash.Add(size);
        }

        foreach (var (node, reach) in curves.OrderBy(c => c.Key))
        {
            hash.Add(node);
            hash.Add((double)reach);
        }

        foreach (var id in built.OrderBy(t => t.I).ThenBy(t => t.J))
        {
            hash.Add(id.I);
            hash.Add(id.J);
        }

        return hash.Value;
    }

    private void SaveSolved(RouteInfo route, ulong stamp)
    {
        var path = SolvedPath(route);
        var temporary = path + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(zip, Encoding.UTF8))
                WriteSolved(w, stamp);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Track: couldn't save the solved track: {e.Message}");
        }
    }

    private void WriteSolved(BinaryWriter w, ulong stamp)
    {
        w.Write(SolvedVersion);
        w.Write(stamp);
        w.Write(_lines.Count);
        foreach (var line in _lines)
        {
            w.Write(line.Rail);
            w.Write(line.Class);
            w.Write(line.Key);
            w.Write(line.Count);
            Numbers(w, line.X);
            Numbers(w, line.Z);
            Numbers(w, line.S);
            Numbers(w, line.Structures);
            w.Write(line.Nodes.Count);
            foreach (var (node, point) in line.Nodes)
            {
                w.Write(node);
                w.Write(point);
            }

            w.Write(line.Tracks);
            w.Write(line.Gauge);
            w.Write(line.Disused);
            w.Write(line.Embedded);
            w.Write(line.Exact);
            w.Write(line.Mark);
            w.Write(line.Width);
            w.Write(line.Lanes);
            w.Write(line.Oneway);
            w.Write(line.Unpaved);
            w.Write(line.Formation);
            w.Write((int)line.Bed);
            w.Write(line.BedHeight);

            w.Write(line.MappedX.Length);
            Numbers(w, line.MappedX);
            Numbers(w, line.MappedZ);

            w.Write(line.Alignment is not null);
            if (line.Alignment is { } alignment)
            {
                w.Write(alignment.Pieces.Count);
                Numbers(w, CollectionsMarshal.AsSpan(alignment.Pieces));
                Numbers(w, alignment.U);
            }
        }

        w.Write(Fitted.Count);
        foreach (var ((node, line), number) in Fitted)
        {
            w.Write(node);
            w.Write(line);
            w.Write(number);
        }

        w.Write(SwitchOptions.Count);
        foreach (var (node, options) in SwitchOptions)
        {
            w.Write(node);
            w.Write(options.Auto ?? int.MinValue);
            w.Write(options.Fits.Length);
            foreach (var fit in options.Fits)
                w.Write(fit);
            w.Write(options.Chosen ?? int.MinValue);
            w.Write(options.Why ?? "");
            w.Write(options.Reach ?? -1f);
            w.Write(options.ReachByHand);
        }

        w.Write(CrossoverProblems.Count);
        foreach (var (node, why) in CrossoverProblems)
        {
            w.Write(node);
            w.Write(why);
        }

        w.Write(Links.Count);
        foreach (var node in Links)
            w.Write(node);
    }

    private static void Numbers<T>(BinaryWriter w, ReadOnlySpan<T> values) where T : unmanaged => w.Write(MemoryMarshal.AsBytes(values));

    private static T[] Numbers<T>(BinaryReader r, int count) where T : unmanaged
    {
        var values = new T[count];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    /// Reads the saved track if it's there and was solved from the same things. False leaves these lines as they were.
    private bool LoadSolved(RouteInfo route, ulong stamp)
    {
        var path = SolvedPath(route);
        if (!File.Exists(path))
            return false;

        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(zip, 1 << 16), Encoding.UTF8);
            if (r.ReadInt32() != SolvedVersion || r.ReadUInt64() != stamp)
                return false;

            var lines = new List<NetworkLine>();
            var count = r.ReadInt32();
            for (var index = 0; index < count; index++)
                lines.Add(ReadLine(r, index));

            var fitted = new Dictionary<(long Node, int Line), int>();
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var (node, line) = (r.ReadInt64(), r.ReadInt32());
                fitted[(node, line)] = r.ReadInt32();
            }

            var options = new Dictionary<long, SwitchOptions>();
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var node = r.ReadInt64();
                var auto = r.ReadInt32();
                var fits = new int[r.ReadInt32()];
                for (var i = 0; i < fits.Length; i++)
                    fits[i] = r.ReadInt32();
                var chosen = r.ReadInt32();
                var why = r.ReadString();
                var reach = r.ReadSingle();
                var byHand = r.ReadBoolean();
                options[node] = new SwitchOptions(auto == int.MinValue ? null : auto, fits, chosen == int.MinValue ? null : chosen, why.Length == 0 ? null : why, reach < 0f ? null : reach, byHand);
            }

            var problems = new Dictionary<long, string>();
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var node = r.ReadInt64();
                problems[node] = r.ReadString();
            }

            var links = new HashSet<long>();
            for (var n = r.ReadInt32(); n > 0; n--)
                links.Add(r.ReadInt64());

            _lines.AddRange(lines);
            Fitted = fitted;
            foreach (var (node, option) in options)
                SwitchOptions[node] = option;
            CrossoverProblems = problems;
            Links = links;
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            Log.Warn($"Track: the saved track is unreadable, solving it again: {e.Message}");
            return false;
        }
    }

    private static NetworkLine ReadLine(BinaryReader r, int index)
    {
        var (rail, kind, key, count) = (r.ReadBoolean(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32());
        var x = Numbers<double>(r, count);
        var z = Numbers<double>(r, count);
        var s = Numbers<float>(r, count);
        var structures = Numbers<Structure>(r, Math.Max(count - 1, 0));
        var nodes = new List<(long Node, int Point)>();
        for (var n = r.ReadInt32(); n > 0; n--)
        {
            var node = r.ReadInt64();
            nodes.Add((node, r.ReadInt32()));
        }

        var line = new NetworkLine
        {
            Index = index, Rail = rail, Class = kind, X = x, Z = z, Y = new float[count], Ground = new float[count], Key = key, S = s, Structures = structures, Nodes = nodes,
            Tracks = r.ReadInt32(), Gauge = r.ReadSingle(), Disused = r.ReadBoolean(), Embedded = r.ReadBoolean(), Exact = r.ReadBoolean(), Mark = r.ReadBoolean(),
            Width = r.ReadSingle(), Lanes = r.ReadInt32(), Oneway = r.ReadBoolean(), Unpaved = r.ReadBoolean(), Formation = r.ReadSingle(),
            Bed = (TrackBed)r.ReadInt32(), BedHeight = r.ReadSingle(),
        };

        var mapped = r.ReadInt32();
        line.MappedX = Numbers<double>(r, mapped);
        line.MappedZ = Numbers<double>(r, mapped);
        if (r.ReadBoolean())
        {
            var pieces = r.ReadInt32();
            line.Alignment = Alignment.Restored(Numbers<TrackPiece>(r, pieces), Numbers<double>(r, count));
        }

        line.MinX = x.Min();
        line.MaxX = x.Max();
        line.MinZ = z.Min();
        line.MaxZ = z.Max();
        return line;
    }
}
