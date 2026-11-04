using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

/// Shaping the track by hand: click a line to select it and it shows its handles. Drag a handle to move it (the track
/// between the handles either side is laid in again through it), click the line between handles to add one and drag it,
/// Delete takes the handle under the pointer out, Shift-click two handles and L makes the line exactly straight between
/// them. Junction handles stay put. A handle snaps to the straight between its neighbours and to the mapped line; Alt
/// turns that off.
internal sealed class ShapeTool
{
    /// How near the pointer has to be, in pixels (scaled), to a handle or a line.
    private const float Reach = 10f;
    /// Tighter than this and the preview warns (the fitting's own tightest curve).
    private const double WarnRadius = 70.0;

    private readonly HashSet<int> _extra = [];
    private readonly HashSet<int> _selected = [];
    private long? _key;
    private int? _hover;
    private int? _grabbed;

    /// What the handle being dragged would make of the line.
    public TrackShaping.Result? Preview { get; private set; }

    public NetworkLine? LineIn(NetworkLines lines) => _key is { } key ? lines.Lines.FirstOrDefault(l => l.Key == key && l.Alignment is not null) : null;

    public int Selected => _selected.Count;

    public List<int> HandlesOf(NetworkLines lines, NetworkLine line) => TrackShaping.Handles(line, lines.IsJunction, _extra);

    /// Selects a line, by its key.
    public void Show(long key)
    {
        Clear();
        _key = key;
    }

    public void Clear()
    {
        (_key, _hover, _grabbed, Preview) = (null, null, null, null);
        _extra.Clear();
        _selected.Clear();
    }

    /// One frame's input. Returns a new shape for the line, if there is one to commit.
    public (NetworkLine Line, Alignment Shape)? Update(NetworkLines lines, float2 mouse, (double X, double Z)? hit, Func<double, double, double, float2?> project, float scale,
        double cameraX, double cameraZ, double reach, bool pressed, bool held, bool shift, bool noSnap, bool remove, bool straighten, bool busy)
    {
        var line = LineIn(lines);
        if (_key is not null && line is null)
            Clear();

        float2? Screen(NetworkLine l, int p) => project(l.X[p], l.Y[p] + NetworkLine.BallastHeight + 0.2, l.Z[p]);

        _hover = null;
        var handles = line is null ? [] : HandlesOf(lines, line);
        if (line is not null && _grabbed is null)
        {
            var best = Reach * scale;
            foreach (var p in handles)
                if (Screen(line, p) is { } at)
                {
                    var d = Distance(at, mouse);
                    if (d < best)
                        (best, _hover) = (d, p);
                }
        }

        // The handle in hand follows the pointer, until it's let go.
        if (_grabbed is { } grabbed && line is not null)
        {
            if (hit is { } ground && handles.Contains(grabbed))
            {
                var (x, z) = noSnap ? ground : TrackShaping.Snapped(line, handles, grabbed, ground.X, ground.Z);
                if (TrackShaping.Move(line, handles, grabbed, x, z) is { } result)
                    Preview = result;
            }

            if (held)
                return null;
            _grabbed = null;
            var done = Preview;
            Preview = null;
            return done is null ? null : (line, done.Shape);
        }

        if (busy)
            return null;

        if (line is not null && remove && _hover is { } doomed && !IsFixed(lines, line, handles, doomed) &&
            TrackShaping.Remove(line, handles, doomed) is { } removed)
        {
            _extra.Remove(doomed);
            _selected.Remove(doomed);
            return (line, removed.Shape);
        }

        if (line is not null && straighten && _selected.Count == 2)
        {
            var (a, b) = (_selected.Min(), _selected.Max());
            if (TrackShaping.Straighten(line, handles, a, b, p => IsFixed(lines, line, handles, p)) is { } straight)
            {
                _selected.Clear();
                return (line, straight.Shape);
            }
        }

        if (!pressed)
            return null;

        if (_hover is { } on && line is not null)
        {
            if (shift)
            {
                if (!_selected.Remove(on))
                    _selected.Add(on);
            }
            else if (!IsFixed(lines, line, handles, on))
                _grabbed = on;
            return null;
        }

        // On the selected line, between its handles: a new handle, in hand. Otherwise, whichever line it's on is selected.
        if (line is not null && !shift && LineAt(lines, [line], mouse, Screen, cameraX, cameraZ, reach, Reach * scale) is { } there)
        {
            _extra.Add(there.Point);
            _grabbed = there.Point;
            return null;
        }

        if (shift)
            return null;
        Clear();
        if (LineAt(lines, null, mouse, Screen, cameraX, cameraZ, reach, Reach * scale) is { } picked)
            _key = picked.Line.Key;
        return null;
    }

    private static bool IsFixed(NetworkLines lines, NetworkLine line, List<int> handles, int point) =>
        point == 0 || point == line.Count - 1 || line.Nodes.Any(n => n.Point == point && lines.IsJunction(n.Node));

    private static float Distance(float2 a, float2 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// The rail line, and its nearest point, under the pointer.
    private static (NetworkLine Line, int Point)? LineAt(NetworkLines lines, NetworkLine[]? only, float2 mouse, Func<NetworkLine, int, float2?> screen,
        double cameraX, double cameraZ, double reach, float within)
    {
        (NetworkLine, int)? found = null;
        var best = within;
        foreach (var line in (IEnumerable<NetworkLine>?)only ?? lines.Lines)
        {
            if (!line.Rail || line.Disused || line.Alignment is null || line.MaxX < cameraX - reach || line.MinX > cameraX + reach || line.MaxZ < cameraZ - reach || line.MinZ > cameraZ + reach)
                continue;
            float2? previous = null;
            for (var p = 0; p < line.Count; p++)
            {
                if (Math.Abs(line.X[p] - cameraX) > reach || Math.Abs(line.Z[p] - cameraZ) > reach)
                {
                    previous = null;
                    continue;
                }

                var at = screen(line, p);
                if (at is { } b && previous is { } a)
                {
                    var (dx, dy) = (b.X - a.X, b.Y - a.Y);
                    var t = Math.Clamp(((mouse.X - a.X) * dx + (mouse.Y - a.Y) * dy) / MathF.Max(dx * dx + dy * dy, 1e-3f), 0f, 1f);
                    var d = Distance(new float2(a.X + dx * t, a.Y + dy * t), mouse);
                    if (d < best)
                        (best, found) = (d, (line, t < 0.5f ? p - 1 : p));
                }

                previous = at;
            }
        }

        return found;
    }

    public void Draw(ImDrawListPtr draw, NetworkLines lines, Func<double, double, double, float2?> project, float scale)
    {
        if (LineIn(lines) is not { } line)
            return;

        float2? Screen(double x, double y, double z) => project(x, y + NetworkLine.BallastHeight + 0.2, z);

        // The map's line, to follow; the line as it stands; and where the handle in hand would take it.
        Polyline(draw, line.MappedX, line.MappedZ, line, new Rgba(0.2f, 0.85f, 0.6f, 0.55f), scale * 1.5f, Screen);
        Polyline(draw, line.X, line.Z, line, Rgba.White, scale * 2f, Screen);
        if (Preview is { } preview)
        {
            var (x, z) = (new double[line.Count], new double[line.Count]);
            for (var p = 0; p < line.Count; p++)
                (x[p], z[p]) = Alignment.Position(preview.Shape.Pieces, preview.Shape.U[p]);
            Polyline(draw, x, z, line, preview.Radius < WarnRadius ? Style.Stop : Style.Caution, scale * 2.5f, Screen);
        }

        foreach (var p in HandlesOf(lines, line))
        {
            if (Screen(line.X[p], line.Y[p], line.Z[p]) is not { } at)
                continue;
            var fixedHere = IsFixed(lines, line, [], p);
            var r = scale * (p == _hover || p == _grabbed ? 7f : 5f);
            var (min, max) = (new float2(at.X - r, at.Y - r), new float2(at.X + r, at.Y + r));
            draw.AddRectFilled(min, max, fixedHere ? new Rgba(0.35f, 0.35f, 0.35f, 1f) : Style.Accent);
            draw.AddRect(min, max, _selected.Contains(p) ? Rgba.White : new Rgba(0f, 0f, 0f, 0.8f), 0f, ImDrawFlags.None, scale * (_selected.Contains(p) ? 2.5f : 1.5f));
        }
    }

    private static void Polyline(ImDrawListPtr draw, double[] xs, double[] zs, NetworkLine line, Rgba color, float width, Func<double, double, double, float2?> screen)
    {
        float2? previous = null;
        for (var p = 0; p < xs.Length; p++)
        {
            var at = screen(xs[p], line.Y[Math.Min(p, line.Count - 1)], zs[p]);
            if (previous is { } a && at is { } b)
                draw.AddLine(a, b, color, width);
            previous = at;
        }
    }
}
