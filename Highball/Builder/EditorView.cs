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
public sealed partial class EditorView : IDisposable, IToolHost
{
    private enum Tool { Look, Raise, Lower, Smooth, Flatten, Paint, Track, Pen, Circuits, Switches, Shape, Follow, Place, Parallel, Road }

    private const float FeetPerMeter = 3.28084f;

    /// Straight down, all but: exactly down leaves the camera no way to tell which way is north.
    private const float TopDownPitch = -MathF.PI / 2f + 0.001f;

    private readonly RouteInfo _route;
    /// The route as it's loaded: ground, track, streaming, switches and the rail graph.
    private readonly RouteWorld _routeWorld;
    private readonly WorldDisplay _world;
    private WorldRenderer _renderer => _world.Renderer;

    /// Puts the sky, time of day and exposure from Settings onto the renderer.
    public Lighting Lighting => _world.Lighting;
    private readonly EditorCamera _eye = new();

    /// The camera as it's seen on the screen: projecting to it and picking the ground.
    private readonly CameraView _view;

    /// The trains on the route: the train window, placing and picking, stepping, the train's cameras and sound.
    private readonly Trains.TrainSession _session;

    /// What the renderer draws this frame, filled in by RenderWorld.
    private FrameScene _scene => _world.Scene;

    /// The camera everything draws from and picks with: the editor's own (EditorCamera), which the drive cameras move too.
    private Camera _camera => _eye.Camera;
    private readonly RoutePoint? _start;
    private Terrain? _terrain => _routeWorld.Terrain;
    private NetworkMeshes? _network => _routeWorld.Network;
    private float _fps;
    /// Looking round with the right button held: the pointer's captured.
    private bool _looking { get => _eye.Looking; set => _eye.Looking = value; }

    private Tool _tool;
    /// The terrain brushes (Raise to Paint).
    private readonly TerrainBrush _brushes;

    /// What the tools are given (ToolContext).
    private readonly ToolContext _tools;
    private double3? _hit;
    /// The ground under the pointer whatever the tool, for the status line.
    private double3? _pointerAt;
    private float2 _screen { get => _view.Size; set => _view.Size = value; }
    // Terrain strokes and network edits, in the order they were made.
    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();

    private TrackEditor? _tracks;
    /// The Track Circuits tool, and the route's circuits (made once the route's open).
    private CircuitsTool? _circuits;
    private CircuitEditor? _circuitEditor => _circuits?.Editor;
    private long? _hover;
    // Editing track: the selected control points, the one grabbed to drag them and how far they've been dragged, and
    // where a box being dragged out to select began.
    private readonly HashSet<long> _selection = [];
    private long? _grabbed;
    private (double X, double Z)? _grabFrom;
    private (double X, double Z)? _dragOffset;
    private float2? _boxFrom;
    /// Editing track, with no control point under the pointer: the place on a track it's over, where Ctrl+click puts a new point.
    private (long Way, int Segment, double X, double Z)? _insertAt;
    private readonly PenTool _pen = new();
    /// The Roads tool.
    private readonly RoadsTool _roadTool;
    private RoadTool _road => _roadTool.Road;
    private RoadEditor? _roads;
    private RoadEditor.Hit? _bridgeFrom;
    /// What the bridge begun at _bridgeFrom can end on, and where on the screen it was begun, to tell a drag from a click.
    private HashSet<long> _bridgeLine = [];
    private float2 _bridgePressed;
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

    /// What the track overlay colours rail lines by: nothing, their grade, or their class (Main, Branch, Yard...).
    private enum TrackOverlay { Off, Grade, Class, ClassMap }

    /// Built track coloured by its class (with or without the mapped track beside it).
    private static bool ByClass => _overlay is TrackOverlay.Class or TrackOverlay.ClassMap;

    // The track overlay (G cycles it), left as it was each time the view opens.
    private static TrackOverlay _overlay = TrackOverlay.Grade;
    private (float2 Screen, float Grade, float Height, int Class)? _gradeAt;

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
    private long? _switch { get => _switchTool.Picked; set => _switchTool.Picked = value; }

    /// The Switches tool.
    private readonly SwitchesTool _switchTool;
    /// The Shape Track tool.
    private readonly ShapeTrackTool _shapeTool;
    private ShapeTool _shape => _shapeTool.Shape;
    private readonly FollowTool _follow = new();

    /// Parallel started on a guide: its first track follows the guide as Follow lays it, the others beside it.
    private readonly FollowTool _parallelFollow = new();
    private bool _parallelFollowing;
    private readonly YardTool _yard = new();
    private readonly YardTool _parallel = YardTool.ForParallel();

    /// Parallel's path, drawn as with Pen, and where it would run on to if clicked now.
    private readonly PenTool _parallelPen = new();
    private (double X, double Z)? _parallelPreview;
    private readonly PlaceTool _place = new();
    private (double X, double Z)? _placeGrab;
    /// Turning with the wheel: what it was before, for one undo when R is let go, and whether the wheel moved this hold of R.
    private PlacedObject? _wheelFrom;
    private bool _wheeled;
    private YardGuides? _guides;
    private SwitchHandles _handles => _switchTool.Handles;
    private string? _switchRefused;
    private float2? _areaFrom;
    private GroundQuad? _area;
    private (double Built, double Cleared) _areaLength;

    public bool CapturesMouse => _looking;
    public bool Ready => _terrain is not null;
    public bool Failed => _routeWorld.Failed;

    /// Where the camera is, in route metres east and north of the origin.
    public RoutePoint Position => new(_camera.X, -_camera.Z);

    /// start: where to put the camera (route metres east and north of the origin); the middle of the built tiles if null.
    public EditorView(RouteInfo route, RoutePoint? start = null)
    {
        _route = route;
        _start = start;
        var place = new GeoPoint(route.Origin.Latitude, route.Origin.Longitude);
        _world = new WorldDisplay(place.Lat, place.Lon, route.Projection?.GridNorth(place) ?? 0.0);
        _routeWorld = new RouteWorld(route);
        _view = new CameraView(_camera);
        _session = new Trains.TrainSession(_routeWorld, _camera);
        _tools = new ToolContext(this);
        _shapeTool = new ShapeTrackTool(_tools);
        _roadTool = new RoadsTool(_tools);
        _switchTool = new SwitchesTool(_tools, route);
        _brushes = new TerrainBrush(_tools);
        if (DevTools.Clean)
            _overlay = TrackOverlay.Off;
        if (DevTools.Skybox is { } sky)
        {
            DevTools.Skybox = null;
            _renderer.Sky.Use(sky);
        }
    }

    public void Dispose()
    {
        Save();
        _switchTool.Dispose();
        _backgroundTiles.Dispose();
        _circuitEditor?.Dispose();
        _session.Dispose();
        _routeWorld.Dispose();
        _world.Dispose();
    }

    /// Saves any edits not yet saved. True when that included track, so a copy of the network read before is stale.
    public bool Save() => _routeWorld.Opened && Save(_routeWorld.Read);

    /// Esc: drops track being drawn, or the selection. False when there was neither, so Esc means something else.
    public bool Escape()
    {
        if (_pen.Count > 0)
            _pen.Clear();
        else if (_tool == Tool.Road && _road.Busy)
            _roadTool.Escape();
        else if (_tool == Tool.Place && _bridgeFrom is not null)
            _bridgeFrom = null;
        else if (_tool == Tool.Flatten && _brushes.FlattenSample is not null)
            SyncBrush().Escape();
        else if (_tool == Tool.Place && _place.RunFrom is not null)
            (_place.RunFrom, _place.RunFirst) = (null, null);
        else if (_tool == Tool.Place && _place.Pen.Count > 0)
            _place.Pen.Clear();
        else if (_selection.Count > 0)
            _selection.Clear();
        else if (_tool == Tool.Parallel && (_parallel.Anchor is not null || _parallelPen.Count > 0 || _parallelFollow.Count > 0))
        {
            _parallelPen.Clear();
            _parallelFollow.Clear();
            _parallel.Clear();
        }
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
        _eye.StopLooking();
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
            if (!_routeWorld.TryFinishOpening())
                return;

            if (DevTools.AuditFile is { } auditFile)
            {
                DevTools.AuditFile = null;
                var report = TrackAudit.Report(TrackAudit.Run(_routeWorld.Read.Lines!, _routeWorld.Read.Objects, _route.Projection!, _route.BuiltTiles(), _route.Grid!.TileSize), _route.Projection!, _route.Name);
                File.WriteAllText(auditFile, report);
                Log.Info(report.Split('#')[1]);
            }
            _tracks = new TrackEditor(_route, _routeWorld.Read.Network);
            _circuits = new CircuitsTool(_tools, new CircuitEditor(_route));
            if (DevTools.ProfileStats)
                Log.Info(ProfileReport.Of(_routeWorld.Read.Lines!, _route.Projection!));
            if (DevTools.Tool is { } first)
            {
                DevTools.Tool = null;
                _tool = (Tool)Math.Clamp(first - 1, 0, (int)Tool.Place);
            }
            PlaceCamera(_routeWorld.Read);
            if (_topDown)
                EnterTopDown();
        }

        if (_topDown)
            _eye.Pan(dt, Blocked, WheelTurns, _screen, _terrain!.Source.HeightAt);
        else
        {
            _eye.Fly(dt, Blocked, WheelTurns, _terrain!.Source.HeightAt);
            if (DevTools.FlySpeed is { } fly)
                FlyTest(fly, _camera.Forward, dt);
        }
        using (Profiler.Sample("Editing"))
        {
            Edit(dt);
            EditTrack();
            EditCircuits();
            PollRebuild();
            _circuitEditor!.Update(_terrain.Source.Network);
            RefreshRailGraph();
            UpdateTrains(dt);
        }

        KeepWorldCurrent();

        // (Left Shift with it: F alone is the tools' own, flipping a side.)
        if (ImGui.IsKeyPressed(ImGuiKey.F) && ImGui.IsKeyDown(ImGuiKey.LeftShift) && !Keyboard.KeysBlocked)
            _renderer.Wireframe = !_renderer.Wireframe;
        if (ImGui.IsKeyPressed(ImGuiKey.G) && !Keyboard.KeysBlocked)
            _overlay = _overlay switch { TrackOverlay.Off => TrackOverlay.Grade, TrackOverlay.Grade => TrackOverlay.Class, TrackOverlay.Class => TrackOverlay.ClassMap, _ => TrackOverlay.Off };
        if (ImGui.IsKeyPressed(ImGuiKey.M) && !Keyboard.KeysBlocked)
            MaterialsWindow.Instance.Toggle();

        // T throws the switch nearest the pointer.
        if (ImGui.IsKeyPressed(ImGuiKey.T) && !Keyboard.KeysBlocked && Pick(Input.MousePosition) is { } at)
            _routeWorld.Throw(at.X, at.Z, 40.0);
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

        _routeWorld.ThrowScripted();
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
        _renderer.Sky.SetViewLimit((float)limit);
        _routeWorld.Stream(_camera, limit);
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
            if (DevTools.Zoom is { } zoom)
                _camera.Zoom = zoom;
        }
    }

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
        _brushes.Collect();
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
                _brushes.Radius = MathF.Max(TerrainBrush.MinRadius, _brushes.Radius / 1.25f);
            if (Input.KeyDown(GlfwKey.RightBracket))
                _brushes.Radius = MathF.Min(TerrainBrush.MaxRadius, _brushes.Radius * 1.25f);
            if (Input.KeyDown(GlfwKey.P))
            {
                if (_tool == Tool.Paint)
                    _brushes.Paint = (_brushes.Paint + 1) % TerrainBrush.PaintCovers.Length;
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
        if (_tool == Tool.Switches)
            _switchTool.Update(dt);

        if (_tool == Tool.Shape)
            _shapeTool.Update(dt);
        if (source.Lines is { } guided)
            (_guides ??= new YardGuides(source.Network, _route.Projection!)).Refresh(guided);
        if (_tool == Tool.Follow)
            FollowUpdate();
        if (_tool == Tool.Parallel)
            ParallelUpdate(Blocked);
        if (_tool == Tool.Place && source.Lines is { } placing)
            PlaceUpdate(source, placing);
        if (_tool == Tool.Road)
            _roadTool.Update(dt);
        BridgeUpdate();
        // "until:<seconds>" holds the ops after it till the app's been running that long.
        if (DevTools.ShapeOps is { Count: > 0 } ops && _rebuild is null
            && !(ops.Peek().StartsWith("until:", StringComparison.Ordinal) && RailWerkz.Core.Time.Time.Now < double.Parse(ops.Peek()[6..], CultureInfo.InvariantCulture)))
        {
            var op = ops.Dequeue();
            if (!op.StartsWith("until:", StringComparison.Ordinal))
                ShapeOp(source, op);
        }

        if (_tool is Tool.Track or Tool.Pen or Tool.Circuits or Tool.Switches or Tool.Shape or Tool.Follow or Tool.Place or Tool.Road or Tool.Parallel)
            return;

        if (DevTools.SculptHold is { } hold && RailWerkz.Core.Time.Time.Now >= hold.From)
        {
            if (RailWerkz.Core.Time.Time.Now > hold.Until)
            {
                DevTools.SculptHold = null;
                _brushes.LetGo();
            }
            else if (Pick(_screen * 0.5f) is { } held && Enum.TryParse<Tool>(hold.Tool, true, out var holding))
            {
                (_tool, _hit, _brushes.Radius) = (holding, held, hold.Radius);
                SyncBrush();
                if (!_brushes.Stroking)
                    _brushes.Begin();
                _brushes.Apply(dt);
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
                SyncBrush().Begin();
                for (var i = 0; i < dev.Steps; i++)
                    _brushes.Apply(1f / 30f);
                if (_brushes.End() is not null)
                    DevTools.Sculpt = null;
            }
        }

        if (IsBrush(_tool))
            SyncBrush().Update(dt);
        else
            _brushes.LetGo();
    }

    /// The brushes set to the one in hand (when one is).
    private TerrainBrush SyncBrush()
    {
        if (IsBrush(_tool))
            _brushes.Mode = (BrushMode)(_tool - Tool.Raise);
        return _brushes;
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
            case BridgeChange bridge:
                ApplyBridge(bridge.Key, undo ? bridge.Before : bridge.After);
                break;
            case CrossingEdit crossing:
                if (crossing.Roads is { } roads)
                    _tracks!.Apply(roads, undo);
                ApplyPlaced(undo ? crossing.Placed.After : crossing.Placed.Before, undo ? crossing.Placed.Before : crossing.Placed.After);
                RebuildNetwork();
                break;
        }
    }

    private bool Save(TerrainSource source)
    {
        _circuitEditor?.Save();
        _session.SaveStock();
        if (source.Unsaved)
            source.SaveEdits();
        if (!source.Network.Unsaved)
            return false;
        source.Network.Save(_route);
        return true;
    }

    // --- Switches. ---

    /// A handle let go (SwitchesTool.Change).
    private void SwitchChange(NetworkLines lines, Turnout? turnout, SwitchHandles.Change? change) => _switchTool.Change(turnout, change);

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

        // A crossing's pad shapes the ground and its roads' ends: placed, taken away or moved, the lines are worked out again.
        if (edits.Any(e => (e.Before?.Kind == PlacedKind.GradeCrossing || e.After?.Kind == PlacedKind.GradeCrossing)
            && (e.Before?.Lat, e.Before?.Lon, e.Before?.Heading, e.Before?.Width) != (e.After?.Lat, e.After?.Lon, e.After?.Heading, e.After?.Width)))
            RebuildNetwork();
    }

    /// A grade crossing placed (or put back) with the roads along it cut back to its pad's ends, as one undo.
    private void CrossingPlace(NetworkLines lines, PlacedObject after, PlacedObject? before)
    {
        var x = PlacedAtWorld(after);
        NetworkEdit? roads = null;
        if (lines.PadFor(after.Id, x.X, x.Z, after.Heading, after.Width) is { } pad)
            roads = Roads.CutAt(pad);
        if (before is not null && roads is null && before == after)
            return;
        ApplyPlaced(before, after);
        _undo.Push(new CrossingEdit(roads, new PlacedEdit(before, after)));
        _redo.Clear();
        _place.Selected = after.Id;
        if (roads is not null)
            RebuildNetwork();
    }

    private void PlacedChange(PlacedObject? before, PlacedObject? after)
    {
        ApplyPlaced(before, after);
        _undo.Push(new PlacedEdit(before, after));
        _redo.Clear();
        _place.Selected = after?.Id;
    }

    /// Lays what the pen's drawn, as one undo.
    private void PenLay()
    {
        var run = PoleRuns.Along(_place.Pen.Points(null, PoleRuns.PenStep(_place.PoleKind)), _place.PoleKind, _place.Gap);
        if (run.Problem is { } problem)
        {
            _switchRefused = problem;
            return;
        }

        var id = DateTime.UtcNow.Ticks;
        var shared = id;
        var edits = run.Poles.Select(pole => new PlacedEdit(null, Placed(_place.PoleKind, pole.X, pole.Z, pole.Heading, id: id++) with { Run = shared })).ToList();
        ApplyPlaced(edits);
        _undo.Push(new PlacedRunEdit(edits));
        _redo.Clear();
        _place.Pen.Clear();
        _place.Selected = null;
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
            if (_place.Drawn == PoleDraw.Fence)
                made = made with { Run = made.Id };
            PlacedChange(null, made);
            if (_place.Drawn == PoleDraw.Fence)
                (_place.RunFrom, _place.RunFirst, _place.RunId, _place.Selected) = ((start.X, start.Z), made.Id, made.Id, null);
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
            _switchRefused = "Closer than the pole gap";
            return;
        }

        var edits = new List<PlacedEdit>();
        // The first pole didn't know which way the run went until now.
        if (_terrain!.Source.Objects.FirstOrDefault(o => o.Id == _place.RunFirst) is { } first)
            edits.Add(new PlacedEdit(first, first with { Heading = run.Poles[0].Heading }));
        var id = DateTime.UtcNow.Ticks;
        foreach (var pole in run.Poles)
            edits.Add(new PlacedEdit(null, Placed(_place.PoleKind, pole.X, pole.Z, pole.Heading, id: id++) with { Run = _place.RunId }));
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
                PlacedChange(null, Placed(PlacedKind.Signal, signal.X, signal.Z, signal.Heading, signal.Node) with { Toward = signal.Toward });
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
                CrossingPlace(lines, Placed(PlacedKind.GradeCrossing, crossing.X, crossing.Z, crossing.Heading, null, crossing.Width), null);
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
                    _switchRefused = "Under 8 degrees: too shallow";
                else
                    Committed(_tracks.JoinCrossing(diamond));
                break;
            default:
                _switchRefused = _place.Mode == PlaceMode.Diamond ? "No tracks cross here" : _place.Mode == PlaceMode.HandThrow ? "No switch here" : "No track here";
                break;
        }
    }

    private RoadEditor Roads => _roads ??= new RoadEditor(_route, _terrain!.Source.Network, _tracks!)
    {
        Anchors = () => _terrain?.Source.Lines?.Pads.SelectMany(p => (IEnumerable<(double, double)>)[p.Start, p.End]) ?? [],
    };

    /// How many metres of ground a few pixels cover, at a place on it.
    private double PickReach(double x, double z) => _tools.PickReach(x, z);

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
                    : "Guide only: lay track first";
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
            _switchRefused = length < ShortestBridge ? "Too short for a bridge" : "Too long for one bridge";
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
                    PlacedChange(o, Placed(o.Kind, x, z, o.Heading, id: o.Id) with { Run = o.Run });
                else if (PoleRuns.At(lines, _place.Snap, o.Kind, _place.PoleOffset, x, z) is { } pole)
                    PlacedChange(o, Placed(o.Kind, pole.X, pole.Z, pole.Heading, id: o.Id) with { Run = o.Run });
                break;
            case PlacedKind.Signal when PlaceTool.SignalAt(lines, x, z) is { } signal:
                PlacedChange(o, Placed(PlacedKind.Signal, signal.X, signal.Z, signal.Heading, signal.Node, id: o.Id) with { Toward = signal.Toward });
                break;
            case PlacedKind.GradeCrossing when PlaceTool.CrossingAt(lines, x, z) is { } crossing:
                PlacedChange(o, Placed(PlacedKind.GradeCrossing, crossing.X, crossing.Z, crossing.Heading, null, o.Width, o.Opposite, id: o.Id) with { Turn = o.Turn, Style = o.Style });
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
        if (_place.Mode == PlaceMode.EditBridge)
        {
            if (!blocked && _rebuild is null)
                BridgeEditUpdate(ground, mouse);
            return;
        }

        _place.Update(lines, _tracks!, ground, PanelLength);
        // What's under the pointer, ringed as what a click would pick. (A run being laid isn't stopped by a pole in its way.)
        var under = blocked || _place.RunFrom is not null || (_place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Pen)
            ? null
            : PlaceTool.At(source.Objects, o => ProjectGround(PlacedAtWorld(o).X, PlacedAtWorld(o).Z), mouse, S(1f));
        _place.Hovered = under;
        if (blocked || _rebuild is not null)
            return;

        // The pen has the pointer and keys to itself, as in the Pen tool: to pick something, go back to One or Fence.
        if (_place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Pen)
        {
            _place.Pen.Update(mouse, _place.PenSnap(lines, ground), ground, ProjectGround, Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
            if (Input.MouseDown(GlfwMouseButton.Left))
                (_place.Selected, _switchRefused) = (null, null);
            if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
                _place.Pen.RemoveLast();
            if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && _place.Pen.Count >= 2)
                PenLay();
            return;
        }

        var picked = source.Objects.FirstOrDefault(o => o.Id == _place.Selected);
        if (Input.MouseDown(GlfwMouseButton.Left))
        {
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

        // R held with the wheel turns it a little at a time (a quarter degree a notch; Ctrl, a twentieth, for lining up
        // exactly; Shift, 5 degrees); R tapped alone turns it 15 degrees as it's let go.
        if (Input.KeyDown(GlfwKey.R))
            _wheeled = false;
        if (WheelTurns && Input.Scroll != 0f)
        {
            _wheeled = true;
            var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
            var degrees = Input.Scroll * (shift ? 5.0 : ctrl ? 0.05 : 0.25);
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
    private void PlaceFlip(PlacedObject o) => PlacedChange(o, o.Kind is PlacedKind.HandThrow or PlacedKind.GradeCrossing ? o with { Opposite = !o.Opposite } : o with { Heading = o.Heading + 180.0 });

    /// The gate turn being dragged on the slider, until it's let go (then it's one edit, for undo).
    private float? _crossingTurn;

    /// A grade crossing's surface, and its masts: turned on their posts, and which side of the road they stand.
    private void CrossingPanel(PlacedObject picked)
    {
        Kit.Caption("Surface");
        var style = (int)picked.Style;
        var styles = Enum.GetValues<CrossingStyle>();
        var names = styles.Select(CrossingStyleName).ToArray();
        var chosen = ToolPanel.Mode("surface", names, style, 2);
        if (chosen != style)
            PlacedChange(picked, picked with { Style = chosen });

        var turn = _crossingTurn ?? picked.Turn;
        ToolPanel.Value("gateturn", "Gate turn (degrees)", ref turn, -180f, 180f, "0", out var let);
        _crossingTurn = ImGui.IsItemActive() ? turn : null;
        if (let)
            PlacedChange(picked, picked with { Turn = turn });
        var nudges = new[] { ("-15", -15f), ("-5", -5f), ("+5", 5f), ("+15", 15f), ("0", 0f) };
        var hit = Kit.Buttons("gatenudge", nudges.Select(n => n.Item1).ToArray());
        if (hit >= 0)
        {
            var by = nudges[hit].Item2;
            PlacedChange(picked, picked with { Turn = by == 0f ? 0f : ((picked.Turn + by + 540f) % 360f) - 180f });
        }

        if (_terrain?.Source.Lines is { } lines && Kit.Button("cutroads", "Cut Roads"))
            CrossingPlace(lines, picked, picked);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Split roads at the crossing edges"u8);
    }

    private static string CrossingStyleName(CrossingStyle style) => style switch
    {
        CrossingStyle.Concrete => "Concrete",
        CrossingStyle.Rubber => "Rubber",
        CrossingStyle.Timber => "Timber",
        _ => "Asphalt",
    };


    private void PlacePanel(TerrainSource source)
    {
        if (_switchRefused is { } refused)
            ToolPanel.Problem(refused);
        if (source.Objects.FirstOrDefault(o => o.Id == _place.Selected) is { } picked)
            PlacePicked(picked);
        if (_place.Mode == PlaceMode.Pole)
            PolePanel();
        PlacePalette(source);
    }

    /// What can be done to the object picked.
    private void PlacePicked(PlacedObject picked)
    {
        Kit.BeginCard(picked.Kind.Name(), null, Accent);
        if (picked.Kind == PlacedKind.GradeCrossing)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var factor = imperial ? FeetPerMeter : 1f;
            var width = _crossingWidth ?? picked.Width * factor;
            ToolPanel.Value("crossingwidth", imperial ? "Road width (ft)" : "Road width (m)", ref width, PlaceTool.MinCrossingWidth * factor, PlaceTool.MaxCrossingWidth * factor, "0", out var let);
            _crossingWidth = ImGui.IsItemActive() ? width : null;
            if (let)
                PlacedChange(picked, picked with { Width = Math.Clamp(width / factor, PlaceTool.MinCrossingWidth, PlaceTool.MaxCrossingWidth) });
            CrossingPanel(picked);
        }

        if (picked.Kind == PlacedKind.Signal && _network is { } network)
        {
            // By hand for now, until there are signalling rules to set them; not saved with the route.
            var lit = network.LampsOf(picked.Id);
            Kit.Caption("Lamps (test only)");
            var first = true;
            foreach (var (lamp, _, _, _, _) in NetworkMeshes.Lamps)
            {
                if (!first)
                    ImGui.SameLine(0f, S(16f));
                first = false;
                var on = lit.HasFlag(lamp);
                if (Kit.Toggle($"lamp{lamp}", lamp.ToString(), ref on))
                    network.SetLamps(picked, on ? lit | lamp : lit & ~lamp);
            }

            switch (Kit.Buttons("lamps", ["All", "None"]))
            {
                case 0:
                    network.SetLamps(picked, SignalLamps.Red | SignalLamps.Yellow | SignalLamps.Green);
                    break;
                case 1:
                    network.SetLamps(picked, SignalLamps.None);
                    break;
            }
        }

        // Its actions, one row: turning (what can turn), the other side or round, and taking it away.
        var sided = picked.Kind is PlacedKind.HandThrow or PlacedKind.GradeCrossing;
        string[] actions = sided ? ["Other Side", "Delete"] : ["Turn 15", "Turn Round", "Delete"];
        switch (Kit.Buttons("picked", actions))
        {
            case var hit when hit >= 0 && actions[hit] == "Turn 15":
                PlaceTurn(picked, 15.0);
                break;
            case var hit when hit >= 0 && actions[hit] is "Other Side" or "Turn Round":
                PlaceFlip(picked);
                break;
            case var hit when hit >= 0 && actions[hit] == "Delete":
                PlacedChange(picked, null);
                break;
        }

        if (picked.Run is { } shared)
        {
            var all = _terrain!.Source.Objects.Where(o => o.Run == shared).ToList();
            if (all.Count > 1 && Kit.Button("deleterun", $"Delete Its Run ({all.Count})"))
            {
                var edits = all.Select(o => new PlacedEdit(o, null)).ToList();
                ApplyPlaced(edits);
                _undo.Push(new PlacedRunEdit(edits));
                _redo.Clear();
                _place.Selected = null;
            }
        }

        Kit.EndCard();
    }

    /// The crossing road width being dragged on the slider, until it's let go.
    private float? _crossingWidth;

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

    /// Parallel: a first click on a track, away from its points, runs beside that track as far as the second click. Anything
    /// else draws as Pen does, from a point, a track's end or open ground: the path is the first track and the rest go beside
    /// it, F flips the side, and Enter lays them all.
    private void ParallelUpdate(bool blocked)
    {
        if (_parallel.Anchor is not null)
        {
            YardUpdate(blocked);
            return;
        }

        var tracks = _tracks!;
        _parallelPreview = null;
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.F) && !ImGui.IsKeyDown(ImGuiKey.LeftShift))
            _parallel.Side = -_parallel.Side;
        if (blocked || _rebuild is not null)
        {
            _parallel.Show(null, null);
            return;
        }

        var mouse = Input.MousePosition;
        var node = tracks.NodeAt(mouse, _camera.X, _camera.Z, ProjectGround);
        if (!_parallelFollowing && _parallelPen.Count == 0 && node is null && Input.MouseDown(GlfwMouseButton.Left) && _hit is { } hit &&
            tracks.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround) is not null)
        {
            _parallel.Start(tracks, hit.X, hit.Z);
            return;
        }

        // Follow: clicked along the guides (from a track's end, or off a track) as Lay's Follow is.
        if (_parallelFollowing)
        {
            var guideClick = FollowClick(_guides!.PlaceAt(mouse, ProjectGround, _camera.X, _camera.Z, ViewReach, S(12f)), tracks.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround));
            ParallelFollow(tracks, guideClick ?? (_hit is { } spot ? new FollowTool.Click(spot.X, spot.Z, null, new LayPoint(spot.X, spot.Z)) : null));
            return;
        }

        LayPoint? snap = node is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : _hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;
        _parallelPen.Update(mouse, snap, _hit is { } h ? (h.X, h.Z) : null, ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
            _parallelPen.RemoveLast();
        _parallelPreview = !_parallelPen.Busy && snap is { } next ? (next.X, next.Z) : null;

        var drawn = _parallelPen.Points(_parallelPreview);
        _parallel.Show(drawn.Count >= 2 ? _parallel.PenParallel(tracks, drawn, PanelLength) : null, _hit is { } p ? (p.X, p.Z) : null);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && _parallelPen.Count >= 2 &&
            _parallel.PenParallel(tracks, _parallelPen.Points(), PanelLength, laying: true) is { Problem: null } plan && tracks.LayWays(plan.Ways) is { } edit)
        {
            _parallelPen.Clear();
            _parallel.Show(null, null);
            Committed(edit);
        }
    }

    /// Parallel along a guide: the clicks as Follow takes them, the first track the fit through them and the rest beside it.
    private void ParallelFollow(TrackEditor tracks, FollowTool.Click? click)
    {
        _parallelFollow.Update(_guides!, click);
        _parallel.Show(_parallelFollow.Preview is { } preview ? _parallel.FollowParallel(tracks, preview, null, PanelLength) : null, click is { } at ? (at.X, at.Z) : null);
        if (Input.MouseDown(GlfwMouseButton.Left) && click is { } placed)
            _parallelFollow.Add(placed);
        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Backspace))
            _parallelFollow.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter))
        {
            _parallelFollow.Update(_guides!, null);
            if (_parallelFollow.Preview is { } pieces && _parallelFollow.Points() is { } first &&
                _parallel.FollowParallel(tracks, pieces, first, PanelLength) is { Problem: null } plan && tracks.LayWays(plan.Ways) is { } edit)
            {
                _parallelFollow.Clear();
                _parallel.Show(null, null);
                Committed(edit);
            }
        }
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
        var modes = new[] { YardMode.Follow, YardMode.Crossover, YardMode.Ladder };
        var current = Array.IndexOf(modes, _yard.Mode);
        var pick = ToolPanel.Mode("yardmode", ["Follow", "Crossover", "Ladder"], current, 3, _rebuild is null);
        if (pick != current && pick >= 0)
        {
            _yard.SetMode(modes[pick]);
            _follow.Clear();
        }

        if (_yard.Mode == YardMode.Follow)
            return;

        Kit.BeginCard(_yard.Mode == YardMode.Ladder ? "Ladder" : "Crossover");
        if (_yard.Mode != YardMode.Crossover)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var factor = imperial ? FeetPerMeter : 1f;
            var spacing = _yard.Spacing * factor;
            if (ToolPanel.Value("yardspacing", imperial ? "Spacing (ft)" : "Spacing (m)", ref spacing, YardTool.MinSpacing * factor, YardTool.MaxSpacing * factor, "0.0"))
                _yard.Spacing = Math.Clamp(spacing / factor, YardTool.MinSpacing, YardTool.MaxSpacing);
        }

        if (_yard.Mode == YardMode.Ladder)
        {
            var tracks = _yard.Tracks;
            if (ToolPanel.Count("yardtracks", "Tracks", ref tracks, 1, YardTool.MaxTracks))
                _yard.Tracks = tracks;
        }

        // The switch size: any standard one (a ladder's up to No. 10), or automatic for a crossover.
        var sizes = new List<(int Number, string Name)>();
        if (_yard.Mode == YardMode.Crossover)
            sizes.Add((0, "Auto"));
        foreach (var (number, _) in Turnout.Sizes)
            if (_yard.Mode != YardMode.Ladder || number <= 10)
                sizes.Add((number, $"No. {number}"));
        var size = _yard.Mode == YardMode.Ladder && _yard.Size == 0 ? 8 : _yard.Size;
        Kit.Caption("Switch size");
        var shown = sizes.FindIndex(s => s.Number == size);
        var chosen = ToolPanel.Mode("yardsize", sizes.Select(s => s.Name).ToArray(), shown, 4);
        if (chosen != shown && chosen >= 0)
            _yard.Size = sizes[chosen].Number;

        if (_yard.Plan is { } plan)
        {
            if (plan.Problem is { } problem)
                ToolPanel.Problem(problem);
            else
                ToolPanel.Line(plan.Summary);
        }

        Kit.EndCard();
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
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        string Far(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter / 5280.0:0.0} mi") : string.Create(CultureInfo.InvariantCulture, $"{metres / 1000.0:0.0} km");
        string Near(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{metres:0} m");

        YardControls();
        if (_follow.Preview is not null)
            ToolPanel.Line($"{Near(_follow.Length)}{(_follow.Unfitted ? ", as mapped: no clean fit" : _follow.Radius < double.MaxValue ? $", tightest curve {Near(_follow.Radius)}" : ", straight")}");

        if (_guides is { GuideLength: > 0.0 } guides)
        {
            Kit.BeginCard("Guides", $"{Far(guides.DoneLength)} of {Far(guides.GuideLength)}");
            Kit.Gauge((float)(guides.DoneLength / guides.GuideLength), Accent);
            Kit.EndCard();
        }
    }

    // --- Shaping track. ---

    private void CommitShape(TerrainSource source, NetworkLine line, Alignment? shape) => _shapeTool.Commit(line, shape);

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

    // --- Track. ---

    private float2? ProjectGround(double x, double z) => Project(x, _terrain!.Source.HeightAt(x, z) + 0.6, z);

    /// The camera's view and projection (CameraView).
    private float4x4 ViewProjection => _view.ViewProjection;

    private float2? Project(double x, double y, double z) => _view.Project(x, y, z);

    private Trains.RailGraph? _railGraph => _routeWorld.Graph;
    private TrainPanel _trains => _session.Panel;

    /// The trains' rail graph, built again whenever the lines change (at first, and after an edit), with the train put
    /// back on it. With --railgraph, checked and logged too.
    /// Signals whose track nodes are missing, or no longer on one line together (an edit took one out), linked again
    /// from where they stand. Not an undo step: it only catches the links up with the track.
    private void RelinkSignals(NetworkLines lines)
    {
        var linesOf = new Dictionary<long, List<int>>();
        foreach (var line in lines.Lines)
            if (line.Rail)
                foreach (var (node, _) in line.Nodes)
                {
                    if (!linesOf.TryGetValue(node, out var list))
                        linesOf[node] = list = [];
                    list.Add(line.Index);
                }

        bool Together(long a, long b) => a != b && linesOf.TryGetValue(a, out var la) && linesOf.TryGetValue(b, out var lb) && la.Intersect(lb).Any();

        var edits = new List<PlacedEdit>();
        foreach (var o in _terrain!.Source.Objects)
        {
            if (o.Kind != PlacedKind.Signal || (o is { Node: { } n, Toward: { } t } && Together(n, t)))
                continue;
            var (x, z) = PlacedAtWorld(o);
            if (PlaceTool.SignalAt(lines, x, z) is { } signal && (signal.Node != o.Node || signal.Toward != o.Toward))
                edits.Add(new PlacedEdit(o, o with { Node = signal.Node, Toward = signal.Toward }));
        }

        if (edits.Count == 0)
            return;
        ApplyPlaced(edits);
        Log.Info($"Signals: {edits.Count} linked to their track again");
    }

    /// The multiplayer session this view's part of, if it is (see BuilderScreen.Play).
    public Multiplayer.NetPlay? Net { get => _session.Net; set => _session.Net = value; }

    /// Tells the crew something (shown in the train window).
    public void Notify(string text) => _session.Notify(text);


    private void RefreshRailGraph()
    {
        if (!_routeWorld.RefreshGraph())
            return;
        var lines = _terrain!.Source.Lines!;
        _session.TrackChanged();
        RelinkSignals(lines);
        if (DevTools.TakeExport())
            Export();
        if (DevTools.RailGraphReport)
            Log.Info(Trains.RailGraphCheck.Report(_railGraph, lines));
    }

    /// The trains (TrainSession): placing (which takes the left button off the tools), picking with Free Look, running
    /// them on, and the camera along with them.
    private void UpdateTrains(float dt)
    {
        _session.Update(dt, _view, _eye, Blocked, DevTools.Lay is null && _rebuild is null, _tool == Tool.Look && !_audioEditor.WantsPointer);
        if (_trains.Placing)
            _tool = Tool.Look;
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
            _insertAt = _hover is null && _grabbed is null && _boxFrom is null ? tracks.SegmentAt(mouse, _camera.X, _camera.Z, ProjectGround) : null;
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
        var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
        if (Input.MouseDown(GlfwMouseButton.Left) && ctrl && _hover is null && _insertAt is { } at)
        {
            // A new point in the track, selected, ready to drag.
            if (tracks.InsertPoint(at.Way, at.Segment, at.X, at.Z, out var made) is { } inserted)
            {
                Committed(inserted);
                _selection.Clear();
                _selection.Add(made);
                _insertAt = null;
            }

            return;
        }

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
        if (Input.KeyDown(GlfwKey.I))
            Committed(tracks.Simplify(_selection));
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
        ("Simplify", "I", (t, s) => t.Simplify(s)),
        ("Join", "J", (t, s) => t.Join(s)),
        ("Split", "U", (t, s) => t.Split(s)),
        ("Delete", "Del", (t, s) => t.DeleteMany(s)),
    ];

    /// The track circuits tool (CircuitsTool).
    private void EditCircuits()
    {
        if (_tool == Tool.Circuits)
            _circuits?.Update(0f);
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
        // Track laid or moved across another gets a diamond there, in the same edit.
        if (_tracks is not null)
        {
            edit = _tracks.WithDiamonds(edit)!;
            if (_tracks.ShallowCrossings > 0)
                _switchRefused = $"Tracks cross under {TrackEditor.MinDiamondDegrees:0} degrees: too shallow for a diamond";
        }

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

    private void Rebuild((int X0, int Z0, int X1, int Z1)? rect)
    {
        if (rect is { } r)
            _terrain!.Invalidate(r.X0, r.Z0, r.X1, r.Z1);
    }

    /// Where the ray through a point on the screen first meets the ground (CameraView.Pick).
    private double3? Pick(float2 point) => _view.Pick(point, _terrain!.Source.HeightAt);

    /// The brush on the ground: a ring of points dropped onto the terrain, projected to the screen.
    private void DrawBrush(ImDrawListPtr draw)
    {
        if (_tool is Tool.Track or Tool.Pen)
        {
            _tracks!.Draw(draw, _camera.X, _camera.Z, ProjectGround, _hover, _selection, _dragOffset);
            // Where Ctrl+click would put a new point: a ring on the track under the pointer, lit while Ctrl's held.
            if (_tool == Tool.Track && _insertAt is { } at && ProjectGround(at.X, at.Z) is { } spot)
            {
                var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
                draw.AddCircleFilled(spot, S(4f), ctrl ? Rgba.White : Rgba.White.WithAlpha(0.55f), 16);
                draw.AddCircle(spot, S(8f), (ctrl ? Style.Accent : Rgba.White).WithAlpha(ctrl ? 1f : 0.45f), 20, S(1.5f));
            }
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

        if (_tool is Tool.Look or Tool.Switches or Tool.Shape or Tool.Follow or Tool.Place or Tool.Road or Tool.Parallel)
            return;
        SyncBrush().Draw(draw);
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _scene.Camera = _camera;
        (_scene.Terrain, _scene.Network) = (_terrain, _network);
        _scene.Models = _trains.Draws;
        _scene.Outlines = _trains.Outlines;
        _scene.OutlineStyle = new OutlineStyle(Style.Clear.Float4, Style.Accent.Float4, S(1f));
        _scene.Lights = _trains.Lamps;
        (_scene.Smoke, _scene.Haze) = (_trains.Smoke, _trains.Haze);
        _world.Render(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _world.Composite(cmd);

    /// The brush or the track being edited, over the world; until the terrain's open, a note that it's loading.
    public void Draw(ImDrawListPtr draw, float2 size)
    {
        _screen = size;
        if (_terrain is null)
        {
            Canvas.Text(draw, Medium, S(15f), size * 0.5f, TextDim, Failed ? "TERRAIN COULDN'T BE OPENED" : "LOADING TERRAIN", Align.Center);
            return;
        }

        using (Profiler.Sample("Background"))
            DrawBackground(draw);
        var overlays = Profiler.Sample("Overlays");
        _gradeAt = null;
        if (_tool == Tool.Circuits)
            _circuits?.Draw(draw);
        else if (_overlay != TrackOverlay.Off)
        {
            if (_overlay == TrackOverlay.ClassMap)
                _guides?.DrawClasses(draw, ProjectGround, _camera.X, _camera.Z, ViewReach, size);
            if (_terrain.Source.Lines is { } graded)
            {
                // From above, as far as the view reaches; in flight, a few kilometres.
                var reach = _topDown
                    ? (_camera.Y - _terrain.Source.HeightAt(_camera.X, _camera.Z)) * Math.Tan(_camera.FieldOfView * 0.5) * (size.X / size.Y) * 1.2
                    : 3000.0;
                _grades.Draw(draw, graded, _view, reach, ByClass, Blocked);
                _gradeAt = _grades.At;
            }
        }
        overlays.Dispose();
        DrawBrush(draw);
        using (Profiler.Sample("Train overlay"))
            _trains.Overlay(draw, Project, new double3(_camera.X, _camera.Y, _camera.Z), new float2(0f, _headerBottom), new float2(size.X, MathF.Min(size.Y, _footerTop)));
        if (_tool == Tool.Switches)
            _switchTool.Draw(draw);
        if (_tool == Tool.Shape)
            _shapeTool.Draw(draw);
        if (_tool is Tool.Follow or Tool.Parallel || (_tool is Tool.Pen or Tool.Shape && _overlay != TrackOverlay.ClassMap))
            _guides?.Draw(draw, ProjectGround, _camera.X, _camera.Z, ViewReach);
        if (_tool is Tool.Follow or Tool.Pen or Tool.Parallel || (_tool == Tool.Place && _place.Mode is PlaceMode.Track or PlaceMode.Bridge))
            _tracks?.DrawAttach(draw, _camera.X, _camera.Z, ProjectGround, Input.MousePosition);
        if (_tool == Tool.Follow)
            _follow.Draw(draw, ProjectGround, Input.MousePosition);
        if (_tool == Tool.Follow)
            _yard.Draw(draw, ProjectGround, S(1f));
        if (_tool == Tool.Parallel)
        {
            _parallelPen.Draw(draw, ProjectGround, _parallelPreview);
            _parallel.Draw(draw, ProjectGround, S(1f));
        }
        if (_tool == Tool.Place && _place.Mode == PlaceMode.EditBridge)
            BridgeDraw(draw);
        else if (_tool == Tool.Place && _terrain is { } shown)
            _place.Draw(draw, ProjectGround, shown.Source.Objects, PlacedAtWorld, S(1f));
        if (_tool == Tool.Road)
        {
            _roadTool.Draw(draw);
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
            var grade = string.Create(CultureInfo.InvariantCulture, $"Grade {at.Grade * 100:0.00}%   {height}");
            PenTool.Label(draw, at.Screen, ByClass ? $"{RailColours.Name(at.Class)}   {grade}" : grade,
                ByClass ? RailColours.Of(at.Class) : GradeColor(at.Grade));
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
    private void DrawPicked(ImDrawListPtr draw, double[] x, double[] z, Rgba color) => _tools.DrawPicked(draw, x, z, color);

    private static Rgba GradeColor(float grade) => GradeOverlay.GradeColor(grade);

    /// The track coloured by its grade or class (G), and the grade under the pointer.
    private readonly GradeOverlay _grades = new();

    // --- What the tools are given (IToolHost, through ToolContext). ---

    RouteWorld IToolHost.World => _routeWorld;
    CameraView IToolHost.View => _view;
    bool IToolHost.Blocked => Blocked;
    double3? IToolHost.Hit => _hit;
    bool IToolHost.TopDown => _topDown;
    double IToolHost.Reach => ViewReach;
    bool IToolHost.Rebuilding => _rebuild is not null;
    string? IToolHost.Refused { get => _switchRefused; set => _switchRefused = value; }
    TrackEditor IToolHost.Tracks => _tracks!;
    RoadEditor IToolHost.Roads => Roads;
    void IToolHost.Record(object? edit) => Record(edit);
    void IToolHost.Commit(NetworkEdit? edit) => Committed(edit);
    void IToolHost.RebuildNetwork() => RebuildNetwork();
    void IToolHost.ChangePlaced(PlacedObject? before, PlacedObject? after) => PlacedChange(before, after);
    PlacedObject IToolHost.NewPlaced(PlacedKind kind, double x, double z, double heading, long? node) => Placed(kind, x, z, heading, node);
    void IToolHost.FlyTo(double x, double z) => FlyTo(x, z);

    // --- Menus, the side panel and windows (ImGui). ---

    public void MenuFile()
    {
        var source = _terrain?.Source;
        if (ImGui.MenuItem("Save", "Ctrl+S", false, source is not null && (source.Unsaved || source.Network.Unsaved)))
            Save(source!);
        ImGui.Separator();
        if (ImGui.MenuItem("Export for Players and Servers", "", false, _railGraph is not null && _export is not { IsCompleted: false } && _route.Zone is not null))
            Export();
        if (_exportStatus is { } status)
            ImGui.MenuItem(status, "", false, false);
    }

    private Task? _export;
    private volatile string? _exportStatus;

    /// Saves, then writes the client packs and server track (WorldExport) in the background, from the rail graph as it
    /// stands.
    private void Export()
    {
        if (_terrain?.Source is not { } source || _railGraph is not { } graph)
            return;
        if (source.Unsaved || source.Network.Unsaved)
            Save(source);
        var objects = source.Objects.ToList();
        _exportStatus = "Exporting...";
        _export = Task.Run(() =>
        {
            try
            {
                var result = Routes.WorldExport.Run(_route, graph, objects, s => _exportStatus = s);
                _exportStatus = result.Lines.Any(l => l.StartsWith("PROBLEM")) ? "Exported, with problems: see the log" : $"Exported to {result.Folder}";
            }
            catch (Exception e)
            {
                Log.Error($"Export: {e}");
                _exportStatus = $"Export failed: {e.Message}";
            }
        });
    }

    public void MenuEdit()
    {
        if (ImGui.MenuItem("Undo", "Ctrl+Z", false, CanUndo))
            UndoLast();
        if (ImGui.MenuItem("Redo", "Ctrl+Y", false, CanRedo))
            RedoLast();
        ImGui.Separator();
        var picked = _tool == Tool.Place ? _terrain?.Source.Objects.FirstOrDefault(o => o.Id == _place.Selected) : null;
        if (ImGui.MenuItem(picked is null ? "Delete Selected" : $"Delete {picked.Kind.Name()}", "Del", false, picked is not null))
            PlacedChange(picked, null);
    }

    /// The route's own items, besides Track Materials (the screen's): the audit, and fitting the track again.
    public void MenuRoute()
    {
        var source = _terrain?.Source;
        if (ImGui.MenuItem("Audit Route...", "", _audit.Open, source is not null))
        {
            _audit.Open = !_audit.Open;
            if (_audit.Open)
                RunAudit();
        }

        if (ImGui.MenuItem("Solve Track Again...", "", false, source is not null && _rebuild is null))
            _confirmSolve = true;
    }

    public void MenuView()
    {
        if (ImGui.MenuItem("Track Grades"u8, "G"u8, _overlay == TrackOverlay.Grade))
            _overlay = _overlay == TrackOverlay.Grade ? TrackOverlay.Off : TrackOverlay.Grade;
        if (ImGui.MenuItem("Track Classes"u8, "G"u8, _overlay == TrackOverlay.Class))
            _overlay = _overlay == TrackOverlay.Class ? TrackOverlay.Off : TrackOverlay.Class;
        if (ImGui.MenuItem("Track Classes + Map"u8, "G"u8, _overlay == TrackOverlay.ClassMap))
            _overlay = _overlay == TrackOverlay.ClassMap ? TrackOverlay.Off : TrackOverlay.ClassMap;
        if (ImGui.MenuItem("Wireframe", "Shift+F",_renderer.Wireframe, true))
            _renderer.Wireframe = !_renderer.Wireframe;
        ImGui.Separator();
        if (ImGui.MenuItem("Audio Editor", "", _audioEditor.Open, true))
            _audioEditor.Open = !_audioEditor.Open;
    }

    /// Solve Track Again was picked: asked about before it's done, since it throws the fitted track away.
    private bool _confirmSolve;

    private void ConfirmSolve()
    {
        if (_confirmSolve)
        {
            ImGui.OpenPopup("Solve Track Again?"u8);
            _confirmSolve = false;
        }

        ImGui.SetNextWindowSize(new float2(420f, 0f), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal("Solve Track Again?"u8, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ToolPanel.Line("Refits all track. Can't be undone.");
        ImGui.Dummy(new float2(1f, S(6f)));
        var answer = Kit.Buttons("solveagain", ["Solve Again", "Cancel"]);
        if (answer == 0)
            SolveTrackAgain();
        if (answer >= 0 || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// The route audit's window: open, what it found last, and whether it's still looking.
    private sealed class AuditState
    {
        public bool Open;
        public List<AuditFinding>? Findings;
        public Task<List<AuditFinding>>? Running;
    }

    private readonly AuditState _audit = new();

    /// The Audio Editor (View, Audio Editor): the trains' sound tuned by ear, with handles on the picked locomotive.
    private readonly AudioEditor _audioEditor = new();

    private void RunAudit()
    {
        if (_terrain?.Source is not { Lines: { } lines } source || _audit.Running is { IsCompleted: false })
            return;
        var objects = source.Objects.ToList();
        var (projection, built, size) = (_route.Projection!, _route.BuiltTiles(), _route.Grid!.TileSize);
        _audit.Running = Task.Run(() => TrackAudit.Run(lines, objects, projection, built, size));
    }

    /// Everything the audit found, by check, worst first; clicking one flies there.
    private void AuditWindow()
    {
        if (_audit.Running is { IsCompleted: true } done)
        {
            _audit.Findings = done.IsCompletedSuccessfully ? done.Result : [];
            if (!done.IsCompletedSuccessfully)
                Log.Error($"Audit: {done.Exception?.InnerException?.Message}");
            _audit.Running = null;
        }

        if (!_audit.Open)
            return;
        ImGui.SetNextWindowSize(new float2(380f, 460f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Route Audit"u8, ref _audit.Open))
        {
            ToolPanel.Header("Route audit", null, _audit.Findings is not { } counted ? "Checking..."
                : counted.Count == 0 ? "Nothing found" : $"{counted.Count} found, {counted.Count(f => f.Severe)} serious");
            if (Kit.Button("auditagain", _audit.Running is null ? "Run Again" : "Checking...", 0f, ButtonKind.Secondary, _audit.Running is null))
                RunAudit();
            if (_audit.Findings is { } findings)
            {
                var index = 0;
                foreach (var check in findings.GroupBy(f => f.Check).OrderByDescending(g => g.Any(f => f.Severe)).ThenBy(g => g.Key))
                {
                    if (!ToolPanel.Section($"check{check.Key}", check.Key, check.Count().ToString(CultureInfo.InvariantCulture)))
                        continue;
                    foreach (var finding in check)
                        if (ToolPanel.Item($"finding{index++}", null, finding.Detail, finding.Severe ? "SERIOUS" : null, false, swatch: finding.Severe ? Style.Stop : Style.Caution))
                            FlyTo(finding.X, finding.Z);
                }
            }
        }

        ImGui.End();
    }

    /// The camera to a place: over it, top down; flying, stood back from it and looking down at it, facing as it faced.
    private void FlyTo(double x, double z)
    {
        if (_topDown)
        {
            MoveTo(new RoutePoint(x, -z));
            return;
        }

        var (sy, cy) = MathF.SinCos(_camera.Yaw);
        _camera.Pitch = -0.6f;
        _camera.X = x - sy * 50.0;
        _camera.Z = z + cy * 50.0;
        _camera.Y = _terrain!.Source.HeightAt(x, z) + 35.0;
    }

    /// Whether the side panel has anything to show.
    public bool HasPanel => _terrain is null || _tool != Tool.Look || _topDown;

    /// The track overlay's key and its title, for the footer while it's shown (the Circuits tool draws its own colours
    /// instead). The class key lists only the classes the route has.
    public (string Title, (Rgba Color, string Name)[] Entries)? OverlayKey
    {
        get
        {
            if (_tool == Tool.Circuits || _overlay == TrackOverlay.Off)
                return null;
            if (_overlay == TrackOverlay.Grade)
                return ("GRADES", [(GradeColor(0f), "<1%"), (GradeColor(0.015f), "1-2%"), (GradeColor(0.03f), "2%+")]);

            var present = 0;
            if (_terrain?.Source.Lines is { } lines)
                foreach (var line in lines.Lines)
                    if (line.Rail)
                        present |= 1 << line.Class;
            if (_overlay == TrackOverlay.ClassMap && _terrain?.Source.Network is { } network)
                foreach (var way in network.Ways.Values)
                    if (way.Rail && RouteNetwork.IsGuide(way))
                        present |= 1 << way.Class;
            return (_overlay == TrackOverlay.ClassMap ? "CLASS + MAP (DASHED)" : "TRACK CLASS", [.. Enum.GetValues<RailClass>().Where(c => (present & (1 << (int)c)) != 0)
                .Select(c => (RailColours.Of((int)c), RailColours.Name((int)c)))]);
        }
    }

    /// The camera and the tool in hand, for the side panel.
    public void Panel()
    {
        if (_terrain is null)
            return;

        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var (title, key) = PanelTitle;
        ToolPanel.Header(title, key);
        if (IsBrush(_tool))
            BrushPanel(imperial);
        if (_tool == Tool.Parallel)
            ParallelPanel();
        if (_tool == Tool.Pen)
            PenPanel(imperial);
        if (_tool == Tool.Circuits)
            _circuits?.Panel(imperial);
        if (_tool == Tool.Switches)
            _switchTool.Panel(imperial);
        if (_tool == Tool.Shape)
            _shapeTool.Panel(imperial);
        if (_tool == Tool.Follow)
            FollowPanel();
        if (_tool == Tool.Track)
            SelectionPanel();
        if (_tool is Tool.Track or Tool.Pen or Tool.Follow or Tool.Parallel || (_tool == Tool.Place && _place.Mode == PlaceMode.Track))
        {
            ClassPanel();
            BedPanel();
        }
        if (_tool == Tool.Place)
            PlacePanel(_terrain.Source);
        if (_tool == Tool.Road)
            _roadTool.Panel(imperial);

        if (_topDown)
        {
            Kit.BeginCard("Background");
            var shown = !_showBackground ? 0 : MapTiles.Background == MapBackground.Map ? 1 : 2;
            var pick = ToolPanel.Mode("background", ["Off", "Map", "Aerial"], shown, 3);
            if (pick != shown)
                (_showBackground, MapTiles.Background) = pick switch { 0 => (false, MapTiles.Background), 1 => (true, MapBackground.Map), _ => (true, MapBackground.Satellite) };
            if (_showBackground)
            {
                ToolPanel.Value("opacity", "Opacity", ref _backgroundOpacity, 0.1f, 1f, "0.0");
                ToolPanel.Hint(MapTiles.Attribution);
            }

            Kit.EndCard();
        }

    }

    /// The side panel's header: the tool in hand (the Place tool by its button) and its key.
    private (string Title, string? Key) PanelTitle => _tool switch
    {
        Tool.Track => ("Edit", "7"),
        Tool.Pen => ("Pen", "8"),
        Tool.Shape => ("Shape", "-"),
        Tool.Follow => ("Lay", "="),
        Tool.Parallel => ("Parallel", "P"),
        Tool.Switches => ("Switches", "0"),
        Tool.Circuits => ("Circuits", "9"),
        Tool.Road => ("Roads", ";"),
        Tool.Place => (GroupOf(_place.Mode) switch
        {
            PlaceGroup.Assemblies => "Assembly",
            PlaceGroup.Structures => "Structures",
            PlaceGroup.Signals => "Signals",
            _ => "Lineside",
        }, null),
        _ when IsBrush(_tool) => ("Terrain", "2-6"),
        _ => ("Free Look", "1"),
    };

    /// What the pen's drawn so far: its points, length, tightest curve and the ground's grade along it.
    private void PenPanel(bool imperial)
    {
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
        if (_penMeasure is not { } m)
            return;
        Kit.BeginCard("Drawn", $"{_pen.Count} point{(_pen.Count == 1 ? "" : "s")}");
            Kit.Stats(
            [
                new Stat("Length", Length(m.Length)),
                new Stat("Tightest curve", double.IsInfinity(m.MinRadius) ? "Straight" : Length(m.MinRadius)),
                new Stat("Ground grade", string.Create(CultureInfo.InvariantCulture, $"{m.Grade * 100:0.00}%")),
                new Stat("Steepest", string.Create(CultureInfo.InvariantCulture, $"{m.SteepestGrade * 100:0.00}%")),
            ], 2);
        Kit.EndCard();
    }

    /// The Edit tool's selection, and what can be done to it.
    private void SelectionPanel()
    {
        Kit.BeginCard("Selection", $"{_selection.Count} point{(_selection.Count == 1 ? "" : "s")}");
        var tools = SelectionTools;
        for (var i = 0; i < tools.Length; i += 2)
        {
            var count = Math.Min(2, tools.Length - i);
            var labels = new string[count];
            var enabled = new bool[count];
            for (var k = 0; k < count; k++)
                (labels[k], enabled[k]) = (tools[i + k].Name, _selection.Count > 0);
            var hit = Kit.Buttons($"selection{i}", labels, enabled);
            if (hit >= 0)
                Committed(tools[i + hit].Act(_tracks!, _selection));
        }

        Kit.EndCard();
    }

    /// Saves a switch's size (SwitchesTool.PickSize).
    private void PickSize(long node, int? size) => _switchTool.PickSize(node, size);

    public void Windows()
    {
        using (Profiler.Sample("Tool strip"))
            ToolStrip();
        using (Profiler.Sample("Train window"))
            _trains.Window(_headerBottom);
        AuditWindow();
        _audioEditor.Window(_trains);
        _audioEditor.Gizmos(_trains, Project, _camera, _screen.Y, Blocked);
        ConfirmSolve();
    }

    /// The editor's lines in Frame Statistics.
    public void DebugStats()
    {
        if (_terrain is not { } terrain || _network is not { } network)
            return;
        var source = terrain.Source;
        DebugWindows.Heading("Route");
        DebugWindows.Row("Tiles", $"{source.Tiles.Count}");
        DebugWindows.Row("Lines / ways", $"{source.Lines?.Lines.Count ?? 0:#,0} / {source.Network.Ways.Count:#,0}");
        DebugWindows.Row("Switches", $"{network.Turnouts}");
        DebugWindows.Row("Edits", $"{_undo.Count} to undo, {(source.Unsaved || source.Network.Unsaved ? "unsaved" : "saved")}");
        DebugWindows.Heading("Streaming");
        DebugWindows.Row("Terrain patches", $"{terrain.PatchesInView} seen, {terrain.DrawnPatches} drawn, {terrain.PatchCount} held");
        DebugWindows.Row("Terrain building", $"{terrain.Building} building, {terrain.Waiting} waiting");
        DebugWindows.Row("Line chunks", $"{network.DrawnChunks} drawn, {network.Building} building");
    }

    /// The Camera window's rows: where the camera is and how it's looking, the light, and the screen; and a launch line that
    /// opens the route with the camera exactly here (--camera takes metres above the ground under it, degrees, yaw 0 north).
    public string CameraInfo()
    {
        static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
        var geo = _route.Projection!.Inverse(new RoutePoint(_camera.X, -_camera.Z));
        var ground = _terrain?.Source.HeightAt(_camera.X, _camera.Z) ?? 0f;
        var above = _camera.Y - ground;
        var yaw = _camera.Yaw * 180.0 / Math.PI;
        yaw -= Math.Floor(yaw / 360.0) * 360.0;
        var pitch = _camera.Pitch * 180.0 / Math.PI;
        var s = Settings.Current;
        var hour = s.SkyHour;

        DebugWindows.Heading("Place");
        DebugWindows.Row("Route", $"{_route.Name} ({_route.Id})");
        DebugWindows.Row("View", _topDown ? "2D Editor" : "3D Editor");
        DebugWindows.Row("Lat, lon", $"{F(geo.Lat, "0.0000000")}, {F(geo.Lon, "0.0000000")}");
        DebugWindows.Row("World X Y Z", $"{F(_camera.X, "0.00")}  {F(_camera.Y, "0.00")}  {F(_camera.Z, "0.00")}");
        DebugWindows.Row("Above ground", $"{F(above, "0.00")} m  (ground {F(ground, "0.00")} m)");
        DebugWindows.Heading("Looking");
        DebugWindows.Row("Yaw (0 = north)", $"{F(yaw, "0.00")} deg");
        DebugWindows.Row("Pitch", $"{F(pitch, "0.00")} deg");
        DebugWindows.Row("Zoom, field of view", $"{F(_camera.Zoom, "0.00")}x / {F(_camera.FieldOfView * 180.0 / Math.PI, "0.0")} deg");
        DebugWindows.Heading("Light");
        DebugWindows.Row("Sky date, hour", $"{s.SkyYear:0000}-{s.SkyMonth:00}-{s.SkyDay:00}  {(int)hour:00}:{(int)((hour - (int)hour) * 60f):00}");
        DebugWindows.Row("Clouds", $"{s.CloudType}, {F(s.CloudCoverage, "0.00")}");
        DebugWindows.Row("Screen", $"{_screen.X:0} x {_screen.Y:0}");

        return string.Create(CultureInfo.InvariantCulture,
            $"--route {_route.Id} {(_topDown ? "--view2d" : "--view")} --camera {geo.Lat:0.0000000} {geo.Lon:0.0000000} {above:0.00} {yaw:0.00} {pitch:0.00} --zoom {_camera.Zoom:0.00} --skytime {s.SkyYear:0000}-{s.SkyMonth:00}-{s.SkyDay:00} {hour:0.000} --cloudtype {s.CloudType} --clouds {s.CloudCoverage:0.00}");
    }

    /// The controls for flying and for the tool in hand, for the footer.
    public (string Key, string Action)[] Hints
    {
        get
        {
            (string, string)[] tool = _tool switch
            {
                Tool.Look => [("RMB", _topDown ? "Pan" : "Look"), ("WASD", "Move"), ("WHEEL", _topDown ? "Zoom" : "Speed"), .. (_topDown ? ((string, string)[])[] : [("ALT+WHEEL", _camera.Zoom <= 1.01f ? "Zoom" : string.Create(CultureInfo.InvariantCulture, $"Zoom {_camera.Zoom:0.0}x"))]), ("G", "Overlay"), ("T", "Throw switch"), .. (_trains.Train is null ? ((string, string)[])[] : [("LMB", "Pick vehicle or coupling")])],
                Tool.Road => _roadTool.Hints,
                Tool.Parallel => _parallel.Anchor is not null
                    ? [("LMB", "End"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")]
                    : _parallelFollowing ? [("LMB", "Along a guide"), ("ENTER", "Lay tracks"), ("F", _parallel.Side > 0 ? "Others right" : "Others left"), ("BACKSPACE", "Remove click"), ("ESC", "Cancel")]
                    : [("LMB", "Point or track"), ("DRAG", "Curve"), ("ENTER", "Lay tracks"), ("F", _parallel.Side > 0 ? "Others right" : "Others left"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")],
                Tool.Switches => _switchTool.Hints,
                Tool.Place when _place.Mode == PlaceMode.EditBridge => [("LMB", "Pick bridge, drag pier"), ("DEL", "Remove pier"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.RunFrom is not null => [("LMB", "Lay to here"), ("ESC", "End run"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Fence && _place.Selected is null => [("LMB", "Start run"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Pen => [("LMB", "Point"), ("DRAG", "Curve"), ("ENTER", "Lay"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel"), ("CTRL+Z", "Undo")],
                Tool.Place when _place.Mode == PlaceMode.Track && _place.Selected is null => [("LMB", "Lay piece"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Mirror"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
                Tool.Place => [("LMB", "Place, pick, drag"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Flip"), ("DEL", "Delete"), ("CTRL+Z", "Undo")],
                Tool.Follow when _yard.Mode != YardMode.Follow => [("LMB", "Pick track"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")],
                Tool.Follow => [("LMB", "Guide or track"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove click"), ("CTRL+Z", "Undo")],
                Tool.Shape => _shapeTool.Hints,
                _ when IsBrush(_tool) => SyncBrush().Hints,
                Tool.Track => [("LMB", "Select, drag"), ("SHIFT", "Add"), ("CTRL+LMB", "Insert point"), ("L", "Straighten"), ("K", "Smooth"), ("I", "Simplify"), ("J", "Join"), ("U", "Split"), ("DEL", "Delete")],
                Tool.Circuits => _circuits?.Hints ?? [],
                Tool.Pen => [("LMB", "Point"), ("DRAG", "Curve"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")],
                _ => [("LMB", _tool.ToString()), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
            };
            return tool;
        }
    }
}

/// A grade crossing placed with the roads along it cut back to its ends: both undone together.
public sealed record CrossingEdit(NetworkEdit? Roads, PlacedEdit Placed);
