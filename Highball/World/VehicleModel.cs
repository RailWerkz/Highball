using RailWerkz.Core.Logging;
using RailWerkz.Vulkan;

namespace Highball.World;

/// A vehicle's model in two: its trucks with their wheelsets, which stay on the rail, and everything they carry (body,
/// doors, couplers, load), which settles and rolls on the springs. An assembled model names its trucks Truck_A and
/// Truck_B (or Truck_Front and Truck_Rear) with the wheelsets under them; one that doesn't is all body, drawn whole.
public sealed class VehicleModel : IDisposable
{
    private const string TruckNodes = "Truck_*";

    public RenderModel Body { get; }

    /// Null when the model has no trucks of its own to keep apart: then Body is the whole thing and shouldn't be moved.
    public RenderModel? Trucks { get; }

    private VehicleModel(RenderModel body, RenderModel? trucks)
    {
        Body = body;
        Trucks = trucks;
    }

    /// Throws if the file can't be read at all.
    public static VehicleModel Load(string relativePath)
    {
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
        return new VehicleModel(new RenderModel(body), trucks is null ? null : new RenderModel(trucks));
    }

    public void Dispose()
    {
        Body.Dispose();
        Trucks?.Dispose();
    }
}
