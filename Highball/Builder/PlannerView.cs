using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The builder's Planner: the route's tiles over OpenStreetMap, picked by dragging across them, each to be built with lidar
/// heights or standard ones, at the road and rail detail chosen in the side panel. The first area picked fixes the route's origin and its tile grid; every
/// later area lands on the same grid, so the route grows outward in one piece.
public sealed class PlannerView : IDisposable
{
    private enum DragMode { Add, Remove }

    private static readonly Rgba LidarColor = Rgba.Hex(0x4D8DFF);
    private static readonly Rgba StandardColor = Rgba.Hex(0xFFC145);

    private sealed record Line(bool Rail, int Class, double[] X, double[] Y, double MinX, double MinY, double MaxX, double MaxY);

    private const double MinSelectZoom = 9.0;
    private const int MaxSelectSide = 40;
    /// An area dragged to pick track from can be far bigger, since only the tiles near track get selected.
    private const int MaxTrackSide = 200;

    private static readonly HttpClient Http = CreateClient();

    private readonly RouteInfo _route;
    private readonly Action<string> _notify;
    private RouteNetwork _network;
    private readonly RouteGenerator _generator = new();
    private readonly MapView _map = new();
    private readonly HashSet<TileId> _selected = [];
    private readonly Dictionary<(int, int), float2> _corners = [];
    private HashSet<TileId> _built;

    /// Of the tiles selected, those to build with lidar; of those built, those that were; and which a drag marks next.
    private readonly HashSet<TileId> _lidar = [];
    private readonly HashSet<TileId> _builtLidar = [];
    private bool _paintLidar = true;

    /// Asking OpenStreetMap where the track is, to mark the selection by it; and then building, for testing.
    private bool _findingTrack;
    private bool _buildAfterFinding;
    private int _roadLevel = 2;
    private int _railLevel = 4;
    private int _generatorVersion;
    private bool _wasRunning;

    private bool _dragging;
    private DragMode _dragMode;
    private TileId _dragFrom;
    private TileId _dragTo;
    /// Shift held at the press: the drag marks the rectangle between its ends; otherwise it marks the tiles it passes over.
    private bool _dragRect;
    private readonly HashSet<TileId> _painted = [];

    /// Regions mode: a drag puts the built tiles it passes over in the region picked (with Ctrl, takes them out of any),
    /// and the map shows every built tile by its region.
    private bool _regionMode;
    private Region _paintRegion = Region.Midwest;
    private RouteRegions _regions;

    private static readonly Dictionary<Region, Rgba> RegionColors = new()
    {
        [Region.West] = Rgba.Hex(0xE8705A),
        [Region.Midwest] = Rgba.Hex(0x5FBF6E),
        [Region.South] = Rgba.Hex(0xE0B341),
        [Region.Northeast] = Rgba.Hex(0x9A86F2),
    };

    /// Along Track mode: a drag marks an area, and once OpenStreetMap says where the rail runs in it, selects the tiles the
    /// track passes through and those within the padding of them, each kind with its own heights.
    private bool _trackMode;
    private int _trackPadding = 1;
    private bool _trackLidar = true;
    private bool _paddingLidar;
    private HashSet<TileId>? _trackArea;
    private (int I0, int J0, int I1, int J1)? _trackAreaBounds;
    private bool _dragTrack;

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
    public (string Status, float Progress)? BuildJob => _generator.Running ? (_generator.Status, _generator.Progress) : null;
    /// Goes up every time a build finishes, so a 3D view of the old tiles knows to open them again.
    public int BuildVersion => _generator.Version;

    public PlannerView(RouteInfo route, Action<string> notify)
    {
        _route = route;
        _notify = notify;
        _network = RouteNetwork.Load(route);
        _built = route.BuiltTiles();
        _regions = RouteRegions.Load(route);
        ReadBuiltSources(_built);
        _layouts = World.TrackLayouts.LoadMarkers(route);

        // The detail the route was last built at.
        if (_built.Count > 0)
        {
            try
            {
                var info = route.LoadTileInfo(_built.First());
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
                : projection.Forward(new GeoPoint(route.Origin.Latitude, route.Origin.Longitude));
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
            if (App.DevTools.TrackBuild)
                (_buildAfterFinding, _) = (true, FindTrack());
            else
            {
                _lidar.UnionWith(_selected);
                Build();
            }
        }
    }

    /// Which of these built tiles have lidar heights, from what each says it was built from.
    private void ReadBuiltSources(IEnumerable<TileId> tiles)
    {
        foreach (var tile in tiles)
        {
            try
            {
                if (_route.LoadTileInfo(tile).HeightSource.Contains("USGS"))
                    _builtLidar.Add(tile);
                else
                    _builtLidar.Remove(tile);
            }
            catch (Exception e)
            {
                Log.Warn($"Tile {tile}: {e.Message}");
            }
        }
    }

    /// Marks the tiles selected: lidar where track runs through them, standard elsewhere.
    private void MarkByTrack()
    {
        var track = TrackTiles();
        _lidar.Clear();
        _lidar.UnionWith(_selected.Where(track.Contains));
        _notify($"Track through {_lidar.Count} of {_selected.Count} tiles");
    }

    /// Every tile a line in use runs through.
    private HashSet<TileId> TrackTiles()
    {
        var track = new HashSet<TileId>();
        foreach (var way in _network.Ways.Values.Where(w => w.Rail && w.Class != (int)RailClass.Disused))
            for (var i = 0; i + 1 < way.Lat.Length; i++)
            {
                var (a, b) = (_route.Projection!.Forward(new GeoPoint(way.Lat[i], way.Lon[i])), _route.Projection.Forward(new GeoPoint(way.Lat[i + 1], way.Lon[i + 1])));
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(b.East - a.East, 2) + Math.Pow(b.North - a.North, 2)) / 100.0));
                for (var k = 0; k <= steps; k++)
                    track.Add(_route.Grid!.TileAt(new RoutePoint(a.East + (b.East - a.East) * k / steps, a.North + (b.North - a.North) * k / steps)));
            }
        return track;
    }

    /// Selects, within the area dragged in Along Track mode, the tiles track runs through and those within the padding of
    /// them (counting diagonals), each marked with the heights picked for it.
    private void SelectAlongTrack(HashSet<TileId> area)
    {
        var track = TrackTiles();
        track.IntersectWith(area);
        var padding = new HashSet<TileId>();
        foreach (var t in track)
            for (var dj = -_trackPadding; dj <= _trackPadding; dj++)
                for (var di = -_trackPadding; di <= _trackPadding; di++)
                    if (new TileId(t.I + di, t.J + dj) is var p && area.Contains(p) && !track.Contains(p))
                        padding.Add(p);

        Mark(track, _trackLidar);
        Mark(padding, _paddingLidar);
        _notify(track.Count == 0 ? "No track in that area" : $"Track through {track.Count} tiles, {padding.Count} padding");

        void Mark(HashSet<TileId> tiles, bool lidar)
        {
            _selected.UnionWith(tiles);
            if (lidar)
                _lidar.UnionWith(tiles);
            else
                _lidar.ExceptWith(tiles);
        }
    }

    /// Fetches where the rail runs under the selection, then marks it. False if a build is running.
    private bool FindTrack() => FindTrack([.. _selected]);

    private bool FindTrack(List<TileId> tiles)
    {
        if (_route.Grid is null || _generator.Running)
            return false;
        _findingTrack = true;
        _generator.Start(new RouteGenerator.Request
        {
            Route = _route,
            Network = _network,
            Tiles = tiles,
            RoadLevel = _roadLevel,
            RailLevel = Math.Max(_railLevel, 1),
            FindTrackOnly = true,
        });
        return true;
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
            Lidar = _lidar.ToHashSet(),
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
            Lidar = _builtLidar.ToHashSet(),
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
            var before = _built;
            _built = _route.BuiltTiles();
            ReadBuiltSources(_built.Where(t => !before.Contains(t) || _selected.Contains(t)));
        }

        if (_wasRunning && !_generator.Running)
        {
            _notify(_generator.Status);
            if (_findingTrack)
            {
                _findingTrack = false;
                _linesVersion = -1;
                var area = _trackArea;
                _trackArea = null;
                if (!_generator.Status.StartsWith("Failed") && _generator.Status != "Cancelled")
                {
                    if (area is not null)
                        SelectAlongTrack(area);
                    else
                        MarkByTrack();
                    if (_buildAfterFinding)
                        Build();
                }

                _buildAfterFinding = false;
            }
            else if (_generator.Status.StartsWith("Built"))
            {
                _selected.Clear();
                _lidar.Clear();
            }
        }

        _wasRunning = _generator.Running;
    }

    public (string Key, string Action)[] Hints =>
        _regionMode
            ? [("LMB", $"Paint {_paintRegion}"), ("SHIFT+LMB", "Paint area"), ("CTRL+LMB", "Clear region"), ("RMB", "Pan"), ("WHEEL", "Zoom"), ("TAB", "2D Editor"), ("ESC", "Exit")]
            : [_trackMode ? ("LMB", "Pick track in area") : ("LMB", "Add tiles"), ("SHIFT+LMB", "Add area"), ("CTRL+LMB", "Remove tiles"), ("RMB", "Pan"), ("WHEEL", "Zoom"), ("TAB", "2D Editor"), ("ESC", "Exit")];

    public string Attribution => MapTiles.Attribution;

    /// The Planner's lines in Frame Statistics.
    public void DebugStats()
    {
        DebugWindows.Heading("Planner");
        DebugWindows.Row("Tiles", $"{_built.Count} built, {_selected.Count} selected");
        DebugWindows.Row("Layouts", $"{_layouts.Count}");
    }

    // --- Menus and the side panel (ImGui). ---

    public void Panel()
    {
        Kit.BeginCard("Go to");
        ImGui.SetNextItemWidth(Kit.Width);
        if (ImGui.InputTextWithHint("##goto"u8, "Place, or lat, lon"u8, _search, ImGuiInputTextFlags.EnterReturnsTrue) && _search.ToString().Trim() is { Length: > 0 } text)
            RunSearch(text);
        Kit.EndCard();

        Kit.BeginCard("Detail");
        Kit.Caption("Roads");
        _roadLevel = Math.Max(0, ToolPanel.Mode("roadlevel", Detail.RoadLevels.Select(l => l.Name).ToArray(), _roadLevel, 2));
        Kit.Caption("Rails");
        _railLevel = Math.Max(0, ToolPanel.Mode("raillevel", Detail.RailLevels.Select(l => l.Name).ToArray(), _railLevel, 2));
        Kit.EndCard();

        Kit.BeginCard("Select");
        _trackMode = ToolPanel.Mode("selectmode", ["Tiles", "Along track"], _trackMode ? 1 : 0, 2) == 1;
        if (_trackMode)
        {
            var padding = _trackPadding;
            if (ToolPanel.Count("trackpadding", "Padding (tiles)", ref padding, 0, 10))
                _trackPadding = padding;
            Kit.Caption("Track tiles");
            _trackLidar = ToolPanel.Mode("tracklidar", ["Lidar", "Standard"], _trackLidar ? 0 : 1, 2) == 0;
            Kit.Caption("Padding tiles");
            _paddingLidar = ToolPanel.Mode("paddinglidar", ["Lidar", "Standard"], _paddingLidar ? 0 : 1, 2) == 0;
        }

        Kit.EndCard();

        Kit.BeginCard("Heights");
        HeightRow("Lidar", LidarColor, true, _lidar.Count, _builtLidar.Count);
        HeightRow("Standard", StandardColor, false, _selected.Count - _lidar.Count, _built.Count - _builtLidar.Count);
        if (Kit.Button("lidaralong", "Lidar Along Track", 0f, ButtonKind.Secondary, _selected.Count > 0 && !_generator.Running))
            FindTrack();
        Kit.EndCard();

        Kit.BeginCard("Regions", $"{_built.Count(t => _regions.Of(t) is null)} unset");
        _regionMode = ToolPanel.Mode("regionmode", ["Off", "Paint"], _regionMode ? 1 : 0, 2) == 1;
        foreach (var region in Enum.GetValues<Region>())
            if (ToolPanel.Item($"region{region}", null, region.ToString(), $"{_built.Count(t => _regions.Of(t) == region)} TILES", _regionMode && _paintRegion == region, swatch: RegionColors[region]))
            {
                _paintRegion = region;
                _regionMode = true;
            }

        Kit.EndCard();

        Kit.BeginCard("Build", $"{_selected.Count} selected");
        if (Kit.Button("build", "Build", 0f, ButtonKind.Primary, _selected.Count > 0 && !_generator.Running))
            Build();
        switch (Kit.Buttons("buildmore", ["Cancel", "Clear"], [_generator.Running, _selected.Count > 0]))
        {
            case 0:
                _generator.Cancel();
                break;
            case 1:
                _selected.Clear();
                _lidar.Clear();
                break;
        }

        if (Kit.Button("regenerate", $"Regenerate Built ({_built.Count})", 0f, ButtonKind.Secondary, _built.Count > 0 && !_generator.Running))
            Regenerate();
        if (_generator.Running)
            Kit.Gauge(_generator.Progress, Accent);
        if (_generator.Status.Length > 0)
            ToolPanel.Line(_generator.Status);
        Kit.EndCard();

        Key();
    }

    /// A kind of heights: picks what a drag marks, with how many tiles are selected and built that way.
    private void HeightRow(string name, Rgba color, bool lidar, int selected, int built)
    {
        if (ToolPanel.Item($"heights{name}", null, name, selected > 0 ? $"{selected} SEL" : $"{built} BUILT", _paintLidar == lidar, swatch: color))
            _paintLidar = lidar;
    }





    /// What's on the map, by class, with how much of it there is.
    private void Key()
    {
        var rows = _lengths.Where(e => e.Value >= 50.0).OrderByDescending(e => e.Key.Rail).ThenBy(e => e.Key.Class).ToList();
        if (rows.Count == 0 && _layouts.Count == 0)
            return;

        Kit.BeginCard("On the map");
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var draw = ImGui.GetWindowDrawList();
        var width = Kit.Width;

        // One row: its mark in the first 28 px, its name, and its amount at the card's right edge.
        void Row(Action<float2> mark, string name, string amount)
        {
            var at = ImGui.GetCursorScreenPos();
            mark(at + new float2(S(14f), S(9.5f)));
            Canvas.Text(draw, Medium, S(15f), at + new float2(S(40f), 0f), Style.Text, name);
            Canvas.Text(draw, Mono, S(14.5f), at + new float2(width, S(0.5f)), TextDim, amount, Align.Right);
            ImGui.Dummy(new float2(width, S(21f)));
        }

        foreach (var ((rail, cls), metres) in rows)
        {
            var (color, _) = LineStyle(rail, cls);
            Row(c => draw.AddRectFilled(c - new float2(S(14f), S(2.5f)), c + new float2(S(14f), S(2.5f)), color),
                rail ? $"Rail {RailColours.Name(cls)}" : $"Road {(RoadClass)cls}",
                imperial ? $"{metres / 1609.344:0.0} mi" : $"{metres / 1000.0:0.0} km");
        }

        var sample = new World.TrackLayouts.Marker(0, 0, World.JunctionKind.Turnout, 10, true, null);
        foreach (var (label, count, marker) in (ReadOnlySpan<(string, int, World.TrackLayouts.Marker)>)
                 [
                     ("Switch", _layouts.Count(l => l.Kind is World.JunctionKind.Turnout or World.JunctionKind.TwoAtOneNode && l.Issue is null), sample),
                     ("Crossover, ladder", _layouts.Count(l => l.Kind is World.JunctionKind.Crossover or World.JunctionKind.LadderLink && l.Issue is null),
                         sample with { Kind = World.JunctionKind.Crossover }),
                     ("Needs fixing", _layouts.Count(l => l.Issue is not null), sample with { Kind = World.JunctionKind.Unknown, Modelled = false, Issue = "" }),
                 ])
        {
            if (count == 0)
                continue;
            var shown = marker;
            Row(c => Junction(draw, c, shown), label, count.ToString(CultureInfo.InvariantCulture));
        }

        Kit.EndCard();
        Issues();
    }

    /// Every junction that needs fixing by hand, with why; clicking one goes to it.
    private void Issues()
    {
        var issues = _layouts.Where(l => l.Issue is not null).ToList();
        if (issues.Count == 0)
            return;

        Kit.BeginCard("Needs fixing", issues.Count.ToString(CultureInfo.InvariantCulture), Stop);
        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            if (ToolPanel.Item($"issue{i}", ToolIcon.Switches, World.TrackLayouts.Name(issue.Kind, issue.Number), null, false))
                _map.LookAt(new GeoPoint(issue.Lat, issue.Lon), Math.Max(_map.Zoom, 17.0));
            ToolPanel.Hint(issue.Issue!);
        }

        Kit.EndCard();
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
        if (_regionMode)
        {
            DrawRegions(draw);
            return;
        }

        foreach (var t in _built)
        {
            var color = _builtLidar.Contains(t) ? LidarColor : StandardColor;
            Quad(draw, t, color.WithAlpha(0.1f), color.WithAlpha(0.35f));
        }

        var preview = _dragging && !_dragTrack ? DragTiles() : [];
        var removing = _dragging && _dragMode == DragMode.Remove;

        foreach (var t in _selected)
        {
            if (preview.Contains(t))
                continue;
            var color = _lidar.Contains(t) ? LidarColor : StandardColor;
            Quad(draw, t, color.WithAlpha(0.32f), color.WithAlpha(0.95f));
        }

        var marking = removing ? Stop : _paintLidar ? LidarColor : StandardColor;
        foreach (var t in preview)
            Quad(draw, t, marking.WithAlpha(0.32f), marking.WithAlpha(0.95f));

        // An Along Track area, being dragged or waiting on OpenStreetMap: one box, not every tile in it.
        if (_dragging && _dragTrack)
            Area(draw, DragBounds(), Rgba.White);
        else if (_trackAreaBounds is { } waiting && _trackArea is not null)
            Area(draw, waiting, Rgba.White.WithAlpha(0.6f));
    }

    /// Every built tile in its region's colour (unset ones outlined only), with the drag in the colour it paints.
    private void DrawRegions(ImDrawListPtr draw)
    {
        var preview = _dragging ? DragTiles() : [];
        foreach (var t in _built)
        {
            if (preview.Contains(t))
            {
                var paint = _dragMode == DragMode.Remove ? Stop : RegionColors[_paintRegion];
                Quad(draw, t, paint.WithAlpha(0.45f), paint.WithAlpha(0.95f));
            }
            else if (_regions.Of(t) is { } region)
                Quad(draw, t, RegionColors[region].WithAlpha(0.3f), RegionColors[region].WithAlpha(0.7f));
            else
                Quad(draw, t, Rgba.White.WithAlpha(0.04f), Rgba.White.WithAlpha(0.45f));
        }
    }

    private void Area(ImDrawListPtr draw, (int I0, int J0, int I1, int J1) b, Rgba color)
    {
        var a = Corner(b.I0, b.J1 + 1);
        var c = Corner(b.I1 + 1, b.J1 + 1);
        var d = Corner(b.I1 + 1, b.J0);
        var e = Corner(b.I0, b.J0);
        draw.AddQuadFilled(a, c, d, e, color.WithAlpha(color.A * 0.08f));
        draw.AddQuad(a, c, d, e, color, 2f);
    }

    private HashSet<TileId> DragTiles() => _dragRect ? DragRect() : _painted;

    /// Marks every tile on the straight run from one tile to the next, so a fast stroke leaves no gaps.
    private void PaintLine(TileId from, TileId to)
    {
        int di = Math.Abs(to.I - from.I), dj = Math.Abs(to.J - from.J);
        int si = Math.Sign(to.I - from.I), sj = Math.Sign(to.J - from.J);
        int i = from.I, j = from.J;
        _painted.Add(from);
        // One axis at a time, toward whichever tile edge the line crosses next, so the run stays edge-connected.
        for (int ni = 0, nj = 0; ni < di || nj < dj;)
        {
            if ((1 + 2 * ni) * dj < (1 + 2 * nj) * di)
            {
                ni++;
                i += si;
            }
            else
            {
                nj++;
                j += sj;
            }
            _painted.Add(new TileId(i, j));
        }
    }

    /// The rectangle between the drag's ends, no longer a side than a drag of its kind may be.
    private (int I0, int J0, int I1, int J1) DragBounds()
    {
        var side = _dragTrack ? MaxTrackSide : MaxSelectSide;
        var i0 = Math.Min(_dragFrom.I, _dragTo.I);
        var j0 = Math.Min(_dragFrom.J, _dragTo.J);
        var i1 = Math.Min(Math.Max(_dragFrom.I, _dragTo.I), i0 + side - 1);
        var j1 = Math.Min(Math.Max(_dragFrom.J, _dragTo.J), j0 + side - 1);
        return (i0, j0, i1, j1);
    }

    private HashSet<TileId> DragRect()
    {
        var tiles = new HashSet<TileId>();
        var (i0, j0, i1, j1) = DragBounds();
        for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
                tiles.Add(new TileId(i, j));
        return tiles;
    }

    /// Left-drag marks the tiles the pointer passes over, or with Shift the rectangle between the drag's ends; they're added
    /// to the selection as the kind of heights picked in the panel, or with Ctrl removed, shown in red.
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

            if (_regionMode && _built.Count == 0)
            {
                _notify("Build some tiles first");
                return;
            }

            if (_route.Grid is null)
            {
                _route.SetOrigin(_map.ToGeo(mouse));
                Log.Info($"Route {_route.Name}: origin {_route.Origin.Latitude}, {_route.Origin.Longitude}");
            }

            _dragMode = ImGui.GetIO().KeyCtrl ? DragMode.Remove : DragMode.Add;
            _dragTrack = _trackMode && !_regionMode && _dragMode == DragMode.Add;
            _dragRect = ImGui.GetIO().KeyShift || _dragTrack;
            _dragFrom = _dragTo = TileUnder(mouse);
            _painted.Clear();
            _painted.Add(_dragFrom);
            _dragging = true;
        }

        if (!_dragging)
            return;

        var under = TileUnder(mouse);
        if (!_dragRect && under != _dragTo)
            PaintLine(_dragTo, under);
        _dragTo = under;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        var rect = DragTiles();
        if (_regionMode)
        {
            var tiles = rect.Where(_built.Contains).ToList();
            _regions.Set(tiles, _dragMode == DragMode.Remove ? null : _paintRegion);
            _regions.Save(_route);
        }
        else if (_dragTrack)
        {
            if (_generator.Running)
                _notify("Wait for the build to finish");
            else if (FindTrack([.. rect]))
            {
                _trackArea = rect;
                _trackAreaBounds = DragBounds();
            }
        }
        else if (_dragMode == DragMode.Remove)
        {
            _selected.ExceptWith(rect);
            _lidar.ExceptWith(rect);
        }
        else
        {
            _selected.UnionWith(rect);
            if (_paintLidar)
                _lidar.UnionWith(rect);
            else
                _lidar.ExceptWith(rect);
        }

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
        ? (RailColours.Of(cls), (RailClass)cls switch
        {
            RailClass.Main => 2.6f,
            RailClass.Branch => 2f,
            RailClass.Industrial or RailClass.Transit or RailClass.NarrowGauge => 1.6f,
            _ => 1.2f,
        })
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
