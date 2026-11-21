using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// The front door: SINGLE PLAYER, MULTIPLAYER, ROUTE BUILDER, CONSIST EDITOR, then the rest.
public sealed class MainMenuScreen : MenuScreen
{
    public MainMenuScreen() : base("MAIN MENU")
    {
        Menu.ShowHeader = false;
        Menu.Add(new MenuItem { Label = "SINGLE PLAYER", OnSelect = () => HighballApp.Instance.Push(new SinglePlayerScreen()) })
            .Add(new MenuItem { Label = "MULTIPLAYER", OnSelect = () => HighballApp.Instance.Push(new MultiplayerScreen()) })
            .Add(new MenuItem { Label = "ROUTE BUILDER", OnSelect = RouteBuilder.Push })
            .Add(new MenuItem { Label = "CONSIST EDITOR", OnSelect = () => HighballApp.Instance.Push(new ConsistEditorScreen()), SeparatorAfter = true })
            .Add(new MenuItem { Label = "SETTINGS", OnSelect = () => HighballApp.Instance.Push(new SettingsScreen()) })
            .Add(new MenuItem { Label = "CREDITS", OnSelect = () => HighballApp.Instance.Push(new CreditsScreen()) })
            .Add(new MenuItem { Label = "QUIT", OnSelect = () => HighballApp.Instance.Quit() });
    }

    /// Escape on the main menu goes to Quit rather than leaving.
    protected override void Back() => Menu.Selected = Menu.Items.Count - 1;

    public override void Enter()
    {
        base.Enter();
        DevTools.OnMainMenu(HighballApp.Instance);
    }

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        base.Draw(draw, size);
        Canvas.Text(draw, Heavy, S(34f), new float2(MenuX(size), MenuTop(size) - S(70f)), Style.Text, "HIGHBALL", Align.Right);
    }

    /// Centred on the window, but never up into the title or down into the key hints.
    protected override float MenuTop(float2 size)
    {
        var height = Menu.ListHeight;
        var min = S(130f);
        var max = MathF.Max(min, size.Y - S(90f) - height);
        return Math.Clamp((size.Y - height) * 0.5f, min, max);
    }
}
