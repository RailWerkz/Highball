using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

/// The picked switch's handles in the Switches tool: its points, dragged along the track it stands on, and the end of its
/// diverging curve, dragged along the diverging track to make the switch longer and gentler or shorter and sharper. The
/// frog falls where the routes part, whatever that comes to. A drag shows what it would make, and letting go asks for it.
public sealed class SwitchHandles
{
    /// A change a drag asks for: where the curve ends, or where the points go; with a crossover's other switch, where its go.
    public sealed record Change(long Node, float? Reach, (double X, double Z)? SlideTo, (Turnout Turnout, (double X, double Z) To)? Partner = null);

    private enum Grab { None, Curve, Points, Crossover }

    private const float HandleRadius = 7f;
    private const float GrabRadius = 12f;
    private const float StartDrag = 4f;
    /// How near a slid switch may come to the next junction or the end of its track.
    private const float SlideClearance = 15f;

    private Grab _grab;
    private long _node;
    private float2 _from;
    private bool _moved;
    private float _reach;
    private (double X, double Z)? _slideTo;
    private double _slid;
    private (double X, double Z)? _partnerTo;

    public bool Active => _grab != Grab.None;

    /// What the curve in hand would make.
    public TurnoutFitting.CurveFit? Curve { get; private set; }

    /// How far the points in hand have been moved.
    public double? Slid => _grab is Grab.Points or Grab.Crossover && _moved ? _slid : null;

    /// The other switch of the crossover this one's in.
    public static Turnout? Partner(NetworkLines lines, Turnout turnout)
    {
        if (lines.Layouts.FirstOrDefault(l => l.Kind == JunctionKind.Crossover && l.Nodes.Contains(turnout.Node)) is not { } layout)
            return null;
        var other = layout.Nodes.FirstOrDefault(n => n != turnout.Node);
        return lines.Turnouts.FirstOrDefault(t => t.Node == other);
    }

    private static (double X, double Z) Middle(Turnout a, Turnout b) => ((a.X + b.X) / 2.0, (a.Z + b.Z) / 2.0);

    /// The middle of a crossover, for a scripted drag.
    public static (double X, double Z)? CrossoverHandle(NetworkLines lines, Turnout turnout) => Partner(lines, turnout) is { } other ? Middle(turnout, other) : null;

    /// Whether a switch can be edited here: a crossover's are laid out as a pair, and a slip isn't one switch.
    public static bool Editable(NetworkLines lines, Turnout turnout) =>
        lines.Layouts.FirstOrDefault(l => l.Nodes.Contains(turnout.Node))?.Kind is not (JunctionKind.Crossover or JunctionKind.LadderLink or JunctionKind.ScissorsCrossover or JunctionKind.SingleSlip or JunctionKind.DoubleSlip or JunctionKind.ThreeWay or JunctionKind.TwoAtOneNode);

    private static (double X, double Z)? CurveEnd(NetworkLines lines, Turnout turnout)
    {
        if (lines.SwitchOptions.GetValueOrDefault(turnout.Node)?.Reach is not { } reach)
            return null;
        var (x, z, _, _, _, _, _) = turnout.Reverse.At(reach);
        return (x, z);
    }

    /// One frame's mouse. ground: where it is on the ground, if it is.
    public Change? Update(NetworkLines lines, Turnout? turnout, float2 mouse, (double X, double Z)? ground, Func<double, double, float2?> project, float scale, bool pressed, bool held)
    {
        if (_grab == Grab.None)
        {
            if (!pressed || turnout is null)
                return null;
            float Away(double x, double z) => project(x, z) is { } at ? MathF.Sqrt((at.X - mouse.X) * (at.X - mouse.X) + (at.Y - mouse.Y) * (at.Y - mouse.Y)) : float.MaxValue;
            var reach = GrabRadius * scale;
            if (Partner(lines, turnout) is { } partner)
            {
                var (mx, mz) = Middle(turnout, partner);
                if (Away(mx, mz) <= reach)
                    (_grab, _node, _from, _moved, Curve, _slideTo, _partnerTo) = (Grab.Crossover, turnout.Node, mouse, false, null, null, null);
                return null;
            }

            if (!Editable(lines, turnout))
                return null;
            var grab = CurveEnd(lines, turnout) is { } end && Away(end.X, end.Z) <= reach ? Grab.Curve : Away(turnout.X, turnout.Z) <= reach ? Grab.Points : Grab.None;
            if (grab != Grab.None)
                (_grab, _node, _from, _moved, Curve, _slideTo) = (grab, turnout.Node, mouse, false, null, null);
            return null;
        }

        if (turnout is null || turnout.Node != _node)
        {
            _grab = Grab.None;
            return null;
        }

        if (MathF.Abs(mouse.X - _from.X) + MathF.Abs(mouse.Y - _from.Y) > StartDrag * scale)
            _moved = true;
        if (_moved && ground is { } hit)
        {
            if (_grab == Grab.Crossover && Partner(lines, turnout) is { } other)
            {
                SlideBoth(lines, turnout, other, hit.X, hit.Z);
            }
            else if (_grab == Grab.Curve)
            {
                _reach = TurnoutFitting.ReachNear(turnout, hit.X, hit.Z, lines.IsJunction);
                Curve = TurnoutFitting.CurveTo(turnout, _reach, lines.SwitchOptions.GetValueOrDefault(turnout.Node)?.Reach ?? 0f);
            }
            else
            {
                Slide(lines, turnout, hit.X, hit.Z);
            }
        }

        if (held)
            return null;

        var (grabbed, moved, curve) = (_grab, _moved, Curve);
        _grab = Grab.None;
        Curve = null;
        if (!moved)
            return null;
        if (grabbed == Grab.Curve)
            return curve is { Fits: true } ? new Change(turnout.Node, _reach, null) : null;
        if (grabbed == Grab.Crossover)
            return _slideTo is { } both && _partnerTo is { } theirs && Partner(lines, turnout) is { } mate ? new Change(turnout.Node, null, both, (mate, theirs)) : null;
        return _slideTo is { } to ? new Change(turnout.Node, null, to) : null;
    }

    /// How far a switch may be slid along a leg, short of what's next along it.
    private static float LegLimit(NetworkLines lines, Leg leg)
    {
        var limit = leg.Length - SlideClearance;
        foreach (var (node, point) in leg.Line.Nodes)
        {
            var s = (leg.Line.S[point] - leg.Start) * leg.Direction;
            if (s > 0.5f && lines.IsJunction(node))
                limit = MathF.Min(limit, s - SlideClearance);
        }

        return limit;
    }

    /// A crossover's middle dragged along its tracks: both switches move the same way and as far as they both can.
    private void SlideBoth(NetworkLines lines, Turnout a, Turnout b, double x, double z)
    {
        var (nx, nz, _, _, _, _, _) = a.Normal.At(5f);
        var (ux, uz) = (nx - a.X, nz - a.Z);
        var length = Math.Max(Math.Sqrt(ux * ux + uz * uz), 1e-9);
        (ux, uz) = (ux / length, uz / length);
        if ((b.X - a.X) * ux + (b.Z - a.Z) * uz < 0.0)
            (ux, uz) = (-ux, -uz);

        (Leg Ahead, Leg Behind) Legs(Turnout t)
        {
            var (px, pz, _, _, _, _, _) = t.Normal.At(5f);
            return (px - t.X) * ux + (pz - t.Z) * uz >= 0.0 ? (t.Normal, t.Toe) : (t.Toe, t.Normal);
        }

        var (legA, legB) = (Legs(a), Legs(b));
        var (mx, mz) = Middle(a, b);
        var wanted = (x - mx) * ux + (z - mz) * uz;
        var forward = Math.Max(MathF.Min(LegLimit(lines, legA.Ahead), LegLimit(lines, legB.Ahead)), 0f);
        var backward = Math.Max(MathF.Min(LegLimit(lines, legA.Behind), LegLimit(lines, legB.Behind)), 0f);
        _slid = Math.Clamp(wanted, -backward, forward);
        if (Math.Abs(_slid) < 1.0)
        {
            (_slideTo, _partnerTo) = (null, null);
            return;
        }

        var far = (float)Math.Abs(_slid);
        var (ax, az, _, _, _, _, _) = (_slid > 0.0 ? legA.Ahead : legA.Behind).At(far);
        var (bx, bz, _, _, _, _, _) = (_slid > 0.0 ? legB.Ahead : legB.Behind).At(far);
        (_slideTo, _partnerTo) = ((ax, az), (bx, bz));
    }

    /// The nearest place on the track the switch stands on, short of what's next along it.
    private void Slide(NetworkLines lines, Turnout turnout, double x, double z)
    {
        var (best, bestAway, bestAt) = (0.0, double.MaxValue, ((double, double)?)null);
        foreach (var (leg, sign) in new[] { (turnout.Normal, 1.0), (turnout.Toe, -1.0) })
        {
            var limit = leg.Length - SlideClearance;
            foreach (var (node, point) in leg.Line.Nodes)
            {
                var s = (leg.Line.S[point] - leg.Start) * leg.Direction;
                if (s > 0.5f && lines.IsJunction(node))
                    limit = MathF.Min(limit, s - SlideClearance);
            }

            for (var s = 0f; s <= limit; s += 1f)
            {
                var (px, pz, _, _, _, _, _) = leg.At(s);
                var away = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (away < bestAway)
                    (best, bestAway, bestAt) = (s * sign, away, (px, pz));
            }
        }

        (_slid, _slideTo) = (best, Math.Abs(best) >= 1.0 ? bestAt : null);
    }

    public void Draw(ImDrawListPtr draw, NetworkLines lines, Turnout turnout, Func<double, double, float2?> project, float scale, Func<double, string> length)
    {
        if (Partner(lines, turnout) is { } partner)
        {
            var (mx, mz) = Middle(turnout, partner);
            if (_grab == Grab.Crossover && _slideTo is { } toA && _partnerTo is { } toB)
                (mx, mz) = ((toA.X + toB.X) / 2.0, (toA.Z + toB.Z) / 2.0);
            if (project(mx, mz) is { } middle)
            {
                draw.AddCircleFilled(middle, HandleRadius * scale, _grab == Grab.Crossover ? Style.Caution : Rgba.White, 16);
                draw.AddCircle(middle, HandleRadius * scale, new Rgba(0f, 0f, 0f, 0.8f), 16, 1.5f * scale);
                if (_grab == Grab.Crossover && _slideTo is not null)
                    PenTool.Label(draw, middle, length(Math.Abs(_slid)), Style.Caution);
            }

            return;
        }

        if (!Editable(lines, turnout))
            return;

        if (turnout.Modelled)
        {
            var (fx, fz, _, _, _, _, _) = turnout.Normal.At(turnout.FrogNormal);
            if (project(fx, fz) is { } frog)
            {
                var r = 4f * scale;
                draw.AddLine(frog + new float2(-r, -r), frog + new float2(r, r), Rgba.White, 2f * scale);
                draw.AddLine(frog + new float2(-r, r), frog + new float2(r, -r), Rgba.White, 2f * scale);
            }
        }

        if (Curve is { } curve)
        {
            var color = curve.Fits ? Style.Caution : Style.Stop;
            var total = Alignment.LengthOf(curve.Pieces);
            var count = Math.Clamp((int)(total / 2.0), 2, 200);
            float2? previous = null;
            for (var k = 0; k <= count; k++)
            {
                var (x, z) = Alignment.Position(curve.Pieces, total * k / count);
                var at = project(x, z);
                if (previous is { } from && at is { } to)
                    draw.AddLine(from, to, color, 3f * scale);
                previous = at;
            }

            var (ex, ez) = Alignment.Position(curve.Pieces, total);
            if (project(ex, ez) is { } end)
            {
                draw.AddCircleFilled(end, HandleRadius * scale, color, 16);
                PenTool.Label(draw, end, curve.Fits ? $"No. {curve.Number}   curve {length(curve.Radius)}" : $"Too sharp: curve {length(curve.Radius)}", color);
            }

            return;
        }

        if (_grab == Grab.Points && _slideTo is { } slid && project(slid.X, slid.Z) is { } moved)
        {
            draw.AddCircleFilled(moved, HandleRadius * scale, Style.Caution, 16);
            PenTool.Label(draw, moved, length(Math.Abs(_slid)), Style.Caution);
            return;
        }

        if (CurveEnd(lines, turnout) is { } handle && project(handle.X, handle.Z) is { } at2)
        {
            draw.AddCircleFilled(at2, HandleRadius * scale, Rgba.White, 16);
            draw.AddCircle(at2, HandleRadius * scale, new Rgba(0f, 0f, 0f, 0.8f), 16, 1.5f * scale);
        }
    }

    /// The screen place of a handle, for a scripted drag.
    public static (double X, double Z)? CurveHandle(NetworkLines lines, Turnout turnout) => CurveEnd(lines, turnout);
}
