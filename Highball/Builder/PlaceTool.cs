using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.Routes;
using Highball.World;
using RailWerkz.UI;

namespace Highball.Builder;

public enum PlaceMode { Signal, HandThrow, GradeCrossing, Diamond, Track, Bridge, RemoveBridge }

/// Placing what stands on or beside the track. A signal snaps beside the nearest track, on the side clicked, facing trains
/// running on that side. A hand throw goes on the nearest switch in place of its power machine. A grade crossing snaps onto the
/// nearest track and takes its road's line and width, if one crosses there. A diamond joins two tracks where they cross.
/// A track piece is laid as real track from the palette (TrackStamps), its switches sized as it says.
public sealed class PlaceTool
{
    /// How far off a track a click may be and still go on it, and how far off the track's centre a signal stands.
    public const double SnapReach = 15.0;
    public const double SignalOff = 3.6;
    public const double SwitchReach = 20.0;
    public const float DefaultCrossingWidth = 8f;
    public const float MinCrossingWidth = 3f;
    public const float MaxCrossingWidth = 40f;
    private const double RoadReach = 15.0;
    private const float GrabRadius = 12f;

    public PlaceMode Mode;

    /// The object picked, and whether it's in hand.
    public long? Selected;

    /// The palette of track pieces, the one in hand, the track spacing its double-track pieces are built to, and how it's
    /// turned where it isn't lined up with a track.
    public List<TrackStamp> Catalogue { get; private set; } = [];
    public TrackStamp? Stamp;
    public float Spacing = 4.6f;
    public double Heading;
    public bool Mirror;

    /// What the piece in hand would lay at the pointer, for the marker and the click.
    public StampPlan? Plan { get; private set; }

    private (float Spacing, bool Imperial)? _built;

    /// Builds the palette afresh if the spacing or units changed; keeps the piece in hand.
    public void Rebuild(bool imperial)
    {
        if (_built == (Spacing, imperial))
            return;
        _built = (Spacing, imperial);
        var held = Stamp?.Id;
        Catalogue = TrackStamps.All(Spacing, imperial);
        Stamp = Catalogue.FirstOrDefault(s => s.Id == held);
    }

    /// Turns the piece by this many degrees. On a track it lies along the track, whichever way along it is nearer this.
    public void Turn(double degrees) => Heading += degrees * Math.PI / 180.0;

    /// Where the pointer is, snapped; and what to say about it.
    public (double X, double Z)? Snapped { get; private set; }
    public string? Note { get; private set; }

    public static (NetworkLine Line, float S, double Away)? NearestRail(NetworkLines lines, double x, double z, double within)
    {
        (NetworkLine, float, double)? found = null;
        var best = within * within;
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Disused || x < line.MinX - within || x > line.MaxX + within || z < line.MinZ - within || z > line.MaxZ + within)
                continue;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var (dx, dz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
                var t = Math.Clamp(((x - line.X[i]) * dx + (z - line.Z[i]) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (line.X[i] + dx * t - x, line.Z[i] + dz * t - z);
                var away = ex * ex + ez * ez;
                if (away < best)
                {
                    best = away;
                    found = (line, line.S[i] + (line.S[i + 1] - line.S[i]) * (float)t, Math.Sqrt(away));
                }
            }
        }

        return found;
    }

    private static (double X, double Z, double Rx, double Rz) Place(NetworkLine line, float s)
    {
        var (x, z, _, _, _, rx, rz) = new Leg(line, 0, 1).At(s);
        return (x, z, rx, rz);
    }

    /// A signal at a click: beside the track on that side, facing trains coming along it with it on their right.
    public static (double X, double Z, double Heading)? SignalAt(NetworkLines lines, double x, double z)
    {
        if (NearestRail(lines, x, z, SnapReach) is not { } on)
            return null;
        var (px, pz, rx, rz) = Place(on.Line, on.S);
        var side = (x - px) * rx + (z - pz) * rz >= 0.0 ? 1.0 : -1.0;

        // On the track's right (+1) the trains it faces run with the line's direction; it faces back along it.
        var (fx, fz) = (rz, -rx);
        var heading = Math.Atan2(-fz * side, -fx * side) * 180.0 / Math.PI;
        return (px + rx * side * SignalOff, pz + rz * side * SignalOff, heading);
    }

    /// A grade crossing at a click: on the track, along the road that crosses it there if there is one.
    public static (double X, double Z, double Heading, float Width, bool OnRoad)? CrossingAt(NetworkLines lines, double x, double z)
    {
        if (NearestRail(lines, x, z, SnapReach) is not { } on)
            return null;
        var (px, pz, rx, rz) = Place(on.Line, on.S);
        var heading = Math.Atan2(rz, rx) * 180.0 / Math.PI;
        var width = DefaultCrossingWidth;
        var onRoad = false;
        var best = double.MaxValue;
        foreach (var road in lines.Lines)
        {
            if (road.Rail || px < road.MinX - RoadReach || px > road.MaxX + RoadReach || pz < road.MinZ - RoadReach || pz > road.MaxZ + RoadReach)
                continue;
            for (var i = 0; i + 1 < road.Count; i++)
            {
                var (dx, dz) = (road.X[i + 1] - road.X[i], road.Z[i + 1] - road.Z[i]);
                var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-9);
                var t = Math.Clamp(((px - road.X[i]) * dx + (pz - road.Z[i]) * dz) / (length * length), 0.0, 1.0);
                var (ex, ez) = (road.X[i] + dx * t - px, road.Z[i] + dz * t - pz);
                var away = Math.Sqrt(ex * ex + ez * ez);
                if (away < RoadReach && away < best && Math.Abs(dx / length * rz - dz / length * rx) < 0.97)
                {
                    best = away;
                    heading = Math.Atan2(dz, dx) * 180.0 / Math.PI;
                    width = Math.Clamp(road.Width, MinCrossingWidth, MaxCrossingWidth);
                    onRoad = true;
                }
            }
        }

        return (px, pz, heading, width, onRoad);
    }

    public static Turnout? SwitchAt(NetworkLines lines, double x, double z) =>
        lines.Turnouts.Where(t => (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z) < SwitchReach * SwitchReach).MinBy(t => (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z));

    /// The object under the pointer.
    public static PlacedObject? At(IReadOnlyList<PlacedObject> objects, Func<PlacedObject, float2?> screen, float2 mouse, float scale)
    {
        PlacedObject? found = null;
        var best = GrabRadius * scale;
        foreach (var o in objects)
        {
            if (screen(o) is not { } at)
                continue;
            var d = MathF.Sqrt((at.X - mouse.X) * (at.X - mouse.X) + (at.Y - mouse.Y) * (at.Y - mouse.Y));
            if (d < best)
                (best, found) = (d, o);
        }

        return found;
    }

    /// Works out what a click at the pointer would do, for the panel and the marker.
    public void Update(NetworkLines lines, TrackEditor tracks, (double X, double Z)? ground, Func<double, string> length)
    {
        (Snapped, Note, Plan) = (null, null, null);
        if (ground is not { } at)
            return;
        if (Mode == PlaceMode.Track)
        {
            if (Stamp is { } stamp)
                Plan = TrackStamps.Plan(stamp, tracks, at.X, at.Z, Heading, Mirror, length);
            if (Plan is { } plan)
                (Snapped, Note) = ((plan.AtX, plan.AtZ), plan.Problem ?? plan.Summary);
            return;
        }

        switch (Mode)
        {
            case PlaceMode.Signal when SignalAt(lines, at.X, at.Z) is { } signal:
                Snapped = (signal.X, signal.Z);
                break;
            case PlaceMode.HandThrow when SwitchAt(lines, at.X, at.Z) is { } turnout:
                Snapped = (turnout.X, turnout.Z);
                Note = turnout.Number is { } number ? $"Switch, No. {number}" : "Switch";
                break;
            case PlaceMode.GradeCrossing when CrossingAt(lines, at.X, at.Z) is { } crossing:
                Snapped = (crossing.X, crossing.Z);
                Note = crossing.OnRoad ? $"Road {length(crossing.Width)} wide" : "No road here";
                break;
            case PlaceMode.Diamond when tracks.CrossingNear(at.X, at.Z, SnapReach) is { } diamond:
                Snapped = (diamond.X, diamond.Z);
                Note = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Tracks cross at {diamond.Degrees:0} degrees");
                break;
        }
    }

    public void Draw(ImDrawListPtr draw, Func<double, double, float2?> project, IReadOnlyList<PlacedObject> objects, Func<PlacedObject, (double X, double Z)> place, float scale)
    {
        foreach (var o in objects)
        {
            var (x, z) = place(o);
            if (project(x, z) is not { } at)
                continue;
            var r = (o.Id == Selected ? 7f : 5f) * scale;
            draw.AddCircleFilled(at, r, o.Id == Selected ? Rgba.White : Style.Accent, 12);
            draw.AddCircle(at, r, new Rgba(0f, 0f, 0f, 0.8f), 12, 1.5f * scale);
        }

        var color = Plan?.Problem is null ? Style.Caution : Style.Stop;
        if (Plan is { } plan)
            foreach (var way in plan.Ways)
            {
                float2? previous = null;
                foreach (var point in way.Points)
                {
                    var here = project(point.X, point.Z);
                    if (previous is { } a && here is { } b)
                        draw.AddLine(a, b, color, 3f * scale);
                    previous = here;
                }
            }

        if (Snapped is { } snapped && project(snapped.X, snapped.Z) is { } marker)
        {
            draw.AddCircle(marker, 7f * scale, color, 16, 2f * scale);
            if (Note is { } note)
                PenTool.Label(draw, marker, note, color);
        }
    }
}
