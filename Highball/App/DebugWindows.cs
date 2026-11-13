using RailWerkz.UI;
using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Highball.App;

/// The debug windows every Route Builder view shares: Frame Statistics (F4), Profiler (F5), Threading (F6) and Camera (F7).
public static class DebugWindows
{
    private const double SampleSeconds = 0.5;

    private static bool _stats;
    private static bool _profiler;
    private static bool _threading;
    private static bool _camera;

    private static readonly float[] CpuHistory = new float[Profiler.History];
    private static int _cpuCursor;
    private static float _cpu;
    private static double _sampledAt;
    private static TimeSpan _cpuAt;
    private static long _ticksAt;

    public static void ShowAll() => _stats = _profiler = _threading = _camera = true;

    public static void Keys()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.F4))
            _stats = !_stats;
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
            _profiler = !_profiler;
        if (ImGui.IsKeyPressed(ImGuiKey.F6))
            _threading = !_threading;
        if (ImGui.IsKeyPressed(ImGuiKey.F7))
            _camera = !_camera;
    }

    public static void Menu()
    {
        ImGui.MenuItem("Frame Statistics"u8, "F4"u8, ref _stats);
        ImGui.MenuItem("Profiler"u8, "F5"u8, ref _profiler);
        ImGui.MenuItem("Threading"u8, "F6"u8, ref _threading);
        ImGui.MenuItem("Camera"u8, "F7"u8, ref _camera);
    }

    /// x, y: where they first open. stats: the view's own rows in Frame Statistics (Heading and Row). camera: the Camera
    /// window's rows and its launch line, or null where there's no camera (the Planner). job: a route build in progress, if any.
    public static void Draw(float x, float y, Action stats, (string Status, float Progress)? job, Func<string>? camera = null)
    {
        Sample();
        if (_stats)
            StatsWindow(x, y, stats);
        if (_camera)
            CameraWindow(x + 1080f, y, camera);
        if (_profiler)
            ProfilerWindow(x, y + 600f);
        if (_threading)
            ThreadingWindow(x + 572f, y, job);
    }

    /// A labelled section of rows in Frame Statistics or Camera.
    public static void Heading(string name)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Dummy(new float2(1f, 4f));
        ImGui.TextColored(Style.Accent.Float4, name.ToUpperInvariant());
        ImGui.TableNextColumn();
    }

    /// One row: its name on the left, dim, and its value on the right.
    public static void Row(string name, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(Style.TextDim.Float4, name);
        ImGui.TableNextColumn();
        ImGui.Text(value);
    }

    private static bool BeginRows(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return false;
        ImGui.TableSetupColumn("name"u8, ImGuiTableColumnFlags.WidthFixed, 175f);
        ImGui.TableSetupColumn("value"u8, ImGuiTableColumnFlags.WidthFixed, 280f);
        return true;
    }

    private static void StatsWindow(float x, float y, Action stats)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Frame Statistics"u8, ref _stats, ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize))
        {
            var times = Profiler.FrameTimes;
            var average = times.Average();
            ImGui.Text($"{1000f / MathF.Max(0.01f, average):0} fps    {average:0.0} ms    GPU {Profiler.GpuFrameTimes.Average():0.0} ms");
            ImGui.PlotLines("##frames"u8, times, Profiler.FrameCursor, default, 0f, 40f, new float2(380f, 44f));
            if (BeginRows("stats"))
            {
                Heading("Frame");
                Row("Worst frame", $"{times.Max():0.0} ms");
                Row("Draw calls", $"{Profiler.DrawCalls:#,0}");
                Row("Triangles", $"{Profiler.Triangles / 1_000_000f:0.00} M");
                Row("GPU", HighballApp.Instance.GpuName);
                stats();
                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    /// Everything needed to put the camera back exactly where it is, and a launch line that does it.
    private static void CameraWindow(float x, float y, Func<string>? camera)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Camera"u8, ref _camera, ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (camera is null)
                ImGui.Text("No camera."u8);
            else
            {
                var line = "";
                if (BeginRows("camera"))
                {
                    line = camera();
                    ImGui.EndTable();
                }

                ImGui.Separator();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 440f);
                ImGui.TextColored(Style.TextDim.Float4, line);
                ImGui.PopTextWrapPos();
                if (ImGui.Button("Copy launch line"u8))
                    ImGui.SetClipboardText(line);
            }
        }

        ImGui.End();
    }

    /// A value, red when it's over its section's budget.
    private static void Value(string text, bool over)
    {
        if (over)
            ImGui.TextColored(Style.Stop.Float4, text);
        else
            ImGui.Text(text);
    }

    private static string Budget((float Cpu, float Gpu, int Draws) budget) => string.Join("  ", new[]
    {
        budget.Cpu > 0f ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"CPU {budget.Cpu:0.#}") : null,
        budget.Gpu > 0f ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"GPU {budget.Gpu:0.#}") : null,
        budget.Draws > 0 ? $"{budget.Draws} draws" : null,
    }.Where(x => x is not null));

    private static void ProfilerWindow(float x, float y)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Profiler"u8, ref _profiler, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (ImGui.BeginTable("sections"u8, 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Section"u8);
                ImGui.TableSetupColumn("CPU last ms"u8);
                ImGui.TableSetupColumn("CPU avg ms"u8);
                ImGui.TableSetupColumn("CPU worst ms"u8);
                ImGui.TableSetupColumn("GPU avg ms"u8);
                ImGui.TableSetupColumn("GPU worst ms"u8);
                ImGui.TableSetupColumn("Draws"u8);
                ImGui.TableSetupColumn("Triangles"u8);
                ImGui.TableSetupColumn("Budget"u8);
                ImGui.TableHeadersRow();

                foreach (var (section, depth) in Profiler.Tree())
                {
                    var (cpuOver, gpuOver, drawsOver) = Profiler.Over(section);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(new string(' ', depth * 2) + section.Name);
                    ImGui.TableNextColumn();
                    Value($"{section.Last:0.00}", cpuOver);
                    ImGui.TableNextColumn();
                    Value($"{section.Average:0.00}", cpuOver);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Worst:0.00}");
                    ImGui.TableNextColumn();
                    if (section.HasGpu)
                        Value($"{section.GpuAverage:0.00}", gpuOver);
                    ImGui.TableNextColumn();
                    if (section.HasGpu)
                        ImGui.Text($"{section.GpuWorst:0.00}");
                    ImGui.TableNextColumn();
                    Value($"{section.DrawCalls}", drawsOver);
                    ImGui.TableNextColumn();
                    if (section.Triangles > 0)
                        ImGui.Text($"{section.Triangles / 1000f:0.0}k");
                    ImGui.TableNextColumn();
                    if (Profiler.Budgets.TryGetValue(section.Name, out var budget))
                        ImGui.TextColored(Style.TextDim.Float4, Budget(budget));
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    /// Twice a second: how much of the machine the whole game is using, and each activity's share of it.
    private static void Sample()
    {
        var now = RailWerkz.Core.Time.Time.Now;
        if (now - _sampledAt < SampleSeconds)
            return;

        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var ticks = Stopwatch.GetTimestamp();
        if (_sampledAt > 0.0)
        {
            var seconds = (ticks - _ticksAt) / (double)Stopwatch.Frequency;
            _cpu = (float)((cpu - _cpuAt).TotalSeconds / seconds / Environment.ProcessorCount * 100.0);
            CpuHistory[_cpuCursor] = _cpu;
            _cpuCursor = (_cpuCursor + 1) % CpuHistory.Length;
            foreach (var activity in Background.All())
            {
                var busy = activity.BusyTicks;
                activity.Cores = (float)((busy - activity.LastBusy) / (double)Stopwatch.Frequency / seconds);
                activity.LastBusy = busy;
            }
        }

        (_sampledAt, _cpuAt, _ticksAt) = (now, cpu, ticks);
    }

    private static void ThreadingWindow(float x, float y, (string Status, float Progress)? job)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Threading"u8, ref _threading, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var logical = Environment.ProcessorCount;
            ImGui.Text($"CPU {_cpu:0}% of {logical} threads ({_cpu * logical / 100f:0.0} busy)");
            ImGui.PlotLines("##cpu"u8, CpuHistory, _cpuCursor, default, 0f, 100f, new float2(420f, 50f));
            ImGui.Text($"Jobs {JobStream.Cores}   Pool {ThreadPool.ThreadCount}");

            if (job is { } building)
            {
                ImGui.Separator();
                ImGui.TextWrapped($"{building.Progress * 100f:0}%  {building.Status}");
            }

            if (ImGui.BeginTable("work"u8, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Work"u8);
                ImGui.TableSetupColumn("Now"u8);
                ImGui.TableSetupColumn("Done"u8);
                ImGui.TableSetupColumn("Threads busy"u8);
                ImGui.TableHeadersRow();
                foreach (var activity in Background.All())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(activity.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text(activity.Runners > 0 ? $"{activity.Running} / {activity.Runners}" : $"{activity.Running}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{activity.Done}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{activity.Cores:0.0}");
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }
}
