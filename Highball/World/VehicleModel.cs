using RailWerkz.Core.Logging;
using RailWerkz.Vulkan;

namespace Highball.World;

/// One truck of a vehicle's model, with the place on the plan it swivels about (its middle, in the model's axes).
public sealed record TruckPart(RenderModel Model, float3 Pivot);

/// A vehicle's model in parts: its trucks with their wheelsets, which stay on the rail and swivel to follow it, and
/// everything they carry (body, doors, couplers, load), which settles and rolls on the springs. An assembled model names
/// its trucks Truck_A and Truck_B (or Truck_Front and Truck_Rear) with the wheelsets under them; one that doesn't is all
/// body, drawn whole.
public sealed class VehicleModel : IDisposable
{
    private const string TruckNodes = "Truck_*";

    public RenderModel Body { get; }

    /// The trucks, each on its own so it can swivel: the front one (towards the model's +z) and the rear. Null when the
    /// model's trucks can't be told apart (then Trucks has both together) or it has none.
    public TruckPart? FrontTruck { get; }
    public TruckPart? RearTruck { get; }

    /// Both trucks as one, for a model whose trucks aren't named one by one. Null when they're apart (FrontTruck and
    /// RearTruck) or there are none: then Body is the whole thing and shouldn't be moved.
    public RenderModel? Trucks { get; }

    /// It has trucks apart from its body, whether one by one or together.
    public bool HasTrucks => Trucks is not null || FrontTruck is not null;

    private VehicleModel(RenderModel body, RenderModel? trucks, TruckPart? front, TruckPart? rear)
    {
        Body = body;
        Trucks = trucks;
        FrontTruck = front;
        RearTruck = rear;
    }

    /// Where its lower-detail versions take over, in metres from the camera, and the folder each is in beside the model
    /// (Tools/Blender/lods.py makes them).
    private static readonly (string Folder, float From)[] Lods = [("LOD1", 60f), ("LOD2", 220f)];

    /// Throws if the file can't be read at all. Its lower-detail versions are added where they're there.
    public static VehicleModel Load(string relativePath)
    {
        var model = LoadOne(relativePath);
        foreach (var (folder, from) in Lods)
        {
            var path = Path.Combine(Path.GetDirectoryName(relativePath) ?? "", folder, Path.GetFileName(relativePath)).Replace('\\', '/');
            if (!File.Exists(RailWerkz.Core.IO.ContentPath.Model(path)))
                continue;
            try
            {
                var lod = LoadOne(path);
                // (Split the same way or not at all: the body's drawn on the springs above the trucks, so they must match.)
                if ((lod.Trucks is null) != (model.Trucks is null) || (lod.FrontTruck is null) != (model.FrontTruck is null))
                {
                    lod.Dispose();
                    continue;
                }

                model.Body.AddLod(lod.Body, from);
                if (lod.Trucks is { } trucks)
                    model.Trucks!.AddLod(trucks, from);
                if (lod.FrontTruck is { } front)
                    model.FrontTruck!.Model.AddLod(front.Model, from);
                if (lod.RearTruck is { } rear)
                    model.RearTruck!.Model.AddLod(rear.Model, from);
            }
            catch (Exception e)
            {
                Log.Warn($"Vehicle model {path}: unreadable ({e.Message}); the full model's drawn there instead");
            }
        }

        return model;
    }

    private static VehicleModel LoadOne(string relativePath)
    {
        // Each truck on its own, if they're named so: the one further towards +z is the front.
        var a = LoadPart(relativePath, "Truck_Front,Truck_A");
        var b = a is null ? null : LoadPart(relativePath, "Truck_Rear,Truck_B");
        if (a is not null && b is not null)
        {
            var (front, rear) = a.Center.Z >= b.Center.Z ? (a, b) : (b, a);
            var rest = Model.Load(relativePath, skipNode: TruckNodes);
            return new VehicleModel(new RenderModel(rest), null, Part(front), Part(rear));
        }

        a?.Dispose();

        Model? trucks = null;
        try
        {
            trucks = Model.Load(relativePath, nodeName: TruckNodes);
        }
        catch (InvalidDataException)
        {
            // No trucks named so: drawn whole.
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicle model {relativePath}: trucks unreadable ({e.Message}); drawn whole");
        }

        var body = trucks is null ? Model.Load(relativePath) : Model.Load(relativePath, skipNode: TruckNodes);
        return new VehicleModel(new RenderModel(body), trucks is null ? null : new RenderModel(trucks), null, null);
    }

    /// A named part of the file, or null if it hasn't one.
    private static Model? LoadPart(string relativePath, string nodes)
    {
        try
        {
            return Model.Load(relativePath, nodeName: nodes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static TruckPart Part(Model truck) => new(new RenderModel(truck), new float3(truck.Center.X, 0f, truck.Center.Z));

    public void Dispose()
    {
        Body.Dispose();
        Trucks?.Dispose();
        FrontTruck?.Model.Dispose();
        RearTruck?.Model.Dispose();
    }
}
