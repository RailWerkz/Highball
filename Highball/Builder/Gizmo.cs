using System.Globalization;
using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The parts of a gizmo the pointer can take hold of.
public enum GizmoHandle { None, MoveX, MoveZ, MovePlane, Rotate }

/// What a gizmo did this frame.
public enum GizmoState { Idle, Hovered, Started, Dragging, Released }

/// A move and turn gizmo for something standing on the ground (a placed object): two arrows along the ground to slide it
/// along one way, a square at its foot to drag it anywhere, and a ring round it to turn it. Everything's worked out against
/// the ground under the pointer, so it handles the same from above (2D) as in flight (3D). The arrows lie along the world's
/// axes, or along the object's own (Local). Snapping (on as set, Ctrl swaps it) puts it on a grid and turns it in steps.
/// Heading: degrees from +X towards +Z, as placed objects have it.
public sealed class Gizmo
{
    /// The arrows along the object's own heading rather than the world's axes.
    public static bool Local = true;
    public static bool SnapMove;
    public static float MoveStep = 1f;
    public static bool SnapTurn = true;
    public static float TurnStep = 15f;

    /// How long the arrows look on the screen, pixels, and how near the pointer has to be to take hold of something.
    private const float ArrowPixels = 110f;
    private const float Grip = 8f;
    private const int RingSegments = 64;

    private static readonly Rgba AxisX = Rgba.Hex(0xE5484D);
    private static readonly Rgba AxisZ = Rgba.Hex(0x3E8BFF);
    private static readonly Rgba RingColor = Rgba.Hex(0xF5C04A);

    public GizmoHandle Hovered { get; private set; }
    public GizmoHandle Active { get; private set; }

    /// The handle held, or let go of this frame.
    public GizmoHandle Held { get; private set; }

    /// Where it is and how it's turned while it's dragged (and on the frame it's let go).
    public double X { get; private set; }
    public double Z { get; private set; }
    public double Heading { get; private set; }

    /// Whether it may be moved and turned (a hand throw can't be turned; it sits on its switch).
    public bool CanMove { get; set; } = true;
    public bool CanTurn { get; set; } = true;

    // This frame's layout on the screen.
    private bool _shown;
    private float2 _centre, _endX, _endZ;
    private readonly float2[] _ring = new float2[RingSegments + 1];
    private readonly bool[] _ringSeen = new bool[RingSegments + 1];

    // What it was when it was taken hold of.
    private double _fromX, _fromZ, _fromHeading, _grabX, _grabZ, _grabAngle;
    private (double X, double Z) _ux, _uz;

    /// Lays it out round (x, y, z) turned heading, works the pointer, and moves it while it's held. pressed: the left button
    /// went down this frame; held: it's down. hit: the ground under the pointer, if any.
    public GizmoState Update(Func<double, double, double, float2?> project, double x, double y, double z, double heading, float2 mouse,
        double3? hit, bool pressed, bool held, bool blocked, bool ctrl)
    {
        if (Active == GizmoHandle.None)
            (X, Z, Heading) = (x, z, heading);
        // It follows what it moves; its arrows keep the heading they had when it was taken hold of.
        Layout(project, X, y, Z, Active == GizmoHandle.None ? heading : _fromHeading);
        if (!_shown)
        {
            Hovered = GizmoHandle.None;
            if (Active != GizmoHandle.None && !held)
            {
                Active = GizmoHandle.None;
                return GizmoState.Released;
            }

            return Active != GizmoHandle.None ? GizmoState.Dragging : GizmoState.Idle;
        }

        if (Active == GizmoHandle.None)
        {
            Hovered = blocked ? GizmoHandle.None : HandleAt(mouse);
            if (Hovered == GizmoHandle.None)
                return GizmoState.Idle;
            if (!pressed || hit is not { } grab)
                return GizmoState.Hovered;
            Active = Held = Hovered;
            (_fromX, _fromZ, _fromHeading) = (x, z, heading);
            (_grabX, _grabZ) = (grab.X, grab.Z);
            _grabAngle = AngleOf(grab.X, grab.Z);
            return GizmoState.Started;
        }

        if (!held)
        {
            Active = GizmoHandle.None;
            return GizmoState.Released;
        }

        if (hit is { } at)
            Drag(at.X, at.Z, SnapMove != ctrl, SnapTurn != ctrl);
        return GizmoState.Dragging;
    }

    /// Lets go without a release (Esc, or the object went away).
    public void Cancel() => (Active, Hovered) = (GizmoHandle.None, GizmoHandle.None);

    private double AngleOf(double x, double z) => Math.Atan2(z - _fromZ, x - _fromX) * 180.0 / Math.PI;

    private void Drag(double hx, double hz, bool snapMove, bool snapTurn)
    {
        var (dx, dz) = (hx - _grabX, hz - _grabZ);
        switch (Active)
        {
            case GizmoHandle.MoveX or GizmoHandle.MoveZ:
            {
                var u = Active == GizmoHandle.MoveX ? _ux : _uz;
                var along = dx * u.X + dz * u.Z;
                if (snapMove && MoveStep > 0f)
                    along = Math.Round(along / MoveStep) * MoveStep;
                (X, Z) = (_fromX + u.X * along, _fromZ + u.Z * along);
                break;
            }
            case GizmoHandle.MovePlane:
            {
                var (nx, nz) = (_fromX + dx, _fromZ + dz);
                if (snapMove && MoveStep > 0f)
                    (nx, nz) = (Math.Round(nx / MoveStep) * MoveStep, Math.Round(nz / MoveStep) * MoveStep);
                (X, Z) = (nx, nz);
                break;
            }
            case GizmoHandle.Rotate:
            {
                var turned = _fromHeading + Wrap(AngleOf(hx, hz) - _grabAngle);
                if (snapTurn && TurnStep > 0f)
                    turned = Math.Round(turned / TurnStep) * TurnStep;
                Heading = turned;
                break;
            }
        }
    }

    private static double Wrap(double degrees) => degrees - 360.0 * Math.Floor((degrees + 180.0) / 360.0);

    /// Where everything is on the screen, round where it is now.
    private void Layout(Func<double, double, double, float2?> project, double x, double y, double z, double heading)
    {
        _shown = false;
        if (project(x, y, z) is not { } centre)
            return;
        var angle = heading * Math.PI / 180.0;
        _ux = Local ? (Math.Cos(angle), Math.Sin(angle)) : (1.0, 0.0);
        _uz = Local ? (-Math.Sin(angle), Math.Cos(angle)) : (0.0, 1.0);

        // A metre's length on the screen, the longer of the two ways along the ground, sizes it to look the same at any range.
        var px = project(x + _ux.X, y, z + _ux.Z) is { } a ? Length(a - centre) : 0f;
        var pz = project(x + _uz.X, y, z + _uz.Z) is { } b ? Length(b - centre) : 0f;
        var perMetre = MathF.Max(px, pz);
        if (perMetre < 1e-4f)
            return;
        var length = Math.Clamp(S(ArrowPixels) / perMetre, 0.25, 2000.0);
        if (project(x + _ux.X * length, y, z + _ux.Z * length) is not { } endX || project(x + _uz.X * length, y, z + _uz.Z * length) is not { } endZ)
            return;
        (_centre, _endX, _endZ) = (centre, endX, endZ);

        var radius = length * 1.35;
        for (var i = 0; i <= RingSegments; i++)
        {
            var t = i * Math.PI * 2.0 / RingSegments;
            var p = project(x + Math.Cos(t) * radius, y, z + Math.Sin(t) * radius);
            _ringSeen[i] = p is not null;
            _ring[i] = p ?? default;
        }

        _shown = true;
    }

    private static float Length(float2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float ToSegment(float2 p, float2 a, float2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / MathF.Max(1e-6f, ab.X * ab.X + ab.Y * ab.Y), 0f, 1f);
        return Length(p - (a + ab * t));
    }

    private GizmoHandle HandleAt(float2 mouse)
    {
        if (CanMove && Length(mouse - _centre) < S(11f))
            return GizmoHandle.MovePlane;
        var grip = S(Grip);
        if (CanMove)
        {
            var dx = ToSegment(mouse, _centre, _endX);
            var dz = ToSegment(mouse, _centre, _endZ);
            if (MathF.Min(dx, dz) < grip)
                return dx <= dz ? GizmoHandle.MoveX : GizmoHandle.MoveZ;
        }

        if (CanTurn)
            for (var i = 0; i < RingSegments; i++)
                if (_ringSeen[i] && _ringSeen[i + 1] && ToSegment(mouse, _ring[i], _ring[i + 1]) < grip)
                    return GizmoHandle.Rotate;
        return GizmoHandle.None;
    }

    public void Draw(ImDrawListPtr draw)
    {
        if (!_shown)
            return;
        var thick = S(2.5f);
        Rgba Ink(GizmoHandle handle, Rgba color) =>
            Active == handle ? Rgba.White : Active != GizmoHandle.None ? color.WithAlpha(0.35f) : Hovered == handle ? Rgba.Mix(color, Rgba.White, 0.45f) : color;

        if (CanTurn)
        {
            var ink = Ink(GizmoHandle.Rotate, RingColor);
            for (var i = 0; i < RingSegments; i++)
                if (_ringSeen[i] && _ringSeen[i + 1])
                    draw.AddLine(_ring[i], _ring[i + 1], ink, Hovered == GizmoHandle.Rotate || Active == GizmoHandle.Rotate ? thick * 1.4f : thick);
            if (Active == GizmoHandle.Rotate)
            {
                // The heading it started at and the one it's at now, from the middle.
                draw.AddLine(_centre, _centre + Direction(_fromHeading) * S(ArrowPixels) * 1.35f, ink.WithAlpha(0.4f), S(1.5f));
                draw.AddLine(_centre, _centre + Direction(Heading) * S(ArrowPixels) * 1.35f, ink, S(1.5f));
            }
        }

        if (CanMove)
        {
            Arrow(draw, _centre, _endX, Ink(GizmoHandle.MoveX, AxisX), thick);
            Arrow(draw, _centre, _endZ, Ink(GizmoHandle.MoveZ, AxisZ), thick);
            var square = S(7f);
            var plane = Ink(GizmoHandle.MovePlane, Rgba.White);
            draw.AddRectFilled(_centre - new float2(square, square), _centre + new float2(square, square), plane.WithAlpha(Hovered == GizmoHandle.MovePlane || Active == GizmoHandle.MovePlane ? 0.55f : 0.25f), S(2f));
            draw.AddRect(_centre - new float2(square, square), _centre + new float2(square, square), plane, S(2f), ImDrawFlags.None, S(1.5f));
        }

        // While it's dragged: how far it's gone, or how far it's turned.
        if (Active != GizmoHandle.None)
        {
            var text = Active == GizmoHandle.Rotate
                ? string.Create(CultureInfo.InvariantCulture, $"{Wrap(Heading - _fromHeading):+0.0;-0.0;0.0}°  ({Wrap(Heading):0.0}°)")
                : UnitText.Length(Math.Sqrt((X - _fromX) * (X - _fromX) + (Z - _fromZ) * (Z - _fromZ)));
            var at = _centre + new float2(S(16f), S(14f));
            var size = Canvas.TextSize(Mono, S(13f), text);
            draw.AddRectFilled(at - new float2(S(5f), S(3f)), at + size + new float2(S(5f), S(3f)), Style.Background.WithAlpha(0.85f), S(3f));
            Canvas.Text(draw, Mono, S(13f), at, Style.Text, text);
        }
    }

    /// The way a heading points on the screen, from the arrows as laid out (so it follows the view).
    private float2 Direction(double heading)
    {
        // The arrows are the object's axes (or the world's): a heading is a mix of them, by how far it's turned from the first.
        var baseHeading = Local ? _fromHeading : 0.0;
        var t = (heading - baseHeading) * Math.PI / 180.0;
        var v = (_endX - _centre) * (float)Math.Cos(t) + (_endZ - _centre) * (float)Math.Sin(t);
        var length = Length(v);
        return length < 1e-4f ? new float2(1f, 0f) : v / length;
    }

    private static void Arrow(ImDrawListPtr draw, float2 from, float2 to, Rgba color, float thick)
    {
        var d = to - from;
        var length = Length(d);
        if (length < 1f)
            return;
        var u = d / length;
        var n = new float2(-u.Y, u.X);
        var head = S(12f);
        var start = from + u * S(11f);
        draw.AddLine(start, to - u * head * 0.8f, color, thick);
        draw.AddTriangleFilled(to, to - u * head + n * head * 0.4f, to - u * head - n * head * 0.4f, color);
    }
}
