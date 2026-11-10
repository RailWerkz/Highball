using Brutal.ImGuiApi;
using Highball.App;
using Highball.Builder;
using Highball.Routes;
using RailWerkz.UI;

namespace Highball.Screens;

/// The way into the Route Builder: start a route or open one.
public sealed class RouteBuilderScreen : MenuScreen
{
    public RouteBuilderScreen() : base("ROUTE BUILDER")
    {
        Menu.Add(new MenuItem { Label = "NEW ROUTE", OnSelect = () => HighballApp.Instance.Push(new NewRouteScreen()) })
            .Add(new MenuItem { Label = "OPEN ROUTE", OnSelect = () => HighballApp.Instance.Push(new OpenRouteScreen()), SeparatorAfter = true })
            .Add(new MenuItem { Label = "BACK", OnSelect = Back });
    }
}

/// Names a new route, then opens it in the editor.
public sealed class NewRouteScreen : MenuScreen
{
    private string _name = "";
    private bool _editing = true;

    public NewRouteScreen() : base("NEW ROUTE")
    {
        Menu.Look = MenuLook.Rows;
        Menu.RowWidth = 640f;
        Menu.Add(new MenuItem { Label = "Name", Value = () => _editing ? _name + "_" : _name, OnSelect = () => _editing = true, SeparatorAfter = true })
            .Add(new MenuItem { Label = "Create", OnSelect = Create, Enabled = () => _name.Trim().Length > 0 })
            .Add(new MenuItem { Label = "Back", OnSelect = Back });
    }

    private void Create()
    {
        var route = RouteInfo.Create(_name);
        HighballApp.Instance.Replace(new BuilderScreen(route));
    }

    public override void Update(float dt)
    {
        if (!_editing)
        {
            Menu.Update();
            return;
        }

        if (Keyboard.Edit(ref _name, 32, c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'))
        {
            _editing = false;
            if (_name.Trim().Length > 0)
                Create();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _editing = false;
            Sfx.Back();
        }
    }

    protected override (string Key, string Action)[] HintKeys() => _editing
        ? [("ENTER", "Create"), ("ESC", "Cancel")]
        : [("ENTER", "Select"), ("ESC", "Back")];
}

/// Every route on disk: Enter opens one in the editor, Del deletes it (once confirmed).
public sealed class OpenRouteScreen : MenuScreen
{
    private readonly List<RouteInfo> _routes = RouteInfo.All();

    public OpenRouteScreen() : base("OPEN ROUTE")
    {
        for (var i = 0; i < _routes.Count; i++)
        {
            var route = _routes[i];
            Menu.Add(new MenuItem { Label = route.Name.ToUpperInvariant(), Detail = Path.GetFullPath(route.Folder), OnSelect = () => HighballApp.Instance.Replace(new BuilderScreen(route)), SeparatorAfter = i == _routes.Count - 1 });
        }

        if (_routes.Count == 0)
            Menu.Add(new MenuItem { Label = "NO ROUTES", Enabled = () => false, SeparatorAfter = true });

        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
    }

    public override void Update(float dt)
    {
        Menu.Update();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && Menu.Selected < _routes.Count && _routes.Count > 0)
            HighballApp.Instance.Push(new DeleteRouteScreen(_routes[Menu.Selected]));
    }

    protected override (string Key, string Action)[] HintKeys() => _routes.Count > 0
        ? [("ENTER", "Open"), ("DEL", "Delete"), ("ESC", "Back")]
        : [("ESC", "Back")];
}

/// Deletes a route's folder (its tiles, network and everything built), once confirmed.
public sealed class DeleteRouteScreen : MenuScreen
{
    public DeleteRouteScreen(RouteInfo route) : base($"DELETE {route.Name.ToUpperInvariant()}?")
    {
        // Cancel first, so the highlight starts on it and a stray Enter deletes nothing.
        Menu.Add(new MenuItem { Label = "CANCEL", OnSelect = Back })
            .Add(new MenuItem { Label = "DELETE", OnSelect = () => Delete(route) });
    }

    private static void Delete(RouteInfo route)
    {
        try
        {
            // Only ever a route's own folder, straight under the routes root.
            if (route.Id.Length == 0 || Path.GetDirectoryName(Path.GetFullPath(route.Folder)) != Path.GetFullPath(RouteInfo.Root).TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException($"{route.Folder} isn't a route folder");
            Directory.Delete(route.Folder, true);
            RailWerkz.Core.Logging.Log.Info($"Route {route.Name} deleted from {route.Folder}");
        }
        catch (Exception e)
        {
            RailWerkz.Core.Logging.Log.Error($"Route {route.Name}: couldn't delete: {e.Message}");
        }

        // Back to the list, read again.
        HighballApp.Instance.Pop();
        HighballApp.Instance.Replace(new OpenRouteScreen());
    }
}
