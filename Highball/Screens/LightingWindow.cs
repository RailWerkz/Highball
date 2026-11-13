using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Screens;

/// The lighting test scene's window: the named times, where the sun is, and anything else the screen wants in it. The
/// rest is in Settings.
public static class LightingWindow
{
    public static void Draw(Lighting lighting, Action? extra = null)
    {
        var open = true;
        ImGui.SetNextWindowPos(new float2(90f, 44f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380f, 0f), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Lighting"u8, ref open))
        {
            if (lighting.HasClock)
                Clock(lighting, Settings.Current);
            else
                ImGui.TextDisabled("Skybox: no time of day"u8);

            extra?.Invoke();
        }

        ImGui.End();
    }

    private static void Clock(Lighting lighting, Settings s)
    {
        foreach (var (id, label) in Lighting.Presets)
        {
            if (ImGui.Button(label))
                lighting.Preset(id);
            ImGui.SameLine();
        }

        ImGui.NewLine();

        var zone = SunPosition.TimeZone(lighting.Longitude);
        var sun = lighting.Sun();
        ImGui.Text($"{Lighting.Clock(s.SkyHour)} on {s.SkyYear}-{s.SkyMonth:00}-{s.SkyDay:00}, UTC{zone:+0;-0;+0}");
        ImGui.Text($"Sun {sun.Elevation:0.0} degrees up, bearing {sun.Azimuth:0}");
        if (double.IsNaN(sun.Sunrise))
            ImGui.Text(sun.Elevation > 0f ? "No sunset today"u8 : "No sunrise today"u8);
        else
            ImGui.Text($"Sunrise {Lighting.Clock(sun.Sunrise)}, noon {Lighting.Clock(sun.SolarNoon)}, sunset {Lighting.Clock(sun.Sunset)}");
    }
}
