using Highball.Routes;
using RailWerkz.UI;

namespace Highball.Builder;

/// Each rail class's colour and name, the same on the Planner's map and in the 2D and 3D Editors' class overlay.
/// Each class has its own hue, kept clear of the Planner's lidar blue and standard amber, and of the grey the roads are drawn in.
public static class RailColours
{
    public static Rgba Of(int cls) => (RailClass)cls switch
    {
        RailClass.Main => Rgba.Hex(0xFF4D6D),
        RailClass.Branch => Rgba.Hex(0xC77DFF),
        RailClass.Industrial => Rgba.Hex(0x2EE6A8),
        RailClass.Yard => Rgba.Hex(0xFF8C42),
        RailClass.Transit => Rgba.Hex(0x4DD7FF),
        RailClass.NarrowGauge => Rgba.Hex(0xD4E157),
        _ => Rgba.Hex(0x8D6E63),
    };

    public static string Name(int cls) => (RailClass)cls switch
    {
        RailClass.NarrowGauge => "Narrow Gauge",
        var c => c.ToString(),
    };
}
