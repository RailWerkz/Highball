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

    public float FieldOfView = 60f * MathF.PI / 180f;
    public float Near = 0.5f;

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
}
