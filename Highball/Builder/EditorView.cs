using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The builder's 2D and 3D Editors: the route's built terrain, seen straight down or flown through freely. From above,
/// drag with the right or middle mouse button (or WASD) to pan and the wheel zooms; in 3D, hold the right mouse button
/// to look, WASD to move, Q and E to sink and climb, and the wheel sets the speed.
///
/// The number keys pick a tool for the left mouse button: sculpting the ground (raise, lower, smooth, flatten to where
/// the stroke began), painting its land cover, editing track (see EditTrack) or the pen
/// (see PenTool; Enter lays the track, Backspace takes the last point back, Esc drops it). [ and ] size the brush, P
/// picks the cover to paint, G shows the track's grades, Ctrl+Z and Ctrl+Y undo and redo, Ctrl+S saves (switching
/// back to the Planner or leaving saves too).
public sealed partial class EditorView : IDisposable
{
    private enum Tool { Look, Raise, Lower, Smooth, Flatten, Paint, Track, Pen, Circuits, Switches, Shape, Follow, Place, Parallel, Road }

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

    /// Straight down, all but: exactly down leaves the camera no way to tell which way is north.
    private const float TopDownPitch = -MathF.PI / 2f + 0.001f;
    private const double MinTopDownHeight = 20.0;
    private const double MaxTopDownHeight = 40000.0;
    private const double GradeReadoutWindow = 50.0;
    private const float GradeColourWindow = 25f;

    private readonly RouteInfo _route;
    private readonly Task<TerrainSource> _opening;
    private readonly WorldRenderer _renderer = new();

    /// Puts the sky, time of day and exposure from Settings onto the renderer.
    public LightingPanel Lighting { get; }
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
    private Task<(int X0, int Z0, int X1, int Z1)?>? _dab;
    private float _dabTime;
    private float _flattenTo;
    /// The height F sampled, which Flatten then works to until Esc; without one, a stroke goes by where it starts.
    private float? _flattenSample;
    private double3? _hit;
    /// The ground under the pointer whatever the tool, for the status line.
    private double3? _pointerAt;
    private float2 _screen = new(1f, 1f);
    // Terrain strokes and network edits, in the order they were made.
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();

    private TrackEditor? _tracks;
    private CircuitEditor? _circuitEditor;
    private long? _hover;
    // Editing track: the selected control points, the one grabbed to drag them and how far they've been dragged, and
    // where a box being dragged out to select began.
    private readonly HashSet<long> _selection = [];
    private long? _grabbed;
    private (double X, double Z)? _grabFrom;
    private (double X, double Z)? _dragOffset;
    private float2? _boxFrom;
    private readonly PenTool _pen = new();
    private readonly RoadTool _road = new();
    private RoadEditor? _roads;
    private RoadEditor.Hit? _bridgeFrom;
    /// What the bridge begun at _bridgeFrom can end on, and where on the screen it was begun, to tell a drag from a click.
    private HashSet<long> _bridgeLine = [];
    private float2 _bridgePressed;
    private long? _roadHover;
    private LayPoint? _roadNext;
    private LayPoint? _layNext;
    private PenTool.Measure? _penMeasure;
    private Task<NetworkChange>? _rebuild;
    private bool _rebuildAgain;

    // The menu bar along the top and the controls footer along the bottom: the pointer over either isn't the world's.
    private float _headerBottom;
    private float _footerTop = float.MaxValue;

    // Seen from above: the flying camera is kept to go back to, and where a pan drag last was.
    private bool _topDown;
    private (double X, double Y, double Z, float Yaw, float Pitch)? _flight;
    private float2? _panFrom;

    // The grades on the track (G), left as they were each time the view opens.
    private static bool _showGrades = true;
    private (float2 Screen, float Grade, float Height)? _gradeAt;

    /// R held with something in the Objects tool to turn: the wheel turns it instead of moving the camera.
    private bool WheelTurns => _tool == Tool.Place && !Keyboard.KeysBlocked && Input.Key(GlfwKey.R) && _terrain is { } terrain &&
                               (terrain.Source.Objects.FirstOrDefault(o => o.Id == _place.Selected) is { Kind: not PlacedKind.HandThrow } || (_place.Selected is null && _place.Mode == PlaceMode.Track && _place.Stamp is not null));

    private bool Blocked => Keyboard.PointerBlocked || Input.MousePosition.Y < _headerBottom || Input.MousePosition.Y > _footerTop;

    private static readonly (Tool Tool, string Name, string Key)[] Tools =
    [
        (Tool.Look, "Free Look", "1"), (Tool.Raise, "Raise", "2"), (Tool.Lower, "Lower", "3"), (Tool.Smooth, "Smooth", "4"),
        (Tool.Flatten, "Flatten", "5"), (Tool.Paint, "Paint Cover", "6"), (Tool.Track, "Edit Track", "7"), (Tool.Pen, "Pen", "8"),
        (Tool.Circuits, "Track Circuits", "9"), (Tool.Switches, "Switches", "0"), (Tool.Shape, "Shape Track", "-"), (Tool.Follow, "Lay Yard", "="), (Tool.Parallel, "Parallel", "P"), (Tool.Place, "Objects", "\\"),
    ];

    /// The switch picked with the Switches tool, by its node, and each standard size's picture (loaded when first shown).
    private long? _switch;
    private readonly ShapeTool _shape = new();
    private readonly FollowTool _follow = new();
    private readonly YardTool _yard = new();
    private readonly YardTool _parallel = YardTool.ForParallel();
    private readonly PlaceTool _place = new();
    private (double X, double Z)? _placeGrab;
    /// Turning with the wheel: what it was before, for one undo when R is let go, and whether the wheel moved this hold of R.
    private PlacedObject? _wheelFrom;
    private bool _wheeled;
    private YardGuides? _guides;
    private readonly SwitchHandles _handles = new();
    private string? _switchRefused;
    private float2? _areaFrom;
    private GroundQuad? _area;
    private (double Built, double Cleared) _areaLength;
    private Dictionary<int, (Texture2D Texture, ImTextureRef Ref)>? _sizePictures;

    public bool CapturesMouse => _looking;
    public bool Ready => _terrain is not null;
    public bool Failed => _opening.IsFaulted;

    /// Where the camera is, in route metres east and north of the origin.
    public RoutePoint Position => new(_camera.X, -_camera.Z);

    /// start: where to put the camera (route metres east and north of the origin); the middle of the built tiles if null.
    public EditorView(RouteInfo route, RoutePoint? start = null)
    {
        _route = route;
        _start = start;
        Lighting = new LightingPanel(_renderer, route.Origin.Latitude, route.Origin.Longitude);
        _opening = TerrainSource.Open(route);
        if (DevTools.Clean)
            _showGrades = false;
        if (DevTools.Skybox is { } sky)
        {
            DevTools.Skybox = null;
            _renderer.Sky.Use(sky);
        }
    }

    public void Dispose()
    {
        Save();
        if (_sizePictures is not null)
            foreach (var (texture, reference) in _sizePictures.Values)
            {
                HighballApp.Instance.RemoveTexture(reference);
                texture.Dispose();
            }
        _backgroundTiles.Dispose();
        _circuitEditor?.Dispose();
        _terrain?.Dispose();
        _network?.Dispose();
        _renderer.Dispose();
    }

    /// Saves any edits not yet saved. True when that included track, so a copy of the network read before is stale.
    public bool Save() => _opening.IsCompletedSuccessfully && Save(_opening.Result);

    /// Esc: drops track being drawn, or the selection. False when there was neither, so Esc means something else.
    public bool Escape()
    {
        if (_pen.Count > 0)
            _pen.Clear();
        else if (_tool == Tool.Road && _road.Busy)
            _road.Clear();
        else if (_tool == Tool.Place && _bridgeFrom is not null)
            _bridgeFrom = null;
        else if (_tool == Tool.Flatten && _flattenSample is not null)
            _flattenSample = null;
        else if (_tool == Tool.Place && _place.RunFrom is not null)
            (_place.RunFrom, _place.RunFirst) = (null, null);
        else if (_selection.Count > 0)
            _selection.Clear();
        else if (_tool == Tool.Parallel && _parallel.Anchor is not null)
            _parallel.Clear();
        else if (_tool == Tool.Place && _place.Selected is not null)
            _place.Selected = null;
        else if (_tool == Tool.Place && _place.Stamp is not null)
            _place.Stamp = null;
        else
            return false;
        return true;
    }

    /// Straight down (the 2D Editor), or flying (the 3D Editor). Going down looks at the ground the camera was looking
    /// at; coming back, the flying camera is put over the middle of the top-down view as high and facing as it was.
    public void SetTopDown(bool on)
    {
        if (on == _topDown)
            return;
        _topDown = on;
        _camera.Zoom = 1f;
        _looking = false;
        _panFrom = null;
        if (_terrain is null)
            return;
        if (on)
            EnterTopDown();
        else
            LeaveTopDown();
    }

    private void EnterTopDown()
    {
        var source = _terrain!.Source;
        _flight = (_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch);
        var above = _camera.Y - source.HeightAt(_camera.X, _camera.Z);
        var (x, z) = Pick(_screen * 0.5f) is { } look && Math.Abs(look.X - _camera.X) + Math.Abs(look.Z - _camera.Z) < 3000.0
            ? (look.X, look.Z)
            : (_camera.X, _camera.Z);
        _camera.X = x;
        _camera.Z = z;
        _camera.Y = source.HeightAt(x, z) + Math.Clamp(above * 3.0, 400.0, 20000.0);
        _camera.Yaw = 0f;
        _camera.Pitch = TopDownPitch;
    }

    private void LeaveTopDown()
    {
        var source = _terrain!.Source;
        var (x, y, z, yaw, pitch) = _flight ?? (_camera.X, _camera.Y, _camera.Z, 0f, -0.2f);
        var above = Math.Max(2.0, y - source.HeightAt(x, z));
        _camera.Y = source.HeightAt(_camera.X, _camera.Z) + above;
        _camera.Yaw = yaw;
        _camera.Pitch = pitch;
    }

    /// Puts the camera over a point in route metres, keeping its height above the ground, heading and pitch.
    public void MoveTo(RoutePoint point)
    {
        if (_terrain is null)
            return;
        var source = _terrain.Source;
        var above = _camera.Y - source.HeightAt(_camera.X, _camera.Z);
        _camera.X = point.East;
        _camera.Z = -point.North;
        _camera.Y = source.HeightAt(_camera.X, _camera.Z) + above;
    }

    /// top and bottom: where the menu bar ends and the footer begins.
    public void Update(float dt, float top, float bottom)
    {
        _headerBottom = top;
        _footerTop = bottom;
        _fps = dt > 0f ? _fps + (1f / dt - _fps) * MathF.Min(1f, dt * 4f) : _fps;

        if (_terrain is null)
        {
            if (!_opening.IsCompleted)
                return;

            if (_opening.IsFaulted)
            {
                Log.Error($"3D Editor: {_opening.Exception?.InnerException?.Message}");
                return;
            }

            _terrain = new Terrain(_opening.Result);
            _network = new NetworkMeshes(_opening.Result);
            _opening.Result.Objects.AddRange(RouteObjects.Load(_route));
            _network.SetObjects(_opening.Result.Objects, [], _route.Projection!);
            if (DevTools.AuditFile is { } auditFile)
            {
                DevTools.AuditFile = null;
                var report = TrackAudit.Report(TrackAudit.Run(_opening.Result.Lines!, _opening.Result.Objects, _route.Projection!, _route.BuiltTiles(), _route.Grid!.TileSize), _route.Projection!, _route.Name);
                File.WriteAllText(auditFile, report);
                Log.Info(report.Split('#')[1]);
            }
            _tracks = new TrackEditor(_route, _opening.Result.Network);
            _circuitEditor = new CircuitEditor(_route);
            if (DevTools.ProfileStats)
                Log.Info(ProfileReport.Of(_opening.Result.Lines!, _route.Projection!));
            if (DevTools.Tool is { } first)
            {
                DevTools.Tool = null;
                _tool = (Tool)Math.Clamp(first - 1, 0, (int)Tool.Place);
            }
            PlaceCamera(_opening.Result);
            if (_topDown)
                EnterTopDown();
        }

        if (_topDown)
            Pan(dt);
        else
            Fly(dt);
        using (Profiler.Sample("Editing"))
        {
            Edit(dt);
            EditTrack();
            EditCircuits();
            PollRebuild();
            _circuitEditor!.Update(_terrain.Source.Network);
        }

        KeepWorldCurrent();

        if (ImGui.IsKeyPressed(ImGuiKey.F))
            _renderer.Wireframe = !_renderer.Wireframe;
        if (ImGui.IsKeyPressed(ImGuiKey.G) && !Keyboard.KeysBlocked)
            _showGrades = !_showGrades;

        // T throws the switch nearest the pointer.
        if (ImGui.IsKeyPressed(ImGuiKey.T) && !Keyboard.KeysBlocked && Pick(Input.MousePosition) is { } at)
            _network!.Throw(at.X, at.Z, 40.0);
        if (DevTools.SwitchAt is { } pick && _terrain.Source.Lines is { } picking && picking.Turnouts.Count > 0 && _rebuild is null)
        {
            DevTools.SwitchAt = null;
            var p = _route.Projection!.Forward(new GeoPoint(pick.Lat, pick.Lon));
            _tool = Tool.Switches;
            _switch = picking.Turnouts.MinBy(t => (t.X - p.East) * (t.X - p.East) + (t.Z + p.North) * (t.Z + p.North))?.Node;
            if (_switch is { } node && picking.SwitchOptions.GetValueOrDefault(node) is var options)
                Log.Info($"Switch {node}: auto {options?.Auto}, fits {string.Join(",", options?.Fits ?? [])}, chosen {options?.Chosen}");
            if (_switch is { } chosen)
                PickSize(chosen, pick.Size < 0 ? null : pick.Size);
        }

        if (DevTools.ThrowAt is { } dev && _network!.Turnouts > 0)
        {
            DevTools.ThrowAt = null;
            var p = _route.Projection!.Forward(new GeoPoint(dev.Lat, dev.Lon));
            var thrown = _network.Throw(p.East, -p.North, 200.0);
            Log.Info(thrown is null ? "Throw: no switch near" : $"Throw: switch at node {thrown.Node}, frog {thrown.FrogNormal:0.0} m out, blades {thrown.BladeLength:0.0} m");
            if (thrown is null)
                foreach (var t in _terrain.Source.Lines!.Turnouts.OrderBy(t => (t.X - p.East) * (t.X - p.East) + (t.Z + p.North) * (t.Z + p.North)).Take(5))
                {
                    var g = _route.Projection.Inverse(new RoutePoint(t.X, -t.Z));
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch near: {g.Lat:0.000000} {g.Lon:0.000000}, frog {t.FrogNormal:0.0} m, side {t.Side}"));
                }
        }
    }

    /// Brings the ground and the track up to date with the camera and the settings.
    public void KeepWorldCurrent()
    {
        if (_terrain is null)
            return;

        // How far the land reaches, past however high the camera is. Looking straight down in the 2D view, all of it.
        var settings = Settings.Current;
        var above = Math.Max(0.0, _camera.Y - _terrain.Source.HeightAt(_camera.X, _camera.Z));
        var limit = _topDown ? 0.0 : settings.ViewDistance + above;
        _terrain.DetailFactor = Quality.TerrainDetail;
        _terrain.ViewLimit = limit;
        _renderer.Sky.SetViewLimit((float)limit);
        _network!.FarRadius = limit > 0.0 ? (float)Math.Min(FarestTrack, limit) : FarestTrack;

        using (Profiler.Sample("Terrain LOD"))
            _terrain.Update(_camera.X, _camera.Y, _camera.Z);
        using (Profiler.Sample("Line chunks"))
            _network.Update(_camera.X, _camera.Y, _camera.Z);
    }

    private const float FarestTrack = 6000f;

    /// Seen from above: drag with the right or middle button (or WASD) to pan, the wheel zooms about the pointer.
    private void Pan(float dt)
    {
        _looking = false;
        var source = _terrain!.Source;
        var above = Math.Max(MinTopDownHeight, _camera.Y - source.HeightAt(_camera.X, _camera.Z));
        var metresPerPixel = 2.0 * above * Math.Tan(_camera.FieldOfView * 0.5) / _screen.Y;
        var mouse = Input.MousePosition;

        var dragging = Input.Mouse(GlfwMouseButton.Right) || Input.Mouse(GlfwMouseButton.Middle);
        if (!dragging)
            _panFrom = null;
        else if (_panFrom is null && !Blocked)
            _panFrom = mouse;
        if (_panFrom is { } from)
        {
            _camera.X -= (mouse.X - from.X) * metresPerPixel;
            _camera.Z -= (mouse.Y - from.Y) * metresPerPixel;
            _panFrom = mouse;
        }

        var speed = above * (Input.Key(GlfwKey.LeftShift) ? 4.0 : 1.0) * dt;
        if (Input.Key(GlfwKey.W)) _camera.Z -= speed;
        if (Input.Key(GlfwKey.S)) _camera.Z += speed;
        if (Input.Key(GlfwKey.A)) _camera.X -= speed;
        if (Input.Key(GlfwKey.D)) _camera.X += speed;

        if (Input.Scroll != 0f && !Blocked && !WheelTurns)
        {
            // The ground under the pointer stays under it.
            var zoomed = Math.Clamp(above * Math.Pow(0.8, Input.Scroll), MinTopDownHeight, MaxTopDownHeight);
            var shrink = 1.0 - zoomed / above;
            _camera.X += (mouse.X - _screen.X * 0.5) * metresPerPixel * shrink;
            _camera.Z += (mouse.Y - _screen.Y * 0.5) * metresPerPixel * shrink;
            above = zoomed;
            _camera.Y = source.HeightAt(_camera.X, _camera.Z) + above;
        }

        _camera.Y = Math.Max(_camera.Y, source.HeightAt(_camera.X, _camera.Z) + MinTopDownHeight);
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
        // Only starting is up to where the pointer is: once looking, it's held until the button's let go.
        _looking = Input.Mouse(GlfwMouseButton.Right) && (_looking || !Blocked);
        if (_looking)
        {
            // (Slower zoomed in, so that things move across the screen at the same rate.)
            var sensitivity = 0.0025f / _camera.Zoom;
            _camera.Yaw += Input.MouseDelta.X * sensitivity;
            _camera.Pitch = Math.Clamp(_camera.Pitch - Input.MouseDelta.Y * sensitivity, -1.55f, 1.55f);
        }

        if (Input.Scroll != 0f && !Blocked && !WheelTurns)
        {
            if (Input.Key(GlfwKey.LeftShift))
                _camera.Zoom = Math.Clamp(_camera.Zoom * MathF.Pow(1.25f, Input.Scroll), 1f, MaxLensZoom);
            else
                _speed = Math.Clamp(_speed * MathF.Pow(1.25f, Input.Scroll), MinSpeed, MaxSpeed);
        }

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

        if (DevTools.FlySpeed is { } fly)
            FlyTest(fly, forward, dt);

        var ground = _terrain!.Source.HeightAt(_camera.X, _camera.Z);
        _camera.Y = Math.Max(_camera.Y, ground + 1.8);
    }

    private const float MaxLensZoom = 10f;

    private double _flyLogAt;
    private float _flyWorst;
    private int _flyWaitingWorst;

    /// --fly: straight ahead along the ground at speed metres a second, logging each second's worst frame and the most
    /// terrain patches left waiting for detail.
    private void FlyTest(float speed, float3 forward, float dt)
    {
        var flat = MathF.Sqrt(forward.X * forward.X + forward.Z * forward.Z);
        if (flat > 0f)
        {
            _camera.X += forward.X / flat * speed * dt;
            _camera.Z += forward.Z / flat * speed * dt;
        }

        _flyWorst = MathF.Max(_flyWorst, dt * 1000f);
        _flyWaitingWorst = Math.Max(_flyWaitingWorst, _terrain!.Waiting);
        var now = RailWerkz.Core.Time.Time.Now;
        if (now - _flyLogAt < 1.0)
            return;
        var slow = string.Join(", ", Profiler.Tree().Where(s => s.Section.Worst > 2.5f).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Section.Name} {s.Section.Worst:0}")));
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Fly: worst frame {_flyWorst:0.0} ms, {_flyWaitingWorst} patches waiting, {_terrain.Building} building, {_terrain.DrawnPatches} drawn, {_renderer.Vegetation.Instances} plants; slow: {slow}"));
        _flyLogAt = now;
        _flyWorst = 0f;
        _flyWaitingWorst = 0;
    }

    // --- Editing. ---

    private void Edit(float dt)
    {
        CollectDab(false);
        var source = _terrain!.Source;
        var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);

        if (IsBrush(_tool))
            _brush = _tool;

        // (Not while typing in a field: digits there aren't tools.)
        if (!Keyboard.KeysBlocked)
        {
            for (var i = 0; i <= (int)Tool.Circuits; i++)
                if (Input.KeyDown(GlfwKey.Number1 + i))
                    _tool = (Tool)i;
            if (Input.KeyDown(GlfwKey.Number0))
                _tool = Tool.Switches;
            if (Input.KeyDown(GlfwKey.Minus))
                _tool = Tool.Shape;
            if (Input.KeyDown(GlfwKey.Equal))
                _tool = Tool.Follow;
            if (Input.KeyDown(GlfwKey.Backslash) || Input.KeyDown(GlfwKey.O))
                _tool = Tool.Place;
            if (Input.KeyDown(GlfwKey.Semicolon))
                _tool = Tool.Road;
            if (Input.KeyDown(GlfwKey.LeftBracket))
                _radius = MathF.Max(MinRadius, _radius / 1.25f);
            if (Input.KeyDown(GlfwKey.RightBracket))
                _radius = MathF.Min(MaxRadius, _radius * 1.25f);
            if (Input.KeyDown(GlfwKey.P))
            {
                if (_tool == Tool.Paint)
                    _paint = (_paint + 1) % PaintCovers.Length;
                else
                    _tool = Tool.Parallel;
            }

            if (ctrl && Input.KeyDown(GlfwKey.Z))
                UndoLast();
            if (ctrl && Input.KeyDown(GlfwKey.Y))
                RedoLast();
            if (ctrl && Input.KeyDown(GlfwKey.S))
                Save(source);
        }

        _pointerAt = Blocked ? null : Pick(Input.MousePosition);
        _hit = _tool == Tool.Look ? null : _pointerAt;
        if (_tool == Tool.Flatten && !Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.F) && _hit is { } sampled)
            SampleFlatten(source, sampled.X, sampled.Z);
        if (_tool == Tool.Switches && source.Lines is { } picked)
            SwitchesUpdate(picked);

        if (_tool == Tool.Shape)
            ShapeUpdate(source);
        if (source.Lines is { } guided)
            (_guides ??= new YardGuides(source.Network, _route.Projection!)).Refresh(guided);
        if (_tool == Tool.Follow)
            FollowUpdate();
        if (_tool == Tool.Parallel)
            YardUpdate(Blocked);
        if (_tool == Tool.Place && source.Lines is { } placing)
            PlaceUpdate(source, placing);
        if (_tool == Tool.Road)
            RoadUpdate();
        BridgeUpdate();
        if (DevTools.ShapeOps is { Count: > 0 } && _rebuild is null)
            ShapeOp(source, DevTools.ShapeOps.Dequeue());

        if (_tool is Tool.Track or Tool.Pen or Tool.Circuits or Tool.Switches or Tool.Shape or Tool.Follow or Tool.Place or Tool.Road or Tool.Parallel)
            return;

        if (DevTools.SculptHold is { } hold && RailWerkz.Core.Time.Time.Now >= hold.From)
        {
            if (RailWerkz.Core.Time.Time.Now > hold.Until)
            {
                DevTools.SculptHold = null;
                End();
            }
            else if (Pick(_screen * 0.5f) is { } held && Enum.TryParse<Tool>(hold.Tool, true, out var holding))
            {
                (_tool, _hit, _radius) = (holding, held, hold.Radius);
                if (_stroke is null)
                    Begin(source);
                Apply(source, dt);
            }

            return;
        }

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
                var scripted = _stroke!;
                End();
                if (!scripted.IsEmpty)
                    DevTools.Sculpt = null;
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
            case SignallingEdit signalling:
                _circuitEditor!.Apply(signalling, undo);
                break;
            case TrackShapeEdit shape:
                _terrain!.Source.TrackEdits.Set(shape.Key, undo ? shape.Before : shape.After);
                RebuildNetwork();
                break;
            case SwitchCurveEdit curve:
                SwitchCurves.Set(_route, curve.Node, undo ? curve.Before : curve.After);
                RebuildNetwork();
                break;
            case PlacedEdit placed:
                (_place.RunFrom, _place.RunFirst) = (null, null);
                ApplyPlaced(undo ? placed.After : placed.Before, undo ? placed.Before : placed.After);
                break;
            case PlacedRunEdit run:
                (_place.RunFrom, _place.RunFirst) = (null, null);
                ApplyPlaced(undo ? run.Edits.Select(e => new PlacedEdit(e.After, e.Before)).ToList() : run.Edits);
                break;
        }
    }

    private bool Save(TerrainSource source)
    {
        _circuitEditor?.Save();
        if (source.Unsaved)
            source.SaveEdits();
        if (!source.Network.Unsaved)
            return false;
        source.Network.Save(_route);
        return true;
    }

    // --- Switches. ---

    /// A press on the picked switch's handles drags them; any other picks what's under it.
    private void SwitchesUpdate(NetworkLines lines)
    {
        var blocked = Blocked;
        var mouse = Input.MousePosition;
        var turnout = _switch is { } node ? lines.Turnouts.FirstOrDefault(t => t.Node == node) : null;
        var clicked = !blocked && Keyboard.Clicked();
        if (_rebuild is null || _handles.Active)
            SwitchChange(lines, turnout, _handles.Update(lines, turnout, mouse, !blocked && _hit is { } hit ? (hit.X, hit.Z) : null, ProjectGround, S(1f), clicked && _rebuild is null, Input.Mouse(GlfwMouseButton.Left)));
        if (clicked && !_handles.Active)
            _switch = SwitchAt(lines, mouse);
    }

    private void SwitchChange(NetworkLines lines, Turnout? turnout, SwitchHandles.Change? change)
    {
        if (change is null || turnout is null)
            return;
        _switchRefused = null;
        if (change.Reach is { } reach)
        {
            var edit = new SwitchCurveEdit(change.Node, SwitchCurves.Load(_route).TryGetValue(change.Node, out var was) ? was : null, reach);
            SwitchCurves.Set(_route, change.Node, reach);
            _undo.Push(edit);
            _redo.Clear();
            RebuildNetwork();
        }
        else if (change.SlideTo is { } to)
        {
            var edit = change.Partner is { } partner ? _tracks!.SlideCrossover(turnout, to, partner.Turnout, partner.To) : _tracks!.SlideSwitch(turnout, to.X, to.Z);
            if (edit is not null)
                Committed(edit);
            else
                _switchRefused = change.Partner is null ? "Can't move there: its diverging track has no room" : "Can't move the crossover there";
        }
    }

    // --- Placing objects. ---

    private (double X, double Z) PlacedAtWorld(PlacedObject o)
    {
        var p = _route.Projection!.Forward(new GeoPoint(o.Lat, o.Lon));
        return (p.East, -p.North);
    }

    private PlacedObject Placed(PlacedKind kind, double x, double z, double heading, long? node = null, float width = 0f, bool opposite = false, long? id = null)
    {
        var geo = _route.Projection!.Inverse(new RoutePoint(x, -z));
        return new PlacedObject(id ?? DateTime.UtcNow.Ticks, kind, geo.Lat, geo.Lon, heading, node, width, opposite);
    }

    /// One object swapped for another in the route's list (null: none), saved, and shown.
    private void ApplyPlaced(PlacedObject? gone, PlacedObject? added) => ApplyPlaced([new PlacedEdit(gone, added)]);

    /// Many such swaps, saved and shown once. (Each one's "before" goes and its "after" comes.)
    private void ApplyPlaced(IReadOnlyList<PlacedEdit> edits)
    {
        var source = _terrain!.Source;
        var changed = new List<(double X, double Z)>();
        foreach (var (gone, added) in edits)
        {
            if (gone is not null)
            {
                source.Objects.RemoveAll(o => o.Id == gone.Id);
                changed.Add(PlacedAtWorld(gone));
            }

            if (added is not null)
            {
                source.Objects.Add(added);
                changed.Add(PlacedAtWorld(added));
            }
        }

        RouteObjects.Save(_route, source.Objects);
        _network!.SetObjects(source.Objects, changed, _route.Projection!);
    }

    private void PlacedChange(PlacedObject? before, PlacedObject? after)
    {
        ApplyPlaced(before, after);
        _undo.Push(new PlacedEdit(before, after));
        _redo.Clear();
        _place.Selected = after?.Id;
    }

    /// A click with a pole in hand: one pole; or, with Fence on, a run's first pole and then each stretch of it, one undo a stretch.
    private void PolePress(NetworkLines lines, double x, double z)
    {
        if (_place.RunFrom is not { } from)
        {
            if (PoleRuns.At(lines, _place.Snap, _place.PoleKind, _place.PoleOffset, x, z) is not { } start)
            {
                _switchRefused = _place.Snap == PoleSnap.Road ? "No road here" : "No track here";
                return;
            }

            var made = Placed(_place.PoleKind, start.X, start.Z, start.Heading);
            PlacedChange(null, made);
            if (_place.Fence)
                (_place.RunFrom, _place.RunFirst, _place.Selected) = ((start.X, start.Z), made.Id, null);
            return;
        }

        var run = PoleRuns.Lay(lines, _place.Snap, _place.PoleKind, _place.PoleOffset, _place.Gap, from, (x, z));
        if (run.Problem is { } problem)
        {
            _switchRefused = problem;
            return;
        }

        if (run.Poles.Count == 0)
        {
            _switchRefused = "That's less than the gap from the last pole";
            return;
        }

        var edits = new List<PlacedEdit>();
        // The first pole didn't know which way the run went until now.
        if (_terrain!.Source.Objects.FirstOrDefault(o => o.Id == _place.RunFirst) is { } first)
            edits.Add(new PlacedEdit(first, first with { Heading = run.Poles[0].Heading }));
        var id = DateTime.UtcNow.Ticks;
        foreach (var pole in run.Poles)
            edits.Add(new PlacedEdit(null, Placed(_place.PoleKind, pole.X, pole.Z, pole.Heading, id: id++)));
        ApplyPlaced(edits);
        _undo.Push(new PlacedRunEdit(edits));
        _redo.Clear();
        var last = run.Poles[^1];
        (_place.RunFrom, _place.RunFirst, _place.Selected) = ((last.X, last.Z), null, null);
    }

    /// A click: on an object, picks it; elsewhere, places one if there's somewhere to.
    private void PlacePress(TerrainSource source, NetworkLines lines, double x, double z)
    {
        _switchRefused = null;
        switch (_place.Mode)
        {
            case PlaceMode.Signal when PlaceTool.SignalAt(lines, x, z) is { } signal:
                PlacedChange(null, Placed(PlacedKind.Signal, signal.X, signal.Z, signal.Heading));
                break;
            case PlaceMode.HandThrow when PlaceTool.SwitchAt(lines, x, z) is { } turnout:
                if (source.Objects.FirstOrDefault(o => o.Node == turnout.Node) is { } already)
                    _place.Selected = already.Id;
                else
                    PlacedChange(null, Placed(PlacedKind.HandThrow, turnout.X, turnout.Z, 0.0, turnout.Node));
                break;
            case PlaceMode.GradeCrossing when source.Objects.FirstOrDefault(o => o.Kind == PlacedKind.GradeCrossing && SameCrossing(o, x, z)) is { } existing:
                // (A crossing takes in every track its road meets: this one's already got this track.)
                _place.Selected = existing.Id;
                break;
            case PlaceMode.GradeCrossing when PlaceTool.CrossingAt(lines, x, z) is { } crossing:
                PlacedChange(null, Placed(PlacedKind.GradeCrossing, crossing.X, crossing.Z, crossing.Heading, null, crossing.Width));
                break;
            case PlaceMode.Pole:
                PolePress(lines, x, z);
                break;
            case PlaceMode.Track:
                LayStamp(lines, x, z);
                break;
            case PlaceMode.Bridge:
                BridgeClick(x, z);
                break;
            case PlaceMode.RemoveBridge:
                if (Roads.Nearest(x, z, PickReach(x, z), RoadEditor.IsBridge) is { } bridge)
                    Committed(Roads.RemoveBridge(bridge.Way));
                else
                    _switchRefused = "No bridge here";
                break;
            case PlaceMode.Diamond when _tracks!.CrossingNear(x, z, PlaceTool.SnapReach) is { } diamond:
                if (diamond.Degrees < 8.0)
                    _switchRefused = "Tracks cross under 8 degrees: too shallow for a diamond";
                else
                    Committed(_tracks.JoinCrossing(diamond));
                break;
            default:
                _switchRefused = _place.Mode == PlaceMode.Diamond ? "No tracks cross here" : _place.Mode == PlaceMode.HandThrow ? "No switch here" : "No track here";
                break;
        }
    }

    private RoadEditor Roads => _roads ??= new RoadEditor(_route, _terrain!.Source.Network, _tracks!);

    /// How many metres of ground a few pixels cover, at a place on it.
    private double PickReach(double x, double z)
    {
        var (dx, dy, dz) = (x - _camera.X, _terrain!.Source.HeightAt(x, z) - _camera.Y, z - _camera.Z);
        return Math.Max(3.0, Math.Sqrt(dx * dx + dy * dy + dz * dz) * 2.0 * Math.Tan(_camera.FieldOfView * 0.5) / Math.Max(_screen.Y, 1f) * S(14f));
    }

    /// The shortest bridge worth building and the longest it can go, in metres. How far past a line's end the cursor has to be to
    /// carry the bridge on past it, and how near an end a bridge starting there begins at the end itself.
    private const double ShortestBridge = 4.0;
    private const double LongestBridge = 800.0;
    private const double BridgeOutFrom = 2.0;
    private const double BridgeStartEnd = 8.0;

    /// Where a bridge would start under the cursor, on the line nearest, road or track. Generous, since it's hard to miss on purpose.
    private RoadEditor.Hit? BridgeStartAt(double x, double z) =>
        Roads.Nearest(x, z, Math.Max(PickReach(x, z) * 2.0, 8.0), w => !w.Tags.ContainsKey(RouteNetwork.RemovedTag) && !RouteNetwork.IsGuide(w)) is { } hit
            ? Roads.ToLooseEnd(hit, BridgeStartEnd)
            : null;

    /// Where a bridge begun already would end: the place on its own line nearest the cursor, however far off the cursor is.
    private RoadEditor.Hit? BridgeEndAt(double x, double z) => Roads.Nearest(x, z, 1e7, w => _bridgeLine.Contains(w.Id));

    /// Where a bridge begun already would land if the cursor's out past a loose end of its line, over whatever gap's there.
    private LayPoint? BridgeLandingAt(RoadEditor.Hit end, double x, double z)
    {
        if (Roads.LooseEndAt(end) is null || Math.Sqrt((x - end.X) * (x - end.X) + (z - end.Z) * (z - end.Z)) < BridgeOutFrom)
            return null;
        return Roads.LandingAt(end, x, z, Math.Max(PickReach(x, z), 6.0));
    }

    /// How long a bridge would be, along its line and on past the end if it's going there.
    private double BridgeLength(RoadEditor.Hit from, RoadEditor.Hit end, LayPoint? landing)
    {
        var along = Roads.Span(from, end)?.Length ?? 0.0;
        return landing is { } l ? along + Math.Sqrt((l.X - end.X) * (l.X - end.X) + (l.Z - end.Z) * (l.Z - end.Z)) : along;
    }

    /// A bridge's first click holds its place, and the second, or letting go of a drag, builds it. Once begun it stays on its line:
    /// the cursor can't pick the one beside it.
    private void BridgeClick(double x, double z)
    {
        _switchRefused = null;
        if (_bridgeFrom is null)
        {
            if (BridgeStartAt(x, z) is not { } hit)
            {
                _switchRefused = Roads.Nearest(x, z, Math.Max(PickReach(x, z), 8.0), w => !w.Tags.ContainsKey(RouteNetwork.RemovedTag)) is null
                    ? "Click on a road or a track"
                    : "That's a guide: lay track on it first";
                return;
            }

            _bridgeFrom = hit;
            _bridgeLine = Roads.LineOf(hit.Way);
            _bridgePressed = Input.MousePosition;
            return;
        }

        BridgeFinish(x, z);
    }

    private void BridgeFinish(double x, double z)
    {
        if (_bridgeFrom is not { } from || BridgeEndAt(x, z) is not { } end)
            return;

        var landing = BridgeLandingAt(end, x, z);
        var length = BridgeLength(from, end, landing);
        if (length < ShortestBridge || length > LongestBridge)
        {
            _switchRefused = length < ShortestBridge ? "Too short for a bridge: keep going" : "Too long for one bridge";
            return;
        }

        _bridgeFrom = null;
        var made = landing is { } out2 ? Roads.AddBridgeOut(from, end, out2, out var refused) : Roads.AddBridge(from, end, out refused);
        if (made is { } edit)
            Committed(edit);
        else
            _switchRefused = refused;
    }

    /// Letting go of a drag ends the bridge there; letting go where it was pressed leaves it waiting for the second click.
    private void BridgeUpdate()
    {
        if (_tool != Tool.Place || _place.Mode != PlaceMode.Bridge || _bridgeFrom is null || Blocked || _hit is not { } hit || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;
        var moved = Input.MousePosition - _bridgePressed;
        if (moved.X * moved.X + moved.Y * moved.Y > S(16f) * S(16f))
            BridgeFinish(hit.X, hit.Z);
    }

    private void RoadUpdate()
    {
        _roadHover = null;
        _roadNext = null;
        if (Blocked || _hit is not { } hit)
            return;

        var (x, z) = (hit.X, hit.Z);
        var reach = PickReach(x, z);
        if (_road.Mode == RoadMode.Remove)
        {
            if (Roads.Nearest(x, z, reach, RoadEditor.IsRoad) is { } under)
            {
                _roadHover = under.Way;
                if (Input.MouseDown(GlfwMouseButton.Left))
                    Committed(Roads.Remove(under.Way));
            }

            return;
        }

        if ((Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift)) && _road.Mode == RoadMode.Straight)
            (x, z) = _road.Stepped(x, z);

        // The control point of a curve stands free; everything else joins what it lands on.
        _roadNext = _road.Mode == RoadMode.Curved && _road.From is not null && _road.Control is null ? new LayPoint(x, z) : Roads.Snap(x, z, reach);
        if (Input.MouseDown(GlfwMouseButton.Left) && _road.Click(_roadNext.Value) is { } points)
            Committed(Roads.Lay(points, RoadTool.Kinds[_road.Kind]));
    }

    /// Lays the track piece in hand, as it's shown at this place, and sets its switches to its size.
    private void LayStamp(NetworkLines lines, double x, double z)
    {
        _place.Update(lines, _tracks!, (x, z), PanelLength);
        if (_place.Plan is not { } plan)
            return;
        if (plan.Problem is { } problem)
        {
            _switchRefused = problem;
            return;
        }

        var laid = new List<long[]>();
        if (_tracks!.LayWays(plan.Ways, laid) is not { } edit)
            return;
        foreach (var (way, point, size) in plan.Switches)
            SwitchSizes.Set(_route, laid[way][point], size);
        Committed(edit);
    }

    /// Whether a place is on a crossing's road, near enough to be one of its tracks.
    private bool SameCrossing(PlacedObject o, double x, double z)
    {
        var (cx, cz) = PlacedAtWorld(o);
        var angle = o.Heading * Math.PI / 180.0;
        var (dx, dz) = (Math.Cos(angle), Math.Sin(angle));
        return Math.Abs((x - cx) * -dz + (z - cz) * dx) < o.Width * 0.5 + 2.0 && Math.Abs((x - cx) * dx + (z - cz) * dz) < 30.0;
    }

    /// Moves the picked object to a place on the ground: it snaps as when it's placed.
    private void PlaceMove(NetworkLines lines, PlacedObject o, double x, double z)
    {
        switch (o.Kind)
        {
            case var kind when kind.IsPole():
                // As the tool's set: dropped where it's let go, keeping its turn; or set off the road or track again.
                if (_place.Snap == PoleSnap.Anywhere)
                    PlacedChange(o, Placed(o.Kind, x, z, o.Heading, id: o.Id));
                else if (PoleRuns.At(lines, _place.Snap, o.Kind, _place.PoleOffset, x, z) is { } pole)
                    PlacedChange(o, Placed(o.Kind, pole.X, pole.Z, pole.Heading, id: o.Id));
                break;
            case PlacedKind.Signal when PlaceTool.SignalAt(lines, x, z) is { } signal:
                PlacedChange(o, Placed(PlacedKind.Signal, signal.X, signal.Z, signal.Heading, id: o.Id));
                break;
            case PlacedKind.GradeCrossing when PlaceTool.CrossingAt(lines, x, z) is { } crossing:
                PlacedChange(o, Placed(PlacedKind.GradeCrossing, crossing.X, crossing.Z, crossing.Heading, null, o.Width, id: o.Id));
                break;
            case PlacedKind.HandThrow when PlaceTool.SwitchAt(lines, x, z) is { } turnout && _terrain!.Source.Objects.All(p => p.Node != turnout.Node):
                PlacedChange(o, Placed(PlacedKind.HandThrow, turnout.X, turnout.Z, 0.0, turnout.Node, opposite: o.Opposite, id: o.Id));
                break;
        }
    }

    private void PlaceTurn(PlacedObject o, double degrees) => PlacedChange(o, o with { Heading = o.Heading + degrees });

    private void PlaceUpdate(TerrainSource source, NetworkLines lines)
    {
        var blocked = Blocked;
        var mouse = Input.MousePosition;
        var ground = !blocked && _hit is { } hit ? (hit.X, hit.Z) : ((double, double)?)null;
        _place.Update(lines, _tracks!, ground, PanelLength);
        if (blocked || _rebuild is not null)
            return;

        var picked = source.Objects.FirstOrDefault(o => o.Id == _place.Selected);
        if (Input.MouseDown(GlfwMouseButton.Left))
        {
            // (A run being laid isn't stopped by a pole in its way.)
            var under = _place.RunFrom is not null ? null : PlaceTool.At(source.Objects, o => ProjectGround(PlacedAtWorld(o).X, PlacedAtWorld(o).Z), mouse, S(1f));
            if (under is not null)
            {
                _place.Selected = under.Id;
                _placeGrab = ground;
            }
            else if (ground is { } at)
            {
                // Always places: what's picked just stops being picked, so object after object takes a click each.
                _place.Selected = null;
                PlacePress(source, lines, at.Item1, at.Item2);
            }
        }

        if (_placeGrab is { } from && !Input.Mouse(GlfwMouseButton.Left))
        {
            _placeGrab = null;
            if (picked is not null && ground is { } to && (to.Item1 - from.X) * (to.Item1 - from.X) + (to.Item2 - from.Z) * (to.Item2 - from.Z) > 1.0)
                PlaceMove(lines, picked, to.Item1, to.Item2);
        }

        var shift = Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift);
        if (Keyboard.KeysBlocked)
            return;

        // R held with the wheel turns it a little at a time; R tapped alone turns it 15 degrees as it's let go.
        if (Input.KeyDown(GlfwKey.R))
            _wheeled = false;
        if (WheelTurns && Input.Scroll != 0f)
        {
            _wheeled = true;
            var degrees = Input.Scroll * (shift ? 5.0 : 1.0);
            if (picked is not null)
            {
                _wheelFrom ??= picked;
                var turned = picked with { Heading = picked.Heading + degrees };
                ApplyPlaced(picked, turned);
                picked = turned;
            }
            else
                _place.Turn(degrees);
        }

        if (Input.KeyUp(GlfwKey.R))
        {
            if (_wheelFrom is { } before && picked is not null)
            {
                _undo.Push(new PlacedEdit(before, picked));
                _redo.Clear();
            }

            _wheelFrom = null;
            if (!_wheeled)
            {
                if (picked is { Kind: not PlacedKind.HandThrow })
                    PlaceTurn(picked, shift ? -15.0 : 15.0);
                else if (picked is null && _place.Mode == PlaceMode.Track)
                    _place.Turn(shift ? -15.0 : 15.0);
            }
        }

        if (picked is null && _place.Mode == PlaceMode.Track && Input.KeyDown(GlfwKey.V))
            _place.Mirror = !_place.Mirror;

        if (picked is null)
            return;
        if (Input.KeyDown(GlfwKey.V))
            PlaceFlip(picked);
        if (Input.KeyDown(GlfwKey.Delete))
            PlacedChange(picked, null);
    }

    /// A signal or crossing turned right round; a hand throw to the switch's other side.
    private void PlaceFlip(PlacedObject o) => PlacedChange(o, o.Kind == PlacedKind.HandThrow ? o with { Opposite = !o.Opposite } : o with { Heading = o.Heading + 180.0 });

    private void PlacePanel(TerrainSource source)
    {
        if (_switchRefused is { } refused)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Style.Stop);
            ImGui.TextWrapped(refused);
            ImGui.PopStyleColor();
        }

        if (source.Objects.FirstOrDefault(o => o.Id == _place.Selected) is { } picked)
            PlacePicked(picked);
        if (_place.Mode == PlaceMode.Pole)
            PolePanel();
        PlacePalette(source);
    }

    /// What can be done to the object picked.
    private void PlacePicked(PlacedObject picked)
    {
        ImGui.SeparatorText(picked.Kind.Name());
        if (picked.Kind == PlacedKind.GradeCrossing)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var width = imperial ? picked.Width * FeetPerMeter : picked.Width;
            if (ImGui.InputFloat(imperial ? "Road (ft)"u8 : "Road (m)"u8, ref width, 1f, 5f, "%.0f"u8, ImGuiInputTextFlags.EnterReturnsTrue))
                PlacedChange(picked, picked with { Width = Math.Clamp(imperial ? width / FeetPerMeter : width, PlaceTool.MinCrossingWidth, PlaceTool.MaxCrossingWidth) });
        }

        if (picked.Kind != PlacedKind.HandThrow)
        {
            if (ImGui.Button("Turn 15"u8))
                PlaceTurn(picked, 15.0);
            ImGui.SameLine(0f, -1f);
        }

        if (ImGui.Button(picked.Kind == PlacedKind.HandThrow ? "Other Side"u8 : "Turn Round"u8))
            PlaceFlip(picked);
        ImGui.SameLine(0f, -1f);
        if (ImGui.Button("Delete"u8))
            PlacedChange(picked, null);
    }

    // --- Following guides. ---

    /// What a click at this place would be: on a guide (right at its node, if that's on built track, it joins there), or
    /// on built track.
    private FollowTool.Click? FollowClick(GuidePlace? guide, (long Way, int Segment, double X, double Z)? segment)
    {
        if (guide is { } place)
        {
            if (_guides!.NodeAt(place, FollowSnap) is { } node && _tracks!.Has(node))
            {
                var (nx, nz) = _tracks.PositionOf(node);
                return new FollowTool.Click(nx, nz, place, new LayPoint(nx, nz, Node: node), _tracks.RunsAt(node));
            }

            // A guide's place on built track joins it there: a branch off it.
            if (_tracks!.TrackNear(place.X, place.Z, FollowOnTrack, FollowAtPoint) is { } track)
                return new FollowTool.Click(track.Point.X, track.Point.Z, place, track.Point, track.Runs);

            return new FollowTool.Click(place.X, place.Z, place, new LayPoint(place.X, place.Z));
        }

        if (segment is not { } s)
            return null;
        return _tracks!.TrackNear(s.X, s.Z, FollowOnTrack, FollowAtPoint) is { } on
            ? new FollowTool.Click(on.Point.X, on.Point.Z, null, on.Point, on.Runs)
            : new FollowTool.Click(s.X, s.Z, null, new LayPoint(s.X, s.Z, Split: (s.Way, s.Segment)));
    }

    /// How near a click on a guide has to be to its node to join what's there.
    private const double FollowSnap = 4.0;

    /// How near a click on a guide has to be to built track to join it, and to one of its points to join there.
    private const double FollowOnTrack = 2.5;
    private const double FollowAtPoint = 2.5;

    /// A Shift-drag marks an area on the ground; a Shift-click drops it.
    private bool BoxUpdate(float2 mouse, bool pressed, bool held)
    {
        if (pressed)
        {
            (_areaFrom, _area) = (mouse, null);
            return true;
        }

        if (_areaFrom is not { } from)
            return false;

        var (dx, dy) = (mouse.X - from.X, mouse.Y - from.Y);
        if (MathF.Abs(dx) > S(6f) && MathF.Abs(dy) > S(6f) && Pick(from) is { } a && Pick(new float2(mouse.X, from.Y)) is { } b && Pick(mouse) is { } c && Pick(new float2(from.X, mouse.Y)) is { } d)
            _area = new GroundQuad((a.X, a.Z), (b.X, b.Z), (c.X, c.Z), (d.X, d.Z));
        if (held)
            return true;

        _areaFrom = null;
        if (_area is { } box)
            _areaLength = _tracks!.LengthIn(box.Contains);
        return true;
    }

    private void BoxSet(bool guide)
    {
        if (_area is not { } box)
            return;
        _follow.Clear();
        _area = null;
        Committed(_tracks!.SetGuide(box.Contains, guide));
    }

    private void FollowUpdate()
    {
        var blocked = Blocked;
        var mouse = Input.MousePosition;
        var shift = Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift);

        if (_yard.Mode != YardMode.Follow)
        {
            YardUpdate(blocked);
            return;
        }

        var click = blocked ? null : FollowClick(_guides!.PlaceAt(mouse, ProjectGround, _camera.X, _camera.Z, ViewReach, S(12f)), _tracks!.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround));
        click ??= !blocked && _hit is { } ground ? new FollowTool.Click(ground.X, ground.Z, null, new LayPoint(ground.X, ground.Z)) : null;
        _follow.Update(_guides!, click);
        if (blocked || _rebuild is not null)
            return;
        if (Input.MouseDown(GlfwMouseButton.Left) && click is { } placed)
            _follow.Add(placed);
        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Backspace))
            _follow.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter))
            FollowLay();
    }

    private string PanelLength(double metres) => Settings.Current.Units == UnitSystem.Imperial
        ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0.#} ft")
        : string.Create(CultureInfo.InvariantCulture, $"{metres:0.#} m");

    private void YardUpdate(bool blocked)
    {
        if (_terrain?.Source.Lines is not { } lines)
            return;
        ActiveYard.Update(_tracks!, lines, !blocked && _hit is { } ground ? (ground.X, ground.Z) : null, PanelLength);
        if (blocked || _rebuild is not null)
            return;
        if (Input.MouseDown(GlfwMouseButton.Left) && _hit is { } hit)
            YardPress(lines, hit.X, hit.Z);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
            ActiveYard.Clear();
    }

    /// A click: the first picks the track to start from, the second lays what's shown if it can be.
    private void YardPress(NetworkLines lines, double x, double z)
    {
        var yard = ActiveYard;
        if (yard.Anchor is null)
        {
            yard.Start(_tracks!, x, z);
            return;
        }

        yard.Update(_tracks!, lines, (x, z), PanelLength);
        if (yard.Plan is not { Problem: null } plan || _tracks!.LayWays(plan.Ways) is not { } edit)
            return;
        if (yard.Mode == YardMode.Crossover && yard.Size != 0 && edit.Ways.Select(w => w.After).FirstOrDefault(w => w is not null && w.Nodes.Length == plan.Ways[0].Points.Count && w.Tags.ContainsKey(RouteNetwork.ExactTag)) is { } crossover)
            SwitchSizes.Set(_route, crossover.Nodes[0], yard.Size);
        yard.Clear();
        Committed(edit);
    }

    private void YardControls()
    {
        foreach (var (mode, name) in ((YardMode, string)[])[(YardMode.Follow, "Follow"), (YardMode.Crossover, "Crossover"), (YardMode.Ladder, "Ladder")])
        {
            if (mode is YardMode.Crossover or YardMode.Ladder)
                ImGui.SameLine(0f, -1f);
            if (ImGui.RadioButton(name, _yard.Mode == mode))
            {
                _yard.SetMode(mode);
                _follow.Clear();
            }
        }

        if (_yard.Mode == YardMode.Follow)
            return;

        if (_yard.Mode != YardMode.Crossover)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var spacing = imperial ? _yard.Spacing * FeetPerMeter : _yard.Spacing;
            if (ImGui.InputFloat(imperial ? "Spacing (ft)"u8 : "Spacing (m)"u8, ref spacing, 0.1f, 0.5f, "%.1f"u8, ImGuiInputTextFlags.None))
                _yard.Spacing = Math.Clamp(imperial ? spacing / FeetPerMeter : spacing, YardTool.MinSpacing, YardTool.MaxSpacing);
        }

        if (_yard.Mode == YardMode.Ladder)
        {
            var tracks = (float)_yard.Tracks;
            if (ImGui.InputFloat("Tracks"u8, ref tracks, 1f, 2f, "%.0f"u8, ImGuiInputTextFlags.None))
                _yard.Tracks = Math.Clamp((int)MathF.Round(tracks), 1, YardTool.MaxTracks);
        }

        if (_yard.Mode != YardMode.Parallel)
        {
            if (_yard.Mode == YardMode.Crossover && ImGui.RadioButton("Automatic##yardsize"u8, _yard.Size == 0))
                _yard.Size = 0;
            var first = _yard.Mode == YardMode.Ladder;
            foreach (var (number, _) in Turnout.Sizes)
            {
                if (_yard.Mode == YardMode.Ladder && number > 10)
                    continue;
                if (!first)
                    ImGui.SameLine(0f, -1f);
                first = false;
                var picked = _yard.Size == number || (_yard.Mode == YardMode.Ladder && _yard.Size == 0 && number == 8);
                if (ImGui.RadioButton($"No. {number}##yardsize", picked))
                    _yard.Size = number;
            }
        }

        if (_yard.Plan is { } plan)
        {
            if (plan.Problem is { } problem)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Style.Stop);
                ImGui.TextWrapped(problem);
                ImGui.PopStyleColor();
            }
            else
                ImGui.TextWrapped(plan.Summary);
        }
    }

    private void FollowLay()
    {
        _follow.Update(_guides!, null);
        if (_follow.Points() is { } points)
        {
            Committed(_tracks!.Lay(points, lead: false, exact: true));
            _follow.Clear();
        }
    }

    /// The route's yards, to clear for laying by hand or put back to be generated; and how much of the guides is done.
    private void FollowPanel()
    {
        if (_guides is not { } guides)
            return;
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        string Far(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter / 5280.0:0.0} mi") : string.Create(CultureInfo.InvariantCulture, $"{metres / 1000.0:0.0} km");
        string Near(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{metres:0} m");

        ImGui.BeginDisabled(_rebuild is not null);
        YardControls();
        ImGui.EndDisabled();
        if (_follow.Preview is not null)
        {
            ImGui.Text($"{Near(_follow.Length)}{(_follow.Unfitted ? ", as mapped: no clean fit" : _follow.Radius < double.MaxValue ? $", tightest curve {Near(_follow.Radius)}" : ", straight")}");
        }

        if (guides.GuideLength > 0.0)
            ImGui.Text($"Guides laid: {Far(guides.DoneLength)} of {Far(guides.GuideLength)}");

    }

    // --- Shaping track. ---

    private void ShapeUpdate(TerrainSource source)
    {
        if (source.Lines is not { } lines)
            return;
        var blocked = Blocked;
        var keys = !Keyboard.KeysBlocked && !blocked;
        var change = _shape.Update(lines, Input.MousePosition, blocked || _hit is not { } hit ? null : (hit.X, hit.Z), Project, S(1f), _camera.X, _camera.Z, ViewReach,
            !blocked && Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left),
            Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift), Input.Key(GlfwKey.LeftAlt) || Input.Key(GlfwKey.RightAlt),
            keys && Input.KeyDown(GlfwKey.Delete), keys && Input.KeyDown(GlfwKey.L), _rebuild is not null);
        if (change is { } made)
            CommitShape(source, made.Line, made.Shape);
    }

    private void CommitShape(TerrainSource source, NetworkLine line, Alignment? shape)
    {
        var edit = new TrackShapeEdit(line.Key, source.TrackEdits.Get(line.Key), shape);
        source.TrackEdits.Set(line.Key, shape);
        _undo.Push(edit);
        _redo.Clear();
        RebuildNetwork();
    }

    private readonly MapTiles _backgroundTiles = new();
    private bool _showBackground;
    private float _backgroundOpacity = 0.7f;

    /// The map or satellite picture laid over the ground in the 2D view, each tile where its corners fall on the ground.
    private void DrawBackground(ImDrawListPtr draw)
    {
        if (!_topDown || !_showBackground || _terrain is null || _route.Projection is not { } projection)
            return;
        _backgroundTiles.BeginFrame();

        // The tile size nearest a pixel of it to a pixel of screen.
        var size = ImGui.GetIO().DisplaySize;
        var centre = projection.Inverse(new RoutePoint(_camera.X, -_camera.Z));
        var (cx, cy) = MapView.ToMercator(centre);
        if (Pick(size * 0.5f) is not { } middle || Pick(size * 0.5f + new float2(S(100f), 0f)) is not { } beside)
            return;
        var metresPerPixel = Math.Sqrt(Math.Pow(beside.X - middle.X, 2.0) + Math.Pow(beside.Z - middle.Z, 2.0)) / S(100f);
        var cosine = Math.Cos(centre.Lat * Math.PI / 180.0);
        var zoom = Math.Clamp((int)Math.Round(Math.Log2(40075016.0 * cosine / (256.0 * Math.Max(metresPerPixel, 0.01)))), 2, MapTiles.MaxZoom);
        var n = 1 << zoom;
        var reach = 0.6 * (Math.Max(size.X, size.Y) * metresPerPixel) / (40075016.0 * cosine);
        var (x0, x1) = ((int)Math.Floor((cx - reach) * n), (int)Math.Floor((cx + reach) * n));
        var (y0, y1) = ((int)Math.Floor((cy - reach) * n), (int)Math.Floor((cy + reach) * n));
        if ((x1 - x0 + 1) * (y1 - y0 + 1) > BackgroundMaxTiles)
            return;

        var tint = new Rgba(1f, 1f, 1f, _backgroundOpacity);
        float2? Corner(int x, int y)
        {
            var p = projection.Forward(MapView.FromMercator(x / (double)n, y / (double)n));
            return ProjectGround(p.East, -p.North);
        }

        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (_backgroundTiles.Get(zoom, x, y) is { } texture && Corner(x, y) is { } a && Corner(x + 1, y) is { } b && Corner(x + 1, y + 1) is { } c && Corner(x, y + 1) is { } d)
                    draw.AddImageQuad(texture, a, b, c, d, new float2(0f, 0f), new float2(1f, 0f), new float2(1f, 1f), new float2(0f, 1f), tint);

        DrawTrackOverBackground(draw, metresPerPixel, Math.Max(size.X, size.Y) * metresPerPixel * 0.6);
    }

    /// The background is drawn over the world, so laid track and the marks are drawn again on top of it, at their real
    /// width where that's more than a line.
    private void DrawTrackOverBackground(ImDrawListPtr draw, double metresPerPixel, double reach)
    {
        if (_terrain?.Source.Lines is not { } lines)
            return;
        var stride = Math.Max(1, (int)(metresPerPixel * 2.0));
        var (track, mark) = (Style.Text, new Rgba(0.78f, 0.66f, 0.46f, 0.9f));
        foreach (var line in lines.Lines)
        {
            if ((!line.Rail && !line.Mark) || line.MaxX < _camera.X - reach || line.MinX > _camera.X + reach || line.MaxZ < _camera.Z - reach || line.MinZ > _camera.Z + reach)
                continue;
            var color = line.Mark ? mark : track;
            var width = MathF.Max(line.Mark ? 1.5f : 1f, (float)((line.Mark ? line.Width : 1.8f) / metresPerPixel));
            float2? previous = null;
            for (var p = 0; p < line.Count; p += p + stride < line.Count || p == line.Count - 1 ? stride : line.Count - 1 - p)
            {
                var at = ProjectGround(line.X[p], line.Z[p]);
                if (previous is { } from && at is { } to)
                    draw.AddLine(from, to, color, width);
                previous = at;
            }
        }
    }


    private const int BackgroundMaxTiles = 200;

    /// A scripted Shape tool step (--shapeops), for testing.
    private void ShapeOp(TerrainSource source, string op)
    {
        var parts = op.Split(',');
        double Number(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        (double X, double Z) At(int i)
        {
            var p = _route.Projection!.Forward(new GeoPoint(Number(i), Number(i + 1)));
            return (p.East, -p.North);
        }

        var lines = source.Lines!;
        (NetworkLine Line, List<int> Handles, int Point)? Nearest((double X, double Z) at, bool free)
        {
            (NetworkLine, List<int>, int)? found = null;
            var best = 90000.0;
            foreach (var line in lines.Lines.Where(l => l.Rail && !l.Disused && l.Alignment is not null))
            {
                if (at.X < line.MinX - 20 || at.X > line.MaxX + 20 || at.Z < line.MinZ - 20 || at.Z > line.MaxZ + 20)
                    continue;
                var handles = TrackShaping.Handles(line, lines.IsJunction, []);
                for (var p = 0; p < handles.Count; p++)
                {
                    var q = handles[p];
                    var d = (line.X[q] - at.X) * (line.X[q] - at.X) + (line.Z[q] - at.Z) * (line.Z[q] - at.Z);
                    if (d < best && (!free || (p > 0 && p < handles.Count - 1 && !line.Nodes.Any(n => n.Point == q && lines.IsJunction(n.Node)))))
                        (best, found) = (d, (line, handles, q));
                }
            }

            return found;
        }

        void Report(string what, NetworkLine line, TrackShaping.Result? result)
        {
            Log.Info(result is null ? $"Shape {what}: refused" : string.Create(CultureInfo.InvariantCulture, $"Shape {what}: line {line.Index}, tightest curve {result.Radius:0} m, {result.OffMap:0.00} m off the map, {result.Shape.Pieces.Count} pieces"));
            if (result is not null)
                CommitShape(source, line, result.Shape);
        }

        switch (parts[0])
        {
            case "clearyard" or "restoreyard":
            {
                if (_guides!.Areas.MinBy(a => a.Ways.Min(w => source.Network.Ways[w].Lat.Select((la, i) => Math.Abs(la - Number(1)) + Math.Abs(source.Network.Ways[w].Lon[i] - Number(2))).Min())) is { } area)
                {
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Yard {parts[0]}: {area.Ways.Count} ways, {area.Length:0} m, {area.Junctions} junctions, of {_guides.Areas.Count} yards"));
                    Committed(_tracks!.SetGuide(area.Ways, parts[0] == "clearyard"));
                }

                break;
            }

            case "switchcurve" or "switchslide":
            {
                // Through the handles: a press on one, a drag this far along its track, and letting go.
                var (sx, sz) = At(1);
                if (lines.Turnouts.Where(t => SwitchHandles.Editable(lines, t)).MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is not { } t)
                    break;
                _tool = Tool.Switches;
                _switch = t.Node;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (t.X, source.HeightAt(t.X, t.Z) + 300.0, t.Z + 40.0, -1.3f);
                var metres = (float)Number(3);
                var curve = parts[0] == "switchcurve";
                (double X, double Z)? grab = curve ? SwitchHandles.CurveHandle(lines, t) : (t.X, t.Z);
                if (grab is not { } g || ProjectGround(g.X, g.Z) is not { } press)
                {
                    Log.Warn($"Switch {t.Node}: no handle");
                    break;
                }

                var (tx, tz, _, _, _, _, _) = curve ? t.Reverse.At(metres) : metres >= 0f ? t.Normal.At(metres) : t.Toe.At(-metres);
                var target = ProjectGround(tx, tz) ?? press;
                var before = lines.SwitchOptions.GetValueOrDefault(t.Node);
                _handles.Update(lines, t, press, (g.X, g.Z), ProjectGround, S(1f), true, true);
                _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, true);
                var preview = _handles.Curve;
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0},{t.Z:0}: No. {t.Number}, curve ends {before?.Reach:0} m; drag {parts[0]} {metres:0} m: preview No. {preview?.Number}, radius {preview?.Radius:0}, fits {preview?.Fits}, slid {_handles.Slid:0}"));
                SwitchChange(lines, t, _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, false));
                break;
            }

            case "alongview":
            {
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                _tool = Tool.Switches;
                (_camera.X, _camera.Z, _camera.Pitch) = (ax, az, -0.08f);
                _camera.Y = source.HeightAt(ax, az) + Number(5);
                _camera.Yaw = MathF.Atan2((float)(bx - ax), (float)(az - bz));
                break;
            }

            case "switchshow":
            {
                var (sx, sz) = At(1);
                if (lines.Turnouts.MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is { } t)
                {
                    var options = lines.SwitchOptions.GetValueOrDefault(t.Node);
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0.0},{t.Z:0.0}: No. {t.Number}, curve ends {options?.Reach:0.0} m, by hand {options?.ReachByHand}, frog {t.FrogNormal:0.0} m, normal line {t.Normal.Line.Index}{(t.Normal.Line.Exact ? " laid" : "")}, reverse line {t.Reverse.Line.Index}{(t.Reverse.Line.Exact ? " laid" : "")}, {lines.Turnouts.Count} switches"));
                }

                break;
            }

            case "boxdrag":
            {
                // Through the tool itself: a press, a drag and letting go, at the screen places of these two ground points.
                var (bx, bz) = At(1);
                var (ex, ez) = At(3);
                if (ProjectGround(bx, bz) is not { } press || ProjectGround(ex, ez) is not { } release)
                {
                    Log.Warn("Box: not in view");
                    break;
                }

                _tool = Tool.Follow;
                BoxUpdate(press, true, true);
                BoxUpdate(release, false, true);
                BoxUpdate(release, false, false);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Box: {_area is not null}, {_areaLength.Built:0} m built, {_areaLength.Cleared:0} m cleared"));
                break;
            }

            case "boxclear" or "boxgenerate":
            {
                var before = source.Network.Ways.Count;
                BoxSet(parts[0] == "boxclear");
                Log.Info($"Box {parts[0]}: {before} ways before, {source.Network.Ways.Count} after");
                break;
            }

            case "parallel" or "crossover" or "ladder":
            {
                // Through the tool itself: a click on the track, the pointer and a second click at the other place.
                _tool = parts[0] == "parallel" ? Tool.Parallel : Tool.Follow;
                var yard = ActiveYard;
                if (parts[0] != "parallel")
                    yard.SetMode(parts[0] == "crossover" ? YardMode.Crossover : YardMode.Ladder);
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                if (parts[0] == "parallel")
                    (yard.Spacing, yard.ParallelTracks) = ((float)Number(5), parts.Length > 6 ? (int)Number(6) : 1);
                else if (parts[0] == "crossover")
                    yard.Size = (int)Number(5);
                else
                    (yard.Tracks, yard.Spacing, yard.Size) = ((int)Number(5), (float)Number(6), (int)Number(7));
                var started = yard.Start(_tracks!, ax, az);
                yard.Update(_tracks!, lines, (bx, bz), PanelLength);
                var planned = yard.Plan;
                Log.Info($"Yard {parts[0]}: started {started}, {planned?.Summary}, problem: {planned?.Problem ?? "none"}, {planned?.Ways.Count} ways");
                YardPress(lines, bx, bz);
                if (parts[0] != "parallel")
                    yard.SetMode(YardMode.Follow);
                break;
            }

            case "place":
            {
                // Through the tool: the mode, and a click on the ground here.
                _tool = Tool.Place;
                _place.Mode = parts[1] switch { "signal" => PlaceMode.Signal, "handthrow" => PlaceMode.HandThrow, "crossing" => PlaceMode.GradeCrossing, _ => PlaceMode.Diamond };
                var (px, pz) = At(2);
                _place.Selected = null;
                _place.Update(lines, _tracks!, (px, pz), PanelLength);
                var note = _place.Note;
                PlacePress(source, lines, px, pz);
                var made = source.Objects.FirstOrDefault(o => o.Id == _place.Selected);
                Log.Info($"Place {parts[1]}: snapped {_place.Snapped is not null}, {note}, refused: {_switchRefused ?? "no"}, placed {made?.Kind}, {source.Objects.Count} objects");
                break;
            }

            case "pole":
            {
                // Through the tool: pole,kind,snap,offset,gap (0: no fence), then a lat,lon for each click.
                _tool = Tool.Place;
                _place.Mode = PlaceMode.Pole;
                _place.PoleKind = Enum.Parse<PlacedKind>(parts[1]);
                _place.Snap = Enum.Parse<PoleSnap>(parts[2]);
                if (_place.Snap == PoleSnap.Road)
                    _place.RoadOffset = (float)Number(3);
                else
                    _place.TrackOffset = (float)Number(3);
                _place.Fence = Number(4) > 0.0;
                if (_place.Fence)
                    _place.Gap = (float)Number(4);
                (_place.RunFrom, _place.RunFirst) = (null, null);
                for (var i = 5; i + 1 < parts.Length; i += 2)
                {
                    var (px, pz) = At(i);
                    _switchRefused = null;
                    _place.Update(lines, _tracks!, (px, pz), PanelLength);
                    var note = _place.Note;
                    var before = source.Objects.Count;
                    PolePress(lines, px, pz);
                    Log.Info($"Pole {parts[1]} {parts[2]}: {note}, refused: {_switchRefused ?? "no"}, {source.Objects.Count - before} made, {source.Objects.Count} objects");
                }

                foreach (var made in source.Objects.Where(o => o.Kind.IsPole()).TakeLast(4))
                {
                    var (poleX, poleZ) = PlacedAtWorld(made);
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Pole {made.Kind} at {poleX:0.0} {poleZ:0.0} heading {made.Heading:0.0}"));
                }

                (_place.RunFrom, _place.RunFirst) = (null, null);
                break;
            }

            case "stamp":
            {
                // Through the tool: a piece from the palette, how it's turned and mirrored, and a click on the ground here.
                _tool = Tool.Place;
                _place.Mode = PlaceMode.Track;
                _place.Rebuild(Settings.Current.Units == UnitSystem.Imperial);
                _place.Stamp = _place.Catalogue.FirstOrDefault(c => c.Id == parts[1]);
                var (sx, sz) = At(2);
                (_place.Heading, _place.Mirror) = (parts.Length > 4 ? Number(4) * Math.PI / 180.0 : 0.0, parts.Length > 5 && parts[5] == "m");
                _place.Update(lines, _tracks!, (sx, sz), PanelLength);
                var plan = _place.Plan;
                Log.Info($"Stamp {parts[1]}: {plan?.Summary}, problem: {plan?.Problem ?? "none"}, {plan?.Ways.Count} ways, {plan?.Switches.Length} switches");
                LayStamp(lines, sx, sz);
                break;
            }

            case "wireframe":
                _renderer.Wireframe = parts[1] == "on";
                break;

            case "stampcheck":
            {
                // What's come of the pieces laid: every switch with its size, and every junction with a problem.
                var (cx, cz) = At(1);
                var within = Number(3);
                foreach (var t in lines.Turnouts.Where(t => Math.Abs(t.X - cx) < within && Math.Abs(t.Z - cz) < within))
                    Log.Info($"Stamp check: switch {t.Node} No. {t.Number?.ToString() ?? "none"}, modelled {t.Modelled}");
                foreach (var l in lines.Layouts.Where(l => Math.Abs(l.X - cx) < within && Math.Abs(l.Z - cz) < within))
                    Log.Info($"Stamp check: {TrackLayouts.Name(l.Kind, l.Number ?? 0)} at {_route.Projection!.Inverse(new RoutePoint(l.X, -l.Z)).Lat:0.0000}, issue: {l.Issue ?? "none"}");
                break;
            }

            case "placeturn" or "placeflip" or "placedelete" or "placemove":
            {
                if (source.Objects.LastOrDefault() is not { } last)
                    break;
                if (parts[0] == "placeturn")
                    PlaceTurn(last, Number(1));
                else if (parts[0] == "placeflip")
                    PlaceFlip(last);
                else if (parts[0] == "placedelete")
                    PlacedChange(last, null);
                else
                {
                    var (toX, toZ) = At(1);
                    PlaceMove(lines, last, toX, toZ);
                }

                Log.Info($"Place {parts[0]}: {source.Objects.Count} objects");
                break;
            }

            case "throw":
            {
                var (tx, tz) = At(1);
                _network!.Throw(tx, tz, 40.0);
                break;
            }

            case "laycurve":
            {
                var (fromX, fromZ) = At(1);
                var (endX, endZ) = At(3);
                var (midX, midZ) = ((fromX + endX) / 2.0, (fromZ + endZ) / 2.0);
                var (ux, uz) = (endX - fromX, endZ - fromZ);
                var length = Math.Sqrt(ux * ux + uz * uz);
                var bow = Number(5);
                var radius = (length * length / 4.0 + bow * bow) / (2.0 * Math.Abs(bow));
                var (cx, cz) = (midX - uz / length * (radius - Math.Abs(bow)) * Math.Sign(bow), midZ + ux / length * (radius - Math.Abs(bow)) * Math.Sign(bow));
                var (a0, a1) = (Math.Atan2(fromZ - cz, fromX - cx), Math.Atan2(endZ - cz, endX - cx));
                var sweep = Math.IEEERemainder(a1 - a0, 2.0 * Math.PI);
                var count = Math.Max(2, (int)(radius * Math.Abs(sweep) / 2.0));
                Committed(_tracks!.Lay([.. Enumerable.Range(0, count + 1).Select(k => new LayPoint(cx + radius * Math.Cos(a0 + sweep * k / count), cz + radius * Math.Sin(a0 + sweep * k / count)))], lead: false, exact: true));
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Yard curve: radius {radius:0} m"));
                break;
            }

            case "laystraight":
            {
                var (fromX, fromZ) = At(1);
                var (endX, endZ) = At(3);
                var count = Math.Max(2, (int)(Math.Sqrt((endX - fromX) * (endX - fromX) + (endZ - fromZ) * (endZ - fromZ)) / 2.0));
                Committed(_tracks!.Lay([.. Enumerable.Range(0, count + 1).Select(k => new LayPoint(fromX + (endX - fromX) * k / count, fromZ + (endZ - fromZ) * k / count))], lead: false, exact: true));
                break;
            }

            case "linesdump":
            {
                // Every built track's line, with its junctions, for working out a hand re-lay.
                using var dump = new StreamWriter(parts[1]);
                foreach (var l in lines.Lines.Where(l => l.Rail && !l.Disused && l.Alignment is not null))
                {
                    var handles = TrackShaping.Handles(l, lines.IsJunction, []);
                    var junctions = l.Nodes.Where(n => lines.IsJunction(n.Node)).Select(n => n.Point).ToHashSet();
                    dump.WriteLine(string.Create(CultureInfo.InvariantCulture, $"L {l.Index} {l.Count} {l.S[^1]:0.0} {(l.Exact ? 1 : 0)} {string.Join(';', handles)} {string.Join(';', junctions)}"));
                    for (var p = 0; p < l.Count; p++)
                    {
                        var g = _route.Projection!.Inverse(new RoutePoint(l.X[p], -l.Z[p]));
                        dump.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{l.X[p]:0.000} {l.Z[p]:0.000} {l.S[p]:0.00} {g.Lat:0.00000000} {g.Lon:0.00000000}"));
                    }
                }

                Log.Info("Lines dumped");
                break;
            }

            case "road":
            {
                // road,KIND,MODE,lat,lon,lat,lon[,lat,lon...]: each point a click.
                _tool = Tool.Road;
                _road.Clear();
                (_road.Kind, _road.Mode) = (int.Parse(parts[1], CultureInfo.InvariantCulture), Enum.Parse<RoadMode>(parts[2], true));
                for (var i = 3; i + 1 < parts.Length; i += 2)
                {
                    var (rx, rz) = At(i);
                    if (_road.Click(Roads.Snap(rx, rz, 5.0)) is { } laid)
                        Committed(Roads.Lay(laid, RoadTool.Kinds[_road.Kind]));
                }

                Log.Info($"Road: laid, {source.Network.Ways.Count} ways");
                break;
            }

            case "roadoff":
            {
                var (rx, rz) = At(1);
                var picked = Roads.Nearest(rx, rz, 40.0, RoadEditor.IsRoad);
                Log.Info($"Road off: {picked?.Way}");
                if (picked is { } off)
                    Committed(Roads.Remove(off.Way));
                break;
            }

            case "bridge":
            {
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                bool Any(NetworkWay w) => !w.Tags.ContainsKey(RouteNetwork.RemovedTag);
                if (Roads.Nearest(ax, az, 40.0, Any) is { } first && Roads.Nearest(bx, bz, 40.0, Any) is { } second)
                {
                    var made = Roads.AddBridge(first, second, out var refused);
                    Log.Info($"Bridge: {(made is null ? refused ?? "nothing" : $"{made.Ways.Count} ways changed")}");
                    Committed(made);
                }

                break;
            }

            case "flattensample":
            {
                // flattensample,lat,lon: as F over that place with Flatten in hand.
                var (fx0, fz0) = At(1);
                SampleFlatten(source, fx0, fz0);
                Log.Info($"Flatten sample: {FlattenSampleText}");
                break;
            }

            case "lens":
                // lens,ZOOM: as rolling the wheel to that zoom in the 3D Editor.
                _camera.Zoom = Math.Clamp((float)Number(1), 1f, MaxLensZoom);
                break;

            case "splitat":
            {
                // splitat,lat,lon: the control point nearest there selected, and U.
                var (sx0, sz0) = At(1);
                _selection.Clear();
                foreach (var near in _tracks!.NodesNear(sx0, sz0, 3.0).Take(1))
                    _selection.Add(near);
                SplitTrack(_tracks);
                Log.Info($"Split: refused {_switchRefused ?? "no"}, {source.Network.Ways.Count} ways");
                break;
            }

            case "bridgeclick":
            {
                // bridgeclick,lat,lon: as a click with Place bridge in hand.
                var (cx, cz) = At(1);
                BridgeClick(cx, cz);
                Log.Info($"Bridge click: {(_bridgeFrom is null ? "finished" : "begun")} {_switchRefused}");
                break;
            }

            case "unbridge":
            {
                var (ax, az) = At(1);
                var found = Roads.Nearest(ax, az, 40.0, RoadEditor.IsBridge);
                Log.Info($"Unbridge: {found?.Way}");
                if (found is { } bridged)
                    Committed(Roads.RemoveBridge(bridged.Way));
                break;
            }

            case "bed":
            {
                // bed,lat,lon,KIND[,HEIGHT]: the rail way nearest there.
                var (bx0, bz0) = At(1);
                var hit = Roads.Nearest(bx0, bz0, 60.0, w => w.Rail);
                Log.Info($"Bed: {parts[3]} on way {hit?.Way}");
                if (hit is { } on)
                    Committed(_tracks!.SetBed([on.Way], parts[3], parts.Length > 4 ? (float)Number(4) : null));
                break;
            }

            case "background":
                (_showBackground, MapTiles.Background, _backgroundOpacity) = (parts[1] != "off", parts[1] == "satellite" ? MapBackground.Satellite : MapBackground.Map, 1f);
                SetTopDown(true);
                break;
            case "yardlist":
            {
                var (lx, lz) = At(1);
                foreach (var l in lines.Layouts.Where(l => (l.X - lx) * (l.X - lx) + (l.Z - lz) * (l.Z - lz) < Number(3) * Number(3)))
                    Log.Info($"Layout {l.Kind} at {l.X:0.0},{l.Z:0.0}: {l.Issue}");
                foreach (var d in lines.CrossingAssemblies)
                {
                    var g = _route.Projection!.Inverse(new RoutePoint(d.X, -d.Z));
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Diamond at {g.Lat:0.000000},{g.Lon:0.000000}: {d.Degrees:0.0} degrees, along {d.Range[0].To - d.Range[0].From:0.0} and {d.Range[1].To - d.Range[1].From:0.0} m"));
                }

                foreach (var t in lines.Turnouts.Where(t => (t.X - lx) * (t.X - lx) + (t.Z - lz) * (t.Z - lz) < Number(3) * Number(3)))
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0.0},{t.Z:0.0}: No. {t.Number}, frog {t.FrogNormal:0.0} m, {lines.Layouts.FirstOrDefault(l => l.Nodes.Contains(t.Node))?.Kind}{(t.Reverse.Line.Exact ? ", laid" : "")}"));
                break;
            }

            case "crossoverslide":
            {
                // Through the handles: a press on the crossover's middle, a drag this far along it, and letting go.
                var (sx, sz) = At(1);
                if (lines.Turnouts.Where(t => SwitchHandles.Partner(lines, t) is not null).MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is not { } t || SwitchHandles.CrossoverHandle(lines, t) is not { } middle)
                {
                    Log.Warn("Crossover: none here");
                    break;
                }

                _tool = Tool.Switches;
                _switch = t.Node;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (middle.X, source.HeightAt(middle.X, middle.Z) + 300.0, middle.Z + 40.0, -1.3f);
                var partner = SwitchHandles.Partner(lines, t)!;
                var (ux, uz) = (partner.X - t.X, partner.Z - t.Z);
                var norm = Math.Max(Math.Sqrt(ux * ux + uz * uz), 1e-9);
                var metres = Number(3);
                var (tx, tz) = (middle.X + ux / norm * metres, middle.Z + uz / norm * metres);
                if (ProjectGround(middle.X, middle.Z) is not { } press)
                    break;
                var target = ProjectGround(tx, tz) ?? press;
                _handles.Update(lines, t, press, middle, ProjectGround, S(1f), true, true);
                _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, true);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Crossover {t.Node} and {partner.Node}: No. {t.Number}, drag {metres:0} m, slid {_handles.Slid:0.0} m, {lines.Layouts.Count(l => l.Kind == JunctionKind.Crossover)} crossovers"));
                SwitchChange(lines, t, _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, false));
                break;
            }

            case "follow":
            {
                _tool = Tool.Follow;
                _follow.Clear();
                for (var k = 1; k + 1 < parts.Length; k += 2)
                {
                    var (fx, fz) = At(k);
                    if (FollowClick(_guides!.PlaceNear(fx, fz, 60.0), null) is { } placed)
                        _follow.Add(placed);
                }

                _follow.Update(_guides!, null);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Follow: {_follow.Length:0} m, tightest curve {_follow.Radius:0} m, fitted {!_follow.Unfitted}, {_follow.Preview?.Count} pieces"));
                FollowLay();
                break;
            }

            case "view":
                _tool = Tool.Follow;
                (_camera.X, _camera.Z) = At(1);
                _camera.Pitch = -1.3f;
                _camera.Z += 8.0;
                _camera.Y = source.HeightAt(_camera.X, _camera.Z) + Number(3);
                break;
            case "drag" when Nearest(At(1), true) is { } h:
            {
                // Through the tool itself: a click on the line, on its handle, a drag to the new place, and letting go.
                var y = h.Line.Y[h.Point] + NetworkLine.BallastHeight + 0.2;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (h.Line.X[h.Point], y + 40.0, h.Line.Z[h.Point] + 8.0, -1.3f);
                if (Project(h.Line.X[h.Point], y, h.Line.Z[h.Point]) is not { } screen)
                {
                    Log.Warn("Shape drag: the handle isn't in view");
                    break;
                }

                var target = (h.Line.X[h.Point] + Number(3), h.Line.Z[h.Point] - Number(4));
                _tool = Tool.Shape;
                (NetworkLine Line, Alignment Shape)? Step(bool pressed, bool held) =>
                    _shape.Update(lines, screen, target, Project, S(1f), _camera.X, _camera.Z, ViewReach, pressed, held, false, false, false, false, false);
                Step(true, true);
                var picked = _shape.LineIn(lines);
                Step(false, false);
                Step(true, true);
                Step(false, true);
                var preview = _shape.Preview;
                var change = Step(false, false);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Shape drag: picked line {picked?.Index}, wanted {h.Line.Index}, tightest curve {preview?.Radius:0} m, {preview?.OffMap:0.00} m off the map, {(change is null ? "nothing to commit" : "committed")}"));
                if (change is { } made)
                    CommitShape(source, made.Line, made.Shape);
                break;
            }

            case "show" when Nearest(At(1), true) is { } h:
                _tool = Tool.Shape;
                _shape.Show(h.Line.Key);
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (h.Line.X[h.Point], h.Line.Y[h.Point] + 60.0, h.Line.Z[h.Point] + 8.0, -1.3f);
                Log.Info($"Shape show: line {h.Line.Index}, handle at point {h.Point} of {h.Line.Count}, {h.Handles.Count} handles");
                break;
            case "move" when Nearest(At(1), true) is { } h:
                var (mx, mz) = (h.Line.X[h.Point] + Number(3), h.Line.Z[h.Point] - Number(4));
                Report("move", h.Line, TrackShaping.Move(h.Line, h.Handles, h.Point, mx, mz));
                break;
            case "straighten" when Nearest(At(1), true) is { } h && h.Handles.IndexOf(h.Point) + 2 < h.Handles.Count:
                var next = h.Handles[h.Handles.IndexOf(h.Point) + 2];
                Report("straighten", h.Line, TrackShaping.Straighten(h.Line, h.Handles, h.Point, next, p => h.Line.Nodes.Any(n => n.Point == p && lines.IsJunction(n.Node))));
                break;
            case "remove" when Nearest(At(1), true) is { } h:
                Report("remove", h.Line, TrackShaping.Remove(h.Line, h.Handles, h.Point));
                break;
            case "reset" when Nearest(At(1), false) is { } h:
                CommitShape(source, h.Line, null);
                Log.Info($"Shape reset: line {h.Line.Index}");
                break;
            case "undo":
                if (_undo.TryPop(out var undone))
                {
                    Redo(undone, undo: true);
                    _redo.Push(undone);
                }

                break;
            case "redo":
                if (_redo.TryPop(out var redone))
                {
                    Redo(redone, undo: false);
                    _undo.Push(redone);
                }

                break;
            case "save":
                Save(source);
                Log.Info("Shape: saved");
                break;
            default:
                Log.Warn($"Shape op: nothing to do for {op}");
                break;
        }
    }

    // --- Track. ---

    private float2? ProjectGround(double x, double z) => Project(x, _terrain!.Source.HeightAt(x, z) + 0.6, z);

    /// The camera's view and projection, made again only when the camera or the window has changed: projecting is done
    /// for thousands of points a frame. Read it on the main thread before any job projects with it.
    private float4x4 ViewProjection
    {
        get
        {
            var state = (_camera.Yaw, _camera.Pitch, _camera.FieldOfView, _screen.X / _screen.Y);
            if (state != _viewProjectionFor)
            {
                _viewProjection = _camera.ViewProjection(state.Item4);
                _viewProjectionFor = state;
            }

            return _viewProjection;
        }
    }

    private float4x4 _viewProjection;
    private (float, float, float, float) _viewProjectionFor = (float.NaN, 0f, 0f, 0f);

    private float2? Project(double x, double y, double z)
    {
        var relative = new float3((float)(x - _camera.X), (float)(y - _camera.Y), (float)(z - _camera.Z));
        var clip = MathEx.Project(relative, ViewProjection);
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

        if (DevTools.TrackOp is { } op)
        {
            // A scripted selection for testing, and what to do with it.
            DevTools.TrackOp = null;
            var at = _route.Projection!.Forward(new GeoPoint(op.Lat, op.Lon));
            _tool = Tool.Track;
            _selection.Clear();
            _selection.UnionWith(tracks.NodesNear(at.East, -at.North, op.Radius));
            Log.Info($"Track op {op.Op}: {_selection.Count} points");
            if (SelectionTools.FirstOrDefault(t => t.Name.Equals(op.Op, StringComparison.OrdinalIgnoreCase)) is { Act: not null } tool)
                Committed(tool.Act(tracks, _selection));
        }

        // From above, control points show as far as the view reaches.
        tracks.Reach = _topDown
            ? Math.Clamp((_camera.Y - _terrain!.Source.HeightAt(_camera.X, _camera.Z)) * Math.Tan(_camera.FieldOfView * 0.5) * (_screen.X / _screen.Y) * 1.2, 1500.0, 8000.0)
            : 1500.0;
        _selection.RemoveWhere(n => !tracks.Has(n));

        if (_tool is not (Tool.Track or Tool.Pen) || Blocked)
        {
            _hover = null;
            _penMeasure = null;
            return;
        }

        if (_grabbed is null)
            _hover = tracks.NodeAt(mouse, _camera.X, _camera.Z, ProjectGround);

        if (_tool == Tool.Track)
        {
            SelectAndDrag(tracks, mouse);
            return;
        }

        // The pen: where the next point would go, snapped to a control point or a track under the pointer.
        _layNext = _hover is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : tracks.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround) is { } segment
                ? new LayPoint(segment.X, segment.Z, Split: (segment.Way, segment.Segment))
                : _hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;

        _pen.Update(mouse, _layNext, _hit is { } h ? (h.X, h.Z) : null, ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
        if (Input.KeyDown(GlfwKey.Backspace))
            _pen.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter) && _pen.Count >= 2)
        {
            Committed(tracks.Lay(_pen.Points(), lead: false));
            _pen.Clear();
        }

        _penMeasure = PenTool.Measured(_pen.Points(PenPreview), _terrain!.Source.HeightAt);
    }

    /// Editing track: click a control point to select it (Shift adds or takes it away), drag out a box over the
    /// ground to select every point in it (Shift adds them), drag a selected point to move them all. L straightens
    /// the selection, K smooths it, J joins the ways it touches and Delete takes its points out.
    private void SelectAndDrag(TrackEditor tracks, float2 mouse)
    {
        var shift = Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift);
        if (Input.MouseDown(GlfwMouseButton.Left))
        {
            if (_hover is { } node && shift)
            {
                if (!_selection.Remove(node))
                    _selection.Add(node);
            }
            else if (_hover is { } grabbed)
            {
                if (!_selection.Contains(grabbed))
                {
                    _selection.Clear();
                    _selection.Add(grabbed);
                }

                _grabbed = grabbed;
                _grabFrom = tracks.PositionOf(grabbed);
            }
            else
                _boxFrom = mouse;
        }

        if (_grabbed is not null && _hit is { } hit && _grabFrom is { } from)
            _dragOffset = (hit.X - from.X, hit.Z - from.Z);

        if (!Input.Mouse(GlfwMouseButton.Left))
        {
            if (_grabbed is not null && _dragOffset is { } o && Math.Abs(o.X) + Math.Abs(o.Z) > 0.01)
                Committed(tracks.MoveMany(_selection.ToDictionary(n => n, n => (tracks.PositionOf(n).X + o.X, tracks.PositionOf(n).Z + o.Z))));
            _grabbed = null;
            _grabFrom = null;
            _dragOffset = null;

            if (_boxFrom is { } start)
            {
                if (!shift)
                    _selection.Clear();
                if (MathF.Abs(mouse.X - start.X) + MathF.Abs(mouse.Y - start.Y) > 4f)
                    _selection.UnionWith(tracks.NodesIn(start, mouse, _camera.X, _camera.Z, ProjectGround));
                _boxFrom = null;
            }
        }

        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Delete))
            Committed(_selection.Count > 0 ? tracks.DeleteMany(_selection) : _hover is { } doomed ? tracks.Delete(doomed) : null);
        if (Input.KeyDown(GlfwKey.L))
            Committed(tracks.Straighten(_selection));
        if (Input.KeyDown(GlfwKey.K))
            Committed(tracks.Smooth(_selection));
        if (Input.KeyDown(GlfwKey.J))
            Committed(tracks.Join(_selection));
        if (Input.KeyDown(GlfwKey.U))
            SplitTrack(tracks);
    }

    /// Unjoins track at the one point selected, which isn't selected after: there are two ends there now.
    private void SplitTrack(TrackEditor tracks)
    {
        var made = tracks.Split(_selection);
        _switchRefused = tracks.SplitRefused;
        if (made is null)
            return;
        Committed(made);
        _selection.Clear();
    }

    /// The selection's tools, for the menu and the side panel: name, key, and what it does.
    private (string Name, string Key, Func<TrackEditor, IReadOnlySet<long>, NetworkEdit?> Act)[] SelectionTools =>
    [
        ("Straighten", "L", (t, s) => t.Straighten(s)),
        ("Smooth", "K", (t, s) => t.Smooth(s)),
        ("Simplify", "", (t, s) => t.Simplify(s)),
        ("Join", "J", (t, s) => t.Join(s)),
        ("Split", "U", (t, s) => t.Split(s)),
        ("Delete", "Del", (t, s) => t.DeleteMany(s)),
    ];

    /// The track circuits tool (see CircuitEditor).
    private void EditCircuits()
    {
        if (_tool != Tool.Circuits || Blocked || _circuitEditor is null)
            return;

        var mouse = Input.MousePosition;
        var pixel = 2.0 * Math.Tan(_camera.FieldOfView * 0.5) / _screen.Y;
        var metresPerPixel = _hit is { } h
            ? Math.Sqrt((h.X - _camera.X) * (h.X - _camera.X) + (h.Y - _camera.Y) * (h.Y - _camera.Y) + (h.Z - _camera.Z) * (h.Z - _camera.Z)) * pixel
            : 1.0;
        var edit = _circuitEditor.Update(mouse, _hit is { } g ? (g.X, g.Z) : null, metresPerPixel, ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left),
            Input.Key(GlfwKey.LeftShift) || Input.Key(GlfwKey.RightShift), Input.KeyDown(GlfwKey.Delete) && !Keyboard.KeysBlocked);
        Record(edit);
    }

    /// Keeps an edit (of any kind) for undoing.
    private void Record(object? edit)
    {
        if (edit is null)
            return;
        _undo.Push(edit);
        _redo.Clear();
    }

    /// How far around the camera there's anything to draw: as far as the view reaches from above, a few kilometres
    /// in flight.
    private double ViewReach => _topDown
        ? (_camera.Y - _terrain!.Source.HeightAt(_camera.X, _camera.Z)) * Math.Tan(_camera.FieldOfView * 0.5) * (_screen.X / _screen.Y) * 1.2
        : 2500.0;

    /// Where the pen's path would run on to if the pointer were clicked now.
    private (double X, double Z)? PenPreview => !_pen.Busy && _layNext is { } next ? (next.X, next.Z) : null;

    private void Committed(NetworkEdit? edit)
    {
        if (edit is null)
            return;
        _undo.Push(edit);
        _redo.Clear();
        RebuildNetwork();
    }

    /// Works out what a network edit changed in the background; edits made meanwhile wait for the next pass.
    /// Fits the track to the map again, from scratch, with the fitting as it now is.
    private void SolveTrackAgain()
    {
        if (_rebuild is null)
            _rebuild = _terrain!.Source.RebuildNetwork(solveAgain: true);
    }

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
        _flattenTo = _flattenSample ?? source.HeightAt(_hit!.Value.X, _hit.Value.Z);
    }

    private void SampleFlatten(TerrainSource source, double x, double z)
    {
        _flattenSample = source.HeightAt(x, z);
        _flattenTo = _flattenSample.Value; // A stroke under way goes to it too.
    }

    private string FlattenSampleText
    {
        get
        {
            if (_flattenSample is not { } height)
                return "Sample height";
            return Settings.Current.Units == UnitSystem.Imperial
                ? string.Create(CultureInfo.InvariantCulture, $"Sampled {height * FeetPerMeter:#,0} ft")
                : string.Create(CultureInfo.InvariantCulture, $"Sampled {height:#,0} m");
        }
    }

    /// The brush works on another thread, one dab at a time: a big one takes longer than a frame. The time since the last dab goes
    /// into the next, so the ground comes up just as fast.
    private void Apply(TerrainSource source, float dt)
    {
        _dabTime += dt;
        CollectDab(false);
        if (_dab is not null)
            return;

        var dab = NextDab(source);
        _dab = Task.Run(dab);
    }

    private Func<(int X0, int Z0, int X1, int Z1)?> NextDab(TerrainSource source)
    {
        var hit = _hit!.Value;
        var fast = Input.Key(GlfwKey.LeftShift) ? 4f : 1f;
        var (tool, radius, paint, flatten, stroke, time) = (_tool, _radius, PaintCovers[_paint].Class, _flattenTo, _stroke!, _dabTime);
        _dabTime = 0f;
        return () => tool switch
        {
            Tool.Raise => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Raise, RaiseRate * fast * time, 0f, stroke),
            Tool.Lower => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Lower, RaiseRate * fast * time, 0f, stroke),
            Tool.Smooth => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Smooth, MathF.Min(1f, BlendRate * fast * time), 0f, stroke),
            Tool.Flatten => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Flatten, MathF.Min(1f, BlendRate * fast * time), flatten, stroke),
            Tool.Paint => source.Paint(hit.X, hit.Z, radius, paint, stroke),
            _ => null,
        };
    }

    /// Takes in the last dab once it's done, or waits for it.
    private void CollectDab(bool wait)
    {
        if (_dab is not { } dab || (!wait && !dab.IsCompleted))
            return;

        _dab = null;
        try
        {
            Rebuild(dab.GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            Log.Error($"Brush: {e.Message}");
        }
    }

    private void End()
    {
        // The stroke isn't whole until the last dab is in, and what's left of its time has gone down.
        CollectDab(true);
        if (_dabTime > 0f && _stroke is not null && _hit is not null)
            Rebuild(NextDab(_terrain!.Source)());
        _dabTime = 0f;

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
        if (_tool is Tool.Track or Tool.Pen)
        {
            _tracks!.Draw(draw, _camera.X, _camera.Z, ProjectGround, _hover, _selection, _dragOffset);
            if (_boxFrom is { } from)
            {
                var to = Input.MousePosition;
                var (min, max) = (new float2(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y)), new float2(MathF.Max(from.X, to.X), MathF.Max(from.Y, to.Y)));
                draw.AddRectFilled(min, max, Rgba.White.WithAlpha(0.06f));
                draw.AddRect(min, max, Rgba.White.WithAlpha(0.6f), 0f, ImDrawFlags.None, 1f);
            }

            if (_tool == Tool.Pen)
            {
                _pen.Draw(draw, ProjectGround, PenPreview);
                if (_penMeasure is { } m && !Blocked)
                    PenTool.Label(draw, Input.MousePosition, PenTool.Describe(m, Settings.Current.Units == UnitSystem.Imperial), Style.Text);
            }

            return;
        }

        if (_hit is not { } hit || _tool is Tool.Look or Tool.Switches or Tool.Shape or Tool.Follow or Tool.Place or Tool.Road or Tool.Parallel)
            return;

        const int segments = 64;
        var source = _terrain!.Source;
        var viewProjection = ViewProjection;
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

    /// The brush or the track being edited, over the world; until the terrain's open, a note that it's loading.
    public void Draw(ImDrawListPtr draw, float2 size)
    {
        _screen = size;
        if (_terrain is null)
        {
            Canvas.Text(draw, Medium, S(15f), size * 0.5f, TextDim, Failed ? "TERRAIN COULDN'T BE OPENED" : "LOADING TERRAIN", Align.Center);
            return;
        }

        DrawBackground(draw);
        if (_tool == Tool.Circuits)
            _circuitEditor?.Draw(draw, _camera.X, _camera.Z, ViewReach, ProjectGround,
                !_topDown || _camera.Y - _terrain.Source.HeightAt(_camera.X, _camera.Z) < 5000.0);
        else if (_showGrades)
            DrawGrades(draw, size);
        DrawBrush(draw);
        if (_tool == Tool.Switches)
            DrawSwitches(draw);
        if (_tool == Tool.Switches && _terrain?.Source.Lines is { } handled && _switch is { } handledNode && handled.Turnouts.FirstOrDefault(t => t.Node == handledNode) is { } handledSwitch)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            _handles.Draw(draw, handled, handledSwitch, ProjectGround, S(1f), m => imperial ? string.Create(CultureInfo.InvariantCulture, $"{m * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{m:0} m"));
        }
        if (_tool == Tool.Shape && _terrain?.Source.Lines is { } shaping)
            _shape.Draw(draw, shaping, Project, S(1f));
        if (_tool is Tool.Follow or Tool.Pen or Tool.Shape or Tool.Parallel)
            _guides?.Draw(draw, ProjectGround, _camera.X, _camera.Z, ViewReach);
        if (_tool is Tool.Follow or Tool.Pen or Tool.Parallel || (_tool == Tool.Place && _place.Mode is PlaceMode.Track or PlaceMode.Bridge))
            _tracks?.DrawAttach(draw, _camera.X, _camera.Z, ProjectGround, Input.MousePosition);
        if (_tool == Tool.Follow)
            _follow.Draw(draw, ProjectGround, Input.MousePosition);
        if (_tool == Tool.Follow)
            _yard.Draw(draw, ProjectGround, S(1f));
        if (_tool == Tool.Parallel)
            _parallel.Draw(draw, ProjectGround, S(1f));
        if (_tool == Tool.Place && _terrain is { } shown)
            _place.Draw(draw, ProjectGround, shown.Source.Objects, PlacedAtWorld, S(1f));
        if (_tool == Tool.Road)
        {
            _road.Draw(draw, ProjectGround, _roadNext, S(1f));
            if (_roadHover is { } doomed && _roads?.Shape(doomed) is { } shape)
                DrawPicked(draw, shape.X, shape.Z, Style.Stop);
        }

        if (_tool == Tool.Place && _place.Mode == PlaceMode.RemoveBridge && !Blocked && _hit is { } at2
            && Roads.Nearest(at2.X, at2.Z, PickReach(at2.X, at2.Z), RoadEditor.IsBridge) is { } gone && _roads?.Shape(gone.Way) is { } goneLine)
            DrawPicked(draw, goneLine.X, goneLine.Z, Style.Stop);
        if (_tool == Tool.Place && _place.Mode == PlaceMode.Bridge && !Blocked && _hit is { } at3)
            DrawBridgePreview(draw, at3.X, at3.Z);

        if (_tool == Tool.Follow)
            _area?.Draw(draw, ProjectGround, Style.Caution);
        if (_gradeAt is { } at && !(_tool == Tool.Pen && _penMeasure is not null))
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var height = imperial ? $"{at.Height * FeetPerMeter:#,0} ft" : $"{at.Height:#,0} m";
            PenTool.Label(draw, at.Screen, string.Create(CultureInfo.InvariantCulture, $"Grade {at.Grade * 100:0.00}%   {height}"), GradeColor(at.Grade));
        }
    }

    /// Before the first click, the line a bridge would go on and the place on it. After, the stretch it would cover, along the line,
    /// and how long that is.
    private void DrawBridgePreview(ImDrawListPtr draw, double x, double z)
    {
        if (_bridgeFrom is not { } from)
        {
            if (BridgeStartAt(x, z) is not { } start)
                return;
            if (_roads?.Shape(start.Way) is { } line)
                DrawPicked(draw, line.X, line.Z, Style.TextDim);
            if (ProjectGround(start.X, start.Z) is { } place)
                draw.AddCircleFilled(place, S(6f), Style.Accent);
            return;
        }

        if (BridgeEndAt(x, z) is not { } end || Roads.Span(from, end) is not { } span)
            return;

        var landing = BridgeLandingAt(end, x, z);
        var total = BridgeLength(from, end, landing);
        var color = total < ShortestBridge || total > LongestBridge ? Style.Stop : Style.Accent;
        for (var i = 0; i + 1 < span.Points.Count; i++)
            if (ProjectGround(span.Points[i].X, span.Points[i].Z) is { } a && ProjectGround(span.Points[i + 1].X, span.Points[i + 1].Z) is { } b)
                draw.AddLine(a, b, color, S(5f));
        if (ProjectGround(from.X, from.Z) is { } first)
            draw.AddCircleFilled(first, S(6f), color);
        var (lastX, lastZ) = landing is { } l ? (l.X, l.Z) : (end.X, end.Z);
        if (landing is not null && ProjectGround(end.X, end.Z) is { } edge && ProjectGround(lastX, lastZ) is { } over)
            draw.AddLine(edge, over, color, S(5f));
        if (ProjectGround(lastX, lastZ) is { } last)
        {
            draw.AddCircleFilled(last, S(6f), color);
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var length = imperial ? $"{total * FeetPerMeter:#,0} ft" : $"{total:#,0} m";
            PenTool.Label(draw, last, $"Bridge {length}", color);
        }
    }

    /// A line over the view, to show what a click would act on.
    private void DrawPicked(ImDrawListPtr draw, double[] x, double[] z, Rgba color)
    {
        for (var i = 0; i + 1 < x.Length; i++)
            if (ProjectGround(x[i], z[i]) is { } a && ProjectGround(x[i + 1], z[i + 1]) is { } b)
                draw.AddLine(a, b, color, S(3f));
    }

    private static Rgba GradeColor(float grade) => MathF.Abs(grade) switch
    {
        < 0.01f => Style.Clear,
        < 0.02f => Style.Caution,
        _ => Style.Stop,
    };

    private readonly List<NetworkLine> _gradeLines = [];
    private List<(float2 A, float2 B, Rgba Color, NetworkLine Line, int Point)>[] _gradeSegments = [];

    /// Every rail line in view coloured by its grade, and the grade under the pointer (averaged over a short stretch,
    /// as a train would feel it) if it's over one. The lines are projected on every core (FrameJobs), each slice into
    /// its own list, and drawn here.
    private void DrawGrades(ImDrawListPtr draw, float2 size)
    {
        _gradeAt = null;
        if (_terrain!.Source.Lines is not { } lines)
            return;

        var reach = _topDown
            ? (_camera.Y - _terrain.Source.HeightAt(_camera.X, _camera.Z)) * Math.Tan(_camera.FieldOfView * 0.5) * (size.X / size.Y) * 1.2
            : 3000.0;
        _gradeLines.Clear();
        foreach (var line in lines.Lines)
            if (line.Rail && line.MaxX >= _camera.X - reach && line.MinX <= _camera.X + reach && line.MaxZ >= _camera.Z - reach && line.MinZ <= _camera.Z + reach)
                _gradeLines.Add(line);

        _ = ViewProjection;
        var slices = FrameJobs.Slices(_gradeLines.Count, 4);
        if (_gradeSegments.Length < slices)
            _gradeSegments = [.. Enumerable.Range(0, slices).Select(i => i < _gradeSegments.Length ? _gradeSegments[i] : [])];
        for (var i = 0; i < slices; i++)
            _gradeSegments[i].Clear();
        FrameJobs.For(_gradeLines.Count, slices, (slice, from, to) => GradeSegments(_gradeLines, from, to, size, _gradeSegments[slice]));

        var mouse = Input.MousePosition;
        var nearest = S(12f);
        (NetworkLine Line, int Point)? hovered = null;
        var width = S(3f);
        for (var slice = 0; slice < slices; slice++)
        {
            foreach (var (a, b, color, line, point) in _gradeSegments[slice])
            {
                draw.AddLine(a, b, color, width);
                var d = DistanceToSegment(mouse, a, b);
                if (d < nearest && !Blocked)
                {
                    nearest = d;
                    hovered = (line, point);
                }
            }
        }

        if (hovered is not { } over)
            return;
        var l = over.Line;
        var (i0, i1) = (over.Point, over.Point);
        while (i0 > 0 && l.S[over.Point] - l.S[i0] < GradeReadoutWindow)
            i0--;
        while (i1 < l.Count - 1 && l.S[i1] - l.S[over.Point] < GradeReadoutWindow)
            i1++;
        var span = l.S[i1] - l.S[i0];
        _gradeAt = (mouse, span > 0.01f ? (l.Y[i1] - l.Y[i0]) / span : 0f, l.Y[over.Point]);
    }

    /// One slice of the lines' coloured segments on the screen (a job). Each segment's colour is the grade over
    /// GradeColourWindow either side of it, not its own: the short pieces round a bridge or a crossing would otherwise
    /// flicker red. Points closer than a couple of pixels to the last one drawn are skipped, and so are segments off
    /// the screen.
    private void GradeSegments(List<NetworkLine> lines, int from, int to, float2 size, List<(float2, float2, Rgba, NetworkLine, int)> into)
    {
        into.Clear();
        var margin = 50f;
        for (var n = from; n < to; n++)
        {
            var line = lines[n];
            float2? previous = Project(line.X[0], line.Y[0] + 0.5, line.Z[0]);
            var (lo, hi) = (0, 0);
            for (var i = 1; i < line.Count; i++)
            {
                var screen = Project(line.X[i], line.Y[i] + 0.5, line.Z[i]);
                if (screen is not { } b)
                {
                    previous = null;
                    continue;
                }

                if (previous is not { } a)
                {
                    previous = b;
                    continue;
                }

                if (i < line.Count - 1 && MathF.Abs(b.X - a.X) + MathF.Abs(b.Y - a.Y) < 2.5f)
                    continue;

                var offscreen = (a.X < -margin && b.X < -margin) || (a.X > size.X + margin && b.X > size.X + margin) ||
                                (a.Y < -margin && b.Y < -margin) || (a.Y > size.Y + margin && b.Y > size.Y + margin);
                if (!offscreen)
                {
                    var middle = (line.S[i] + line.S[i - 1]) * 0.5f;
                    while (lo < i - 1 && line.S[lo + 1] <= middle - GradeColourWindow)
                        lo++;
                    hi = Math.Max(hi, i);
                    while (hi < line.Count - 1 && line.S[hi] < middle + GradeColourWindow)
                        hi++;
                    var ds = line.S[hi] - line.S[lo];
                    var grade = ds > 0.01f ? (line.Y[hi] - line.Y[lo]) / ds : 0f;
                    into.Add((a, b, GradeColor(grade).WithAlpha(0.9f), line, i));
                }

                previous = b;
            }
        }
    }

    private static float DistanceToSegment(float2 p, float2 a, float2 b)
    {
        var ab = b - a;
        var length = ab.X * ab.X + ab.Y * ab.Y;
        var t = length > 0f ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / length, 0f, 1f) : 0f;
        var q = a + ab * t;
        return MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    // --- Menus, the side panel and windows (ImGui). ---

    public void MenuFile()
    {
        var source = _terrain?.Source;
        if (ImGui.MenuItem("Save", "Ctrl+S", false, source is not null && (source.Unsaved || source.Network.Unsaved)))
            Save(source!);
        if (ImGui.MenuItem("Solve Track Again", "", false, source is not null && _rebuild is null))
            SolveTrackAgain();
    }

    public void MenuView()
    {
        if (ImGui.MenuItem("Wireframe", "F", _renderer.Wireframe, true))
            _renderer.Wireframe = !_renderer.Wireframe;
        ImGui.MenuItem("Track Grades"u8, "G"u8, ref _showGrades);
    }

    /// Whether the side panel has anything to show.
    public bool HasPanel => _terrain is null || _tool != Tool.Look || _topDown || _showGrades;

    /// The camera and the tool in hand, for the side panel.
    public void Panel()
    {
        if (_terrain is null)
            return;

        var imperial = Settings.Current.Units == UnitSystem.Imperial;

        // (Leaves the labels room inside the panel.)
        ImGui.PushItemWidth(-104f);
        if (_tool == Tool.Pen)
            ImGui.Text($"{_pen.Count} point{(_pen.Count == 1 ? "" : "s")}");
        if (IsBrush(_tool))
            BrushPanel(imperial);
        if (_tool == Tool.Parallel)
            ParallelPanel();
        if (_tool == Tool.Pen && _penMeasure is { } m)
        {
            string Length(double metres) => imperial
                ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
                : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
            ImGui.Text($"Length {Length(m.Length)}");
            ImGui.Text(double.IsInfinity(m.MinRadius) ? "Straight" : $"Tightest curve radius {Length(m.MinRadius)}");
            ImGui.Text(string.Create(CultureInfo.InvariantCulture, $"Ground grade {m.Grade * 100:0.00}% end to end, {m.SteepestGrade * 100:0.00}% steepest"));
        }

        if (_tool == Tool.Circuits)
            Record(_circuitEditor?.Panel(imperial));

        if (_tool == Tool.Switches)
            SwitchPanel();

        if (_tool == Tool.Shape)
            ShapePanel();

        if (_tool == Tool.Follow)
            FollowPanel();

        if (_tool == Tool.Track)
        {
            ImGui.Text($"{_selection.Count} point{(_selection.Count == 1 ? "" : "s")} selected");
            ImGui.BeginDisabled(_selection.Count == 0);
            var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            var first = true;
            foreach (var (name, _, act) in SelectionTools)
            {
                // On this row if it fits, else the next.
                if (!first && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(name).X + ImGui.GetStyle().FramePadding.X * 2f <= right)
                    ImGui.SameLine(0f, -1f);
                first = false;
                if (ImGui.Button(name))
                    Committed(act(_tracks!, _selection));
            }

            ImGui.EndDisabled();
        }

        if (_tool is Tool.Track or Tool.Pen or Tool.Follow or Tool.Parallel || (_tool == Tool.Place && _place.Mode == PlaceMode.Track))
            BedPanel();
        if (_tool == Tool.Place)
            PlacePanel(_terrain.Source);
        if (_tool == Tool.Road)
            RoadPanel();

        if (_topDown)
        {
            ImGui.SeparatorText("Background"u8);
            if (ImGui.RadioButton("Off"u8, !_showBackground))
                _showBackground = false;
            ImGui.SameLine(0f, -1f);
            if (ImGui.RadioButton("Map"u8, _showBackground && MapTiles.Background == MapBackground.Map))
                (_showBackground, MapTiles.Background) = (true, MapBackground.Map);
            ImGui.SameLine(0f, -1f);
            if (ImGui.RadioButton("Satellite"u8, _showBackground && MapTiles.Background == MapBackground.Satellite))
                (_showBackground, MapTiles.Background) = (true, MapBackground.Satellite);
            if (_showBackground)
            {
                ImGui.SliderFloat("Opacity"u8, ref _backgroundOpacity, 0.1f, 1f, "%.1f"u8, ImGuiSliderFlags.None);
                ImGui.TextDisabled(MapTiles.Attribution);
            }
        }


        if (_showGrades && _tool != Tool.Circuits)
        {
            ImGui.SeparatorText("Track Grades"u8);
            foreach (var (grade, name) in (ReadOnlySpan<(float, string)>)[(0f, "under 1%"), (0.015f, "1 to 2%"), (0.03f, "over 2%")])
            {
                var c = GradeColor(grade);
                ImGui.ColorButton($"##grade{name}", new float4(c.R, c.G, c.B, 1f), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder, new float2(28f, 6f));
                ImGui.SameLine(0f, -1f);
                ImGui.Text(name);
            }
        }

        ImGui.PopItemWidth();
    }

    // --- Switches: each one's size, picked from the models. ---

    /// Every switch marked (filled where it's modelled), the picked one ringed, and every junction that needs fixing
    /// ringed red.
    private void DrawSwitches(ImDrawListPtr draw)
    {
        if (_terrain?.Source.Lines is not { } lines)
            return;

        foreach (var layout in lines.Layouts)
        {
            if (layout.Issue is null || Math.Abs(layout.X - _camera.X) > ViewReach || Math.Abs(layout.Z - _camera.Z) > ViewReach || ProjectGround(layout.X, layout.Z) is not { } at)
                continue;
            draw.AddCircleFilled(at, S(11f), Style.Stop.WithAlpha(0.25f), 20);
            draw.AddCircle(at, S(11f), Style.Stop, 20, S(2f));
        }

        foreach (var t in lines.Turnouts)
        {
            if (Math.Abs(t.X - _camera.X) > ViewReach || Math.Abs(t.Z - _camera.Z) > ViewReach || ProjectGround(t.X, t.Z) is not { } at)
                continue;
            var r = S(t.Node == _switch ? 9f : 6f);
            var (a, b, c, d) = (at + new float2(0f, -r), at + new float2(r, 0f), at + new float2(0f, r), at + new float2(-r, 0f));
            if (t.Modelled)
                draw.AddQuadFilled(a, b, c, d, Style.Caution);
            draw.AddQuad(a, b, c, d, t.Modelled ? new Rgba(0f, 0f, 0f, 0.8f) : Style.Clear, S(1.5f));
            if (t.Node == _switch)
                draw.AddCircle(at, S(16f), Rgba.White, 24, S(2f));
        }
    }

    /// The switch whose marker (or red ring) is under the mouse, measured on the screen as they're drawn, so a click has
    /// to land on one whatever the camera's distance; the nearest if several are; null if it missed them all.
    private long? SwitchAt(NetworkLines lines, float2 mouse)
    {
        long? nearest = null;
        var best = float.MaxValue;
        void Consider(long node, double x, double z, float radius)
        {
            if (Math.Abs(x - _camera.X) > ViewReach || Math.Abs(z - _camera.Z) > ViewReach || ProjectGround(x, z) is not { } at)
                return;
            var (dx, dy) = (at.X - mouse.X, at.Y - mouse.Y);
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= radius && distance < best)
                (nearest, best) = (node, distance);
        }

        foreach (var t in lines.Turnouts)
            Consider(t.Node, t.X, t.Z, S(t.Node == _switch ? 16f : 9f));
        foreach (var layout in lines.Layouts)
        {
            if (layout.Issue is not null && lines.Turnouts.FirstOrDefault(t => layout.Nodes.Contains(t.Node)) is { } turnout)
                Consider(turnout.Node, layout.X, layout.Z, S(11f));
        }

        return nearest;
    }

    /// The line in the Shape tool: what's been done to it, what the handle in hand would make of it, and back to as solved.
    private void ShapePanel()
    {
        if (_terrain?.Source is not { Lines: { } lines } source)
            return;
        if (_shape.LineIn(lines) is not { } line)
        {
            ImGui.TextDisabled("Click a track"u8);
            return;
        }

        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        string Length(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{metres:0.#} m");
        ImGui.Text($"{Length(line.S[^1])} long, {(source.TrackEdits.Has(line.Key) ? "shaped by hand" : "as solved")}");
        if (_shape.Preview is { } preview)
        {
            if (preview.Radius < double.MaxValue)
                ImGui.Text($"Tightest curve {Length(preview.Radius)}");
            ImGui.Text($"Off the map {Length(preview.OffMap)}");
        }

        ImGui.BeginDisabled(!source.TrackEdits.Has(line.Key) || _rebuild is not null);
        if (ImGui.Button("Back to Solved"u8))
            CommitShape(source, line, null);
        ImGui.EndDisabled();
    }

    /// The picked switch: what it is now, and the sizes to choose from, each shown by its model.
    private void SwitchPanel()
    {
        if (_terrain?.Source.Lines is not { } lines)
            return;
        if (_switch is not { } node || lines.Turnouts.FirstOrDefault(t => t.Node == node) is not { } turnout)
        {
            ImGui.TextDisabled("Click a switch"u8);
            IssueList(lines);
            return;
        }

        if (lines.Layouts.FirstOrDefault(l => l.Nodes.Contains(node)) is { } layout)
        {
            if (layout.Kind is JunctionKind.Crossover or JunctionKind.LadderLink or JunctionKind.ScissorsCrossover)
                ImGui.Text($"Part of a {TrackLayouts.Name(layout.Kind, 0).ToLowerInvariant()}");
            if (layout.Issue is { } issue)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Style.Stop);
                ImGui.TextWrapped(issue);
                ImGui.PopStyleColor();
            }
        }

        if (_switchRefused is { } refused)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Style.Stop);
            ImGui.TextWrapped(refused);
            ImGui.PopStyleColor();
        }

        var options = lines.SwitchOptions.GetValueOrDefault(node);
        var chosen = options?.Chosen;
        var byHand = options?.ReachByHand == true;
        ImGui.Text(turnout.Number is { } now
            ? $"No. {now}, {(byHand ? "curve set by hand" : chosen is null ? "sized automatically" : "picked")}"
            : chosen == SwitchSizes.AsMapped ? "As mapped (picked)" : "As mapped: no standard size fits");
        ImGui.TextDisabled($"Node {node}");

        ImGui.BeginDisabled(_rebuild is not null);
        if (ImGui.RadioButton("Automatic"u8, chosen is null && !byHand))
            PickSize(node, null);
        ImGui.SameLine(0f, -1f);
        if (ImGui.RadioButton("As mapped"u8, chosen == SwitchSizes.AsMapped))
            PickSize(node, SwitchSizes.AsMapped);
        ImGui.EndDisabled();

        _sizePictures ??= LoadSizePictures();
        var width = ImGui.GetContentRegionAvail().X;
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        string Length(float metres) => imperial ? $"{metres * FeetPerMeter:0} ft" : $"{metres:0} m";
        foreach (var (number, radius) in Turnout.Sizes)
        {
            var fits = options?.Fits.Contains(number) ?? false;
            var current = turnout.Number == number;
            var frog = Turnout.FrogOf(number);
            var angle = MathF.Atan(frog / radius / 2f) * 360f / MathF.PI;

            ImGui.Spacing();
            ImGui.Text($"No. {number}{(options?.Auto == number ? "   automatic pick" : "")}{(fits ? "" : "   no room")}");
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"Curve {Length(radius)}   frog {Length(frog)} out   {angle:0.0} deg"));

            var size = new float2(width, width * 150f / 480f);
            var min = ImGui.GetCursorScreenPos();
            ImGui.BeginDisabled(!fits || _rebuild is not null);
            if (ImGui.InvisibleButton($"##size{number}", size, ImGuiButtonFlags.None) && !current)
                PickSize(node, number);
            ImGui.EndDisabled();
            var hovered = fits && ImGui.IsItemHovered();
            var draw = ImGui.GetWindowDrawList();
            if (_sizePictures.TryGetValue(number, out var picture))
                draw.AddImage(picture.Ref, min, min + size, null, null, fits ? Rgba.White : new Rgba(1f, 1f, 1f, 0.3f));
            else
                draw.AddRectFilled(min, min + size, Style.Stroke.WithAlpha(0.2f));
            if (current || hovered)
                draw.AddRect(min, min + size, current ? Style.Caution : Style.Accent, 0f, ImDrawFlags.None, S(current ? 2.5f : 1.5f));
        }
    }

    /// Every junction that needs fixing, with why; clicking one flies there and picks its switch.
    private void IssueList(NetworkLines lines)
    {
        var issues = lines.Layouts.Where(l => l.Issue is not null).ToList();
        if (issues.Count == 0)
            return;

        ImGui.SeparatorText($"Needs Fixing ({issues.Count})");
        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            if (ImGui.Selectable($"{TrackLayouts.Name(issue.Kind, issue.Number ?? 0)}##issue{i}", false, ImGuiSelectableFlags.None, default))
            {
                _switch = issue.Nodes[0];
                if (_topDown)
                    MoveTo(new RoutePoint(issue.X, -issue.Z));
                else
                {
                    // Flying: stand back from it, up and looking down at it, facing the way the camera faced.
                    var (sy, cy) = MathF.SinCos(_camera.Yaw);
                    _camera.Pitch = -0.6f;
                    _camera.X = issue.X - sy * 50.0;
                    _camera.Z = issue.Z + cy * 50.0;
                    _camera.Y = _terrain!.Source.HeightAt(issue.X, issue.Z) + 35.0;
                }
            }

            ImGui.Indent();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(issue.Issue!);
            ImGui.PopTextWrapPos();
            ImGui.Unindent();
        }
    }

    private static Dictionary<int, (Texture2D Texture, ImTextureRef Ref)> LoadSizePictures()
    {
        var pictures = new Dictionary<int, (Texture2D, ImTextureRef)>();
        foreach (var (number, _) in Turnout.Sizes)
        {
            try
            {
                var texture = Texture2D.LoadFile(ContentPath.Model($"Track/turnout_{number}.png"), false, false);
                pictures[number] = (texture, HighballApp.Instance.AddTexture(texture));
            }
            catch (Exception e)
            {
                Log.Warn($"Switch picture No. {number}: {e.Message}");
            }
        }

        return pictures;
    }

    /// Saves a switch's size (null: automatic) with the route, and lays its track again. A crossover's or ladder link's
    /// two switches go together.
    private void PickSize(long node, int? size)
    {
        var layout = _terrain?.Source.Lines?.Layouts.FirstOrDefault(l => l.Nodes.Contains(node));
        foreach (var each in layout is { Kind: JunctionKind.Crossover or JunctionKind.LadderLink } ? layout.Nodes : [node])
        {
            SwitchSizes.Set(_route, each, size);
            SwitchCurves.Set(_route, each, null);
        }
        Log.Info($"Switch {node}: {(size is null ? "automatic" : size == SwitchSizes.AsMapped ? "as mapped" : $"No. {size}")}");
        RebuildNetwork();
    }

    public void Windows() => ToolStrip();

    /// The editor's lines in Frame Statistics.
    public void DebugStats()
    {
        if (_terrain is { } terrain && _network is { } network)
        {
            var source = terrain.Source;
            ImGui.Separator();
            ImGui.Text($"Tiles {source.Tiles.Count}   lines {source.Lines?.Lines.Count ?? 0}   ways {source.Network.Ways.Count}");
            ImGui.Text($"Terrain patches {terrain.DrawnPatches} drawn / {terrain.PatchCount} held   {terrain.Building} building   {terrain.Waiting} waiting");
            ImGui.Text($"Line chunks {network.DrawnChunks} drawn   {network.Building} building   {network.Turnouts} switches");
            ImGui.Text($"Edits {_undo.Count} to undo   {(source.Unsaved || source.Network.Unsaved ? "unsaved" : "saved")}");
        }

        ImGui.Text($"Camera {_camera.X:0} {_camera.Y:0} {_camera.Z:0}   yaw {_camera.Yaw * 180f / MathF.PI:0}   pitch {_camera.Pitch * 180f / MathF.PI:0}");
    }

    /// The controls for flying and for the tool in hand, for the footer.
    public (string Key, string Action)[] Hints
    {
        get
        {
            (string, string)[] tool = _tool switch
            {
                Tool.Look => [("RMB", _topDown ? "Pan" : "Look"), ("WASD", "Move"), ("WHEEL", _topDown ? "Zoom" : "Speed"), .. (_topDown ? ((string, string)[])[] : [("SHIFT+WHEEL", _camera.Zoom <= 1.01f ? "Zoom" : string.Create(CultureInfo.InvariantCulture, $"Zoom {_camera.Zoom:0.0}x"))]), ("G", "Grades"), ("T", "Throw switch")],
                Tool.Road when _road.Mode == RoadMode.Remove => [("LMB", "Remove road"), ("CTRL+Z", "Undo")],
                Tool.Road => [("LMB", "Next point"), ("SHIFT", "15 degree steps"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
                Tool.Parallel => [("LMB", "Start, then end"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")],
                Tool.Switches => [("LMB", "Pick a switch"), ("DRAG", "Move points, curve end or crossover"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.RunFrom is not null => [("LMB", "Lay to here"), ("ESC", "End run"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.Mode == PlaceMode.Pole && _place.Fence && _place.Selected is null => [("LMB", "Start run"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.Mode == PlaceMode.Track && _place.Selected is null => [("LMB", "Lay piece"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Mirror"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
                Tool.Place => [("LMB", "Place, pick, drag"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Turn round or other side"), ("DEL", "Delete"), ("CTRL+Z", "Undo")],
                Tool.Follow when _yard.Mode != YardMode.Follow => [("LMB", "Pick a track, then lay"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")],
                Tool.Follow => [("LMB", "Click a guide or track"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove click"), ("CTRL+Z", "Undo")],
                Tool.Shape => [("LMB", "Select, drag handle"), ("SHIFT+LMB", "Mark handles"), ("L", "Straighten"), ("DEL", "Remove handle"), ("ALT", "No snap"), ("CTRL+Z", "Undo")],
                Tool.Flatten when _flattenSample is not null => [("LMB", "Flatten"), ("F", FlattenSampleText), ("ESC", "Clear sample"), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
                Tool.Flatten => [("LMB", "Flatten"), ("F", FlattenSampleText), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
                Tool.Paint => [("LMB", "Paint"), ("P", "Cover"), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
                Tool.Track => [("LMB", "Select, drag"), ("SHIFT", "Add"), ("L", "Straighten"), ("K", "Smooth"), ("J", "Join"), ("U", "Split"), ("DEL", "Delete")],
                Tool.Circuits => [("LMB", "Select circuit"), ("SHIFT+LMB", "Add joint"), ("DRAG", "Move joint"), ("DEL", "Remove joint"), ("CTRL+Z", "Undo")],
                Tool.Pen => [("LMB", "Point"), ("DRAG", "Curve"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")],
                _ => [("LMB", _tool.ToString()), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
            };
            return tool;
        }
    }
}
