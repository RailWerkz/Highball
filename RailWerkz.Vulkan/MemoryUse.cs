using Brutal.VulkanApi.Vma;

namespace RailWerkz.Vulkan;

/// How much memory the game's using: on the GPU (what its allocator has taken from the driver, every heap) and in RAM (the process's
/// working set). Read a few times a second at most.
public static class MemoryUse
{
    private static double _readAt = -1.0;
    private static (long Vram, long Ram) _last;

    public static (long Vram, long Ram) Now
    {
        get
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (now - _readAt < 0.5)
                return _last;
            _readAt = now;
            Gpu.Allocator.CalculateStatistics(out VmaTotalStatistics stats);
            var vram = (long)stats.Total.Statistics.BlockBytes;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return _last = (vram, process.WorkingSet64);
        }
    }
}
