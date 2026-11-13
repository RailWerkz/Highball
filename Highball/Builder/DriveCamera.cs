using Brutal.GlfwApi;
using Highball.App;
using Highball.World;

namespace Highball.Builder;

/// A driver's cameras on the train: the engineer's seat in the lead unit's cab, and Orbit's starting place behind it. Both
/// follow the lead unit as it's drawn this frame (TrainPanel.Drawn), so they're as steady as Follow.
public sealed class DriveCamera
{
    /// The engineer's eye: over the rail tops, back from the lead unit's front end, and right of its middle.
    private const double CabHeight = 3.9, CabBack = 3.0, CabRight = 0.6;

    private float _cabYaw, _cabPitch;

    /// The lead unit as it's drawn this frame: its cab end's way and the trucks under it.
    private static (double Dx, double Dz, (double X, double Y, double Z) Front, (double X, double Y, double Z) Rear, Trains.TrainCar Car)? Lead(Trains.Train train, TrainPanel trains)
    {
        var index = Math.Clamp(train.LeadUnit, 0, train.Cars.Count - 1);
        if (trains.Drawn(index) is not { } pose)
            return null;
        var car = train.Cars[index];
        // Whichever end its cab faces.
        var (front, rear) = train.CabSense < 0 ? (pose.Rear, pose.Front) : (pose.Front, pose.Rear);
        var (dx, dz) = (front.X - rear.X, front.Z - rear.Z);
        var flat = Math.Sqrt(dx * dx + dz * dz);
        return flat < 1e-6 ? null : (dx / flat, dz / flat, front, rear, car);
    }

    /// Orbit's starting place: behind the lead unit and above it, looking along the train. False if the lead unit isn't
    /// drawn yet (the camera's left where it is).
    public bool PlaceOrbit(Camera camera, Trains.Train train, TrainPanel trains, Func<double, double, float> heightAt)
    {
        if (Lead(train, trains) is not var (dx, dz, front, rear, _))
            return false;
        var (cx, cy, cz) = ((front.X + rear.X) * 0.5, (front.Y + rear.Y) * 0.5, (front.Z + rear.Z) * 0.5);
        camera.X = cx - dx * 30.0 - dz * 12.0;
        camera.Z = cz - dz * 30.0 + dx * 12.0;
        camera.Y = Math.Max(cy + 12.0, heightAt(camera.X, camera.Z) + 2.0);
        var (tx, ty, tz) = (cx - camera.X, cy + 2.0 - camera.Y, cz - camera.Z);
        camera.Yaw = (float)Math.Atan2(tx, -tz);
        camera.Pitch = (float)Math.Atan2(ty, Math.Sqrt(tx * tx + tz * tz));
        return true;
    }

    /// From the engineer's seat: the right button looks round (eye.Looking, held as the editor's is), the wheel zooms the
    /// lens. False if the lead unit isn't drawn yet.
    public bool Cab(Camera camera, Trains.Train train, TrainPanel trains, bool blocked, EditorCamera eye)
    {
        if (Lead(train, trains) is not var (dx, dz, front, rear, car))
            return false;
        var flat = Math.Sqrt((front.X - rear.X) * (front.X - rear.X) + (front.Z - rear.Z) * (front.Z - rear.Z));
        var heading = (float)Math.Atan2(dx, -dz);
        var grade = (float)Math.Atan2(front.Y - rear.Y, flat);
        // The cab end is the truck's overhang on from it.
        var overhang = train.CabSense < 0 ? car.TrailingOverhang : car.LeadingOverhang;
        var end = (X: front.X + dx * overhang, Y: front.Y, Z: front.Z + dz * overhang);

        eye.Looking = Input.Mouse(GlfwMouseButton.Right) && (eye.Looking || !blocked);
        if (eye.Looking)
        {
            var look = Input.MouseDelta * (0.0025f / camera.Zoom);
            _cabYaw = Math.Clamp(_cabYaw + look.X, -2.6f, 2.6f);
            _cabPitch = Math.Clamp(_cabPitch - look.Y, -1.2f, 1.0f);
        }

        if (Input.Scroll != 0f && !blocked)
            camera.Zoom = Math.Clamp(camera.Zoom * MathF.Pow(1.25f, Input.Scroll), 1f, EditorCamera.MaxLensZoom);
        camera.X = end.X - dx * CabBack - dz * CabRight;
        camera.Z = end.Z - dz * CabBack + dx * CabRight;
        camera.Y = end.Y + CabHeight;
        camera.Yaw = heading + _cabYaw;
        camera.Pitch = grade + _cabPitch;
        return true;
    }
}
