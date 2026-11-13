using System.Runtime.InteropServices;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace RailWerkz.Vulkan;

/// Data written fresh every frame (instances, indirect commands, per-draw tables), appended into a host-visible buffer of the
/// frame's own slot, so it's safe from frames still in flight and can be written at any point while the frame's recorded,
/// with nothing to copy and no barrier. Starts again empty each frame.
public sealed class FrameRing<T> : IDisposable where T : unmanaged
{
    private readonly BufferEx[] _buffers = new BufferEx[Gpu.FramesInFlight];
    private readonly int[] _capacity = new int[Gpu.FramesInFlight];
    private readonly VkBufferUsageFlags _usage;
    private readonly string _name;
    private long _frame = -1;
    private int _used;

    public FrameRing(VkBufferUsageFlags usage, string name)
    {
        _usage = usage;
        _name = name;
    }

    /// Appends items; returns the buffer they went into and the index of the first. A buffer outgrown mid-frame stays alive
    /// (and keeps what's in it) for what's already been recorded from it, and goes once the frame's done.
    public (BufferEx Buffer, int First) Append(ReadOnlySpan<T> items)
    {
        if (_frame != Gpu.FrameNumber)
            (_frame, _used) = (Gpu.FrameNumber, 0);

        var slot = Gpu.FrameSlot;
        var needed = _used + Math.Max(items.Length, 1);
        if (_capacity[slot] < needed)
        {
            var capacity = Math.Max(256, _capacity[slot]);
            while (capacity < needed)
                capacity *= 2;
            if (_capacity[slot] > 0)
                Gpu.Defer(_buffers[slot].Dispose);
            _buffers[slot] = Buffers.Create((long)capacity * Marshal.SizeOf<T>(), _usage, MemoryPreference.PreferCpu, _name);
            _capacity[slot] = capacity;
            // The new buffer starts empty: this frame's earlier items are read from the old one by what named it.
            _used = 0;
        }

        var buffer = _buffers[slot];
        var first = _used;
        if (items.Length > 0)
        {
            using var mapped = buffer.Map();
            MemoryMarshal.AsBytes(items).CopyTo(mapped.AsSpan()[(first * Marshal.SizeOf<T>())..]);
        }

        _used += items.Length;
        return (buffer, first);
    }

    public void Dispose()
    {
        for (var i = 0; i < _buffers.Length; i++)
            if (_capacity[i] > 0)
                Gpu.Defer(_buffers[i].Dispose);
        Array.Clear(_capacity);
    }
}
