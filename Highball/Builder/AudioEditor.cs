using System.Globalization;
using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.Core.Audio;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Audio Editor (View, Audio Editor): tuning how the trains sound, heard as it's changed, and kept in AudioTuning.json.
///
///   Mix           each group's level, and the bus compressor that holds them all down when they pile up, with meters
///   Locomotive    the picked locomotive's sounds: each one's level, the engine's pitch, and where each comes from on it,
///                 moved with handles drawn on the engine in the world
///   Running gear  the wheels, trucks and motors: each sound's level, and what sets off the knocks, and how often
///
/// Handles: drag one to move it across, hold Shift to move it up and down, right-click it to put it back where the game had it.
public sealed class AudioEditor
{
    public bool Open = App.DevTools.AudioEditor is not null;

    /// The handles over the picked locomotive are showing.
    public bool ShowPositions = true;

    private int _tab = App.DevTools.AudioEditor switch { "locomotive" => 1, "gear" => 2, _ => 0 };
    private SoundSpot? _hot;
    private SoundSpot? _dragging;
    private float2 _dragFrom;
    private double3 _dragStart;
    private float _metresPerPixel;

    private static readonly (SoundSpot Spot, string Name, Rgba Color)[] Spots =
    [
        (SoundSpot.Engine, "Engine", Rgba.Hex(0xFF9F1C)),
        (SoundSpot.AirCompressor, "Air compressor", Rgba.Hex(0x4D8DFF)),
        (SoundSpot.BrakeFan, "Brake fan", Rgba.Hex(0xC07BFF)),
        (SoundSpot.Horn, "Horn", Rgba.Hex(0x3DDC97)),
        (SoundSpot.Bell, "Bell", Rgba.Hex(0xD9B26A)),
    ];

    private static readonly string[] TabNames = ["Mix", "Locomotive", "Running gear"];

    /// A handle's under the pointer or being dragged: the editor's clicks shouldn't go to picking vehicles.
    public bool WantsPointer => _hot is not null || _dragging is not null;

    private static AudioTuning Tuning => AudioTuning.Current;

    public void Window(TrainPanel trains)
    {
        if (!Open)
            return;

        // Clear of the tool bar down the left, the first time.
        ImGui.SetNextWindowPos(new float2(S(64f), S(56f)), ImGuiCond.FirstUseEver);
        if (Kit.Begin("Audio Editor##panel", ref Open, S(400f)))
        {
            var picked = trains.SoundAnchor(out var car, out _, out _) ? car : null;
            var subtitle = picked is null ? "Pick a locomotive to tune it" : picked.Def.Name;
            var (status, color) = AudioTuning.Unsaved ? ("UNSAVED", Caution) : ("SAVED", Clear);
            if (Kit.Header("AUDIO EDITOR", subtitle, status, color))
                Open = false;

            _tab = Kit.Tabs("audio.tabs", TabNames, _tab);
            switch (_tab)
            {
                case 0:
                    MixTab();
                    break;
                case 1:
                    LocomotiveTab(picked);
                    break;
                default:
                    GearTab();
                    break;
            }

            ImGui.Dummy(new float2(1f, S(6f)));
            switch (Kit.Buttons("audio.file", ["Save", "Revert", "Game's own"], [AudioTuning.Unsaved, AudioTuning.Unsaved, true]))
            {
                case 0:
                    AudioTuning.Save();
                    break;
                case 1:
                    AudioTuning.Revert();
                    break;
                case 2:
                    AudioTuning.Reset();
                    break;
            }
        }

        Kit.End();
    }

    // --- The tabs. ---

    private static void MixTab()
    {
        var mix = Tuning.Mix;
        Kit.BeginCard("LEVELS");
        Level("mix.master", "Master", () => mix.Master, v => mix.Master = v, 0f, 2f);
        Level("mix.engines", "Engines", () => mix.Engines, v => mix.Engines = v, 0f, 2f);
        Level("mix.horn", "Horn", () => mix.Horn, v => mix.Horn = v, 0f, 2f);
        Level("mix.bell", "Bell", () => mix.Bell, v => mix.Bell = v, 0f, 2f);
        Level("mix.air", "Air compressor", () => mix.AirCompressor, v => mix.AirCompressor = v, 0f, 2f);
        Level("mix.gear", "Running gear", () => mix.RunningGear, v => mix.RunningGear = v, 0f, 2f);
        Kit.EndCard();

        Kit.BeginCard("BUS COMPRESSOR");
        Kit.Note("Train sounds ducked together when loud.");
        var on = mix.Compressor;
        if (Kit.Toggle("mix.comp.on", "On", ref on))
        {
            mix.Compressor = on;
            AudioTuning.Unsaved = true;
        }

        Number("mix.comp.threshold", "Threshold", () => mix.ThresholdDb, v => mix.ThresholdDb = v, -40f, 0f, "{0:0.0} dB");
        Number("mix.comp.ratio", "Ratio", () => mix.Ratio, v => mix.Ratio = v, 1f, 20f, "{0:0.0} : 1");
        Number("mix.comp.attack", "Attack", () => mix.AttackMs, v => mix.AttackMs = v, 1f, 300f, "{0:0} ms");
        Number("mix.comp.release", "Release", () => mix.ReleaseMs, v => mix.ReleaseMs = v, 20f, 3000f, "{0:0} ms");
        Number("mix.comp.makeup", "Makeup", () => mix.MakeupDb, v => mix.MakeupDb = v, 0f, 12f, "{0:0.0} dB");

        Reading("Loudness", $"{AudioEngine.BusLevelDb:0.0} dB");
        Kit.Gauge(Math.Clamp((AudioEngine.BusLevelDb + 60f) / 60f, 0f, 1f), AudioEngine.BusLevelDb > mix.ThresholdDb ? Caution : Clear);
        Reading("Turned down by", $"{AudioEngine.BusReductionDb:0.0} dB");
        Kit.Gauge(Math.Clamp(AudioEngine.BusReductionDb / 20f, 0f, 1f), Stop);
        Kit.EndCard();
    }

    private void LocomotiveTab(TrainCar? car)
    {
        if (car is null)
        {
            Kit.Note("No locomotive picked.");
            return;
        }

        var tuned = Tuning.Edit(car.Def.Id);
        Kit.BeginCard("LEVELS", "This locomotive");
        Level("loco.engine", "Engine", () => tuned.Engine, v => tuned.Engine = v, 0f, 3f);
        Number("loco.pitch", "Engine pitch", () => tuned.EnginePitch, v => tuned.EnginePitch = v, 0.8f, 1.25f, "x{0:0.000}");
        Level("loco.chug", "Exhaust chug", () => tuned.Chug, v => tuned.Chug = v, 0f, 3f);
        Level("loco.turbo", "Turbo", () => tuned.Turbo, v => tuned.Turbo = v, 0f, 3f);
        Level("loco.startup", "Startup", () => tuned.Startup, v => tuned.Startup = v, 0f, 3f);
        Level("loco.air", "Air compressor", () => tuned.AirCompressor, v => tuned.AirCompressor = v, 0f, 3f);
        Level("loco.fan", "Brake fan", () => tuned.BrakeFan, v => tuned.BrakeFan = v, 0f, 3f);
        Level("loco.horn", "Horn", () => tuned.Horn, v => tuned.Horn = v, 0f, 3f);
        Level("loco.bell", "Bell", () => tuned.Bell, v => tuned.Bell = v, 0f, 3f);
        Kit.EndCard();

        Kit.BeginCard("POSITIONS");
        Kit.Toggle("loco.show", "Show handles in the world", ref ShowPositions);
        Kit.Note("Metres: across, up, forward. Shift+drag: height. Right-click: reset.");
        foreach (var (spot, name, color) in Spots)
        {
            var p = TrainAudio.SoundPosition(car, spot);
            var tunedHere = Get(tuned, spot) is not null;
            var at = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            Canvas.Lamp(draw, at + new float2(S(5f), S(9f)), S(4f), color);
            Canvas.Text(draw, Medium, S(14f), at + new float2(S(16f), S(1f)), tunedHere ? Text : TextDim, name);
            Canvas.Text(draw, Mono, S(13f), at + new float2(S(150f), S(2f)), TextDim,
                string.Format(CultureInfo.InvariantCulture, "{0,6:0.00} {1,6:0.00} {2,6:0.00}", p.X, p.Y, p.Z));
            ImGui.Dummy(new float2(Kit.Width - S(64f), S(20f)));
            ImGui.SameLine();
            if (Kit.Button($"loco.reset.{spot}", "Reset", S(56f), enabled: tunedHere))
            {
                Set(tuned, spot, null);
                AudioTuning.Unsaved = true;
            }
        }

        Kit.EndCard();
    }

    private static void GearTab()
    {
        var g = Tuning.Gear;
        Kit.BeginCard("LEVELS", "Wheels, trucks, motors");
        Level("gear.roll", "Rolling", () => g.Rolling, v => g.Rolling = v, 0f, 2f);
        Level("gear.slow", "Slow rolling", () => g.SlowRolling, v => g.SlowRolling = v, 0f, 2f);
        Level("gear.clank", "Axle clank", () => g.Clank, v => g.Clank = v, 0f, 2f);
        Level("gear.flange", "Flange screech", () => g.Flange, v => g.Flange = v, 0f, 2f);
        Level("gear.squeal", "Brake squeal", () => g.Squeal, v => g.Squeal = v, 0f, 2f);
        Level("gear.motors", "Traction motors", () => g.Motors, v => g.Motors = v, 0f, 2f);
        Level("gear.slip", "Wheel slip", () => g.Slip, v => g.Slip = v, 0f, 2f);
        Kit.EndCard();

        Kit.BeginCard("AXLE CLANKS", "Over rail joints");
        Number("gear.clank.min", "Every, at least", () => g.ClankEveryMin, v => g.ClankEveryMin = v, 2f, 200f, "{0:0} m");
        Number("gear.clank.max", "Every, at most", () => g.ClankEveryMax, v => g.ClankEveryMax = v, 2f, 400f, "{0:0} m");
        Number("gear.clank.speed", "Faster than", () => g.ClankMinSpeed, v => g.ClankMinSpeed = v, 0f, 10f, Mph);
        Kit.EndCard();

        Kit.BeginCard("FLANGE SCREECH", "On curves");
        Number("gear.flange.radius", "Full at a radius of", () => g.FlangeRadius, v => g.FlangeRadius = v, 100f, 2000f, "{0:0} m");
        Number("gear.flange.onset", "From (share of full)", () => g.FlangeOnset, v => g.FlangeOnset = v, 0f, 1f, "{0:0.00}");
        Number("gear.flange.chance", "Chance a second, at full", () => g.FlangeChance, v => g.FlangeChance = v, 0f, 3f, "{0:0.00}");
        Number("gear.flange.speed", "Faster than", () => g.FlangeMinSpeed, v => g.FlangeMinSpeed = v, 0f, 20f, Mph);
        Kit.EndCard();

        Kit.BeginCard("BRAKE SQUEAL", "Slowing down");
        Number("gear.squeal.force", "Braking harder than", () => g.SquealForce / 1000f, v => g.SquealForce = v * 1000f, 0f, 50f, "{0:0.0} kN a car");
        Number("gear.squeal.min", "Faster than", () => g.SquealMinSpeed, v => g.SquealMinSpeed = v, 0f, 10f, Mph);
        Number("gear.squeal.max", "Slower than", () => g.SquealMaxSpeed, v => g.SquealMaxSpeed = v, 0f, 30f, Mph);
        Number("gear.squeal.chance", "Chance a second", () => g.SquealChance, v => g.SquealChance = v, 0f, 3f, "{0:0.00}");
        Kit.EndCard();
    }

    /// A speed in metres a second shown in mph.
    private const string Mph = "mph";

    // --- Controls. ---

    /// A level: shown as a share (1 as it was) and in dB.
    private static void Level(string id, string label, Func<float> get, Action<float> set, float min, float max)
    {
        var v = get();
        var db = v > 1e-4f ? 20f * MathF.Log10(v) : float.NegativeInfinity;
        Slider(id, label, v, min, max, float.IsNegativeInfinity(db) ? "off" : string.Format(CultureInfo.InvariantCulture, "{0:0.00}  ({1:+0.0;-0.0;0.0} dB)", v, db), set);
    }

    private static void Number(string id, string label, Func<float> get, Action<float> set, float min, float max, string format)
    {
        var v = get();
        var text = format == Mph ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} mph", v * 2.23694f) : string.Format(CultureInfo.InvariantCulture, format, v);
        Slider(id, label, v, min, max, text, set);
    }

    private static void Slider(string id, string label, float value, float min, float max, string shown, Action<float> set)
    {
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        Canvas.Text(draw, Medium, S(13.5f), at, TextDim, label);
        Canvas.Text(draw, Mono, S(13f), at + new float2(Kit.Width, S(1f)), Text, shown, Align.Right);
        ImGui.Dummy(new float2(Kit.Width, S(16f)));
        var t = (value - min) / (max - min);
        if (Kit.Slider(id, ref t, out _))
        {
            set(min + t * (max - min));
            AudioTuning.Unsaved = true;
        }
    }

    private static void Reading(string label, string value)
    {
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        Canvas.Text(draw, Medium, S(13.5f), at, TextDim, label);
        Canvas.Text(draw, Mono, S(13f), at + new float2(Kit.Width, S(1f)), Text, value, Align.Right);
        ImGui.Dummy(new float2(Kit.Width, S(17f)));
    }

    private static float[]? Get(AudioTuning.VehicleTuning t, SoundSpot spot) => spot switch
    {
        SoundSpot.Engine => t.EngineAt,
        SoundSpot.AirCompressor => t.AirCompressorAt,
        SoundSpot.BrakeFan => t.BrakeFanAt,
        SoundSpot.Horn => t.HornAt,
        _ => t.BellAt,
    };

    private static void Set(AudioTuning.VehicleTuning t, SoundSpot spot, float[]? p)
    {
        switch (spot)
        {
            case SoundSpot.Engine: t.EngineAt = p; break;
            case SoundSpot.AirCompressor: t.AirCompressorAt = p; break;
            case SoundSpot.BrakeFan: t.BrakeFanAt = p; break;
            case SoundSpot.Horn: t.HornAt = p; break;
            default: t.BellAt = p; break;
        }
    }

    // --- The handles in the world. ---

    /// Draws the picked locomotive's sound handles over the world and moves the one dragged. blocked: the pointer's over a
    /// window or off the view, where it mustn't start a drag.
    public void Gizmos(TrainPanel trains, VehicleOverlay.Projector project, World.Camera camera, float screenHeight, bool blocked)
    {
        _hot = null;
        if (!Open || !ShowPositions || !trains.SoundAnchor(out var car, out var rotation, out var middle))
        {
            _dragging = null;
            return;
        }

        var draw = ImGui.GetForegroundDrawList();
        var mouse = ImGui.GetIO().MousePos;
        var tuned = Tuning.Edit(car.Def.Id);
        var radius = S(8f);
        var best = float.MaxValue;
        var drawn = new List<float2>();
        foreach (var (spot, name, color) in Spots)
        {
            var world = World(rotation, middle, TrainAudio.SoundPosition(car, spot));
            if (project(world.X, world.Y, world.Z) is not { } at)
                continue;
            // A thin line down to the rail, so where it is along and across reads at a glance.
            if (project(world.X, middle.Y, world.Z) is { } foot)
                draw.AddLine(at, foot, color.WithAlpha(0.35f).U32, S(1f));
            var dragged = _dragging == spot;
            var d = Length(mouse - at);
            if (!blocked && _dragging is null && d < radius * 1.6f && d < best)
            {
                best = d;
                _hot = spot;
            }

            var lit = dragged || _hot == spot;
            draw.AddCircleFilled(at, radius * (lit ? 1.25f : 1f), color.WithAlpha(lit ? 1f : 0.85f).U32);
            draw.AddCircle(at, radius * (lit ? 1.25f : 1f) + S(2f), Rgba.Hex(0x0B0D10).WithAlpha(0.9f).U32, 0, S(2f));
            // Handles at the same place (the engine, compressor and fan start together): their names one under another.
            var stacked = drawn.Count(p => Length(p - at) < radius * 2f);
            drawn.Add(at);
            Canvas.Text(draw, Bold, S(13f), at + new float2(radius + S(6f), -S(8f) + stacked * S(16f)), color, name);
        }

        if (_hot is { } hot)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _dragging = hot;
                _dragFrom = mouse;
                _dragStart = World(rotation, middle, TrainAudio.SoundPosition(car, hot));
                // How far a pixel moves it, where it is: its distance over the view's height at that distance.
                var (dx, dy, dz) = (_dragStart.X - camera.X, _dragStart.Y - camera.Y, _dragStart.Z - camera.Z);
                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                _metresPerPixel = 2f * distance * MathF.Tan(camera.FieldOfView * 0.5f) / MathF.Max(screenHeight, 1f);
            }
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                Set(tuned, hot, null);
                AudioTuning.Unsaved = true;
            }
        }

        if (_dragging is { } moving)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _dragging = null;
                return;
            }

            var delta = (mouse - _dragFrom) * _metresPerPixel;
            double3 world;
            if (ImGui.GetIO().KeyShift)
                world = new double3(_dragStart.X, _dragStart.Y - delta.Y, _dragStart.Z);
            else
            {
                // Across the ground: right with the view's right, up the screen away from the camera.
                var right = Flat(camera.Right);
                var away = Flat(camera.Forward);
                world = new double3(_dragStart.X + right.X * delta.X - away.X * delta.Y, _dragStart.Y, _dragStart.Z + right.Z * delta.X - away.Z * delta.Y);
            }

            var local = VehicleOverlay.Rotate(new float4(-rotation.X, -rotation.Y, -rotation.Z, rotation.W),
                new float3((float)(world.X - middle.X), (float)(world.Y - middle.Y), (float)(world.Z - middle.Z)));
            Set(tuned, moving, AudioTuning.Keep(local));
            AudioTuning.Unsaved = true;
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
    }

    private static double3 World(float4 rotation, double3 middle, float3 local)
    {
        var p = VehicleOverlay.Rotate(rotation, local);
        return new double3(middle.X + p.X, middle.Y + p.Y, middle.Z + p.Z);
    }

    private static float3 Flat(float3 v)
    {
        var length = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        return length > 1e-4f ? new float3(v.X / length, 0f, v.Z / length) : new float3(1f, 0f, 0f);
    }

    private static float Length(float2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
}
