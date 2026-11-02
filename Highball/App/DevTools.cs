using Brutal.StbApi;
using Highball.Screens;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Time;
using RailWerkz.Vulkan;

namespace Highball.App;

/// Command-line helpers for development and testing:
///   --menu SCREEN                                   open a menu after the splash: single, multi, builder, options, credits
///   --tab NAME                                      with --menu options, open on that tab: display, graphics, audio, simulation
///   --route NAME                                    open that route in the Route Builder (made if there isn't one)
///   --select LAT LON N                              with --route, pick N x N tiles there and build them
///   --view                                          with --route, open its built terrain in 3D
///   --camera LAT LON HEIGHT YAW PITCH               with --view, start there: metres above ground, degrees (yaw 0 north)
///   --sculpt TOOL STEPS                             with --view, once loaded, apply a tool (raise, lower, smooth, flatten,
///                                                   paint) STEPS times at the middle of the screen
///   --panels                                        with --view, open the Frame Statistics and Profiler windows
///   --skybox NAME                                   with --view, show that sky (a file name in Textures/Skyboxes, or Procedural)
///   --lay LAT LON LAT LON                           with --view, once loaded, lay track between two points (branching off
///                                                   any track within 20 m of the first)
///   --shot SECONDS FILE.png [SECONDS FILE.png ...]  save the screen at those times
///   --quit SECONDS                                  close after that long
///   --novsync                                       run uncapped, to time frames (not saved)
///   --skipsplash                                    skip the BRUTAL logo and the splash animation
///   --validation                                    Vulkan validation layers (needs the Vulkan SDK; on in Debug builds)
public static class DevTools
{
    private static readonly List<(double At, string Path)> Shots = [];
    private static double _quitAt = double.MaxValue;
    private static string? _menu;

    /// The options tab asked for on the command line, read once by the options screen.
    public static string? Tab { get; set; }

    /// Tiles to pick and build on opening the route editor (--select), read once by it.
    public static (double Lat, double Lon, int Size)? Select { get; set; }

    private static string? _route;
    private static bool _view;

    /// Open the 3D view's Frame Statistics and Profiler windows (--panels).
    public static bool Panels { get; set; }

    /// The sky to show in the 3D view (--skybox), read once by it.
    public static string? Skybox { get; set; }

    /// Scripted track in the 3D view (--lay), read once by it.
    public static (double Lat0, double Lon0, double Lat1, double Lon1)? Lay { get; set; }

    /// A scripted stroke in the 3D view (--sculpt), read once by it.
    public static (string Tool, int Steps)? Sculpt { get; set; }

    /// Where the 3D view starts (--camera), read once by it.
    public static (double Lat, double Lon, float Height, float Yaw, float Pitch)? Camera { get; set; }

    public static bool NoVSync { get; private set; }
    public static bool SkipSplash { get; private set; }
#if DEBUG
    public static bool Validation { get; private set; } = true;
#else
    public static bool Validation { get; private set; }
#endif

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--menu" when i + 1 < args.Length:
                    _menu = args[++i];
                    break;
                case "--tab" when i + 1 < args.Length:
                    Tab = args[++i];
                    break;
                case "--route" when i + 1 < args.Length:
                    _route = args[++i];
                    break;
                case "--select" when i + 3 < args.Length:
                    var culture = System.Globalization.CultureInfo.InvariantCulture;
                    Select = (double.Parse(args[i + 1], culture), double.Parse(args[i + 2], culture), int.Parse(args[i + 3], culture));
                    i += 3;
                    break;
                case "--view":
                    _view = true;
                    break;
                case "--panels":
                    Panels = true;
                    break;
                case "--skybox" when i + 1 < args.Length:
                    Skybox = args[++i];
                    break;
                case "--lay" when i + 4 < args.Length:
                    var g = System.Globalization.CultureInfo.InvariantCulture;
                    Lay = (double.Parse(args[i + 1], g), double.Parse(args[i + 2], g), double.Parse(args[i + 3], g), double.Parse(args[i + 4], g));
                    i += 4;
                    break;
                case "--sculpt" when i + 2 < args.Length:
                    Sculpt = (args[i + 1], int.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case "--camera" when i + 5 < args.Length:
                    var c = System.Globalization.CultureInfo.InvariantCulture;
                    Camera = (double.Parse(args[i + 1], c), double.Parse(args[i + 2], c), float.Parse(args[i + 3], c), float.Parse(args[i + 4], c), float.Parse(args[i + 5], c));
                    i += 5;
                    break;
                case "--novsync":
                    NoVSync = true;
                    break;
                case "--skipsplash":
                    SkipSplash = true;
                    break;
                case "--validation":
                    Validation = true;
                    break;
                case "--quit" when i + 1 < args.Length:
                    _quitAt = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--shot":
                    while (i + 2 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at))
                    {
                        Shots.Add((at, args[i + 2]));
                        i += 2;
                    }

                    break;
            }
        }

        // Test runs never touch the player's settings.
        if (Shots.Count > 0 || _quitAt < double.MaxValue)
            Settings.ReadOnly = true;
    }

    /// Called by the main menu as it first opens: jumps to the menu asked for on the command line.
    public static void OnMainMenu(HighballApp app)
    {
        var menu = _menu;
        _menu = null;

        Screen? screen = menu switch
        {
            "single" => new SinglePlayerScreen(),
            "multi" => new MultiplayerScreen(),
            "builder" => new RouteBuilderScreen(),
            "options" => new OptionsScreen(),
            "credits" => new CreditsScreen(),
            _ => null,
        };

        if (screen is not null)
            app.Push(screen);

        if (_route is { } name)
        {
            _route = null;
            var route = Routes.RouteInfo.All().FirstOrDefault(r => r.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Routes.RouteInfo.Create(name);
            app.Push(new Builder.RouteEditorScreen(route));
            if (_view)
                app.Push(new Builder.RouteViewScreen(route));
        }
    }

    /// After drawing, before the present: asks for the frame of any screenshot that's due.
    public static void AfterRender(HighballApp app)
    {
        var now = Time.Now;

        for (var i = Shots.Count - 1; i >= 0; i--)
        {
            if (Shots[i].At > now)
                continue;

            var path = Shots[i].Path;
            Shots.RemoveAt(i);
            Gpu.CaptureNextFrame((pixels, width, height) =>
            {
                for (var k = 3; k < pixels.Length; k += 4)
                    pixels[k] = 255;

                Stb.WritePng(path, width, height, 4, pixels);
                Log.Info($"Screenshot: {path}");
            });
            break;
        }

        if (now > _quitAt)
            app.Quit();
    }
}
