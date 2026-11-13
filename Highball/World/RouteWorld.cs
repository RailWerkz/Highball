using System.Globalization;
using Highball.App;
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

    /// The stock on the track and what happens between it.
    public Railway Railway { get; } = new();

    /// The trains' rail graph for the track as it is now (RefreshGraph), or null before there's track.
    public RailGraph? Graph { get; private set; }

    /// The rail graph was built from the track's lines as they are now.
    public bool GraphCurrent => Source?.Lines is { } lines && ReferenceEquals(lines, _graphFor);

    public RouteWorld(RouteInfo route)
    {
        Route = route;
        _opening = TerrainSource.Open(route);
        Railway.Switches = Switches;
    }

    /// Runs the trains on for this frame, paced (below); every cut's stepped, but one left standing a while sleeps until
    /// it's driven, run into, or is awake (the one looked at). Returns how far on it ran them, seconds.
    public double Step(float dt, Railway.Cut? awake)
    {
        var step = Paced(dt);
        Railway.Step(step, awake);
        return step;
    }

    // Frame times as measured wobble from one frame to the next by more than the screen's do: the picture is shown at the
    // display's own beat, so running the train on by each frame's own time makes it move unevenly on the screen. It's run on
    // by a running average instead, and whatever that gets ahead of or behind real time is paid back a little each frame,
    // so it never drifts from the clock. A hitch past a quarter of a second is let through at once.
    private double _paceAverage = -1.0;
    private double _paceOwed;

    private double Paced(double dt)
    {
        if (dt <= 0.0)
            return 0.0;
        _paceAverage = _paceAverage < 0.0 ? dt : _paceAverage + (dt - _paceAverage) * 0.1;
        _paceOwed += dt - _paceAverage;
        if (Math.Abs(_paceOwed) > 0.25)
        {
            var hitch = _paceOwed;
            _paceOwed = 0.0;
            return Math.Max(0.0, _paceAverage + hitch);
        }

        var repay = _paceOwed * 0.05;
        _paceOwed -= repay;
        return Math.Max(0.0, _paceAverage + repay);
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

    /// Throws the switch nearest a point, if there's one within so many metres. Returns it.
    public Turnout? Throw(double x, double z, double within) => Network?.Throw(x, z, within);

    /// --throw: the switch nearest a place thrown once there's track, and what was found logged (the nearest few when
    /// none was).
    public void ThrowScripted()
    {
        if (DevTools.ThrowAt is not { } dev || Network is not { Turnouts: > 0 } network)
            return;
        DevTools.ThrowAt = null;
        var projection = Route.Projection!;
        var p = projection.Forward(new GeoPoint(dev.Lat, dev.Lon));
        var thrown = network.Throw(p.East, -p.North, 200.0);
        Log.Info(thrown is null ? "Throw: no switch near" : $"Throw: switch at node {thrown.Node}, frog {thrown.FrogNormal:0.0} m out, blades {thrown.BladeLength:0.0} m");
        if (thrown is null)
            foreach (var t in Source!.Lines!.Turnouts.OrderBy(t => (t.X - p.East) * (t.X - p.East) + (t.Z + p.North) * (t.Z + p.North)).Take(5))
            {
                var g = projection.Inverse(new RoutePoint(t.X, -t.Z));
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch near: {g.Lat:0.000000} {g.Lon:0.000000}, frog {t.FrogNormal:0.0} m, side {t.Side}"));
            }
    }

    public void Dispose()
    {
        Terrain?.Dispose();
        Network?.Dispose();
    }
}
