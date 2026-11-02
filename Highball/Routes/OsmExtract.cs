using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// OpenStreetMap from Geofabrik's regional extracts instead of a public query server: the smallest region holding
/// each piece of the route (a US state, a country, a province) is downloaded once as .osm.pbf and read here, so
/// builds have no rate limits, every tag, and the same answer every time.
public static class OsmExtract
{
    private const string IndexUrl = "https://download.geofabrik.de/index-v1.json";

    private static readonly HttpClient Http = CreateClient();

    public sealed record Region(string Id, string Name, string Url, double Area, List<List<(double Lon, double Lat)>> Rings);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Highball-RouteBuilder", typeof(OsmExtract).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return client;
    }

    private static string Folder
    {
        get
        {
            var folder = Path.Combine(ContentPath.UserData, "TileCache", "geofabrik");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static List<Region>? _regions;

    /// Geofabrik's regions with their outlines (the index is refreshed weekly).
    public static async Task<List<Region>> Regions(CancellationToken token)
    {
        if (_regions is not null)
            return _regions;

        var path = Path.Combine(Folder, "index-v1.json");
        if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7))
            await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(IndexUrl, token), token);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        var regions = new List<Region>();
        foreach (var feature in document.RootElement.GetProperty("features"u8).EnumerateArray())
        {
            var properties = feature.GetProperty("properties"u8);
            if (!properties.TryGetProperty("urls"u8, out var urls) || !urls.TryGetProperty("pbf"u8, out var pbf))
                continue;
            if (!feature.TryGetProperty("geometry"u8, out var geometry) || geometry.ValueKind != JsonValueKind.Object)
                continue;

            var rings = new List<List<(double, double)>>();
            var polygons = geometry.GetProperty("type"u8).GetString() == "Polygon"
                ? [geometry.GetProperty("coordinates"u8)]
                : geometry.GetProperty("coordinates"u8).EnumerateArray().ToList();
            foreach (var polygon in polygons)
            {
                // The outer ring only: holes in an extract's outline are rare and harmless to ignore.
                var outer = polygon.EnumerateArray().First();
                rings.Add([.. outer.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble()))]);
            }

            var area = rings.Sum(r => Math.Abs(SignedArea(r)));
            regions.Add(new Region(properties.GetProperty("id"u8).GetString()!, properties.GetProperty("name"u8).GetString() ?? "", pbf.GetString()!, area, rings));
        }

        return _regions = regions;
    }

    private static double SignedArea(List<(double Lon, double Lat)> ring)
    {
        var sum = 0.0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += (ring[j].Lon - ring[i].Lon) * (ring[j].Lat + ring[i].Lat);
        return sum / 2.0;
    }

    private static bool Contains(Region region, GeoPoint p)
    {
        foreach (var ring in region.Rings)
        {
            var inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                if ((ring[i].Lat > p.Lat) != (ring[j].Lat > p.Lat) &&
                    p.Lon < (ring[j].Lon - ring[i].Lon) * (p.Lat - ring[i].Lat) / (ring[j].Lat - ring[i].Lat) + ring[i].Lon)
                    inside = !inside;
            }

            if (inside)
                return true;
        }

        return false;
    }

    /// The smallest region holding the point, or null where Geofabrik has none.
    public static Region? Smallest(List<Region> regions, GeoPoint p) =>
        regions.Where(r => Contains(r, p)).MinBy(r => r.Area);

    /// The region's .osm.pbf, downloaded if it isn't here yet (or is over a month old). progress gets bytes so far
    /// and the total.
    public static async Task<string> Download(Region region, Action<long, long> progress, CancellationToken token)
    {
        var path = Path.Combine(Folder, region.Id.Replace('/', '_') + ".osm.pbf");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(30))
            return path;

        Log.Info($"OSM extract: downloading {region.Name} from {region.Url}");
        using var response = await Http.GetAsync(region.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        var partial = path + ".part";

        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress(done, total);
            }
        }

        File.Move(partial, path, true);
        return path;
    }

    // --- Reading .osm.pbf: just enough protobuf. ---

    private ref struct Proto(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public bool More => _at < _data.Length;

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = _data[_at++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
            }
        }

        public long Signed()
        {
            var v = Varint();
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        public (int Field, int Wire) Tag()
        {
            var tag = Varint();
            return ((int)(tag >> 3), (int)(tag & 7));
        }

        /// Where the next length-prefixed field's bytes start in the data, and how long they are (then skips them).
        public (int Start, int Length) Range()
        {
            var length = (int)Varint();
            var start = _at;
            _at += length;
            return (start, length);
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = (int)Varint();
            var span = _data.Slice(_at, length);
            _at += length;
            return span;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _at += 8; break;
                case 2:
                    var length = (int)Varint(); // read first: "_at += Varint()" would add to the old position
                    _at += length;
                    break;
                case 5: _at += 4; break;
                default: throw new InvalidDataException($"protobuf wire type {wire}");
            }
        }
    }

    /// The file's data blobs: where each starts and how long it is (read once, then decoded in parallel).
    private static List<(long Offset, int Length)> Blobs(string path)
    {
        var blobs = new List<(long, int)>();
        using var file = File.OpenRead(path);
        var size = new byte[4];
        while (file.Read(size, 0, 4) == 4)
        {
            var headerLength = (size[0] << 24) | (size[1] << 16) | (size[2] << 8) | size[3];
            var header = new byte[headerLength];
            file.ReadExactly(header);

            var type = "";
            var dataSize = 0;
            var p = new Proto(header);
            while (p.More)
            {
                var (field, wire) = p.Tag();
                if (field == 1)
                    type = Encoding.UTF8.GetString(p.Bytes());
                else if (field == 3)
                    dataSize = (int)p.Varint();
                else
                    p.Skip(wire);
            }

            if (type == "OSMData")
                blobs.Add((file.Position, dataSize));
            file.Seek(dataSize, SeekOrigin.Current);
        }

        return blobs;
    }

    private static byte[] Inflate(string path, long offset, int length)
    {
        var blob = new byte[length];
        using (var file = File.OpenRead(path))
        {
            file.Seek(offset, SeekOrigin.Begin);
            file.ReadExactly(blob);
        }

        var raw = ReadOnlySpan<byte>.Empty;
        var rawSize = 0;
        byte[]? zlib = null;
        var p = new Proto(blob);
        while (p.More)
        {
            var (field, wire) = p.Tag();
            switch (field)
            {
                case 1: raw = p.Bytes(); break;
                case 2: rawSize = (int)p.Varint(); break;
                case 3: zlib = p.Bytes().ToArray(); break;
                default: p.Skip(wire); break;
            }
        }

        if (zlib is null)
            return raw.ToArray();

        var data = new byte[rawSize];
        using var input = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        input.ReadExactly(data);
        return data;
    }

    /// A primitive block's string table, coordinate scaling, and its groups (scaling fields come after the groups).
    private static (string[] Strings, long Granularity, long LatOffset, long LonOffset, List<(int Start, int Length)> Groups) Block(byte[] data)
    {
        var strings = Array.Empty<string>();
        long granularity = 100, latOffset = 0, lonOffset = 0;
        var groups = new List<(int, int)>();
        var p = new Proto(data);

        while (p.More)
        {
            var (field, wire) = p.Tag();
            switch (field)
            {
                case 1:
                {
                    var table = new Proto(p.Bytes());
                    var list = new List<string>();
                    while (table.More)
                    {
                        var (f, w) = table.Tag();
                        if (f == 1)
                            list.Add(Encoding.UTF8.GetString(table.Bytes()));
                        else
                            table.Skip(w);
                    }

                    strings = [.. list];
                    break;
                }
                case 2:
                    groups.Add(p.Range());
                    break;
                case 17: granularity = (long)p.Varint(); break;
                case 19: latOffset = (long)p.Varint(); break;
                case 20: lonOffset = (long)p.Varint(); break;
                default: p.Skip(wire); break;
            }
        }

        return (strings, granularity, latOffset, lonOffset, groups);
    }


    public sealed class Way
    {
        public long Id;
        public bool Rail;
        public int Class;
        public long[] Refs = [];
        public Dictionary<string, string> Tags = [];
    }

    /// Every way the classes take in, from a whole extract, with its points; then only those touching the box are
    /// kept. Two passes: the ways and the nodes they need, then those nodes' positions.
    public static List<NetworkWay> Read(string path, RoadClass[] roads, RailClass[] rails,
        (double South, double West, double North, double East) box, Action<string> say, CancellationToken token)
    {
        var blobs = Blobs(path);
        var ways = new System.Collections.Concurrent.ConcurrentBag<Way>();
        var done = 0;

        // Pass 1: ways.
        Parallel.ForEach(blobs, new ParallelOptions { CancellationToken = token }, blob =>
        {
            var data = Inflate(path, blob.Offset, blob.Length);
            var (strings, _, _, _, groups) = Block(data);
            foreach (var (start, length) in groups)
            {
                var group = new Proto(data.AsSpan(start, length));
                while (group.More)
                {
                    var (field, wire) = group.Tag();
                    if (field != 3)
                    {
                        group.Skip(wire);
                        continue;
                    }

                    if (ParseWay(group.Bytes(), strings, roads, rails) is { } way)
                        ways.Add(way);
                }
            }

            if (Interlocked.Increment(ref done) % 200 == 0)
                say($"Reading ways ({done * 100 / blobs.Count}%)");
        });

        // The nodes those ways need, sorted for lookup.
        var needed = ways.SelectMany(w => w.Refs).Distinct().ToArray();
        Array.Sort(needed);
        var lat = new double[needed.Length];
        var lon = new double[needed.Length];
        var found = new bool[needed.Length];
        done = 0;

        // Pass 2: node positions.
        Parallel.ForEach(blobs, new ParallelOptions { CancellationToken = token }, blob =>
        {
            var data = Inflate(path, blob.Offset, blob.Length);
            var (_, granularity, latOffset, lonOffset, groups) = Block(data);
            foreach (var (start, length) in groups)
            {
                var group = new Proto(data.AsSpan(start, length));
                while (group.More)
                {
                    var (field, wire) = group.Tag();
                    if (field == 2)
                        ParseDense(group.Bytes(), granularity, latOffset, lonOffset, needed, lat, lon, found);
                    else if (field == 1)
                        ParseNode(group.Bytes(), granularity, latOffset, lonOffset, needed, lat, lon, found);
                    else
                        group.Skip(wire);
                }
            }

            if (Interlocked.Increment(ref done) % 200 == 0)
                say($"Reading nodes ({done * 100 / blobs.Count}%)");
        });

        var result = new List<NetworkWay>();
        foreach (var way in ways)
        {
            var wayLat = new List<double>();
            var wayLon = new List<double>();
            var nodes = new List<long>();
            var touches = false;
            foreach (var r in way.Refs)
            {
                var i = Array.BinarySearch(needed, r);
                if (i < 0 || !found[i])
                    continue;
                wayLat.Add(lat[i]);
                wayLon.Add(lon[i]);
                nodes.Add(r);
                touches |= lat[i] >= box.South && lat[i] <= box.North && lon[i] >= box.West && lon[i] <= box.East;
            }

            if (touches && wayLat.Count >= 2)
                result.Add(new NetworkWay { Id = way.Id, Rail = way.Rail, Class = way.Class, Nodes = [.. nodes], Lat = [.. wayLat], Lon = [.. wayLon], Tags = way.Tags });
        }

        return result;
    }

    private static Way? ParseWay(ReadOnlySpan<byte> bytes, string[] strings, RoadClass[] roads, RailClass[] rails)
    {
        long id = 0;
        ReadOnlySpan<byte> keys = default, values = default, refs = default;
        var p = new Proto(bytes);
        while (p.More)
        {
            var (field, wire) = p.Tag();
            switch (field)
            {
                case 1: id = (long)p.Varint(); break;
                case 2: keys = p.Bytes(); break;
                case 3: values = p.Bytes(); break;
                case 8: refs = p.Bytes(); break;
                default: p.Skip(wire); break;
            }
        }

        // Tags first, and only the ways that are roads or rail lines go further.
        var tags = new Dictionary<string, string>();
        var k = new Proto(keys);
        var v = new Proto(values);
        while (k.More && v.More)
            tags[strings[(int)k.Varint()]] = strings[(int)v.Varint()];

        if (!tags.ContainsKey("highway") && !tags.ContainsKey("railway"))
            return null;

        string Tag(string key) => tags.GetValueOrDefault(key, "");
        bool rail;
        int cls;
        if (Detail.Rail(Tag) is { } railClass && rails.Contains(railClass))
            (rail, cls) = (true, (int)railClass);
        else if (Detail.Road(Tag("highway")) is { } roadClass && roads.Contains(roadClass))
            (rail, cls) = (false, (int)roadClass);
        else
            return null;

        var list = new List<long>();
        var r = new Proto(refs);
        long last = 0;
        while (r.More)
            list.Add(last += r.Signed());

        var kept = new Dictionary<string, string>();
        foreach (var key in RouteNetwork.KeptTags)
            if (tags.TryGetValue(key, out var value))
                kept[key] = value;

        return new Way { Id = id, Rail = rail, Class = cls, Refs = [.. list], Tags = kept };
    }

    private static void Store(long id, long rawLat, long rawLon, long granularity, long latOffset, long lonOffset, long[] needed, double[] lat, double[] lon, bool[] found)
    {
        var i = Array.BinarySearch(needed, id);
        if (i < 0)
            return;
        lat[i] = 1e-9 * (latOffset + granularity * rawLat);
        lon[i] = 1e-9 * (lonOffset + granularity * rawLon);
        found[i] = true;
    }

    private static void ParseDense(ReadOnlySpan<byte> bytes, long granularity, long latOffset, long lonOffset, long[] needed, double[] lat, double[] lon, bool[] found)
    {
        ReadOnlySpan<byte> ids = default, lats = default, lons = default;
        var p = new Proto(bytes);
        while (p.More)
        {
            var (field, wire) = p.Tag();
            switch (field)
            {
                case 1: ids = p.Bytes(); break;
                case 8: lats = p.Bytes(); break;
                case 9: lons = p.Bytes(); break;
                default: p.Skip(wire); break;
            }
        }

        var a = new Proto(ids);
        var b = new Proto(lats);
        var c = new Proto(lons);
        long id = 0, la = 0, lo = 0;
        while (a.More)
        {
            id += a.Signed();
            la += b.Signed();
            lo += c.Signed();
            Store(id, la, lo, granularity, latOffset, lonOffset, needed, lat, lon, found);
        }
    }

    private static void ParseNode(ReadOnlySpan<byte> bytes, long granularity, long latOffset, long lonOffset, long[] needed, double[] lat, double[] lon, bool[] found)
    {
        long id = 0, la = 0, lo = 0;
        var p = new Proto(bytes);
        while (p.More)
        {
            var (field, wire) = p.Tag();
            switch (field)
            {
                case 1: id = p.Signed(); break;
                case 8: la = p.Signed(); break;
                case 9: lo = p.Signed(); break;
                default: p.Skip(wire); break;
            }
        }

        Store(id, la, lo, granularity, latOffset, lonOffset, needed, lat, lon, found);
    }
}
