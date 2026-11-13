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
    /// The Edit Track tool, and its selection (which the class and bed panels apply to).
    private readonly EditTrackTool _editTrack;
    private HashSet<long> _selection => _editTrack.Selection;

    /// The Pen tool.
    private readonly PenTrackTool _penTrack;
    private PenTool _pen => _penTrack.Pen;
    /// The Roads tool.
    private readonly RoadsTool _roadTool;
    private RoadTool _road => _roadTool.Road;
    private RoadEditor? _roads;
    private PenTool.Measure? _penMeasure => _penTrack.Measure;
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
    private bool WheelTurns => _tool == Tool.Place && _terrain is not null && _objects.WheelTurns;

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
    /// The Lay Yard tool: along the guides, crossovers and ladders.
    private readonly LayYardTool _layYard;
    private FollowTool _follow => _layYard.Follow;
    private YardTool _yard => _layYard.Yard;

    /// The Parallel tool.
    private readonly ParallelTool _parallelTool;
    private FollowTool _parallelFollow => _parallelTool.Follow;
    private bool _parallelFollowing { get => _parallelTool.Following; set => _parallelTool.Following = value; }
    private YardTool _parallel => _parallelTool.Yard;
    private PenTool _parallelPen => _parallelTool.Pen;
    /// The Objects tool (its four strip buttons).
    private readonly ObjectsTool _objects;
    private PlaceTool _place => _objects.Place;
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
        _layYard = new LayYardTool(_tools, route);
        _editTrack = new EditTrackTool(_tools);
        _penTrack = new PenTrackTool(_tools, _editTrack.Selection);
        _objects = new ObjectsTool(_tools, route);
        _parallelTool = new ParallelTool(_tools, route);
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
            _penTrack.Escape();
        else if (_tool == Tool.Road && _road.Busy)
            _roadTool.Escape();
        else if (_tool == Tool.Place && _objects.Escape())
        {
            // (Let go of whatever the Objects tool had under way.)
        }
        else if (_tool == Tool.Flatten && _brushes.FlattenSample is not null)
            SyncBrush().Escape();
        else if (_selection.Count > 0)
            _selection.Clear();
        else if (_tool == Tool.Parallel && (_parallel.Anchor is not null || _parallelPen.Count > 0 || _parallelFollow.Count > 0))
            _parallelTool.Escape();
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
            _layYard.Update(dt);
        if (_tool == Tool.Parallel)
            _parallelTool.Update(dt);
        if (_tool == Tool.Place)
            _objects.Update(dt);
        if (_tool == Tool.Road)
            _roadTool.Update(dt);
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
                _objects.ApplyBridge(bridge.Key, undo ? bridge.Before : bridge.After);
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

    // --- Placing objects (ObjectsTool). ---

    private RoadEditor Roads => _roads ??= new RoadEditor(_route, _terrain!.Source.Network, _tracks!)
    {
        Anchors = () => _terrain?.Source.Lines?.Pads.SelectMany(p => (IEnumerable<(double, double)>)[p.Start, p.End]) ?? [],
    };

    /// How many metres of ground a few pixels cover, at a place on it.
    private double PickReach(double x, double z) => _tools.PickReach(x, z);

    private (double X, double Z) PlacedAtWorld(PlacedObject o) => _objects.PlacedAtWorld(o);
    private PlacedObject Placed(PlacedKind kind, double x, double z, double heading, long? node = null) => _objects.Placed(kind, x, z, heading, node);
    private void ApplyPlaced(PlacedObject? gone, PlacedObject? added) => _objects.ApplyPlaced(gone, added);
    private void ApplyPlaced(IReadOnlyList<PlacedEdit> edits) => _objects.ApplyPlaced(edits);
    private void PlacedChange(PlacedObject? before, PlacedObject? after) => _objects.PlacedChange(before, after);
    private void CrossingPlace(NetworkLines lines, PlacedObject after, PlacedObject? before) => _objects.CrossingPlace(lines, after, before);
    private static PlaceGroup GroupOf(PlaceMode mode) => ObjectsTool.GroupOf(mode);

    // --- Following guides (LayYardTool, ParallelTool, YardLaying). ---

    private FollowTool.Click? FollowClick(GuidePlace? guide, (long Way, int Segment, double X, double Z)? segment) => YardLaying.Click(_guides!, _tracks!, guide, segment);

    private string PanelLength(double metres) => YardLaying.Length(metres);

    private void YardPress(NetworkLines lines, double x, double z) => YardLaying.Press(_tools, _route, ActiveYard, lines, x, z);

    private void FollowLay() => _layYard.Lay();

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
            _editTrack.Idle();
            _penTrack.Idle();
            return;
        }

        if (_tool == Tool.Track)
            _editTrack.Update(0f);
        else
            _penTrack.Update(0f);
    }

    /// Unjoins track at the one point selected (EditTrackTool.Split).
    private void SplitTrack(TrackEditor tracks) => _editTrack.Split();

    private static (string Name, string Key, Func<TrackEditor, IReadOnlySet<long>, NetworkEdit?> Act)[] SelectionTools => EditTrackTool.SelectionTools;

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
            if (_tool == Tool.Track)
                _editTrack.Draw(draw);
            else
                _penTrack.Draw(draw);
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
            _layYard.Draw(draw);
        if (_tool == Tool.Parallel)
            _parallelTool.Draw(draw);
        if (_tool == Tool.Place)
            _objects.Draw(draw);
        if (_tool == Tool.Road)
        {
            _roadTool.Draw(draw);
        }


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
    YardGuides? IToolHost.Guides => _guides;
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
            _parallelTool.Panel(imperial);
        if (_tool == Tool.Pen)
            _penTrack.Panel(imperial);
        if (_tool == Tool.Circuits)
            _circuits?.Panel(imperial);
        if (_tool == Tool.Switches)
            _switchTool.Panel(imperial);
        if (_tool == Tool.Shape)
            _shapeTool.Panel(imperial);
        if (_tool == Tool.Follow)
            _layYard.Panel(imperial);
        if (_tool == Tool.Track)
            _editTrack.Panel(imperial);
        if (_tool is Tool.Track or Tool.Pen or Tool.Follow or Tool.Parallel || (_tool == Tool.Place && _place.Mode == PlaceMode.Track))
        {
            ClassPanel();
            BedPanel();
        }
        if (_tool == Tool.Place)
            _objects.Panel(imperial);
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
                Tool.Parallel => _parallelTool.Hints,
                Tool.Switches => _switchTool.Hints,
                Tool.Place => _objects.Hints,
                Tool.Follow => _layYard.Hints,
                Tool.Shape => _shapeTool.Hints,
                _ when IsBrush(_tool) => SyncBrush().Hints,
                Tool.Track => _editTrack.Hints,
                Tool.Circuits => _circuits?.Hints ?? [],
                Tool.Pen => _penTrack.Hints,
                _ => [("LMB", _tool.ToString()), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
            };
            return tool;
        }
    }
}

/// A grade crossing placed with the roads along it cut back to its ends: both undone together.
public sealed record CrossingEdit(NetworkEdit? Roads, PlacedEdit Placed);
