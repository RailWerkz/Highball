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
        HighballApp.Instance.Replace(new RouteEditorScreen(route));
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

/// Every route on disk, to open in the editor.
public sealed class OpenRouteScreen : MenuScreen
{
    public OpenRouteScreen() : base("OPEN ROUTE")
    {
        var routes = RouteInfo.All();
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            Menu.Add(new MenuItem { Label = route.Name.ToUpperInvariant(), OnSelect = () => HighballApp.Instance.Replace(new RouteEditorScreen(route)), SeparatorAfter = i == routes.Count - 1 });
        }

        if (routes.Count == 0)
            Menu.Add(new MenuItem { Label = "NO ROUTES", Enabled = () => false, SeparatorAfter = true });

        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
    }
}
