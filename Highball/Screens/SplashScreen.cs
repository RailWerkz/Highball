using Brutal.ImGuiApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Audio;
using RailWerkz.UI;
using RailWerkz.UI.Anim;

namespace Highball.Screens;

/// Black while the startup work runs, one step a frame (the GPU, the audio device, the interface sounds, the routes),
/// then the main menu.
public sealed class SplashScreen : Screen
{
    public override WorldView View => WorldView.None;

    private Startup _startup = null!;
    private bool _leaving;

    public override void Enter()
    {
        var app = HighballApp.Instance;

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
        if (!_startup.Done)
            _startup.Advance(Tween.Now);
        else if (!_leaving)
        {
            _leaving = true;
            HighballApp.Instance.Replace(new MainMenuScreen());
        }
    }

    public override void Draw(ImDrawListPtr draw, float2 size) { }
}
