using RailWerkz.Core.Utils;

namespace Highball.World;

/// A camera as it's seen on a screen of some size: projecting points onto the screen, and picking the ground under one.
public sealed class CameraView(Camera camera)
{
    private float4x4 _viewProjection;
    private (float, float, float, float) _viewProjectionFor = (float.NaN, 0f, 0f, 0f);

    public Camera Camera => camera;

    /// The screen's size in pixels (set each frame as it's drawn).
    public float2 Size { get; set; } = new(1f, 1f);

    /// The camera's view and projection, made again only when the camera or the screen has changed: projecting is done
    /// for thousands of points a frame. Read it on the main thread before any job projects with it.
    public float4x4 ViewProjection
    {
        get
        {
            var state = (camera.Yaw, camera.Pitch, camera.FieldOfView, Size.X / Size.Y);
            if (state != _viewProjectionFor)
            {
                _viewProjection = camera.ViewProjection(state.Item4);
                _viewProjectionFor = state;
            }

            return _viewProjection;
        }
    }

    /// Where a point in the world is on the screen, or null behind the camera.
    public float2? Project(double x, double y, double z)
    {
        var relative = new float3((float)(x - camera.X), (float)(y - camera.Y), (float)(z - camera.Z));
        var clip = MathEx.Project(relative, ViewProjection);
        if (clip.W <= 0.01f)
            return null;
        return new float2((clip.X / clip.W * 0.5f + 0.5f) * Size.X, (0.5f - clip.Y / clip.W * 0.5f) * Size.Y);
    }

    /// Where the ray through a point on the screen first meets the ground: marched out in steps that grow with
    /// distance, then narrowed down by halving.
    public double3? Pick(float2 point, Func<double, double, float> heightAt)
    {
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var aspect = Size.X / Size.Y;
        var nx = point.X / Size.X * 2f - 1f;
        var ny = 1f - point.Y / Size.Y * 2f;
        var dir = MathEx.Normalize(camera.Forward + camera.Right * (nx * tanY * aspect) + camera.Up * (ny * tanY));

        double Above(double t) => camera.Y + dir.Y * t - heightAt(camera.X + dir.X * t, camera.Z + dir.Z * t);

        var previous = 0.0;
        var t = 1.0;
        while (t < 20000.0)
        {
            if (Above(t) < 0.0)
            {
                var lo = previous;
                var hi = t;
                for (var i = 0; i < 24; i++)
                {
                    var mid = (lo + hi) * 0.5;
                    if (Above(mid) < 0.0) hi = mid;
                    else lo = mid;
                }

                return new double3(camera.X + dir.X * hi, camera.Y + dir.Y * hi, camera.Z + dir.Z * hi);
            }

            previous = t;
            t += Math.Max(1.0, t * 0.01);
        }

        return null;
    }
}
