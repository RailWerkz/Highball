using System.Globalization;
using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// Laying track with curves, as a pen tool draws paths: click for a corner (straight track either side), click and
/// drag for a smooth point whose handles pull the curve through it. Points and handles already placed can be dragged
/// again. The path is cubic Bézier curves between the points, laid as track sampled every few metres; a point placed
/// on a track's control point joins it there, one placed partway along a track branches off it.
public sealed class PenTool
{
    public sealed class Anchor
    {
        public LayPoint Point;
        /// The handle out towards the next point, relative to the point; the one back is its mirror.
        public double OutX, OutZ;
        public bool Smooth => OutX * OutX + OutZ * OutZ > 0.01;
    }

    public readonly record struct Measure(double Length, double MinRadius, double Grade, double SteepestGrade);

    private const float PickPixels = 10f;
    private const float DragPixels = 4f;
    private const double Step = 10.0;
    private const double GradeWindow = 100.0;

    private readonly List<Anchor> _anchors = [];
    private (Anchor Anchor, int Part)? _dragging; // part: 0 the point, 1 its handle out, -1 its handle back
    private float2 _pressedAt;
    private bool _placing;

    public int Count => _anchors.Count;
    public bool Busy => _dragging is not null;

    public void Clear()
    {
        _anchors.Clear();
        _dragging = null;
    }

    /// A point put down without the pointer: for scripted steps.
    public void Add(LayPoint point, double outX = 0.0, double outZ = 0.0) => _anchors.Add(new Anchor { Point = point, OutX = outX, OutZ = outZ });

    public void RemoveLast()
    {
        if (_anchors.Count > 0)
            _anchors.RemoveAt(_anchors.Count - 1);
        _dragging = null;
    }

    /// One frame of the pointer: snap is where a new point would go (on a track or the ground), null off the route.
    public void Update(float2 mouse, LayPoint? snap, (double X, double Z)? ground, Func<double, double, float2?> project, bool pressed, bool held)
    {
        if (pressed)
        {
            _pressedAt = mouse;
            if (Pick(mouse, project) is { } grabbed)
            {
                _dragging = grabbed;
                _placing = false;
            }
            else if (snap is { } point)
            {
                var anchor = new Anchor { Point = point };
                _anchors.Add(anchor);
                _dragging = (anchor, 1);
                _placing = true;
            }
        }

        if (_dragging is not { } drag)
            return;

        if (!held)
        {
            _dragging = null;
            return;
        }

        var (anchor2, part) = drag;
        if (part == 0)
        {
            if (snap is { } moved)
                anchor2.Point = moved;
            return;
        }

        // A new point only grows handles once the pointer has moved a little, so a click stays a corner.
        var moving = MathF.Abs(mouse.X - _pressedAt.X) + MathF.Abs(mouse.Y - _pressedAt.Y) > DragPixels;
        if (ground is not { } g || (_placing && !moving))
            return;
        var (dx, dz) = (g.X - anchor2.Point.X, g.Z - anchor2.Point.Z);
        (anchor2.OutX, anchor2.OutZ) = part == 1 ? (dx, dz) : (-dx, -dz);
    }

    /// The line Shift holds the next point to, while it's held: from the last point, on the way the path runs there.
    public ((double X, double Z) From, (double X, double Z) Way, bool Either)? Guide { get; private set; }

    /// Which way the path runs on from its last point: along the track when that's the only point and it's on one (out of
    /// a track's end; either way along it partway, Either), otherwise on along the last stretch (or the last handle).
    private ((double X, double Z) Way, bool Either)? Ahead(TrackEditor tracks, NetworkLines? lines)
    {
        static (double X, double Z)? Unit(double x, double z)
        {
            var length = Math.Sqrt(x * x + z * z);
            return length < 1e-6 ? null : (x / length, z / length);
        }

        if (_anchors.Count == 0)
            return null;
        var last = _anchors[^1];
        if (_anchors.Count >= 2 || last.Smooth)
        {
            if (last.Smooth)
                return Unit(last.OutX, last.OutZ) is { } handle ? (handle, false) : null;
            var before = _anchors[^2].Point;
            return Unit(last.Point.X - before.X, last.Point.Z - before.Z) is { } stretch ? (stretch, false) : null;
        }

        if (last.Point.Node is { } node && tracks.Has(node))
        {
            // Out of a track's end: its heading there, read off its last stretch (so a curving track's tangent, not the chord
            // to its last control point).
            if (tracks.IsLooseEnd(node))
            {
                if (BuiltTrack.EndPose(lines, tracks, node, 30.0) is { } end)
                    return ((Math.Cos(end.Heading), Math.Sin(end.Heading)), false);
                if (tracks.LeavingEnd(node) is { } outward)
                    return (outward, false);
            }

            return tracks.RunsAt(node) is { } runs ? (runs, true) : null;
        }

        if (last.Point.Split is { } split && tracks.Network.Ways.TryGetValue(split.Way, out var way) && split.Segment + 1 < way.Nodes.Length &&
            tracks.Has(way.Nodes[split.Segment]) && tracks.Has(way.Nodes[split.Segment + 1]))
        {
            var (ax, az) = tracks.PositionOf(way.Nodes[split.Segment]);
            var (bx, bz) = tracks.PositionOf(way.Nodes[split.Segment + 1]);
            return Unit(bx - ax, bz - az) is { } along ? (along, true) : null;
        }

        return null;
    }

    /// Shift held: where the next point goes is pulled onto the line straight on from the last point (see Ahead), so
    /// track carries on exactly in line with what it leaves. Off (or with nothing to carry on from), snap as it is.
    public LayPoint? Straighten(LayPoint? snap, TrackEditor tracks, bool shift, NetworkLines? lines = null)
    {
        Guide = null;
        if (!shift || Busy || snap is not { } point || Ahead(tracks, lines) is not { } ahead)
            return snap;

        var from = (_anchors[^1].Point.X, _anchors[^1].Point.Z);
        var (wx, wz) = ahead.Way;
        var t = (point.X - from.Item1) * wx + (point.Z - from.Item2) * wz;
        if (!ahead.Either)
            t = Math.Max(t, 1.0);
        else if (Math.Abs(t) < 1.0)
            t = t < 0 ? -1.0 : 1.0;
        Guide = (from, ahead.Way, ahead.Either);
        return new LayPoint(from.Item1 + wx * t, from.Item2 + wz * t);
    }

    private (Anchor, int)? Pick(float2 mouse, Func<double, double, float2?> project)
    {
        bool Near(double x, double z) =>
            project(x, z) is { } s && MathF.Abs(s.X - mouse.X) < PickPixels && MathF.Abs(s.Y - mouse.Y) < PickPixels;

        foreach (var a in _anchors)
        {
            if (a.Smooth && Near(a.Point.X + a.OutX, a.Point.Z + a.OutZ))
                return (a, 1);
            if (a.Smooth && Near(a.Point.X - a.OutX, a.Point.Z - a.OutZ))
                return (a, -1);
        }

        foreach (var a in _anchors)
            if (Near(a.Point.X, a.Point.Z))
                return (a, 0);
        return null;
    }

    // --- The curve. ---

    private static (double X, double Z) Bezier(Anchor a, Anchor b, double t)
    {
        var (p0x, p0z) = (a.Point.X, a.Point.Z);
        var (p1x, p1z) = (a.Point.X + a.OutX, a.Point.Z + a.OutZ);
        var (p2x, p2z) = (b.Point.X - b.OutX, b.Point.Z - b.OutZ);
        var (p3x, p3z) = (b.Point.X, b.Point.Z);
        var u = 1 - t;
        var w0 = u * u * u;
        var w1 = 3 * u * u * t;
        var w2 = 3 * u * t * t;
        var w3 = t * t * t;
        return (w0 * p0x + w1 * p1x + w2 * p2x + w3 * p3x, w0 * p0z + w1 * p1z + w2 * p2z + w3 * p3z);
    }

    /// The path sampled about every Step metres, the placed points kept as they are (so their joins and branches stay).
    /// With preview, a last stretch out to it as if it were the next corner.
    public List<LayPoint> Points((double X, double Z)? preview = null, double step = Step)
    {
        var anchors = _anchors.ToList();
        if (preview is { } p && anchors.Count > 0)
            anchors.Add(new Anchor { Point = new LayPoint(p.X, p.Z) });

        var points = new List<LayPoint>();
        for (var i = 0; i < anchors.Count; i++)
        {
            points.Add(anchors[i].Point);
            if (i == anchors.Count - 1)
                break;

            var (a, b) = (anchors[i], anchors[i + 1]);
            var length = 0.0;
            var last = (a.Point.X, a.Point.Z);
            for (var k = 1; k <= 16; k++)
            {
                var q = Bezier(a, b, k / 16.0);
                length += Math.Sqrt((q.X - last.X) * (q.X - last.X) + (q.Z - last.Z) * (q.Z - last.Z));
                last = q;
            }

            var n = Math.Max(1, (int)Math.Ceiling(length / step));
            for (var k = 1; k < n; k++)
            {
                var (x, z) = Bezier(a, b, (double)k / n);
                points.Add(new LayPoint(x, z));
            }
        }

        return points;
    }

    /// The path's length, its tightest curve, and the grade of the ground under it: end to end, and the steepest over
    /// any GradeWindow metres of it.
    public static Measure? Measured(IReadOnlyList<LayPoint> points, Func<double, double, float> height)
    {
        if (points.Count < 2)
            return null;

        var s = new double[points.Count];
        var h = new double[points.Count];
        h[0] = height(points[0].X, points[0].Z);
        var minRadius = double.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            s[i] = s[i - 1] + Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            h[i] = height(b.X, b.Z);
            if (i < points.Count - 1)
                minRadius = Math.Min(minRadius, Circumradius(a, b, points[i + 1]));
        }

        var length = s[^1];
        if (length < 1.0)
            return null;

        var steepest = 0.0;
        var j = 0;
        for (var i = 0; i < points.Count; i++)
        {
            while (j < points.Count - 1 && s[j] - s[i] < GradeWindow)
                j++;
            if (s[j] - s[i] >= Math.Min(GradeWindow, length) * 0.99)
                steepest = Math.Max(steepest, Math.Abs(h[j] - h[i]) / (s[j] - s[i]));
        }

        return new Measure(length, minRadius, (h[^1] - h[0]) / length, steepest);
    }

    private static double Circumradius(LayPoint a, LayPoint b, LayPoint c)
    {
        var ab = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        var bc = Math.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Z - b.Z) * (c.Z - b.Z));
        var ca = Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Z - c.Z) * (a.Z - c.Z));
        var cross = Math.Abs((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X));
        return cross < 1e-9 ? double.PositiveInfinity : ab * bc * ca / (2.0 * cross);
    }

    // --- Drawing. ---

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, (double X, double Z)? preview)
    {
        void Path(List<LayPoint> points, Rgba color, float width)
        {
            for (var i = 1; i < points.Count; i++)
                if (project(points[i - 1].X, points[i - 1].Z) is { } a && project(points[i].X, points[i].Z) is { } b)
                    draw.AddLine(a, b, color, S(width));
        }

        if (Guide is { } guide)
        {
            // The line Shift holds to, faint, a long way on (both ways when either will do).
            var (fx, fz) = guide.From;
            var (wx, wz) = guide.Way;
            const double reach = 600.0, piece = 15.0;
            for (var s = guide.Either ? -reach : 0.0; s < reach; s += piece * 2)
                if (project(fx + wx * s, fz + wz * s) is { } a && project(fx + wx * (s + piece), fz + wz * (s + piece)) is { } b)
                    draw.AddLine(a, b, Accent.WithAlpha(0.6f), S(1.5f));
        }

        if (preview is not null && _dragging is null && _anchors.Count > 0)
            Path(Points(preview), Rgba.White.WithAlpha(0.35f), 2f);
        Path(Points(), Rgba.White, 2.5f);

        foreach (var a in _anchors)
        {
            if (project(a.Point.X, a.Point.Z) is not { } p)
                continue;

            if (a.Smooth)
            {
                foreach (var sign in (ReadOnlySpan<int>)[1, -1])
                {
                    if (project(a.Point.X + a.OutX * sign, a.Point.Z + a.OutZ * sign) is not { } handle)
                        continue;
                    draw.AddLine(p, handle, Accent, S(1.5f));
                    draw.AddCircleFilled(handle, S(4f), Accent);
                }
            }

            var half = S(5f);
            var snapped = a.Point.Node is not null || a.Point.Split is not null;
            var fill = snapped ? Style.Clear : Rgba.White;
            draw.AddRectFilled(p - new float2(half), p + new float2(half), fill);
            draw.AddRect(p - new float2(half), p + new float2(half), Background, 0f, ImDrawFlags.None, 1f);
        }
    }

    /// A small label beside the pointer.
    public static void Label(ImDrawListPtr draw, float2 at, string text, Rgba color)
    {
        var size = S(13f);
        var width = Canvas.SpacedWidth(Medium, size, text, 0f);
        var min = at + new float2(S(16f), S(12f));
        draw.AddRectFilled(min - new float2(S(6f), S(4f)), min + new float2(width + S(6f), size + S(5f)), Surface, S(3f));
        Canvas.Text(draw, Medium, size, min, color, text);
    }

    /// How far the track built through the thinned points may stray from the path drawn, metres: well inside what the
    /// mouse places a point to.
    private const double ThinTolerance = 0.15;

    /// Turns sharper than this at a point are corners, kept as they are, degrees.
    private const double CornerDegrees = 5.0;

    /// Drawn track with as few control points as lay the same track: the path's sampled every few metres to be drawn, but
    /// a straight needs only its ends and a curve a point every few degrees (FollowTool.ControlPoints: where the spline the
    /// track's built as would stray more than a few centimetres, it gets more). Points on track (joins, branches) and
    /// corners are kept as they are.
    public static List<LayPoint> Thin(IReadOnlyList<LayPoint> points)
    {
        if (points.Count < 3)
            return [.. points];

        bool Kept(int i)
        {
            if (i == 0 || i == points.Count - 1 || points[i].Node is not null || points[i].Split is not null)
                return true;
            // A corner turns more than the curve it's on: than the points either side turn, on average.
            var here = Turn(i);
            var aside = 0.5 * ((i > 1 ? Turn(i - 1) : here) + (i < points.Count - 2 ? Turn(i + 1) : here));
            return Math.Abs(here - aside) > CornerDegrees * Math.PI / 180.0;
        }

        double Turn(int i)
        {
            var (a, b, c) = (points[i - 1], points[i], points[i + 1]);
            var turn = Math.Atan2(c.Z - b.Z, c.X - b.X) - Math.Atan2(b.Z - a.Z, b.X - a.X);
            return Math.Atan2(Math.Sin(turn), Math.Cos(turn));
        }

        var result = new List<LayPoint> { points[0] };
        var from = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (!Kept(i))
                continue;
            // The run from the last kept point to this one, thinned.
            if (i - from >= 2)
            {
                var run = new List<(double X, double Z)>();
                for (var k = from; k <= i; k++)
                    run.Add((points[k].X, points[k].Z));
                var pieces = GuideSmoothing.Pieces(run);
                if (pieces.Count > 0)
                    foreach (var u in FollowTool.ControlPoints(pieces, merge: true, tolerance: ThinTolerance).Skip(1).SkipLast(1))
                    {
                        var (x, z) = Alignment.Position(pieces, u);
                        result.Add(new LayPoint(x, z));
                    }
            }

            result.Add(points[i]);
            from = i;
        }

        return result;
    }

    public static string Describe(Measure m, bool imperial)
    {
        var length = imperial
            ? m.Length >= 1609.344 ? $"{m.Length / 1609.344:0.00} mi" : $"{m.Length * 3.28084:0} ft"
            : m.Length >= 1000 ? $"{m.Length / 1000:0.00} km" : $"{m.Length:0} m";
        var radius = double.IsInfinity(m.MinRadius) ? "straight" : imperial ? $"R {m.MinRadius * 3.28084:0} ft" : $"R {m.MinRadius:0} m";
        return string.Create(CultureInfo.InvariantCulture, $"{length}   {radius}   {m.SteepestGrade * 100:0.0}% max");
    }
}
