using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Route Builder for one route: the Planner (its tiles over OpenStreetMap, picked and built), and the 2D and 3D
/// Editors (the built terrain from straight above or flown through, sculpted, painted and laid with track), switched
/// with Tab (in that order), F1 to F3 or the View menu. All three share the menu bar along the top, the side panel on
/// the left (beside the editors' tool strip) and the controls footer along the bottom. The editors are one view of the terrain, kept open once it's
/// been opened, so switching costs nothing; it opens again only after a build.
public sealed class BuilderScreen : Screen, IWorldScreen
{
    public enum Mode { Planner, Editor2D, Editor3D }

    private const float PanelWidth = 320f;
    private const float FooterHeight = 36f;
    private const double NoticeSeconds = 4.0;

    private readonly RouteInfo _route;
    private readonly PlannerView _planner;
    private EditorView? _editor;
    /// The Planner's build count when the 3D Editor opened its tiles.
    private int _editorBuild;
    private Mode _mode;
    private bool _showPanel = true;
    private bool _exit;

    private float _headerBottom;
    private float _footerTop = float.MaxValue;
    private string _notice = "";
    private double _noticeAt = double.MinValue;

    public override WorldView View => _mode != Mode.Planner && _editor is { Ready: true } ? WorldView.World : WorldView.None;
    public override bool CapturesMouse => _mode != Mode.Planner && _editor?.CapturesMouse == true;

    public BuilderScreen(RouteInfo route)
    {
        _route = route;
        _planner = new PlannerView(route, Notify);
        if (DevTools.Panels)
            DebugWindows.ShowAll();
    }

    public override void Exit()
    {
        _editor?.Dispose();
        _planner.Dispose();
    }

    /// A short notice on the right of the footer.
    private void Notify(string text)
    {
        _notice = text;
        _noticeAt = RailWerkz.Core.Time.Time.Now;
    }

    private bool CanOpen3D => _planner.Built.Count > 0 && !_planner.Building;

    /// Switches view. From the Planner, the editors open over the middle of the map if that's built (where their camera
    /// was otherwise), and the map centres on where the camera was on the way back.
    public void Show(Mode mode)
    {
        if (mode == _mode)
            return;

        if (mode != Mode.Planner)
        {
            if (!CanOpen3D)
            {
                Notify(_planner.Building ? "Wait for the build to finish" : "Build some tiles first");
                return;
            }

            if (_editor is not null && _editorBuild != _planner.BuildVersion)
            {
                _editor.Dispose();
                _editor = null;
            }

            var centre = _planner.Centre();
            if (_editor is null)
            {
                _editor = new EditorView(_route, centre);
                _editorBuild = _planner.BuildVersion;
            }
            else if (_mode == Mode.Planner && centre is { } point)
                _editor.MoveTo(point);
            _editor.SetTopDown(mode == Mode.Editor2D);
        }
        else if (_editor is not null)
        {
            if (_editor.Save())
                _planner.ReloadNetwork();
            _planner.ReloadTurnouts();
            if (_editor.Ready)
                _planner.LookAt(_editor.Position);
        }

        _mode = mode;
    }

    public override void Update(float dt)
    {
        _planner.Update();
        DebugWindows.Keys();

        if (!Keyboard.KeysBlocked)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Tab))
                Show(_mode switch { Mode.Planner => Mode.Editor2D, Mode.Editor2D => Mode.Editor3D, _ => Mode.Planner });
            else if (ImGui.IsKeyPressed(ImGuiKey.F1))
                Show(Mode.Planner);
            else if (ImGui.IsKeyPressed(ImGuiKey.F2))
                Show(Mode.Editor2D);
            else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                Show(Mode.Editor3D);
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !(_mode != Mode.Planner && _editor!.Escape()))
            {
                if (_mode != Mode.Planner)
                    Show(Mode.Planner);
                else
                    _exit = true;
            }
        }

        if (_exit)
        {
            Sfx.Back();
            HighballApp.Instance.Pop();
            return;
        }

        if (_mode == Mode.Planner)
            return;

        _editor!.Update(dt, _headerBottom, _footerTop);
        if (_editor.Failed)
        {
            Notify("The terrain couldn't be opened");
            _editor.Dispose();
            _editor = null;
            _mode = Mode.Planner;
        }
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _editor?.Lighting.Apply();
        _editor?.RenderWorld(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _editor?.CompositeWorld(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        _footerTop = size.Y - S(FooterHeight);
        if (_mode == Mode.Planner)
            _planner.Draw(draw, size, _headerBottom, _footerTop);
        else
            _editor!.Draw(draw, size);
        DrawFooter(draw, size);
    }

    /// The controls for the view and tool in hand along the bottom, with any notice (or the map's credit) on the right.
    private void DrawFooter(ImDrawListPtr draw, float2 size)
    {
        var height = S(FooterHeight);
        draw.AddRectFilled(new float2(0f, _footerTop), size, Surface);
        draw.AddLine(new float2(0f, _footerTop), new float2(size.X, _footerTop), StrokeStrong, 1f);
        var left = S(24f);
        if (_mode != Mode.Planner && _editor!.Status is { Length: > 0 } status)
        {
            Canvas.Text(draw, Mono, S(13f), new float2(left, _footerTop + (height - S(13f)) * 0.5f), Style.Text, status);
            left += Canvas.TextSize(Mono, S(13f), status).X + S(32f);
        }

        Canvas.Hints(draw, new float2(left, size.Y - (height - S(22f)) * 0.5f), _mode == Mode.Planner ? _planner.Hints : _editor!.Hints);

        var noticing = RailWerkz.Core.Time.Time.Now - _noticeAt < NoticeSeconds;
        var right = noticing ? _notice : _mode == Mode.Planner ? _planner.Attribution : "";
        if (right.Length > 0)
            Canvas.Text(draw, Medium, S(13f), new float2(size.X - S(24f), _footerTop + (height - S(13f)) * 0.5f), noticing ? Style.Text : TextDim, right, Align.Right);
    }

    // --- The menu bar, side panel and windows (ImGui). ---

    public override void DrawWindows()
    {
        if (ImGui.BeginMainMenuBar())
        {
            _headerBottom = ImGui.GetWindowSize().Y;
            var editing = _mode != Mode.Planner;

            if (ImGui.BeginMenu("File"))
            {
                if (editing)
                {
                    _editor!.MenuFile();
                    ImGui.Separator();
                }

                if (ImGui.MenuItem("Settings"))
                    HighballApp.Instance.Push(new Screens.SettingsScreen(this, _route.Origin.Latitude, _route.Origin.Longitude));
                if (ImGui.MenuItem("Exit Route Builder", editing ? "" : "Esc"))
                    _exit = true;
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Planner", "F1", !editing, true))
                    Show(Mode.Planner);
                if (ImGui.MenuItem("2D Editor", "F2", _mode == Mode.Editor2D, editing || CanOpen3D))
                    Show(Mode.Editor2D);
                if (ImGui.MenuItem("3D Editor", "F3", _mode == Mode.Editor3D, editing || CanOpen3D))
                    Show(Mode.Editor3D);
                ImGui.Separator();
                ImGui.MenuItem("Side Panel"u8, ""u8, ref _showPanel);
                if (editing)
                {
                    ImGui.Separator();
                    _editor!.MenuView();
                }

                ImGui.Separator();
                if (ImGui.MenuItem("Lighting Test Scene"))
                    HighballApp.Instance.Push(new Screens.LightingScreen());
                DebugWindows.Menu();
                ImGui.EndMenu();
            }

            TopBar(editing);
            ImGui.EndMainMenuBar();
        }

        if (_showPanel && (_mode == Mode.Planner || _editor?.HasPanel == true))
            Panel();
        if (_mode != Mode.Planner)
            _editor?.Windows();

        var left = (_mode == Mode.Planner ? 0f : EditorView.StripWidth) + (_showPanel ? PanelWidth : 0f) + 12f;
        Action lines = _mode == Mode.Planner || _editor is null ? _planner.DebugStats : _editor.DebugStats;
        DebugWindows.Draw(left, _headerBottom + 12f, lines, _planner.BuildJob);
    }

    private static readonly (Mode Mode, string Name, string Key)[] ViewTabs =
    [
        (Mode.Planner, "Planner", "F1"), (Mode.Editor2D, "2D Editor", "F2"), (Mode.Editor3D, "3D Editor", "F3"),
    ];

    /// The views as tabs in the middle of the menu bar, then the route and whether it's saved at its right.
    private void TopBar(bool editing)
    {
        const float pad = 16f;
        const float textSize = 15f;
        var draw = ImGui.GetWindowDrawList();
        var height = ImGui.GetWindowSize().Y;
        var total = ViewTabs.Sum(t => Canvas.TextSize(Medium, textSize, t.Name).X + pad * 2f);
        var x = (ImGui.GetIO().DisplaySize.X - total) * 0.5f;
        foreach (var (mode, name, key) in ViewTabs)
        {
            var size = new float2(Canvas.TextSize(Medium, textSize, name).X + pad * 2f, height);
            var at = new float2(x, 0f);
            var on = _mode == mode;
            var open = mode == Mode.Planner || editing || CanOpen3D;
            ImGui.SetCursorScreenPos(at);
            if (ImGui.InvisibleButton(name, size))
                Show(mode);
            var hovered = ImGui.IsItemHovered();
            if (on)
                draw.AddRectFilled(at, at + size, Raised);
            else if (hovered && open)
                draw.AddRectFilled(at, at + size, RaisedHover);
            if (on)
                draw.AddRectFilled(at + new float2(0f, height - 2f), at + size, Accent);
            Canvas.Text(draw, Medium, textSize, at + new float2(size.X * 0.5f, (height - textSize) * 0.5f), on ? Style.Text : open ? hovered ? Style.Text : TextDim : TextFaint, name, Align.Center);
            if (hovered)
                ImGui.SetTooltip(open ? $"{name} ({key})" : "Build some tiles first");
            x += size.X;
        }

        var label = editing ? $"{_route.Name}   {_editor!.SaveState}" : _route.Name;
        ImGui.SameLine(ImGui.GetIO().DisplaySize.X - ImGui.CalcTextSize(label).X - 16f, -1f);
        ImGui.Text(label);
    }

    /// What the view in front has to show and set: in the Planner, the route's place first.
    private void Panel()
    {
        ImGui.SetNextWindowPos(new float2(_mode == Mode.Planner ? 0f : EditorView.StripWidth, _headerBottom), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(PanelWidth, Math.Max(100f, _footerTop - _headerBottom)), ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin("Route"u8, flags))
        {
            if (_mode == Mode.Planner)
            {
                ImGui.TextDisabled(_route.Grid is null
                    ? "Origin not set"
                    : string.Create(CultureInfo.InvariantCulture, $"Origin {_route.Origin.Latitude:0.00000}, {_route.Origin.Longitude:0.00000}"));
                ImGui.TextDisabled($"{_planner.Built.Count} tile{(_planner.Built.Count == 1 ? "" : "s")} built");
                _planner.Panel();
            }
            else
                _editor!.Panel();
        }

        ImGui.End();
    }
}
