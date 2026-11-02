using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Audio;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Power on: the RailWerkz mark, then HIGHBALL, while the real startup work runs underneath, one step a frame (the
/// GPU, the audio device, the interface sounds, the routes). A key or click skips the rest of the
/// animation once the work is done.
public sealed class SplashScreen : Screen
{
    public override WorldView View => WorldView.None;

    private const float MinimumTime = 2.6f;
    private Startup _startup = null!;
    private double _start;
    private double _doneAt = -1.0;
    private bool _leaving;

    public override void Enter()
    {
        var app = HighballApp.Instance;
        _start = Tween.Now;

        _startup = new Startup(
        [
            new("Display", _ => new(Startup.Status.Ok, app.GpuName)),
            new("Vulkan", _ => new(Startup.Status.Ok, app.ApiVersion)),
            new("Audio", _ => AudioEngine.Available ? new(Startup.Status.Ok, "OpenAL") : new(Startup.Status.Warn, "No device")),
            new("Interface sounds", _ =>
            {
                Sfx.Register();
                return new(Startup.Status.Ok, AudioEngine.Available ? "Loaded" : "Silent");
            }),
            new("Routes", _ =>
            {
                var count = RouteInfo.All().Count;
                return count > 0 ? new(Startup.Status.Ok, $"{count} found") : new(Startup.Status.Warn, "None");
            }),
        ]);
    }

    public override void Update(float dt)
    {
        var now = Tween.Now;
        var elapsed = now - _start;

        // Let the logo land before the work starts, so a slow step doesn't freeze the first frames.
        if (elapsed > 0.5 && !_startup.Done)
            _startup.Advance(now);

        if (_startup.Done && _doneAt < 0.0)
            _doneAt = now;

        var skip = _startup.Done && (Keyboard.Clicked() || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Space) || ImGui.IsKeyPressed(ImGuiKey.Escape));
        if (!_leaving && _startup.Done && (elapsed > MinimumTime || skip || DevTools.SkipSplash))
        {
            _leaving = true;
            HighballApp.Instance.Replace(new MainMenuScreen());
        }
    }

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        var t = (float)(Tween.Now - _start);
        var center = new float2(size.X * 0.5f, size.Y * 0.46f);

        // RAILWERKZ, letter-spaced, tracking in from wide.
        var markIn = Easing.Apply(Ease.OutQuint, t / 0.9f);
        Canvas.Spaced(draw, Bold, S(15f), new float2(center.X, center.Y - S(70f)), TextDim.Fade(markIn), "RAILWERKZ", 0.9f - 0.45f * markIn, Align.Center);

        // HIGHBALL rises into place.
        var titleIn = Easing.Apply(Ease.OutQuint, (t - 0.25f) / 0.9f);
        var titleY = center.Y - S(40f) + (1f - titleIn) * S(30f);
        Canvas.Spaced(draw, Heavy, S(92f), new float2(center.X, titleY), Style.Text.Fade(titleIn), "HIGHBALL", 0.08f, Align.Center);

        // The accent rule wipes out from the centre.
        var ruleIn = Easing.Apply(Ease.InOutCubic, (t - 0.6f) / 0.7f);
        var ruleHalf = S(140f) * ruleIn;
        var ruleY = titleY + S(118f);
        draw.AddRectFilled(new float2(center.X - ruleHalf, ruleY), new float2(center.X + ruleHalf, ruleY + S(3f)), Accent);

        // Progress: a thin track at the bottom with the step running now.
        var barWidth = S(360f);
        var barY = size.Y - S(120f);
        var barIn = Easing.Apply(Ease.OutCubic, (t - 0.4f) / 0.5f);
        var progress = Tween.Float("splash", "progress", _startup.Progress, 0.3f, Ease.OutCubic, initial: 0f);
        var barMin = new float2(center.X - barWidth * 0.5f, barY);
        draw.AddRectFilled(barMin, barMin + new float2(barWidth, S(3f)), new Rgba(1f, 1f, 1f, 0.08f * barIn));
        draw.AddRectFilled(barMin, barMin + new float2(barWidth * progress, S(3f)), Accent.Fade(barIn));

        var line = _startup.Done ? "Ready" : _startup.CurrentLabel;
        Canvas.Text(draw, Medium, S(13f), new float2(center.X, barY + S(16f)), TextDim.Fade(barIn), line, Align.Center);

        var version = $"v{typeof(SplashScreen).Assembly.GetName().Version?.ToString(3)}";
        Canvas.Text(draw, Mono, S(12f), new float2(size.X - S(40f), size.Y - S(36f)), TextFaint.Fade(barIn), version, Align.Right);
    }
}
