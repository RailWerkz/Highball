using Brutal.GlfwApi;
using Highball.App;
using Highball.World;

namespace Highball.Builder;

/// The Route Builder's own camera. In 3D it's flown: the right button looks round, WASD and Q/E move, the wheel sets the
/// speed and Alt+wheel zooms the lens. Seen from above it's panned: drag with the right or middle button (or WASD), and the
/// wheel zooms about the pointer.
public sealed class EditorCamera
{
    public const float MinSpeed = 2f;
    public const float MaxSpeed = 2000f;
    public const float MaxLensZoom = 10f;
    public const double MinTopDownHeight = 20.0;
    public const double MaxTopDownHeight = 40000.0;

    public Camera Camera { get; } = new();

    /// How fast it flies, metres a second.
    public float Speed { get; set; } = 60f;

    /// Looking round with the right button held: the pointer's captured.
    public bool Looking { get; set; }

    /// How quickly the view catches up with right-button looking, per second (exponential, frame-rate independent): the
    /// mouse moves a target and the view eases toward it. Higher is snappier; ~30 closes 63% of the gap in 33 ms.
    public const float LookSharpness = 30f;

    /// How quickly Alt+wheel lens zoom catches up with the wheel, per second, eased in log space so each notch feels the
    /// same at any zoom.
    public const float ZoomSharpness = 14f;

    /// How much one wheel notch changes the lens zoom (and the fly speed).
    public const float WheelStep = 1.25f;

    private float2? _panFrom;

    // Where looking and the lens are headed, and what was last put on the camera, so that anything else moving the camera
    // (a jump to a place, the 2D view) is noticed and taken as the new target rather than fought.
    private bool _haveAim;
    private float _yawTarget, _pitchTarget, _zoomTarget;
    private float _yawShown, _pitchShown, _zoomShown;

    public void StopLooking()
    {
        Looking = false;
        _panFrom = null;
    }

    /// Flown in 3D. Only starting to look is up to where the pointer is (blocked: over a window); once looking, it's held
    /// until the button's let go. wheelTurns: the wheel's taken by a tool. It stays above the ground.
    public void Fly(float dt, bool blocked, bool wheelTurns, Func<double, double, float> heightAt)
    {
        var camera = Camera;
        if (!_haveAim || camera.Yaw != _yawShown || camera.Pitch != _pitchShown || camera.Zoom != _zoomShown)
        {
            _yawTarget = camera.Yaw;
            _pitchTarget = camera.Pitch;
            _zoomTarget = camera.Zoom;
            _haveAim = true;
        }

        Looking = Input.Mouse(GlfwMouseButton.Right) && (Looking || !blocked);
        if (Looking)
        {
            // (Slower zoomed in, so that things move across the screen at the same rate.)
            var sensitivity = 0.0025f / _zoomTarget;
            _yawTarget += Input.MouseDelta.X * sensitivity;
            _pitchTarget = Math.Clamp(_pitchTarget - Input.MouseDelta.Y * sensitivity, -1.55f, 1.55f);
        }

        if (Input.Scroll != 0f && !blocked && !wheelTurns)
        {
            if (Input.Key(GlfwKey.LeftAlt))
                _zoomTarget = Math.Clamp(_zoomTarget * MathF.Pow(WheelStep, Input.Scroll), 1f, MaxLensZoom);
            else
                Speed = Math.Clamp(Speed * MathF.Pow(WheelStep, Input.Scroll), MinSpeed, MaxSpeed);
        }

        // Ease toward the targets: the fraction of the gap left after dt is exp(-sharpness * dt).
        var lookLeft = MathF.Exp(-LookSharpness * dt);
        camera.Yaw = Settle(_yawTarget + (camera.Yaw - _yawTarget) * lookLeft, _yawTarget, 1e-5f);
        camera.Pitch = Settle(_pitchTarget + (camera.Pitch - _pitchTarget) * lookLeft, _pitchTarget, 1e-5f);
        camera.Zoom = Settle(_zoomTarget * MathF.Pow(camera.Zoom / _zoomTarget, MathF.Exp(-ZoomSharpness * dt)), _zoomTarget, 1e-4f);
        _yawShown = camera.Yaw;
        _pitchShown = camera.Pitch;
        _zoomShown = camera.Zoom;

        var forward = camera.Forward;
        var right = camera.Right;
        var move = default(float3);
        if (Input.Key(GlfwKey.W)) move += forward;
        if (Input.Key(GlfwKey.S)) move -= forward;
        if (Input.Key(GlfwKey.D)) move += right;
        if (Input.Key(GlfwKey.A)) move -= right;
        if (Input.Key(GlfwKey.E)) move.Y += 1f;
        if (Input.Key(GlfwKey.Q)) move.Y -= 1f;

        var length = MathF.Sqrt(move.X * move.X + move.Y * move.Y + move.Z * move.Z);
        if (length > 0f)
        {
            var speed = Speed * (Input.Key(GlfwKey.LeftShift) ? 4f : 1f) * dt / length;
            camera.X += move.X * speed;
            camera.Y += move.Y * speed;
            camera.Z += move.Z * speed;
        }

        camera.Y = Math.Max(camera.Y, heightAt(camera.X, camera.Z) + 1.8);
    }

    private static float Settle(float value, float target, float epsilon) =>
        MathF.Abs(value - target) < epsilon ? target : value;

    /// Seen from above, on a screen this size (pixels).
    public void Pan(float dt, bool blocked, bool wheelTurns, float2 screen, Func<double, double, float> heightAt)
    {
        var camera = Camera;
        Looking = false;
        var above = Math.Max(MinTopDownHeight, camera.Y - heightAt(camera.X, camera.Z));
        var metresPerPixel = 2.0 * above * Math.Tan(camera.FieldOfView * 0.5) / screen.Y;
        var mouse = Input.MousePosition;

        var dragging = Input.Mouse(GlfwMouseButton.Right) || Input.Mouse(GlfwMouseButton.Middle);
        if (!dragging)
            _panFrom = null;
        else if (_panFrom is null && !blocked)
            _panFrom = mouse;
        if (_panFrom is { } from)
        {
            camera.X -= (mouse.X - from.X) * metresPerPixel;
            camera.Z -= (mouse.Y - from.Y) * metresPerPixel;
            _panFrom = mouse;
        }

        var speed = above * (Input.Key(GlfwKey.LeftShift) ? 4.0 : 1.0) * dt;
        if (Input.Key(GlfwKey.W)) camera.Z -= speed;
        if (Input.Key(GlfwKey.S)) camera.Z += speed;
        if (Input.Key(GlfwKey.A)) camera.X -= speed;
        if (Input.Key(GlfwKey.D)) camera.X += speed;

        if (Input.Scroll != 0f && !blocked && !wheelTurns)
        {
            // The ground under the pointer stays under it.
            var zoomed = Math.Clamp(above * Math.Pow(0.8, Input.Scroll), MinTopDownHeight, MaxTopDownHeight);
            var shrink = 1.0 - zoomed / above;
            camera.X += (mouse.X - screen.X * 0.5) * metresPerPixel * shrink;
            camera.Z += (mouse.Y - screen.Y * 0.5) * metresPerPixel * shrink;
            above = zoomed;
            camera.Y = heightAt(camera.X, camera.Z) + above;
        }

        camera.Y = Math.Max(camera.Y, heightAt(camera.X, camera.Z) + MinTopDownHeight);
    }
}
