using System.Diagnostics;
using System.Globalization;
using System.Text;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.App;

/// CPU timings for the Profiler window (from DeathFromAbove): wrap a section in `using (Profiler.Sample("Name"))` and it
/// shows up under the section it ran inside, with its last frame, average and worst over the last two seconds, and the
/// draw calls it made last frame. Give it the command buffer too, `Profiler.Sample("Name", cmd)`, and the GPU's time on what
/// it records is measured as well. Also keeps the frame times and draw counts the Stats window shows.
public static class Profiler
{
    public const int History = 120;

    public sealed class Section(string name, Section? parent)
    {
        public string Name { get; } = name;
        public Section? Parent { get; } = parent;
        public List<Section> Children { get; } = [];
        public float[] Samples { get; } = new float[History];
        public float Last => Samples[(_cursor + History - 1) % History];
        public float Average => Samples.Average();
        public float Worst => Samples.Max();
        public int DrawCalls { get; internal set; }
        public float[] GpuSamples { get; } = new float[History];
        public float GpuAverage => GpuSamples.Average();
        public float GpuWorst => GpuSamples.Max();
        /// Whether the GPU's time on this section has been measured.
        public bool HasGpu { get; internal set; }
        internal double Accumulated;
        internal int AccumulatedCalls;
        internal float GpuAccumulated;
    }

    private static readonly Dictionary<(Section?, string), Section> Known = [];
    private static readonly List<Section> Roots = [];
    private static readonly Stack<Section> Open = new();
    private static int _cursor;

    /// Milliseconds per frame, oldest first from FrameCursor.
    public static float[] FrameTimes { get; } = new float[History];
    public static int FrameCursor { get; private set; }

    /// Last frame's totals.
    public static int DrawCalls { get; private set; }
    public static long Triangles { get; private set; }

    /// Milliseconds the GPU spent on the longest timed section of each frame, which is the one round them all; same order as FrameTimes.
    public static float[] GpuFrameTimes { get; } = new float[History];
    private static float _gpuLongest;

    /// Every section, parents before their children, with how deep each one sits.
    public static IEnumerable<(Section Section, int Depth)> Tree()
    {
        IEnumerable<(Section, int)> Walk(Section section, int depth)
        {
            yield return (section, depth);
            foreach (var child in section.Children)
                foreach (var item in Walk(child, depth + 1))
                    yield return item;
        }

        return Roots.ToList().SelectMany(root => Walk(root, 0));
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Section _section;
        private readonly long _start;
        private readonly int _calls;
        private readonly CommandBuffer _cmd;
        private readonly int _gpuSpan;

        internal Scope(Section section)
        {
            _section = section;
            _start = Stopwatch.GetTimestamp();
            _calls = Mesh.DrawCalls;
            _gpuSpan = -1;
            Open.Push(section);
        }

        internal Scope(Section section, CommandBuffer cmd) : this(section)
        {
            _cmd = cmd;
            _gpuSpan = GpuTimers.Begin(cmd, section);
        }

        public void Dispose()
        {
            GpuTimers.End(_cmd, _gpuSpan);
            if (Open.Count > 0 && Open.Peek() == _section)
                Open.Pop();
            _section.Accumulated += Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _section.AccumulatedCalls += Mesh.DrawCalls - _calls;
        }
    }

    public static Scope Sample(string name) => new(Find(name));

    /// A section of what's being recorded into cmd, timed on the GPU as well.
    public static Scope Sample(string name, CommandBuffer cmd) => new(Find(name), cmd);

    private static Section Find(string name)
    {
        var parent = Open.Count > 0 ? Open.Peek() : null;
        if (!Known.TryGetValue((parent, name), out var section))
        {
            Known[(parent, name)] = section = new Section(name, parent);
            (parent?.Children ?? Roots).Add(section);
        }

        return section;
    }

    /// Call once per frame, before anything is sampled: last frame's totals go into the history.
    public static void BeginFrame(float frameSeconds)
    {
        _gpuLongest = 0f;
        GpuTimers.Drain(static (owner, milliseconds) =>
        {
            var section = (Section)owner;
            section.GpuAccumulated += milliseconds;
            section.HasGpu = true;
            _gpuLongest = MathF.Max(_gpuLongest, milliseconds);
        });

        foreach (var section in Known.Values)
        {
            section.Samples[_cursor] = (float)section.Accumulated;
            section.Accumulated = 0;
            section.DrawCalls = section.AccumulatedCalls;
            section.AccumulatedCalls = 0;
            section.GpuSamples[_cursor] = section.GpuAccumulated;
            section.GpuAccumulated = 0f;
        }

        _cursor = (_cursor + 1) % History;
        Open.Clear();

        FrameTimes[FrameCursor] = frameSeconds * 1000f;
        GpuFrameTimes[FrameCursor] = _gpuLongest;
        FrameCursor = (FrameCursor + 1) % History;
        DrawCalls = Mesh.DrawCalls;
        Triangles = Mesh.Triangles;
        Mesh.ResetStats();
    }

    /// The frame, draw counts and every section the GPU's time was measured on, as text for the log.
    public static string GpuReport()
    {
        var times = new StringBuilder();
        times.Append(CultureInfo.InvariantCulture, $"Frame {FrameTimes.Average():0.00} ms ({1000f / MathF.Max(0.01f, FrameTimes.Average()):0} fps), GPU {GpuFrameTimes.Average():0.00} ms, {DrawCalls} draw calls, {Triangles / 1000f:0.0}k triangles");
        foreach (var (section, depth) in Tree())
            if (section.HasGpu)
                times.Append(CultureInfo.InvariantCulture, $"\n  {new string(' ', depth * 2)}{section.Name}: {section.GpuAverage:0.00} ms, worst {section.GpuWorst:0.00}");
        return times.ToString();
    }
}
