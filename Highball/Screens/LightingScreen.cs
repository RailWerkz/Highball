using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;

namespace Highball.Screens;

/// A place to judge the light: the SD40 on a short length of track, with rows of test spheres beside it, under a sun you
/// can move. Rail top is y = 0, as in the locomotive's files. --menu lighting opens it; --lighting and --orbit fix what a
/// screenshot sees, so the same views can be compared after every shader change.
public sealed class LightingScreen : Screen, IWorldScreen
{
    private const float GroundY = -0.6f;
    private const float BallastY = -0.22f;
    private const float TieTopY = -0.2f; // the rail's foot, less the tie plate's depth
    private const float TrackHalfLength = 60f;
    private const float TieSpacing = 0.6f;
    private const float RailOffset = 1.435f * 0.5f + 0.0373f;
    private const float EngineLength = 20.437f;

    private static readonly (string Id, string Label)[] Engines =
    [
        ("csx", "SD40, as the game has it"),
        ("source", "SD40 source (PBR maps)"),
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
                _engine = model;
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
        var grass = Texture2D.LoadDds("Terrain/Grass/Grass_Albedo.dds", srgb: true);
        var ballast = Texture2D.Load("Track/ballast.png", srgb: true);
        // The grass map is detail for a land cover's colour to tint (see GroundTextures), so it brings its own here.
        var ground = Own(new RenderModel(Plane(1500f, 4f), grass, new float4(0.14f, 0.17f, 0.08f, 1f), 0f, 0.95f, grass));
        var bed = Own(new RenderModel(Ballast(), ballast, new float4(1f), 0f, 0.9f, ballast));
        _scene.Add(new ModelDraw(ground, default, CastsShadow: false));
        _scene.Add(new ModelDraw(bed, default, CastsShadow: false));

        // The rail head is worn bright; everything under it has rusted.
        var head = Own(new RenderModel(RailMesh(top: true), null, new float4(0.62f, 0.62f, 0.63f, 1f), 1f, 0.28f));
        var web = Own(new RenderModel(RailMesh(top: false), null, new float4(0.20f, 0.11f, 0.07f, 1f), 0f, 0.8f));
        foreach (var side in (float[])[-1f, 1f])
        {
            _scene.Add(new ModelDraw(head, new float3(side * RailOffset, 0f, 0f)));
            _scene.Add(new ModelDraw(web, new float3(side * RailOffset, 0f, 0f)));
        }

        var tie = Own(new RenderModel(Model.Load("Track/tie.glb")));
        var plate = Own(new RenderModel(Model.Load("Track/plate.glb")));
        var tieHeight = tie.Mesh.BoundsMax.Y;
        var plateHeight = plate.Mesh.BoundsMax.Y;
        for (var z = -TrackHalfLength; z <= TrackHalfLength; z += TieSpacing)
        {
            _scene.Add(new ModelDraw(tie, new float3(0f, TieTopY - tieHeight, z)));
            foreach (var side in (float[])[-1f, 1f])
                _scene.Add(new ModelDraw(plate, new float3(side * RailOffset, -NetworkMeshes.RailTop - plateHeight, z)));
        }
    }

    private RenderModel Engine(string id)
    {
        if (_engines.TryGetValue(id, out var engine))
            return engine;

        var model = id == "source"
            ? Model.Load("Locomotives/SD40/Source/emd-sd40_diesel_electric_locomotive_1k.glb", targetLength: EngineLength)
            : Model.Load("Locomotives/SD40/Assembled/sd40_csx.glb");
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
        if (on > 0f)
        {
            var ahead = new float3(0f, -0.04f, 1f);
            lamps.Add(new LocalLight(0.0, 3.05, 9.15, 350f, new float3(2000f, 1800f, 1500f) * on, ahead, 0.985f, 0.95f, 0.35f, new float3(5f, 4.4f, 3.4f) * on));
            foreach (var side in (float[])[-1f, 1f])
                lamps.Add(new LocalLight(side * 1.1, 1.35, 10.0, 120f, new float3(350f, 320f, 265f) * on, new float3(0f, -0.08f, 1f), 0.97f, 0.9f, 0.2f, new float3(4f, 3.6f, 2.8f) * on));
        }
        if (_showSpheres)
            AddSpheres(_draws);
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height) =>
        _renderer.Render(cmd, new int2(width, height), _camera, null, null, _draws);

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
            ImGui.TextDisabled("Drag to orbit, wheel to zoom, Esc to leave"u8);
        });
        _panel.Apply();
    }

    private static Mesh Plane(float half, float tile)
    {
        var y = GroundY;
        Vertex Corner(float x, float z) => new(new float3(x, y, z), new float3(0f, 1f, 0f), new float2(x / tile, z / tile));
        return Mesh.Create([Corner(-half, -half), Corner(half, -half), Corner(half, half), Corner(-half, half)], [0u, 1u, 2u, 0u, 2u, 3u]);
    }

    /// The track bed: a flat top with sloping shoulders, its stone a metre and a half to the tile.
    private static Mesh Ballast()
    {
        (float X, float Y)[] section = [(-3.0f, GroundY), (-1.7f, BallastY), (1.7f, BallastY), (3.0f, GroundY)];
        return Sweep(section, 1.5f);
    }

    /// One rail, foot at -RailTop and head at 0, in two pieces so the head can be a different material.
    private static Mesh RailMesh(bool top)
    {
        var profile = NetworkMeshes.RailProfile;
        var section = profile.Select(p => (X: p.U, Y: p.Y - NetworkMeshes.RailTop)).ToArray();
        return top ? Sweep(section[7..11], 1f) : Sweep(section[..8].Concat(section[10..]).ToArray(), 1f, skip: 7);
    }

    /// A section swept along the track, each flat of it shaded on its own. skip leaves out the flat starting at that point.
    private static Mesh Sweep((float X, float Y)[] section, float tile, int skip = -1)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var along = 0f;
        for (var i = 0; i + 1 < section.Length; i++)
        {
            var (a, b) = (section[i], section[i + 1]);
            var length = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (i != skip)
            {
                var normal = new float3(b.Y - a.Y, a.X - b.X, 0f) / MathF.Max(length, 1e-5f);
                if (normal.Y < 0f || (normal.Y == 0f && (a.X + b.X) * normal.X < 0f))
                    normal = -normal;

                var first = (uint)vertices.Count;
                vertices.Add(new Vertex(new float3(a.X, a.Y, -TrackHalfLength), normal, new float2(along / tile, -TrackHalfLength / tile)));
                vertices.Add(new Vertex(new float3(b.X, b.Y, -TrackHalfLength), normal, new float2((along + length) / tile, -TrackHalfLength / tile)));
                vertices.Add(new Vertex(new float3(b.X, b.Y, TrackHalfLength), normal, new float2((along + length) / tile, TrackHalfLength / tile)));
                vertices.Add(new Vertex(new float3(a.X, a.Y, TrackHalfLength), normal, new float2(along / tile, TrackHalfLength / tile)));
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }

            along += length;
        }

        return Mesh.Create(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
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
