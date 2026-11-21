using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Builder;
using Highball.Multiplayer;
using Highball.Routes;
using Highball.Trains;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Driving an installed world (Single Player, or a multiplayer session), in the workspace layout without a rail: the
/// Train inspector on the left (C), the camera switch in the corner (F1 cab, F2 orbiting the lead unit, F3 free), the
/// drive strip along the bottom while a train's driven, and the footer's keys and notices. Esc pauses.
public sealed class DriveScreen : Screen, IWorldScreen
{
    private const double NoticeSeconds = 5.0;

    private readonly PackSource _pack;
    private readonly NetPlay? _net;
    private readonly DrivingView _view;
    private string _notice = "";
    private double _noticeAt = double.MinValue;

    private static readonly string[] ViewNames = ["Cab", "Orbit", "Free"];
    private static readonly DriveView[] Views = [DriveView.Cab, DriveView.Orbit, DriveView.Free];

    public override bool EditorTheme => true;

    public override WorldView View => _view.Ready ? WorldView.World : WorldView.None;
    public override bool CapturesMouse => _view.CapturesMouse;

    public DriveScreen(PackSource pack, NetPlay? net = null)
    {
        _pack = pack;
        _net = net;
        var route = pack.Route;
        var built = route.BuiltTiles();
        RoutePoint? middle = built.Count > 0 && route.Grid is { } grid
            ? new RoutePoint(built.Average(t => t.I + 0.5) * grid.TileSize, built.Average(t => t.J + 0.5) * grid.TileSize)
            : null;
        _view = new DrivingView(route, middle, net) { View = DriveView.Orbit };
        if (net is not null)
            net.Notice += Notify;
        if (DevTools.DriveView is { } start && Enum.TryParse<DriveView>(start, true, out var view))
        {
            DevTools.DriveView = null;
            _view.View = view;
        }
    }

    private void Notify(string text)
    {
        _notice = text;
        _noticeAt = RailWerkz.Core.Time.Time.Now;
    }

    public override void Exit()
    {
        _view.Dispose();
        _pack.Dispose();
        _net?.Dispose();
    }

    public override void Update(float dt)
    {
        if (!Keyboard.KeysBlocked)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F1))
                _view.View = DriveView.Cab;
            else if (ImGui.IsKeyPressed(ImGuiKey.F2))
                _view.View = DriveView.Orbit;
            else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                _view.View = DriveView.Free;
            else if (ImGui.IsKeyPressed(ImGuiKey.C))
                _view.TrainWindow.Open = !_view.TrainWindow.Open;
            else if (ImGui.IsKeyPressed(ImGuiKey.F))
                _stats = !_stats;
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !_view.TrainWindow.Placing)
                HighballApp.Instance.Push(new PauseScreen(this));
        }

        _view.Update(dt, Workspace.Top, Workspace.Bottom);
        if (DevTools.HideWindows && _view.Train is not null)
        {
            _view.TrainWindow.Open = false;
            _view.TrainWindow.Graphs.Open = false;
        }
        if (_view.Failed)
        {
            Notify("The world couldn't be opened");
            HighballApp.Instance.Pop();
        }
    }

    public override void UpdateBehind() => _view.KeepWorldCurrent();

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _view.RenderWorld(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _view.CompositeWorld(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        _view.Draw(draw, size);
        if (!_view.Ready)
            Canvas.Text(draw, Medium, S(18f), new float2(size.X * 0.5f, size.Y * 0.5f), TextDim, "Opening the world...", Align.Center);

        // The train looked at when it isn't the one driven (the drive strip has the rest), and with F the frame's numbers.
        var train = _view.Train;
        var status = train is not null && !ReferenceEquals(train, _view.TrainWindow.Controlled)
            ? $"{train.Name.ToUpperInvariant()}   {UnitText.Speed(Math.Abs(train.CabSpeed)).ToUpperInvariant()}"
            : null;
        var noticing = RailWerkz.Core.Time.Time.Now - _noticeAt < NoticeSeconds;
        var notice = noticing ? _notice : _view.TrainWindow.Notice;
        if (!_stats)
        {
            Workspace.Status(draw, status, Hints, notice, noticing);
            return;
        }

        Workspace.Status(draw, status, Hints);
        var y = Workspace.StatusTop + (Workspace.StatusHeight - S(14f)) * 0.5f;
        var edge = Stats(draw, size.X - S(10f), y) - S(24f);
        if (notice is { Length: > 0 })
            Canvas.Text(draw, Regular, S(12.5f), new float2(edge, y), noticing ? Style.Text : TextDim, notice, Align.Right);
    }

    /// Shown with F: frames a second, triangles and draw calls last frame, the CPU's and GPU's milliseconds a frame on average, and VRAM
    /// and RAM in use.
    private bool _stats = System.Environment.GetEnvironmentVariable("HB_FOOTERSTATS") is not null;

    /// The frame's numbers, right-aligned to right; returns where they start.
    private static float Stats(ImDrawListPtr draw, float right, float y)
    {
        var frame = Profiler.FrameTimes.Average();
        // (VRAM: what the game's allocator has taken from the driver; RAM: the process's working set. Read twice a second.)
        var memory = MemoryUse.Now;
        static string Gigabytes(long bytes) => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        // Each value in a slot as wide as the widest it gets (Widest), right-aligned in it: so nothing moves as the numbers change.
        var items = new (string Label, string Value, string Widest)[]
        {
            ("FPS", $"{1000f / MathF.Max(frame, 0.01f):0}", "0000"),
            ("TRIS", Profiler.Triangles >= 1_000_000 ? $"{Profiler.Triangles / 1_000_000f:0.00}M" : $"{Profiler.Triangles / 1000f:0}K", "00.00M"),
            ("DRAWS", $"{Profiler.DrawCalls:#,0}", "00,000"),
            ("CPU", $"{Profiler.CpuAverage:0.0} MS", "000.0 MS"),
            ("GPU", $"{Profiler.GpuFrameTimes.Average():0.0} MS", "000.0 MS"),
            ("VRAM", Gigabytes(memory.Vram), "00.00 GB"),
            ("RAM", Gigabytes(memory.Ram), "00.00 GB"),
        };
        var x = right;
        for (var i = items.Length - 1; i >= 0; i--)
        {
            var (label, value, widest) = items[i];
            Canvas.Text(draw, Mono, S(14f), new float2(x - Canvas.TextSize(Mono, S(14f), value).X, y), Style.Text, value);
            x -= Canvas.TextSize(Mono, S(14f), widest).X;
            x -= S(8f) + Canvas.SpacedWidth(Medium, S(10f), label, 0.18f);
            Canvas.Spaced(draw, Medium, S(10f), new float2(x, y + S(2f)), TextFaint, label, 0.18f);
            x -= S(22f);
        }

        return x + S(22f);
    }

    private (string Key, string Action)[] Hints => _view.View switch
    {
        DriveView.Free => [("RMB", "Look"), ("WASD", "Move"), ("WHEEL", "Speed"), ("T", "Throw switch"), ("C", "Train"), ("F", "Stats"), ("ESC", "Menu")],
        DriveView.Cab => [("RMB", "Look round"), ("WHEEL", "Zoom"), ("T", "Throw switch"), ("C", "Train"), ("F", "Stats"), ("ESC", "Menu")],
        _ => [("RMB", "Look"), ("WASD", "Move round the train"), ("WHEEL", "Speed"), ("T", "Throw switch"), ("C", "Train"), ("F", "Stats"), ("ESC", "Menu")],
    };

    public override void DrawWindows()
    {
        var trains = _view.TrainWindow;
        if (Workspace.BeginBar(out var top))
        {
            if (ImGui.BeginMenu("Game"))
            {
                if (ImGui.MenuItem("Train", "C", trains.Open, true))
                    trains.Open = !trains.Open;
                if (ImGui.MenuItem("Settings..."))
                    HighballApp.Instance.Push(new SettingsScreen(this, _pack.Route.Origin.Latitude, _pack.Route.Origin.Longitude));
                ImGui.Separator();
                if (ImGui.MenuItem(_net is null ? "Leave" : "Leave Session", "Esc"))
                    HighballApp.Instance.Pop();
                ImGui.EndMenu();
            }

            if (_net is not null)
                ImGui.TextDisabled(_net.Hosting ? $"Hosting · player {_net.Number}" : $"Player {_net.Number}");
            var picked = Workspace.Views("##camera", ViewNames, Array.IndexOf(Views, _view.View));
            Workspace.EndBar();
            if (picked >= 0 && Views[picked] != _view.View)
                _view.View = Views[picked];
        }

        Workspace.Begin(top, trains.HasDock);
        if (trains.Open)
        {
            if (Workspace.BeginProperties("##properties"))
                _view.Inspector();
            Workspace.EndProperties();
        }

        if (trains.HasDock)
        {
            if (Workspace.BeginDock("##dock", 0f))
                _view.Dock();
            Workspace.EndDock();
        }

        _view.Windows();
        Workspace.End();
    }
}

/// Paused while driving: carry on, the settings, or leave the world (or the session).
public sealed class PauseScreen : MenuScreen
{
    public PauseScreen(DriveScreen under) : base("PAUSED")
    {
        Menu.Add(new MenuItem { Label = "RESUME", OnSelect = Back })
            .Add(new MenuItem { Label = "SETTINGS", OnSelect = () => HighballApp.Instance.Push(new SettingsScreen(under)) })
            .Add(new MenuItem { Label = "LEAVE", OnSelect = () =>
            {
                HighballApp.Instance.Pop();
                HighballApp.Instance.Pop();
            } });
    }
}
