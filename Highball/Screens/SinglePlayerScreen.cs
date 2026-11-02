using Highball.Routes;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Screens;

/// Pick a route to drive: every route installed under Content/Routes.
public sealed class SinglePlayerScreen : MenuScreen
{
    public SinglePlayerScreen() : base("SINGLE PLAYER")
    {
        var routes = RouteInfo.All();

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            Menu.Add(new MenuItem { Label = route.Name.ToUpperInvariant(), OnSelect = () => Choose(route), SeparatorAfter = i == routes.Count - 1 });
        }

        if (routes.Count == 0)
            Menu.Add(new MenuItem { Label = "NO ROUTES", Enabled = () => false, SeparatorAfter = true });

        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
        Menu.Selected = Math.Max(0, routes.FindIndex(r => r.Id == Settings.Current.LastRoute));
    }

    private void Choose(RouteInfo route)
    {
        Settings.Current.LastRoute = route.Id;
        Settings.Save();
        Notify("Not available yet.");
    }
}
