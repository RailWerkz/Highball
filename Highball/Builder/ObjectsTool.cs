using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Place tool's buttons on the strip, each with its own part of the palette: premade and fitted pieces, structures,
/// signals, and what stands beside the line.
public enum PlaceGroup { Assemblies, Structures, Signals, Lineside }

/// The Objects tool, on its four strip buttons: track pieces, grade crossings and diamonds (Assembly); bridges placed, edited
/// and removed (Structures); signals and hand throws (Signals); poles, lights and loose ties, one at a time, in fenced runs
/// or along a pen's path (Lineside). A placed object is picked, dragged, turned (R, or R with the wheel), flipped (V) or
/// deleted; every change is saved with the route and undone as one.
internal sealed partial class ObjectsTool(ToolContext context, RouteInfo route) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    private readonly RouteInfo _route = route;
    private readonly PlaceTool _place = new();
    private (double X, double Z)? _placeGrab;

    /// Turning with the wheel: what it was before, for one undo when R is let go, and whether the wheel moved this hold of R.
    private PlacedObject? _wheelFrom;
    private bool _wheeled;

    /// A bridge being placed: where it was begun, what it can end on, and where on the screen it was begun, to tell a drag
    /// from a click.
    private RoadEditor.Hit? _bridgeFrom;
    private HashSet<long> _bridgeLine = [];
    private float2 _bridgePressed;

    private Terrain? _terrain => context.Terrain;
    private NetworkMeshes? _network => context.Network;
    private TrackEditor? _tracks => context.Tracks;
    private string? _switchRefused { get => context.Refused; set => context.Refused = value; }

    /// What's in hand and how it goes down.
    public PlaceTool Place => _place;

    public RoadEditor.Hit? BridgeFrom { get => _bridgeFrom; set => _bridgeFrom = value; }
    public long? BridgeKey { get => _bridgeKey; set => _bridgeKey = value; }
    public string? BridgeProblem => _bridgeProblem;

    /// R held with something to turn: the wheel turns it instead of moving the camera.
    public bool WheelTurns => !Keyboard.KeysBlocked && Input.Key(GlfwKey.R) &&
                              (context.Source.Objects.FirstOrDefault(o => o.Id == _place.Selected) is { Kind: not PlacedKind.HandThrow } || (_place.Selected is null && _place.Mode == PlaceMode.Track && _place.Stamp is not null));

    public void Update(float dt)
    {
        var source = context.Source;
        if (source.Lines is { } lines)
            PlaceUpdate(source, lines);
        BridgeUpdate();
    }

    public void Draw(ImDrawListPtr draw)
    {
        if (_place.Mode == PlaceMode.EditBridge)
            BridgeDraw(draw);
        else
            _place.Draw(draw, context.ProjectGround, context.Source.Objects, PlacedAtWorld, S(1f));
        if (_place.Mode == PlaceMode.RemoveBridge && !context.Blocked && context.Hit is { } at2
            && context.Roads.Nearest(at2.X, at2.Z, context.PickReach(at2.X, at2.Z), RoadEditor.IsBridge) is { } gone && context.Roads.Shape(gone.Way) is { } goneLine)
            context.DrawPicked(draw, goneLine.X, goneLine.Z, Stop);
        if (_place.Mode == PlaceMode.Bridge && !context.Blocked && context.Hit is { } at3)
            DrawBridgePreview(draw, at3.X, at3.Z);
    }

    public void Panel(bool imperial) => PlacePanel(context.Source);

    public bool Escape()
    {
        if (_bridgeFrom is not null)
            _bridgeFrom = null;
        else if (_place.RunFrom is not null)
            (_place.RunFrom, _place.RunFirst) = (null, null);
        else if (_place.Pen.Count > 0)
            _place.Pen.Clear();
        else if (_place.Selected is not null)
            _place.Selected = null;
        else if (_place.Stamp is not null)
            _place.Stamp = null;
        else
            return false;
        return true;
    }

    public (string Key, string Action)[] Hints => _place switch
    {
        { Mode: PlaceMode.EditBridge } => [("LMB", "Pick bridge, drag pier"), ("DEL", "Remove pier"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
        { RunFrom: not null } => [("LMB", "Lay to here"), ("ESC", "End run"), ("CTRL+Z", "Undo")],
        { Mode: PlaceMode.Pole, Drawn: PoleDraw.Fence, Selected: null } => [("LMB", "Start run"), ("CTRL+Z", "Undo")],
        { Mode: PlaceMode.Pole, Drawn: PoleDraw.Pen } => [("LMB", "Point"), ("DRAG", "Curve"), ("ENTER", "Lay"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel"), ("CTRL+Z", "Undo")],
        { Mode: PlaceMode.Track, Selected: null } => [("LMB", "Lay piece"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Mirror"), ("ESC", "Let go"), ("CTRL+Z", "Undo")],
        _ => [("LMB", "Place, pick, drag"), ("R", "Turn 15"), ("R+WHEEL", "Turn slowly"), ("V", "Flip"), ("DEL", "Delete"), ("CTRL+Z", "Undo")],
    };

    public (double X, double Z) PlacedAtWorld(PlacedObject o)
    {
        var p = _route.Projection!.Forward(new GeoPoint(o.Lat, o.Lon));
        return (p.East, -p.North);
    }

    public PlacedObject Placed(PlacedKind kind, double x, double z, double heading, long? node = null, float width = 0f, bool opposite = false, long? id = null)
    {
        var geo = _route.Projection!.Inverse(new RoutePoint(x, -z));
        return new PlacedObject(id ?? DateTime.UtcNow.Ticks, kind, geo.Lat, geo.Lon, heading, node, width, opposite);
    }

    /// One object swapped for another in the route's list (null: none), saved, and shown.
    public void ApplyPlaced(PlacedObject? gone, PlacedObject? added) => ApplyPlaced([new PlacedEdit(gone, added)]);

    /// Many such swaps, saved and shown once. (Each one's "before" goes and its "after" comes.)
    public void ApplyPlaced(IReadOnlyList<PlacedEdit> edits)
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
            context.RebuildNetwork();
    }

    /// A grade crossing placed (or put back) with the roads along it cut back to its pad's ends, as one undo.
    public void CrossingPlace(NetworkLines lines, PlacedObject after, PlacedObject? before)
    {
        var x = PlacedAtWorld(after);
        NetworkEdit? roads = null;
        if (lines.PadFor(after.Id, x.X, x.Z, after.Heading, after.Width) is { } pad)
            roads = context.Roads.CutAt(pad);
        if (before is not null && roads is null && before == after)
            return;
        ApplyPlaced(before, after);
        context.Record(new CrossingEdit(roads, new PlacedEdit(before, after)));
        _place.Selected = after.Id;
        if (roads is not null)
            context.RebuildNetwork();
    }

    public void PlacedChange(PlacedObject? before, PlacedObject? after)
    {
        ApplyPlaced(before, after);
        context.Record(new PlacedEdit(before, after));
        _place.Selected = after?.Id;
    }

    /// Lays what the pen's drawn, as one undo.
    public void PenLay()
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
        context.Record(new PlacedRunEdit(edits));
        _place.Pen.Clear();
        _place.Selected = null;
    }

    /// A click with a pole in hand: one pole; or, with Fence on, a run's first pole and then each stretch of it, one undo a stretch.
    public void PolePress(NetworkLines lines, double x, double z)
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
        context.Record(new PlacedRunEdit(edits));
        var last = run.Poles[^1];
        (_place.RunFrom, _place.RunFirst, _place.Selected) = ((last.X, last.Z), null, null);
    }

    /// A click: on an object, picks it; elsewhere, places one if there's somewhere to.
    public void PlacePress(TerrainSource source, NetworkLines lines, double x, double z)
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
                if (context.Roads.Nearest(x, z, context.PickReach(x, z), RoadEditor.IsBridge) is { } bridge)
                    context.Commit(context.Roads.RemoveBridge(bridge.Way));
                else
                    _switchRefused = "No bridge here";
                break;
            case PlaceMode.Diamond when _tracks!.CrossingNear(x, z, PlaceTool.SnapReach) is { } diamond:
                if (diamond.Degrees < 8.0)
                    _switchRefused = "Under 8 degrees: too shallow";
                else
                    context.Commit(_tracks.JoinCrossing(diamond));
                break;
            default:
                _switchRefused = _place.Mode == PlaceMode.Diamond ? "No tracks cross here" : _place.Mode == PlaceMode.HandThrow ? "No switch here" : "No track here";
                break;
        }
    }

    /// The shortest bridge worth building and the longest it can go, in metres. How far past a line's end the cursor has to be to
    /// carry the bridge on past it, and how near an end a bridge starting there begins at the end itself.
    private const double ShortestBridge = 4.0;
    private const double LongestBridge = 800.0;
    private const double BridgeOutFrom = 2.0;
    private const double BridgeStartEnd = 8.0;

    /// Where a bridge would start under the cursor, on the line nearest, road or track. Generous, since it's hard to miss on purpose.
    private RoadEditor.Hit? BridgeStartAt(double x, double z) =>
        context.Roads.Nearest(x, z, Math.Max(context.PickReach(x, z) * 2.0, 8.0), w => !w.Tags.ContainsKey(RouteNetwork.RemovedTag) && !RouteNetwork.IsGuide(w)) is { } hit
            ? context.Roads.ToLooseEnd(hit, BridgeStartEnd)
            : null;

    /// Where a bridge begun already would end: the place on its own line nearest the cursor, however far off the cursor is.
    private RoadEditor.Hit? BridgeEndAt(double x, double z) => context.Roads.Nearest(x, z, 1e7, w => _bridgeLine.Contains(w.Id));

    /// Where a bridge begun already would land if the cursor's out past a loose end of its line, over whatever gap's there.
    private LayPoint? BridgeLandingAt(RoadEditor.Hit end, double x, double z)
    {
        if (context.Roads.LooseEndAt(end) is null || Math.Sqrt((x - end.X) * (x - end.X) + (z - end.Z) * (z - end.Z)) < BridgeOutFrom)
            return null;
        return context.Roads.LandingAt(end, x, z, Math.Max(context.PickReach(x, z), 6.0));
    }

    /// How long a bridge would be, along its line and on past the end if it's going there.
    private double BridgeLength(RoadEditor.Hit from, RoadEditor.Hit end, LayPoint? landing)
    {
        var along = context.Roads.Span(from, end)?.Length ?? 0.0;
        return landing is { } l ? along + Math.Sqrt((l.X - end.X) * (l.X - end.X) + (l.Z - end.Z) * (l.Z - end.Z)) : along;
    }

    /// A bridge's first click holds its place, and the second, or letting go of a drag, builds it. Once begun it stays on its line:
    /// the cursor can't pick the one beside it.
    public void BridgeClick(double x, double z)
    {
        _switchRefused = null;
        if (_bridgeFrom is null)
        {
            if (BridgeStartAt(x, z) is not { } hit)
            {
                _switchRefused = context.Roads.Nearest(x, z, Math.Max(context.PickReach(x, z), 8.0), w => !w.Tags.ContainsKey(RouteNetwork.RemovedTag)) is null
                    ? "Click on a road or a track"
                    : "Guide only: lay track first";
                return;
            }

            _bridgeFrom = hit;
            _bridgeLine = context.Roads.LineOf(hit.Way);
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
        var made = landing is { } out2 ? context.Roads.AddBridgeOut(from, end, out2, out var refused) : context.Roads.AddBridge(from, end, out refused);
        if (made is { } edit)
            context.Commit(edit);
        else
            _switchRefused = refused;
    }

    /// Letting go of a drag ends the bridge there; letting go where it was pressed leaves it waiting for the second click.
    private void BridgeUpdate()
    {
        if (_place.Mode != PlaceMode.Bridge || _bridgeFrom is null || context.Blocked || context.Hit is not { } hit || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;
        var moved = Input.MousePosition - _bridgePressed;
        if (moved.X * moved.X + moved.Y * moved.Y > S(16f) * S(16f))
            BridgeFinish(hit.X, hit.Z);
    }

    /// Lays the track piece in hand, as it's shown at this place, and sets its switches to its size.
    public void LayStamp(NetworkLines lines, double x, double z)
    {
        _place.Update(lines, _tracks!, (x, z), YardLaying.Length);
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
        context.Commit(edit);
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
    public void PlaceMove(NetworkLines lines, PlacedObject o, double x, double z)
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
                PlacedChange(o, Placed(PlacedKind.GradeCrossing, crossing.X, crossing.Z, crossing.Heading, null, o.Width, o.Opposite, id: o.Id) with { Turn = o.Turn, Style = o.Style, Approach = o.Approach });
                break;
            case PlacedKind.HandThrow when PlaceTool.SwitchAt(lines, x, z) is { } turnout && _terrain!.Source.Objects.All(p => p.Node != turnout.Node):
                PlacedChange(o, Placed(PlacedKind.HandThrow, turnout.X, turnout.Z, 0.0, turnout.Node, opposite: o.Opposite, id: o.Id));
                break;
        }
    }

    public void PlaceTurn(PlacedObject o, double degrees) => PlacedChange(o, o with { Heading = o.Heading + degrees });

    private void PlaceUpdate(TerrainSource source, NetworkLines lines)
    {
        var blocked = context.Blocked;
        var mouse = Input.MousePosition;
        var ground = !blocked && context.Hit is { } hit ? (hit.X, hit.Z) : ((double, double)?)null;
        if (_place.Mode == PlaceMode.EditBridge)
        {
            if (!blocked && !context.Rebuilding)
                BridgeEditUpdate(ground, mouse);
            return;
        }

        _place.Update(lines, _tracks!, ground, YardLaying.Length);
        // What's under the pointer, ringed as what a click would pick. (A run being laid isn't stopped by a pole in its way.)
        var under = blocked || _place.RunFrom is not null || (_place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Pen)
            ? null
            : PlaceTool.At(source.Objects, o => context.ProjectGround(PlacedAtWorld(o).X, PlacedAtWorld(o).Z), mouse, S(1f));
        _place.Hovered = under;
        if (blocked || context.Rebuilding)
            return;

        // The pen has the pointer and keys to itself, as in the Pen tool: to pick something, go back to One or Fence.
        if (_place.Mode == PlaceMode.Pole && _place.Drawn == PoleDraw.Pen)
        {
            _place.Pen.Update(mouse, _place.PenSnap(lines, ground), ground, context.ProjectGround, Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
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
                context.Record(new PlacedEdit(before, picked));
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
    public void PlaceFlip(PlacedObject o) => PlacedChange(o, o.Kind is PlacedKind.HandThrow or PlacedKind.GradeCrossing ? o with { Opposite = !o.Opposite } : o with { Heading = o.Heading + 180.0 });

    /// The gate turn being dragged on the slider, until it's let go (then it's one edit, for undo).
    private float? _crossingTurn;
    private float? _crossingApproach;
    private const float MetresPerMile = 1609.344f;

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

        // How far out along each track the approach circuits reach: a train coming in starts the warning about WarningTime
        // seconds before it reaches the road, if it's within this far.
        Kit.Caption("Approach circuits");
        var miles = _crossingApproach ?? picked.ApproachLength / MetresPerMile;
        ToolPanel.Value("approach", "Approach (miles)", ref miles, PlacedObject.MinApproach / MetresPerMile, PlacedObject.MaxApproach / MetresPerMile, "0.00", out var released);
        _crossingApproach = ImGui.IsItemActive() ? miles : null;
        if (released)
            PlacedChange(picked, picked with { Approach = miles * MetresPerMile });

        if (_terrain?.Source.Lines is { } lines && Kit.Button("cutroads", "Cut context.Roads"))
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
                context.Record(new PlacedRunEdit(edits));
                _place.Selected = null;
            }
        }

        Kit.EndCard();
    }

    /// The crossing road width being dragged on the slider, until it's let go.
    private float? _crossingWidth;

    /// Before the first click, the line a bridge would go on and the place on it. After, the stretch it would cover, along the line,
    /// and how long that is.
    private void DrawBridgePreview(ImDrawListPtr draw, double x, double z)
    {
        if (_bridgeFrom is not { } from)
        {
            if (BridgeStartAt(x, z) is not { } start)
                return;
            if (context.Roads.Shape(start.Way) is { } line)
                context.DrawPicked(draw, line.X, line.Z, Style.TextDim);
            if (context.ProjectGround(start.X, start.Z) is { } place)
                draw.AddCircleFilled(place, S(6f), Style.Accent);
            return;
        }

        if (BridgeEndAt(x, z) is not { } end || context.Roads.Span(from, end) is not { } span)
            return;

        var landing = BridgeLandingAt(end, x, z);
        var total = BridgeLength(from, end, landing);
        var color = total < ShortestBridge || total > LongestBridge ? Style.Stop : Style.Accent;
        for (var i = 0; i + 1 < span.Points.Count; i++)
            if (context.ProjectGround(span.Points[i].X, span.Points[i].Z) is { } a && context.ProjectGround(span.Points[i + 1].X, span.Points[i + 1].Z) is { } b)
                draw.AddLine(a, b, color, S(5f));
        if (context.ProjectGround(from.X, from.Z) is { } first)
            draw.AddCircleFilled(first, S(6f), color);
        var (lastX, lastZ) = landing is { } l ? (l.X, l.Z) : (end.X, end.Z);
        if (landing is not null && context.ProjectGround(end.X, end.Z) is { } edge && context.ProjectGround(lastX, lastZ) is { } over)
            draw.AddLine(edge, over, color, S(5f));
        if (context.ProjectGround(lastX, lastZ) is { } last)
        {
            draw.AddCircleFilled(last, S(6f), color);
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var length = imperial ? $"{total * FeetPerMeter:#,0} ft" : $"{total:#,0} m";
            PenTool.Label(draw, last, $"Bridge {length}", color);
        }
    }
}
