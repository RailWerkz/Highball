using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Brutal.ImGuiApi;
using Highball.Routes;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The builder's Planner: the route's tiles over OpenStreetMap, picked by dragging across them, and built at the road
/// and rail detail chosen in the side panel. The first area picked fixes the route's origin and its tile grid; every
/// later area lands on the same grid, so the route grows outward in one piece.
public sealed class PlannerView : IDisposable
{
    private enum DragMode { Replace, Add, Remove }

    private sealed record Line(bool Rail, int Class, double[] X, double[] Y, double MinX, double MinY, double MaxX, double MaxY);

    private const double MinSelectZoom = 9.0;
    private const int MaxSelectSide = 40;

    private static readonly HttpClient Http = CreateClient();

    private readonly RouteInfo _route;
    private readonly Action<string> _notify;
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

    private readonly ImInputString _search = new(128, ""u8);
    private volatile string? _searchResult;
    private GeoPoint? _pendingLook;

    private List<Line> _lines = [];
    private List<World.TrackLayouts.Marker> _layouts = [];
    private readonly Dictionary<(bool Rail, int Class), double> _lengths = [];
    private int _linesVersion = -1;
    private float2[] _scratch = new float2[256];

    public HashSet<TileId> Built => _built;
    public bool Building => _generator.Running;
    /// Goes up every time a build finishes, so a 3D view of the old tiles knows to open them again.
    public int BuildVersion => _generator.Version;

    public PlannerView(RouteInfo route, Action<string> notify)
    {
        _route = route;
        _notify = notify;
        _network = RouteNetwork.Load(route);
        _built = route.BuiltTiles();
        _layouts = World.TrackLayouts.LoadMarkers(route);

        // The detail the route was last built at.
        if (_built.Count > 0)
        {
            try
            {
                var info = RouteTile.LoadInfo(route.TileFolder(_built.First()));
                _roadLevel = Math.Max(0, Array.FindIndex(Detail.RoadLevels, l => l.Name == info.Roads));
                _railLevel = Math.Max(0, Array.FindIndex(Detail.RailLevels, l => l.Name == info.Rails));
            }
            catch (Exception e)
            {
                Log.Warn($"Route {route.Name}: {e.Message}");
            }
        }

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

        if (App.DevTools.Look is { } look)
        {
            App.DevTools.Look = null;
            _map.LookAt(new GeoPoint(look.Lat, look.Lon), look.Zoom);
        }

        if (App.DevTools.Regenerate)
        {
            App.DevTools.Regenerate = false;
            Regenerate();
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
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Highball-RouteBuilder", typeof(PlannerView).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return client;
    }

    public void Dispose()
    {
        _generator.Cancel();
        _map.Dispose();
    }

    /// The 3D Editor saved the track it edited: read the network again so a later build merges into that, not the
    /// copy from before.
    public void ReloadNetwork()
    {
        _network = RouteNetwork.Load(_route);
        _linesVersion = -1;
    }

    /// The junctions as the Editor last laid them (it writes them whenever it lays the track).
    public void ReloadTurnouts() => _layouts = World.TrackLayouts.LoadMarkers(_route);

    /// The middle of the map in route metres, if it's over a built tile.
    public RoutePoint? Centre()
    {
        if (_route.Projection is null)
            return null;
        var centre = _route.Projection.Forward(MapView.FromMercator(_map.CenterX, _map.CenterY));
        return _built.Contains(_route.Grid!.TileAt(centre)) ? centre : null;
    }

    /// Centres the map on a point in route metres, keeping the zoom.
    public void LookAt(RoutePoint point) => _map.LookAt(_route.Projection!.Inverse(point), _map.Zoom);

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

    /// Every built tile again at the detail now chosen, the roads and rail fetched afresh in place of the old (track
    /// edited by hand stays).
    private void Regenerate()
    {
        if (_route.Grid is null || _built.Count == 0)
            return;

        _generator.Start(new RouteGenerator.Request
        {
            Route = _route,
            Network = _network,
            Tiles = [.. _built],
            RoadLevel = _roadLevel,
            RailLevel = _railLevel,
            ReplaceNetwork = true,
        });
    }

    // --- Going to a place: "lat, lon" directly, anything else through Nominatim. ---

    private void RunSearch(string text)
    {
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

    /// Every frame, shown or not, so a build finishing while the 3D Editor is up still lands.
    public void Update()
    {
        if (_pendingLook is { } look)
        {
            _pendingLook = null;
            _map.LookAt(look, 13.0);
        }

        if (_searchResult is { } message)
        {
            _searchResult = null;
            _notify(message);
        }

        // A build that just finished: show what it made, and drop the selection it built.
        if (_generator.Version != _generatorVersion)
        {
            _generatorVersion = _generator.Version;
            _built = _route.BuiltTiles();
        }

        if (_wasRunning && !_generator.Running)
        {
            _notify(_generator.Status);
            if (_generator.Status.StartsWith("Built"))
                _selected.Clear();
        }

        _wasRunning = _generator.Running;
    }

    public (string Key, string Action)[] Hints =>
        [("LMB", "Select"), ("SHIFT", "Add"), ("CTRL", "Remove"), ("RMB", "Pan"), ("WHEEL", "Zoom"), ("TAB", "2D Editor"), ("ESC", "Exit")];

    public string Attribution => MapTiles.Attribution;

    // --- Menus and the side panel (ImGui). ---

    public void MenuEdit()
    {
        if (ImGui.MenuItem("Clear Selection", "", false, _selected.Count > 0))
            _selected.Clear();
    }

    public void Panel()
    {
        ImGui.SeparatorText("Go To"u8);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##goto"u8, "Place, or lat, lon"u8, _search, ImGuiInputTextFlags.EnterReturnsTrue) && _search.ToString().Trim() is { Length: > 0 } text)
            RunSearch(text);

        ImGui.SeparatorText("Detail"u8);
        Level("Roads"u8, ref _roadLevel, Detail.RoadLevels.Select(l => l.Name).ToArray());
        Level("Rails"u8, ref _railLevel, Detail.RailLevels.Select(l => l.Name).ToArray());

        ImGui.SeparatorText("Build"u8);
        ImGui.Text($"{_selected.Count} tile{(_selected.Count == 1 ? "" : "s")} selected");
        ImGui.BeginDisabled(_selected.Count == 0 || _generator.Running);
        if (ImGui.Button("Build"u8))
            Build();
        ImGui.EndDisabled();
        ImGui.SameLine(0f, -1f);
        ImGui.BeginDisabled(!_generator.Running);
        if (ImGui.Button("Cancel"u8))
            _generator.Cancel();
        ImGui.EndDisabled();
        ImGui.SameLine(0f, -1f);
        ImGui.BeginDisabled(_selected.Count == 0);
        if (ImGui.Button("Clear Selection"u8))
            _selected.Clear();
        ImGui.EndDisabled();
        ImGui.BeginDisabled(_built.Count == 0 || _generator.Running);
        if (ImGui.Button($"Regenerate {_built.Count} Built Tile{(_built.Count == 1 ? "" : "s")}"))
            Regenerate();
        ImGui.EndDisabled();

        if (_generator.Running)
            ImGui.ProgressBar(_generator.Progress, new float2(-1f, 0f), ImString.Null);
        if (_generator.Status.Length > 0)
            ImGui.TextWrapped(_generator.Status);

        Key();
    }

    private static void Level(ImString label, ref int level, string[] names)
    {
        ImGui.SetNextItemWidth(-80f);
        if (ImGui.BeginCombo(label, names[level], ImGuiComboFlags.None))
        {
            for (var i = 0; i < names.Length; i++)
                if (ImGui.Selectable(names[i], i == level, ImGuiSelectableFlags.None, null))
                    level = i;
            ImGui.EndCombo();
        }
    }

    /// What's on the map, by class, with how much of it there is.
    private void Key()
    {
        var rows = _lengths.Where(e => e.Value >= 50.0).OrderByDescending(e => e.Key.Rail).ThenBy(e => e.Key.Class).ToList();
        if (rows.Count == 0 && _layouts.Count == 0)
            return;

        ImGui.SeparatorText("On the Map"u8);
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        if (!ImGui.BeginTable("key"u8, 3, ImGuiTableFlags.None))
            return;

        ImGui.TableSetupColumn("##swatch"u8, ImGuiTableColumnFlags.WidthFixed, 34f, default);
        ImGui.TableSetupColumn("##class"u8, ImGuiTableColumnFlags.WidthStretch, 0f, default);
        ImGui.TableSetupColumn("##length"u8, ImGuiTableColumnFlags.WidthFixed, 0f, default);

        foreach (var ((rail, cls), metres) in rows)
        {
            var (color, _) = LineStyle(rail, cls);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var swatch = new float4(color.R, color.G, color.B, 1f);
            ImGui.ColorButton($"##{rail}{cls}", swatch, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder, new float2(28f, 6f));
            ImGui.TableNextColumn();
            ImGui.Text($"{(rail ? "Rail" : "Road")} {(rail ? ((RailClass)cls).ToString() : ((RoadClass)cls).ToString())}");
            ImGui.TableNextColumn();
            ImGui.Text(imperial ? $"{metres / 1609.344:0.0} mi" : $"{metres / 1000.0:0.0} km");
        }

        var sample = new World.TrackLayouts.Marker(0, 0, World.JunctionKind.Turnout, 10, true, null);
        foreach (var (label, count, marker) in (ReadOnlySpan<(string, int, World.TrackLayouts.Marker)>)
                 [
                     ("Switch", _layouts.Count(l => l.Kind is World.JunctionKind.Turnout or World.JunctionKind.TwoAtOneNode && l.Issue is null), sample),
                     ("Crossover or ladder link", _layouts.Count(l => l.Kind is World.JunctionKind.Crossover or World.JunctionKind.LadderLink && l.Issue is null),
                         sample with { Kind = World.JunctionKind.Crossover }),
                     ("Needs fixing", _layouts.Count(l => l.Issue is not null), sample with { Kind = World.JunctionKind.Unknown, Modelled = false, Issue = "" }),
                 ])
        {
            if (count == 0)
                continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var at = ImGui.GetCursorScreenPos() + new float2(S(14f), ImGui.GetTextLineHeight() * 0.5f);
            Junction(ImGui.GetWindowDrawList(), at, marker);
            ImGui.Dummy(new float2(S(28f), ImGui.GetTextLineHeight()));
            ImGui.TableNextColumn();
            ImGui.Text(label);
            ImGui.TableNextColumn();
            ImGui.Text($"{count}");
        }

        ImGui.EndTable();
        Issues();
    }

    /// Every junction that needs fixing by hand, with why; clicking one goes to it.
    private void Issues()
    {
        var issues = _layouts.Where(l => l.Issue is not null).ToList();
        if (issues.Count == 0)
            return;

        ImGui.SeparatorText($"Needs Fixing ({issues.Count})");
        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            if (ImGui.Selectable($"{World.TrackLayouts.Name(issue.Kind, issue.Number)}##issue{i}", false, ImGuiSelectableFlags.None, default))
                _map.LookAt(new GeoPoint(issue.Lat, issue.Lon), Math.Max(_map.Zoom, 17.0));
            ImGui.Indent();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(issue.Issue!);
            ImGui.PopTextWrapPos();
            ImGui.Unindent();
        }
    }

    // --- Drawing. ---

    /// The map over the whole window; area is the part of it not under the menu bar or footer, where the pointer is
    /// the map's.
    public void Draw(ImDrawListPtr draw, float2 size, float top, float bottom)
    {
        var mouse = ImGui.GetIO().MousePos;
        var hovered = !Keyboard.PointerBlocked && mouse.X >= 0f && mouse.X < size.X && mouse.Y >= top && mouse.Y < bottom;

        _map.Draw(draw, new float2(0f, 0f), size);
        _map.Input(hovered);
        _corners.Clear();

        if (_route.Grid is not null)
        {
            DrawGrid(draw, size);
            DrawTiles(draw);
        }

        DrawNetwork(draw, size);
        DrawTurnouts(draw, size, hovered, mouse);
        Select(hovered, mouse);
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
                _notify("Zoom in to pick tiles");
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

    /// A junction's marker: a diamond for a switch, a square for a crossover, a circle for anything else; filled orange
    /// where it's drawn with models, red where it needs fixing.
    private static void Junction(ImDrawListPtr draw, float2 at, World.TrackLayouts.Marker marker)
    {
        var fill = marker.Issue is not null ? Stop : marker.Modelled ? Caution : default;
        var edge = fill.A > 0f ? new Rgba(0f, 0f, 0f, 0.8f) : Clear;
        var r = S(5f);
        switch (marker.Kind)
        {
            case World.JunctionKind.Turnout or World.JunctionKind.TwoAtOneNode:
            {
                var (a, b, c, d) = (at + new float2(0f, -r), at + new float2(r, 0f), at + new float2(0f, r), at + new float2(-r, 0f));
                if (fill.A > 0f)
                    draw.AddQuadFilled(a, b, c, d, fill);
                draw.AddQuad(a, b, c, d, edge, S(1.2f));
                break;
            }
            case World.JunctionKind.Crossover or World.JunctionKind.LadderLink:
                r *= 0.85f;
                if (fill.A > 0f)
                    draw.AddRectFilled(at - new float2(r, r), at + new float2(r, r), fill);
                draw.AddRect(at - new float2(r, r), at + new float2(r, r), edge, 0f, ImDrawFlags.None, S(1.2f));
                break;
            default:
                if (fill.A > 0f)
                    draw.AddCircleFilled(at, r, fill, 16);
                draw.AddCircle(at, r, edge, 16, S(1.2f));
                break;
        }
    }

    /// The junctions, once zoomed in far enough to tell them apart; hovering one says what it is and what's wrong.
    private void DrawTurnouts(ImDrawListPtr draw, float2 size, bool hovered, float2 mouse)
    {
        if (_layouts.Count == 0 || _map.Zoom < 13.0)
            return;

        World.TrackLayouts.Marker? under = null;
        var nearest = S(8f) * S(8f);
        foreach (var marker in _layouts.OrderBy(l => l.Issue is not null))
        {
            var at = _map.ToScreen(new GeoPoint(marker.Lat, marker.Lon));
            if (at.X < -10f || at.Y < -10f || at.X > size.X + 10f || at.Y > size.Y + 10f)
                continue;
            Junction(draw, at, marker);
            var d = (at - mouse).LengthSquared();
            if (hovered && d < nearest)
                (nearest, under) = (d, marker);
        }

        if (under is { } hover)
            ImGui.SetTooltip(World.TrackLayouts.Name(hover.Kind, hover.Number) + (hover.Modelled ? ", modelled" : "") + (hover.Issue is { } why ? $"\n{why}" : ""));
    }

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
}
