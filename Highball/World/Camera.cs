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

    /// What the camera sees this frame, for leaving out what's wholly outside it before it's drawn.
    public ViewCone View(float aspect) => new(this, aspect);
}

/// The four sides of a camera's view and the plane through it (nothing is left out for being far: whatever chooses what to
/// draw has its own reach). Points are relative to the camera.
public readonly struct ViewCone
{
    private readonly float3 _forward, _right, _up;
    private readonly float _tanX, _tanY, _slackX, _slackY;

    public ViewCone(Camera camera, float aspect)
    {
        (_forward, _right, _up) = (camera.Forward, camera.Right, camera.Up);
        _tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        _tanX = _tanY * aspect;
        // How much further out a side reaches, measured across the view, for a sphere's radius to clear it.
        _slackX = MathF.Sqrt(1f + _tanX * _tanX);
        _slackY = MathF.Sqrt(1f + _tanY * _tanY);
    }

    /// Whether any of the sphere round centre (relative to the camera) might be in view.
    public bool Sees(float3 centre, float radius)
    {
        var along = MathEx.Dot(centre, _forward);
        return along >= -radius
            && MathF.Abs(MathEx.Dot(centre, _right)) <= along * _tanX + radius * _slackX
            && MathF.Abs(MathEx.Dot(centre, _up)) <= along * _tanY + radius * _slackY;
    }
}
