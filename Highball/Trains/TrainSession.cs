using System.Globalization;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.Builder;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// The views a driver switches between: from the lead unit's cab, orbiting the lead unit, or flying free.
public enum DriveView { Cab, Orbit, Free }

/// Trains running on a route, whatever's showing it (the editor, driving): the train window, placing and picking stock,
/// stepping the railway, the multiplayer session, the cameras that go with the train, its sound, and the route's saved
/// stock put back and saved again.
public sealed class TrainSession : IDisposable
{
    private readonly RouteWorld _world;
    private readonly RouteInfo _route;
    private readonly Camera _camera;
    private readonly DriveCamera _drive = new();
    private (double X, double Y, double Z)? _followed;
    private DriveView _view = DriveView.Free;
    private bool _placeOrbit;

    /// The route's saved stock has been put back on the track (once, on the first rail graph). Until then nothing's saved
    /// over it, so a save before the track's ready doesn't lose it.
    private bool _stockRestored;

    /// The train window: placing, building, driving and coupling stock.
    public TrainPanel Panel { get; }

    /// The multiplayer session this is part of, if it is.
    public Multiplayer.NetPlay? Net { get; set; }

    public TrainSession(RouteWorld world, Camera camera)
    {
        _world = world;
        _route = world.Route;
        _camera = camera;
        Panel = new TrainPanel(world.Railway);
    }

    /// The camera: Orbit is the Follow camera (flown as ever, carried along by the train as it's drawn), Free is the same
    /// without following, Cab sits in the lead unit's cab.
    public DriveView View
    {
        get => _view;
        set
        {
            var entering = value == DriveView.Orbit && (_view != DriveView.Orbit || !Panel.Follow);
            _view = value;
            Panel.Follow = value == DriveView.Orbit;
            if (value != DriveView.Cab)
                _camera.Zoom = 1f;
            if (entering)
                _placeOrbit = true;
        }
    }

    /// In the cab of a train: the camera's the cab's, not flown.
    public bool InCab => View == DriveView.Cab && Focus is not null;

    /// The train the cameras are on: the one looked at (which Follow tracks; taking control looks at it), or else the one
    /// driven.
    public Train? Focus => Panel.Train ?? Panel.Controlled;

    /// Tells the crew something (shown in the train window).
    public void Notify(string text) => Panel.Notify(text);

    /// The rail graph's been built again (RouteWorld.RefreshGraph): the trains put back on it, the saved stock the first
    /// time, and the multiplayer session told.
    public void TrackChanged()
    {
        var source = _world.Source!;
        Panel.Ground = (x, z) => source.HeightAt(x, z);
        Panel.Use(_world.Graph!);
        RestoreStock();
        Net?.Attach(_world.Railway, _world.Switches, _world.Graph);
    }

    /// After the camera's flown this frame: --train's scripted placing, placing where the ground's clicked, picking a
    /// vehicle (canPick: the left button's free for it), running the trains on, the camera carried with the train, and
    /// the sound. settled: the track's built and not being laid again, so --train can place.
    public void Update(float dt, CameraView view, EditorCamera eye, bool blocked, bool settled, bool canPick)
    {
        var heightAt = _world.Source!.HeightAt;
        if (DevTools.Train is not null && settled && _world.Graph is not null && _world.GraphCurrent)
            PlaceScripted();

        // --trainbrake: the brake worked on a timetable once it's placed.
        if (Panel.Train is { } braked && DevTools.TrainBrakes.Count > 0 && braked.Time >= DevTools.TrainBrakes[0].At)
        {
            var (_, handle) = DevTools.TrainBrakes[0];
            DevTools.TrainBrakes.RemoveAt(0);
            braked.Notch = 0;
            braked.Emergency = handle < 0.0;
            braked.Brake = Math.Max(handle, 0.0);
            Log.Info($"--trainbrake: at {braked.Time:0.0} s, automatic brake {(handle < 0.0 ? "emergency" : handle.ToString("0.00", CultureInfo.InvariantCulture))}");
        }

        if (Panel.Placing)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                Panel.CancelPlacing();
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !blocked && view.Pick(Input.MousePosition, heightAt) is { } p)
            {
                var forward = _camera.Forward;
                Panel.PlaceAt(p.X, p.Z, forward.X, forward.Z);
            }
        }
        else if (canPick && Panel.Train is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !blocked)
            // The left button picks a vehicle (or lets go of it, off them).
            Panel.SelectAt(Input.MousePosition, view.Project, new double3(_camera.X, _camera.Y, _camera.Z));

        Panel.Update(dt, _world.Step(dt, Panel.FocusCut));
        Net?.Update(dt);
        if (_placeOrbit && View == DriveView.Orbit && Focus is { } orbited)
        {
            _placeOrbit = false;
            if (_drive.PlaceOrbit(_camera, orbited, Panel, heightAt))
                _followed = null;
        }

        if (View == DriveView.Cab && Focus is { } train)
        {
            if (_drive.Cab(_camera, train, Panel, blocked, eye))
                _followed = null;
        }
        else
        {
            var (bx, by, bz) = Panel.FollowBy(ref _followed);
            _camera.X += bx;
            _camera.Y += by;
            _camera.Z += bz;
        }

        Panel.Hear(_camera, dt);
    }

    /// --train: the named consist placed where it says, facing the way the camera does, and set going.
    private void PlaceScripted()
    {
        var dev = DevTools.Train!.Value;
        DevTools.Train = null;
        var at = _route.Projection!.Forward(new GeoPoint(dev.Lat, dev.Lon));
        var forward = _camera.Forward;
        if (!Panel.Pick(dev.Consist))
            Log.Warn($"--train: no saved consist called {dev.Consist}");
        Panel.PlaceAt(at.East, -at.North, forward.X, forward.Z);
        if (Panel.Train is null)
            Log.Warn($"--train: not placed ({Panel.Notice})");
        if (Panel.Train is { } placed)
        {
            placed.Reverser = 1;
            placed.Independent = 0.0;
            placed.Brake = 0.0;
            placed.Notch = dev.Notch;
            var (hx, hz) = placed.Graph.FacingOf(placed.Head);
            double From((double X, double Y, double Z) p) => Math.Sqrt((p.X - _camera.X) * (p.X - _camera.X) + (p.Z - _camera.Z) * (p.Z - _camera.Z));
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"--train: facing ({hx:0.00}, {hz:0.00}), camera ({forward.X:0.00}, {forward.Z:0.00}); lead front truck {From(placed.Cars[0].FrontAt):0.0} m from the camera, last rear truck {From(placed.Cars[^1].RearAt):0.0} m"));
            Panel.Follow = true;
        }

        Panel.Open = true;
        if (Panel.Train is { } cut && DevTools.Uncouple is { } behind && behind >= 1 && behind < cut.Cars.Count)
        {
            var air = cut.Body.Brakes;
            air.CockAhead[behind - 1] = air.CockBehind[behind - 1] = 0.0;
            air.Hose[behind - 1] = false;
            cut.Body.Uncouple(behind - 1, out _);
        }

        if (DevTools.TrainTab is { } tab)
            Panel.ShowTab(tab);
        if (DevTools.PickCoupling is { } coupling)
            Panel.SelectCoupling(coupling - 1);
        if (DevTools.PickVehicle is { } pick)
            Panel.Select(pick - 1);
        else if (DevTools.PickCoupling is null)
            Panel.Graphs.Open = true;
    }

    /// The stock on the track written to the route's Stock.json.
    public void SaveStock()
    {
        if (!_stockRestored || _route.Projection is not { } projection)
            return;
        try
        {
            RouteStock.Save(_route, Panel.Snapshot((x, z) =>
            {
                var geo = projection.Inverse(new RoutePoint(x, -z));
                return (geo.Lat, geo.Lon);
            }));
        }
        catch (Exception e)
        {
            Log.Warn($"Placed stock: not saved ({e.Message})");
        }
    }

    private void RestoreStock()
    {
        if (_stockRestored || _route.Projection is not { } projection)
            return;
        _stockRestored = true;
        var saved = RouteStock.Load(_route);
        if (saved.Count > 0)
            Panel.Restore(saved, (lat, lon) =>
            {
                var p = projection.Forward(new GeoPoint(lat, lon));
                return (p.East, -p.North);
            });
    }

    public void Dispose() => Panel.Dispose();
}
