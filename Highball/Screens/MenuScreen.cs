using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Audio;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// The frame every menu shares: the menu, panels on the right, key hints along the bottom, and a
/// toast for short notices.
public abstract class MenuScreen : Screen
{
    protected Menu Menu { get; }

    private string _toast = string.Empty;
    private double _toastAt = -10.0;

    protected MenuScreen(string title)
    {
        Menu = new Menu(title) { OnBack = Back };
    }

    public override void Enter()
    {
        AudioEngine.PlayMusic(MusicTrack.Menu);
        Menu.Open();
    }

    public override void Resume() => Menu.Open();

    protected virtual void Back() => HighballApp.Instance.Pop();

    public override void Update(float dt) => Menu.Update();

    /// A short notice that slides up from the bottom and fades after a few seconds.
    protected void Notify(string text)
    {
        _toast = text;
        _toastAt = Tween.Now;
    }

    /// The menu's anchor: its right edge for front pages, its left edge for settings rows.
    protected virtual float MenuX(float2 size) => Menu.Look == MenuLook.Hero ? size.X * 0.3f : S(110f);

    protected virtual float MenuTop(float2 size) => size.Y * 0.2f;

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        Menu.Draw(draw, MenuX(size), MenuTop(size));
        DrawRight(draw, size);
        Canvas.Hints(draw, new float2(S(110f), size.Y - S(40f)), HintKeys());
        DrawToast(draw, size);
    }

    protected virtual (string Key, string Action)[] HintKeys() => [("ENTER", "Select"), ("ESC", "Back")];

    /// Panels to the right of the menu.
    protected virtual void DrawRight(ImDrawListPtr draw, float2 size) { }

    protected void DrawToast(ImDrawListPtr draw, float2 size)
    {
        var age = (float)(Tween.Now - _toastAt);
        const float life = 3f;
        if (age > life || _toast.Length == 0)
            return;

        var enter = Easing.Apply(Ease.OutCubic, age / 0.25f);
        var alpha = MathF.Min(enter, Math.Clamp((life - age) / 0.3f, 0f, 1f));

        var textWidth = Canvas.SpacedWidth(Medium, S(13f), _toast.ToUpperInvariant(), 0.08f);
        var w = textWidth + S(72f);
        var h = S(44f);
        var min = new float2((size.X - w) * 0.5f, size.Y - S(130f) + (1f - enter) * S(16f));
        var max = min + new float2(w, h);

        Canvas.Plate(draw, min, max, alpha);
        Canvas.Lamp(draw, new float2(min.X + S(24f), min.Y + h * 0.5f), S(3.5f), Accent, alpha);
        Canvas.Spaced(draw, Medium, S(13f), new float2(min.X + S(44f), min.Y + h * 0.5f - S(8f)), Style.Text.Fade(alpha), _toast.ToUpperInvariant(), 0.08f);
    }
}
