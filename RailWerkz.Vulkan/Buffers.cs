using Brutal;

namespace RailWerkz.Vulkan;

/// Buffer creation and uploads through a staging buffer.
public static class Buffers
{
    public static BufferEx Create(long size, VkBufferUsageFlags usage, MemoryPreference memory, string name)
    {
        var host = memory == MemoryPreference.PreferCpu;
        return Gpu.Device.CreateBuffer(new BufferEx.CreateInfo
        {
            Name = name,
            BufferSize = ByteSize.Of(Math.Max(size, 16L)),
            BufferUsage = usage,
            BufferSharingMode = VkSharingMode.Exclusive,
            AllocPreference = memory,
            AllocRequiredProperties = host ? VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit : VkMemoryPropertyFlags.DeviceLocalBit,
        });
    }

    /// A host-visible buffer holding these bytes, for copying from.
    public static BufferEx Staging(ReadOnlySpan<byte> data, string name)
    {
        var staging = Create(data.Length, VkBufferUsageFlags.TransferSrcBit, MemoryPreference.PreferCpu, name);
        using var mapped = staging.Map();
        data.CopyTo(mapped.AsSpan());
        return staging;
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
