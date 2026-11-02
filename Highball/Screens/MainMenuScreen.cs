using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// The front door: SINGLE PLAYER, MULTIPLAYER, ROUTE BUILDER, then the rest, beside the changelog.
public sealed class MainMenuScreen : MenuScreen
{
    private readonly List<Changelog.Release> _changelog = Changelog.Load();
    private double _openedAt;

    public MainMenuScreen() : base("MAIN MENU")
    {
        Menu.ShowHeader = false;
        Menu.Add(new MenuItem { Label = "SINGLE PLAYER", OnSelect = () => HighballApp.Instance.Push(new SinglePlayerScreen()) })
            .Add(new MenuItem { Label = "MULTIPLAYER", OnSelect = () => HighballApp.Instance.Push(new MultiplayerScreen()) })
            .Add(new MenuItem { Label = "ROUTE BUILDER", OnSelect = () => HighballApp.Instance.Push(new RouteBuilderScreen()), SeparatorAfter = true })
            .Add(new MenuItem { Label = "OPTIONS", OnSelect = () => HighballApp.Instance.Push(new OptionsScreen()) })
            .Add(new MenuItem { Label = "CREDITS", OnSelect = () => HighballApp.Instance.Push(new CreditsScreen()) })
            .Add(new MenuItem { Label = "QUIT", OnSelect = () => HighballApp.Instance.Quit() });
    }

    /// Escape on the main menu goes to Quit rather than leaving.
    protected override void Back() => Menu.Selected = Menu.Items.Count - 1;

    public override void Enter()
    {
        base.Enter();
        _openedAt = Tween.Now;
        ReplayBoards();
        DevTools.OnMainMenu(HighballApp.Instance);
    }

    public override void Resume()
    {
        base.Resume();
        _openedAt = Tween.Now;
        ReplayBoards();
    }

    private void ReplayBoards()
    {
        for (var r = 0; r < _changelog.Count; r++)
        {
            SplitFlap.Replay($"changelog.{r}.v");
            SplitFlap.Replay($"changelog.{r}.d");
        }
    }

    /// Centred on the window, but never up into the wordmark or down into the key hints.
    protected override float MenuTop(float2 size)
    {
        var height = Menu.ListHeight;
        var min = S(130f);
        var max = MathF.Max(min, size.Y - S(90f) - height);
        return Math.Clamp((size.Y - height) * 0.5f, min, max);
    }

    /// The plate's height for the whole changelog, matching the layout in DrawRight.
    private float ChangelogHeight(float textWidth)
    {
        var height = S(44f) + S(22f) + S(8f);
        foreach (var release in _changelog)
        {
            height += S(26f) + S(18f) + S(24f);
            foreach (var change in release.Changes)
                height += Canvas.Wrap(change, Style.Mono, S(14f), textWidth - S(18f)).Count * S(21f) + S(7f);
        }

        return height;
    }

    protected override void DrawRight(ImDrawListPtr draw, float2 size)
    {
        if (_changelog.Count == 0)
            return;

        // Top-right corner, level with the wordmark. It narrows to stay clear of the menu, and is left out when
        // the window is too small to hold its title and one release row.
        var right = size.X - S(64f);
        var menuRight = MenuX(size) + S(30f);
        var width = MathF.Min(S(460f), right - menuRight - S(64f));
        var top = S(44f);
        var bottom = MathF.Min(size.Y - S(64f), top + ChangelogHeight(width - S(48f)));
        if (width < S(320f) || bottom - top < S(44f) + S(22f) + S(26f) + S(40f))
            return;

        var left = right - width;
        var enter = Tween.Progress(_openedAt, 0.5f, Ease.OutQuint);
        var offset = new float2((1f - enter) * S(30f), 0f);
        var min = new float2(left, top) + offset;
        var max = new float2(right, bottom) + offset;

        var content = Canvas.Plate(draw, min, max, enter, "CHANGELOG");
        var x = content.X;
        var textWidth = width - S(48f);
        var y = content.Y;
        var flap = S(26f);
        draw.PushClipRect(min, max - new float2(0f, S(20f)), true);

        for (var r = 0; r < _changelog.Count; r++)
        {
            var release = _changelog[r];
            SplitFlap.Draw(draw, $"changelog.{r}.v", release.Version, new float2(x, y), flap, Accent, 0, Align.Left, enter);
            if (release.Date.Length > 0)
                SplitFlap.Draw(draw, $"changelog.{r}.d", release.Date, new float2(max.X - S(24f), y), flap, Style.Text, 0, Align.Right, enter);
            y += flap + S(18f);

            foreach (var change in release.Changes)
            {
                draw.AddRectFilled(new float2(x, y + S(7f)), new float2(x + S(5f), y + S(12f)), TextDim.Fade(enter));
                foreach (var line in Canvas.Wrap(change, Mono, S(14f), textWidth - S(18f)))
                {
                    Canvas.Text(draw, Mono, S(14f), new float2(x + S(18f), y), TextDim.Fade(enter), line);
                    y += S(21f);
                }

                y += S(7f);
            }

            y += S(24f);
        }

        draw.PopClipRect();
    }
}
