using Highball.Routes;
using Highball.Trains;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// A route as it's loaded: its ground and track opened in the background, then built and streamed round the camera, and
/// the trains' rail graph built again whenever the track changes. Whatever's showing the route (the editor, driving) holds
/// one and asks it for what it needs; editing and the trains go on above it.
public sealed class RouteWorld : IDisposable
{
    private readonly Task<TerrainSource> _opening;
    private NetworkLines? _graphFor;

    /// The furthest out the track's drawn, metres.
    public const float FarestTrack = 6000f;

    public RouteInfo Route { get; }

    /// The ground: null until the route's open.
    public Terrain? Terrain { get; private set; }

    /// The track, roads and lineside: null until the route's open.
    public NetworkMeshes? Network { get; private set; }

    /// What the route's made of: null until it's open.
    public TerrainSource? Source => Terrain?.Source;

    /// Opening the route failed (logged).
    public bool Failed => _opening.IsFaulted;

    /// It's open, read, and in hand.
    public bool Opened => _opening.IsCompletedSuccessfully;

    /// What's been read, once it's open (Opened); throws before.
    public TerrainSource Read => _opening.Result;

    /// Which way every switch is set: kept across the track being laid out again, for the trains and the blades both.
    public SwitchStates Switches { get; } = new();

    /// The trains' rail graph for the track as it is now (RefreshGraph), or null before there's track.
    public RailGraph? Graph { get; private set; }

    /// The rail graph was built from the track's lines as they are now.
    public bool GraphCurrent => Source?.Lines is { } lines && ReferenceEquals(lines, _graphFor);

    public RouteWorld(RouteInfo route)
    {
        Route = route;
        _opening = TerrainSource.Open(route);
    }

    /// Builds the ground and track once the route's been read in the background. True on the frame they're made.
    public bool TryFinishOpening()
    {
        if (Terrain is not null || !_opening.IsCompleted)
            return false;
        if (_opening.IsFaulted)
        {
            Log.Error($"3D Editor: {_opening.Exception?.InnerException?.Message}");
            return false;
        }

        var source = _opening.Result;
        Terrain = new Terrain(source);
        Network = new NetworkMeshes(source) { Switches = Switches };
        Network.SetObjects(source.Objects, [], Route.Projection!);
        return true;
    }

    /// Brings the ground and track up to date round the camera: land out to viewLimit metres (0 for all of it, looking
    /// straight down), at the Quality setting's detail.
    public void Stream(Camera camera, double viewLimit)
    {
        if (Terrain is null)
            return;
        Terrain.DetailFactor = Quality.TerrainDetail;
        Terrain.ViewLimit = viewLimit;
        Network!.FarRadius = viewLimit > 0.0 ? (float)Math.Min(FarestTrack, viewLimit) : FarestTrack;

        using (App.Profiler.Sample("Terrain LOD"))
            Terrain.Update(camera.X, camera.Y, camera.Z);
        using (App.Profiler.Sample("Line chunks"))
            Network.Update(camera.X, camera.Y, camera.Z);
    }

    /// The rail graph built again if the track's lines have changed since it was last built (at first, and after an edit),
    /// with the switches as they're set. True when it was: the trains need putting back on it.
    public bool RefreshGraph()
    {
        if (Source?.Lines is not { } lines || Network is null || ReferenceEquals(lines, _graphFor))
            return false;
        _graphFor = lines;
        Graph = RailGraphBuilder.Build(lines);
        Graph.Reversed = Switches.Reversed;
        return true;
    }

    public void Dispose()
    {
        Terrain?.Dispose();
        Network?.Dispose();
    }
}
