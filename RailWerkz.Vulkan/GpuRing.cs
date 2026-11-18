using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace RailWerkz.Vulkan;

/// Scratch space on the GPU that only the GPU writes and reads within a frame (culling's output: indirect commands, their counts
/// and tables), handed out in aligned pieces from a buffer of the frame's own slot. Starts again empty each frame; a buffer
/// outgrown mid-frame stays alive for what's already been recorded from it.
public sealed class GpuRing : IDisposable
{
    private const long Align = 256;
    private readonly BufferEx[] _buffers = new BufferEx[Gpu.FramesInFlight];
    private readonly long[] _capacity = new long[Gpu.FramesInFlight];
    private readonly VkBufferUsageFlags _usage;
    private readonly string _name;
    private long _frame = -1;
    private long _used;

    public GpuRing(VkBufferUsageFlags usage, string name)
    {
        _usage = usage | VkBufferUsageFlags.ShaderDeviceAddressBit | VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit;
        _name = name;
    }

    public (BufferEx Buffer, long Offset) Reserve(long bytes)
    {
        if (_frame != Gpu.FrameNumber)
            (_frame, _used) = (Gpu.FrameNumber, 0);

        var slot = Gpu.FrameSlot;
        bytes = Math.Max((bytes + Align - 1) / Align * Align, Align);
        if (_capacity[slot] < _used + bytes)
        {
            var capacity = Math.Max(1L << 16, _capacity[slot]);
            while (capacity < _used + bytes)
                capacity *= 2;
            if (_capacity[slot] > 0)
                Gpu.Defer(_buffers[slot].Dispose);
            _buffers[slot] = Buffers.Create(capacity, _usage, MemoryPreference.PreferGpu, _name);
            _capacity[slot] = capacity;
            _used = 0;
        }

        var offset = _used;
        _used += bytes;
        return (_buffers[slot], offset);
    }

    public void Dispose()
    {
        for (var i = 0; i < _buffers.Length; i++)
            if (_capacity[i] > 0)
                Gpu.Defer(_buffers[i].Dispose);
        Array.Clear(_capacity);
    }
}
