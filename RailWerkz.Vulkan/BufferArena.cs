using Brutal;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace RailWerkz.Vulkan;

/// One device-local buffer shared by many allocations of fixed-size elements (instance data streamed in and out), so they
/// can all be drawn from one binding: a first-fit free list, with what's freed reused only once the frames that may still
/// read it are done. Uploads and growth are recorded into the frame's commands as they happen, outside any render pass.
public sealed class BufferArena : IDisposable
{
    private readonly int _stride;
    private readonly VkBufferUsageFlags _usage;
    private readonly string _name;
    private readonly List<(int Start, int Count)> _free = [];
    private bool _written;

    public BufferEx Buffer { get; private set; }
    public int Capacity { get; private set; }
    public int Used { get; private set; }

    public BufferArena(int stride, int capacity, VkBufferUsageFlags usage, string name)
    {
        _stride = stride;
        _usage = usage | VkBufferUsageFlags.TransferDstBit | VkBufferUsageFlags.TransferSrcBit;
        _name = name;
        Capacity = Math.Max(capacity, 64);
        Buffer = Buffers.Create((long)Capacity * stride, _usage, MemoryPreference.PreferGpu, name);
        _free.Add((0, Capacity));
    }

    /// Room for count elements, filled with data (count elements' bytes) by a copy recorded into cmd. Returns the first
    /// element's index. Call EndUploads before anything draws from what was uploaded.
    public int Upload(CommandBuffer cmd, ReadOnlySpan<byte> data, int count)
    {
        count = Math.Max(count, 1);
        var start = Take(count);
        if (start < 0)
        {
            Grow(cmd, Capacity + count);
            start = Take(count);
        }

        Used += count;
        if (data.Length > 0)
        {
            var staging = Buffers.Staging(data, _name + ".staging");
            cmd.CopyBuffer(staging.VkBuffer, Buffer.VkBuffer, [new VkBufferCopy { SrcOffset = new ByteSize64(0), DstOffset = new ByteSize64((ulong)((long)start * _stride)), Size = new ByteSize64((ulong)(data.Length)) }]);
            Gpu.Defer(staging.Dispose);
            _written = true;
        }

        return start;
    }

    /// One barrier so the frame's vertex reads see this frame's uploads.
    public void EndUploads(CommandBuffer cmd)
    {
        if (!_written)
            return;
        _written = false;
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.VertexAttributeReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.VertexInputBit, VkDependencyFlags.None, [barrier], [], []);
    }

    /// Gives back count elements from start, once the frames that may still draw them are done.
    public void Free(int start, int count)
    {
        count = Math.Max(count, 1);
        Used -= count;
        // (Indices hold across growth: a grown buffer has everything at the same places.)
        Gpu.Defer(() => Release(start, count));
    }

    private void Release(int start, int count)
    {
        var i = 0;
        while (i < _free.Count && _free[i].Start < start)
            i++;
        _free.Insert(i, (start, count));
        // Joined with its neighbours where they touch.
        if (i + 1 < _free.Count && _free[i].Start + _free[i].Count == _free[i + 1].Start)
        {
            _free[i] = (_free[i].Start, _free[i].Count + _free[i + 1].Count);
            _free.RemoveAt(i + 1);
        }

        if (i > 0 && _free[i - 1].Start + _free[i - 1].Count == _free[i].Start)
        {
            _free[i - 1] = (_free[i - 1].Start, _free[i - 1].Count + _free[i].Count);
            _free.RemoveAt(i);
        }
    }

    private int Take(int count)
    {
        for (var i = 0; i < _free.Count; i++)
        {
            var (start, size) = _free[i];
            if (size < count)
                continue;
            if (size == count)
                _free.RemoveAt(i);
            else
                _free[i] = (start + count, size - count);
            return start;
        }

        return -1;
    }

    /// A buffer at least needed elements long, with everything copied across to the same places. The old one's let go once
    /// the frames drawing from it are done.
    private void Grow(CommandBuffer cmd, int needed)
    {
        var capacity = Capacity;
        while (capacity < needed)
            capacity *= 2;
        var grown = Buffers.Create((long)capacity * _stride, _usage, MemoryPreference.PreferGpu, _name);
        var old = Buffer;
        // Earlier uploads this frame must land before they're copied across.
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [barrier], [], []);
        cmd.CopyBuffer(old.VkBuffer, grown.VkBuffer, [new VkBufferCopy { SrcOffset = new ByteSize64(0), DstOffset = new ByteSize64(0), Size = new ByteSize64((ulong)((long)Capacity * _stride)) }]);
        Gpu.Defer(old.Dispose);
        Release(Capacity, capacity - Capacity);
        Buffer = grown;
        Capacity = capacity;
        _written = true;
    }

    public void Dispose()
    {
        var buffer = Buffer;
        Gpu.Defer(buffer.Dispose);
    }
}
