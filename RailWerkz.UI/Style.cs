using Brutal.ImGuiApi;
using Brutal.Numerics;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace RailWerkz.UI;

/// The look: a near-black palette with one accent colour, the fonts and the interface scale. Everything the
/// toolkit draws reads these, so changing the accent repaints the whole interface on the next frame.
public static class Style
{
    // Neutrals: near-black, the enamel of the plates, hairlines, and three strengths of text.
    public static Rgba Background = Rgba.Hex(0x07090D);
    public static Rgba Surface = Rgba.Hex(0x0C0D0F, 0.97f);
    public static Rgba Raised = Rgba.Hex(0x1A1D22);
    public static Rgba RaisedHover = Rgba.Hex(0x262A31);
    public static Rgba Stroke = new(1f, 1f, 1f, 0.08f);
    public static Rgba StrokeStrong = new(1f, 1f, 1f, 0.16f);
    public static Rgba Text = Rgba.Hex(0xEDF1F7);
    public static Rgba TextDim = Rgba.Hex(0x8C96A8);
    public static Rgba TextFaint = Rgba.Hex(0x566072);

    // Status colours, named for the signals they borrow from.
    public static readonly Rgba Clear = Rgba.Hex(0x3DDC97);
    public static readonly Rgba Caution = Rgba.Hex(0xFFC145);
    public static readonly Rgba Stop = Rgba.Hex(0xFF5A5F);

    /// The accent: buttons, the selection, focus rings.
    public static Rgba Accent = Rgba.Hex(0xFF9F1C);

    public readonly record struct AccentDef(string Name, Rgba Color);

    public static readonly AccentDef[] Accents =
    [
        new("SIGNAL", Rgba.Hex(0xFF9F1C)),
        new("CLEAR", Rgba.Hex(0x3DDC97)),
        new("COBALT", Rgba.Hex(0x4D8DFF)),
        new("CRIMSON", Rgba.Hex(0xFF4F64)),
        new("ORCHID", Rgba.Hex(0xC07BFF)),
        new("BRASS", Rgba.Hex(0xD9B26A)),
    ];

    public static int FindAccent(string name)
    {
        for (var i = 0; i < Accents.Length; i++)
        {
            if (string.Equals(Accents[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    public static void SetAccent(string name)
    {
        Accent = Accents[FindAccent(name)].Color;
        ApplyImGui();
    }

    /// ImGui's own widgets in the same palette and proportions as the rest of the interface.
    public static void ApplyImGui()
    {
        var style = ImGui.GetStyle();
        style.WindowPadding = new float2(12f, 10f);
        style.WindowRounding = 0f;
        style.WindowBorderSize = 1f;
        style.PopupRounding = 4f;
        style.PopupBorderSize = 1f;
        style.FramePadding = new float2(8f, 5f);
        style.FrameRounding = 4f;
        style.ItemSpacing = new float2(6f, 6f);
        style.ItemInnerSpacing = new float2(6f, 4f);
        style.ScrollbarSize = 10f;
        style.ScrollbarRounding = 4f;
        style.GrabMinSize = 10f;
        style.GrabRounding = 3f;
        style.SeparatorTextBorderSize = 1f;
        style.SeparatorTextPadding = new float2(0f, 4f);

        void Set(ImGuiCol slot, Rgba c) => style.Colors[(int)slot] = new float4(c.R, c.G, c.B, c.A);
        var clear = new Rgba(0f, 0f, 0f, 0f);
        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, TextDim);
        Set(ImGuiCol.WindowBg, Surface);
        Set(ImGuiCol.ChildBg, clear);
        Set(ImGuiCol.PopupBg, Rgba.Hex(0x121417));
        Set(ImGuiCol.Border, StrokeStrong);
        Set(ImGuiCol.BorderShadow, clear);
        Set(ImGuiCol.FrameBg, Raised);
        Set(ImGuiCol.FrameBgHovered, RaisedHover);
        Set(ImGuiCol.FrameBgActive, RaisedHover);
        Set(ImGuiCol.TitleBg, Surface);
        Set(ImGuiCol.TitleBgActive, Raised);
        Set(ImGuiCol.TitleBgCollapsed, Surface);
        Set(ImGuiCol.MenuBarBg, Surface);
        Set(ImGuiCol.ScrollbarBg, clear);
        Set(ImGuiCol.ScrollbarGrab, StrokeStrong);
        Set(ImGuiCol.ScrollbarGrabHovered, TextFaint);
        Set(ImGuiCol.ScrollbarGrabActive, TextDim);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, Accent);
        Set(ImGuiCol.Button, Raised);
        Set(ImGuiCol.ButtonHovered, RaisedHover);
        Set(ImGuiCol.ButtonActive, Accent.WithAlpha(0.55f));
        Set(ImGuiCol.Header, Raised);
        Set(ImGuiCol.HeaderHovered, RaisedHover);
        Set(ImGuiCol.HeaderActive, RaisedHover);
        Set(ImGuiCol.Separator, StrokeStrong);
        Set(ImGuiCol.SeparatorHovered, StrokeStrong);
        Set(ImGuiCol.SeparatorActive, Accent);
        Set(ImGuiCol.ResizeGrip, clear);
        Set(ImGuiCol.ResizeGripHovered, StrokeStrong);
        Set(ImGuiCol.ResizeGripActive, Accent);
        Set(ImGuiCol.PlotLines, Accent);
        Set(ImGuiCol.PlotLinesHovered, Text);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotHistogramHovered, Text);
        Set(ImGuiCol.TableHeaderBg, Raised);
        Set(ImGuiCol.TableBorderStrong, StrokeStrong);
        Set(ImGuiCol.TableBorderLight, Stroke);
        Set(ImGuiCol.TableRowBg, clear);
        Set(ImGuiCol.TableRowBgAlt, Stroke);
        Set(ImGuiCol.TextSelectedBg, Accent.WithAlpha(0.3f));
    }

    /// Text drawn on the accent: dark on light accents, white on dark ones.
    public static Rgba OnAccent => Accent.R * 0.299f + Accent.G * 0.587f + Accent.B * 0.114f > 0.55f ? Rgba.Hex(0x0B0D12) : Rgba.White;

    // Fonts. Inter (drop Inter-Regular/Medium/SemiBold/Bold.ttf into Content/Fonts) if it's there, otherwise
    // JetBrains Mono, which ships with the game. Mono is always loaded for figures and readouts.
    public static ImFontPtr Regular { get; private set; }
    public static ImFontPtr Medium { get; private set; }
    public static ImFontPtr Bold { get; private set; }
    public static ImFontPtr Heavy { get; private set; }
    public static ImFontPtr Mono { get; private set; }
    public static ImFontPtr MonoBold { get; private set; }

    /// After the ImGui context exists, before the first frame.
    public static void LoadFonts()
    {
        var io = ImGui.GetIO();

        ImFontPtr Load(string file, ImFontPtr fallback)
        {
            var path = ContentPath.Font(file);
            if (!File.Exists(path))
                return fallback;

            var font = io.Fonts.AddFontFromFileTTF(path, 18f);
            return font.IsNull() ? fallback : font;
        }

        Mono = Load("JetBrainsMono-Regular.ttf", default);
        MonoBold = Load("JetBrainsMono-Bold.ttf", Mono);
        var monoLight = Load("JetBrainsMono-Light.ttf", Mono);
        var monoHeavy = Load("JetBrainsMono-ExtraBold.ttf", MonoBold);

        // Inter comes as a set or not at all; without it the mono family stands in at every weight.
        if (File.Exists(ContentPath.Font("Inter-Regular.ttf")))
        {
            Regular = Load("Inter-Regular.ttf", Mono);
            Medium = Load("Inter-Medium.ttf", Regular);
            Bold = Load("Inter-SemiBold.ttf", Medium);
            Heavy = Load("Inter-Bold.ttf", Bold);
        }
        else
        {
            Regular = monoLight;
            Medium = Mono;
            Bold = MonoBold;
            Heavy = monoHeavy;
        }

        if (Regular.IsNull())
            Log.Warn("No fonts found in Content/Fonts; using ImGui's default");
        else
            io.FontDefault = Regular;
    }

    /// The interface scale: 1 at 1080 lines, so the menus read the same on any screen.
    public static float Scale { get; set; } = 1f;

    public static float S(float v) => v * Scale;
}
