using System.Runtime.InteropServices;
using Brutal;

namespace RailWerkz.Vulkan;

/// How long the GPU itself spends on parts of a frame, from timestamps written into the command buffer. A span is opened and closed
/// while recording; its time is known only once that frame's slot comes round again, a frame or two later, and Drain hands it over.
public static class GpuTimers
{
    /// Timestamps per frame: two for each span. Spans past this aren't timed.
    private const int MaxQueries = 256;

    private sealed class Slot
    {
        public VkQueryPool Pool;
        public int Written;
        public readonly List<(object Owner, int Begin, int End)> Spans = [];
    }

    private static readonly Slot[] Slots = new Slot[Gpu.FramesInFlight];
    private static readonly List<(object Owner, float Milliseconds)> Finished = [];
    private static readonly ulong[] Values = new ulong[MaxQueries];
    private static Slot? _current;
    private static double _millisecondsPerTick;

    /// False on a GPU whose graphics queue can't write timestamps; every span is then ignored.
    public static bool Supported { get; private set; }

    internal static void Init()
    {
        var limits = Gpu.PhysicalDevice.GetProperties().Limits;
        Supported = (bool)limits.TimestampComputeAndGraphics;
        if (!Supported)
            return;

        _millisecondsPerTick = limits.TimestampPeriod / 1e6;
        var info = new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = MaxQueries };
        for (var i = 0; i < Slots.Length; i++)
            Slots[i] = new Slot { Pool = Gpu.Device.CreateQueryPool(in info, Gpu.Device.VkAllocator) };
    }

    /// The frame in this slot has begun recording and the one before it in the slot has finished: collects that one's times and starts over.
    internal static void BeginFrame(CommandBuffer cmd, int slot)
    {
        if (!Supported)
            return;

        var current = _current = Slots[slot];
        Collect(current);
        cmd.ResetQueryPool(current.Pool, 0, MaxQueries);
        current.Written = 0;
        current.Spans.Clear();
    }

    private static void Collect(Slot slot)
    {
        if (slot.Written == 0)
            return;

        // Never waits: its fence has been passed, and if the results still weren't ready this frame just goes untimed.
        var result = Gpu.Device.GetQueryPoolResults(slot.Pool, 0, slot.Written, MemoryMarshal.AsBytes(Values.AsSpan(0, slot.Written)), new ByteSize64(sizeof(ulong)), VkQueryResultFlags._64Bit);
        if (result != VkResult.Success)
            return;

        foreach (var (owner, begin, end) in slot.Spans)
            if (end >= 0 && Values[end] >= Values[begin]) // the counter can wrap
                Finished.Add((owner, (float)((Values[end] - Values[begin]) * _millisecondsPerTick)));
    }

    /// Starts timing, once everything recorded so far is done. Returns what to pass to End, or -1 if this one isn't being timed.
    public static int Begin(CommandBuffer cmd, object owner)
    {
        if (_current is not { } slot || slot.Written + 2 > MaxQueries)
            return -1;

        cmd.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, slot.Pool, slot.Written);
        slot.Spans.Add((owner, slot.Written, -1));
        slot.Written += 2; // Room for its end, so a span always has one.
        return slot.Spans.Count - 1;
    }

    public static void End(CommandBuffer cmd, int span)
    {
        if (span < 0 || _current is not { } slot)
            return;

        var (owner, begin, _) = slot.Spans[span];
        cmd.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, slot.Pool, begin + 1);
        slot.Spans[span] = (owner, begin, begin + 1);
    }

    /// Hands over the times that have come in since the last call.
    public static void Drain(Action<object, float> report)
    {
        foreach (var (owner, milliseconds) in Finished)
            report(owner, milliseconds);
        Finished.Clear();
    }

    internal static void Shutdown()
    {
        foreach (var slot in Slots)
            if (slot is not null)
                Gpu.Device.DestroyQueryPool(slot.Pool, Gpu.Device.VkAllocator);
    }
}
