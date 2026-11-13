using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Lay Yard tool: track laid along the mapped guides, click by click (Follow), or a crossover or ladder off a track.
public sealed class LayYardTool(ToolContext context, RouteInfo route) : IEditorTool
{
    private const float FeetPerMeter = 3.28084f;

    /// The clicks along the guides, and the fit through them.
    public FollowTool Follow { get; } = new();

    /// Crossover and Ladder (Follow is its mode for the guides).
    public YardTool Yard { get; } = new();

    public void Update(float dt)
    {
        if (Yard.Mode != YardMode.Follow)
        {
            YardLaying.Update(context, route, Yard);
            return;
        }

        var guides = context.Guides!;
        var click = YardLaying.ClickAt(context, guides, Input.MousePosition);
        Follow.Update(guides, click);
        if (context.Blocked || context.Rebuilding)
            return;
        if (Input.MouseDown(GlfwMouseButton.Left) && click is { } placed)
            Follow.Add(placed);
        if (Keyboard.KeysBlocked)
            return;
        if (Input.KeyDown(GlfwKey.Backspace))
            Follow.RemoveLast();
        if (Input.KeyDown(GlfwKey.Enter))
            Lay();
    }

    /// The track through the clicks laid.
    public void Lay()
    {
        Follow.Update(context.Guides!, null);
        if (Follow.Points() is { } points)
        {
            context.Commit(context.Tracks.Lay(points, lead: false, exact: true));
            Follow.Clear();
        }
    }

    public void Draw(ImDrawListPtr draw)
    {
        Follow.Draw(draw, context.ProjectGround, Input.MousePosition);
        Yard.Draw(draw, context.ProjectGround, S(1f));
    }

    /// The mode and its settings, the track in hand, and how much of the guides is done.
    public void Panel(bool imperial)
    {
        string Far(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter / 5280.0:0.0} mi") : string.Create(CultureInfo.InvariantCulture, $"{metres / 1000.0:0.0} km");
        string Near(double metres) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{metres * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{metres:0} m");

        Controls(imperial);
        if (Follow.Preview is not null)
            ToolPanel.Line($"{Near(Follow.Length)}{(Follow.Unfitted ? ", as mapped: no clean fit" : Follow.Radius < double.MaxValue ? $", tightest curve {Near(Follow.Radius)}" : ", straight")}");

        if (context.Guides is { GuideLength: > 0.0 } guides)
        {
            Kit.BeginCard("Guides", $"{Far(guides.DoneLength)} of {Far(guides.GuideLength)}");
            Kit.Gauge((float)(guides.DoneLength / guides.GuideLength), Accent);
            Kit.EndCard();
        }
    }

    private void Controls(bool imperial)
    {
        var modes = new[] { YardMode.Follow, YardMode.Crossover, YardMode.Ladder };
        var current = Array.IndexOf(modes, Yard.Mode);
        var pick = ToolPanel.Mode("yardmode", ["Follow", "Crossover", "Ladder"], current, 3, !context.Rebuilding);
        if (pick != current && pick >= 0)
        {
            Yard.SetMode(modes[pick]);
            Follow.Clear();
        }

        if (Yard.Mode == YardMode.Follow)
            return;

        Kit.BeginCard(Yard.Mode == YardMode.Ladder ? "Ladder" : "Crossover");
        if (Yard.Mode != YardMode.Crossover)
        {
            var factor = imperial ? FeetPerMeter : 1f;
            var spacing = Yard.Spacing * factor;
            if (ToolPanel.Value("yardspacing", imperial ? "Spacing (ft)" : "Spacing (m)", ref spacing, YardTool.MinSpacing * factor, YardTool.MaxSpacing * factor, "0.0"))
                Yard.Spacing = Math.Clamp(spacing / factor, YardTool.MinSpacing, YardTool.MaxSpacing);
        }

        if (Yard.Mode == YardMode.Ladder)
        {
            var tracks = Yard.Tracks;
            if (ToolPanel.Count("yardtracks", "Tracks", ref tracks, 1, YardTool.MaxTracks))
                Yard.Tracks = tracks;
        }

        // The switch size: any standard one (a ladder's up to No. 10), or automatic for a crossover.
        var sizes = new List<(int Number, string Name)>();
        if (Yard.Mode == YardMode.Crossover)
            sizes.Add((0, "Auto"));
        foreach (var (number, _) in Turnout.Sizes)
            if (Yard.Mode != YardMode.Ladder || number <= 10)
                sizes.Add((number, $"No. {number}"));
        var size = Yard.Mode == YardMode.Ladder && Yard.Size == 0 ? 8 : Yard.Size;
        Kit.Caption("Switch size");
        var shown = sizes.FindIndex(s => s.Number == size);
        var chosen = ToolPanel.Mode("yardsize", sizes.Select(s => s.Name).ToArray(), shown, 4);
        if (chosen != shown && chosen >= 0)
            Yard.Size = sizes[chosen].Number;

        if (Yard.Plan is { } plan)
        {
            if (plan.Problem is { } problem)
                ToolPanel.Problem(problem);
            else
                ToolPanel.Line(plan.Summary);
        }

        Kit.EndCard();
    }

    public bool Escape() => false;

    public (string Key, string Action)[] Hints => Yard.Mode != YardMode.Follow
        ? [("LMB", "Pick track"), ("BACKSPACE", "Start over"), ("CTRL+Z", "Undo")]
        : [("LMB", "Guide or track"), ("ENTER", "Lay track"), ("BACKSPACE", "Remove click"), ("CTRL+Z", "Undo")];
}
