using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.VulkanApi;
using Highball.Screens;
using RailWerkz.Core.Audio;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Time;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using RailWerkz.Vulkan;

namespace Highball.App;

/// The program: a GLFW window rendered with Vulkan 1.3 (Brutal.Glfw, RailWerkz.Vulkan), Dear ImGui on top of it
/// (Brutal.ImGui) for the interface, OpenAL for sound, and a stack of screens over the world.
public sealed class HighballApp : IDisposable
{
    public const string Title = "Highball";

    public static HighballApp Instance { get; private set; } = null!;

    private readonly List<Screen> _screens = [];
    private readonly List<Action> _deferred = [];
    private GlfwWindow _window = null!;
    private GlfwBackend _glfwBackend = null!;
    private VulkanBackend _vulkanBackend = null!;
    private ImGuiContextPtr _imgui;
    private bool _cursorCaptured;
    private bool _quit;
    private int2 _windowedSize = new(1600, 900);
    private int2 _windowedPosition = new(100, 100);
    private double _transitionAt = -10.0;

    public int2 Size => _window.FramebufferSize;
    public string GpuName => Gpu.Name;
    public string ApiVersion => Gpu.ApiVersion;

    public Screen? Top => _screens.Count > 0 ? _screens[^1] : null;

    public static void Run(string[] args)
    {
        ContentPath.AppName = Title;
        DevTools.Parse(args);
        using var app = new HighballApp();
        Instance = app;
        app.Init();
        app.Loop();
    }

    private unsafe void Init()
    {
        Log.Init();
        Settings.Load();
        Log.Info($"{Title} {typeof(HighballApp).Assembly.GetName().Version} // RailWerkz");

        if (!Glfw.Init())
        {
            var code = Glfw.GetError(out var message);
            throw new InvalidOperationException($"GLFW: {code} {message}");
        }

        if (!Glfw.VulkanSupported())
            throw new InvalidOperationException("Vulkan is not available. Update the graphics driver.");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
        Glfw.WindowHint(GlfwWindowHint.ScaleToMonitor, 1);
        // (Hidden until it's where it was left; test runs always open the same.)
        var restore = !Settings.ReadOnly && Settings.Current.WindowWidth > 0;
        if (restore)
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);

        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = Title, Size = _windowedSize });
        if (restore)
            RestoreWindow();

        var names = Glfw.GetRequiredInstanceExtensions(out var count);
        var extensions = new List<string>();
        for (var i = 0; i < count; i++)
            extensions.Add(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)names[i])!);

        Gpu.Init(extensions, instance =>
        {
            var result = Glfw.CreateWindowSurface(instance, _window, out var surface);
            return result == 0 ? surface : throw new InvalidOperationException($"Vulkan: no window surface ({result})");
        }, _window.FramebufferSize, VSyncWanted, DevTools.Validation);

        Input.Attach(_window);
        _window.OnClose += _ => _quit = true;

        _imgui = ImGui.CreateContext();
        ImGui.GetIO().IniFilename = default;
        _glfwBackend = new GlfwBackend(_window, GlfwBackend.ClientApi.Vulkan);
        var backend = new VulkanBackend.CreateInfo
        {
            Device = Gpu.Device,
            GraphicsQueue = Gpu.Queue,
            RenderPass = Gpu.PresentPass,
            SubPass = 0,
            MinImageCount = Gpu.MinImageCount,
            ImageCount = Gpu.ImageCount,
            SampleCount = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = 1024, // one pool for every texture: the backend can only free sets from its first (MapTiles keeps up to 320)
            MinAllocationSize = ByteSize.FromMegabytes(1),
        };
        _vulkanBackend = new VulkanBackend(in backend);
        Style.LoadFonts();
        ApplyTheme();

        if (Settings.Current.Fullscreen)
            SetFullscreen(true);

        AudioEngine.Init();
        Push(DevTools.SkipSplash ? new SplashScreen() : new BrutalScreen());
    }

    /// A texture for ImGui to draw. Remove it with RemoveTexture before disposing the texture.
    public ImTextureRef AddTexture(Texture2D texture) =>
        _vulkanBackend.AddTexture(texture.Sampler, texture.View, VkImageLayout.ShaderReadOnlyOptimal);

    public void RemoveTexture(ImTextureRef texture) => Gpu.Defer(() => _vulkanBackend.RemoveTexture(texture));

    public void ApplyTheme()
    {
        Style.SetAccent(Settings.Current.Accent);
        Tween.ReduceMotion = Settings.Current.ReduceMotion;
    }

    /// The window as it was last left, if that's still on a screen.
    private void RestoreWindow()
    {
        var s = Settings.Current;
        _windowedSize = new int2(Math.Max(s.WindowWidth, MinWindow), Math.Max(s.WindowHeight, MinWindow));
        _window.Size = _windowedSize;
        if (OnAScreen(s.WindowX, s.WindowY))
        {
            _windowedPosition = new int2(s.WindowX, s.WindowY);
            _window.Position = _windowedPosition;
        }

        if (s.WindowMaximized)
            _window.Maximize();
        _window.Visible = true;
    }

    /// Whether a window placed here would have its title bar on one of the screens.
    private static unsafe bool OnAScreen(int x, int y)
    {
        foreach (var monitor in Glfw.GetMonitors())
        {
            monitor.GetMonitorWorkArea(out var at, out var size);
            if (x + MinWindow > at.X && x < at.X + size.X - MinWindow && y >= at.Y - MinWindow && y < at.Y + size.Y - MinWindow)
                return true;
        }

        return false;
    }

    private const int MinWindow = 200;

    /// Keeps the window's place while it's an ordinary window, to go back to from fullscreen and to save.
    private void TrackWindow()
    {
        if (_borderless || _window.IsFullscreen || _window.IsMaximised || _window.IsIconified)
            return;
        (_windowedSize, _windowedPosition) = (_window.Size, _window.Position);
    }

    private void SaveWindow()
    {
        var s = Settings.Current;
        (s.WindowWidth, s.WindowHeight, s.WindowX, s.WindowY) = (_windowedSize.X, _windowedSize.Y, _windowedPosition.X, _windowedPosition.Y);
        s.WindowMaximized = _borderless ? _maximizedBeforeFullscreen : _window.IsMaximised;
    }

    /// Fullscreen is a borderless window covering the monitor, not exclusive mode: clicking onto another
    /// window or screen leaves it where it is instead of minimizing it.
    private bool _borderless;
    private bool _maximizedBeforeFullscreen;

    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen && !_borderless && MonitorUnderWindow() is { } monitor)
        {
            _maximizedBeforeFullscreen = _window.IsMaximised;
            if (_maximizedBeforeFullscreen)
                _window.Restore();
            _borderless = true;
            _window.SetAttribute(GlfwWindowAttribute.Decorated, 0);
            var mode = monitor.VideoMode;
            _window.SetWindowed(monitor.Position, new int2(mode.Width, mode.Height));
        }
        else if (!fullscreen && _borderless)
        {
            _borderless = false;
            _window.SetAttribute(GlfwWindowAttribute.Decorated, 1);
            _window.SetWindowed(_windowedPosition, _windowedSize);
            if (_maximizedBeforeFullscreen)
                _window.Maximize();
        }

        ApplyVSync();
    }

    /// The monitor holding the middle of the window, or the primary one.
    private GlfwMonitor? MonitorUnderWindow()
    {
        var middle = _window.Position + _window.Size / 2;
        foreach (var monitor in Glfw.GetMonitors())
        {
            var (at, mode) = (monitor.Position, monitor.VideoMode);
            if (middle.X >= at.X && middle.X < at.X + mode.Width && middle.Y >= at.Y && middle.Y < at.Y + mode.Height)
                return monitor;
        }

        return Glfw.PrimaryMonitor;
    }

    private static bool VSyncWanted => Settings.Current.VSync && !DevTools.NoVSync;

    public void ApplyVSync() => Gpu.VSync = VSyncWanted;

    // Screens. Changes wait for the end of the frame so a screen can replace itself mid-update.
    public void Push(Screen screen) => _deferred.Add(() =>
    {
        _screens.Add(screen);
        BeginTransition();
        screen.Enter();
    });

    public void Pop() => _deferred.Add(() =>
    {
        if (_screens.Count == 0)
            return;

        var top = _screens[^1];
        _screens.RemoveAt(_screens.Count - 1);
        top.Exit();
        BeginTransition();
        Top?.Resume();
    });

    public void Replace(Screen screen) => _deferred.Add(() =>
    {
        if (_screens.Count > 0)
        {
            var top = _screens[^1];
            _screens.RemoveAt(_screens.Count - 1);
            top.Exit();
        }

        _screens.Add(screen);
        BeginTransition();
        screen.Enter();
    });

    /// Clears the stack down to a fresh main menu.
    public void ToMainMenu() => _deferred.Add(() =>
    {
        for (var i = _screens.Count - 1; i >= 0; i--)
            _screens[i].Exit();

        _screens.Clear();
        var menu = new MainMenuScreen();
        _screens.Add(menu);
        BeginTransition();
        menu.Enter();
    });

    public void Quit() => _quit = true;

    /// Every screen change dips the deck: a short fade from the background colour.
    private void BeginTransition() => _transitionAt = Tween.Now;

    private void SetCursorCaptured(bool captured)
    {
        if (captured == _cursorCaptured)
            return;

        _cursorCaptured = captured;
        Glfw.PInvoke.SetInputMode(_window, Glfw.Constants.GLFW_CURSOR, captured ? Glfw.Constants.GLFW_CURSOR_DISABLED : Glfw.Constants.GLFW_CURSOR_NORMAL);

        if (Glfw.PInvoke.RawMouseMotionSupported() != 0)
            Glfw.PInvoke.SetInputMode(_window, Glfw.Constants.GLFW_RAW_MOUSE_MOTION, captured ? 1 : 0);

        Input.SetCaptured(captured);
    }

    private void Loop()
    {
        while (!_quit && !_window.ShouldClose)
        {
            Glfw.PollEvents();
            TrackWindow();
            Input.BeginFrame();
            Time.Tick();

            var size = _window.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0 || _window.IsIconified)
            {
                Thread.Sleep(30);
                Input.EndFrame();
                continue;
            }

            Profiler.BeginFrame(Time.RealDelta);
            var frame = Profiler.Sample("Frame");

            _glfwBackend.NewFrame();
            _vulkanBackend.NewFrame();
            ImGui.NewFrame();
            ImString.SharedStorage.Reset();
            Tween.BeginFrame(Time.RealDelta);

            var display = ImGui.GetIO().DisplaySize;
            Style.Scale = Math.Clamp(display.Y / 1080f, 0.6f, 2.5f) * Settings.Current.UiScale;

            AudioEngine.Update();

            var top = Top;
            SetCursorCaptured(top?.CapturesMouse == true && _window.IsFocused);
            using (Profiler.Sample("Update"))
                top?.Update(Time.RealDelta);
            var ui = Profiler.Sample("UI");

            // The deck: one transparent window over the whole display that every screen draws on.
            ImGui.SetNextWindowPos(new float2(0f, 0f));
            ImGui.SetNextWindowSize(display);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollWithMouse;
            ImGui.Begin("##deck"u8, flags);
            var draw = ImGui.GetWindowDrawList();

            // Windows above the deck (future tools); while one has the pointer or a text field, the deck ignores input.
            // AllowWhenBlockedByActiveItem: a deck button being held is itself the active item, and without it the
            // pointer would read as blocked on the very frame the button is released, so its click was dropped.
            Keyboard.PointerBlocked = !ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            Keyboard.KeysBlocked = ImGui.GetIO().WantTextInput;

            // Draw every screen from the lowest one the top lets show through.
            var first = _screens.Count - 1;
            while (first > 0 && _screens[first].IsOverlay)
                first--;

            for (var i = Math.Max(0, first); i < _screens.Count; i++)
                using (Profiler.Sample(_screens[i].GetType().Name))
                    _screens[i].Draw(draw, display);

            ImGui.End();
            ImGui.PopStyleVar(2);
            using (Profiler.Sample("Windows"))
                top?.DrawWindows();

            var fade = 1f - Tween.Progress(_transitionAt, 0.35f, Ease.OutCubic);
            if (fade > 0.001f)
                ImGui.GetForegroundDrawList().AddRectFilled(new float2(0f, 0f), display, Style.Background.WithAlpha(fade * 0.6f));

            using (Profiler.Sample("ImGui render"))
                ImGui.Render();
            ui.Dispose();

            // The image is acquired as late as possible, once the interface is built.
            bool began;
            using (Profiler.Sample("Wait for GPU"))
                began = Gpu.BeginFrame(size);
            if (began)
            {
                using (Profiler.Sample("Render", Gpu.Cmd))
                {
                    // The world behind: whatever the top screen is looking at, or black.
                    var world = top?.View == WorldView.World ? top as IWorldScreen : null;
                    world?.RenderWorld(Gpu.Cmd, size.X, size.Y);

                    Gpu.BeginPresentPass(new float4(0f, 0f, 0f, 1f));
                    using (Profiler.Sample("Tonemap", Gpu.Cmd))
                        world?.CompositeWorld(Gpu.Cmd);
                    using (Profiler.Sample("Interface", Gpu.Cmd))
                        _vulkanBackend.RenderDrawData(ImGui.GetDrawData(), Gpu.Cmd);
                    Gpu.EndPresentPass();
                }

                DevTools.AfterRender(this);
                using (Profiler.Sample("Present"))
                    Gpu.EndFrame();
            }

            frame.Dispose();

            Input.EndFrame();

            // Changes can queue more changes (a screen pushing another as it enters); run until there are none.
            while (_deferred.Count > 0)
            {
                var changes = _deferred.ToArray();
                _deferred.Clear();
                foreach (var change in changes)
                    change();
            }
        }
    }

    public void Dispose()
    {
        SaveWindow();
        foreach (var screen in _screens)
            screen.Exit();

        AudioEngine.Shutdown();
        Gpu.Flush(); // textures the screens let go of are removed from ImGui while its renderer still exists
        World.Sky.Shutdown();
        World.SunShadows.Shutdown();
        World.Clouds.Shutdown();
        _vulkanBackend?.Dispose();
        _glfwBackend?.Dispose();

        if (!_imgui.IsNull())
            ImGui.DestroyContext(_imgui);

        Gpu.Shutdown();
        _window?.Dispose();
        Glfw.Terminate();
        Settings.Save();
        Log.Shutdown();
    }
}
