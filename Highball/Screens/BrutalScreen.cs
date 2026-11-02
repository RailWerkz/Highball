using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using RailWerkz.UI.Anim;

namespace Highball.Screens;

/// The BRUTAL logo on black for 2.5 seconds before anything else, as BRUTAL's licence requires. Not skippable.
public sealed class BrutalScreen : Screen
{
    private const float Duration = 2.5f;
    private const float Fade = 0.4f;

    private Texture2D? _logo;
    private ImTextureRef _logoRef;
    private double _start;
    private bool _leaving;

    public override void Enter()
    {
        _start = Tween.Now;

        try
        {
            _logo = Texture2D.LoadFile(ContentPath.Get("Vendor/Brutal/LOGO_BRUTAL_WHT.png"), false, true);
            _logoRef = HighballApp.Instance.AddTexture(_logo);
        }
        catch (Exception e)
        {
            Log.Error($"BRUTAL logo: {e.Message}");
        }
    }

    public override void Exit()
    {
        if (_logo is null)
            return;

        HighballApp.Instance.RemoveTexture(_logoRef);
        _logo.Dispose();
    }

    public override void Update(float dt)
    {
        if (!_leaving && Tween.Now - _start >= Duration)
        {
            _leaving = true;
            HighballApp.Instance.Replace(new SplashScreen());
        }
    }

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        if (_logo is null)
            return;

        var t = (float)(Tween.Now - _start);
        var alpha = Math.Clamp(MathF.Min(t / Fade, (Duration - t) / Fade), 0f, 1f);
        var width = MathF.Min(size.X * 0.4f, Style.S(720f));
        var height = width * _logo.Height / _logo.Width;
        var min = new float2((size.X - width) * 0.5f, (size.Y - height) * 0.5f);
        draw.AddImage(_logoRef, min, min + new float2(width, height), null, null, Rgba.White.WithAlpha(alpha));
    }
}
