using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;

namespace Highball.Screens;

/// A place to judge the light: the SD40-2 on a short length of track, with rows of test spheres beside it, under a sun you
/// can move. Rail top is y = 0, as in the locomotive's files. --menu lighting opens it; --lighting and --orbit fix what a
/// screenshot sees, so the same views can be compared after every shader change.
public sealed class LightingScreen : Screen, IWorldScreen
{
    private const float GroundY = DisplayTrack.GroundY;
    private const float Sd40Length = 20.437f;

    private static readonly (string Id, string Label)[] Engines =
    [
        ("black", "SD40-2, as the game has it"),
        ("source", "Old SD40 source (PBR maps)"),
    ];

    private readonly WorldRenderer _renderer = new();
    private readonly LightingPanel _panel;
    private readonly Camera _camera = new();
    private readonly List<IDisposable> _owned = [];
    private readonly List<ModelDraw> _scene = [];
    private readonly Dictionary<string, RenderModel> _engines = [];
    private readonly List<ModelDraw> _draws = [];

    private string _engine = Engines[0].Id;
    private bool _showSpheres = true;

    private float _yaw = 215f * MathF.PI / 180f;
    private float _pitch = -12f * MathF.PI / 180f;
    private float _distance = 26f;

    public override WorldView View => WorldView.World;

    public LightingScreen() => _panel = new LightingPanel(_renderer);

    public override void Enter()
    {
        if (DevTools.Lighting is { } start)
        {
            DevTools.Lighting = null;
            _panel.Preset(start.Preset);
            if (start.Model is { } model)
                _engine = model == "csx" ? "black" : model; // csx was the SD40's
        }

        if (DevTools.Orbit is { } orbit)
        {
            DevTools.Orbit = null;
            _yaw = orbit.Yaw * MathF.PI / 180f;
            _pitch = orbit.Pitch * MathF.PI / 180f;
            _distance = orbit.Distance;
        }

        if (DevTools.Skybox is { } skybox)
        {
            DevTools.Skybox = null;
            _renderer.Sky.Use(skybox);
        }

        BuildScene();
    }

    public override void Exit()
    {
        foreach (var engine in _engines.Values)
            engine.Dispose();
        foreach (var owned in _owned)
            owned.Dispose();
        _renderer.Dispose();
    }

    private T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    private void BuildScene()
    {
        var track = Own(new DisplayTrack());
        _scene.AddRange(track.Draws);
    }

    private RenderModel Engine(string id)
    {
        if (_engines.TryGetValue(id, out var engine))
            return engine;

        var model = id == "source"
            ? Model.Load("Locomotives/SD40/Source/emd-sd40_diesel_electric_locomotive_1k.glb", targetLength: Sd40Length)
            : Model.Load("Locomotives/SD40-2/Assembled/sd40_2_black.glb");
        return _engines[id] = new RenderModel(model);
    }

    private RenderModel? _sphere;

    /// Two rows beside the track, rough on the left to polished on the right: metal over non-metal. Each should change
    /// smoothly along its row, and the metal should show the sky rather than go black.
    private void AddSpheres(List<ModelDraw> draws)
    {
        _sphere ??= Own(new RenderModel(Sphere(), null, new float4(1f), 0f, 0.5f));
        const int count = 7;
        for (var i = 0; i < count; i++)
        {
            var roughness = 1f - i / (count - 1f);
            var z = (i - (count - 1) * 0.5f) * 1.4f;
            draws.Add(new ModelDraw(_sphere, new float3(6f, GroundY + 0.6f, z), Scale: 0.6f, BaseColor: new float4(0.5f, 0.5f, 0.5f, 1f), Metallic: 0f, Roughness: roughness));
            draws.Add(new ModelDraw(_sphere, new float3(6f, GroundY + 1.8f, z), Scale: 0.6f, BaseColor: new float4(0.95f, 0.93f, 0.88f, 1f), Metallic: 1f, Roughness: roughness));
        }
    }

    public override void Update(float dt)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            HighballApp.Instance.Pop();

        if (!Keyboard.PointerBlocked)
        {
            if (Input.Mouse(GlfwMouseButton.Left) || Input.Mouse(GlfwMouseButton.Right))
            {
                _yaw += Input.MouseDelta.X * 0.005f;
                _pitch -= Input.MouseDelta.Y * 0.005f;
            }

            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, Input.Scroll), 2f, 150f);
        }

        // Round the middle of the engine, never under the ground.
        var target = new float3(0f, 1.9f, 0f);
        var highest = MathF.Asin(Math.Clamp((target.Y - GroundY - 0.3f) / _distance, 0f, 1f));
        _pitch = Math.Clamp(_pitch, -1.45f, highest);
        _camera.Yaw = _yaw;
        _camera.Pitch = _pitch;
        var at = target - _camera.Forward * _distance;
        (_camera.X, _camera.Y, _camera.Z) = (at.X, at.Y, at.Z);

        _draws.Clear();
        _draws.AddRange(_scene);
        _draws.Add(new ModelDraw(Engine(_engine), default));

        // Its headlight and ditch lights, on towards dusk.
        var lamps = _renderer.SceneLights;
        lamps.Clear();
        var on = 1f - MathEx.SmoothStep(-0.02f, 0.1f, _renderer.Sky.RealSunHeight);
        if (on > 0f && Trains.VehicleCatalog.Find("sd40-2") is { } def)
        {
            // The front end's lamps, where the catalog has them: the headlight's beam shared between its lamps.
            var heads = Math.Max(1, def.Lamps.Count(l => l.Front && !l.Ditch));
            foreach (var lamp in def.Lamps.Where(l => l.Front))
            {
                var aim = MathEx.Normalize(lamp.Facing + new float3(0f, lamp.Ditch ? -0.08f : -0.04f, 0f));
                var (x, y, z) = (lamp.Position.X, lamp.Position.Y, lamp.Position.Z);
                lamps.Add(lamp.Ditch
                    ? new LocalLight(x, y, z, 120f, new float3(350f, 320f, 265f) * on, aim, 0.97f, 0.9f, 0.2f, new float3(4f, 3.6f, 2.8f) * on)
                    : new LocalLight(x, y, z, 350f, new float3(2000f, 1800f, 1500f) * (on / heads), aim, 0.985f, 0.95f, 0.35f, new float3(5f, 4.4f, 3.4f) * on));
            }
        }
        if (_showSpheres)
            AddSpheres(_draws);
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _panel.Apply();
        _renderer.Render(cmd, new int2(width, height), _camera, null, null, _draws);
    }

    public void CompositeWorld(CommandBuffer cmd) => _renderer.Composite(cmd);

    public override void Draw(ImDrawListPtr draw, float2 size) { }

    public override void DrawWindows()
    {
        // Always open here: it's what the screen is for.
        _panel.Open = true;
        _panel.Draw(() =>
        {
            ImGui.Separator();
            foreach (var (id, label) in Engines)
            {
                if (ImGui.RadioButton(label, _engine == id))
                    _engine = id;
            }

            ImGui.Checkbox("Test spheres"u8, ref _showSpheres);
            ImGui.Separator();
            if (ImGui.Button("Settings"u8))
                HighballApp.Instance.Push(new SettingsScreen(this));
            ImGui.Separator();
            ImGui.TextDisabled("Drag to orbit, wheel to zoom, Esc to leave"u8);
        });
    }

    private static Mesh Sphere()
    {
        const int rings = 32;
        const int segments = 64;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        for (var r = 0; r <= rings; r++)
        {
            var (sinTheta, cosTheta) = MathF.SinCos(MathF.PI * r / rings);
            for (var s = 0; s <= segments; s++)
            {
                var (sinPhi, cosPhi) = MathF.SinCos(2f * MathF.PI * s / segments);
                var n = new float3(sinTheta * cosPhi, cosTheta, sinTheta * sinPhi);
                vertices.Add(new Vertex(n, n, new float2((float)s / segments, (float)r / rings)));
            }
        }

        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = (uint)(r * (segments + 1) + s);
                var b = a + segments + 1;
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }

        return Mesh.Create(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
    }
}
