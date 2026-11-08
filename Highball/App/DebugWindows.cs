using RailWerkz.UI;
using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Highball.App;

/// The debug windows every Route Builder view shares: Frame Statistics (F4), Profiler (F5) and Threading (F6).
public static class DebugWindows
{
    private const double SampleSeconds = 0.5;

    private static bool _stats;
    private static bool _profiler;
    private static bool _threading;

    private static readonly float[] CpuHistory = new float[Profiler.History];
    private static int _cpuCursor;
    private static float _cpu;
    private static double _sampledAt;
    private static TimeSpan _cpuAt;
    private static long _ticksAt;

    public static void ShowAll() => _stats = _profiler = _threading = true;

    public static void Keys()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.F4))
            _stats = !_stats;
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
            _profiler = !_profiler;
        if (ImGui.IsKeyPressed(ImGuiKey.F6))
            _threading = !_threading;
    }

    public static void Menu()
    {
        ImGui.MenuItem("Frame Statistics"u8, "F4"u8, ref _stats);
        ImGui.MenuItem("Profiler"u8, "F5"u8, ref _profiler);
        ImGui.MenuItem("Threading"u8, "F6"u8, ref _threading);
    }

    /// x, y: where they first open. stats: the view's own lines in Frame Statistics. job: a route build in progress, if there is one.
    public static void Draw(float x, float y, Action stats, (string Status, float Progress)? job)
    {
        Sample();
        if (_stats)
            StatsWindow(x, y, stats);
        if (_profiler)
            ProfilerWindow(x, y + 290f);
        if (_threading)
            ThreadingWindow(x + 572f, y, job);
    }

    private static void StatsWindow(float x, float y, Action stats)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(560f, 280f), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Frame Statistics"u8, ref _stats, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            var times = Profiler.FrameTimes;
            var average = times.Average();
            ImGui.Text($"{1000f / MathF.Max(0.01f, average):0} fps   {average:0.00} ms   worst {times.Max():0.0} ms   GPU {Profiler.GpuFrameTimes.Average():0.00} ms");
            ImGui.PlotLines("##frames"u8, times, Profiler.FrameCursor, default, 0f, 40f, new float2(300f, 50f));
            ImGui.Text($"{Profiler.DrawCalls} draw calls   {Profiler.Triangles / 1000f:0.0}k triangles");
            ImGui.Text($"GPU {HighballApp.Instance.GpuName}");
            stats();
        }

        ImGui.End();
    }

    private static void ProfilerWindow(float x, float y)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Profiler"u8, ref _profiler, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Time per frame, over the last two seconds"u8);
            if (ImGui.BeginTable("sections"u8, 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Section"u8);
                ImGui.TableSetupColumn("CPU last"u8);
                ImGui.TableSetupColumn("CPU avg"u8);
                ImGui.TableSetupColumn("CPU worst"u8);
                ImGui.TableSetupColumn("GPU avg"u8);
                ImGui.TableSetupColumn("GPU worst"u8);
                ImGui.TableSetupColumn("Draws"u8);
                ImGui.TableSetupColumn("Triangles"u8);
                ImGui.TableHeadersRow();

                foreach (var (section, depth) in Profiler.Tree())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(new string(' ', depth * 2) + section.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Last:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Average:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.Worst:0.00}");
                    ImGui.TableNextColumn();
                    if (section.HasGpu)
                        ImGui.Text($"{section.GpuAverage:0.00}");
                    ImGui.TableNextColumn();
                    if (section.HasGpu)
                        ImGui.Text($"{section.GpuWorst:0.00}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{section.DrawCalls}");
                    ImGui.TableNextColumn();
                    if (section.Triangles > 0)
                        ImGui.Text($"{section.Triangles / 1000f:0.0}k");
                }

                ImGui.EndTable();
            }

            ImGui.Text("In milliseconds. Wait for GPU and Present include waiting for the display. GPU times count waits for other programs using it."u8);
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
            ImGui.Text($"Whole game {_cpu:0}% of {logical} threads, {_cpu * logical / 100f:0.0} in use");
            ImGui.PlotLines("##cpu"u8, CpuHistory, _cpuCursor, default, 0f, 100f, new float2(420f, 50f));
            ImGui.Text($"{JobStream.Cores} threads for jobs   {ThreadPool.ThreadCount} in the pool");

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
