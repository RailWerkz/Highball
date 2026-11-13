using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Parallel tool: a first click on a track, away from its points, runs beside that track as far as the second click.
/// Anything else draws as Pen does (or, following, clicks along the guides as Lay Yard does): the path is the first track
/// and the rest go beside it, F flips the side, and Enter lays them all.
public sealed class ParallelTool(ToolContext context, RouteInfo route) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    /// Where the drawn path would run on to if clicked now.
    private (double X, double Z)? _preview;

    /// How many tracks, how far apart, and which side; beside a track once one's picked.
    public YardTool Yard { get; } = YardTool.ForParallel();

    /// The path drawn, as with Pen.
    public PenTool Pen { get; } = new();

    /// Started on a guide: its first track follows the guide as Lay Yard does, the others beside it.
    public FollowTool Follow { get; } = new();
    public bool Following { get; set; }

    public void Update(float dt)
    {
        if (Yard.Anchor is not null)
        {
            YardLaying.Update(context, route, Yard);
            return;
        }

        var tracks = context.Tracks;
        _preview = null;
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.F) && !ImGui.IsKeyDown(ImGuiKey.LeftShift))
            Yard.Side = -Yard.Side;
        if (context.Blocked || context.Rebuilding)
        {
            Yard.Show(null, null);
            return;
        }

        var camera = context.Camera;
        var mouse = Input.MousePosition;
        var hit = context.Hit;
        var node = tracks.NodeAt(mouse, camera.X, camera.Z, context.ProjectGround);
        if (!Following && Pen.Count == 0 && node is null && Input.MouseDown(GlfwMouseButton.Left) && hit is { } first &&
            tracks.SegmentAt(mouse, camera.X, camera.Z, context.ProjectGround) is not null)
        {
            Yard.Start(tracks, first.X, first.Z);
            return;
        }

        // Following: clicked along the guides (from a track's end, or off a track) as Lay Yard's Follow is.
        if (Following)
        {
            AlongGuide(tracks, YardLaying.ClickAt(context, context.Guides!, mouse));
            return;
        }

        LayPoint? snap = node is { } on
            ? new LayPoint(tracks.PositionOf(on).X, tracks.PositionOf(on).Z, Node: on)
            : hit is { } ground ? new LayPoint(ground.X, ground.Z) : null;
        Pen.Update(mouse, snap, hit is { } h ? (h.X, h.Z) : null, context.ProjectGround,
            Input.MouseDown(GlfwMouseButton.Left), Input.Mouse(GlfwMouseButton.Left));
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Backspace))
            Pen.RemoveLast();
        _preview = !Pen.Busy && snap is { } next ? (next.X, next.Z) : null;

        var drawn = Pen.Points(_preview);
        Yard.Show(drawn.Count >= 2 ? Yard.PenParallel(tracks, drawn, YardLaying.Length) : null, hit is { } p ? (p.X, p.Z) : null);
        if (!Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.Enter) && Pen.Count >= 2 &&
            Yard.PenParallel(tracks, Pen.Points(), YardLaying.Length, laying: true) is { Problem: null } plan && tracks.LayWays(plan.Ways) is { } edit)
        {
            Pen.Clear();
            Yard.Show(null, null);
            context.Commit(edit);
        }
    }

    /// Along a guide: the clicks as Follow takes them, the first track the fit through them and the rest beside it.
    public void AlongGuide(TrackEditor tracks, FollowTool.Click? click)
    {
        var guides = context.Guides!;
        Follow.Update(guides, click);
        Yard.Show(Follow.Preview is { } preview ? Yard.FollowParallel(tracks, preview, null, YardLaying.Length) : null, click is { } at ? (at.X, at.Z) : null);
        if (Input.MouseDown(GlfwMouseButton.Left) && click is { } placed)
            Follow.Add(placed);
        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Backspace))
            Follow.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter))
        {
            Follow.Update(guides, null);
            if (Follow.Preview is { } pieces && Follow.Points() is { } first &&
                Yard.FollowParallel(tracks, pieces, first, YardLaying.Length) is { Problem: null } plan && tracks.LayWays(plan.Ways) is { } edit)
            {
                Follow.Clear();
                Yard.Show(null, null);
                context.Commit(edit);
            }
        }
    }

    public void Draw(ImDrawListPtr draw)
    {
        Pen.Draw(draw, context.ProjectGround, _preview);
        Yard.Draw(draw, context.ProjectGround, S(1f));
    }

    /// How many tracks to lay, and how far apart: beside a track, on whichever side of it the pointer is, or on open ground.
    public void Panel(bool imperial)
    {
        Kit.BeginCard("Tracks");
        var mode = ToolPanel.Mode("parallelmode", ["Draw", "Follow"], Following ? 1 : 0, 2);
        if (mode == 1 != Following)
        {
            Following = mode == 1;
            Clear();
        }

        var tracks = Yard.ParallelTracks;
        if (ToolPanel.Count("paralleltracks", "Tracks", ref tracks, 1, YardTool.MaxParallel))
            Yard.ParallelTracks = tracks;
        var factor = imperial ? FeetPerMeter : 1f;
        var spacing = Yard.Spacing * factor;
        if (ToolPanel.Value("parallelspacing", imperial ? "Spacing (ft)" : "Spacing (m)", ref spacing, YardTool.MinSpacing * factor, YardTool.MaxSpacing * factor, "0.0"))
            Yard.Spacing = Math.Clamp(spacing / factor, YardTool.MinSpacing, YardTool.MaxSpacing);

        if (Yard.Plan is not { } plan)
            ToolPanel.Hint(Yard.Anchor is not null ? "Click where they end." : Pen.Count > 0 || Follow.Count > 0 ? "Enter lays them."
                : Following ? "Click along a guide." : "Click a track, or draw.");
        else if (plan.Problem is { } problem)
            ToolPanel.Problem(problem);
        else
            ToolPanel.Line(plan.Summary);
        Kit.EndCard();
    }

    private void Clear()
    {
        Pen.Clear();
        Follow.Clear();
        Yard.Clear();
    }

    public bool Escape()
    {
        if (Yard.Anchor is null && Pen.Count == 0 && Follow.Count == 0)
            return false;
        Clear();
        return true;
    }

    public (string Key, string Action)[] Hints => Yard.Anchor is not null
        ? [("LMB", "End"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")]
        : Following ? [("LMB", "Along a guide"), ("ENTER", "Lay tracks"), ("F", Yard.Side > 0 ? "Others right" : "Others left"), ("BACKSPACE", "Remove click"), ("ESC", "Cancel")]
        : [("LMB", "Point or track"), ("DRAG", "Curve"), ("ENTER", "Lay tracks"), ("F", Yard.Side > 0 ? "Others right" : "Others left"), ("BACKSPACE", "Remove point"), ("ESC", "Cancel")];
}
