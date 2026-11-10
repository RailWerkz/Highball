using Brutal;
using Brutal.VulkanApi.Vma;

namespace RailWerkz.Vulkan;

/// Buffer creation and uploads through a staging buffer.
public static class Buffers
{
    /// PreferGpu: device-local, for data only the GPU touches. PreferCpu: host-visible and coherent, for staging
    /// uploads and reading results back. Either way the memory comes out of Gpu.Allocator.
    public static BufferEx Create(long size, VkBufferUsageFlags usage, MemoryPreference memory, string name)
    {
        var host = memory == MemoryPreference.PreferCpu;
        return Gpu.Allocator.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = name,
            BufferSize = ByteSize.Of(Math.Max(size, 16L)),
            BufferUsage = usage,
            BufferSharingMode = VkSharingMode.Exclusive,
            AllocPreference = memory,
            AllocRequiredProperties = host ? VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit : VkMemoryPropertyFlags.DeviceLocalBit,
        });
    }

    /// Makes what the GPU wrote into a host-visible buffer visible to the CPU before reading it back. Goes through
    /// VMA because the buffer sits at an offset inside a shared block, which MappedMemory.Invalidate does not know.
    public static void Invalidate(BufferEx buffer)
    {
        var allocation = new VmaAllocation(buffer.BufferAllocationInfo.AllocationInfo.AllocHandle);
        var result = Gpu.Allocator.InvalidateAllocation(allocation, ByteSize.Zero, ByteSize.Of((long)VK.WHOLE_SIZE));
        if (result != VkResult.Success)
            throw new InvalidOperationException($"Vulkan: invalidating {buffer.VkBuffer} failed ({result})");
    }

    /// A host-visible buffer holding these bytes, for copying from.
    public static BufferEx Staging(ReadOnlySpan<byte> data, string name)
    {
        var staging = Create(data.Length, VkBufferUsageFlags.TransferSrcBit, MemoryPreference.PreferCpu, name);
        using var mapped = staging.Map();
        data.CopyTo(mapped.AsSpan());
        return staging;
    }

    private static readonly List<(BufferEx Staging, BufferEx Buffer, int Size)> Pending = [];

    /// A device-local buffer that will hold data once the next frame starts: the copy is recorded at the top of that
    /// frame's commands instead of waiting for the GPU here. Main thread only, and not for drawing before that frame.
    public static BufferEx UploadLater(ReadOnlySpan<byte> data, VkBufferUsageFlags usage, string name)
    {
        var buffer = Create(data.Length, usage | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, name);
        Pending.Add((Staging(data, name + ".staging"), buffer, data.Length));
        return buffer;
    }

    /// Records the copies UploadLater asked for, then one barrier so the frame's vertex and index reads see them.
    internal static void FlushUploads(CommandBuffer cmd)
    {
        if (Pending.Count == 0)
            return;

        foreach (var (staging, buffer, size) in Pending)
        {
            cmd.CopyBuffer(staging.VkBuffer, buffer.VkBuffer, ByteSize.Of(size));
            Gpu.Defer(staging.Dispose);
        }

        Pending.Clear();
        var barrier = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.TransferWriteBit,
            DstAccessMask = VkAccessFlags.VertexAttributeReadBit | VkAccessFlags.IndexReadBit,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.VertexInputBit, VkDependencyFlags.None, [barrier], [], []);
    }

    /// A device-local buffer filled with data (waits for the copy).
    public static BufferEx Upload(ReadOnlySpan<byte> data, VkBufferUsageFlags usage, string name)
    {
        var buffer = Create(data.Length, usage | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, name);
        using var staging = Staging(data, name + ".staging");
        var size = data.Length;
        Gpu.Submit(cmd => cmd.CopyBuffer(staging.VkBuffer, buffer.VkBuffer, ByteSize.Of(size)));
        return buffer;
    }
}
