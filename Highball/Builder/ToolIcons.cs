using Brutal.ImGuiApi;
using Brutal.Numerics;
using RailWerkz.UI;

namespace Highball.Builder;

public enum ToolIcon { Look, Edit, Pen, Shape, Lay, Parallel, Switches, Place, Circuits, Terrain, Signal, HandThrow, Crossing, Diamond, Undo, Redo, Road, Bridge, RemoveBridge, UtilityPole, LightPole, Ties }

/// The editor's icons, drawn as lines so they stay sharp at any interface scale. Each is lines (x, y, x, y...) and dots
/// (x, y, radius) in a square from -1 to 1, y down.
public static class ToolIcons
{
    private static readonly Dictionary<ToolIcon, (float[][] Lines, float[][] Dots)> Shapes = new()
    {
        [ToolIcon.Look] = ([[-0.6f, -0.9f, -0.6f, 0.65f, -0.2f, 0.25f, 0.1f, 0.9f, 0.35f, 0.8f, 0.05f, 0.15f, 0.6f, 0.1f, -0.6f, -0.9f]], []),
        [ToolIcon.Edit] = ([[-0.85f, 0.65f, -0.1f, 0.1f, 0.85f, -0.6f]], [[-0.85f, 0.65f, 0.2f], [-0.1f, 0.1f, 0.2f], [0.85f, -0.6f, 0.2f]]),
        [ToolIcon.Pen] = ([[-0.8f, 0.8f, -0.55f, 0.05f, 0.45f, -0.75f, 0.8f, -0.4f, 0f, 0.55f, -0.8f, 0.8f], [-0.8f, 0.8f, -0.1f, 0.1f]], [[-0.1f, 0.1f, 0.12f]]),
        [ToolIcon.Shape] = ([[-0.9f, 0.6f, -0.45f, 0.1f, 0f, -0.1f, 0.45f, 0.1f, 0.9f, 0.6f], [-0.65f, -0.1f, 0.65f, -0.1f]], [[0f, -0.1f, 0.2f], [-0.65f, -0.1f, 0.12f], [0.65f, -0.1f, 0.12f]]),
        [ToolIcon.Lay] = ([[-0.85f, 0.7f, -0.3f, 0.25f, 0.1f, 0.35f, 0.4f, -0.2f, 0.85f, -0.65f]], [[-0.85f, 0.7f, 0.17f], [0.1f, 0.35f, 0.17f], [0.85f, -0.65f, 0.17f]]),
        [ToolIcon.Parallel] = ([[-0.9f, 0.1f, 0.1f, -0.9f], [-0.5f, 0.5f, 0.5f, -0.5f], [-0.1f, 0.9f, 0.9f, -0.1f]], []),
        [ToolIcon.Switches] = ([[-0.1f, 0.9f, -0.1f, -0.9f], [-0.1f, 0.5f, 0.1f, 0.1f, 0.65f, -0.35f, 0.75f, -0.9f]], []),
        [ToolIcon.Place] = ([[0f, 0.9f, -0.6f, -0.05f, -0.55f, -0.5f, -0.2f, -0.85f, 0.2f, -0.85f, 0.55f, -0.5f, 0.6f, -0.05f, 0f, 0.9f]], [[0f, -0.3f, 0.2f]]),
        [ToolIcon.Circuits] = ([[0.2f, -0.9f, -0.5f, 0.1f, 0.05f, 0.1f, -0.2f, 0.9f, 0.5f, -0.15f, -0.05f, -0.15f, 0.2f, -0.9f]], []),
        [ToolIcon.Terrain] = ([[-0.95f, 0.7f, -0.3f, -0.45f, 0.05f, 0.1f, 0.35f, -0.2f, 0.95f, 0.7f, -0.95f, 0.7f]], []),
        [ToolIcon.Signal] = ([[-0.3f, -0.9f, 0.3f, -0.9f, 0.3f, 0.3f, -0.3f, 0.3f, -0.3f, -0.9f], [0f, 0.3f, 0f, 0.9f], [-0.45f, 0.9f, 0.45f, 0.9f]], [[0f, -0.55f, 0.15f], [0f, -0.05f, 0.15f]]),
        [ToolIcon.HandThrow] = ([[-0.7f, 0.7f, 0.7f, 0.7f], [0f, 0.7f, 0.45f, -0.6f]], [[0.45f, -0.7f, 0.2f], [0f, 0.7f, 0.15f]]),
        [ToolIcon.Crossing] = ([[-0.85f, -0.35f, 0.85f, -0.35f], [-0.85f, 0.35f, 0.85f, 0.35f], [-0.4f, -0.9f, -0.4f, 0.9f], [0.4f, -0.9f, 0.4f, 0.9f]], []),
        [ToolIcon.Road] = ([[-0.55f, 0.9f, -0.2f, -0.9f], [0.55f, 0.9f, 0.2f, -0.9f], [0f, 0.8f, 0f, 0.4f], [0f, 0.1f, 0f, -0.25f], [0f, -0.55f, 0f, -0.85f]], []),
        [ToolIcon.Ties] = ([[-0.9f, -0.7f, 0.9f, -0.7f], [-0.9f, -0.25f, 0.9f, -0.25f], [-0.9f, 0.2f, 0.9f, 0.2f], [-0.9f, 0.65f, 0.9f, 0.65f]], []),
        [ToolIcon.UtilityPole] = ([[0f, -0.9f, 0f, 0.9f], [-0.7f, -0.55f, 0.7f, -0.55f], [-0.5f, -0.2f, 0.5f, -0.2f], [-0.35f, -0.55f, 0f, -0.25f, 0.35f, -0.55f]], [[-0.7f, -0.72f, 0.1f], [0.7f, -0.72f, 0.1f]]),
        [ToolIcon.LightPole] = ([[-0.25f, 0.9f, -0.25f, -0.4f, -0.05f, -0.75f, 0.55f, -0.8f], [-0.55f, 0.9f, 0.05f, 0.9f]], [[0.6f, -0.65f, 0.17f]]),
        [ToolIcon.Bridge] = ([[-0.95f, -0.2f, 0.95f, -0.2f], [-0.95f, 0.05f, 0.95f, 0.05f], [-0.45f, 0.05f, -0.45f, 0.8f], [0.45f, 0.05f, 0.45f, 0.8f], [-0.95f, -0.2f, -0.95f, -0.55f], [0.95f, -0.2f, 0.95f, -0.55f]], []),
        [ToolIcon.RemoveBridge] = ([[-0.95f, 0.1f, 0.95f, 0.1f], [-0.45f, 0.1f, -0.45f, 0.8f], [0.45f, 0.1f, 0.45f, 0.8f], [-0.5f, -0.85f, 0.5f, -0.2f], [0.5f, -0.85f, -0.5f, -0.2f]], []),
        [ToolIcon.Diamond] = ([[0f, -0.85f, 0.85f, 0f, 0f, 0.85f, -0.85f, 0f, 0f, -0.85f]], []),
        [ToolIcon.Undo] = ([[-0.8f, -0.25f, 0.3f, -0.25f, 0.75f, 0f, 0.85f, 0.35f, 0.65f, 0.7f, 0.25f, 0.85f, -0.3f, 0.85f], [-0.35f, -0.7f, -0.8f, -0.25f, -0.35f, 0.2f]], []),
        [ToolIcon.Redo] = ([[0.8f, -0.25f, -0.3f, -0.25f, -0.75f, 0f, -0.85f, 0.35f, -0.65f, 0.7f, -0.25f, 0.85f, 0.3f, 0.85f], [0.35f, -0.7f, 0.8f, -0.25f, 0.35f, 0.2f]], []),
    };

    public static void Draw(ImDrawListPtr draw, ToolIcon icon, float2 center, float half, Rgba color, float thickness)
    {
        var (lines, dots) = Shapes[icon];
        foreach (var line in lines)
        {
            for (var i = 0; i + 3 < line.Length; i += 2)
                draw.AddLine(center + new float2(line[i], line[i + 1]) * half, center + new float2(line[i + 2], line[i + 3]) * half, color, thickness);
        }

        foreach (var dot in dots)
            draw.AddCircleFilled(center + new float2(dot[0], dot[1]) * half, dot[2] * half, color, 12);
    }
}
