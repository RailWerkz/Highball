using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The route in 3D: its built terrain, flown through freely. Hold the right mouse button to look, WASD to move, Q and E
/// to sink and climb; the wheel sets the speed.
///
/// The number keys pick a tool for the left mouse button: sculpting the ground (raise, lower, smooth, flatten to where
/// the stroke began), painting its land cover, editing track (drag a control point; Delete takes it out) or laying
/// track (click points, starting on a track to branch off it; Enter lays it, Backspace takes the last point back, Esc
/// drops it). [ and ] size the brush, P picks the cover to paint, Ctrl+Z and Ctrl+Y undo and redo, Ctrl+S saves
/// (leaving saves too).
public sealed class RouteViewScreen : Screen, IWorldScreen
{
    private enum Tool { Look, Raise, Lower, Smooth, Flatten, Paint, Track, Lay }

    private static readonly (byte Class, string Name)[] PaintCovers =
    [
        (30, "GRASS"), (10, "FOREST"), (40, "CROPS"), (20, "SHRUB"), (50, "BUILT-UP"), (60, "BARE"), (80, "WATER"),
    ];

    private const float RaiseRate = 3f;   // metres a second at the brush's centre
    private const float BlendRate = 4f;   // smoothing and flattening: how much of the way a second
    private const float MinRadius = 2f;
    private const float MaxRadius = 400f;

    private const float FeetPerMeter = 3.28084f;
    private const float MinSpeed = 2f;
    private const float MaxSpeed = 2000f;

    private readonly RouteInfo _route;
    private readonly Task<TerrainSource> _opening;
    private readonly WorldRenderer _renderer = new();
    private readonly Camera _camera = new();
    private readonly RoutePoint? _start;
    private Terrain? _terrain;
    private NetworkMeshes? _network;
    private float _speed = 60f;
    private float _fps;
    private bool _looking;

    private Tool _tool;
    private float _radius = 20f;
    private int _paint;
    private TerrainStroke? _stroke;
    private float _flattenTo;
    private double3? _hit;
    private float2 _screen = new(1f, 1f);
    // Terrain strokes and network edits, in the order they were made.
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();

    private TrackEditor? _tracks;
    private long? _hover;
    private long? _dragging;
    private (double X, double Z)? _dragTo;
    private readonly List<LayPoint> _laying = [];
    private LayPoint? _layNext;
    private Task<NetworkChange>? _rebuild;
    private bool _rebuildAgain;

    // The menu bar along the top and the controls footer along the bottom: the pointer over either isn't the world's.
    private float _headerBottom;
    private float _footerTop = float.MaxValue;
    private bool _backRequested;

    // The Stats and Profiler windows (View, F4 and F5), left as they were each time the view opens.
    private static bool _showStats;
    private static bool _showProfiler;

    private bool Blocked => Keyboard.PointerBlocked || Input.MousePosition.Y < _headerBottom || Input.MousePosition.Y > _footerTop;

    private static readonly (Tool Tool, string Name, string Key)[] Tools =
    [
        (Tool.Look, "Look", "1"), (Tool.Raise, "Raise", "2"), (Tool.Lower, "Lower", "3"), (Tool.Smooth, "Smooth", "4"),
        (Tool.Flatten, "Flatten", "5"), (Tool.Paint, "Paint Cover", "6"), (Tool.Track, "Edit Track", "7"), (Tool.Lay, "Lay Track", "8"),
    ];

    public override WorldView View => WorldView.World;
    public override bool CapturesMouse => _looking;

    /// start: where to put the camera (route metres east and north of the origin); the middle of the built tiles if null.
    public RouteViewScreen(RouteInfo route, RoutePoint? start = null)
    {
        _route = route;
        _start = start;
        _opening = TerrainSource.Open(route);
        if (DevTools.Panels)
            _showStats = _showProfiler = true;
        if (DevTools.Skybox is { } sky)
        {
            DevTools.Skybox = null;
            _renderer.Sky.Use(sky);
        }
    }

    public override void Exit()
    {
        if (_opening.IsCompletedSuccessfully)
            Save(_opening.Result);
        _terrain?.Dispose();
        _network?.Dispose();
        _renderer.Dispose();
    }

    public override void Update(float dt)
    {
        _fps = dt > 0f ? _fps + (1f / dt - _fps) * MathF.Min(1f, dt * 4f) : _fps;

        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && _laying.Count > 0)
            _laying.Clear();
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape) || _backRequested)
        {
            Sfx.Back();
            HighballApp.Instance.Pop();
            return;
        }

        if (_terrain is null)
        {
            if (!_opening.IsCompleted)
                return;

            if (_opening.IsFaulted)
            {
                Log.Error($"Route view: {_opening.Exception?.InnerException?.Message}");
                HighballApp.Instance.Pop();
                return;
            }

            _terrain = new Terrain(_opening.Result);
            _network = new NetworkMeshes(_opening.Result);
            _tracks = new TrackEditor(_route, _opening.Result.Network);
            PlaceCamera(_opening.Result);
        }

        Fly(dt);
        using (Profiler.Sample("Editing"))
        {
            Edit(dt);
            EditTrack();
            PollRebuild();
        }

        using (Profiler.Sample("Terrain LOD"))
            _terrain.Update(_camera.X, _camera.Y, _camera.Z);
        using (Profiler.Sample("Line chunks"))
            _network!.Update(_camera.X, _camera.Y, _camera.Z);

        if (ImGui.IsKeyPressed(ImGuiKey.F))
            _renderer.Wireframe = !_renderer.Wireframe;
        if (ImGui.IsKeyPressed(ImGuiKey.F4))
            _showStats = !_showStats;
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
            _showProfiler = !_showProfiler;
    }

    private void PlaceCamera(TerrainSource source)
    {
        double x, z;
        if (_start is { } start)
        {
            x = start.East;
            z = -start.North;
        }
        else
        {
            x = (source.MinGx + source.MaxGx) * 0.5 * source.Spacing;
            z = (source.MinGz + source.MaxGz) * 0.5 * source.Spacing;
        }

        _camera.X = x;
        _camera.Z = z;
        _camera.Y = source.HeightAt(x, z) + 150.0;
        _camera.Yaw = 0f;
        _camera.Pitch = -0.2f;

        if (DevTools.Camera is { } dev)
        {
            DevTools.Camera = null;
            var p = _route.Projection!.Forward(new GeoPoint(dev.Lat, dev.Lon));
            _camera.X = p.East;
            _camera.Z = -p.North;
            _camera.Y = source.HeightAt(_camera.X, _camera.Z) + dev.Height;
            _camera.Yaw = dev.Yaw * MathF.PI / 180f;
            _camera.Pitch = dev.Pitch * MathF.PI / 180f;
        }
    }

    private void Fly(float dt)
    {
        _looking = Input.Mouse(GlfwMouseButton.Right) && !Blocked;
        if (_looking)
        {
            const float sensitivity = 0.0025f;
            _camera.Yaw += Input.MouseDelta.X * sensitivity;
            _camera.Pitch = Math.Clamp(_camera.Pitch - Input.MouseDelta.Y * sensitivity, -1.55f, 1.55f);
        }

        if (Input.Scroll != 0f && !Blocked)
            _speed = Math.Clamp(_speed * MathF.Pow(1.25f, Input.Scroll), MinSpeed, MaxSpeed);

        var forward = _camera.Forward;
        var right = _camera.Right;
        var move = default(float3);
        if (Input.Key(GlfwKey.W)) move += forward;
        if (Input.Key(GlfwKey.S)) move -= forward;
        if (Input.Key(GlfwKey.D)) move += right;
        if (Input.Key(GlfwKey.A)) move -= right;
        if (Input.Key(GlfwKey.E)) move.Y += 1f;
        if (Input.Key(GlfwKey.Q)) move.Y -= 1f;

        var length = MathF.Sqrt(move.X * move.X + move.Y * move.Y + move.Z * move.Z);
        if (length > 0f)
        {
            var speed = _speed * (Input.Key(GlfwKey.LeftShift) ? 4f : 1f) * dt / length;
            _camera.X += move.X * speed;
            _camera.Y += move.Y * speed;
            _camera.Z += move.Z * speed;
        }

        var ground = _terrain!.Source.HeightAt(_camera.X, _camera.Z);
        _camera.Y = Math.Max(_camera.Y, ground + 1.8);
    }

    // --- Editing. ---

    private void Edit(float dt)
    {
        var source = _terrain!.Source;
        var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);

        for (var i = 0; i <= (int)Tool.Lay; i++)
            if (Input.KeyDown(GlfwKey.Number1 + i))
                _tool = (Tool)i;
        if (Input.KeyDown(GlfwKey.LeftBracket))
            _radius = MathF.Max(MinRadius, _radius / 1.25f);
        if (Input.KeyDown(GlfwKey.RightBracket))
            _radius = MathF.Min(MaxRadius, _radius * 1.25f);
        if (Input.KeyDown(GlfwKey.P))
            _paint = (_paint + 1) % PaintCovers.Length;

        if (ctrl && Input.KeyDown(GlfwKey.Z) && _undo.TryPop(out var undone))
        {
            Redo(undone, undo: true);
            _redo.Push(undone);
        }

        if (ctrl && Input.KeyDown(GlfwKey.Y) && _redo.TryPop(out var redone))
        {
            Redo(redone, undo: false);
            _undo.Push(redone);
        }

        if (ctrl && Input.KeyDown(GlfwKey.S))
            Save(source);

        _hit = _tool == Tool.Look || Blocked ? null : Pick(Input.MousePosition);
        if (_tool is Tool.Track or Tool.Lay)
            return;

        if (DevTools.Sculpt is { } dev)
        {
            // A scripted stroke at the middle of the screen, for testing; tried again each frame until the ground there
            // can be edited (its full heights have loaded).
            if (Pick(_screen * 0.5f) is { } centre && Enum.TryParse<Tool>(dev.Tool, true, out var tool))
            {
                _tool = tool;
                _hit = centre;
                Begin(source);
                for (var i = 0; i < dev.Steps; i++)
                    Apply(source, 1f / 30f);
                if (!_stroke!.IsEmpty)
                    DevTools.Sculpt = null;
                End();
            }
        }

        var down = Input.Mouse(GlfwMouseButton.Left) && _tool != Tool.Look && !Blocked;
        if (down && _stroke is null && _hit is not null)
            Begin(source);
        if (down && _stroke is not null && _hit is not null)
            Apply(source, dt);
        if (!down && _stroke is not null)
            End();
    }

    private void Redo(object edit, bool undo)
    {
        switch (edit)
        {
            case TerrainStroke stroke:
                Rebuild(_terrain!.Source.Apply(stroke, undo));
                break;
            case NetworkEdit network:
                _tracks!.Apply(network, undo);
                RebuildNetwork();
                break;
        }
    }

    private void Save(TerrainSource source)
    {
        if (source.Unsaved)
            source.SaveEdits();
        if (source.Network.Unsaved)
            source.Network.Save(_route);
    }

    // --- Track. ---

    private float2? ProjectGround(double x, double z)
    {
        var source = _terrain!.Source;
        var relative = new float3((float)(x - _camera.X), (float)(source.HeightAt(x, z) + 0.6 - _camera.Y), (float)(z - _camera.Z));
        var clip = MathEx.Project(relative, _camera.ViewProjection(_screen.X / _screen.Y));
        if (clip.W <= 0.01f)
            return null;
        return new float2((clip.X / clip.W * 0.5f + 0.5f) * _screen.X, (0.5f - clip.Y / clip.W * 0.5f) * _screen.Y);
    }

    private void EditTrack()
    {
        var tracks = _tracks!;
        var mouse = Input.MousePosition;

        if (DevTools.Lay is { } dev)
        {
            // Scripted track for testing: from the first point (branching off any track within 20 m of it) to the second.
            DevTools.Lay = null;
            var a = _route.Projection!.Forward(new GeoPoint(dev.Lat0, dev.Lon0));
            var b = _route.Projection!.Forward(new GeoPoint(dev.Lat1, dev.Lon1));
            var start = tracks.SegmentNear(a.East, -a.North, 20.0) is { } s ? new LayPoint(s.X, s.Z, Split: (s.Way, s.Segment)) : new LayPoint(a.East, -a.North);
            var mid = new LayPoint((start.X + b.East) * 0.5, (start.Z - b.North) * 0.5);
            Committed(tracks.Lay([start, mid, new LayPoint(b.East, -b.North)]));
        }

        if (_tool is not (Tool.Track or Tool.Lay) || Blocked)
        {
            _hover = null;
            return;
        }

        if (_dragging is null)
            _hover = tracks.NodeAt(mouse, _camera.X, _camera.Z, ProjectGround);

        if (_tool == Tool.Track)
        {
            if (Input.MouseDown(GlfwMouseButton.Left) && _hover is { } grabbed)
                _dragging = grabbed;
            if (_dragging is not null && _hit is { } hit)
                _dragTo = (hit.X, hit.Z);
            if (_dragging is { } node && !Input.Mouse(GlfwMouseButton.Left))
            {
                if (_dragTo is { } to)
                    Committed(tracks.Move(node, to.X, to.Z));
                _dragging = null;
                _dragTo = null;
            }

            if (Input.KeyDown(GlfwKey.Delete) && _hover is { } doomed)
                Committed(tracks.Delete(doomed));
            return;
        }

        // Laying: the point the next click would add.
        _layNext = _hover is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : tracks.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround) is { } segment
                ? new LayPoint(segment.X, segment.Z, Split: (segment.Way, segment.Segment))
                : _hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;

        if (Input.MouseDown(GlfwMouseButton.Left) && _layNext is { } next)
            _laying.Add(next);
        if (Input.KeyDown(GlfwKey.Backspace) && _laying.Count > 0)
            _laying.RemoveAt(_laying.Count - 1);
        if (Input.KeyDown(GlfwKey.Enter) && _laying.Count >= 2)
        {
            Committed(tracks.Lay(_laying));
            _laying.Clear();
        }
    }

    private void Committed(NetworkEdit? edit)
    {
        if (edit is null)
            return;
        _undo.Push(edit);
        _redo.Clear();
        RebuildNetwork();
    }

    /// Works out what a network edit changed in the background; edits made meanwhile wait for the next pass.
    private void RebuildNetwork()
    {
        if (_rebuild is not null)
        {
            _rebuildAgain = true;
            return;
        }

        _rebuild = _terrain!.Source.RebuildNetwork();
    }

    private void PollRebuild()
    {
        if (_rebuild is not { IsCompleted: true } done)
            return;

        _rebuild = null;
        if (done.IsCompletedSuccessfully)
        {
            var change = done.Result;
            foreach (var (x0, z0, x1, z1) in _terrain!.Source.Apply(change))
                _terrain.Invalidate(x0, z0, x1, z1);
            _network!.Replace(change.Lines, change.Areas);
        }
        else
            Log.Error($"Track rebuild: {done.Exception?.InnerException?.Message}");

        if (_rebuildAgain)
        {
            _rebuildAgain = false;
            RebuildNetwork();
        }
    }

    // --- Sculpting and painting. ---

    private void Begin(TerrainSource source)
    {
        _stroke = new TerrainStroke();
        _flattenTo = source.HeightAt(_hit!.Value.X, _hit.Value.Z);
    }

    private void Apply(TerrainSource source, float dt)
    {
        var hit = _hit!.Value;
        var fast = Input.Key(GlfwKey.LeftShift) ? 4f : 1f;
        var changed = _tool switch
        {
            Tool.Raise => source.Sculpt(hit.X, hit.Z, _radius, SculptMode.Raise, RaiseRate * fast * dt, 0f, _stroke!),
            Tool.Lower => source.Sculpt(hit.X, hit.Z, _radius, SculptMode.Lower, RaiseRate * fast * dt, 0f, _stroke!),
            Tool.Smooth => source.Sculpt(hit.X, hit.Z, _radius, SculptMode.Smooth, MathF.Min(1f, BlendRate * fast * dt), 0f, _stroke!),
            Tool.Flatten => source.Sculpt(hit.X, hit.Z, _radius, SculptMode.Flatten, MathF.Min(1f, BlendRate * fast * dt), _flattenTo, _stroke!),
            Tool.Paint => source.Paint(hit.X, hit.Z, _radius, PaintCovers[_paint].Class, _stroke!),
            _ => null,
        };
        Rebuild(changed);
    }

    private void End()
    {
        if (_stroke is { IsEmpty: false } stroke)
        {
            _undo.Push(stroke);
            _redo.Clear();
        }

        _stroke = null;
    }

    private void Rebuild((int X0, int Z0, int X1, int Z1)? rect)
    {
        if (rect is { } r)
            _terrain!.Invalidate(r.X0, r.Z0, r.X1, r.Z1);
    }

    /// Where the ray through a point on the screen first meets the ground: marched out in steps that grow with
    /// distance, then narrowed down by halving.
    private double3? Pick(float2 point)
    {
        var source = _terrain!.Source;
        var tanY = MathF.Tan(_camera.FieldOfView * 0.5f);
        var aspect = _screen.X / _screen.Y;
        var nx = point.X / _screen.X * 2f - 1f;
        var ny = 1f - point.Y / _screen.Y * 2f;
        var dir = MathEx.Normalize(_camera.Forward + _camera.Right * (nx * tanY * aspect) + _camera.Up * (ny * tanY));

        double Above(double t) => _camera.Y + dir.Y * t - source.HeightAt(_camera.X + dir.X * t, _camera.Z + dir.Z * t);

        var previous = 0.0;
        var t = 1.0;
        while (t < 20000.0)
        {
            if (Above(t) < 0.0)
            {
                var lo = previous;
                var hi = t;
                for (var i = 0; i < 24; i++)
                {
                    var mid = (lo + hi) * 0.5;
                    if (Above(mid) < 0.0) hi = mid;
                    else lo = mid;
                }

                return new double3(_camera.X + dir.X * hi, _camera.Y + dir.Y * hi, _camera.Z + dir.Z * hi);
            }

            previous = t;
            t += Math.Max(1.0, t * 0.01);
        }

        return null;
    }

    /// The brush on the ground: a ring of points dropped onto the terrain, projected to the screen.
    private void DrawBrush(ImDrawListPtr draw)
    {
        if (_tool is Tool.Track or Tool.Lay)
        {
            _tracks!.Draw(draw, _camera.X, _camera.Z, ProjectGround, _hover, _dragging, _dragTo, _laying,
                _layNext is { } next ? (next.X, next.Z) : null);
            return;
        }

        if (_hit is not { } hit || _tool == Tool.Look)
            return;

        const int segments = 64;
        var source = _terrain!.Source;
        var viewProjection = _camera.ViewProjection(_screen.X / _screen.Y);
        var points = new float2?[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = MathF.Tau * i / segments;
            var x = hit.X + MathF.Cos(a) * _radius;
            var z = hit.Z + MathF.Sin(a) * _radius;
            var relative = new float3((float)(x - _camera.X), (float)(source.HeightAt(x, z) + 0.3 - _camera.Y), (float)(z - _camera.Z));
            var clip = MathEx.Project(relative, viewProjection);
            if (clip.W <= 0.01f)
                continue;
            points[i] = new float2((clip.X / clip.W * 0.5f + 0.5f) * _screen.X, (0.5f - clip.Y / clip.W * 0.5f) * _screen.Y);
        }

        var color = _tool == Tool.Paint ? Style.Accent : Rgba.White;
        for (var i = 0; i < segments; i++)
            if (points[i] is { } a && points[(i + 1) % segments] is { } b)
                draw.AddLine(a, b, color, S(1.5f));
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height) => _renderer.Render(cmd, new int2(width, height), _camera, _terrain, _network);

    public void CompositeWorld(CommandBuffer cmd) => _renderer.Composite(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        _screen = size;
        var left = S(40f);
        var top = _headerBottom + S(24f);
        Canvas.Text(draw, Heavy, S(24f), new float2(left, top), Style.Text, _route.Name.ToUpperInvariant());

        if (_terrain is null)
        {
            Canvas.Text(draw, Medium, S(15f), new float2(left, top + S(36f)), TextDim, "LOADING TERRAIN");
            return;
        }

        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var ground = _terrain.Source.HeightAt(_camera.X, _camera.Z);
        string Height(double meters) => imperial
            ? string.Create(CultureInfo.InvariantCulture, $"{meters * FeetPerMeter:#,0} FT")
            : string.Create(CultureInfo.InvariantCulture, $"{meters:#,0} M");
        var speed = imperial
            ? string.Create(CultureInfo.InvariantCulture, $"{_speed * 2.23694f:0} MPH")
            : string.Create(CultureInfo.InvariantCulture, $"{_speed * 3.6f:0} KM/H");
        var geo = _route.Projection!.Inverse(new RoutePoint(_camera.X, -_camera.Z));

        string[] lines =
        [
            string.Create(CultureInfo.InvariantCulture, $"{geo.Lat:0.00000}, {geo.Lon:0.00000}"),
            $"ALTITUDE {Height(_camera.Y)}   ABOVE GROUND {Height(_camera.Y - ground)}",
            $"SPEED {speed}",
            string.Create(CultureInfo.InvariantCulture, $"{_fps:0} FPS   {_terrain.DrawnPatches} PATCHES   {_network?.DrawnChunks} LINE CHUNKS   {_terrain.Building + _network?.Building} BUILDING"),
        ];

        for (var i = 0; i < lines.Length; i++)
            Canvas.Text(draw, Medium, S(14f), new float2(left, top + S(36f) + i * S(20f)), i == 0 ? Style.Text : TextDim, lines[i]);

        DrawBrush(draw);
        var tool = _tool switch
        {
            Tool.Look => "LOOK",
            Tool.Paint => $"PAINT {PaintCovers[_paint].Name}",
            Tool.Track => "EDIT TRACK",
            Tool.Lay => _laying.Count > 0 ? $"LAY TRACK   {_laying.Count} POINTS" : "LAY TRACK",
            _ => _tool.ToString().ToUpperInvariant(),
        };
        var radius = imperial
            ? string.Create(CultureInfo.InvariantCulture, $"{_radius * FeetPerMeter:0} FT")
            : string.Create(CultureInfo.InvariantCulture, $"{_radius:0} M");
        var toolLine = _tool is Tool.Look or Tool.Track or Tool.Lay ? tool : $"{tool}   RADIUS {radius}";
        if (_rebuild is not null)
            toolLine += "   UPDATING";
        if (_terrain.Source.Unsaved || _terrain.Source.Network.Unsaved)
            toolLine += "   UNSAVED";
        Canvas.Text(draw, Medium, S(14f), new float2(left, top + S(36f) + lines.Length * S(20f) + S(8f)), Style.Accent, toolLine);

        DrawFooter(draw, size);
    }

    /// File, Edit and View along the top: ImGui's main menu bar.
    public override void DrawWindows()
    {
        if (ImGui.BeginMainMenuBar())
        {
            _headerBottom = ImGui.GetWindowSize().Y;
            var source = _terrain?.Source;

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Save", "Ctrl+S", false, source is not null && (source.Unsaved || source.Network.Unsaved)))
                    Save(source!);
                ImGui.Separator();
                if (ImGui.MenuItem("Back to Route Builder", "Esc"))
                    _backRequested = true;
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _undo.Count > 0 && source is not null))
                {
                    var edit = _undo.Pop();
                    Redo(edit, undo: true);
                    _redo.Push(edit);
                }

                if (ImGui.MenuItem("Redo", "Ctrl+Y", false, _redo.Count > 0 && source is not null))
                {
                    var edit = _redo.Pop();
                    Redo(edit, undo: false);
                    _undo.Push(edit);
                }

                ImGui.Separator();
                foreach (var (tool, name, key) in Tools)
                    if (ImGui.MenuItem(name, key, _tool == tool, source is not null))
                        _tool = tool;
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.BeginMenu("Skybox"))
                {
                    foreach (var name in Sky.Available)
                    {
                        if (ImGui.MenuItem(Sky.Label(name), "", _renderer.Sky.Name == name, true))
                        {
                            _renderer.Sky.Use(name);
                            Settings.Current.Skybox = name;
                            Settings.Save();
                        }
                    }

                    ImGui.EndMenu();
                }

                if (ImGui.MenuItem("Wireframe", "F", _renderer.Wireframe, true))
                    _renderer.Wireframe = !_renderer.Wireframe;
                ImGui.Separator();
                ImGui.MenuItem("Frame Statistics"u8, "F4"u8, ref _showStats);
                ImGui.MenuItem("Profiler"u8, "F5"u8, ref _showProfiler);
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }

        StatsWindow();
        ProfilerWindow();
    }

    private void StatsWindow()
    {
        if (!_showStats)
            return;

        ImGui.SetNextWindowPos(new float2(_screen.X - 12f, _headerBottom + 12f), ImGuiCond.FirstUseEver, new float2(1f, 0f));
        ImGui.SetNextWindowSize(new float2(470f, 270f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Frame Statistics"u8, ref _showStats, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            var times = Profiler.FrameTimes;
            var average = times.Average();
            ImGui.Text($"{1000f / MathF.Max(0.01f, average):0} fps   {average:0.00} ms   worst {times.Max():0.0} ms");
            ImGui.PlotLines("##frames"u8, times, Profiler.FrameCursor, default, 0f, 40f, new float2(300f, 50f));
            ImGui.Text($"{Profiler.DrawCalls} draw calls   {Profiler.Triangles / 1000f:0.0}k triangles");
            ImGui.Text($"GPU {HighballApp.Instance.GpuName}");

            if (_terrain is { } terrain && _network is { } network)
            {
                var source = terrain.Source;
                ImGui.Separator();
                ImGui.Text($"Tiles {source.Tiles.Count}   lines {source.Lines?.Lines.Count ?? 0}   ways {source.Network.Ways.Count}");
                ImGui.Text($"Terrain patches {terrain.DrawnPatches} drawn / {terrain.PatchCount} held   {terrain.Building} building");
                ImGui.Text($"Line chunks {network.DrawnChunks} drawn   {network.Building} building");
                ImGui.Text($"Edits {_undo.Count} to undo   {(source.Unsaved || source.Network.Unsaved ? "unsaved" : "saved")}");
            }

            ImGui.Text($"Camera {_camera.X:0} {_camera.Y:0} {_camera.Z:0}   yaw {_camera.Yaw * 180f / MathF.PI:0}   pitch {_camera.Pitch * 180f / MathF.PI:0}");
        }

        ImGui.End();
    }

    private void ProfilerWindow()
    {
        if (!_showProfiler)
            return;

        ImGui.SetNextWindowPos(new float2(_screen.X - 12f, _headerBottom + 300f), ImGuiCond.FirstUseEver, new float2(1f, 0f));
        if (ImGui.Begin("Profiler"u8, ref _showProfiler, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("CPU time per frame, over the last two seconds"u8);
            if (ImGui.BeginTable("sections"u8, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Section"u8);
                ImGui.TableSetupColumn("Last ms"u8);
                ImGui.TableSetupColumn("Avg ms"u8);
                ImGui.TableSetupColumn("Worst ms"u8);
                ImGui.TableSetupColumn("Draws"u8);
                ImGui.TableHeadersRow();

                foreach (var (section, depth) in Profiler.Tree())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(new string(' ', depth * 2) + section.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Last:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Average:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Worst:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.DrawCalls}");
                }

                ImGui.EndTable();
            }

            ImGui.Text("Wait for GPU and Present include waiting for the display."u8);
        }

        ImGui.End();

    }

    /// The controls for flying and for the tool in hand, on one bar along the bottom.
    private void DrawFooter(ImDrawListPtr draw, float2 size)
    {
        var height = S(36f);
        _footerTop = size.Y - height;
        draw.AddRectFilled(new float2(0f, _footerTop), size, Surface);
        draw.AddLine(new float2(0f, _footerTop), new float2(size.X, _footerTop), StrokeStrong, 1f);

        (string, string)[] tool = _tool switch
        {
            Tool.Look => [("1-8", "Pick a tool")],
            Tool.Paint => [("LMB", "Paint"), ("P", "Cover"), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+Y", "Redo"), ("CTRL+S", "Save")],
            Tool.Track => [("LMB", "Drag point"), ("DEL", "Delete point"), ("CTRL+Z", "Undo"), ("CTRL+Y", "Redo"), ("CTRL+S", "Save")],
            Tool.Lay => [("LMB", "Add point"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel"), ("CTRL+Z", "Undo")],
            _ => [("LMB", _tool.ToString()), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+Y", "Redo"), ("CTRL+S", "Save")],
        };
        Canvas.Hints(draw, new float2(S(24f), size.Y - (height - S(22f)) * 0.5f),
            [("RMB", "Look"), ("WASD", "Move"), ("Q/E", "Down/Up"), ("SHIFT", "Fast"), ("WHEEL", "Speed"), ("F", "Wireframe"), ("ESC", "Back"), .. tool]);
    }
}
