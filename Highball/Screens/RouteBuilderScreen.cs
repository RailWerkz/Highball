using Highball.App;
using Highball.Builder;
using Highball.Routes;

namespace Highball.Screens;

/// The way into the Route Builder: straight into the one world, no route to pick.
public static class RouteBuilder
{
    public static BuilderScreen Open() => new(RouteInfo.World());

    public static void Push() => HighballApp.Instance.Push(Open());
}
