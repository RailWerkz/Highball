using System.Globalization;
using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The train's air and slack, drawn: the brake pipe, reservoirs and cylinders car by car along the train; the air at
/// the head and the rear over the last couple of minutes; and every coupler's force (and how far into its slack it is),
/// draft up and buff down. Hover any of them for the numbers. A plate in the deck's look, like the Train window.
public sealed class TrainGraphs
{
    private const float HistorySeconds = 120f;
    private const float SampleEvery = 0.25f;
    private const int Samples = (int)(HistorySeconds / SampleEvery);

    private static readonly Rgba PipeColour = Rgba.Hex(0x4EA8FF);
    private static readonly Rgba AuxColour = Rgba.Hex(0x7E8AA0);
    private static readonly Rgba CylinderColour = Rgba.Hex(0xFF9F1C);
    private static readonly Rgba EqualizingColour = Rgba.Hex(0xEDF1F7);
    private static readonly Rgba MainColour = Rgba.Hex(0xB28DFF);
    private static readonly Rgba RearColour = Rgba.Hex(0x2CC5C5);
    private static readonly Rgba DraftColour = Rgba.Hex(0x52C97A);
    private static readonly Rgba BuffColour = Rgba.Hex(0xFF5A5F);
    private static readonly Rgba SlackColour = Rgba.Hex(0x3A414D);
    private static readonly Rgba GearColour = Rgba.Hex(0xE8B341);
    private static readonly Rgba Back = Rgba.Hex(0x0A0C0F, 0.85f);
    private static readonly Rgba Grid = new(1f, 1f, 1f, 0.07f);
    private static readonly Rgba Axis = new(1f, 1f, 1f, 0.22f);
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private readonly record struct Sample(float Equalizing, float Main, float HeadPipe, float RearPipe, float HeadCylinder, float RearCylinder);

    private readonly Sample[] _history = new Sample[Samples];
    private int _count, _cursor;
    private float _since;
    private Train? _for;
    private float _forceScale = 100f;

    public bool Open;

    /// Every frame: samples the air for the history.
    public void Record(Train? train, float dt)
    {
        if (!ReferenceEquals(train, _for))
        {
            _for = train;
            _count = _cursor = 0;
            _since = 0f;
        }

        if (train is null)
            return;
        _since += dt;
        if (_since < SampleEvery)
            return;
        _since -= SampleEvery;
        if (_since > SampleEvery)
            _since = 0f;

        var b = train.Body.Brakes;
        var head = Math.Max(b.Control, 0);
        _history[_cursor] = new Sample((float)b.Equalizing, (float)b.Main, (float)b.Pipe[head], (float)b.Pipe[^1], (float)b.CylinderOf(head), (float)b.CylinderOf(b.Count - 1));
        _cursor = (_cursor + 1) % Samples;
        _count = Math.Min(_count + 1, Samples);
    }

    private static float PanelWidth => S(720f);

    public void Window(Train? train)
    {
        if (!Open)
            return;

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new float2(S(24f) + EditorView.StripWidth, display.Y - S(700f)), ImGuiCond.FirstUseEver);
        if (Kit.Begin("Air graphs##panel", ref Open, PanelWidth))
            Contents(train);
        Kit.End();
    }

    private void Contents(Train? train)
    {
        if (train is null)
        {
            if (Kit.Header("AIR AND SLACK", "No train", "IDLE", TextFaint))
                Open = false;
            Kit.Note("Place a train to see its air and slack.");
            return;
        }

        var b = train.Body.Brakes;
        var head = Math.Max(b.Control, 0);
        var emergency = b.Valve[head] == ValveState.Emergency || b.Valve[^1] == ValveState.Emergency;
        var (status, colour) = emergency ? ("EMERGENCY", Stop) : b.Compressing ? ("PUMPING", Caution) : b.Flow > 5.0 ? ("CHARGING", Caution) : ("CHARGED", Style.Clear);
        if (Kit.Header("AIR AND SLACK", $"{train.Name}  ·  {train.Cars.Count} vehicles", status, colour))
            Open = false;
        Kit.Stats([
            new Stat("Main res", UnitText.Pressure(b.Main)),
            new Stat("Equalizing", UnitText.Pressure(b.Equalizing)),
            new Stat("Pipe head", UnitText.Pressure(b.Pipe[head])),
            new Stat("Pipe rear", UnitText.Pressure(b.Pipe[^1])),
            new Stat("Cylinder", UnitText.Pressure(b.CylinderOf(head))),
            new Stat("Flow", string.Create(C, $"{b.Flow:0} cfm")),
        ], 6, big: true);

        var tall = S(150f);
        Kit.BeginCard("Air along the train", "head to rear");
        AlongTheTrain(train, new float2(Kit.Width, tall));
        Kit.EndCard();
        Kit.BeginCard("Air over the last two minutes", "head and rear");
        OverTime(new float2(Kit.Width, tall));
        Kit.EndCard();
        Kit.BeginCard("Couplers", "draft up, buff down");
        Couplers(train, new float2(Kit.Width, tall));
        Kit.EndCard();
    }

    /// The numbers under the pointer, in a small card beside it (not ImGui's tooltip, which takes its text as a format).
    private static void Tip(params string[] lines)
    {
        var draw = ImGui.GetForegroundDrawList();
        var size = S(12f);
        var width = lines.Select((l, i) => Canvas.TextSize(i == 0 ? Bold : Mono, size, l).X).Max() + S(20f);
        var height = lines.Length * S(16f) + S(14f);
        var display = ImGui.GetIO().DisplaySize;
        var mouse = ImGui.GetMousePos();
        var at = new float2(Math.Clamp(mouse.X + S(16f), 0f, display.X - width), Math.Clamp(mouse.Y + S(16f), 0f, display.Y - height));
        draw.AddRectFilled(at, at + new float2(width, height), Surface);
        draw.AddRect(at, at + new float2(width, height), StrokeStrong, 0f, ImDrawFlags.None, 1f);
        draw.AddRectFilled(at, new float2(at.X + S(2f), at.Y + height), Accent);
        for (var i = 0; i < lines.Length; i++)
            Canvas.Text(draw, i == 0 ? Bold : Mono, size, at + new float2(S(10f), S(7f) + i * S(16f)), i == 0 ? Text : TextDim, lines[i]);
    }

    /// Room for an axis down the left, a row along the top for its unit and legend, and the ends' names under it; returns
    /// the plot's corners and whether the pointer's over it.
    private static (float2 Min, float2 Max, bool Hovered, ImDrawListPtr Draw) Plot(string id, float2 size, string unit)
    {
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, Back);
        draw.AddRect(min, min + size, Stroke, 0f, ImDrawFlags.None, 1f);
        Label(draw, min + new float2(S(6f), S(4f)), TextFaint, unit);
        return (min + new float2(S(44f), S(26f)), min + size - new float2(S(8f), S(16f)), hovered, draw);
    }

    /// The legend, in the row over the plot.
    private static float2 LegendAt(float2 plotMin) => new(plotMin.X, plotMin.Y - S(20f));

    private static void Label(ImDrawListPtr draw, float2 at, Rgba colour, string text, Align align = Align.Left) =>
        Canvas.Text(draw, Mono, S(10.5f), at, colour, text, align);

    /// Horizontal gridlines for pressures from 0 to top psi, labelled in the units shown.
    private static Func<double, float> PressureAxis(ImDrawListPtr draw, float2 min, float2 max, double top)
    {
        float Y(double psi) => max.Y - (float)(psi / top) * (max.Y - min.Y);
        var imperialStep = top > 120 ? 25.0 : 20.0;
        var step = UnitText.PressureUnit == "psi" ? imperialStep : 100.0 / UnitText.KpaPerPsi;
        for (var p = 0.0; p <= top + 1e-6; p += step)
        {
            var y = Y(p);
            draw.AddLine(new float2(min.X, y), new float2(max.X, y), p == 0.0 ? Axis : Grid, 1f);
            Label(draw, new float2(min.X - S(6f), y - S(7f)), TextDim, UnitText.PressureValue(p).ToString("0", C), Align.Right);
        }

        return Y;
    }

    private static void Series(ImDrawListPtr draw, int count, Func<int, float> x, Func<int, float> y, Rgba colour, float thickness)
    {
        for (var i = 1; i < count; i++)
            draw.AddLine(new float2(x(i - 1), y(i - 1)), new float2(x(i), y(i)), colour, thickness);
    }

    private static void Legend(ImDrawListPtr draw, float2 at, params (string Name, Rgba Colour)[] items)
    {
        foreach (var (name, colour) in items)
        {
            draw.AddRectFilled(at + new float2(0f, S(3f)), at + new float2(S(8f), S(11f)), colour);
            var text = name.ToUpperInvariant();
            Canvas.Spaced(draw, Medium, S(9.5f), at + new float2(S(13f), S(1f)), TextDim, text, 0.16f);
            at.X += S(26f) + Canvas.SpacedWidth(Medium, S(9.5f), text, 0.16f);
        }
    }

    private void AlongTheTrain(Train train, float2 size)
    {
        var b = train.Body.Brakes;
        var (min, max, hovered, draw) = Plot("##along", size, UnitText.PressureUnit);
        var y = PressureAxis(draw, min, max, 100.0);
        var n = b.Count;
        float X(int i) => n <= 1 ? (min.X + max.X) * 0.5f : min.X + i / (float)(n - 1) * (max.X - min.X);

        // The equalizing reservoir, what the pipe's being brought to.
        var er = y(b.Equalizing);
        draw.AddLine(new float2(min.X, er), new float2(max.X, er), EqualizingColour.WithAlpha(0.35f), 1f);
        Series(draw, n, X, i => y(b.Aux[i]), AuxColour, 1f);
        Series(draw, n, X, i => y(b.Pipe[i]), PipeColour, 2f);
        Series(draw, n, X, i => y(b.CylinderOf(i)), CylinderColour, 2f);
        Legend(draw, LegendAt(min), ("brake pipe", PipeColour), ("aux reservoir", AuxColour), ("brake cylinder", CylinderColour), ("equalizing", EqualizingColour));
        Label(draw, new float2(min.X, max.Y + S(2f)), TextFaint, "head");
        Label(draw, new float2(max.X, max.Y + S(2f)), TextFaint, "rear", Align.Right);

        if (!hovered || n == 0)
            return;
        var at = n <= 1 ? 0 : (int)MathF.Round((ImGui.GetMousePos().X - min.X) / (max.X - min.X) * (n - 1));
        at = Math.Clamp(at, 0, n - 1);
        draw.AddLine(new float2(X(at), min.Y), new float2(X(at), max.Y), Axis, 1f);
        var car = train.Cars[at];
        Tip($"{at + 1}. {car.Label}", $"brake pipe     {UnitText.Pressure(b.Pipe[at])}", $"aux reservoir  {UnitText.Pressure(b.Aux[at])}",
            $"emergency res  {UnitText.Pressure(b.Reserve[at])}", $"cylinder       {UnitText.Pressure(b.CylinderOf(at))}",
            $"valve          {b.Valve[at].ToString().ToLowerInvariant()}", $"braking        {UnitText.Force(train.Body.Braking[at])}");
    }

    private void OverTime(float2 size)
    {
        var (min, max, hovered, draw) = Plot("##time", size, UnitText.PressureUnit);
        var y = PressureAxis(draw, min, max, 150.0);
        if (_count >= 2)
        {
            var first = (_cursor - _count + Samples) % Samples;
            Sample S(int i) => _history[(first + i) % Samples];
            float X(int i) => max.X - (_count - 1 - i) * SampleEvery / HistorySeconds * (max.X - min.X);
            Series(draw, _count, X, i => y(S(i).Main), MainColour, 1f);
            Series(draw, _count, X, i => y(S(i).Equalizing), EqualizingColour, 1f);
            Series(draw, _count, X, i => y(S(i).RearPipe), RearColour, 2f);
            Series(draw, _count, X, i => y(S(i).HeadPipe), PipeColour, 2f);
            Series(draw, _count, X, i => y(S(i).RearCylinder), CylinderColour.WithAlpha(0.55f), 2f);
            Series(draw, _count, X, i => y(S(i).HeadCylinder), CylinderColour, 2f);

            if (hovered)
            {
                var back = (int)MathF.Round((max.X - ImGui.GetMousePos().X) / (max.X - min.X) * HistorySeconds / SampleEvery);
                var i = Math.Clamp(_count - 1 - back, 0, _count - 1);
                var s = S(i);
                draw.AddLine(new float2(X(i), min.Y), new float2(X(i), max.Y), Axis, 1f);
                Tip(string.Create(C, $"{(_count - 1 - i) * SampleEvery:0} s ago"), $"main        {UnitText.Pressure(s.Main)}", $"equalizing  {UnitText.Pressure(s.Equalizing)}",
                    $"pipe        {UnitText.Pressure(s.HeadPipe)} head, {UnitText.Pressure(s.RearPipe)} rear",
                    $"cylinder    {UnitText.Pressure(s.HeadCylinder)} head, {UnitText.Pressure(s.RearCylinder)} rear");
            }
        }

        Legend(draw, LegendAt(min), ("main", MainColour), ("equalizing", EqualizingColour), ("pipe head", PipeColour), ("pipe rear", RearColour),
            ("cylinder head", CylinderColour), ("cylinder rear", CylinderColour.WithAlpha(0.55f)));
        Label(draw, new float2(min.X, max.Y + S(2f)), TextFaint, "-2:00");
        Label(draw, new float2(max.X, max.Y + S(2f)), TextFaint, "now", Align.Right);
    }

    private void Couplers(Train train, float2 size)
    {
        var body = train.Body;
        var forces = body.CouplerForce;
        var (min, max, hovered, draw) = Plot("##couplers", size, UnitText.ForceKUnit);
        var n = forces.Length;
        if (n == 0)
        {
            Label(draw, min + new float2(S(6f), S(4f)), TextDim, "One vehicle: no couplers in use.");
            return;
        }

        // The scale follows the biggest force, eased so it doesn't jump about, and never under 100.
        var biggest = 0.0;
        int draftAt = 0, buffAt = 0;
        for (var j = 0; j < n; j++)
        {
            biggest = Math.Max(biggest, Math.Abs(UnitText.ForceKValue(forces[j])));
            if (forces[j] > forces[draftAt])
                draftAt = j;
            if (forces[j] < forces[buffAt])
                buffAt = j;
        }

        var wanted = (float)Math.Max(100.0, Math.Ceiling(biggest * 1.15 / 50.0) * 50.0);
        _forceScale += (wanted - _forceScale) * MathF.Min(1f, ImGui.GetIO().DeltaTime * (wanted > _forceScale ? 8f : 1f));
        var scale = _forceScale;

        // A strip along the bottom: how far into its travel each coupling is, from slack (grey) through the gear to solid.
        var strip = 6f;
        var plotMax = new float2(max.X, max.Y - strip - 3f);
        var mid = (min.Y + plotMax.Y) * 0.5f;
        float Y(double value) => mid - (float)(value / scale) * (plotMax.Y - min.Y) * 0.5f;
        // Two or so lines each way.
        var step = 50f;
        foreach (var nice in (ReadOnlySpan<float>)[50f, 100f, 200f, 250f, 500f, 1000f])
        {
            step = nice;
            if (nice >= scale / 2.5f)
                break;
        }

        for (var v = 0f; v <= scale + 1e-3f; v += step)
        {
            foreach (var sign in (ReadOnlySpan<int>)[1, -1])
            {
                if (v == 0f && sign < 0)
                    continue;
                var yy = Y(v * sign);
                draw.AddLine(new float2(min.X, yy), new float2(max.X, yy), v == 0f ? Axis : Grid, 1f);
                Label(draw, new float2(min.X - S(6f), yy - S(7f)), TextDim, (v * sign).ToString("0", C), Align.Right);
            }
        }

        var slot = (max.X - min.X) / n;
        var bar = MathF.Max(1f, slot * 0.7f);
        for (var j = 0; j < n; j++)
        {
            // On whole pixels, so bars side by side come out alike.
            var x = MathF.Floor(min.X + (j + 0.5f) * slot) + 0.5f;
            var value = UnitText.ForceKValue(forces[j]);
            var top = Y(Math.Clamp(value, -scale, scale));
            if (value >= 0.0)
                draw.AddRectFilled(new float2(x - bar * 0.5f, top), new float2(x + bar * 0.5f, mid), DraftColour);
            else
                draw.AddRectFilled(new float2(x - bar * 0.5f, mid), new float2(x + bar * 0.5f, top), BuffColour);

            var travel = Math.Abs(body.Extension[j]) - TrainBody.FreeSlack;
            var colour = !body.Coupled[j] ? Caution.WithAlpha(0.35f) : travel <= 0.0 ? SlackColour : travel <= TrainBody.GearTravel ? Rgba.Lerp(GearColour.WithAlpha(0.5f), GearColour, (float)(travel / TrainBody.GearTravel)) : BuffColour;
            draw.AddRectFilled(new float2(x - slot * 0.5f + 0.5f, max.Y - strip), new float2(x + slot * 0.5f - 0.5f, max.Y), colour);
        }

        Label(draw, LegendAt(min), TextDim, string.Create(C,
            $"max draft {UnitText.ForceK(Math.Max(forces[draftAt], 0.0))} at {draftAt + 1}-{draftAt + 2}   max buff {UnitText.ForceK(Math.Max(-forces[buffAt], 0.0))} at {buffAt + 1}-{buffAt + 2}"));
        Label(draw, new float2(min.X, max.Y + S(2f)), TextFaint, "head");
        Label(draw, new float2(max.X, max.Y + S(2f)), TextFaint, "rear  (strip: slack / draft gear / solid / uncoupled)", Align.Right);

        if (!hovered)
            return;
        var at = Math.Clamp((int)((ImGui.GetMousePos().X - min.X) / slot), 0, n - 1);
        var cx = min.X + (at + 0.5f) * slot;
        draw.AddLine(new float2(cx, min.Y), new float2(cx, max.Y), Axis, 1f);
        var e = body.Extension[at];
        var state = !body.Coupled[at] ? $"uncoupled, {UnitText.Length(body.Gap(at))} apart"
            : Math.Abs(e) <= TrainBody.FreeSlack ? "in the slack" : Math.Abs(e) - TrainBody.FreeSlack <= TrainBody.GearTravel ? "in the draft gear" : "solid";
        Tip($"{at + 1} {train.Cars[at].Label}  |  {at + 2} {train.Cars[at + 1].Label}",
            $"{UnitText.ForceK(Math.Abs(forces[at]))} {(forces[at] > 1.0 ? "draft" : forces[at] < -1.0 ? "buff" : "")}",
            string.Create(C, $"{Math.Abs(e) * 1000.0:0} mm {(e >= 0 ? "stretched" : "bunched")}"), state);
    }
}
