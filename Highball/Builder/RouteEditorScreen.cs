using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.Screens;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Route Builder's map: the route's tiles over OpenStreetMap, picked by dragging across them, and built at the
/// road and rail detail chosen in the panel. The first area picked fixes the route's origin and its tile grid; every
/// later area lands on the same grid, so the route grows outward in one piece.
public sealed class RouteEditorScreen : MenuScreen
{
    private enum DragMode { Replace, Add, Remove }

    private sealed record Line(bool Rail, int Class, double[] X, double[] Y, double MinX, double MinY, double MaxX, double MaxY);

    private const double MinSelectZoom = 9.0;
    private const int MaxSelectSide = 40;
    private const float PanelWidth = 472f;

    private static readonly HttpClient Http = CreateClient();

    private readonly RouteInfo _route;
    private RouteNetwork _network;
    private readonly RouteGenerator _generator = new();
    private readonly MapView _map = new();
    private readonly HashSet<TileId> _selected = [];
    private readonly Dictionary<(int, int), float2> _corners = [];
    private HashSet<TileId> _built;
    private int _roadLevel = 2;
    private int _railLevel = 4;
    private int _generatorVersion;
    private bool _wasRunning;

    private bool _dragging;
    private DragMode _dragMode;
    private TileId _dragFrom;
    private TileId _dragTo;

    private bool _searching;
    private string _search = "";
    private string _lastSearch = "";
    private volatile string? _searchResult;
    private GeoPoint? _pendingLook;

    private List<Line> _lines = [];
    private readonly Dictionary<(bool Rail, int Class), double> _lengths = [];
    private int _linesVersion = -1;
    private float2[] _scratch = new float2[256];

    public RouteEditorScreen(RouteInfo route) : base(route.Name.ToUpperInvariant())
    {
        _route = route;
        _network = RouteNetwork.Load(route);
        _built = route.BuiltTiles();

        Menu.Look = MenuLook.Rows;
        Menu.RowWidth = PanelWidth - 32f;
        Menu.ShowHeader = false;
        Menu.Add(new MenuItem { Label = "Go to", Value = () => _searching ? Tail(_search) + "_" : Tail(_lastSearch), OnSelect = () => { _searching = true; _search = ""; } })
            .Add(new MenuItem { Label = "Roads", Value = () => Detail.RoadLevels[_roadLevel].Name, OnChange = d => _roadLevel = Math.Clamp(_roadLevel + d, 0, Detail.RoadLevels.Length - 1) })
            .Add(new MenuItem { Label = "Rails", Value = () => Detail.RailLevels[_railLevel].Name, OnChange = d => _railLevel = Math.Clamp(_railLevel + d, 0, Detail.RailLevels.Length - 1), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Build", Value = () => $"{_selected.Count} TILE{(_selected.Count == 1 ? "" : "S")}", OnSelect = Build, Enabled = () => _selected.Count > 0 && !_generator.Running })
            .Add(new MenuItem { Label = "Cancel build", OnSelect = _generator.Cancel, Enabled = () => _generator.Running })
            .Add(new MenuItem { Label = "Clear selection", OnSelect = _selected.Clear, Enabled = () => _selected.Count > 0, SeparatorAfter = true })
            .Add(new MenuItem { Label = "View 3D", OnSelect = View3D, Enabled = () => _built.Count > 0 && !_generator.Running, SeparatorAfter = true })
            .Add(new MenuItem { Label = "Back", OnSelect = Back });

        if (route.Projection is { } projection)
        {
            var middle = _built.Count > 0
                ? new RoutePoint(_built.Average(t => t.I + 0.5) * route.Grid!.TileSize, _built.Average(t => t.J + 0.5) * route.Grid.TileSize)
                : new RoutePoint(0, 0);
            _map.LookAt(projection.Inverse(middle), 12.0);
        }
        else
        {
            _map.LookAt(new GeoPoint(39.5, -98.35), 4.5);
        }

        if (App.DevTools.Select is { } select)
        {
            App.DevTools.Select = null;
            var point = new GeoPoint(select.Lat, select.Lon);
            route.SetOrigin(point);
            var centre = route.Grid!.TileAt(route.Projection!.Forward(point));
            for (var j = 0; j < select.Size; j++)
                for (var i = 0; i < select.Size; i++)
                    _selected.Add(new TileId(centre.I - select.Size / 2 + i, centre.J - select.Size / 2 + j));
            _map.LookAt(point, 13.0);
            Build();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Highball-RouteBuilder", typeof(RouteEditorScreen).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return client;
    }

    private static string Tail(string text) => text.Length > 12 ? text[^12..] : text;

    protected override void Back()
    {
        _generator.Cancel();
        base.Back();
    }

    public override void Exit() => _map.Dispose();

    /// Back from the 3D view, which saves any track it edited: read the network again so a later build merges into
    /// that, not the copy from before.
    public override void Resume()
    {
        _network = RouteNetwork.Load(_route);
        _linesVersion = -1;
    }

    private void Build()
    {
        if (_route.Grid is null)
            return;

        _generator.Start(new RouteGenerator.Request
        {
            Route = _route,
            Network = _network,
            Tiles = [.. _selected],
            RoadLevel = _roadLevel,
            RailLevel = _railLevel,
        });
    }

    /// Flies over the built terrain, starting above the middle of the map if that's built, else the middle of the route.
    private void View3D()
    {
        var centre = _route.Projection!.Forward(MapView.FromMercator(_map.CenterX, _map.CenterY));
        HighballApp.Instance.Push(new RouteViewScreen(_route, _built.Contains(_route.Grid!.TileAt(centre)) ? centre : null));
    }

    // --- Going to a place: "lat, lon" directly, anything else through Nominatim. ---

    private void RunSearch(string text)
    {
        _lastSearch = text.Trim();
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
        {
            _pendingLook = new GeoPoint(lat, lon);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var json = await Http.GetStringAsync($"https://nominatim.openstreetmap.org/search?format=json&limit=1&q={Uri.EscapeDataString(text)}");
                using var document = JsonDocument.Parse(json);
                var results = document.RootElement;
                if (results.GetArrayLength() == 0)
                {
                    _searchResult = "Nothing found";
                    return;
                }

                var first = results[0];
                _pendingLook = new GeoPoint(
                    double.Parse(first.GetProperty("lat"u8).GetString()!, CultureInfo.InvariantCulture),
                    double.Parse(first.GetProperty("lon"u8).GetString()!, CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Log.Warn($"Place search: {e.Message}");
                _searchResult = "Search failed";
            }
        });
    }

    public override void Update(float dt)
    {
        if (_pendingLook is { } look)
        {
            _pendingLook = null;
            _map.LookAt(look, 13.0);
        }

        if (_searchResult is { } message)
        {
            _searchResult = null;
            Notify(message);
        }

        // A build that just finished: show what it made, and drop the selection it built.
        if (_generator.Version != _generatorVersion)
        {
            _generatorVersion = _generator.Version;
            _built = _route.BuiltTiles();
        }

        if (_wasRunning && !_generator.Running)
        {
            Notify(_generator.Status);
            if (_generator.Status.StartsWith("Built"))
                _selected.Clear();
        }

        _wasRunning = _generator.Running;

        if (!_searching)
        {
            Menu.Update();
            return;
        }

        if (Keyboard.Edit(ref _search, 64))
        {
            _searching = false;
            if (_search.Trim().Length > 0)
                RunSearch(_search);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _searching = false;
            Sfx.Back();
        }
    }

    protected override (string Key, string Action)[] HintKeys() => _searching
        ? [("ENTER", "Go"), ("ESC", "Cancel")]
        : [("LMB", "Select"), ("SHIFT", "Add"), ("CTRL", "Remove"), ("RMB", "Pan"), ("WHEEL", "Zoom"), ("ESC", "Back")];

    // --- Drawing. ---

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        var panelLeft = size.X - S(40f) - S(PanelWidth);
        var mouse = ImGui.GetIO().MousePos;
        var hovered = !Keyboard.PointerBlocked && mouse.X < panelLeft - S(24f) && mouse.X >= 0f && mouse.Y >= 0f && mouse.Y < size.Y;

        _map.Draw(draw, new float2(0f, 0f), size);
        _map.Input(hovered);
        _corners.Clear();

        if (_route.Grid is not null)
        {
            DrawGrid(draw, size);
            DrawTiles(draw);
        }

        DrawNetwork(draw, size);
        Select(hovered, mouse);

        // The panel's ground, and the wordmark's.
        Canvas.HGradient(draw, new float2(panelLeft - S(120f), 0f), new float2(panelLeft - S(24f), size.Y), Background.WithAlpha(0f), Background.WithAlpha(0.9f));
        draw.AddRectFilled(new float2(panelLeft - S(24f), 0f), size, Background.WithAlpha(0.9f));
        Canvas.VGradient(draw, new float2(0f, 0f), new float2(panelLeft - S(24f), S(110f)), Background.WithAlpha(0.85f), Background.WithAlpha(0f));
        DrawFrame(draw, size);

        DrawPanel(draw, size, panelLeft);
        Canvas.Hints(draw, new float2(S(110f), size.Y - S(40f)), HintKeys());
        Canvas.Text(draw, Regular, S(12f), new float2(panelLeft - S(36f), size.Y - S(30f)), TextDim, MapTiles.Attribution, Align.Right);
        DrawToast(draw, size);
    }

    private float2 Corner(int i, int j)
    {
        if (_corners.TryGetValue((i, j), out var p))
            return p;

        p = _map.ToScreen(_route.Projection!.Inverse(_route.Grid!.Corner(new TileId(i, j))));
        _corners[(i, j)] = p;
        return p;
    }

    private void Quad(ImDrawListPtr draw, TileId t, Rgba fill, Rgba outline)
    {
        var a = Corner(t.I, t.J + 1);
        var b = Corner(t.I + 1, t.J + 1);
        var c = Corner(t.I + 1, t.J);
        var d = Corner(t.I, t.J);
        if (fill.A > 0f)
            draw.AddQuadFilled(a, b, c, d, fill);
        if (outline.A > 0f)
            draw.AddQuad(a, b, c, d, outline, 1f);
    }

    private TileId TileUnder(float2 screen) => _route.Grid!.TileAt(_route.Projection!.Forward(_map.ToGeo(screen)));

    /// Grid lines over the visible part of the route once tiles are big enough to pick.
    private void DrawGrid(ImDrawListPtr draw, float2 size)
    {
        var a = TileUnder(new float2(0f, 0f));
        var b = TileUnder(size);
        var c = TileUnder(new float2(size.X, 0f));
        var d = TileUnder(new float2(0f, size.Y));
        var i0 = Math.Min(Math.Min(a.I, b.I), Math.Min(c.I, d.I));
        var i1 = Math.Max(Math.Max(a.I, b.I), Math.Max(c.I, d.I));
        var j0 = Math.Min(Math.Min(a.J, b.J), Math.Min(c.J, d.J));
        var j1 = Math.Max(Math.Max(a.J, b.J), Math.Max(c.J, d.J));
        if (i1 - i0 > 80 || j1 - j0 > 80)
            return;

        var line = Stroke.WithAlpha(0.12f);
        for (var i = i0; i <= i1 + 1; i++)
            draw.AddLine(Corner(i, j0), Corner(i, j1 + 1), line, 1f);
        for (var j = j0; j <= j1 + 1; j++)
            draw.AddLine(Corner(i0, j), Corner(i1 + 1, j), line, 1f);
    }

    private void DrawTiles(ImDrawListPtr draw)
    {
        foreach (var t in _built)
            Quad(draw, t, Clear.WithAlpha(0.14f), Clear.WithAlpha(0.45f));

        var preview = _dragging ? DragRect() : [];
        var removing = _dragging && _dragMode == DragMode.Remove;

        foreach (var t in _selected)
        {
            if (removing && preview.Contains(t))
                continue;
            if (_dragging && _dragMode == DragMode.Replace)
                continue;
            Quad(draw, t, Accent.WithAlpha(0.3f), Accent.WithAlpha(0.9f));
        }

        if (!removing)
            foreach (var t in preview)
                Quad(draw, t, Accent.WithAlpha(0.3f), Accent.WithAlpha(0.9f));
    }

    private HashSet<TileId> DragRect()
    {
        var tiles = new HashSet<TileId>();
        var i0 = Math.Min(_dragFrom.I, _dragTo.I);
        var j0 = Math.Min(_dragFrom.J, _dragTo.J);
        var i1 = Math.Min(Math.Max(_dragFrom.I, _dragTo.I), i0 + MaxSelectSide - 1);
        var j1 = Math.Min(Math.Max(_dragFrom.J, _dragTo.J), j0 + MaxSelectSide - 1);
        for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
                tiles.Add(new TileId(i, j));
        return tiles;
    }

    /// Left-drag picks a rectangle of tiles: alone it replaces the selection, with Shift it adds, with Ctrl it removes.
    /// The very first press on a route without an origin fixes the origin there.
    private void Select(bool hovered, float2 mouse)
    {
        if (hovered && Keyboard.Clicked())
        {
            if (_map.Zoom < MinSelectZoom)
            {
                Notify("Zoom in to pick tiles");
                return;
            }

            if (_route.Grid is null)
            {
                _route.SetOrigin(_map.ToGeo(mouse));
                Log.Info($"Route {_route.Name}: origin {_route.Origin.Latitude}, {_route.Origin.Longitude}");
            }

            var io = ImGui.GetIO();
            _dragMode = io.KeyCtrl ? DragMode.Remove : io.KeyShift ? DragMode.Add : DragMode.Replace;
            _dragFrom = _dragTo = TileUnder(mouse);
            _dragging = true;
        }

        if (!_dragging)
            return;

        _dragTo = TileUnder(mouse);
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        var rect = DragRect();
        if (_dragMode == DragMode.Replace)
            _selected.Clear();
        if (_dragMode == DragMode.Remove)
            _selected.ExceptWith(rect);
        else
            _selected.UnionWith(rect);

        _dragging = false;
        Sfx.Hover();
    }

    // --- The network over the map. ---

    private void RebuildLines()
    {
        _linesVersion = _network.Version;
        _lines = [];
        _lengths.Clear();

        foreach (var way in _network.Ways.Values)
        {
            var x = new double[way.Lat.Length];
            var y = new double[way.Lat.Length];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var length = 0.0;

            for (var i = 0; i < x.Length; i++)
            {
                (x[i], y[i]) = MapView.ToMercator(new GeoPoint(way.Lat[i], way.Lon[i]));
                minX = Math.Min(minX, x[i]);
                minY = Math.Min(minY, y[i]);
                maxX = Math.Max(maxX, x[i]);
                maxY = Math.Max(maxY, y[i]);
                if (i > 0)
                    length += Haversine(way.Lat[i - 1], way.Lon[i - 1], way.Lat[i], way.Lon[i]);
            }

            _lines.Add(new Line(way.Rail, way.Class, x, y, minX, minY, maxX, maxY));
            _lengths[(way.Rail, way.Class)] = _lengths.GetValueOrDefault((way.Rail, way.Class)) + length;
        }

        // Roads under rails, smaller under bigger.
        _lines = [.. _lines.OrderBy(l => l.Rail).ThenByDescending(l => l.Class)];
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static (Rgba Color, float Width) LineStyle(bool rail, int cls) => rail
        ? (RailClass)cls switch
        {
            RailClass.Main => (Accent, 2.6f),
            RailClass.Branch => (Accent.WithAlpha(0.85f), 2f),
            RailClass.Industrial => (Caution.WithAlpha(0.85f), 1.6f),
            RailClass.Yard => (Accent.WithAlpha(0.55f), 1.2f),
            RailClass.Transit => (Rgba.Hex(0x7FB2FF), 1.6f),
            RailClass.NarrowGauge => (Clear, 1.6f),
            _ => (TextFaint, 1.2f),
        }
        : (RoadClass)cls switch
        {
            RoadClass.Highway => (new Rgba(1f, 1f, 1f, 0.8f), 2.4f),
            RoadClass.Major => (new Rgba(1f, 1f, 1f, 0.6f), 1.8f),
            RoadClass.Minor => (new Rgba(1f, 1f, 1f, 0.45f), 1.4f),
            RoadClass.Local => (new Rgba(1f, 1f, 1f, 0.3f), 1f),
            _ => (new Rgba(1f, 1f, 1f, 0.2f), 1f),
        };

    /// Smaller classes appear as the map zooms in, as on any road map.
    private bool Shown(Line line) => line.Rail
        ? (RailClass)line.Class != RailClass.Yard || _map.Zoom >= 11.0
        : (RoadClass)line.Class switch
        {
            RoadClass.Highway or RoadClass.Major => true,
            RoadClass.Minor => _map.Zoom >= 10.0,
            _ => _map.Zoom >= 12.0,
        };

    private void DrawNetwork(ImDrawListPtr draw, float2 size)
    {
        if (_network.Version != _linesVersion)
            RebuildLines();

        var a = _map.ToGeo(new float2(0f, 0f));
        var b = _map.ToGeo(size);
        var (minX, minY) = MapView.ToMercator(a);
        var (maxX, maxY) = MapView.ToMercator(b);
        var pixel = _map.MercatorPerPixel * 1.5;

        foreach (var line in _lines)
        {
            if (line.MaxX < minX || line.MinX > maxX || line.MaxY < minY || line.MinY > maxY || !Shown(line))
                continue;

            if (_scratch.Length < line.X.Length)
                _scratch = new float2[line.X.Length * 2];

            // Points closer than a pixel and a half to the last one kept add nothing at this zoom.
            var count = 0;
            double lastX = 0, lastY = 0;
            for (var i = 0; i < line.X.Length; i++)
            {
                if (count > 0 && i < line.X.Length - 1 && Math.Abs(line.X[i] - lastX) < pixel && Math.Abs(line.Y[i] - lastY) < pixel)
                    continue;
                _scratch[count++] = _map.MercatorToScreen(line.X[i], line.Y[i]);
                (lastX, lastY) = (line.X[i], line.Y[i]);
            }

            var (color, width) = LineStyle(line.Rail, line.Class);
            draw.AddPolyline(_scratch.AsSpan(0, count), color, ImDrawFlags.None, S(width));
        }
    }

    // --- The panel. ---

    private void DrawPanel(ImDrawListPtr draw, float2 size, float left)
    {
        var top = S(44f);
        Canvas.Text(draw, Heavy, S(28f), new float2(left, top), Style.Text, _route.Name.ToUpperInvariant());

        var origin = _route.Grid is null
            ? "ORIGIN NOT SET"
            : string.Create(CultureInfo.InvariantCulture, $"ORIGIN {_route.Origin.Latitude:0.00000}, {_route.Origin.Longitude:0.00000}");
        Canvas.Text(draw, Mono, S(13f), new float2(left, top + S(42f)), TextDim, origin);
        Canvas.Text(draw, Mono, S(13f), new float2(left, top + S(62f)), TextDim, $"BUILT {_built.Count} TILE{(_built.Count == 1 ? "" : "S")}");

        var bottom = Menu.Draw(draw, left + S(16f), top + S(110f));
        var y = bottom + S(30f);

        if (_generator.Running)
        {
            Canvas.SegmentBar(draw, new float2(left, y), new float2(left + S(PanelWidth), y + S(10f)), _generator.Progress, Accent, 32);
            y += S(22f);
        }

        if (_generator.Status.Length > 0)
        {
            foreach (var line in Canvas.Wrap(_generator.Status, Mono, S(13f), S(PanelWidth)))
            {
                Canvas.Text(draw, Mono, S(13f), new float2(left, y), _generator.Running ? Style.Text : TextDim, line);
                y += S(19f);
            }

            y += S(14f);
        }

        DrawKey(draw, left, y);
    }

    /// What's on the map, by class, with how much of it there is.
    private void DrawKey(ImDrawListPtr draw, float left, float y)
    {
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var rows = _lengths.Where(e => e.Value >= 50.0).OrderByDescending(e => e.Key.Rail).ThenBy(e => e.Key.Class).ToList();

        foreach (var ((rail, cls), metres) in rows)
        {
            var (color, width) = LineStyle(rail, cls);
            draw.AddLine(new float2(left, y + S(9f)), new float2(left + S(28f), y + S(9f)), color, S(width));
            var name = rail ? ((RailClass)cls).ToString() : ((RoadClass)cls).ToString();
            Canvas.Spaced(draw, Medium, S(12f), new float2(left + S(42f), y + S(2f)), TextDim, $"{(rail ? "RAIL" : "ROAD")} {name.ToUpperInvariant()}", 0.08f);
            var amount = imperial ? $"{metres / 1609.344:0.0} MI" : $"{metres / 1000.0:0.0} KM";
            Canvas.Text(draw, Mono, S(13f), new float2(left + S(PanelWidth), y + S(1f)), Style.Text, amount, Align.Right);
            y += S(22f);
        }
    }
}
