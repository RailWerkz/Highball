using System.Globalization;
using Brutal.GlfwApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Builder;

/// What Lay Yard and Parallel share: a click read against the guides and the built track, and a yard tool's two clicks
/// (the track to start from, then where it ends) laying what it shows.
public static class YardLaying
{
    private const float FeetPerMeter = 3.28084f;

    /// How near a click on a guide has to be to its node to join what's there.
    private const double FollowSnap = 4.0;

    /// How near a click on a guide has to be to built track to join it, and to one of its points to join there.
    private const double FollowOnTrack = 2.5;
    private const double FollowAtPoint = 2.5;

    /// A length for the panels, in the units set.
    public static string Length(double metres) => Settings.Current.Units == UnitSystem.Imperial
        ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0.#} ft")
        : string.Create(CultureInfo.InvariantCulture, $"{metres:0.#} m");

    /// What a click at this place would be: on a guide (right at its node, if that's on built track, it joins there), or
    /// on built track.
    public static FollowTool.Click? Click(YardGuides guides, TrackEditor tracks, GuidePlace? guide, (long Way, int Segment, double X, double Z)? segment)
    {
        if (guide is { } place)
        {
            if (guides.NodeAt(place, FollowSnap) is { } node && tracks.Has(node))
            {
                var (nx, nz) = tracks.PositionOf(node);
                return new FollowTool.Click(nx, nz, place, new LayPoint(nx, nz, Node: node), tracks.RunsAt(node));
            }

            // A guide's place on built track joins it there: a branch off it.
            if (tracks.TrackNear(place.X, place.Z, FollowOnTrack, FollowAtPoint) is { } track)
                return new FollowTool.Click(track.Point.X, track.Point.Z, place, track.Point, track.Runs);

            return new FollowTool.Click(place.X, place.Z, place, new LayPoint(place.X, place.Z));
        }

        if (segment is not { } s)
            return null;
        return tracks.TrackNear(s.X, s.Z, FollowOnTrack, FollowAtPoint) is { } on
            ? new FollowTool.Click(on.Point.X, on.Point.Z, null, on.Point, on.Runs)
            : new FollowTool.Click(s.X, s.Z, null, new LayPoint(s.X, s.Z, Split: (s.Way, s.Segment)));
    }

    /// The click under the pointer, on a guide or built track, or else on the ground; null when the pointer's not on the world.
    public static FollowTool.Click? ClickAt(ToolContext context, YardGuides guides, float2 mouse)
    {
        if (context.Blocked)
            return null;
        var camera = context.Camera;
        return Click(guides, context.Tracks, guides.PlaceAt(mouse, context.ProjectGround, camera.X, camera.Z, context.Reach, RailWerkz.UI.Style.S(12f)),
                   context.Tracks.SegmentAt(mouse, camera.X, camera.Z, context.ProjectGround))
               ?? (context.Hit is { } ground ? new FollowTool.Click(ground.X, ground.Z, null, new LayPoint(ground.X, ground.Z)) : null);
    }

    /// A yard tool (Crossover, Ladder, or Parallel beside a track) following the pointer, and its clicks.
    public static void Update(ToolContext context, RouteInfo route, YardTool yard)
    {
        if (context.Source.Lines is not { } lines)
            return;
        var blocked = context.Blocked;
        yard.Update(context.Tracks, lines, !blocked && context.Hit is { } ground ? (ground.X, ground.Z) : null, Length);
        if (blocked || context.Rebuilding)
            return;
        if (Input.MouseDown(GlfwMouseButton.Left) && context.Hit is { } hit)
            Press(context, route, yard, lines, hit.X, hit.Z);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
            yard.Clear();
    }

    /// A click: the first picks the track to start from, the second lays what's shown if it can be.
    public static void Press(ToolContext context, RouteInfo route, YardTool yard, NetworkLines lines, double x, double z)
    {
        var tracks = context.Tracks;
        if (yard.Anchor is null)
        {
            yard.Start(tracks, x, z);
            return;
        }

        yard.Update(tracks, lines, (x, z), Length);
        if (yard.Plan is not { Problem: null } plan || tracks.LayWays(plan.Ways) is not { } edit)
            return;
        if (yard.Mode == YardMode.Crossover && yard.Size != 0 && edit.Ways.Select(w => w.After).FirstOrDefault(w => w is not null && w.Nodes.Length == plan.Ways[0].Points.Count && w.Tags.ContainsKey(RouteNetwork.ExactTag)) is { } crossover)
            SwitchSizes.Set(route, crossover.Nodes[0], yard.Size);
        yard.Clear();
        context.Commit(edit);
    }
}
