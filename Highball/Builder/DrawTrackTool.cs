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

/// The Track tool: track laid as real track is designed, piece by piece, each running on from the last with no kink.
/// Straight carries straight on (or, at the very start off any track, goes where it's pointed); Curve turns from where the
/// track has got to, a circular curve with an easement either side, through the place clicked (or, with a radius picked,
/// at that radius towards it). Started on a track's loose end it carries on from that track. The track's kept as its
/// corners and their curves (AlignmentTrack) and built from them exactly. Enter lays it; Backspace takes the last piece
/// back; Tab switches Straight and Curve; [ and ] step the radius.
public sealed class DrawTrackTool(ToolContext context, IReadOnlySet<long> selection) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    /// The radii [ and ] step through, metres (0: as the click puts it).
    private static readonly double[] Radii = [0, 150, 200, 250, 300, 400, 500, 600, 800, 1000, 1200, 1500, 2000, 2500, 3000];

    private enum Mode { Straight, Curve }

    private static Mode _mode = Mode.Straight;

    /// The radius curves are laid at (0: through the click), and the last one picked, to go back to.
    private static double _radius;
    private static double _lastFixed;

    /// No curve tighter than this, metres (0: none).
    private static float _least = 150f;

    /// The easement either side of a curve, metres (0: as the radius would have it).
    private static float _easing;

    /// Where the track starts (on a track's end, it joins it there), and which way it leaves (null: any way, until the
    /// first piece says).
    private LayPoint? _start;

    /// The corners so far, where the track has got to and which way it's running there; and each piece's state before it,
    /// for Backspace.
    private readonly List<Corner> _corners = [];
    private (double X, double Z) _end;
    private (double X, double Z)? _dir;
    private readonly Stack<(int Corners, (double X, double Z) End, (double X, double Z)? Dir)> _history = new();

    // The piece the pointer would add, and the whole track with it, to draw and measure.
    private (Corner? Corner, (double X, double Z) End, (double X, double Z)? Dir, double Radius)? _next;
    private List<(double X, double Z)>? _preview;
    private long? _hover;
    private bool _tightened;

    public PenTool.Measure? Measure { get; private set; }

    public void Idle()
    {
        _hover = null;
        _next = null;
    }

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        var camera = context.Camera;
        var mouse = Input.MousePosition;
        if (_start is { Node: { } startNode } && !tracks.Has(startNode))
            Clear();

        if (!Keyboard.KeysBlocked)
        {
            if (Input.KeyDown(GlfwKey.Tab))
                _mode = _mode == Mode.Straight ? Mode.Curve : Mode.Straight;
            if (Input.KeyDown(GlfwKey.RightBracket) || Input.KeyDown(GlfwKey.LeftBracket))
            {
                var at = Array.FindIndex(Radii, r => r >= _radius - 1e-6);
                at = Math.Clamp((at < 0 ? 0 : at) + (Input.KeyDown(GlfwKey.RightBracket) ? 1 : -1), 0, Radii.Length - 1);
                _radius = Radii[at];
            }
        }

        // Where the pointer is: a track's control point, a guide, or the ground.
        _hover = tracks.NodeAt(mouse, camera.X, camera.Z, context.ProjectGround);
        (double X, double Z)? target = _hover is { } node ? tracks.PositionOf(node)
            : GuideAt(mouse) is { } guide ? (guide.X, guide.Z)
            : context.Hit is { } hit ? (hit.X, hit.Z) : null;

        if (_start is null)
        {
            _next = null;
            if (target is { } first && Input.MouseDown(GlfwMouseButton.Left))
                Begin(first);
        }
        else
        {
            _next = target is { } t ? Piece(t) : null;
            if (_next is { } piece && Input.MouseDown(GlfwMouseButton.Left))
            {
                _history.Push((_corners.Count, _end, _dir));
                if (piece.Corner is { } corner)
                    _corners.Add(corner);
                (_end, _dir) = (piece.End, piece.Dir);
                _next = null;
            }
        }

        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
        {
            if (_history.TryPop(out var before))
            {
                _corners.RemoveRange(before.Corners, _corners.Count - before.Corners);
                (_end, _dir) = (before.End, before.Dir);
            }
            else
                Clear();
        }

        Preview();
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && _start is { } s && _history.Count > 0)
        {
            var points = new List<LayPoint> { s };
            points.AddRange(_corners.Select(c => new LayPoint(c.X, c.Z)));
            // Ending on a track's control point joins it there.
            var endNode = tracks.NodesNear(_end.X, _end.Z, 0.05).FirstOrDefault();
            points.Add(endNode != 0 && tracks.Has(endNode) ? new LayPoint(_end.X, _end.Z, Node: endNode) : new LayPoint(_end.X, _end.Z));
            var tags = new Dictionary<string, string> { [AlignmentTrack.Tag] = AlignmentTrack.Encode(_corners) };
            context.Commit(tracks.Lay(points, lead: false, exact: true, along: RailClass.Main, tags: tags));
            Clear();
        }
    }

    private void Clear()
    {
        (_start, _dir, _next, _preview, Measure) = (null, null, null, null, null);
        _corners.Clear();
        _history.Clear();
    }

    /// The first click: on a track's loose end it carries on from that track the way it runs out; anywhere else it starts free.
    private void Begin((double X, double Z) at)
    {
        var tracks = context.Tracks;
        if (_hover is { } node && tracks.IsLooseEnd(node))
        {
            _start = new LayPoint(at.X, at.Z, Node: node);
            _dir = BuiltTrack.EndPose(context.Source.Lines, tracks, node, 30.0) is { } pose ? (Math.Cos(pose.Heading), Math.Sin(pose.Heading)) : tracks.LeavingEnd(node);
        }
        else
        {
            _start = _hover is { } on ? new LayPoint(at.X, at.Z, Node: on) : new LayPoint(at.X, at.Z);
            _dir = null;
        }

        _end = at;
    }

    /// The piece the pointer at target would add: in Straight, on along the way the track runs to level with the target
    /// (from a free start, straight to it); in Curve, a curve from where the track's got to whose end lies towards the
    /// target, at the radius picked or through the target. Null if it would go nowhere.
    private (Corner? Corner, (double X, double Z) End, (double X, double Z)? Dir, double Radius)? Piece((double X, double Z) target)
    {
        var (cx, cz) = (target.X - _end.X, target.Z - _end.Z);
        var reach = Math.Sqrt(cx * cx + cz * cz);
        if (reach < 1.0)
            return null;

        if (_dir is not { } d)
            return (null, target, (cx / reach, cz / reach), double.PositiveInfinity);

        var along = cx * d.X + cz * d.Z;
        var side = d.X * cz - d.Z * cx;
        var half = Math.Atan2(side, along); // half the turn: a symmetric curve ends on the line at half its turn
        if (_mode == Mode.Straight || Math.Abs(half) < 0.25 * Math.PI / 180.0)
            return along < 1.0 ? null : (null, (_end.X + d.X * along, _end.Z + d.Z * along), d, double.PositiveInfinity);

        half = Math.Clamp(half, -Math.PI * 0.49, Math.PI * 0.49);
        var turn = 2.0 * half;
        double Easing(double r) => _easing > 0f ? _easing : AlignmentTrack.DefaultEasing(r);

        // The radius: as picked, or whatever puts the curve's end on the target (its tangent length grows with the radius).
        double radius;
        if (_radius > 0)
            radius = _radius;
        else
        {
            var want = reach / (2.0 * Math.Cos(half));
            var (lo, hi) = (1.0, 100000.0);
            for (var i = 0; i < 60; i++)
            {
                var mid = Math.Sqrt(lo * hi);
                if (AlignmentTrack.CurveAt(turn, mid, Easing(mid)).Tangent < want)
                    lo = mid;
                else
                    hi = mid;
            }

            radius = lo;
        }

        if (_least > 0f)
            radius = Math.Max(radius, _least);
        var curve = AlignmentTrack.CurveAt(turn, radius, Easing(radius));
        var corner = (_end.X + d.X * curve.Tangent, _end.Z + d.Z * curve.Tangent);
        var (ox, oz) = (d.X * Math.Cos(turn) - d.Z * Math.Sin(turn), d.X * Math.Sin(turn) + d.Z * Math.Cos(turn));
        var end = (corner.Item1 + ox * curve.Tangent, corner.Item2 + oz * curve.Tangent);
        return (new Corner(corner.Item1, corner.Item2, radius, curve.Easing), end, (ox, oz), radius);
    }

    /// The whole track so far with the next piece, built as it will be, and its length, tightest curve and grade.
    private void Preview()
    {
        _preview = null;
        Measure = null;
        if (_start is not { } s)
            return;
        var corners = _corners.ToList();
        var end = _end;
        if (_next is { } piece)
        {
            if (piece.Corner is { } c)
                corners.Add(c);
            end = piece.End;
        }

        if (Math.Abs(end.X - s.X) + Math.Abs(end.Z - s.Z) < 1e-6)
            return;
        var built = AlignmentTrack.Build((s.X, s.Z), corners, end);
        if (built is null)
            return;
        _preview = built.Points;
        _tightened = built.Tightened;
        Measure = PenTool.Measured([.. built.Points.Select(p => new LayPoint(p.X, p.Z))], context.Source.HeightAt);
    }

    private GuidePlace? GuideAt(float2 mouse)
    {
        if (context.Guides is not { } guides || Input.Key(GlfwKey.LeftAlt) || Input.Key(GlfwKey.RightAlt))
            return null;
        var camera = context.Camera;
        return guides.PlaceAt(mouse, context.ProjectGround, camera.X, camera.Z, context.Reach, S(12f));
    }

    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        var project = context.ProjectGround;
        if (_hover is { } hovered && !context.Tracks.Has(hovered))
            _hover = null;
        context.Tracks.Draw(draw, camera.X, camera.Z, project, _hover, selection, null);

        if (_preview is { } points)
            for (var i = 1; i < points.Count; i++)
                if (project(points[i - 1].X, points[i - 1].Z) is { } a && project(points[i].X, points[i].Z) is { } b)
                    draw.AddLine(a, b, _tightened ? Caution : Rgba.White, S(2.5f));

        // The corners (where the straights meet), faint, and where the track's got to.
        foreach (var c in _corners)
            if (project(c.X, c.Z) is { } p)
                draw.AddCircle(p, S(4f), Accent.WithAlpha(0.7f), 0, S(1.5f));
        if (_start is { } s && project(s.X, s.Z) is { } sp)
            draw.AddCircleFilled(sp, S(5f), Accent);
        if (_start is not null && project(_end.X, _end.Z) is { } ep)
            draw.AddCircle(ep, S(7f), Rgba.White, 0, S(2f));

        context.Kinks.Update(context);
        context.Kinks.Draw(draw, project);
        if (context.Blocked)
            return;
        var imperial = Settings.Current.Units == UnitSystem.Imperial;
        var mode = _mode == Mode.Straight ? "Straight" : "Curve";
        if (_start is null)
            PenTool.Label(draw, Input.MousePosition, $"{mode}: click a track end, or anywhere, to start", TextDim);
        else if (Measure is { } m)
        {
            var radius = _next is { Radius: var r } && !double.IsInfinity(r) ? imperial ? $"   R {r * FeetPerMeter:0} ft" : $"   R {r:0} m" : "";
            PenTool.Label(draw, Input.MousePosition, $"{mode}{radius}   {PenTool.Describe(m, imperial)}", _tightened ? Caution : Style.Text);
        }
    }

    public void Panel(bool imperial)
    {
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
        var unit = imperial ? FeetPerMeter : 1f;
        var suffix = imperial ? "ft" : "m";

        Kit.BeginCard("Lay", _start is null ? "click to start" : $"{_history.Count} piece{(_history.Count == 1 ? "" : "s")}");
        var pick = ToolPanel.Mode("draw.mode", ["Straight", "Curve"], (int)_mode, 2);
        if (pick >= 0)
            _mode = (Mode)pick;

        // The radius: Free (through the click) or Fixed at a value, which [ and ] step through the usual ones.
        var fixedRadius = _radius > 0 ? 1 : 0;
        var how = ToolPanel.Mode("draw.radiusmode", ["Free radius", "Fixed radius"], fixedRadius, 2);
        if (how != fixedRadius)
            _radius = how == 1 ? (_lastFixed > 0 ? _lastFixed : 500.0) : 0.0;
        if (_radius > 0)
        {
            var radius = (float)(_radius * unit);
            if (ToolPanel.Value("draw.radius", "Radius", ref radius, 100f * unit, 3000f * unit, $"0' {suffix}'", log: true))
                _radius = Math.Round(radius / unit / 5.0) * 5.0;
            _lastFixed = _radius;
        }

        var least = _least * unit;
        if (ToolPanel.Value("draw.least", "Least radius", ref least, 0f, 1500f * unit, $"0' {suffix}';;'none'"))
            _least = least / unit < 5f ? 0f : least / unit;
        var easing = _easing * unit;
        if (ToolPanel.Value("draw.easing", "Easement", ref easing, 0f, 200f * unit, $"0' {suffix}';;'auto'"))
            _easing = easing / unit < 2f ? 0f : easing / unit;
        if (_tightened)
            ToolPanel.Problem("Two curves don't fit on the straight between them: shown tighter (amber).");
        context.Kinks.Panel();
        Kit.EndCard();

        if (Measure is not { } m)
            return;
        Kit.BeginCard("Track");
        Kit.Stats(
        [
            new Stat("Length", Length(m.Length)),
            new Stat("Tightest curve", double.IsInfinity(m.MinRadius) ? "Straight" : Length(m.MinRadius)),
            new Stat("Ground grade", string.Create(CultureInfo.InvariantCulture, $"{m.Grade * 100:0.00}%")),
            new Stat("Steepest", string.Create(CultureInfo.InvariantCulture, $"{m.SteepestGrade * 100:0.00}%")),
        ], 2);
        Kit.EndCard();
    }

    public bool Escape()
    {
        if (_start is null)
            return false;
        Clear();
        return true;
    }

    public (string Key, string Action)[] Hints =>
        [("LMB", _start is null ? "Start" : "Lay piece"), ("TAB", _mode == Mode.Straight ? "Curve" : "Straight"), ("[ ]", "Radius"),
         ("ALT", "No guide snap"), ("ENTER", "Lay track"), ("BACKSPACE", "Take back"), ("ESC", "Cancel")];
}
