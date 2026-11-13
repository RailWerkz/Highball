using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// A tool in the editor's left hand: worked each frame it's in hand, drawn over the world, and given the side panel.
public interface IEditorTool
{
    /// Each frame it's in hand, after the camera's moved.
    void Update(float dt);

    /// Over the world, while it's in hand.
    void Draw(ImDrawListPtr draw);

    /// Its cards in the side panel.
    void Panel(bool imperial);

    /// Esc: lets go of whatever's under way. False when there was nothing, so Esc means something else.
    bool Escape();

    /// The footer's keys for it.
    (string Key, string Action)[] Hints { get; }
}

/// What the editor gives its tools (ToolContext wraps it).
public interface IToolHost
{
    RouteWorld World { get; }
    CameraView View { get; }
    bool Blocked { get; }
    double3? Hit { get; }
    bool TopDown { get; }
    double Reach { get; }
    bool Rebuilding { get; }
    string? Refused { get; set; }
    TrackEditor Tracks { get; }
    RoadEditor Roads { get; }
    void Record(object? edit);
    void Commit(NetworkEdit? edit);
    void RebuildNetwork();
}

/// What the editor gives its tools: the route as loaded, the camera on the screen, the ground under the pointer, the
/// track and road editors, and the undo history.
public sealed class ToolContext(IToolHost host)
{
    public TerrainSource Source => host.World.Source!;
    public Terrain Terrain => host.World.Terrain!;
    public CameraView View => host.View;
    public Camera Camera => host.View.Camera;

    /// The pointer's over a window, the menu bar or the footer, not the world.
    public bool Blocked => host.Blocked;

    /// The ground under the pointer, with a tool in hand.
    public double3? Hit => host.Hit;

    /// Seen straight down (the 2D Editor).
    public bool TopDown => host.TopDown;

    /// How far round the camera there's anything to draw, metres.
    public double Reach => host.Reach;

    /// The track's being laid out again in the background after an edit.
    public bool Rebuilding => host.Rebuilding;

    /// Why the last thing tried was refused, shown in the panel; null when it wasn't.
    public string? Refused { get => host.Refused; set => host.Refused = value; }

    public TrackEditor Tracks => host.Tracks;
    public RoadEditor Roads => host.Roads;

    /// Keeps an edit (of any kind) for undoing.
    public void Record(object? edit) => host.Record(edit);

    /// A network edit made: diamonds put in where it crosses track, kept for undoing, and the track laid out again.
    public void Commit(NetworkEdit? edit) => host.Commit(edit);

    /// The track laid out again from the network as it is (after an edit that isn't a NetworkEdit).
    public void RebuildNetwork() => host.RebuildNetwork();

    /// Where a point in the world is on the screen.
    public float2? Project(double x, double y, double z) => host.View.Project(x, y, z);

    /// Where a point on the ground (a little over it) is on the screen.
    public float2? ProjectGround(double x, double z) => host.View.Project(x, Source.HeightAt(x, z) + 0.6, z);

    /// How many metres of ground a few pixels cover, at a place on it.
    public double PickReach(double x, double z)
    {
        var camera = Camera;
        var (dx, dy, dz) = (x - camera.X, Source.HeightAt(x, z) - camera.Y, z - camera.Z);
        return Math.Max(3.0, Math.Sqrt(dx * dx + dy * dy + dz * dz) * 2.0 * Math.Tan(camera.FieldOfView * 0.5) / Math.Max(View.Size.Y, 1f) * S(14f));
    }

    /// A line over the view, to show what a click would act on.
    public void DrawPicked(ImDrawListPtr draw, double[] x, double[] z, Rgba color)
    {
        for (var i = 0; i + 1 < x.Length; i++)
            if (ProjectGround(x[i], z[i]) is { } a && ProjectGround(x[i + 1], z[i + 1]) is { } b)
                draw.AddLine(a, b, color, S(3f));
    }

    /// The ground in a rectangle of grid cells changed: its patches built again.
    public void Invalidate((int X0, int Z0, int X1, int Z1)? rect)
    {
        if (rect is { } r)
            Terrain.Invalidate(r.X0, r.Z0, r.X1, r.Z1);
    }
}
