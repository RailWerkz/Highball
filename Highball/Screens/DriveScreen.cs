using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Builder;
using Highball.Multiplayer;
using Highball.Routes;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Driving an installed world (Single Player, or a multiplayer session): the world in 3D with a header and a footer
/// instead of the Route Builder's tools. The header has the world, the camera views (F1 cab, F2 orbiting the lead unit,
/// F3 free) and the Consist button; the footer the train's speed, notch and brakes, the controls and any notice. The
/// train window (build a consist, place it, drive it, couple and uncouple) and its air graphs open over it. Esc pauses.
public sealed class DriveScreen : Screen, IWorldScreen
{
    private const float FooterHeight = 40f;
    private const double NoticeSeconds = 5.0;

    private readonly PackSource _pack;
    private readonly NetPlay? _net;
    private readonly EditorView _view;
    private float _headerBottom;
    private float _footerTop = float.MaxValue;
    private string _notice = "";
    private double _noticeAt = double.MinValue;

    private static readonly (EditorView.DriveView View, string Name, string Key)[] Views =
    [
        (EditorView.DriveView.Cab, "Cab", "F1"), (EditorView.DriveView.Orbit, "Orbit", "F2"), (EditorView.DriveView.Free, "Free", "F3"),
    ];

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
        _view = new EditorView(route, middle) { Driving = true, Net = net };
        _view.View = EditorView.DriveView.Orbit;
        if (net is not null)
            net.Notice += Notify;
        if (DevTools.DriveView is { } start && Enum.TryParse<EditorView.DriveView>(start, true, out var view))
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
                _view.View = EditorView.DriveView.Cab;
            else if (ImGui.IsKeyPressed(ImGuiKey.F2))
                _view.View = EditorView.DriveView.Orbit;
            else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                _view.View = EditorView.DriveView.Free;
            else if (ImGui.IsKeyPressed(ImGuiKey.C))
                _view.TrainWindow.Open = !_view.TrainWindow.Open;
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !_view.Escape())
                HighballApp.Instance.Push(new PauseScreen(this));
        }

        _view.Update(dt, _headerBottom, _footerTop);
        if (DevTools.HideWindows && _view.DriveTrain is not null)
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
        _view.Lighting.Apply();
        _view.RenderWorld(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _view.CompositeWorld(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        _footerTop = size.Y - S(FooterHeight);
        _view.Draw(draw, size);
        DrawFooter(draw, size);
        if (!_view.Ready)
            Canvas.Text(draw, Medium, S(18f), new float2(size.X * 0.5f, size.Y * 0.5f), TextDim, "Opening the world...", Align.Center);
    }

    /// The train's readout at the left, the controls in the middle, any notice at the right.
    private void DrawFooter(ImDrawListPtr draw, float2 size)
    {
        var height = S(FooterHeight);
        draw.AddRectFilled(new float2(0f, _footerTop), size, Surface);
        draw.AddLine(new float2(0f, _footerTop), new float2(size.X, _footerTop), StrokeStrong, 1f);
        var y = _footerTop + (height - S(14f)) * 0.5f;
        var x = S(24f);

        if (_view.DriveTrain is { } train)
        {
            var driving = ReferenceEquals(train, _view.TrainWindow.Controlled);
            var items = new List<(string Label, string Value)>
            {
                (driving ? "DRIVING" : "LOOKING AT", train.Name.ToUpperInvariant()),
                ("SPEED", UnitText.Speed(Math.Abs(train.CabSpeed)).ToUpperInvariant()),
            };
            if (driving)
            {
                items.Add(("THROTTLE", train.Notch > 0 ? $"N{train.Notch}" : train.Dynamic > 0.0 ? $"DB {train.Dynamic * 100:0}%" : "IDLE"));
                items.Add(("REVERSER", train.CabReverser switch { > 0 => "FWD", < 0 => "REV", _ => "N" }));
                items.Add(("BRAKE PIPE", UnitText.Pressure(train.Body.Brakes.Pipe[Math.Clamp(train.LeadUnit, 0, train.Cars.Count - 1)]).ToUpperInvariant()));
            }

            foreach (var (label, value) in items)
            {
                Canvas.Spaced(draw, Medium, S(10f), new float2(x, y + S(2f)), TextFaint, label, 0.18f);
                x += Canvas.SpacedWidth(Medium, S(10f), label, 0.18f) + S(8f);
                Canvas.Text(draw, Mono, S(14f), new float2(x, y), Style.Text, value);
                x += Canvas.TextSize(Mono, S(14f), value).X + S(22f);
            }
        }

        Canvas.Hints(draw, new float2(x + S(8f), size.Y - (height - S(22f)) * 0.5f), Hints);

        var noticing = RailWerkz.Core.Time.Time.Now - _noticeAt < NoticeSeconds;
        var right = noticing ? _notice : _view.TrainWindow.Notice ?? "";
        if (right.Length > 0)
            Canvas.Text(draw, Medium, S(13f), new float2(size.X - S(24f), _footerTop + (height - S(13f)) * 0.5f), noticing ? Style.Text : TextDim, right, Align.Right);
    }

    private (string Key, string Action)[] Hints => _view.View switch
    {
        EditorView.DriveView.Free => [("RMB", "Look"), ("WASD", "Move"), ("WHEEL", "Speed"), ("T", "Throw switch"), ("C", "Consist"), ("ESC", "Menu")],
        EditorView.DriveView.Cab => [("RMB", "Look round"), ("WHEEL", "Zoom"), ("T", "Throw switch"), ("C", "Consist"), ("ESC", "Menu")],
        _ => [("RMB", "Look"), ("WASD", "Move round the train"), ("WHEEL", "Speed"), ("T", "Throw switch"), ("C", "Consist"), ("ESC", "Menu")],
    };

    public override void DrawWindows()
    {
        if (ImGui.BeginMainMenuBar())
        {
            _headerBottom = ImGui.GetWindowSize().Y;
            if (ImGui.BeginMenu("Game"))
            {
                if (ImGui.MenuItem("Consist", "C", _view.TrainWindow.Open, true))
                    _view.TrainWindow.Open = !_view.TrainWindow.Open;
                if (ImGui.MenuItem("Settings..."))
                    HighballApp.Instance.Push(new SettingsScreen(this, _pack.Route.Origin.Latitude, _pack.Route.Origin.Longitude));
                ImGui.Separator();
                if (ImGui.MenuItem(_net is null ? "Leave" : "Leave Session", "Esc"))
                    HighballApp.Instance.Pop();
                ImGui.EndMenu();
            }

            Header();
            ImGui.EndMainMenuBar();
        }

        WindowBounds.Top = _headerBottom;
        WindowBounds.Bottom = _footerTop;
        _view.Windows();
        WindowBounds.Reset();
    }

    /// The camera views as tabs in the middle, the world and the session at the right.
    private void Header()
    {
        const float pad = 18f;
        const float textSize = 15f;
        var draw = ImGui.GetWindowDrawList();
        var height = ImGui.GetWindowSize().Y;
        var total = Views.Sum(v => Canvas.TextSize(Medium, textSize, $"{v.Key}  {v.Name}").X + pad * 2f);
        var x = (ImGui.GetIO().DisplaySize.X - total) * 0.5f;
        foreach (var (view, name, key) in Views)
        {
            var label = $"{key}  {name}";
            var size = new float2(Canvas.TextSize(Medium, textSize, label).X + pad * 2f, height);
            var at = new float2(x, 0f);
            var on = _view.View == view;
            ImGui.SetCursorScreenPos(at);
            if (ImGui.InvisibleButton(name, size))
                _view.View = view;
            var hovered = ImGui.IsItemHovered();
            if (on)
                draw.AddRectFilled(at, at + size, Raised);
            else if (hovered)
                draw.AddRectFilled(at, at + size, RaisedHover);
            if (on)
                draw.AddRectFilled(at + new float2(0f, height - 2f), at + size, Accent);
            Canvas.Text(draw, Medium, textSize, at + new float2(size.X * 0.5f, (height - textSize) * 0.5f), on || hovered ? Style.Text : TextDim, label, Align.Center);
            x += size.X;
        }

        var session = _net is null ? "Single Player" : _net.Hosting ? $"Hosting · Player {_net.Number}" : $"Player {_net.Number}";
        var world = _pack.Header.Region == "World" ? _pack.Route.Name : $"{_pack.Route.Name} · {_pack.Header.Region}";
        var right = $"{world}   {session}";
        ImGui.SameLine(ImGui.GetIO().DisplaySize.X - ImGui.CalcTextSize(right).X - 16f, -1f);
        ImGui.Text(right);
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
