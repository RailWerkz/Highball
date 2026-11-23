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

/// The gizmo on the object picked: dragged to move it or turned on its ring, and its place and heading typed in the
/// Inspector's Transform card. While it's dragged the object's left out of the track's meshes and drawn on its own where
/// it's going (NetworkMeshes.ShowDragged), so nothing else is built again till it's let go: what stands free (poles, lights,
/// ties) follows the gizmo exactly; a signal or signal bridge shows where it would snap onto the track. A crossing (built with
/// its road) and a hand throw (built with its switch) move when they're let go. Each drag is one undo.
internal sealed partial class ObjectsTool
{
    private readonly Gizmo _gizmo = new();

    /// The object as it was when the gizmo was taken hold of, for the one undo when it's let go.
    private PlacedObject? _gizmoFrom;

    /// The Transform card's fields while they're typed in.
    private (long Id, double East, double North, double Heading)? _typed;

    /// Whether it stands where it's put, rather than snapping to the track.
    private static bool StandsFree(PlacedKind kind) => kind.IsPole();

    /// Whether it's drawn on its own while it's dragged (the rest move when they're let go).
    private static bool DrawnWhileDragged(PlacedKind kind) => kind is not (PlacedKind.GradeCrossing or PlacedKind.HandThrow);

    /// Where the gizmo has the object now: where it stands free, or snapped onto the track (as it was, if there's no track
    /// there to snap to).
    private PlacedObject Dragged(NetworkLines lines, PlacedObject start)
    {
        if (_gizmo.Held == GizmoHandle.Rotate)
            return start with { Heading = _gizmo.Heading };
        if (StandsFree(start.Kind))
            return MovedTo(start, _gizmo.X, _gizmo.Z, _gizmo.Heading);
        return MovedOnto(lines, start, _gizmo.X, _gizmo.Z) ?? start;
    }

    /// Works the gizmo on the picked object. True while it has the pointer (hovered or held), so a click there doesn't
    /// pick or place anything.
    private bool GizmoUpdate(NetworkLines lines, PlacedObject picked, float2 mouse)
    {
        var (x, z) = PlacedAtWorld(picked);
        var y = context.Source.HeightAt(x, z) + 0.3;
        _gizmo.CanMove = true;
        _gizmo.CanTurn = picked.Kind != PlacedKind.HandThrow;
        var ctrl = Input.Key(GlfwKey.LeftControl) || Input.Key(GlfwKey.RightControl);
        var state = _gizmo.Update(context.Project, x, y, z, picked.Heading, mouse, context.Hit, Input.MouseDown(GlfwMouseButton.Left),
            Input.Mouse(GlfwMouseButton.Left), context.Blocked, ctrl);

        switch (state)
        {
            case GizmoState.Started:
                _gizmoFrom = picked;
                if (DrawnWhileDragged(picked.Kind))
                {
                    _network?.HideObject(picked.Id, x, z);
                    _network?.ShowDragged(picked);
                }

                break;
            case GizmoState.Dragging when _gizmoFrom is { } start && DrawnWhileDragged(start.Kind):
                _network?.ShowDragged(Dragged(lines, start));
                break;
            case GizmoState.Released when _gizmoFrom is { } start:
            {
                _gizmoFrom = null;
                var after = start.Kind == PlacedKind.GradeCrossing || start.Kind == PlacedKind.HandThrow
                    ? _gizmo.Held == GizmoHandle.Rotate ? start with { Heading = _gizmo.Heading } : MovedOnto(lines, start, _gizmo.X, _gizmo.Z) ?? start
                    : Dragged(lines, start);
                EndDrag(start);
                if (after != start)
                    PlacedChange(start, after);
                break;
            }
        }

        return state is GizmoState.Hovered or GizmoState.Started or GizmoState.Dragging or GizmoState.Released;
    }

    /// Stops drawing the dragged object on its own and puts it back in the meshes.
    private void EndDrag(PlacedObject start)
    {
        var (fx, fz) = PlacedAtWorld(start);
        _network?.ShowDragged(null);
        _network?.HideObject(null, fx, fz);
    }

    /// The object moved to a place in the world and turned to heading, everything else about it kept.
    private PlacedObject MovedTo(PlacedObject o, double x, double z, double heading)
    {
        var geo = _route.Projection!.Inverse(new RoutePoint(x, -z));
        return o with { Lat = geo.Lat, Lon = geo.Lon, Heading = heading };
    }

    private void GizmoDraw(ImDrawListPtr draw)
    {
        if (_place.Mode != PlaceMode.EditBridge && _place.Selected is { } id && context.Source.Objects.Any(o => o.Id == id))
            _gizmo.Draw(draw);
    }

    /// Lets go of the gizmo, putting back what was being dragged. True if there was a drag to let go of.
    private bool GizmoEscape()
    {
        if (_gizmoFrom is not { } start)
            return false;
        EndDrag(start);
        _gizmoFrom = null;
        _gizmo.Cancel();
        return true;
    }

    /// The Inspector's Transform card: where the picked object is (metres east and north of the route's origin), how it's
    /// turned, and how the gizmo handles.
    private void TransformPanel(PlacedObject picked)
    {
        Kit.BeginCard("Transform");
        var (x, z) = PlacedAtWorld(picked);
        if (_typed is not { } typed || typed.Id != picked.Id)
            typed = (picked.Id, x, -z, picked.Heading);
        if (_gizmoFrom is not null)
            typed = (picked.Id, _gizmo.X, -_gizmo.Z, _gizmo.Heading);

        var free = StandsFree(picked.Kind);
        var east = typed.East;
        var north = typed.North;
        var heading = typed.Heading;
        ImGui.PushItemWidth(Kit.Width - S(90f));
        var entered = ImGui.InputDouble("East (m)##xf.east"u8, ref east, 0.0, 0.0, "%.2f", ImGuiInputTextFlags.EnterReturnsTrue);
        entered |= ImGui.InputDouble("North (m)##xf.north"u8, ref north, 0.0, 0.0, "%.2f", ImGuiInputTextFlags.EnterReturnsTrue);
        var turnedIn = picked.Kind != PlacedKind.HandThrow && ImGui.InputDouble("Heading (°)##xf.heading"u8, ref heading, 0.0, 0.0, "%.1f", ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopItemWidth();
        _typed = (picked.Id, east, north, heading);
        if (entered || turnedIn)
        {
            var moved = Math.Abs(east - x) > 1e-4 || Math.Abs(-north - z) > 1e-4;
            if (moved && !free && _tracks is not null && context.Source.Lines is { } lines)
                PlaceMove(lines, picked with { Heading = heading }, east, -north);
            else
                PlacedChange(picked, MovedTo(picked, east, -north, heading));
            _typed = null;
        }

        if (!free)
            Kit.Note("Snaps to the track where it's moved to.");

        Kit.Separator();
        Kit.Toggle("xf.local", "Local axes", ref Gizmo.Local);
        Kit.Toggle("xf.snapmove", "Snap moves", ref Gizmo.SnapMove);
        if (Gizmo.SnapMove)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            var factor = imperial ? FeetPerMeter : 1f;
            var step = Gizmo.MoveStep * factor;
            if (ToolPanel.Value("xf.movestep", imperial ? "Grid (ft)" : "Grid (m)", ref step, 0.1f * factor, 50f * factor, "0.0", log: true))
                Gizmo.MoveStep = step / factor;
        }

        Kit.Toggle("xf.snapturn", "Snap turns", ref Gizmo.SnapTurn);
        if (Gizmo.SnapTurn)
            ToolPanel.Value("xf.turnstep", "Step (°)", ref Gizmo.TurnStep, 1f, 90f, "0");
        Kit.Note("Ctrl while dragging swaps snapping.");
        Kit.EndCard();
    }
}
