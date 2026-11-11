using Brutal.StbApi;
using Highball.Screens;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Time;
using RailWerkz.Vulkan;

namespace Highball.App;

/// Command-line helpers for development and testing:
///   --menu SCREEN                                   open a menu after the splash: single, multi, builder, consists, settings,
///                                                   credits, lighting (the lighting test scene)
///   --lighting PRESET [MODEL]                       with --menu lighting, start at noon, golden or night, with the model
///                                                   black (the game's SD40-2) or source (the old SD40's source file, with PBR maps)
///   --orbit YAW PITCH DISTANCE                      with --menu lighting, where the camera starts (degrees, metres)
///   --skytime YYYY-MM-DD HOUR                       the procedural sky's date and time, in hours on the route's clock
///   --clouds COVERAGE                               how much of the procedural sky is cloud, 0 to 1
///   --tab NAME                                      with --menu settings, open on that tab: display, graphics, lighting, audio, simulation
///   --consist NAME                                  open that saved consist in the Consist Editor
///   --route NAME                                    open that route in the Route Builder (made if there isn't one)
///   --migrate-zone NAME                             move a route made before zones onto its UTM zone's grid (the old
///                                                   folder is kept as NAME.prezone)
///   --select LAT LON N                              with --route, pick N x N tiles there and build them
///   --trackbuild                                    with --select, build lidar only where track runs, standard elsewhere
///   --view                                          with --route, open its built terrain in 3D
///   --camera LAT LON HEIGHT YAW PITCH               with --view, start there: metres above ground, degrees (yaw 0 north)
///   --sculpt TOOL STEPS                             with --view, once loaded, apply a tool (raise, lower, smooth, flatten,
///                                                   paint) STEPS times at the middle of the screen
///   --fly SPEED                                     with --view, fly straight ahead at SPEED metres a second, logging the
///                                                   frame times and the terrain left waiting every second
///   --panels                                        with --view, open the Frame Statistics, Profiler, Threading and Camera windows
///   --skybox NAME                                   with --view, show that sky (a file name in Textures/Skyboxes, or Procedural)
///   --throw LAT LON                                 with --view or --view2d, once loaded, throw the switch nearest there
///   --train CONSIST LAT LON NOTCH                   with --view, once loaded, put that saved consist on the track there
///                                                   (facing away from the camera), throttle at NOTCH, camera following,
///                                                   the Train window and its graphs open
///   --pickvehicle N                                 with --train, pick its Nth vehicle (from 1) once it's placed
///   --traintab NAME                                 with --train, open the Train window on that tab (drive, power, vehicle,
///                                                   coupling, train)
///   --pickcoupling K                                with --train, pick the coupling behind vehicle K (0 the front end)
///   --uncouple K                                    with --train, cut the train behind vehicle K as it's placed: angle
///                                                   cocks closed, hose parted, uncoupled
///   --trainbrake SECONDS HANDLE                     with --train, that long after it's placed close the throttle and put
///                                                   the automatic brake at HANDLE (0 release to 1 full service, e emergency)
///   --railgraph                                     with --view, once loaded, build the trains' rail graph and log its checks
///   --profilestats                                  with --view or --view2d, log how the rail profiles came out
///   --clean                                         with --view or --view2d, no overlays (track grades)
///   --tool N                                        with --view or --view2d, start with tool N (its number key)
///   --trackop LAT LON RADIUS OP                     with --view or --view2d, once loaded, select the control points within
///                                                   RADIUS metres and apply OP (select, straighten, smooth, simplify, join)
///   --lay LAT LON LAT LON                           with --view, once loaded, lay track between two points (branching off
///                                                   any track within 20 m of the first)
///   --shot SECONDS FILE.png [SECONDS FILE.png ...]  save the screen at those times
///   --quit SECONDS                                  close after that long
///   --novsync                                       run uncapped, to time frames (not saved)
///   --noshadows                                     no sun shadows, to see what they cost
///   --sculpthold TOOL RADIUS FROM UNTIL             with --view, hold a brush stroke at the middle of the screen between those seconds
///   --gputimes                                      with --quit, log what each part of the frame cost the GPU on the way out
///   --zoom N                                        with --camera, the lens zoomed in N times (as Alt+wheel does)
///   --taa                                           temporal anti-aliasing on, whatever the settings say (not saved)
///   --skipsplash                                    skip the BRUTAL logo and the splash animation
///   --validation                                    Vulkan validation layers (needs the Vulkan SDK; on in Debug builds)
public static class DevTools
{
    private static readonly List<(double At, string Path)> Shots = [];
    private static double _quitAt = double.MaxValue;
    private static string? _menu;

    /// The lighting test scene's first time of day and model (--lighting), read once by it.
    public static (string Preset, string? Model)? Lighting { get; set; }

    /// How much of the sky is cloud (--clouds), read once by the lighting window.
    public static float? CloudCoverage { get; set; }

    /// No grass, shrubs, trees or rocks (--novegetation), to see what they cost.
    public static bool NoVegetation { get; set; }
    public static bool NoOcclusion { get; set; }
    public static bool NoTemporalAA { get; set; }
    public static bool TemporalAA { get; set; }
    public static string? Weather { get; set; }
    public static bool NoBloom { get; set; }
    public static bool NoGodRays { get; set; }
    public static string? ColorGrade { get; set; }

    /// Which clouds (--cloudtype), read once by the lighting window.
    public static string? CloudType { get; set; }

    /// The procedural sky's date and time (--skytime), read once by the lighting window.
    public static (int Year, int Month, int Day, float Hour)? SkyTime { get; set; }

    /// Where the lighting test scene's camera starts (--orbit), read once by it.
    public static (float Yaw, float Pitch, float Distance)? Orbit { get; set; }

    /// The settings tab asked for on the command line, read once by the settings screen.
    public static string? Tab { get; set; }

    /// Log the GPU's time on each part of the frame when --quit closes the game (--gputimes).
    public static bool GpuTimes { get; private set; }

    /// Tiles to pick and build on opening the route editor (--select), read once by it.
    public static (double Lat, double Lon, int Size)? Select { get; set; }

    /// --trackbuild: --select marks its tiles by the track before building.
    public static bool TrackBuild { get; private set; }

    /// --regenerate: the Planner regenerates every built tile as it opens.
    public static bool Regenerate { get; set; }
    /// Scripted Shape tool steps, one per finished rebuild: move,LAT,LON,EAST,NORTH; straighten,LAT,LON (from the handle there to the next but one); remove,LAT,LON; reset,LAT,LON; undo; redo; save; clearyard,LAT,LON; restoreyard,LAT,LON; follow,LAT,LON,LAT,LON...; view,LAT,LON,HEIGHT; stamp,ID,LAT,LON[,DEGREES[,m]] (a Place palette piece, m mirrored); stampcheck,LAT,LON,REACH (logs the switches and issues there); wireframe,on|off.
    public static Queue<string>? ShapeOps { get; set; }
    /// Solve the track again on opening, rather than use what's saved with the route.
    public static bool Refit { get; private set; }

    /// --look LAT LON ZOOM: where the Planner's map opens.
    public static (double Lat, double Lon, double Zoom)? Look { get; set; }

    private static string? _route;
    private static string? _migrateZone;
    private static string? _consist;
    private static bool _view;
    private static bool _view2D;

    /// Open the 3D view's Frame Statistics and Profiler windows (--panels).
    public static bool Panels { get; set; }

    /// The lamps every signal has lit unless set by hand (--lamps red,yellow,green, all or none).
    public static World.SignalLamps? Lamps { get; set; }

    /// Open the Track Materials window (--materials).
    public static bool Materials { get; set; }

    /// The sky to show in the 3D view (--skybox), read once by it.
    public static string? Skybox { get; set; }

    /// Scripted track in the 3D view (--lay), read once by it.
    public static (double Lat0, double Lon0, double Lat1, double Lon1)? Lay { get; set; }

    /// A switch to throw in the editors (--throw), read once by them.
    public static (double Lat, double Lon)? ThrowAt { get; set; }

    /// --switch LAT LON SIZE: picks the switch nearest there with the Switches tool, and sets its size (-1 automatic, 0 as
    /// mapped, else a frog number).
    public static (double Lat, double Lon, int Size)? SwitchAt { get; set; }
    public static (string Consist, double Lat, double Lon, int Notch)? Train { get; set; }

    /// With --train: when (seconds after it's placed) to work the automatic brake, and to what (0 release to 1 full
    /// service, or -1 emergency), throttle closed. In time order.
    public static List<(double At, double Handle)> TrainBrakes { get; } = [];
    /// With --train: log how smoothly the train moves on the screen, each second.
    public static bool TrainLog { get; private set; }

    /// With --pickvehicle: the vehicle (from 1) to pick once the train's placed.
    public static int? PickVehicle { get; private set; }

    /// With --pickcoupling and --uncouple: the coupling behind that vehicle (from 1; 0 the front end).
    public static int? PickCoupling { get; private set; }
    public static int? Uncouple { get; private set; }

    /// With --traintab: the Train window's tab to open on.
    public static string? TrainTab { get; private set; }

    /// Log the rail profiles' statistics once the terrain opens (--profilestats).
    public static bool ProfileStats { get; private set; }
    public static bool RailGraphReport { get; private set; }

    /// Where to write the audit of the track, once a route's open (TrackAudit).
    public static string? AuditFile { get; set; }

    /// No overlays in the editors (--clean).
    public static bool Clean { get; private set; }

    /// The editors' first tool (--tool), read once by them.
    public static int? Tool { get; set; }

    /// A scripted selection and operation on it in the editors (--trackop), read once by them.
    public static (double Lat, double Lon, double Radius, string Op)? TrackOp { get; set; }

    /// A scripted stroke in the 3D view (--sculpt), read once by it.
    public static (string Tool, int Steps)? Sculpt { get; set; }

    /// A stroke held at the middle of the screen for a while, as a mouse would (--sculpthold).
    public static (string Tool, float Radius, double From, double Until)? SculptHold { get; set; }

    /// Where the 3D view starts (--camera), read once by it.
    public static (double Lat, double Lon, float Height, float Yaw, float Pitch)? Camera { get; set; }
    /// --zoom N: with --camera, the lens zoomed in N times.
    public static float? Zoom { get; set; }

    /// --fly: metres a second straight ahead, for testing streaming.
    public static float? FlySpeed { get; private set; }

    public static bool NoVSync { get; private set; }
    public static bool NoShadows { get; private set; }
    public static bool SkipSplash { get; private set; }
#if DEBUG
    public static bool Validation { get; private set; } = true;
#else
    public static bool Validation { get; private set; }
#endif

    /// A settings file to read and write instead of the player's, for testing what's saved.
    public static string? SettingsFile { get; private set; }

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--menu" when i + 1 < args.Length:
                    _menu = args[++i];
                    break;
                case "--lighting" when i + 1 < args.Length:
                    var preset = args[++i];
                    Lighting = (preset, i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null);
                    break;
                case "--clouds" when i + 1 < args.Length:
                    CloudCoverage = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--weather" when i + 1 < args.Length:
                    Weather = args[++i];
                    break;
                case "--zoom" when i + 1 < args.Length:
                    Zoom = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--taa":
                    TemporalAA = true;
                    break;
                case "--notaa":
                    NoTemporalAA = true;
                    break;
                case "--nobloom":
                    NoBloom = true;
                    break;
                case "--norays":
                    NoGodRays = true;
                    break;
                case "--grade" when i + 1 < args.Length:
                    ColorGrade = args[++i];
                    break;
                case "--noao":
                    NoOcclusion = true;
                    break;
                case "--novegetation":
                    NoVegetation = true;
                    break;
                case "--cloudtype" when i + 1 < args.Length:
                    CloudType = args[++i];
                    break;
                case "--skytime" when i + 2 < args.Length:
                    var date = DateTime.ParseExact(args[i + 1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    SkyTime = (date.Year, date.Month, date.Day, float.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case "--orbit" when i + 3 < args.Length:
                    var o = System.Globalization.CultureInfo.InvariantCulture;
                    Orbit = (float.Parse(args[i + 1], o), float.Parse(args[i + 2], o), float.Parse(args[i + 3], o));
                    i += 3;
                    break;
                case "--tab" when i + 1 < args.Length:
                    Tab = args[++i];
                    break;
                case "--settingsfile" when i + 1 < args.Length:
                    SettingsFile = args[++i];
                    break;
                case "--consist" when i + 1 < args.Length:
                    _consist = args[++i];
                    break;
                case "--route" when i + 1 < args.Length:
                    _route = args[++i];
                    break;
                case "--migrate-zone" when i + 1 < args.Length:
                    _migrateZone = args[++i];
                    break;
                case "--select" when i + 3 < args.Length:
                    var culture = System.Globalization.CultureInfo.InvariantCulture;
                    Select = (double.Parse(args[i + 1], culture), double.Parse(args[i + 2], culture), int.Parse(args[i + 3], culture));
                    i += 3;
                    break;
                case "--look" when i + 3 < args.Length:
                    var invariant = System.Globalization.CultureInfo.InvariantCulture;
                    Look = (double.Parse(args[i + 1], invariant), double.Parse(args[i + 2], invariant), double.Parse(args[i + 3], invariant));
                    i += 3;
                    break;
                case "--shapeops" when i + 1 < args.Length:
                    ShapeOps = new Queue<string>((args[++i].StartsWith('@') ? File.ReadAllText(args[i][1..]) : args[i]).Split(';', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "--trackbuild":
                    TrackBuild = true;
                    break;
                case "--refit":
                    Refit = true;
                    break;
                case "--regenerate":
                    Regenerate = true;
                    break;
                case "--view":
                    _view = true;
                    break;
                case "--view2d":
                    _view2D = true;
                    break;
                case "--panels":
                    Panels = true;
                    break;
                case "--lamps" when i + 1 < args.Length:
                    var named = args[++i].ToLowerInvariant();
                    Lamps = named == "all" ? World.SignalLamps.Red | World.SignalLamps.Yellow | World.SignalLamps.Green
                        : named.Split(',').Aggregate(World.SignalLamps.None, (lit, n) => lit | (Enum.TryParse<World.SignalLamps>(n, true, out var l) ? l : World.SignalLamps.None));
                    break;
                case "--materials":
                    Materials = true;
                    break;
                case "--skybox" when i + 1 < args.Length:
                    Skybox = args[++i];
                    break;
                case "--throw" when i + 2 < args.Length:
                    var w = System.Globalization.CultureInfo.InvariantCulture;
                    ThrowAt = (double.Parse(args[i + 1], w), double.Parse(args[i + 2], w));
                    i += 2;
                    break;
                case "--trainbrake" when i + 2 < args.Length:
                    var tb = System.Globalization.CultureInfo.InvariantCulture;
                    TrainBrakes.Add((double.Parse(args[i + 1], tb), args[i + 2] == "e" ? -1.0 : double.Parse(args[i + 2], tb)));
                    TrainBrakes.Sort((a, b) => a.At.CompareTo(b.At));
                    i += 2;
                    break;
                case "--train" when i + 4 < args.Length:
                    var tc = System.Globalization.CultureInfo.InvariantCulture;
                    Train = (args[i + 1], double.Parse(args[i + 2], tc), double.Parse(args[i + 3], tc), int.Parse(args[i + 4], tc));
                    TrainLog = true;
                    i += 4;
                    break;
                case "--traintab" when i + 1 < args.Length:
                    TrainTab = args[++i];
                    break;
                case "--pickcoupling" when i + 1 < args.Length:
                    PickCoupling = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--uncouple" when i + 1 < args.Length:
                    Uncouple = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--pickvehicle" when i + 1 < args.Length:
                    PickVehicle = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--switch" when i + 3 < args.Length:
                    var v = System.Globalization.CultureInfo.InvariantCulture;
                    SwitchAt = (double.Parse(args[i + 1], v), double.Parse(args[i + 2], v), int.Parse(args[i + 3], v));
                    i += 3;
                    break;
                case "--audit" when i + 1 < args.Length:
                    AuditFile = args[++i];
                    break;
                case "--railgraph":
                    RailGraphReport = true;
                    break;
                case "--profilestats":
                    ProfileStats = true;
                    break;
                case "--clean":
                    Clean = true;
                    break;
                case "--tool" when i + 1 < args.Length:
                    Tool = int.Parse(args[++i]);
                    break;
                case "--trackop" when i + 4 < args.Length:
                    var t = System.Globalization.CultureInfo.InvariantCulture;
                    TrackOp = (double.Parse(args[i + 1], t), double.Parse(args[i + 2], t), double.Parse(args[i + 3], t), args[i + 4]);
                    i += 4;
                    break;
                case "--lay" when i + 4 < args.Length:
                    var g = System.Globalization.CultureInfo.InvariantCulture;
                    Lay = (double.Parse(args[i + 1], g), double.Parse(args[i + 2], g), double.Parse(args[i + 3], g), double.Parse(args[i + 4], g));
                    i += 4;
                    break;
                case "--sculpthold" when i + 4 < args.Length:
                {
                    var n = System.Globalization.CultureInfo.InvariantCulture;
                    SculptHold = (args[i + 1], float.Parse(args[i + 2], n), double.Parse(args[i + 3], n), double.Parse(args[i + 4], n));
                    i += 4;
                    break;
                }
                case "--sculpt" when i + 2 < args.Length:
                    Sculpt = (args[i + 1], int.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case "--camera" when i + 5 < args.Length:
                    var c = System.Globalization.CultureInfo.InvariantCulture;
                    Camera = (double.Parse(args[i + 1], c), double.Parse(args[i + 2], c), float.Parse(args[i + 3], c), float.Parse(args[i + 4], c), float.Parse(args[i + 5], c));
                    i += 5;
                    break;
                case "--fly" when i + 1 < args.Length:
                    FlySpeed = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--noshadows":
                    NoShadows = true;
                    break;
                case "--novsync":
                    NoVSync = true;
                    break;
                case "--gputimes":
                    GpuTimes = true;
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
        if (SettingsFile is not null)
            Settings.FileOverride = SettingsFile;
        else if (Shots.Count > 0 || _quitAt < double.MaxValue)
            Settings.ReadOnly = true;
    }

    /// Called by the main menu as it first opens: jumps to the menu asked for on the command line.
    public static void OnMainMenu(HighballApp app)
    {
        if (_migrateZone is { } migrate)
        {
            _migrateZone = null;
            try
            {
                Routes.ZoneMigration.Run(migrate);
            }
            catch (Exception e)
            {
                Log.Error($"--migrate-zone {migrate}: {e}");
            }
        }

        var menu = _menu;
        _menu = null;

        Screen? screen = menu switch
        {
            "single" => new SinglePlayerScreen(),
            "multi" => new MultiplayerScreen(),
            "builder" => RouteBuilder.Open(),
            "consists" or "consist" => new ConsistEditorScreen(),
            "settings" or "options" => new SettingsScreen(),
            "credits" => new CreditsScreen(),
            "lighting" => new LightingScreen(),
            _ => null,
        };

        if (screen is not null)
            app.Push(screen);

        if (_consist is { } consistName)
        {
            _consist = null;
            var consist = Trains.Consist.All().FirstOrDefault(c => c.Name.Equals(consistName, StringComparison.OrdinalIgnoreCase));
            if (consist is null)
                Log.Warn($"--consist: no saved consist called {consistName}");
            app.Push(new ConsistEditorScreen(consist));
        }

        if (_route is { } name)
        {
            _route = null;
            var route = Routes.RouteInfo.All().FirstOrDefault(r => r.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Routes.RouteInfo.Create(name);
            var builder = new Builder.BuilderScreen(route);
            app.Push(builder);
            if (_view || _view2D)
                builder.Show(_view2D ? Builder.BuilderScreen.Mode.Editor2D : Builder.BuilderScreen.Mode.Editor3D);
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
        {
            if (GpuTimes)
                Log.Info(Profiler.GpuReport());
            app.Quit();
        }
    }
}
