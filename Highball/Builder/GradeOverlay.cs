using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// Every rail line in view coloured by its grade (or its class), and the grade under the pointer (averaged over a short
/// stretch, as a train would feel it) if it's over one. Each point's colour is worked out once per layout of the track
/// and kept; the lines are projected on every core (FrameJobs), each slice into its own list, and drawn on the main thread.
public sealed class GradeOverlay
{
    /// How far either side of the pointer the readout's grade is averaged, metres.
    private const double ReadoutWindow = 50.0;

    /// How far either side of a segment its colour's grade is taken, metres: the short pieces round a bridge or a
    /// crossing would otherwise flicker red.
    private const float ColourWindow = 25f;

    private readonly List<NetworkLine> _inView = [];
    private List<(float2 A, float2 B, Rgba Color, NetworkLine Line, int Point)>[] _segments = [];

    /// Each line's colour at each point (the segment ending there), for the track as it was laid out (_coloursFor) and
    /// the colouring (_byClass).
    private readonly Dictionary<NetworkLine, Rgba[]> _colours = [];
    private NetworkLines? _coloursFor;
    private bool _byClass;

    /// The grade under the pointer this frame: where, the grade, the height, and the line's class.
    public (float2 Screen, float Grade, float Height, int Class)? At { get; private set; }

    public static Rgba GradeColor(float grade) => MathF.Abs(grade) switch
    {
        < 0.01f => Clear,
        < 0.02f => Caution,
        _ => Stop,
    };

    /// The lines within reach metres of the camera, coloured by grade or by class; blocked: the pointer's not over the
    /// world, so nothing's read out.
    public void Draw(ImDrawListPtr draw, NetworkLines lines, CameraView view, double reach, bool byClass, bool blocked)
    {
        At = null;
        if (!ReferenceEquals(lines, _coloursFor) || byClass != _byClass)
        {
            _colours.Clear();
            (_coloursFor, _byClass) = (lines, byClass);
        }

        var camera = view.Camera;
        _inView.Clear();
        foreach (var line in lines.Lines)
            if (line.Rail && line.MaxX >= camera.X - reach && line.MinX <= camera.X + reach && line.MaxZ >= camera.Z - reach && line.MinZ <= camera.Z + reach)
            {
                if (!_colours.ContainsKey(line))
                    _colours[line] = Colours(line, byClass);
                _inView.Add(line);
            }

        _ = view.ViewProjection;
        var size = view.Size;
        var slices = FrameJobs.Slices(_inView.Count, 4);
        if (_segments.Length < slices)
            _segments = [.. Enumerable.Range(0, slices).Select(i => i < _segments.Length ? _segments[i] : [])];
        for (var i = 0; i < slices; i++)
            _segments[i].Clear();
        using (Profiler.Sample("Project"))
            FrameJobs.For(_inView.Count, slices, (slice, from, to) => Segments(view, reach, from, to, size, _segments[slice]));
        using var drawing = Profiler.Sample("Lines");

        var mouse = Input.MousePosition;
        var nearest = S(12f);
        (NetworkLine Line, int Point)? hovered = null;
        var width = S(3f);
        for (var slice = 0; slice < slices; slice++)
        {
            foreach (var (a, b, color, line, point) in _segments[slice])
            {
                draw.AddLine(a, b, color, width);
                var d = DistanceToSegment(mouse, a, b);
                if (d < nearest && !blocked)
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
        while (i0 > 0 && l.S[over.Point] - l.S[i0] < ReadoutWindow)
            i0--;
        while (i1 < l.Count - 1 && l.S[i1] - l.S[over.Point] < ReadoutWindow)
            i1++;
        var span = l.S[i1] - l.S[i0];
        At = (mouse, span > 0.01f ? (l.Y[i1] - l.Y[i0]) / span : 0f, l.Y[over.Point], l.Class);
    }

    /// A line's colour at each point: the segment ending there, by the grade over ColourWindow either side of its middle
    /// (or by the line's class).
    private static Rgba[] Colours(NetworkLine line, bool byClass)
    {
        var colours = new Rgba[line.Count];
        if (byClass)
        {
            Array.Fill(colours, RailColours.Of(line.Class).WithAlpha(0.9f));
            return colours;
        }

        var (lo, hi) = (0, 0);
        for (var i = 1; i < line.Count; i++)
        {
            var middle = (line.S[i] + line.S[i - 1]) * 0.5f;
            while (lo < i - 1 && line.S[lo + 1] <= middle - ColourWindow)
                lo++;
            hi = Math.Max(hi, i);
            while (hi < line.Count - 1 && line.S[hi] < middle + ColourWindow)
                hi++;
            var ds = line.S[hi] - line.S[lo];
            colours[i] = GradeColor(ds > 0.01f ? (line.Y[hi] - line.Y[lo]) / ds : 0f).WithAlpha(0.9f);
        }

        return colours;
    }

    /// One slice of the lines' coloured segments on the screen (a job). Points further than reach from the camera aren't
    /// projected (a long line in reach runs on for kilometres past it); points closer than a couple of pixels to the last
    /// one drawn are skipped, and so are segments off the screen.
    private void Segments(CameraView view, double reach, int from, int to, float2 size, List<(float2, float2, Rgba, NetworkLine, int)> into)
    {
        into.Clear();
        const float margin = 50f;
        var (cx, cz) = (view.Camera.X, view.Camera.Z);
        float2? At(NetworkLine line, int i) => Math.Abs(line.X[i] - cx) > reach || Math.Abs(line.Z[i] - cz) > reach
            ? null
            : view.Project(line.X[i], line.Y[i] + 0.5, line.Z[i]);

        for (var n = from; n < to; n++)
        {
            var line = _inView[n];
            var colours = _colours[line];
            var previous = At(line, 0);
            for (var i = 1; i < line.Count; i++)
            {
                if (At(line, i) is not { } b)
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
                    into.Add((a, b, colours[i], line, i));
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
}
