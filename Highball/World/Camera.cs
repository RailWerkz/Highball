using RailWerkz.Core.Utils;

namespace Highball.World;

/// A camera in world metres (x east, y up, z south from the route origin). The position is kept in doubles and the
/// world is drawn relative to it, so nothing shakes however far the route reaches.
public sealed class Camera
{
    public double X;
    public double Y;
    public double Z;

    /// Radians: yaw 0 looks north, increasing clockwise seen from above; pitch up is positive.
    public float Yaw;
    public float Pitch;

    /// How far the lens is zoomed in: 1 is the setting's field of view, 2 shows things twice the size.
    public float Zoom = 1f;

    /// Radians, from the setting and the zoom.
    public float FieldOfView => 2f * MathF.Atan(MathF.Tan(RailWerkz.Core.Settings.Settings.Current.FieldOfView * MathF.PI / 360f) / Zoom);
    public float Near = 0.5f;

    /// How far off, in clip space, the world is drawn this frame for temporal anti-aliasing.
    public float2 Jitter;

    public float3 Forward
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw);
            var (sp, cp) = MathF.SinCos(Pitch);
            return new float3(sy * cp, sp, -cy * cp);
        }
    }

    public float3 Right
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw);
            return new float3(cy, 0f, sy);
        }
    }

    public float3 Up => MathEx.Cross(Right, Forward);

    /// View and projection with the camera at the origin.
    public float4x4 ViewProjection(float aspect) =>
        MathEx.LookTo(default, Forward, new float3(0f, 1f, 0f)) * MathEx.Perspective(FieldOfView, aspect, Near);

    /// The same, as the world's drawn this frame. Whatever has to line up from frame to frame uses ViewProjection instead.
    public float4x4 JitteredViewProjection(float aspect)
    {
        var values = ViewProjection(aspect).AsReadOnlySpan().ToArray();
        for (var row = 0; row < 4; row++)
        {
            values[row * 4] += Jitter.X * values[row * 4 + 3];
            values[row * 4 + 1] += Jitter.Y * values[row * 4 + 3];
        }

        return new float4x4(values);
    }
}
