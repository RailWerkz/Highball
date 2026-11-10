using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.Signalling;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// One change to the signalling, kept so it can be undone and redone: its saved state before and after.
public sealed record SignallingEdit(SignallingState Before, SignallingState After);

public sealed record SignallingState(List<RouteSignalling.GeoJoint> Joints, List<RouteSignalling.GeoAnchor> Circuits, int Next, bool Laid);

/// The track circuits in the editors (tool 9): shown along the track, each circuit its own colour with its id, its
/// joints as ticks across the track. Click a circuit to select it (and rename it in the side panel), Shift+click on
/// the track to put a joint there, drag a joint along the track to move it, Delete takes the hovered joint out. The
/// side panel lays out the automatic joints again.
///
/// The circuits are built on a job (Brutal.Concurrency) from a snapshot of the ways, the joints and the ids, whenever
/// any of them changes; what's shown is the last build until the next one is in.
public sealed class CircuitEditor : IDisposable
{
    private const float PickPixels = 10f;

    private readonly RouteInfo _route;
    private readonly RouteProjection _projection;
    private readonly JobStream _jobs = new("Signalling", 0.1);
    private RouteSignalling _data;
    private TrackCircuits? _circuits;
    private volatile Result? _result;
    private int _builtVersion = -1;
    private bool _dirty = true;
    private bool _regenerate;

    private int? _hoverJoint;
    private TrackCircuits.Circuit? _hover;
    private string? _selected;
    private int? _dragging;
    private (double X, double Z)? _dragTo;
    private ImInputString _rename = new(32, ""u8);
    private string? _renameFor;
    private float _blockLength;

    private sealed record Result(TrackCircuits Circuits, List<RouteSignalling.GeoJoint> Joints, List<RouteSignalling.GeoAnchor> Anchors, int Next, bool Generated);

    public bool Unsaved { get; private set; }
    public bool Building => _jobs.Busy;
    public TrackCircuits? Circuits => _circuits;

    public CircuitEditor(RouteInfo route)
    {
        _route = route;
        _projection = route.Projection!;
        _data = RouteSignalling.Load(route);
        _blockLength = (float)_data.BlockLength;
    }

    public void Dispose() => _jobs.Dispose();

    public void Save()
    {
        if (!Unsaved)
            return;
        _data.Save(_route);
        Unsaved = false;
    }

    // --- Building. ---

    /// Once a frame: takes in a finished build, and starts another if the track, the joints or the ids changed.
    public void Update(RouteNetwork network)
    {
        if (_result is { } result)
        {
            _result = null;
            _circuits = result.Circuits;
            // Only what changes the circuits counts as unsaved, not joints and anchors settling a little onto the track.
            var changed = result.Generated || result.Next != _data.Next || result.Joints.Count != _data.Joints.Count ||
                          !result.Anchors.Select(a => a.Id).SequenceEqual(_data.Circuits.Select(a => a.Id));
            _data.Joints = result.Joints;
            _data.Circuits = result.Anchors;
            _data.Next = result.Next;
            _data.Laid = true;
            Unsaved |= changed;
            if (_selected is not null && _circuits.Circuits.All(c => c.Id != _selected))
                _selected = null;
        }

        if ((network.Version == _builtVersion && !_dirty) || _jobs.Busy)
            return;

        _builtVersion = network.Version;
        _dirty = false;
        var ways = network.Ways.Values.Where(w => !RouteNetwork.IsGuide(w)).ToList();
        var joints = _data.WorldJoints(_projection);
        var anchors = _data.WorldAnchors(_projection);
        var next = _data.Next;
        var generate = !_data.Laid || _regenerate;
        var blockLength = _data.BlockLength;
        _regenerate = false;
        _jobs.TryRun([(new Job("track circuits", () => Build(ways, joints, anchors, next, generate, blockLength)), 1)]);
    }

    private void Build(List<NetworkWay> ways, List<Joint> joints, List<CircuitAnchor> anchors, int next, bool generate, double blockLength)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var graph = TrackGraph.Build(ways, _projection);
        if (generate)
            joints = TrackCircuits.Generate(graph, joints, blockLength);
        var circuits = TrackCircuits.Build(graph, joints, anchors, ref next);

        // Joints that found their track are kept where it put them; those that didn't are kept as they were, in case
        // their track comes back.
        var placed = circuits.Joints.Select(p => RouteSignalling.ToGeo(p.Joint, _projection)).ToList();
        var lost = joints.Where(j => circuits.Joints.All(p => (p.Joint.X - j.X) * (p.Joint.X - j.X) + (p.Joint.Z - j.Z) * (p.Joint.Z - j.Z) > TrackCircuits.SnapDistance * TrackCircuits.SnapDistance))
            .Select(j => RouteSignalling.ToGeo(j, _projection));
        var anchorsOut = circuits.Circuits.Select(c => RouteSignalling.ToGeo(new CircuitAnchor(c.Id, c.AnchorX, c.AnchorZ), _projection)).ToList();
        _result = new Result(circuits, [.. placed, .. lost], anchorsOut, next, generate);
        Log.Info($"Track circuits: {circuits.Circuits.Count} circuits, {circuits.Joints.Count} joints, {graph.Edges.Count} edges, " +
                 $"{graph.Vertices.Count(v => v.Junction)} junctions in {watch.ElapsedMilliseconds} ms");
    }

    // --- Edits. ---

    private SignallingState State => new([.. _data.Joints], [.. _data.Circuits], _data.Next, _data.Laid);

    private SignallingEdit Change(Action change)
    {
        var before = State;
        change();
        _dirty = true;
        Unsaved = true;
        return new SignallingEdit(before, State);
    }

    public void Apply(SignallingEdit edit, bool undo)
    {
        var state = undo ? edit.Before : edit.After;
        _data.Joints = [.. state.Joints];
        _data.Circuits = [.. state.Circuits];
        _data.Next = state.Next;
        _data.Laid = state.Laid;
        _dirty = true;
        Unsaved = true;
    }

    /// Lays out the automatic joints again at the panel's block length, keeping the manual ones.
    public SignallingEdit Regenerate() => Change(() =>
    {
        _data.BlockLength = Math.Clamp(_blockLength, 200f, 10000f);
        _data.Laid = false;
        _regenerate = true;
    });

    private SignallingEdit? AddJoint(double x, double z) =>
        _circuits?.Graph.Nearest(x, z, TrackCircuits.SnapDistance) is { } at
            ? Change(() =>
            {
                var (px, pz, _, _) = _circuits.Graph.PointAt(at.Edge, at.S);
                _data.Joints.Add(RouteSignalling.ToGeo(new Joint(px, pz, true), _projection));
            })
            : null;

    private SignallingEdit? RemoveJoint(int placed) => Change(() => _data.Joints.Remove(NearestStored(placed)));

    private SignallingEdit? MoveJoint(int placed, double x, double z) =>
        _circuits?.Graph.Nearest(x, z, TrackCircuits.SnapDistance) is { } at
            ? Change(() =>
            {
                var (px, pz, _, _) = _circuits.Graph.PointAt(at.Edge, at.S);
                var stored = NearestStored(placed);
                _data.Joints[_data.Joints.IndexOf(stored)] = RouteSignalling.ToGeo(new Joint(px, pz, true), _projection);
            })
            : null;

    /// The stored joint a placed one came from: the nearest to it.
    private RouteSignalling.GeoJoint NearestStored(int placed)
    {
        var joint = _circuits!.Joints[placed].Joint;
        var geo = RouteSignalling.ToGeo(joint, _projection);
        return _data.Joints.MinBy(j => (j.Lat - geo.Lat) * (j.Lat - geo.Lat) + (j.Lon - geo.Lon) * (j.Lon - geo.Lon))!;
    }

    private SignallingEdit? Rename(TrackCircuits.Circuit circuit, string id)
    {
        id = id.Trim().ToUpperInvariant();
        if (id.Length == 0 || id == circuit.Id || _circuits!.Circuits.Any(c => c.Id == id))
            return null;
        var old = circuit.Id;
        return Change(() =>
        {
            var anchor = RouteSignalling.ToGeo(new CircuitAnchor(id, circuit.AnchorX, circuit.AnchorZ), _projection);
            _data.Circuits.RemoveAll(a => a.Id == old || a.Id == id);
            _data.Circuits.Insert(0, anchor);
            circuit.Id = id;
            _selected = id;
        });
    }

    // --- The tool. ---

    /// One frame of the pointer over the world: ground is the point under it, metresPerPixel roughly how big a pixel
    /// is there. Returns the edit it made, if any.
    public SignallingEdit? Update(float2 mouse, (double X, double Z)? ground, double metresPerPixel, Func<double, double, float2?> project,
        bool pressed, bool held, bool shift, bool delete)
    {
        _hoverJoint = null;
        _hover = null;
        if (_circuits is not { } circuits)
            return null;

        var best = PickPixels;
        for (var i = 0; i < circuits.Joints.Count; i++)
        {
            var j = circuits.Joints[i].Joint;
            if (project(j.X, j.Z) is not { } s)
                continue;
            var d = MathF.Abs(s.X - mouse.X) + MathF.Abs(s.Y - mouse.Y);
            if (d < best)
                (best, _hoverJoint) = (d, i);
        }

        if (ground is { } g)
            _hover = circuits.Locate(g.X, g.Z, Math.Max(3.0, metresPerPixel * PickPixels));

        SignallingEdit? edit = null;
        if (pressed)
        {
            if (_hoverJoint is { } grabbed && !shift)
                _dragging = grabbed;
            else if (shift && ground is { } at)
                edit = AddJoint(at.X, at.Z);
            else
                _selected = _hover?.Id;
        }

        if (_dragging is not null && ground is { } to)
            _dragTo = to;
        if (_dragging is { } dragged && !held)
        {
            if (_dragTo is { } target && project(target.X, target.Z) is { } screen && project(circuits.Joints[dragged].Joint.X, circuits.Joints[dragged].Joint.Z) is { } from &&
                MathF.Abs(screen.X - from.X) + MathF.Abs(screen.Y - from.Y) > 3f)
                edit = MoveJoint(dragged, target.X, target.Z);
            _dragging = null;
            _dragTo = null;
        }

        if (delete && _hoverJoint is { } doomed)
            edit = RemoveJoint(doomed);
        return edit;
    }

    /// A colour of its own for each circuit, from its id.
    private static Rgba ColorOf(string id)
    {
        var hash = (uint)id.GetHashCode(StringComparison.Ordinal);
        var hue = (hash % 360u) / 360f;
        float Channel(float n)
        {
            var k = (n + hue * 6f) % 6f;
            return 0.85f - 0.55f * MathF.Max(0f, MathF.Min(MathF.Min(k, 4f - k), 1f));
        }

        return new Rgba(Channel(5f), Channel(3f), Channel(1f), 1f);
    }

    /// The circuits near (cx, cz) along the track, the selected and hovered ones wider, the joints as ticks across it,
    /// and the ids when there's room to read them.
    public void Draw(ImDrawListPtr draw, double cx, double cz, double reach, Func<double, double, float2?> project, bool labels)
    {
        if (_circuits is not { } circuits)
            return;

        var graph = circuits.Graph;
        foreach (var circuit in circuits.Circuits)
        {
            var color = ColorOf(circuit.Id);
            var width = S(circuit.Id == _selected ? 6f : circuit == _hover ? 5f : 3f);
            foreach (var (index, from, to) in circuit.Spans)
            {
                var edge = graph.Edges[index];
                if (edge.MaxX < cx - reach || edge.MinX > cx + reach || edge.MaxZ < cz - reach || edge.MinZ > cz + reach)
                    continue;

                float2? previous = null;
                var (px, pz, first) = (0.0, 0.0, true);
                void Point(double x, double z)
                {
                    var screen = project(x, z);
                    if (previous is { } a && screen is { } b)
                        draw.AddLine(a, b, color, width);
                    else if (!first && (previous is null) != (screen is null))
                    {
                        // One end's behind the camera, which happens on a long straight: draw the part in front.
                        var (seen, (fromX, fromZ), (toX, toZ)) = previous is { } p ? (p, (px, pz), (x, z)) : (screen!.Value, (x, z), (px, pz));
                        if (InFront(project, fromX, fromZ, toX, toZ) is { } edge)
                            draw.AddLine(seen, edge, color, width);
                    }

                    (previous, px, pz, first) = (screen, x, z, false);
                }

                var (sx, sz, _, _) = graph.PointAt(index, from);
                Point(sx, sz);
                for (var i = 0; i < edge.S.Length; i++)
                    if (edge.S[i] > from && edge.S[i] < to)
                        Point(edge.X[i], edge.Z[i]);
                var (ex, ez, _, _) = graph.PointAt(index, to);
                Point(ex, ez);
            }
        }

        for (var i = 0; i < circuits.Joints.Count; i++)
        {
            var placed = circuits.Joints[i];
            var (x, z, dx, dz) = graph.PointAt(placed.Edge, placed.S);
            if (Math.Abs(x - cx) > reach || Math.Abs(z - cz) > reach)
                continue;
            if (_dragging == i && _dragTo is { } to && graph.Nearest(to.X, to.Z, TrackCircuits.SnapDistance) is { } moved)
                (x, z, dx, dz) = graph.PointAt(moved.Edge, moved.S);
            if (project(x, z) is not { } p || project(x + dx, z + dz) is not { } q)
                continue;
            var along = q - p;
            var length = MathF.Max(1e-3f, MathF.Sqrt(along.X * along.X + along.Y * along.Y));
            var across = new float2(-along.Y, along.X) / length * S(_hoverJoint == i ? 11f : 8f);
            draw.AddLine(p - across, p + across, placed.Joint.Manual ? Caution : Rgba.White, S(_hoverJoint == i ? 3.5f : 2.5f));
        }

        if (!labels)
            return;
        foreach (var circuit in circuits.Circuits)
        {
            if (Math.Abs(circuit.AnchorX - cx) > reach || Math.Abs(circuit.AnchorZ - cz) > reach || project(circuit.AnchorX, circuit.AnchorZ) is not { } at)
                continue;
            var size = S(12f);
            var width = Canvas.SpacedWidth(Medium, size, circuit.Id, 0f);
            var min = at - new float2(width * 0.5f, size * 0.5f);
            draw.AddRectFilled(min - new float2(S(4f), S(2f)), min + new float2(width + S(4f), size + S(3f)), Surface, S(3f));
            Canvas.Text(draw, Medium, size, min, ColorOf(circuit.Id), circuit.Id);
        }
    }

    /// The farthest point from (fromX, fromZ), which can be seen, towards (toX, toZ), which can't, that still can: found
    /// by halving, since the ground's height under the line makes it no straight line in the air.
    private static float2? InFront(Func<double, double, float2?> project, double fromX, double fromZ, double toX, double toZ)
    {
        float2? last = null;
        var (lo, hi) = (0.0, 1.0);
        for (var i = 0; i < 20; i++)
        {
            var t = (lo + hi) * 0.5;
            if (project(fromX + (toX - fromX) * t, fromZ + (toZ - fromZ) * t) is { } at)
                (lo, last) = (t, at);
            else
                hi = t;
        }

        return last;
    }

    /// The side panel: totals, the selected circuit (renamed here), the circuit under the pointer, and laying out
    /// the automatic joints again. Returns the edit it made, if any.
    public SignallingEdit? Panel(bool imperial)
    {
        SignallingEdit? edit = null;
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * 3.28084:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

        if (_circuits is not { } circuits)
        {
            ToolPanel.Hint("Building...");
            return null;
        }

        Kit.BeginCard("Circuits", _jobs.Busy ? "Updating" : null);
        Kit.Stats([new Stat("Circuits", circuits.Circuits.Count.ToString()), new Stat("Joints", circuits.Joints.Count.ToString()), new Stat("Hovered", _hover?.Id ?? "None")], 2);
        Kit.EndCard();

        if (circuits.Circuits.FirstOrDefault(c => c.Id == _selected) is { } selected)
        {
            Kit.BeginCard("Picked circuit", null, Style.Accent);
            if (_renameFor != selected.Id)
            {
                _rename = new ImInputString(32, selected.Id);
                _renameFor = selected.Id;
            }

            Kit.Caption("Id");
            ImGui.SetNextItemWidth(Kit.Width);
            if (ImGui.InputText("##circuitid"u8, _rename, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsUppercase))
                edit = Rename(selected, _rename.ToString());
            Kit.Stats(
            [
                new Stat("Length", Length(selected.Length)),
                new Stat("Stretches", selected.Spans.Count.ToString()),
                new Stat("Switches", selected.Junctions.ToString()),
            ], 2);
            Kit.EndCard();
        }

        Kit.BeginCard("Automatic joints");
        var factor = imperial ? 3.28084f : 1f;
        var block = _blockLength * factor;
        if (ToolPanel.Value("blocklength", imperial ? "Block length (ft)" : "Block length (m)", ref block, 200f * factor, 10000f * factor, "0", log: true))
            _blockLength = MathF.Round(block / 50f) * 50f / factor;
        if (Kit.Button("layoutagain", "Lay Out Again"))
            edit = Regenerate();
        Kit.EndCard();
        return edit;
    }

    public TrackCircuits.Circuit? Hovered => _hover;
}
