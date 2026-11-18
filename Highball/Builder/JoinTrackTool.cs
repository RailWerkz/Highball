using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Join tool: click two loose track ends and it draws the track between them itself (JoinCurve), leaving each end
/// the way that track runs and, with Match curvature, curving as it's curving there, read off the last stretch of it
/// (Sample). Enter lays it as the Pen tool does, so the class, bed and height in hand apply, as one edit.
public sealed class JoinTrackTool(ToolContext context, IReadOnlySet<long> selection) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;
    private const double Step = 10.0;

    /// How far back along each track its heading and curvature are read from, metres.
    private static float _sample = 60f;

    /// Curve out of and into each end as that track's curving, not only along its heading.
    private static bool _matchCurvature = true;

    /// Kept wider than this where it can be, metres (0: no least).
    private static float _minRadius;

    private long? _hover;
    private long? _a, _b;

    // The curve as last worked out, and what it was worked out from.
    private (long A, long B, float Sample, bool Match, float MinRadius, int Version)? _for;
    private JoinCurve.Fit? _fit;
    private List<(double X, double Z)>? _backA, _backB;
    private JoinCurve.Pose? _poseA, _poseB;

    /// The curve: its length, tightest curve and the ground's grade along it.
    public PenTool.Measure? Measure { get; private set; }

    public void Idle()
    {
        _hover = null;
        ForgetGone();
    }

    /// Lets go of a picked end that's no longer there (undone, or changed by the edit just made), and the curve to it.
    private void ForgetGone()
    {
        var tracks = context.Tracks;
        var gone = false;
        if (_a is { } a && !tracks.Has(a))
            (_a, gone) = (null, true);
        if (_b is { } b && !tracks.Has(b))
            (_b, gone) = (null, true);
        if (gone)
            (_fit, _for, Measure, _backA, _backB, _poseA, _poseB) = (null, null, null, null, null, null, null);
    }

    public void Update(float dt)
    {
        var tracks = context.Tracks;
        var camera = context.Camera;
        ForgetGone();

        _hover = tracks.NodeAt(Input.MousePosition, camera.X, camera.Z, context.ProjectGround) is { } on && tracks.IsLooseEnd(on) ? on : null;
        if (Input.MouseDown(GlfwMouseButton.Left) && _hover is { } picked)
        {
            if (picked == _a)
                (_a, _b) = (_b, null);
            else if (picked == _b)
                _b = null;
            else if (_a is null)
                _a = picked;
            else if (_b is null)
                _b = picked;
            else
                (_a, _b) = (picked, null);
        }

        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
        {
            if (_b is not null)
                _b = null;
            else
                _a = null;
        }

        Refit();
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && _fit is { } fit && _a is { } a && _b is { } b)
        {
            var points = new List<LayPoint> { new(fit.Points[0].X, fit.Points[0].Z, Node: a) };
            for (var i = 1; i < fit.Points.Count - 1; i++)
                points.Add(new LayPoint(fit.Points[i].X, fit.Points[i].Z));
            points.Add(new LayPoint(fit.Points[^1].X, fit.Points[^1].Z, Node: b));
            // (Exact: the curve's worked out to run on from both ends, so the fitting mustn't redraw it.)
            context.Commit(tracks.Lay(PenTool.Thin(points), lead: false, exact: true));
            (_a, _b, _fit, _for, Measure) = (null, null, null, null, null);
        }
    }

    /// The curve worked out again when either end, the settings or the track have changed.
    private void Refit()
    {
        var tracks = context.Tracks;
        if (_a is not { } a || _b is not { } b)
        {
            (_fit, _for, Measure) = (null, null, null);
            _backA = _a is { } only ? tracks.Behind(only, _sample) : null;
            _backB = null;
            return;
        }

        var key = (a, b, _sample, _matchCurvature, _minRadius, tracks.Network.Version + (context.Rebuilding ? 0 : 1 << 30));
        if (_for == key)
            return;
        _for = key;
        _backA = tracks.Behind(a, _sample);
        _backB = tracks.Behind(b, _sample);
        // (Read off the rails as built where it can: what the new track has to run on from.)
        _poseA = BuiltTrack.EndPose(context.Source.Lines, tracks, a, _sample);
        _poseB = BuiltTrack.EndPose(context.Source.Lines, tracks, b, _sample);
        _fit = null;
        Measure = null;
        if (_poseA is not { } pa || _poseB is not { } pb)
            return;

        // Into b, the curve runs against the way b's track runs out of it, and so curves the other way round.
        var into = pb with { Heading = pb.Heading + Math.PI, Curvature = -pb.Curvature };
        _fit = JoinCurve.Solve(pa, into, _matchCurvature, _minRadius, Step);
        if (_fit is not null)
            Measure = PenTool.Measured([.. _fit.Points.Select(p => new LayPoint(p.X, p.Z))], context.Source.HeightAt);
    }

    public void Draw(ImDrawListPtr draw)
    {
        var camera = context.Camera;
        var project = context.ProjectGround;
        // (Drawn after an undo before the next Update: what was picked may be gone.)
        ForgetGone();
        if (_hover is { } hovered && !context.Tracks.Has(hovered))
            _hover = null;
        context.Tracks.Draw(draw, camera.X, camera.Z, project, _hover, selection, null);

        void Path(List<(double X, double Z)>? points, Rgba color, float width)
        {
            if (points is null)
                return;
            for (var i = 1; i < points.Count; i++)
                if (project(points[i - 1].X, points[i - 1].Z) is { } p && project(points[i].X, points[i].Z) is { } q)
                    draw.AddLine(p, q, color, S(width));
        }

        // How far back each end is read from, then the curve.
        Path(_backA, Accent, 4f);
        Path(_backB, Accent, 4f);
        var tight = _minRadius > 0 && Measure is { } m0 && m0.MinRadius < _minRadius;
        Path(_fit?.Points, tight ? Caution : Rgba.White, 2.5f);

        foreach (var end in (ReadOnlySpan<long?>)[_a, _b])
            if (end is { } node && context.Tracks.Has(node) && project(context.Tracks.PositionOf(node).X, context.Tracks.PositionOf(node).Z) is { } p)
            {
                draw.AddCircleFilled(p, S(6f), Accent);
                draw.AddCircle(p, S(10f), Rgba.White, 0, S(2f));
            }

        context.Kinks.Update(context);
        context.Kinks.Draw(draw, project);
        if (Measure is { } m && !context.Blocked)
            PenTool.Label(draw, Input.MousePosition, PenTool.Describe(m, Settings.Current.Units == UnitSystem.Imperial), tight ? Caution : Style.Text);
        else if (!context.Blocked && _hover is null && context.Hit is not null)
            PenTool.Label(draw, Input.MousePosition, _a is null ? "Click a track end" : "Click the other end", TextDim);
    }

    public void Panel(bool imperial)
    {
        string Length(double metres) => imperial
            ? metres >= 1609.344 ? $"{metres / 1609.344:0.00} mi" : $"{metres * FeetPerMeter:0} ft"
            : metres >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";
        string Radius(double curvature) => Math.Abs(curvature) < 1e-5 ? "Straight" : Length(1.0 / Math.Abs(curvature));

        Kit.BeginCard("Join ends", _a is null ? "pick an end" : _b is null ? "pick the other" : _fit?.Spiral == false ? "Bézier" : "spiral");
        var unit = imperial ? FeetPerMeter : 1f;
        var sample = _sample * unit;
        if (ToolPanel.Value("join.sample", "Sample back", ref sample, 10f * unit, 400f * unit, imperial ? "0' ft'" : "0' m'", log: true))
            _sample = sample / unit;
        var least = _minRadius * unit;
        if (ToolPanel.Value("join.radius", "Least radius", ref least, 0f, 1500f * unit, imperial ? "0' ft';;'off'" : "0' m';;'off'"))
            _minRadius = least / unit < 5f ? 0f : least / unit;
        Kit.Toggle("join.match", "Match curvature", ref _matchCurvature);
        if (_poseA is { } pa && _poseB is { } pb && _a is not null && _b is not null)
            ToolPanel.Hint($"Ends curving: {Radius(pa.Curvature)}, {Radius(pb.Curvature)}");
        if (_a is not null && _b is not null && _fit is null)
            ToolPanel.Problem("Can't join these two ends.");
        else if (_fit?.Spiral == false)
            ToolPanel.Hint("The spiral fit didn't close: shown as a Bézier.");
        context.Kinks.Panel();
        Kit.EndCard();

        if (Measure is not { } m)
            return;
        Kit.BeginCard("Curve", _minRadius > 0 && m.MinRadius < _minRadius ? "tighter than least" : null);
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
        if (_a is null && _b is null)
            return false;
        (_a, _b, _fit, _for, Measure) = (null, null, null, null, null);
        return true;
    }

    public (string Key, string Action)[] Hints =>
        [("LMB", "Pick track end"), ("ENTER", "Lay track"), ("BACKSPACE", "Drop end"), ("ESC", "Cancel")];
}
