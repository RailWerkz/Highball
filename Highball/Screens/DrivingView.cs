using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Builder;
using Highball.Multiplayer;
using Highball.Routes;
using Highball.Trains;
using Highball.World;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Screens;

/// A route as it's driven: its world (RouteWorld) and trains (TrainSession), seen from the free, orbit or cab camera, with
/// the train window over it. Nothing's edited.
public sealed class DrivingView : IDisposable
{
    private readonly RouteInfo _route;
    private readonly RoutePoint? _start;
    private readonly RouteWorld _world;
    private readonly WorldDisplay _display;
    private readonly EditorCamera _eye = new();
    private readonly CameraView _view;
    private readonly TrainSession _session;

    // The menu bar along the top and the footer along the bottom: the pointer over either isn't the world's.
    private float _headerBottom;
    private float _footerTop = float.MaxValue;

    public bool Ready => _world.Terrain is not null;
    public bool Failed => _world.Failed;
    public bool CapturesMouse => _eye.Looking;

    /// The camera (TrainSession.View).
    public DriveView View { get => _session.View; set => _session.View = value; }

    /// The train the cameras are on (TrainSession.Focus).
    public Train? Train => _session.Focus;

    /// The train window: placing, building, driving and coupling stock.
    public TrainPanel TrainWindow => _session.Panel;

    private Camera Camera => _eye.Camera;
    private bool Blocked => Keyboard.PointerBlocked || Input.MousePosition.Y < _headerBottom || Input.MousePosition.Y > _footerTop;

    /// start: where to put the camera (route metres east and north of the origin); the middle of the route if null.
    public DrivingView(RouteInfo route, RoutePoint? start, NetPlay? net)
    {
        _route = route;
        _start = start;
        var place = new GeoPoint(route.Origin.Latitude, route.Origin.Longitude);
        _display = new WorldDisplay(place.Lat, place.Lon, route.Projection?.GridNorth(place) ?? 0.0);
        _world = new RouteWorld(route);
        _view = new CameraView(Camera);
        _session = new TrainSession(_world, Camera) { Net = net };
        if (DevTools.Skybox is { } sky)
        {
            DevTools.Skybox = null;
            _display.Renderer.Sky.Use(sky);
        }
    }

    public void Dispose()
    {
        _session.SaveStock();
        _session.Dispose();
        _world.Dispose();
        _display.Dispose();
    }

    /// top and bottom: where the menu bar ends and the footer begins.
    public void Update(float dt, float top, float bottom)
    {
        _headerBottom = top;
        _footerTop = bottom;
        if (_world.Terrain is null)
        {
            if (!_world.TryFinishOpening())
                return;
            PlaceCamera(_world.Read);
        }

        var heightAt = _world.Source!.HeightAt;
        if (!_session.InCab)
            _eye.Fly(dt, Blocked, false, heightAt);
        using (Profiler.Sample("Trains"))
        {
            if (_world.RefreshGraph())
                _session.TrackChanged();
            _session.Update(dt, _view, _eye, Blocked, true, true);
        }

        KeepWorldCurrent();

        // T throws the switch nearest the pointer.
        if (ImGui.IsKeyPressed(ImGuiKey.T) && !Keyboard.KeysBlocked && _view.Pick(Input.MousePosition, heightAt) is { } at)
            _world.Throw(at.X, at.Z, 40.0);
        _world.ThrowScripted();
    }

    /// Brings the ground and the track up to date with the camera and the settings: the land out to the view distance,
    /// past however high the camera is.
    public void KeepWorldCurrent()
    {
        if (_world.Terrain is not { } terrain)
            return;
        var above = Math.Max(0.0, Camera.Y - terrain.Source.HeightAt(Camera.X, Camera.Z));
        var limit = Settings.Current.ViewDistance + above;
        _display.Renderer.Sky.SetViewLimit((float)limit);
        _world.Stream(Camera, limit);
    }

    private void PlaceCamera(TerrainSource source)
    {
        var (x, z) = _start is { } start
            ? (start.East, -start.North)
            : ((source.MinGx + source.MaxGx) * 0.5 * source.Spacing, (source.MinGz + source.MaxGz) * 0.5 * source.Spacing);
        Camera.X = x;
        Camera.Z = z;
        Camera.Y = source.HeightAt(x, z) + 150.0;
        Camera.Yaw = 0f;
        Camera.Pitch = -0.2f;

        if (DevTools.Camera is { } dev)
        {
            DevTools.Camera = null;
            var p = _route.Projection!.Forward(new GeoPoint(dev.Lat, dev.Lon));
            Camera.X = p.East;
            Camera.Z = -p.North;
            Camera.Y = source.HeightAt(Camera.X, Camera.Z) + dev.Height;
            Camera.Yaw = dev.Yaw * MathF.PI / 180f;
            Camera.Pitch = dev.Pitch * MathF.PI / 180f;
            if (DevTools.Zoom is { } zoom)
                Camera.Zoom = zoom;
        }
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        var scene = _display.Scene;
        var trains = _session.Panel;
        scene.Camera = Camera;
        (scene.Terrain, scene.Network) = (_world.Terrain, _world.Network);
        scene.Models = trains.Draws;
        scene.Outlines = trains.Outlines;
        scene.OutlineStyle = new OutlineStyle(Style.Clear.Float4, Style.Accent.Float4, Style.S(1f));
        scene.Lights = trains.Lamps;
        (scene.Smoke, scene.Haze) = (trains.Smoke, trains.Haze);
        _display.Render(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _display.Composite(cmd);

    /// The picked vehicle's outline and labels over the world.
    public void Draw(ImDrawListPtr draw, float2 size)
    {
        _view.Size = size;
        if (!Ready)
            return;
        using (Profiler.Sample("Train overlay"))
            _session.Panel.Overlay(draw, _view.Project, new double3(Camera.X, Camera.Y, Camera.Z), new float2(0f, _headerBottom),
                new float2(size.X, MathF.Min(size.Y, _footerTop)));
    }

    public void Windows()
    {
        using (Profiler.Sample("Train window"))
            _session.Panel.Window(_headerBottom);
    }
}
