using Brutal.ImGuiApi;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace RailWerkz.UI;

/// Split-flap readouts, as on a station departure board: one dark cell per character with a seam across the
/// middle. When the text changes (or first appears) each cell riffles through letters before it settles, left
/// to right.
public static class SplitFlap
{
    private sealed class Board
    {
        public string Text = string.Empty;
        public double ChangedAt;
    }

    private const string Glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const float FlipTime = 0.045f;

    private static readonly Dictionary<string, Board> Boards = [];

    /// The width of a readout of count characters at this cell height.
    public static float Width(int count, float height) => count <= 0 ? 0f : count * CellWidth(height) + (count - 1) * Gap(height);

    private static float CellWidth(float height) => height * 0.66f;
    private static float Gap(float height) => MathF.Max(1f, height * 0.08f);

    /// Draws text in cells from pos (top-left, or top-right with Align.Right). Pads to cells characters if given, on
    /// the side away from the alignment.
    public static void Draw(ImDrawListPtr draw, string id, string text, float2 pos, float height, Rgba color, int cells = 0, Align align = Align.Left, float alpha = 1f)
    {
        var now = Tween.Now;
        text = text.ToUpperInvariant();
        if (text.Length < cells)
            text = align == Align.Right ? text.PadLeft(cells) : text.PadRight(cells);

        if (!Boards.TryGetValue(id, out var board))
            Boards[id] = board = new Board { Text = "", ChangedAt = now };

        if (board.Text != text)
        {
            board.Text = text;
            board.ChangedAt = now;
        }

        var cw = CellWidth(height);
        var gap = Gap(height);
        var total = Width(text.Length, height);
        var x = align switch { Align.Right => pos.X - total, Align.Center => pos.X - total * 0.5f, _ => pos.X };
        var fontSize = height * 0.78f;
        var age = (float)(now - board.ChangedAt);

        for (var i = 0; i < text.Length; i++)
        {
            var min = new float2(x, pos.Y);
            var max = new float2(x + cw, pos.Y + height);
            draw.AddRectFilled(min, max, Rgba.Hex(0x16181C).Fade(alpha));

            var c = text[i];
            var settle = Tween.ReduceMotion ? 0f : 0.12f + i * 0.035f;
            var flipping = c != ' ' && age < settle;
            var shown = c;

            if (flipping)
            {
                var slot = (int)(age / FlipTime);
                shown = Glyphs[(int)((uint)(slot * 7919 + i * 104729) % Glyphs.Length)];
            }

            if (shown != ' ')
            {
                var s = shown.ToString();
                var size = Canvas.TextSize(MonoBold, fontSize, s);
                Canvas.Text(draw, MonoBold, fontSize, new float2(min.X + (cw - size.X) * 0.5f, min.Y + (height - size.Y) * 0.5f), (flipping ? color.Fade(0.7f) : color).Fade(alpha), s);
            }

            // The seam between the two flaps; it catches light for a moment while a flap falls.
            var seamY = pos.Y + height * 0.5f;
            draw.AddLine(new float2(min.X, seamY), new float2(max.X, seamY), Rgba.Black.WithAlpha(0.85f * alpha), MathF.Max(1f, height * 0.05f));
            if (flipping && (int)(age / FlipTime * 2f) % 2 == 0)
                draw.AddRectFilled(new float2(min.X, min.Y), new float2(max.X, seamY), new Rgba(1f, 1f, 1f, 0.05f * alpha));

            x += cw + gap;
        }
    }

    /// Makes the board riffle again, as if just set (for a screen that opens).
    public static void Replay(string id)
    {
        if (Boards.TryGetValue(id, out var board))
            board.ChangedAt = Tween.Now;
    }
}
