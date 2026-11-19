using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Conservative Morphological Anti-Aliasing (Intel's CMAA2, in Kitten Space Agency's Vulkan port: Content/Shaders/Post/Cmaa2, under
/// Intel's Apache 2.0 licence there). It works on the finished, tonemapped picture: it finds the edges that stand out against what's
/// round them, traces the shapes they make, and blends along them, leaving everything else as it was. So it smooths the jagged edges
/// MSAA leaves (and the ones it can't reach, a cut-out leaf's) without blurring texture, and needs nothing from past frames.
///
/// The tonemap draws into Picture (8-bit, display encoded) instead of the screen; Run smooths it in place; Present lays it on the
/// screen, inside the present pass, before the interface.
public sealed unsafe class Cmaa2 : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public uint Width, Height, CandidateCapacity, BlendLocationCapacity, BlendItemCapacity;
        public float Gamma, InverseGamma;
    }

    /// The display encoding the tonemap writes (sRGB, near enough): blending's done in linear colour, decoded with it.
    private const float DisplayGamma = 2.2f;

    /// cmaa2_common.glsl's kernel sizes: a 16 x 16 group of 2 x 2 quads finds the edges of the 14 x 14 inside its ring.
    private const int EdgeQuads = 14;

    private const VkDescriptorType Sampled = VkDescriptorType.CombinedImageSampler, Image = VkDescriptorType.StorageImage, Storage = VkDescriptorType.StorageBuffer;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _edges, _arguments, _candidates, _apply;

    private readonly VkDescriptorSetLayout _presentSetLayout;
    private readonly VkPipelineLayout _presentLayout;
    private readonly VkPipeline _present;

    /// The append counters (elements 4, 8, 12; 3 the next indirect pass's count) and the indirect dispatch the passes build.
    private readonly BufferEx _control;
    private readonly BufferEx _indirect;

    private BufferEx? _shapeCandidates, _blendLocations, _blendItems;
    private StorageImage? _edgesImage, _heads;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set, _presentSet;
    private int2 _size;
    private Push _push;

    /// What the tonemap draws into, and Run smooths in place: 8-bit, display encoded.
    public StorageImage? Picture { get; private set; }

    public Cmaa2()
    {
        _setLayout = SetLayout(VkShaderStageFlags.ComputeBit, Sampled, Image, Image, Storage, Storage, Storage, Image, Storage, Storage);
        // (Compute's push constants: Pipelines.Layout gives them to the drawing stages.)
        var range = new VkPushConstantRange { StageFlags = VkShaderStageFlags.ComputeBit, Offset = (ByteSize32)0, Size = (ByteSize32)sizeof(Push) };
        _layout = Gpu.Device.CreatePipelineLayout([_setLayout], [range], Gpu.Device.VkAllocator);
        using (var shader = Shader.Load("Post/Cmaa2/cmaa2_edges_color.comp"))
            _edges = Pipelines.CreateCompute(shader, _layout);
        using (var shader = Shader.Load("Post/Cmaa2/cmaa2_compute_dispatch_args.comp"))
            _arguments = Pipelines.CreateCompute(shader, _layout);
        using (var shader = Shader.Load("Post/Cmaa2/cmaa2_process_candidates.comp"))
            _candidates = Pipelines.CreateCompute(shader, _layout);
        using (var shader = Shader.Load("Post/Cmaa2/cmaa2_deferred_color_apply.comp"))
            _apply = Pipelines.CreateCompute(shader, _layout);

        _presentSetLayout = SetLayout(VkShaderStageFlags.FragmentBit, Sampled);
        _presentLayout = Pipelines.Layout(0, _presentSetLayout);
        using var vertex = Shader.Load("fullscreen.vert");
        using var fragment = Shader.Load("Post/Cmaa2/cmaa2_present.frag");
        _present = Pipelines.Create(new PipelineDesc
        {
            Vertex = vertex, Fragment = fragment, Layout = _presentLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            RenderPass = Gpu.PresentPass, ColorFormat = Gpu.SwapchainFormat,
        });

        var transfer = VkBufferUsageFlags.TransferDstBit;
        _control = Buffers.Create(16 * sizeof(uint), VkBufferUsageFlags.StorageBufferBit | transfer, MemoryPreference.PreferGpu, "cmaa2.control");
        _indirect = Buffers.Create(4 * sizeof(uint), VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.IndirectBufferBit | transfer, MemoryPreference.PreferGpu, "cmaa2.indirect");
        var (control, indirect) = (_control.VkBuffer, _indirect.VkBuffer);
        Gpu.Submit(cmd =>
        {
            cmd.FillBuffer(control, new ByteSize64(0), new ByteSize64(16 * sizeof(uint)), 0);
            cmd.FillBuffer(indirect, new ByteSize64(0), new ByteSize64(4 * sizeof(uint)), 0);
        });
    }

    /// A pipeline that draws the tonemap into Picture (dynamic rendering, its format).
    public static VkFormat PictureFormat => VkFormat.R8G8B8A8UNorm;

    private static VkDescriptorSetLayout SetLayout(VkShaderStageFlags stage, params VkDescriptorType[] types)
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[types.Length];
        for (var i = 0; i < types.Length; i++)
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = types[i], DescriptorCount = 1, StageFlags = stage };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = types.Length, Bindings = bindings };
        return Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
    }

    private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    /// The picture and the working buffers at this size (Intel's: a candidate for every fourth pixel, a blend item for every
    /// second, a list entry for every sixth), made again when it changes.
    public void Resize(int2 size)
    {
        if (size == _size && Picture is not null)
            return;

        var oldPool = _pool;
        if (!oldPool.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(oldPool, Gpu.Device.VkAllocator));
        Picture?.Dispose();
        _edgesImage?.Dispose();
        _heads?.Dispose();
        // (The buffers once the GPU's done with last frame's passes, which used them.)
        var (oldCandidates, oldLocations, oldItems) = (_shapeCandidates, _blendLocations, _blendItems);
        Gpu.Defer(() =>
        {
            oldCandidates?.Dispose();
            oldLocations?.Dispose();
            oldItems?.Dispose();
        });

        _size = size;
        var (w, h) = (Math.Max(size.X, 2), Math.Max(size.Y, 2));
        Picture = new StorageImage(w, h, 1, PictureFormat, false, false, "cmaa2.picture", drawnTo: true);
        _edgesImage = new StorageImage((w + 1) / 2, h, 1, VkFormat.R8UInt, false, false, "cmaa2.edges");
        _heads = new StorageImage((w + 1) / 2, (h + 1) / 2, 1, VkFormat.R32UInt, false, false, "cmaa2.heads");

        var pixels = (long)w * h;
        var candidates = (uint)(pixels / 4);
        var items = (uint)(pixels / 2);
        var locations = (uint)((pixels + 3) / 6);
        var storage = VkBufferUsageFlags.StorageBufferBit;
        _shapeCandidates = Buffers.Create(candidates * sizeof(uint), storage, MemoryPreference.PreferGpu, "cmaa2.candidates");
        _blendLocations = Buffers.Create(locations * sizeof(uint), storage, MemoryPreference.PreferGpu, "cmaa2.locations");
        _blendItems = Buffers.Create(items * 2L * sizeof(uint), storage, MemoryPreference.PreferGpu, "cmaa2.items");
        _push = new Push
        {
            Width = (uint)w, Height = (uint)h, CandidateCapacity = candidates, BlendLocationCapacity = locations, BlendItemCapacity = items,
            Gamma = DisplayGamma, InverseGamma = 1f / DisplayGamma,
        };

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = Sampled, DescriptorCount = 2 };
        sizes[1] = new VkDescriptorPoolSize { Type = Image, DescriptorCount = 3 };
        sizes[2] = new VkDescriptorPoolSize { Type = Storage, DescriptorCount = 5 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 2, PoolSizeCount = 3, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        _set = Allocate(_setLayout);
        _presentSet = Allocate(_presentSetLayout);

        var read = new VkDescriptorImageInfo { Sampler = Picture.Sampler, ImageView = Picture.View, ImageLayout = VkImageLayout.General };
        var write = new VkDescriptorImageInfo { ImageView = Picture.View, ImageLayout = VkImageLayout.General };
        var edges = new VkDescriptorImageInfo { ImageView = _edgesImage.View, ImageLayout = VkImageLayout.General };
        var heads = new VkDescriptorImageInfo { ImageView = _heads.View, ImageLayout = VkImageLayout.General };
        var candidateList = Whole(_shapeCandidates.Value, candidates * sizeof(uint));
        var locationList = Whole(_blendLocations.Value, locations * sizeof(uint));
        var itemList = Whole(_blendItems.Value, items * 2L * sizeof(uint));
        var control = Whole(_control, 16 * sizeof(uint));
        var indirect = Whole(_indirect, 4 * sizeof(uint));
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = Sampled, ImageInfo = &read },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 1, DescriptorCount = 1, DescriptorType = Image, ImageInfo = &write },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 2, DescriptorCount = 1, DescriptorType = Image, ImageInfo = &edges },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 3, DescriptorCount = 1, DescriptorType = Storage, BufferInfo = &candidateList },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 4, DescriptorCount = 1, DescriptorType = Storage, BufferInfo = &locationList },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 5, DescriptorCount = 1, DescriptorType = Storage, BufferInfo = &itemList },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 6, DescriptorCount = 1, DescriptorType = Image, ImageInfo = &heads },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 7, DescriptorCount = 1, DescriptorType = Storage, BufferInfo = &control },
            new VkWriteDescriptorSet { DstSet = _set, DstBinding = 8, DescriptorCount = 1, DescriptorType = Storage, BufferInfo = &indirect },
            new VkWriteDescriptorSet { DstSet = _presentSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = Sampled, ImageInfo = &read },
        ], []);
    }

    private static VkDescriptorBufferInfo Whole(BufferEx buffer, long bytes) =>
        new() { Buffer = buffer.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of((int)bytes) };

    /// Before the tonemap draws into Picture: what read it (last frame's Present) and wrote it (last frame's Run) are done.
    public void BeforeDrawing(CommandBuffer cmd)
    {
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkDependencyFlags.None, [barrier], [], []);
    }

    /// Smooths Picture in place, once the tonemap's drawn it. Outside any render pass. Five passes: the edges (and the pixels where
    /// two meet, candidates for shapes), the next pass's dispatch, the shapes traced and their blends queued, the next dispatch (and
    /// the counters cleared for the next frame), and the queued blends applied.
    public void Run(CommandBuffer cmd)
    {
        if (Picture is null)
            return;

        var drawn = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [drawn], [], []);

        var push = _push;
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_set], []);
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _edges);
        cmd.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        var quadSpan = EdgeQuads * 2;
        cmd.Dispatch((int)((_push.Width + quadSpan - 1) / quadSpan), (int)((_push.Height + quadSpan - 1) / quadSpan), 1);
        Between(cmd);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _arguments);
        cmd.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.Dispatch(2, 1, 1);
        ToIndirect(cmd);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _candidates);
        cmd.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.DispatchIndirect(_indirect.VkBuffer, new ByteSize64(0));
        Between(cmd);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _arguments);
        cmd.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.Dispatch(1, 2, 1);
        ToIndirect(cmd);

        cmd.BindPipeline(VkPipelineBindPoint.Compute, _apply);
        cmd.PushConstants(_layout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.DispatchIndirect(_indirect.VkBuffer, new ByteSize64(0));

        var done = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, [done], [], []);
    }

    private static void Between(CommandBuffer cmd)
    {
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [barrier], [], []);
    }

    /// After a pass that wrote the next one's dispatch: it's read as an indirect command, and by the shaders.
    private static void ToIndirect(CommandBuffer cmd)
    {
        var barrier = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ShaderWriteBit,
            DstAccessMask = VkAccessFlags.IndirectCommandReadBit | VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.DrawIndirectBit | VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, [barrier], [], []);
    }

    /// Lays the smoothed picture over the screen. Inside the present pass, before the interface.
    public void Present(CommandBuffer cmd)
    {
        if (Picture is null)
            return;
        var size = Gpu.Extent;
        cmd.SetViewport(0, [new VkViewport { X = 0f, Y = 0f, Width = size.X, Height = size.Y, MinDepth = 0f, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D(size.X, size.Y)]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _present);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _presentLayout, 0, [_presentSet], []);
        cmd.Draw(3, 1, 0, 0);
    }

    public void Dispose()
    {
        var pool = _pool;
        Picture?.Dispose();
        _edgesImage?.Dispose();
        _heads?.Dispose();
        var buffers = (_shapeCandidates, _blendLocations, _blendItems, _control, _indirect);
        foreach (var pipeline in (ReadOnlySpan<VkPipeline>)[_edges, _arguments, _candidates, _apply, _present])
            Pipelines.Destroy(pipeline, default);
        var (layout, setLayout, presentLayout, presentSetLayout) = (_layout, _setLayout, _presentLayout, _presentSetLayout);
        Gpu.Defer(() =>
        {
            buffers.Item1?.Dispose();
            buffers.Item2?.Dispose();
            buffers.Item3?.Dispose();
            buffers.Item4.Dispose();
            buffers.Item5.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(presentLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(presentSetLayout, Gpu.Device.VkAllocator);
        });
    }
}
