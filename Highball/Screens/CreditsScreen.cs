using Brutal.ImGuiApi;
using Highball.App;
using RailWerkz.Core.Audio;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Who made it and what it's built on, rolling up the middle of the screen. Any key or click goes back.
public sealed class CreditsScreen : Screen
{
    private double _openedAt;

    private static readonly (string Heading, string[] Lines)[] Sections =
    [
        ("DEVELOPED AND PUBLISHED BY", ["RailWerkz"]),
        ("BUILT ON", ["BRUTAL: GLFW, Vulkan, Dear ImGui, OpenAL, RakNet, stb, glTF", "NLayer (MIT)", "FastNoiseLite (MIT)"]),
        ("TYPE", ["JetBrains Mono (SIL Open Font License)"]),
    ];

    public override void Enter()
    {
        _openedAt = Tween.Now;
        AudioEngine.PlayMusic(MusicTrack.Credits);
    }

    public override void Update(float dt)
    {
        if (Keyboard.Clicked() || Keyboard.Pressed(ImGuiKey.Escape) || Keyboard.Pressed(ImGuiKey.Enter) || Keyboard.Pressed(ImGuiKey.Backspace) || Keyboard.Pressed(ImGuiKey.Space))
        {
            Sfx.Back();
            HighballApp.Instance.Pop();
        }
    }

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        var t = (float)(Tween.Now - _openedAt);
        var cx = size.X * 0.5f;

        // Total height, so the roll loops once everything has passed.
        var height = S(260f);
        foreach (var (heading, lines) in Sections)
            height += (heading.Length > 0 ? S(34f) : 0f) + lines.Length * S(40f) + S(70f);

        var speed = S(46f);
        var travel = size.Y + height;
        var y = size.Y - (t * speed % travel) + S(40f);

        Canvas.Spaced(draw, Heavy, S(64f), new float2(cx, y), Style.Text, "HIGHBALL", 0.08f, Align.Center);
        draw.AddRectFilled(new float2(cx - S(60f), y + S(90f)), new float2(cx + S(60f), y + S(93f)), Accent);
        y += S(260f);

        foreach (var (heading, lines) in Sections)
        {
            if (heading.Length > 0)
            {
                Canvas.Spaced(draw, Bold, S(13f), new float2(cx, y), Accent, heading, 0.24f, Align.Center);
                y += S(34f);
            }

            foreach (var line in lines)
            {
                Canvas.Text(draw, Medium, S(22f), new float2(cx, y), Style.Text, line, Align.Center);
                y += S(40f);
            }

            y += S(70f);
        }

        // Fade the roll in and out at the top and bottom edges.
        Canvas.VGradient(draw, new float2(0f, 0f), new float2(size.X, S(200f)), Rgba.Black, Rgba.Black.WithAlpha(0f));
        Canvas.VGradient(draw, new float2(0f, size.Y - S(200f)), size, Rgba.Black.WithAlpha(0f), Rgba.Black);
        Canvas.Hints(draw, new float2(S(110f), size.Y - S(40f)), ("ESC", "Back"));
    }
}
