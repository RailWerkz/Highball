using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Brutal.StbApi;
using RailWerkz.Core.Logging;

namespace Highball.Routes;

/// Moves a route made before zones (laid out around its own origin) onto the UTM zone grid, keeping what's been built:
/// every tile is sampled again from the old ones (heights with Catmull-Rom, sculpting bilinearly, land cover nearest),
/// headings turn with the plane, and the solved track is left to be solved again. The old folder is kept beside it as
/// NAME.prezone. Tiles only partly over the old ground have their missing samples filled from the nearest ones;
/// building those again from the Planner fills them properly.
public static class ZoneMigration
{
    /// The grid the mapping from new to old is worked out exactly on, per tile side; between, it's interpolated.
    private const int Lattice = 21;

    public static void Run(string id)
    {
        var routes = RouteInfo.All();
        var old = routes.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"no route called {id}");
        if (old.Zone is not null)
        {
            Log.Info($"Zone migration: {old.Name} is already on zone {old.Zone}");
            return;
        }

        if (old.Grid is not { } grid || old.Projection is not { } from)
            throw new InvalidOperationException($"{old.Name} has nothing built");

        var zone = RouteProjection.ZoneOf(old.Origin.Longitude);
        var to = new RouteProjection(zone);
        var target = old.Folder + ".zoned";
        var backup = old.Folder + ".prezone";
        if (Directory.Exists(target) || Directory.Exists(backup))
            throw new InvalidOperationException($"{target} or {backup} is already there; move it away first");

        var started = DateTime.UtcNow;
        Log.Info($"Zone migration: {old.Name} onto UTM zone {zone} into {target}");
        var source = new OldTiles(old, grid);

        // The new tiles: every one any old tile's ground reaches.
        var size = grid.TileSize;
        var candidates = new HashSet<TileId>();
        foreach (var t in source.Built)
        {
            for (var a = 0; a <= 4; a++)
            for (var b = 0; b <= 4; b++)
            {
                var p = to.Forward(from.Inverse(new RoutePoint((t.I + a / 4.0) * size, (t.J + b / 4.0) * size)));
                candidates.Add(grid.TileAt(p));
            }
        }

        var samples = grid.Samples;
        var cells = grid.CoverCells;
        var written = 0;
        var partial = new List<TileId>();
        foreach (var tile in candidates.OrderBy(t => -t.J).ThenBy(t => t.I))
        {
            var map = new Mapping(grid, tile, from, to);
            var heights = new float[samples * samples];
            var edits = new float[samples * samples];
            var known = new bool[samples * samples];
            var count = 0;
            var anyEdit = false;
            for (var z = 0; z < samples; z++)
            for (var x = 0; x < samples; x++)
            {
                var (e, n) = map.At(x / (double)(samples - 1), z / (double)(samples - 1));
                var h = source.Height(e, n);
                if (float.IsNaN(h))
                    continue;
                var i = z * samples + x;
                heights[i] = h;
                known[i] = true;
                count++;
                var edit = source.Edit(e, n);
                edits[i] = edit;
                anyEdit |= edit != 0f;
            }

            if (count == 0)
                continue;

            if (count < heights.Length)
            {
                Fill(heights, known, samples);
                partial.Add(tile);
            }

            var cover = new byte[cells * cells];
            var coverKnown = new bool[cells * cells];
            for (var z = 0; z < cells; z++)
            for (var x = 0; x < cells; x++)
            {
                var (e, n) = map.At((x + 0.5) / cells, (z + 0.5) / cells);
                if (source.Cover(e, n) is { } c)
                {
                    cover[z * cells + x] = c;
                    coverKnown[z * cells + x] = true;
                }
            }

            Fill(cover, coverKnown, cells);

            var (ce, cn) = map.At(0.5, 0.5);
            var like = source.InfoNear(ce, cn);
            var meta = new RouteTile.Info
            {
                I = tile.I, J = tile.J, Samples = samples, Spacing = grid.Spacing, CoverCells = cells,
                MinHeight = heights.Min(), MaxHeight = heights.Max(),
                Roads = like?.Roads ?? "", Rails = like?.Rails ?? "", HeightSource = like?.HeightSource ?? "",
                Built = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            };
            var folder = Path.Combine(target, "Tiles", tile.ToString());
            new RouteTile(meta, heights, cover).Save(folder);
            if (anyEdit)
            {
                using var file = File.Create(Path.Combine(folder, "Edits.bin.gz"));
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                gzip.Write(MemoryMarshal.AsBytes(edits.AsSpan()));
            }

            written++;
        }

        // Everything else: kept, except what's in plane metres. The solved track is a cache; hand shapes are keyed by
        // their lines' geometry, which moves, so they couldn't be found again.
        foreach (var file in Directory.GetFiles(old.Folder))
        {
            var name = Path.GetFileName(file);
            if (name is "Track.bin.gz" or "Route.json")
                continue;
            if (name == "TrackEdits.bin.gz")
            {
                Log.Warn("Zone migration: hand-shaped track (TrackEdits.bin.gz) can't be carried over and is left in the backup");
                continue;
            }

            File.Copy(file, Path.Combine(target, name));
        }

        TurnHeadings(Path.Combine(target, "Objects.json"), from, to, clockwise: true);
        TurnHeadings(Path.Combine(target, "Stock.json"), from, to, clockwise: false);

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(old.Folder, "Route.json")))!.AsObject();
        json["zone"] = zone;
        File.WriteAllText(Path.Combine(target, "Route.json"), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Directory.Move(old.Folder, backup);
        Directory.Move(target, old.Folder);
        Log.Info($"Zone migration: {written} tiles from {source.Built.Count} in {(DateTime.UtcNow - started).TotalSeconds:0} s; " +
                 $"{partial.Count} only partly over the old ground (edges filled): {string.Join(' ', partial)}. Old route kept in {backup}");
    }

    /// Headings in a JSON list of things with Lat, Lon and Heading, turned by how much the plane turns there. Objects'
    /// headings run from +X towards +Z (clockwise from above); stock's from +Z towards +X (anticlockwise).
    private static void TurnHeadings(string path, RouteProjection from, RouteProjection to, bool clockwise)
    {
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray list)
            return;
        // Objects store degrees; stock radians (from Math.Atan2).
        foreach (var item in list.OfType<JsonObject>())
        {
            var lat = Get(item, "Lat");
            var lon = Get(item, "Lon");
            var key = item.Select(p => p.Key).FirstOrDefault(k => k.Equals("Heading", StringComparison.OrdinalIgnoreCase));
            if (lat is null || lon is null || key is null)
                continue;
            var place = new GeoPoint(lat.Value, lon.Value);
            var turn = to.GridNorth(place) - from.GridNorth(place);
            var degrees = clockwise ? turn : -turn;
            item[key] = item[key]!.GetValue<double>() + (clockwise ? degrees : degrees * Math.PI / 180.0);
        }

        File.WriteAllText(path, list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static double? Get(JsonObject item, string name) =>
        item.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<double>();

    /// Gaps filled from their nearest known neighbours, spreading a ring at a time.
    private static void Fill<T>(T[] values, bool[] known, int side)
    {
        var frontier = new List<int>();
        for (var i = 0; i < values.Length; i++)
            if (known[i])
                frontier.Add(i);
        if (frontier.Count == 0)
            return;

        while (frontier.Count > 0)
        {
            var next = new List<int>();
            foreach (var i in frontier)
            {
                var (x, z) = (i % side, i / side);
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var (nx, nz) = (x + dx, z + dz);
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side)
                        continue;
                    var j = nz * side + nx;
                    if (known[j])
                        continue;
                    values[j] = values[i];
                    known[j] = true;
                    next.Add(j);
                }
            }

            frontier = next;
        }
    }

    /// Where a new tile's points are on the old plane: exact on a Lattice x Lattice grid, bilinear between (the
    /// mapping bends by well under a millimetre across 100 m).
    private sealed class Mapping
    {
        private readonly double[] _e = new double[Lattice * Lattice];
        private readonly double[] _n = new double[Lattice * Lattice];

        public Mapping(RouteGrid grid, TileId tile, RouteProjection from, RouteProjection to)
        {
            for (var b = 0; b < Lattice; b++)
            for (var a = 0; a < Lattice; a++)
            {
                // u east across the tile, v south down it, both 0 to 1 (image order, like the samples).
                var (u, v) = (a / (double)(Lattice - 1), b / (double)(Lattice - 1));
                var p = new RoutePoint((tile.I + u) * grid.TileSize, (tile.J + 1 - v) * grid.TileSize);
                var q = from.Forward(to.Inverse(p));
                _e[b * Lattice + a] = q.East;
                _n[b * Lattice + a] = q.North;
            }
        }

        public (double East, double North) At(double u, double v)
        {
            var fx = u * (Lattice - 1);
            var fz = v * (Lattice - 1);
            var x = Math.Min((int)fx, Lattice - 2);
            var z = Math.Min((int)fz, Lattice - 2);
            var (tx, tz) = (fx - x, fz - z);
            double Lerp(double[] g) =>
                (g[z * Lattice + x] * (1 - tx) + g[z * Lattice + x + 1] * tx) * (1 - tz) +
                (g[(z + 1) * Lattice + x] * (1 - tx) + g[(z + 1) * Lattice + x + 1] * tx) * tz;
            return (Lerp(_e), Lerp(_n));
        }
    }

    /// The old route's tiles, read as they're needed and a few kept in memory.
    private sealed class OldTiles(RouteInfo route, RouteGrid grid)
    {
        private sealed record Loaded(float[] Heights, float[]? Edits, byte[] Cover, RouteTile.Info Info);

        private readonly Dictionary<TileId, Loaded> _cache = [];
        private readonly LinkedList<TileId> _order = [];
        private readonly int _quads = grid.Samples - 1;

        public HashSet<TileId> Built { get; } = route.BuiltTiles();

        private Loaded? Get(TileId id)
        {
            if (_cache.TryGetValue(id, out var hit))
                return hit;
            if (!Built.Contains(id))
                return null;

            var folder = route.TileFolder(id);
            var tile = RouteTile.Load(folder);
            float[]? edits = null;
            var path = Path.Combine(folder, "Edits.bin.gz");
            if (File.Exists(path))
            {
                using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                using var output = new MemoryStream();
                input.CopyTo(output);
                var e = MemoryMarshal.Cast<byte, float>(output.ToArray()).ToArray();
                edits = e.Length == tile.Heights.Length ? e : null;
            }

            var loaded = new Loaded(tile.Heights, edits, tile.Cover, tile.Meta);
            _cache[id] = loaded;
            _order.AddLast(id);
            if (_order.Count > 24)
            {
                _cache.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }

            return loaded;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// The old sample at global column gx (east) and row gz (south), or NaN where nothing's built.
        private float Sample(int gx, int gz, bool edit)
        {
            var i = FloorDiv(gx, _quads);
            var k = FloorDiv(gz, _quads);
            if (Get(new TileId(i, -k - 1)) is not { } t)
                return float.NaN;
            var index = (gz - k * _quads) * grid.Samples + (gx - i * _quads);
            return edit ? t.Edits?[index] ?? 0f : t.Heights[index];
        }

        public float Height(double east, double north)
        {
            var fx = east / grid.Spacing;
            var fz = -north / grid.Spacing;
            var x = (int)Math.Floor(fx);
            var z = (int)Math.Floor(fz);
            var (tx, tz) = (fx - x, fz - z);
            Span<double> rows = stackalloc double[4];
            for (var r = 0; r < 4; r++)
            {
                var p0 = Sample(x - 1, z - 1 + r, false);
                var p1 = Sample(x, z - 1 + r, false);
                var p2 = Sample(x + 1, z - 1 + r, false);
                var p3 = Sample(x + 2, z - 1 + r, false);
                // At the edge of the old ground, the curve leans on the nearest sample instead.
                rows[r] = float.IsNaN(p1) || float.IsNaN(p2) ? double.NaN : CatmullRom(p0, p1, p2, p3, tx);
            }

            if (double.IsNaN(rows[1]) || double.IsNaN(rows[2]))
                return float.NaN;
            return (float)CatmullRom(rows[0], rows[1], rows[2], rows[3], tz);
        }

        private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
        {
            if (double.IsNaN(p0))
                p0 = p1;
            if (double.IsNaN(p3))
                p3 = p2;
            return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t * t * t);
        }

        public float Edit(double east, double north)
        {
            var fx = east / grid.Spacing;
            var fz = -north / grid.Spacing;
            var x = (int)Math.Floor(fx);
            var z = (int)Math.Floor(fz);
            var (tx, tz) = (fx - x, fz - z);
            float S(int a, int b) => Sample(a, b, true) is var v && float.IsNaN(v) ? 0f : v;
            return (float)((S(x, z) * (1 - tx) + S(x + 1, z) * tx) * (1 - tz) + (S(x, z + 1) * (1 - tx) + S(x + 1, z + 1) * tx) * tz);
        }

        public byte? Cover(double east, double north)
        {
            var cells = grid.CoverCells;
            var cell = grid.TileSize / cells;
            var cx = (int)Math.Floor(east / cell);
            var cz = (int)Math.Floor(-north / cell);
            var i = FloorDiv(cx, cells);
            var k = FloorDiv(cz, cells);
            if (Get(new TileId(i, -k - 1)) is not { } t)
                return null;
            return t.Cover[(cz - k * cells) * cells + (cx - i * cells)];
        }

        public RouteTile.Info? InfoNear(double east, double north)
        {
            var id = grid.TileAt(new RoutePoint(east, north));
            if (Get(id) is { } t)
                return t.Info;
            foreach (var b in Built.OrderBy(b => Math.Abs(b.I - id.I) + Math.Abs(b.J - id.J)))
                return Get(b)?.Info;
            return null;
        }
    }
}
