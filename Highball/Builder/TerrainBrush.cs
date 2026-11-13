using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// What the terrain brush does to the ground.
public enum BrushMode { Raise, Lower, Smooth, Flatten, Paint }

/// The terrain brushes: raising, lowering, smoothing and flattening the ground, and painting its land cover, in strokes
/// that are each one undo. The brush works on another thread, one dab at a time: a big one takes longer than a frame.
public sealed class TerrainBrush(ToolContext context) : IEditorTool
{
    public static readonly (byte Class, string Name)[] PaintCovers =
    [
        (30, "GRASS"), (10, "FOREST"), (40, "CROPS"), (20, "SHRUB"), (50, "BUILT-UP"), (60, "BARE"), (80, "WATER"),
    ];

    private const float RaiseRate = 3f;   // metres a second at the brush's centre
    private const float BlendRate = 4f;   // smoothing and flattening: how much of the way a second
    public const float MinRadius = 2f;
    public const float MaxRadius = 400f;
    private const float FeetPerMeter = 3.28084f;

    private TerrainStroke? _stroke;
    private Task<(int X0, int Z0, int X1, int Z1)?>? _dab;
    private float _dabTime;
    private float _flattenTo;

    public BrushMode Mode { get; set; }
    public float Radius { get; set; } = 20f;

    /// Which of PaintCovers is painted.
    public int Paint { get; set; }

    /// The height F sampled, which Flatten then works to until Esc; without one, a stroke goes by where it starts.
    public float? FlattenSample { get; private set; }

    /// A stroke's under way.
    public bool Stroking => _stroke is not null;

    public void Update(float dt)
    {
        var source = context.Source;
        if (Mode == BrushMode.Flatten && !Keyboard.KeysBlocked && Input.KeyDown(GlfwKey.F) && !ImGui.IsKeyDown(ImGuiKey.LeftShift) && context.Hit is { } sampled)
            SampleFlatten(sampled.X, sampled.Z);

        var down = Input.Mouse(GlfwMouseButton.Left) && !context.Blocked;
        if (down && _stroke is null && context.Hit is not null)
            Begin();
        if (down && _stroke is not null && context.Hit is not null)
            Apply(dt);
        if (!down && _stroke is not null)
            End();
    }

    /// Put down with a stroke under way (Free Look picked up): the stroke's finished.
    public void LetGo()
    {
        if (_stroke is not null)
            End();
    }

    /// Takes in the last dab if it's done (every frame, whatever's in hand).
    public void Collect() => CollectDab(false);

    public bool Escape()
    {
        if (Mode != BrushMode.Flatten || FlattenSample is null)
            return false;
        FlattenSample = null;
        return true;
    }

    public void Begin()
    {
        _stroke = new TerrainStroke();
        _flattenTo = FlattenSample ?? context.Source.HeightAt(context.Hit!.Value.X, context.Hit.Value.Z);
    }

    public void SampleFlatten(double x, double z)
    {
        FlattenSample = context.Source.HeightAt(x, z);
        _flattenTo = FlattenSample.Value; // A stroke under way goes to it too.
    }

    public string FlattenSampleText
    {
        get
        {
            if (FlattenSample is not { } height)
                return "Sample height";
            return Settings.Current.Units == UnitSystem.Imperial
                ? string.Create(CultureInfo.InvariantCulture, $"Sampled {height * FeetPerMeter:#,0} ft")
                : string.Create(CultureInfo.InvariantCulture, $"Sampled {height:#,0} m");
        }
    }

    /// A dab at a time; the time since the last dab goes into the next, so the ground comes up just as fast.
    public void Apply(float dt)
    {
        _dabTime += dt;
        CollectDab(false);
        if (_dab is not null)
            return;

        var dab = NextDab();
        _dab = Task.Run(dab);
    }

    private Func<(int X0, int Z0, int X1, int Z1)?> NextDab()
    {
        var source = context.Source;
        var hit = context.Hit!.Value;
        var fast = Input.Key(GlfwKey.LeftShift) ? 4f : 1f;
        var (mode, radius, paint, flatten, stroke, time) = (Mode, Radius, PaintCovers[Paint].Class, _flattenTo, _stroke!, _dabTime);
        _dabTime = 0f;
        return () => mode switch
        {
            BrushMode.Raise => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Raise, RaiseRate * fast * time, 0f, stroke),
            BrushMode.Lower => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Lower, RaiseRate * fast * time, 0f, stroke),
            BrushMode.Smooth => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Smooth, MathF.Min(1f, BlendRate * fast * time), 0f, stroke),
            BrushMode.Flatten => source.Sculpt(hit.X, hit.Z, radius, SculptMode.Flatten, MathF.Min(1f, BlendRate * fast * time), flatten, stroke),
            BrushMode.Paint => source.Paint(hit.X, hit.Z, radius, paint, stroke),
            _ => null,
        };
    }

    /// Takes in the last dab once it's done, or waits for it.
    private void CollectDab(bool wait)
    {
        if (_dab is not { } dab || (!wait && !dab.IsCompleted))
            return;

        _dab = null;
        try
        {
            context.Invalidate(dab.GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            Log.Error($"Brush: {e.Message}");
        }
    }

    /// The stroke finished and kept for undoing. Returns it (null if it changed nothing).
    public TerrainStroke? End()
    {
        // The stroke isn't whole until the last dab is in, and what's left of its time has gone down.
        CollectDab(true);
        if (_dabTime > 0f && _stroke is not null && context.Hit is not null)
            context.Invalidate(NextDab()());
        _dabTime = 0f;

        var stroke = _stroke;
        _stroke = null;
        if (stroke is not { IsEmpty: false })
            return null;
        context.Record(stroke);
        return stroke;
    }

    /// The brush on the ground: a ring of points dropped onto the terrain, projected to the screen.
    public void Draw(ImDrawListPtr draw)
    {
        if (context.Hit is not { } hit)
            return;

        const int segments = 64;
        var source = context.Source;
        var points = new float2?[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = MathF.Tau * i / segments;
            var x = hit.X + MathF.Cos(a) * Radius;
            var z = hit.Z + MathF.Sin(a) * Radius;
            points[i] = context.View.Project(x, source.HeightAt(x, z) + 0.3, z);
        }

        var color = Mode == BrushMode.Paint ? Accent : Rgba.White;
        for (var i = 0; i < segments; i++)
            if (points[i] is { } a && points[(i + 1) % segments] is { } b)
                draw.AddLine(a, b, color, S(1.5f));
    }

    /// The brush's size, and what to paint.
    public void Panel(bool imperial)
    {
        Kit.BeginCard("Brush");
        var factor = imperial ? FeetPerMeter : 1f;
        var radius = Radius * factor;
        if (ToolPanel.Value("brushradius", imperial ? "Radius (ft)" : "Radius (m)", ref radius, MinRadius * factor, MaxRadius * factor, "0", log: true))
            Radius = Math.Clamp(radius / factor, MinRadius, MaxRadius);
        Kit.EndCard();

        if (Mode != BrushMode.Paint)
            return;
        Kit.BeginCard("Paint");
        var covers = PaintCovers.Select(c => c.Name[..1] + c.Name[1..].ToLowerInvariant()).ToArray();
        var chosen = ToolPanel.Mode("paintcover", covers, Paint, 3);
        if (chosen >= 0)
            Paint = chosen;
        Kit.EndCard();
    }

    public (string Key, string Action)[] Hints => Mode switch
    {
        BrushMode.Flatten when FlattenSample is not null => [("LMB", "Flatten"), ("F", FlattenSampleText), ("ESC", "Clear sample"), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
        BrushMode.Flatten => [("LMB", "Flatten"), ("F", FlattenSampleText), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
        BrushMode.Paint => [("LMB", "Paint"), ("P", "Cover"), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
        _ => [("LMB", Mode.ToString()), ("[ ]", "Brush size"), ("CTRL+Z", "Undo"), ("CTRL+S", "Save")],
    };
}
