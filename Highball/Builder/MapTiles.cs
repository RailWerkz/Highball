using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Brutal.ImGuiApi;
using Brutal.StbApi;
using Highball.App;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Vulkan;

namespace Highball.Builder;

/// The map's background: OpenStreetMap's standard raster tiles, fetched two at a time with a proper User-Agent (as
/// its tile policy asks), cached on disk under the user's data folder, and kept as textures while they're on screen.
public sealed class MapTiles : IDisposable
{
    public const string Attribution = "Â© OpenStreetMap contributors";
    private const string Url = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";
    private const int MaxTextures = 320;
    private const int UploadsPerFrame = 6;

    private sealed class Entry
    {
        public Texture2D? Texture;
        public ImTextureRef Ref;
        public int LastUsed;
        public bool Failed;
    }

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Lanes = new(2);

    private readonly Dictionary<(int Z, int X, int Y), Entry> _entries = [];
    private readonly ConcurrentQueue<((int Z, int X, int Y) Key, byte[]? Pixels, int Width, int Height)> _loaded = new();
    private int _frame;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Highball-RouteBuilder", typeof(MapTiles).Assembly.GetName().Version?.ToString(3) ?? "0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(RailWerkz)"));
        return client;
    }

    /// Call once a frame before drawing: uploads what finished loading and forgets textures long off screen.
    public void BeginFrame()
    {
        _frame++;
        for (var n = 0; n < UploadsPerFrame && _loaded.TryDequeue(out var done); n++)
        {
            if (!_entries.TryGetValue(done.Key, out var entry))
                continue;

            if (done.Pixels is null)
            {
                entry.Failed = true;
                continue;
            }

            var texture = Texture2D.FromPixels(done.Width, done.Height, done.Pixels, false, false, name: "map");
            entry.Ref = HighballApp.Instance.AddTexture(texture);
            entry.Texture = texture;
        }

        if (_entries.Count <= MaxTextures)
            return;

        foreach (var (key, entry) in _entries.OrderBy(e => e.Value.LastUsed).Take(_entries.Count - MaxTextures).ToList())
        {
            if (entry.LastUsed >= _frame - 2)
                break;
            Release(entry);
            _entries.Remove(key);
        }
    }

    /// The tile's texture if it's loaded; starts loading it if not.
    public ImTextureRef? Get(int z, int x, int y)
    {
        var n = 1 << z;
        if (y < 0 || y >= n)
            return null;
        x = ((x % n) + n) % n;

        var key = (z, x, y);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry = new Entry();
            _ = Load(key);
        }

        entry.LastUsed = _frame;
        return entry.Texture is not null ? entry.Ref : null;
    }

    private async Task Load((int Z, int X, int Y) key)
    {
        try
        {
            var folder = Path.Combine(ContentPath.UserData, "TileCache", "osm", key.Z.ToString(), key.X.ToString());
            var path = Path.Combine(folder, $"{key.Y}.png");
            if (!File.Exists(path))
            {
                await Lanes.WaitAsync();
                try
                {
                    var bytes = await Http.GetByteArrayAsync(string.Format(Url, key.Z, key.X, key.Y));
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(path, bytes);
                }
                finally
                {
                    Lanes.Release();
                }
            }

            using var image = Stb.Load(path, 4);
            _loaded.Enqueue((key, image.AsSpan().ToArray(), image.Width, image.Height));
        }
        catch (Exception e)
        {
            Log.Warn($"Map tile {key.Z}/{key.X}/{key.Y}: {e.Message}");
            _loaded.Enqueue((key, null, 0, 0));
        }
    }

    private static void Release(Entry entry)
    {
        if (entry.Texture is null)
            return;
        HighballApp.Instance.RemoveTexture(entry.Ref);
        entry.Texture.Dispose();
        entry.Texture = null;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            Release(entry);
        _entries.Clear();
    }
}
