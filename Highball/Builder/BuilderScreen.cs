using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

//// The Route Builder for one route, in the workspace layout: the rail of tools down the left (the Area tool first, which
/// is the map of the route's tiles over OpenStreetMap, picked and built; then the editor's tools for the built terrain),
/// the inspector for the tool in hand beside it, and the view switch (MAP, 2D, 3D) in the viewport's corner. The editor is
/// one view of the terrain, kept open once it's been opened, so switching costs nothing; it opens again only after a build.
public sealed class BuilderScreen : Screen, IWorldScreen
{
    /// What the viewport shows: the map (with the Area tool in hand), or the built terrain from above or flown through.
    public enum ViewMode { Map, TopDown, Free }

    private static readonly string[] ViewNames = ["MAP", "2D", "3D"];

    private const double NoticeSeconds = 4.0;

    private readonly RouteInfo _route;
    private readonly PlannerView _planner;
    private EditorView? _editor;
    /// The Planner's build count when the editor opened its tiles.
    private int _editorBuild;
    private ViewMode _view;
    /// The editor's view to go back to from the map.
    private ViewMode _lastEditorView = ViewMode.Free;

    /// The Developer menu: in a Debug build, or with Developer tools on in Settings.
#if DEBUG
    private static bool DeveloperMenu => true;
#else
    private static bool DeveloperMenu => RailWerkz.Core.Settings.Settings.Current.DeveloperTools;
#endif
    private bool _exit;

    private string _notice = "";
    private double _noticeAt = double.MinValue;

    public override bool EditorTheme => true;

    public override WorldView View => _view != ViewMode.Map && _editor is { Ready: true } ? WorldView.World : WorldView.None;
    public override bool CapturesMouse => _view != ViewMode.Map && _editor?.CapturesMouse == true;

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
        MaterialsWindow.Instance.Dispose();
    }

    /// A short notice on the right of the footer.
    private void Notify(string text)
    {
        _notice = text;
        _noticeAt = RailWerkz.Core.Time.Time.Now;
    }

    private bool CanOpenEditor => _planner.Built.Count > 0 && !_planner.Building;

    /// Switches view. From the map, the editor opens over the middle of the map if that's built (where its camera was
    /// otherwise), and the map centres on where the camera was on the way back.
    public void Show(ViewMode view)
    {
        if (view == _view)
            return;

        if (view != ViewMode.Map)
        {
            if (!CanOpenEditor)
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
            else if (_view == ViewMode.Map && centre is { } point)
                _editor.MoveTo(point);
            _editor.SetTopDown(view == ViewMode.TopDown);
            _lastEditorView = view;
        }
        else if (_editor is not null)
        {
            if (_editor.Save())
                _planner.ReloadNetwork();
            _planner.ReloadTurnouts();
            if (_editor.Ready)
                _planner.LookAt(_editor.Position);
        }

        _view = view;
    }

    public override void Update(float dt)
    {
        _planner.Update();
        if (_planner.ResetRequested)
        {
            FactoryReset();
            return;
        }

        DebugWindows.Keys();

        if (!Keyboard.KeysBlocked)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Tab))
                Show(_view switch { ViewMode.Map => ViewMode.TopDown, ViewMode.TopDown => ViewMode.Free, _ => ViewMode.Map });
            else if (ImGui.IsKeyPressed(ImGuiKey.F1))
                Show(ViewMode.Map);
            else if (ImGui.IsKeyPressed(ImGuiKey.F2))
                Show(ViewMode.TopDown);
            else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                Show(ViewMode.Free);
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !(_view != ViewMode.Map && _editor!.Escape()))
            {
                if (_view != ViewMode.Map)
                    Show(ViewMode.Map);
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

        if (_view == ViewMode.Map)
            return;

        _editor!.Update(dt, Workspace.Top, Workspace.Bottom);
        if (_editor.Failed)
        {
            Notify("The terrain couldn't be opened");
            _editor.Dispose();
            _editor = null;
            _view = ViewMode.Map;
        }
    }

    /// The route wiped back to nothing (the Planner's Factory Reset): the editor closed (saving whatever it had, so nothing
    /// writes into the folder after it's gone), the route's folder deleted whole, and the builder opened again on a new,
    /// empty route of the same name, its origin and grid unset till the first area's picked.
    private void FactoryReset()
    {
        _editor?.Dispose();
        _editor = null;
        _view = ViewMode.Map;

        var folder = _route.Folder;
        Exception? failure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                failure = null;
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // (A tile still being read in the background lets go of its file in a moment.)
                failure = e;
                Thread.Sleep(200);
            }
        }

        if (failure is not null)
        {
            RailWerkz.Core.Logging.Log.Warn($"Factory reset: {folder} not wholly deleted ({failure.Message})");
            Notify("Factory reset couldn't delete everything; close anything using the route and try again");
        }
        else
            RailWerkz.Core.Logging.Log.Info($"Factory reset: {folder} deleted");

        var fresh = _route.Fresh();
        HighballApp.Instance.Replace(new BuilderScreen(fresh));
    }

    public override void UpdateBehind()
    {
        if (_view != ViewMode.Map)
            _editor?.KeepWorldCurrent();
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _editor?.RenderWorld(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _editor?.CompositeWorld(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        if (_view == ViewMode.Map)
            _planner.Draw(draw, size, Workspace.Top, Workspace.Bottom);
        else
            _editor!.Draw(draw, size);

        var noticing = RailWerkz.Core.Time.Time.Now - _noticeAt < NoticeSeconds;
        var editing = _view != ViewMode.Map;
        var status = editing ? _editor!.Status : null;
        var hints = editing ? _editor!.Hints : _planner.Hints;
        if (noticing || !editing || _editor!.OverlayKey is not var (title, key))
        {
            Workspace.Status(draw, status, hints, noticing ? _notice : editing ? null : _planner.Attribution, noticing);
            return;
        }

        // The track overlay's key at the right while it's shown.
        Workspace.Status(draw, status, hints);
        var x = size.X - S(10f);
        var y = Workspace.StatusTop + (Workspace.StatusHeight - S(12f)) * 0.5f;
        for (var i = key.Length - 1; i >= 0; i--)
        {
            var (color, name) = key[i];
            x -= Canvas.TextSize(Medium, S(12f), name).X;
            Canvas.Text(draw, Medium, S(12f), new float2(x, y), TextDim, name);
            x -= S(22f);
            draw.AddRectFilled(new float2(x, y + S(4f)), new float2(x + S(16f), y + S(9f)), color);
            x -= S(16f);
        }

        Canvas.Spaced(draw, Medium, S(10f), new float2(x - Canvas.SpacedWidth(Medium, S(10f), title, 0.18f), y + S(1f)), TextFaint, title, 0.18f);
    }

    // --- The menu bar, rail, inspector, dock and windows (ImGui). ---

    public override void DrawWindows()
    {
        var editing = _view != ViewMode.Map;
        if (Workspace.BeginBar(out var top))
        {
            Menus(editing);
            if (editing)
                _editor!.ToolModes();
            var picked = Workspace.Views("##view", ViewNames, (int)_view, [true, CanOpenEditor || editing, CanOpenEditor || editing],
                editing && _editor!.Unsaved, editing ? _editor!.SaveState : null);
            Workspace.EndBar();
            if (picked != (int)_view)
                Show((ViewMode)picked);
        }

        Workspace.Begin(top, editing && _editor!.HasDock);

        // The rail: the Area tool (the map), then the editor's tools, the train, undo and redo.
        if (Workspace.BeginRail())
        {
            if (Workspace.RailItem("rail.area", ToolIcon.Place, "Area (F1)", _view == ViewMode.Map))
                Show(ViewMode.Map);
            Workspace.RailGap();
            if (_editor is { } editor && editing)
                editor.RailItems();
            else if (DisabledRail())
                Show(_lastEditorView);
        }

        Workspace.EndRail();

        if (!editing || _editor!.HasProperties)
        {
            if (Workspace.BeginProperties("##properties"))
            {
                if (editing)
                    _editor!.Panel();
                else
                    _planner.Panel();
            }

            Workspace.EndProperties();
        }

        if (editing)
        {
            if (_editor!.HasDock)
            {
                if (Workspace.BeginDock("##dock", 0f))
                    _editor.Dock();
                Workspace.EndDock();
            }

            _editor.Windows();
            MaterialsWindow.Instance.Draw();
        }

        Action lines = !editing || _editor is null ? _planner.DebugStats : _editor.DebugStats;
        DebugWindows.Draw(Workspace.RailWidth + S(12f), top + S(12f), lines, _planner.BuildJob, !editing || _editor is null ? null : _editor.CameraInfo);
        Workspace.End();
    }

    /// The editor's rail buttons shown greyed while only the map is open: any of them opens the editor.
    private bool DisabledRail()
    {
        var clicked = false;
        foreach (var (icon, name) in (ReadOnlySpan<(ToolIcon, string)>)
                 [(ToolIcon.Look, "Look"), (ToolIcon.Draw, "Draw"), (ToolIcon.Edit, "Edit"), (ToolIcon.Lay, "Yard"), (ToolIcon.Switches, "Switches"),
                  (ToolIcon.Pieces, "Place"), (ToolIcon.Signal, "Signals"), (ToolIcon.Road, "Roads"), (ToolIcon.Terrain, "Terrain"), (ToolIcon.Consist, "Train")])
            clicked |= Workspace.RailItem($"rail.{name}", icon, name, false, CanOpenEditor);
        return clicked;
    }

    /// File, Edit, Route, View and (when it's on) Developer.
    private void Menus(bool editing)
    {
        if (ImGui.BeginMenu("File"))
        {
            if (editing)
            {
                _editor!.MenuFile();
                ImGui.Separator();
            }

            if (ImGui.MenuItem("Settings..."))
                HighballApp.Instance.Push(new Screens.SettingsScreen(this, _route.Origin.Latitude, _route.Origin.Longitude));
            ImGui.Separator();
            if (ImGui.MenuItem("Exit Route Builder", editing ? "" : "Esc"))
                _exit = true;
            ImGui.EndMenu();
        }

        if (editing && ImGui.BeginMenu("Edit"))
        {
            _editor!.MenuEdit();
            ImGui.EndMenu();
        }

        if (editing && ImGui.BeginMenu("Route"))
        {
            _editor!.MenuRoute();
            if (ImGui.MenuItem("Material Editor...", "M", MaterialsWindow.Instance.Open, true))
                MaterialsWindow.Instance.Toggle();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Map", "F1", _view == ViewMode.Map, true))
                Show(ViewMode.Map);
            if (ImGui.MenuItem("Top down", "F2", _view == ViewMode.TopDown, editing || CanOpenEditor))
                Show(ViewMode.TopDown);
            if (ImGui.MenuItem("3D", "F3", _view == ViewMode.Free, editing || CanOpenEditor))
                Show(ViewMode.Free);
            if (editing)
            {
                ImGui.Separator();
                _editor!.MenuView();
            }

            ImGui.EndMenu();
        }

        if (DeveloperMenu && ImGui.BeginMenu("Developer"))
        {
            DebugWindows.Menu();
            ImGui.Separator();
            if (ImGui.MenuItem("Lighting Test Scene"))
                HighballApp.Instance.Push(new Screens.LightingScreen());
            ImGui.EndMenu();
        }
    }
}
