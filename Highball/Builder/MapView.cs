using Brutal.ImGuiApi;
using Highball.Routes;
using RailWerkz.UI;

namespace Highball.Builder;

/// A pannable, zoomable Web Mercator map over a rectangle of the screen: the OpenStreetMap background, and conversions
/// between degrees, Mercator (0..1 across the world, y down) and screen pixels for whatever is drawn over it.
public sealed class MapView : IDisposable
{
    private readonly MapTiles _tiles = new();
    private float2 _min;
    private float2 _max;
    private bool _panning;

    /// The centre, in Mercator units.
    public double CenterX { get; private set; } = 0.5;
    public double CenterY { get; private set; } = 0.4;

    /// Fractional zoom: the world is 256 * 2^Zoom pixels across.
    public double Zoom { get; private set; } = 3.0;

    public const double MaxZoom = 19.0;

    private double WorldPixels => 256.0 * Math.Pow(2.0, Zoom);

    public static (double X, double Y) ToMercator(GeoPoint p)
    {
        var lat = Math.Clamp(p.Lat, -85.05, 85.05) * Math.PI / 180.0;
        return ((p.Lon + 180.0) / 360.0, (1.0 - Math.Asinh(Math.Tan(lat)) / Math.PI) / 2.0);
    }

    public static GeoPoint FromMercator(double x, double y) =>
        new(Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y))) * 180.0 / Math.PI, x * 360.0 - 180.0);

    public float2 MercatorToScreen(double x, double y)
    {
        var c = (_min + _max) * 0.5f;
        return new float2(c.X + (float)((x - CenterX) * WorldPixels), c.Y + (float)((y - CenterY) * WorldPixels));
    }

    public float2 ToScreen(GeoPoint p)
    {
        var (x, y) = ToMercator(p);
        return MercatorToScreen(x, y);
    }

    public GeoPoint ToGeo(float2 screen)
    {
        var c = (_min + _max) * 0.5f;
        return FromMercator(CenterX + (screen.X - c.X) / WorldPixels, CenterY + (screen.Y - c.Y) / WorldPixels);
    }

    /// Mercator units per screen pixel, for culling and simplifying what's drawn.
    public double MercatorPerPixel => 1.0 / WorldPixels;

    public void LookAt(GeoPoint p, double zoom)
    {
        (CenterX, CenterY) = ToMercator(p);
        Zoom = Math.Clamp(zoom, 2.0, MaxZoom);
    }

    /// Wheel zooms about the pointer; the right or middle button drags. Call while the pointer is over the map.
    public void Input(bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;

        if (hovered && io.MouseWheel != 0f)
        {
            var before = ToGeo(mouse);
            Zoom = Math.Clamp(Zoom + io.MouseWheel * 0.25, 2.0, MaxZoom);
            var (bx, by) = ToMercator(before);
            var c = (_min + _max) * 0.5f;
            CenterX = bx - (mouse.X - c.X) / WorldPixels;
            CenterY = by - (mouse.Y - c.Y) / WorldPixels;
        }

        if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsMouseClicked(ImGuiMouseButton.Middle)))
            _panning = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            _panning = false;

        if (_panning)
        {
            CenterX -= io.MouseDelta.X / WorldPixels;
            CenterY -= io.MouseDelta.Y / WorldPixels;
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }

        CenterY = Math.Clamp(CenterY, 0.0, 1.0);
        CenterX -= Math.Floor(CenterX);
    }

    /// Lays the map out over the rectangle and draws its background, darkened to sit under the interface. Missing
    /// tiles borrow a stretched piece of a loaded parent until they arrive.
    public void Draw(ImDrawListPtr draw, float2 min, float2 max)
    {
        _min = min;
        _max = max;
        _tiles.BeginFrame();
        draw.AddRectFilled(min, max, Style.Background);

        var z = (int)Math.Clamp(Math.Round(Zoom), 0, MapTiles.MaxZoom);
        var n = 1 << z;
        var tilePixels = WorldPixels / n;
        var topLeft = ToMercatorAt(min);
        var bottomRight = ToMercatorAt(max);
        var x0 = (int)Math.Floor(topLeft.X * n);
        var x1 = (int)Math.Floor(bottomRight.X * n);
        var y0 = Math.Max(0, (int)Math.Floor(topLeft.Y * n));
        var y1 = Math.Min(n - 1, (int)Math.Floor(bottomRight.Y * n));
        var tint = new Rgba(0.42f, 0.45f, 0.52f, 1f);

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var a = MercatorToScreen(x / (double)n, y / (double)n);
                var b = a + new float2((float)tilePixels, (float)tilePixels);

                if (_tiles.Get(z, x, y) is { } texture)
                {
                    draw.AddImage(texture, a, b, null, null, tint);
                    continue;
                }

                // The nearest loaded ancestor, cropped to this tile's part of it.
                for (var up = 1; up <= 4 && z - up >= 0; up++)
                {
                    var px = x >> up;
                    var py = y >> up;
                    if (_tiles.Get(z - up, px, py) is not { } parent)
                        continue;

                    var span = 1 << up;
                    var uv0 = new float2((x - (px << up)) / (float)span, (y - (py << up)) / (float)span);
                    draw.AddImage(parent, a, b, uv0, uv0 + new float2(1f / span, 1f / span), tint);
                    break;
                }
            }
        }
    }

    private (double X, double Y) ToMercatorAt(float2 screen)
    {
        var c = (_min + _max) * 0.5f;
        return (CenterX + (screen.X - c.X) / WorldPixels, CenterY + (screen.Y - c.Y) / WorldPixels);
    }

    public void Dispose() => _tiles.Dispose();
}
