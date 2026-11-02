using Brutal.ImGuiApi;
using static RailWerkz.UI.Style;

namespace RailWerkz.UI;

public enum Align { Left, Center, Right }

/// The draw-list pieces every screen is built from, in the manner of a signal panel: flat enamel plates with two
/// corners cut, hairline frames, square lamps, segmented gauges. No rounding, no shadows.
public static class Canvas
{
    public static float2 TextSize(ImFontPtr font, float size, string text) => font.CalcTextSizeA(size, float.MaxValue, 0f, text);

    public static void Text(ImDrawListPtr draw, ImFontPtr font, float size, float2 pos, Rgba color, string text, Align align = Align.Left)
    {
        if (align != Align.Left)
        {
            var width = TextSize(font, size, text).X;
            pos.X -= align == Align.Center ? width * 0.5f : width;
        }

        draw.AddText(font, size, pos, color, text);
    }

    /// Letter-spaced capitals.
    public static void Spaced(ImDrawListPtr draw, ImFontPtr font, float size, float2 pos, Rgba color, string text, float tracking = 0.12f, Align align = Align.Left)
    {
        var gap = size * tracking;
        var width = SpacedWidth(font, size, text, tracking);
        var x = align switch { Align.Center => pos.X - width * 0.5f, Align.Right => pos.X - width, _ => pos.X };

        foreach (var c in text)
        {
            var s = c.ToString();
            draw.AddText(font, size, new float2(x, pos.Y), color, s);
            x += TextSize(font, size, s).X + gap;
        }
    }

    public static float SpacedWidth(ImFontPtr font, float size, string text, float tracking = 0.12f)
    {
        if (text.Length == 0)
            return 0f;

        var width = 0f;
        foreach (var c in text)
            width += TextSize(font, size, c.ToString()).X;
        return width + size * tracking * (text.Length - 1);
    }

    /// The outline of a plate: a rectangle with its top-left and bottom-right corners cut at 45 degrees.
    private static void PlatePoints(float2 min, float2 max, float cut, Span<float2> p)
    {
        p[0] = new float2(min.X + cut, min.Y);
        p[1] = new float2(max.X, min.Y);
        p[2] = new float2(max.X, max.Y - cut);
        p[3] = new float2(max.X - cut, max.Y);
        p[4] = new float2(min.X, max.Y);
        p[5] = new float2(min.X, min.Y + cut);
    }

    /// A filled plate with a hairline frame. Returns where the content starts (below the title band, if any).
    public static float2 Plate(ImDrawListPtr draw, float2 min, float2 max, float alpha = 1f, string? title = null)
    {
        var cut = S(16f);
        Span<float2> p = stackalloc float2[6];
        PlatePoints(min, max, cut, p);

        // One polygon, not a triangle fan: ImGui anti-aliases every triangle's edges, which leaves seams inside.
        draw.AddConvexPolyFilled(p, Surface.Fade(alpha));

        var frame = StrokeStrong.Fade(alpha);
        for (var i = 0; i < 6; i++)
            draw.AddLine(p[i], p[(i + 1) % 6], frame, 1f);

        if (title is null)
            return min + new float2(S(24f), S(20f));

        // The title band: a lamp, the title, then a double rule across the plate.
        var band = S(44f);
        Lamp(draw, new float2(min.X + S(24f), min.Y + band * 0.5f), S(4f), Accent, alpha);
        Spaced(draw, Bold, S(12f), new float2(min.X + S(38f), min.Y + band * 0.5f - S(7.5f)), Style.Text.Fade(alpha), title, 0.24f);
        draw.AddLine(new float2(min.X, min.Y + band), new float2(max.X, min.Y + band), frame, 1f);
        draw.AddLine(new float2(min.X, min.Y + band + S(3f)), new float2(max.X, min.Y + band + S(3f)), Stroke.Fade(alpha), 1f);
        return new float2(min.X + S(24f), min.Y + band + S(22f));
    }

    /// A square indicator lamp, lit or dark.
    public static void Lamp(ImDrawListPtr draw, float2 center, float half, Rgba color, float alpha = 1f, bool lit = true)
    {
        var h = new float2(half, half);
        if (lit)
        {
            draw.AddRectFilled(center - h * 2.2f, center + h * 2.2f, color.WithAlpha(0.12f * alpha));
            draw.AddRectFilled(center - h, center + h, color.Fade(alpha));
        }
        else
        {
            draw.AddRect(center - h, center + h, TextFaint.Fade(alpha), 0f, ImDrawFlags.None, 1f);
        }
    }

    /// A segmented bar gauge, value 0..1.
    public static void SegmentBar(ImDrawListPtr draw, float2 min, float2 max, float value, Rgba color, int segments = 20, float alpha = 1f)
    {
        var gap = S(2f);
        var width = (max.X - min.X - gap * (segments - 1)) / segments;
        var lit = Math.Clamp(value, 0f, 1f) * segments;

        for (var i = 0; i < segments; i++)
        {
            var x = min.X + i * (width + gap);
            var on = Math.Clamp(lit - i, 0f, 1f);
            draw.AddRectFilled(new float2(x, min.Y), new float2(x + width, max.Y), new Rgba(1f, 1f, 1f, 0.07f * alpha));
            if (on > 0f)
                draw.AddRectFilled(new float2(x, min.Y), new float2(x + width, max.Y), color.Fade(on * alpha));
        }
    }

    /// A small solid triangle pointing left or right, centred on c.
    public static void Arrow(ImDrawListPtr draw, float2 c, float size, Rgba color, bool left = false)
    {
        var d = left ? -1f : 1f;
        draw.AddTriangleFilled(new float2(c.X - size * 0.4f * d, c.Y - size * 0.5f), new float2(c.X + size * 0.5f * d, c.Y), new float2(c.X - size * 0.4f * d, c.Y + size * 0.5f), color);
    }

    /// A vertical gradient (top to bottom).
    public static void VGradient(ImDrawListPtr draw, float2 min, float2 max, Rgba top, Rgba bottom) =>
        draw.AddRectFilledMultiColor(min, max, top, top, bottom, bottom);

    /// A horizontal gradient (left to right).
    public static void HGradient(ImDrawListPtr draw, float2 min, float2 max, Rgba left, Rgba right) =>
        draw.AddRectFilledMultiColor(min, max, left, right, right, left);

    /// The key hints along the bottom: [ENTER] Select   [ESC] Back.
    public static void Hints(ImDrawListPtr draw, float2 bottomLeft, params (string Key, string Action)[] hints)
    {
        var x = bottomLeft.X;
        var h = S(22f);

        foreach (var (key, action) in hints)
        {
            var keySize = TextSize(MonoBold, S(12f), key);
            var keyMin = new float2(x, bottomLeft.Y - h);
            var keyMax = keyMin + new float2(keySize.X + S(14f), h);
            draw.AddRect(keyMin, keyMax, StrokeStrong, 0f, ImDrawFlags.None, 1f);
            draw.AddLine(new float2(keyMin.X + 1f, keyMax.Y - S(2f)), new float2(keyMax.X - 1f, keyMax.Y - S(2f)), Stroke, S(2f));
            Text(draw, MonoBold, S(12f), keyMin + new float2(S(7f), (h - keySize.Y) * 0.5f - S(1f)), Style.Text, key);
            x = keyMax.X + S(8f);
            var actionSize = TextSize(Regular, S(13f), action);
            Text(draw, Regular, S(13f), new float2(x, bottomLeft.Y - h + (h - actionSize.Y) * 0.5f), TextDim, action.ToUpperInvariant());
            x += TextSize(Regular, S(13f), action.ToUpperInvariant()).X + S(24f);
        }
    }

    /// Splits text into lines no wider than width.
    public static List<string> Wrap(string text, ImFontPtr font, float size, float width)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;

            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (TextSize(font, size, candidate).X > width && line.Length > 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);
        }

        return lines;
    }
}
