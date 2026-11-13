using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Switches tool: every switch marked, one picked to size (from the standard sizes, each shown by its model), its
/// curve's end and its points dragged with handles (SwitchHandles), and what works it; junctions that need fixing listed.
public sealed class SwitchesTool(ToolContext context, RouteInfo route) : IEditorTool, IDisposable
{
    private const float FeetPerMeter = 3.28084f;

    private Dictionary<int, (Texture2D Texture, ImTextureRef Ref)>? _sizePictures;

    /// The picked switch, by its node.
    public long? Picked { get; set; }

    /// The picked switch's handles.
    public SwitchHandles Handles { get; } = new();

    /// A press on the picked switch's handles drags them; any other picks what's under it.
    public void Update(float dt)
    {
        if (context.Source.Lines is not { } lines)
            return;
        var blocked = context.Blocked;
        var mouse = Input.MousePosition;
        var turnout = Picked is { } node ? lines.Turnouts.FirstOrDefault(t => t.Node == node) : null;
        var clicked = !blocked && Keyboard.Clicked();
        if (!context.Rebuilding || Handles.Active)
            Change(turnout, Handles.Update(lines, turnout, mouse, !blocked && context.Hit is { } hit ? (hit.X, hit.Z) : null, context.ProjectGround, S(1f),
                clicked && !context.Rebuilding, Input.Mouse(GlfwMouseButton.Left)));
        if (clicked && !Handles.Active)
            Picked = SwitchAt(lines, mouse);
    }

    /// A handle let go: the curve's end saved, or the switch (or crossover) slid along its track.
    public void Change(Turnout? turnout, SwitchHandles.Change? change)
    {
        if (change is null || turnout is null)
            return;
        context.Refused = null;
        if (change.Reach is { } reach)
        {
            var edit = new SwitchCurveEdit(change.Node, SwitchCurves.Load(route).TryGetValue(change.Node, out var was) ? was : null, reach);
            SwitchCurves.Set(route, change.Node, reach);
            context.Record(edit);
            context.RebuildNetwork();
        }
        else if (change.SlideTo is { } to)
        {
            var tracks = context.Tracks;
            var edit = change.Partner is { } partner ? tracks.SlideCrossover(turnout, to, partner.Turnout, partner.To) : tracks.SlideSwitch(turnout, to.X, to.Z);
            if (edit is not null)
                context.Commit(edit);
            else
                context.Refused = change.Partner is null ? "No room for the diverging track" : "Can't move the crossover there";
        }
    }

    /// Every switch marked (filled where it's modelled), the picked one ringed with its handles, and every junction that
    /// needs fixing ringed red.
    public void Draw(ImDrawListPtr draw)
    {
        if (context.Source.Lines is not { } lines)
            return;

        var camera = context.Camera;
        var reach = context.Reach;
        foreach (var layout in lines.Layouts)
        {
            if (layout.Issue is null || Math.Abs(layout.X - camera.X) > reach || Math.Abs(layout.Z - camera.Z) > reach || context.ProjectGround(layout.X, layout.Z) is not { } at)
                continue;
            draw.AddCircleFilled(at, S(11f), Stop.WithAlpha(0.25f), 20);
            draw.AddCircle(at, S(11f), Stop, 20, S(2f));
        }

        foreach (var t in lines.Turnouts)
        {
            if (Math.Abs(t.X - camera.X) > reach || Math.Abs(t.Z - camera.Z) > reach || context.ProjectGround(t.X, t.Z) is not { } at)
                continue;
            var r = S(t.Node == Picked ? 9f : 6f);
            var (a, b, c, d) = (at + new float2(0f, -r), at + new float2(r, 0f), at + new float2(0f, r), at + new float2(-r, 0f));
            if (t.Modelled)
                draw.AddQuadFilled(a, b, c, d, Caution);
            draw.AddQuad(a, b, c, d, t.Modelled ? new Rgba(0f, 0f, 0f, 0.8f) : Clear, S(1.5f));
            if (t.Node == Picked)
                draw.AddCircle(at, S(16f), Rgba.White, 24, S(2f));
        }

        if (Picked is { } node && lines.Turnouts.FirstOrDefault(t => t.Node == node) is { } picked)
        {
            var imperial = Settings.Current.Units == UnitSystem.Imperial;
            Handles.Draw(draw, lines, picked, context.ProjectGround, S(1f),
                m => imperial ? string.Create(CultureInfo.InvariantCulture, $"{m * FeetPerMeter:0} ft") : string.Create(CultureInfo.InvariantCulture, $"{m:0} m"));
        }
    }

    /// The switch whose marker (or red ring) is under the mouse, measured on the screen as they're drawn, so a click has
    /// to land on one whatever the camera's distance; the nearest if several are; null if it missed them all.
    private long? SwitchAt(NetworkLines lines, float2 mouse)
    {
        var camera = context.Camera;
        var reach = context.Reach;
        long? nearest = null;
        var best = float.MaxValue;
        void Consider(long node, double x, double z, float radius)
        {
            if (Math.Abs(x - camera.X) > reach || Math.Abs(z - camera.Z) > reach || context.ProjectGround(x, z) is not { } at)
                return;
            var (dx, dy) = (at.X - mouse.X, at.Y - mouse.Y);
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= radius && distance < best)
                (nearest, best) = (node, distance);
        }

        foreach (var t in lines.Turnouts)
            Consider(t.Node, t.X, t.Z, S(t.Node == Picked ? 16f : 9f));
        foreach (var layout in lines.Layouts)
        {
            if (layout.Issue is not null && lines.Turnouts.FirstOrDefault(t => layout.Nodes.Contains(t.Node)) is { } turnout)
                Consider(turnout.Node, layout.X, layout.Z, S(11f));
        }

        return nearest;
    }

    /// The picked switch: what it is now, and the sizes to choose from, each shown by its model.
    public void Panel(bool imperial)
    {
        if (context.Source.Lines is not { } lines)
            return;
        if (Picked is not { } node || lines.Turnouts.FirstOrDefault(t => t.Node == node) is not { } turnout)
        {
            ToolPanel.Hint("Click a switch.");
            IssueList(lines);
            return;
        }

        var options = lines.SwitchOptions.GetValueOrDefault(node);
        var chosen = options?.Chosen;
        var byHand = options?.ReachByHand == true;
        Kit.BeginCard("Switch", $"Node {node}");
        Kit.Stats(
        [
            new Stat("Size", turnout.Number is { } now ? $"No. {now}" : "As mapped"),
            new Stat("Sized", turnout.Number is null
                ? chosen == SwitchSizes.AsMapped ? "Picked" : "No standard fits"
                : byHand ? "Curve by hand" : chosen is null ? "Automatically" : "Picked"),
        ], 2);
        if (lines.Layouts.FirstOrDefault(l => l.Nodes.Contains(node)) is { } layout)
        {
            if (layout.Kind is JunctionKind.Crossover or JunctionKind.LadderLink or JunctionKind.ScissorsCrossover)
                ToolPanel.Line($"Part of a {TrackLayouts.Name(layout.Kind, 0).ToLowerInvariant()}");
            if (layout.Issue is { } issue)
                ToolPanel.Problem(issue);
        }

        if (context.Refused is { } refused)
            ToolPanel.Problem(refused);
        Kit.EndCard();

        Working(turnout);

        Kit.BeginCard("Size");
        var rebuilding = context.Rebuilding;
        var sizing = chosen is null && !byHand ? 0 : chosen == SwitchSizes.AsMapped ? 1 : -1;
        var pickSizing = ToolPanel.Mode("sizing", ["Automatic", "As mapped"], sizing, 2, !rebuilding);
        if (pickSizing != sizing && pickSizing >= 0)
            PickSize(node, pickSizing == 0 ? null : SwitchSizes.AsMapped);

        _sizePictures ??= LoadSizePictures();
        var width = Kit.Width;
        string Length(float metres) => imperial ? $"{metres * FeetPerMeter:0} ft" : $"{metres:0} m";
        foreach (var (number, radius) in Turnout.Sizes)
        {
            var fits = options?.Fits.Contains(number) ?? false;
            var current = turnout.Number == number;
            var frog = Turnout.FrogOf(number);
            var angle = MathF.Atan(frog / radius / 2f) * 360f / MathF.PI;

            ImGui.Dummy(new float2(1f, S(4f)));
            var draw = ImGui.GetWindowDrawList();
            var top = ImGui.GetCursorScreenPos();
            Canvas.Text(draw, current ? Bold : Medium, S(15.5f), top, fits ? Style.Text : TextFaint, $"No. {number}");
            var tag = current ? "IN USE" : options?.Auto == number ? "AUTOMATIC PICK" : fits ? "" : "NO ROOM";
            if (tag.Length > 0)
                Canvas.Spaced(draw, Medium, S(11.5f), new float2(top.X + width - Canvas.SpacedWidth(Medium, S(11.5f), tag, 0.16f), top.Y + S(3.5f)), current ? Caution : TextFaint, tag, 0.16f);
            ImGui.Dummy(new float2(width, S(21f)));
            ToolPanel.Hint(string.Create(CultureInfo.InvariantCulture, $"{Length(radius)} curve, {angle:0.0} deg"));

            var size = new float2(width, width * 150f / 480f);
            var min = ImGui.GetCursorScreenPos();
            ImGui.BeginDisabled(!fits || rebuilding);
            if (ImGui.InvisibleButton($"##size{number}", size, ImGuiButtonFlags.None) && !current)
                PickSize(node, number);
            ImGui.EndDisabled();
            var hovered = fits && ImGui.IsItemHovered();
            if (_sizePictures.TryGetValue(number, out var picture))
                draw.AddImage(picture.Ref, min, min + size, null, null, fits ? Rgba.White : new Rgba(1f, 1f, 1f, 0.3f));
            else
                draw.AddRectFilled(min, min + size, Stroke.WithAlpha(0.2f));
            if (current || hovered)
                draw.AddRect(min, min + size, current ? Caution : Accent, 0f, ImDrawFlags.None, S(current ? 2.5f : 1.5f));
        }

        Kit.EndCard();
    }

    /// What works the switch: its power machine, or a hand throw in its place (on either side of the track).
    private void Working(Turnout turnout)
    {
        var hand = context.Source.Objects.FirstOrDefault(o => o.Kind == PlacedKind.HandThrow && o.Node == turnout.Node);
        Kit.BeginCard("Worked by");
        var working = hand is null ? 0 : 1;
        var pick = ToolPanel.Mode("working", ["Machine", "Hand throw"], working, 2);
        if (pick == 0 && hand is not null)
            context.ChangePlaced(hand, null);
        else if (pick == 1 && hand is null)
            context.ChangePlaced(null, context.NewPlaced(PlacedKind.HandThrow, turnout.X, turnout.Z, 0.0, turnout.Node));
        if (hand is not null && Kit.Button("handside", "Other Side"))
            context.ChangePlaced(hand, hand with { Opposite = !hand.Opposite });
        Kit.EndCard();
    }

    /// Every junction that needs fixing, with why; clicking one flies there and picks its switch.
    private void IssueList(NetworkLines lines)
    {
        var issues = lines.Layouts.Where(l => l.Issue is not null).ToList();
        if (issues.Count == 0)
            return;

        Kit.BeginCard("Needs fixing", issues.Count.ToString(CultureInfo.InvariantCulture), Stop);
        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            if (ToolPanel.Item($"issue{i}", ToolIcon.Switches, TrackLayouts.Name(issue.Kind, issue.Number ?? 0), null, false))
            {
                Picked = issue.Nodes[0];
                context.FlyTo(issue.X, issue.Z);
            }

            ToolPanel.Hint(issue.Issue!);
        }

        Kit.EndCard();
    }

    private static Dictionary<int, (Texture2D Texture, ImTextureRef Ref)> LoadSizePictures()
    {
        var pictures = new Dictionary<int, (Texture2D, ImTextureRef)>();
        foreach (var (number, _) in Turnout.Sizes)
        {
            try
            {
                var texture = Texture2D.LoadFile(ContentPath.Model($"Track/turnout_{number}.png"), false, false);
                pictures[number] = (texture, HighballApp.Instance.AddTexture(texture));
            }
            catch (Exception e)
            {
                Log.Warn($"Switch picture No. {number}: {e.Message}");
            }
        }

        return pictures;
    }

    /// Saves a switch's size (null: automatic) with the route, and lays its track again. A crossover's or ladder link's
    /// two switches go together.
    public void PickSize(long node, int? size)
    {
        var layout = context.Source.Lines?.Layouts.FirstOrDefault(l => l.Nodes.Contains(node));
        foreach (var each in layout is { Kind: JunctionKind.Crossover or JunctionKind.LadderLink } ? layout.Nodes : [node])
        {
            SwitchSizes.Set(route, each, size);
            SwitchCurves.Set(route, each, null);
        }
        Log.Info($"Switch {node}: {(size is null ? "automatic" : size == SwitchSizes.AsMapped ? "as mapped" : $"No. {size}")}");
        context.RebuildNetwork();
    }

    public bool Escape() => false;

    public (string Key, string Action)[] Hints => [("LMB", "Pick a switch"), ("DRAG", "Move"), ("CTRL+Z", "Undo")];

    public void Dispose()
    {
        if (_sizePictures is null)
            return;
        foreach (var (texture, reference) in _sizePictures.Values)
        {
            HighballApp.Instance.RemoveTexture(reference);
            texture.Dispose();
        }
    }
}
