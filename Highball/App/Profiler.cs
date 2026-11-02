using System.Diagnostics;
using RailWerkz.Vulkan;

namespace Highball.App;

/// CPU timings for the Profiler window (from DeathFromAbove): wrap a section in `using (Profiler.Sample("Name"))` and it
/// shows up under the section it ran inside, with its last frame, average and worst over the last two seconds, and the
/// draw calls it made last frame. Also keeps the frame times and draw counts the Stats window shows.
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
        internal double Accumulated;
        internal int AccumulatedCalls;
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

        internal Scope(Section section)
        {
            _section = section;
            _start = Stopwatch.GetTimestamp();
            _calls = Mesh.DrawCalls;
            Open.Push(section);
        }

        public void Dispose()
        {
            if (Open.Count > 0 && Open.Peek() == _section)
                Open.Pop();
            _section.Accumulated += Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _section.AccumulatedCalls += Mesh.DrawCalls - _calls;
        }
    }

    public static Scope Sample(string name)
    {
        var parent = Open.Count > 0 ? Open.Peek() : null;
        if (!Known.TryGetValue((parent, name), out var section))
        {
            Known[(parent, name)] = section = new Section(name, parent);
            (parent?.Children ?? Roots).Add(section);
        }

        return new Scope(section);
    }

    /// Call once per frame, before anything is sampled: last frame's totals go into the history.
    public static void BeginFrame(float frameSeconds)
    {
        foreach (var section in Known.Values)
        {
            section.Samples[_cursor] = (float)section.Accumulated;
            section.Accumulated = 0;
            section.DrawCalls = section.AccumulatedCalls;
            section.AccumulatedCalls = 0;
        }

        _cursor = (_cursor + 1) % History;
        Open.Clear();

        FrameTimes[FrameCursor] = frameSeconds * 1000f;
        FrameCursor = (FrameCursor + 1) % History;
        DrawCalls = Mesh.DrawCalls;
        Triangles = Mesh.Triangles;
        Mesh.ResetStats();
    }
}
