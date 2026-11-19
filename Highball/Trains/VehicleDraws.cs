using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Utils;

namespace Highball.Trains;

/// Vehicles' models, each loaded the first time it's asked for (with its lower-detail versions, see VehicleModel), and
/// let go of together.
public sealed class VehicleModels(string who) : IDisposable
{
    private readonly Dictionary<string, VehicleModel?> _models = [];

    /// Null if there's no path or the file can't be read (logged once, as who).
    public VehicleModel? Get(string? path)
    {
        if (path is null)
            return null;
        if (_models.TryGetValue(path, out var model))
            return model;
        try
        {
            model = VehicleModel.Load(path);
        }
        catch (Exception e)
        {
            Log.Warn($"{who}: model {path} unreadable ({e.Message})");
            model = null;
        }

        return _models[path] = model;
    }

    public void Dispose()
    {
        foreach (var model in _models.Values)
            model?.Dispose();
        _models.Clear();
    }
}

/// Drawing vehicles that aren't moving (on display, in the consist editor).
public static class VehicleDraws
{
    /// A vehicle standing at a place, turned yaw about the vertical: its trucks on the rail and its body settled sag metres
    /// on its springs, or all of it as one if it has no trucks of its own.
    public static void AddStanding(List<ModelDraw> into, VehicleModel model, float3 at, float yaw, float sag)
    {
        if (model.HasTrucks)
        {
            if (model.Trucks is { } trucks)
                into.Add(new ModelDraw(trucks, at, Yaw: yaw));
            if (model.FrontTruck is { } front)
                into.Add(new ModelDraw(front.Model, at, Yaw: yaw));
            if (model.RearTruck is { } rear)
                into.Add(new ModelDraw(rear.Model, at, Yaw: yaw));
            into.Add(new ModelDraw(model.Body, at + new float3(0f, -sag, 0f), Yaw: yaw));
        }
        else
        {
            into.Add(new ModelDraw(model.Body, at, Yaw: yaw));
        }

        // Its couplers straight, on the body.
        var body = model.HasTrucks ? at + new float3(0f, -sag, 0f) : at;
        if (model.FrontCoupler is { } frontCoupler)
            into.Add(new ModelDraw(frontCoupler.Model, body, Yaw: yaw));
        if (model.RearCoupler is { } rearCoupler)
            into.Add(new ModelDraw(rearCoupler.Model, body, Yaw: yaw));
    }
}

/// A locomotive's lamps as lights in the world, the same wherever one's drawn.
public static class LocoLamps
{
    /// One lamp lit to level (0 to 1), on a vehicle turned rotation and standing at at, aimed a little down the track. A
    /// headlight's beam is shared between the lamps at its end (heads of them); ditch lights are each their own.
    public static LocalLight Light(VehicleLamp lamp, int heads, float level, float4 rotation, double3 at)
    {
        var aim = MathEx.Normalize(lamp.Facing + new float3(0f, lamp.Ditch ? -0.08f : -0.04f, 0f));
        var p = Builder.VehicleOverlay.Rotate(rotation, lamp.Position);
        var f = Builder.VehicleOverlay.Rotate(rotation, aim);
        var (x, y, z) = (at.X + p.X, at.Y + p.Y, at.Z + p.Z);
        return lamp.Ditch
            ? new LocalLight(x, y, z, 120f, Warm * 350f * level, f, 0.97f, 0.9f, 0.2f, Warm * 4f * level)
            : new LocalLight(x, y, z, 350f, Warm * 2000f * (level / Math.Max(1, heads)), f, 0.985f, 0.95f, 0.3f, Warm * 5f * level);
    }

    /// A sealed-beam incandescent lamp's warm white (about 2850 K), linear: what most locomotives' headlights and ditch lights are.
    private static readonly float3 Warm = new(1f, 0.74f, 0.47f);

    /// How many headlight lamps share the beam at that end of a vehicle.
    public static int Heads(VehicleDef def, bool front) => def.Lamps.Count(l => !l.Ditch && l.Front == front);
}
