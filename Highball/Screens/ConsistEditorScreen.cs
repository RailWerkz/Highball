using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Trains;
using Highball.World;
using RailWerkz.Core.Audio;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using RailWerkz.Vulkan;

namespace Highball.Screens;

/// Makes up a train. The catalog on the left (locomotives, then each kind of rolling stock), the consist on the right from
/// the front down, and whichever vehicle was picked last standing on a length of track in the middle, to walk round.
/// Vehicles go in with ADD, Enter, or by dragging them into the list; rows move with the buttons, Ctrl+arrows or a drag.
public sealed partial class ConsistEditorScreen : Screen, IWorldScreen
{
    private const int MaxUndo = 200;

    private readonly WorldDisplay _world = new();
    private WorldRenderer _renderer => _world.Renderer;
    private LightingPanel _lighting => _world.Lighting;
    private readonly Camera _camera = new();
    private readonly List<ModelDraw> _draws = [];
    private FrameScene _scene => _world.Scene;
    private readonly Dictionary<string, VehicleModel?> _models = [];
    private DisplayTrack? _track;

    private Consist _consist;
    private bool _dirty;
    private readonly List<List<ConsistCar>> _undo = [];
    private readonly List<List<ConsistCar>> _redo = [];

    /// The row picked in the consist, or -1.
    private int _selected = -1;

    /// The kind shown in the catalog, and the vehicle picked in it with the livery chosen for it.
    private VehicleKind _kind = VehicleKind.Locomotive;
    private VehicleDef? _vehicle;
    private readonly Dictionary<string, string> _liveryOf = [];

    /// Whether the preview shows the consist's row (true) or the catalog's vehicle.
    private bool _previewRow;

    private bool _editingName;
    private string _toast = "";
    private double _toastAt = -10.0;
    private Consist? _opened;

    // The camera, round the vehicle shown.
    private float _yaw = 215f * MathF.PI / 180f;
    private float _pitch = -10f * MathF.PI / 180f;
    private float _distance = 28f;
    private bool _orbiting;
    private double _touchedAt = -10.0;
    private string? _framedFor;

    /// What the camera orbits: moved with WASD (across, relative to the view) and Q/E (down, up), and glided to the car
    /// picked in the list.
    private float3 _target = new(0f, 2f, 0f);
    private float? _glideZ;

    /// Where each car of the consist stands on the display track (its middle between the trucks, along z), the front at +z.
    private readonly List<float> _carZ = [];

    public override WorldView View => WorldView.World;

    public ConsistEditorScreen(Consist? consist = null)
    {
        _consist = consist ?? new Consist { Name = UnusedName() };
        _vehicle = VehicleCatalog.OfKind(_kind).FirstOrDefault();
    }

    public override void Enter()
    {
        AudioEngine.PlayMusic(MusicTrack.Menu);
        _track = new DisplayTrack(80f);
        if (DevTools.Skybox is { } skybox)
        {
            DevTools.Skybox = null;
            _renderer.Sky.Use(skybox);
        }

        if (_consist.Cars.Count > 0)
            Select(0);
    }

    public override void Exit()
    {
        foreach (var model in _models.Values)
            model?.Dispose();
        _models.Clear();
        _track?.Dispose();
        _world.Dispose();
    }

    /// Back from the list of saved consists, with one picked or not.
    public override void Resume()
    {
        if (_opened is not { } opened)
            return;

        _opened = null;
        SaveIfChanged();
        _consist = opened;
        _dirty = false;
        _undo.Clear();
        _redo.Clear();
        _selected = -1;
        if (_consist.Cars.Count > 0)
            Select(0);
        Notify($"Opened {_consist.Name}");
    }

    /// Called by the Open list: the consist to show once it closes.
    internal void Open(Consist consist) => _opened = consist;

    /// Called once a saved consist's file is gone. If it's the one being edited, start afresh rather than save it back.
    internal void Deleted(string path, string name)
    {
        if (_opened?.SavedAt is { } openedAt && SamePath(openedAt, path))
            _opened = null;
        if (_consist.SavedAt is not { } at || !SamePath(at, path))
            return;

        StartFresh();
        Notify($"Deleted {name}");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // --- Editing. Every change goes through Change, so it can be undone. ---

    private void Change(Action<List<ConsistCar>> edit)
    {
        _undo.Add([.. _consist.Cars]);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
        edit(_consist.Cars);
        _dirty = true;
        _selected = Math.Min(_selected, _consist.Cars.Count - 1);
    }

    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Add([.. _consist.Cars]);
        _consist.Cars = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _dirty = true;
        _selected = Math.Min(_selected, _consist.Cars.Count - 1);
        Sfx.Back();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Add([.. _consist.Cars]);
        _consist.Cars = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true;
        _selected = Math.Min(_selected, _consist.Cars.Count - 1);
        Sfx.Select();
    }

    private string LiveryFor(VehicleDef def) =>
        _liveryOf.TryGetValue(def.Id, out var id) ? id : def.Liveries.FirstOrDefault()?.Id ?? "";

    /// The catalog's vehicle at index (in the list, 0 the front), or after the selected row when index is null.
    private void Insert(VehicleDef def, int? index = null)
    {
        var at = Math.Clamp(index ?? (_selected >= 0 ? _selected + 1 : _consist.Cars.Count), 0, _consist.Cars.Count);
        Change(cars => cars.Insert(at, new ConsistCar(def.Id, LiveryFor(def))));
        Select(at);
        _reveal = true;
        Sfx.Select();
    }

    private void Move(int from, int to)
    {
        to = Math.Clamp(to, 0, _consist.Cars.Count - 1);
        if (from < 0 || from >= _consist.Cars.Count || from == to)
            return;
        Change(cars =>
        {
            var car = cars[from];
            cars.RemoveAt(from);
            cars.Insert(to, car);
        });
        Select(to);
        _reveal = true;
        Sfx.Tick();
    }

    private void Delete(int index)
    {
        if (index < 0 || index >= _consist.Cars.Count)
            return;
        Change(cars => cars.RemoveAt(index));
        if (_consist.Cars.Count == 0)
            _selected = -1;
        else
            Select(Math.Min(index, _consist.Cars.Count - 1));
        Sfx.Back();
    }

    private void Duplicate(int index)
    {
        if (index < 0 || index >= _consist.Cars.Count)
            return;
        Change(cars => cars.Insert(index + 1, cars[index]));
        Select(index + 1);
        _reveal = true;
        Sfx.Select();
    }

    private void Flip(int index)
    {
        if (index < 0 || index >= _consist.Cars.Count)
            return;
        Change(cars => cars[index] = cars[index] with { Reversed = !cars[index].Reversed });
        Sfx.Tick();
    }

    private void ToggleLoad(int index)
    {
        if (index < 0 || index >= _consist.Cars.Count || _consist.Cars[index].Def is not { CanLoad: true })
            return;
        Change(cars => cars[index] = cars[index].WithFill(cars[index].Fill > 0.0 ? 0.0 : 1.0));
        Sfx.Tick();
    }

    /// The next of the selected car's paint schemes.
    private void CycleLivery(int index)
    {
        if (index < 0 || index >= _consist.Cars.Count || _consist.Cars[index].Def is not { Liveries.Count: > 1 } def)
            return;
        var car = _consist.Cars[index];
        var at = Math.Max(0, def.Liveries.ToList().FindIndex(l => l.Id == def.FindLivery(car.Livery)?.Id));
        var next = def.Liveries[(at + 1) % def.Liveries.Count].Id;
        Change(cars => cars[index] = cars[index].Settled() with { Livery = next });
        Sfx.Tick();
    }

    private void Clear()
    {
        if (_consist.Cars.Count == 0)
            return;
        Change(cars => cars.Clear());
        _selected = -1;
        Sfx.Back();
    }

    private void Select(int index)
    {
        _selected = index;
        _previewRow = index >= 0;
        // The view glides along to the car picked.
        LayOut();
        if (index >= 0 && index < _carZ.Count)
            _glideZ = _carZ[index];
    }

    /// Each car's place along the track, coupler to coupler, the whole consist centred on the origin. Returns its length.
    private float LayOut()
    {
        _carZ.Clear();
        var total = (float)_consist.Cars.Sum(c => c.Def?.Length ?? 0.0);
        var front = total * 0.5f;
        foreach (var car in _consist.Cars)
        {
            if (car.Def is not { } def)
            {
                _carZ.Add(front);
                continue;
            }

            var leading = (float)(car.Reversed ? def.RearOverhang : def.FrontOverhang);
            _carZ.Add(front - leading - (float)def.Centres * 0.5f);
            front -= (float)def.Length;
        }

        return total;
    }

    /// Whether the whole consist is on show (it has anything in it), rather than the catalog's vehicle alone.
    private bool ShowingConsist => _consist.Cars.Count > 0;

    private void Pick(VehicleDef def)
    {
        _vehicle = def;
        _previewRow = false;
    }

    // --- Saving. ---

    private void Save()
    {
        _consist.Name = _consist.Name.Trim().Length > 0 ? _consist.Name.Trim() : UnusedName();
        _consist.Save();
        _dirty = false;
        Notify($"Saved {_consist.Name}");
    }

    /// Leaving, opening another or starting a new one keeps what was made.
    private void SaveIfChanged()
    {
        if (_dirty && (_consist.Cars.Count > 0 || _consist.SavedAt is not null))
            Save();
    }

    private void New()
    {
        SaveIfChanged();
        StartFresh();
        Notify("New consist");
    }

    private void StartFresh()
    {
        _consist = new Consist { Name = UnusedName() };
        _dirty = false;
        _editingName = false;
        _undo.Clear();
        _redo.Clear();
        _selected = -1;
        _previewRow = false;
    }

    private static string UnusedName()
    {
        var taken = Directory.EnumerateFiles(Consist.Folder, "*.json").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!taken.Contains($"Consist {n}"))
                return $"Consist {n}";
    }

    private void Leave()
    {
        SaveIfChanged();
        HighballApp.Instance.Pop();
    }

    private void Notify(string text)
    {
        _toast = text;
        _toastAt = Tween.Now;
    }

    // --- Each frame. ---

    public override void Update(float dt)
    {
        HandleKeys();
        UpdateCamera(dt);

        _draws.Clear();
        var length = LayOut();
        // The track's laid long enough for the whole consist, with room to walk round either end.
        var wanted = MathF.Max(80f, MathF.Ceiling((length * 0.5f + 60f) / 100f) * 100f);
        if (_track is null || _track.HalfLength < length * 0.5f + 30f || _track.HalfLength > wanted * 2f)
        {
            _track?.Dispose();
            _track = new DisplayTrack(wanted);
        }

        _track.DrawsNear(_draws, _target.Z, MathF.Min(120f + _distance * 0.5f, 400f));
        if (ShowingConsist)
        {
            for (var i = 0; i < _consist.Cars.Count; i++)
            {
                var car = _consist.Cars[i];
                if (car.Def is not { } def || ModelFor(car.ModelPath) is not { } carModel)
                    continue;
                var yaw = car.Reversed ? MathF.PI : 0f;
                var at = new float3(0f, 0f, _carZ[i]);
                if (carModel.Trucks is { } carTrucks)
                {
                    var sag = (float)Suspension.Sag(def, car.Fill);
                    _draws.Add(new ModelDraw(carTrucks, at, Yaw: yaw));
                    _draws.Add(new ModelDraw(carModel.Body, at + new float3(0f, -sag, 0f), Yaw: yaw));
                }
                else
                    _draws.Add(new ModelDraw(carModel.Body, at, Yaw: yaw));
            }
        }
        else if (Shown() is { } shown && ModelFor(ShownModel(shown)) is { } model)
        {
            // The body settles on its springs under its load; the trucks stay on the rail.
            var yaw = shown.Reversed ? MathF.PI : 0f;
            if (model.Trucks is { } trucks)
            {
                var sag = (float)Suspension.Sag(shown.Def, shown.Car?.Fill ?? 0.0);
                _draws.Add(new ModelDraw(trucks, default, Yaw: yaw));
                _draws.Add(new ModelDraw(model.Body, new float3(0f, -sag, 0f), Yaw: yaw));
            }
            else
            {
                _draws.Add(new ModelDraw(model.Body, default, Yaw: yaw));
            }
        }
    }

    private void HandleKeys()
    {
        if (_editingName)
        {
            var name = _consist.Name;
            var done = Keyboard.Edit(ref name, 40, c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '#' or '(' or ')');
            if (name != _consist.Name)
            {
                _consist.Name = name;
                _dirty = true;
            }

            if (done || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _editingName = false;
                Sfx.Select();
            }

            return;
        }

        var ctrl = ImGui.GetIO().KeyCtrl;
        var count = _consist.Cars.Count;

        if (Keyboard.Pressed(ImGuiKey.Escape))
        {
            if (_drag is not null)
                _drag = null;
            else
                Leave();
            return;
        }

        if (ctrl)
        {
            if (Keyboard.Pressed(ImGuiKey.Z, true))
                Undo();
            if (Keyboard.Pressed(ImGuiKey.Y, true))
                Redo();
            if (Keyboard.Pressed(ImGuiKey.S))
                Save();
            if (Keyboard.Pressed(ImGuiKey.D, true))
                Duplicate(_selected);
            if (Keyboard.Pressed(ImGuiKey.UpArrow, true))
                Move(_selected, _selected - 1);
            if (Keyboard.Pressed(ImGuiKey.DownArrow, true))
                Move(_selected, _selected + 1);
            return;
        }

        if (count > 0)
        {
            if (Keyboard.Pressed(ImGuiKey.UpArrow, true))
            {
                Select(Math.Max(0, _selected < 0 ? count - 1 : _selected - 1));
                _reveal = true;
                Sfx.Hover();
            }

            if (Keyboard.Pressed(ImGuiKey.DownArrow, true))
            {
                Select(Math.Min(count - 1, _selected + 1));
                _reveal = true;
                Sfx.Hover();
            }

            if (Keyboard.Pressed(ImGuiKey.Home))
            {
                Select(0);
                _reveal = true;
            }

            if (Keyboard.Pressed(ImGuiKey.End))
            {
                Select(count - 1);
                _reveal = true;
            }
        }

        if (Keyboard.Pressed(ImGuiKey.Delete, true))
            Delete(_selected);
        if (Keyboard.Pressed(ImGuiKey.F))
            Flip(_selected);
        if (Keyboard.Pressed(ImGuiKey.L))
            ToggleLoad(_selected);
        if (Keyboard.Pressed(ImGuiKey.P))
            CycleLivery(_selected);
        if ((Keyboard.Pressed(ImGuiKey.Enter) || Keyboard.Pressed(ImGuiKey.KeypadEnter) || Keyboard.Pressed(ImGuiKey.Insert)) && _vehicle is { } def)
            Insert(def);
    }

    /// What the middle of the screen shows: the picked row, else the catalog's vehicle.
    private (VehicleDef Def, Livery? Livery, bool Reversed, ConsistCar? Car)? Shown()
    {
        if (_previewRow && _selected >= 0 && _selected < _consist.Cars.Count && _consist.Cars[_selected] is var car && car.Def is { } def)
            return (def, def.FindLivery(car.Livery), car.Reversed, car);
        if (_vehicle is { } vehicle)
            return (vehicle, vehicle.FindLivery(LiveryFor(vehicle)), false, null);
        return null;
    }

    /// The model of what's shown: a row's carrying its cargo once loaded, the catalog's empty.
    private static string? ShownModel((VehicleDef Def, Livery? Livery, bool Reversed, ConsistCar? Car) shown) =>
        shown.Car?.ModelPath ?? shown.Livery?.Model;

    /// Loaded the first time it's shown; null if it can't be.
    private VehicleModel? ModelFor(string? path)
    {
        if (path is null)
            return null;
        if (_models.TryGetValue(path, out var model))
            return model;

        try
        {
            model = VehicleModel.Load(path);
        }
        catch (Exception e)
        {
            Log.Warn($"Consist editor: model {path} unreadable ({e.Message})");
            model = null;
        }

        return _models[path] = model;
    }

    /// What the selected car is seen carrying once it's loaded.
    private void SetCargo(int index, string cargo)
    {
        if (index < 0 || index >= _consist.Cars.Count || _consist.Cars[index].CargoId == cargo)
            return;
        Change(cars => cars[index] = cars[index].Settled() with { Cargo = cargo });
        _previewRow = true;
        Sfx.Tick();
    }

    private void UpdateCamera(float dt)
    {
        var shown = Shown();
        var length = ShowingConsist ? (float)_consist.Cars.Sum(c => c.Def?.Length ?? 0.0) : (float)(shown?.Def.Length ?? 20.0);

        // The consist framed when it first comes on show (or a vehicle alone, as it's picked); after that the view stays
        // where it's put.
        var framing = ShowingConsist ? "consist:" + _consist.Name : shown?.Def.Id;
        if (framing != _framedFor)
        {
            _framedFor = framing;
            _distance = Math.Clamp(length * (ShowingConsist ? 0.7f : 1.35f), 8f, 900f);
            _target = new float3(0f, 2f, 0f);
            _glideZ = null;
        }

        // WASD moves what's orbited across the ground, as the view faces; Q and E lower and raise it. Faster the further
        // out the view is, and with Shift.
        var keys = !Keyboard.KeysBlocked && !ImGui.GetIO().KeyCtrl && !_editingName;
        if (keys)
        {
            var (sy, cy) = MathF.SinCos(_yaw);
            var (forward, right) = (new float3(sy, 0f, -cy), new float3(cy, 0f, sy));
            var move = default(float3);
            if (Input.Key(GlfwKey.W)) move += forward;
            if (Input.Key(GlfwKey.S)) move -= forward;
            if (Input.Key(GlfwKey.D)) move += right;
            if (Input.Key(GlfwKey.A)) move -= right;
            if (Input.Key(GlfwKey.E)) move += new float3(0f, 1f, 0f);
            if (Input.Key(GlfwKey.Q)) move -= new float3(0f, 1f, 0f);
            if (move.X != 0f || move.Y != 0f || move.Z != 0f)
            {
                var speed = MathF.Max(6f, _distance * 0.9f) * (ImGui.GetIO().KeyShift ? 3f : 1f);
                _target += move * (speed * dt);
                _target = new float3(_target.X, Math.Clamp(_target.Y, 0.3f, 60f), Math.Clamp(_target.Z, -(_track?.HalfLength ?? 80f), _track?.HalfLength ?? 80f));
                _touchedAt = Tween.Now;
                _glideZ = null;
            }
        }

        if (_glideZ is { } glide)
        {
            var z = _target.Z + (glide - _target.Z) * MathF.Min(1f, dt * 6f);
            _target = new float3(_target.X + (0f - _target.X) * MathF.Min(1f, dt * 6f), _target.Y, z);
            if (MathF.Abs(glide - z) < 0.05f)
                _glideZ = null;
        }

        var mouse = ImGui.GetIO().MousePos;
        var overStage = mouse.X > _stageMin.X && mouse.X < _stageMax.X && mouse.Y > _stageMin.Y && mouse.Y < _stageMax.Y && !Keyboard.PointerBlocked;
        if (overStage && _drag is null && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            _orbiting = true;
        if (!Input.Mouse(GlfwMouseButton.Left) && !Input.Mouse(GlfwMouseButton.Right))
            _orbiting = false;

        if (_orbiting)
        {
            _yaw += Input.MouseDelta.X * 0.005f;
            _pitch -= Input.MouseDelta.Y * 0.005f;
            _touchedAt = Tween.Now;
        }

        if (overStage && Input.Scroll != 0f)
        {
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, Input.Scroll), 4f, 1200f);
            _touchedAt = Tween.Now;
        }

        // Left alone, it turns slowly, like a model on a stand.
        if (Tween.Now - _touchedAt > 4.0 && !Tween.ReduceMotion)
            _yaw += dt * 0.12f;

        var target = _target;
        var highest = MathF.Asin(Math.Clamp((target.Y - DisplayTrack.GroundY - 0.3f) / _distance, 0f, 1f));
        _pitch = Math.Clamp(_pitch, -1.45f, highest);
        _camera.Yaw = _yaw;
        _camera.Pitch = _pitch;

        // Set over so the vehicle stands in the middle of the space between the panels and above the caption, not of
        // the window: the camera slides sideways and up rather than turning, so it's still seen square on.
        var aspect = _screen.X > 0f && _screen.Y > 0f ? _screen.X / _screen.Y : 16f / 9f;
        var tan = MathF.Tan(_camera.FieldOfView * 0.5f);
        var across = _screen.X > 0f ? ((_stageMin.X + _stageMax.X) / _screen.X - 1f) * _distance * tan * aspect : 0f;
        var up = _screen.Y > 0f ? (1f - (_stageMin.Y + _stageMax.Y) / _screen.Y) * _distance * tan : 0f;
        var at = target - _camera.Forward * _distance - _camera.Right * across - _camera.Up * up;
        (_camera.X, _camera.Y, _camera.Z) = (at.X, at.Y, at.Z);
    }

    public void RenderWorld(CommandBuffer cmd, int width, int height)
    {
        _scene.Camera = _camera;
        _scene.Models = _draws;
        _world.Render(cmd, width, height);
    }

    public void CompositeWorld(CommandBuffer cmd) => _world.Composite(cmd);
}
