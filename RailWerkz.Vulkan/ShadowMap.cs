namespace RailWerkz.Vulkan;

/// A depth texture array to render shadows into, one layer per cascade, read back with a comparison sampler. Depth is
/// reversed like the world's: cleared to 0, nearer is greater, and a point is lit if it's at least as near as what's stored.
public sealed unsafe class ShadowMap : IDisposable
{
    private readonly ImageEx _image;
    private readonly ImageViewEx _arrayView;
    private readonly ImageViewEx[] _layerViews;
    private bool _used;

    public int Size { get; }
    public int Layers { get; }
    public VkImageView View => _arrayView.VkImageView;
    public VkSampler Sampler { get; }

    public ShadowMap(int size, int layers)
    {
        Size = size;
        Layers = layers;
        _image = Gpu.Device.CreateImage(new ImageEx.CreateInfo
        {
            Name = "world.shadows",
            ImageType = VkImageType._2D,
            ImageFormat = RenderTarget.DepthFormat,
            ImageExtent = new VkExtent3D { Width = size, Height = size, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = layers,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });

        _arrayView = _image.CreateImageView(VkImageViewType._2DArray, Subresources.Full(VkImageAspectFlags.DepthBit), Gpu.Device.VkAllocator);
        _layerViews = new ImageViewEx[layers];
        for (var layer = 0; layer < layers; layer++)
            _layerViews[layer] = _image.CreateImageView(VkImageViewType._2D, Subresources.Single(VkImageAspectFlags.DepthBit, 0, layer), Gpu.Device.VkAllocator);

        var sampler = Presets.Sampler.SamplerLinearClamped;
        sampler.CompareEnable = new Brutal.Bool32(true);
        sampler.CompareOp = VkCompareOp.GreaterOrEqual;
        sampler.MaxLod = 0f;
        Sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);
    }

    /// Gets every layer ready to render into. Call once a frame, before the first BeginLayer.
    public void BeginFrame(CommandBuffer cmd)
    {
        var from = _used ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined;
        Gpu.Barrier(cmd, _image.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), from, VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.EarlyFragmentTestsBit, VkAccessFlags.ShaderReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);
        _used = true;
    }

    /// Clears one layer and starts rendering into it, with the viewport and scissor covering it.
    public void BeginLayer(CommandBuffer cmd, int layer)
    {
        var clear = new VkClearValue();
        clear.DepthStencil.Depth = 0f;
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _layerViews[layer].VkImageView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = clear,
        };
        var info = new VkRenderingInfo
        {
            RenderArea = new VkRect2D(Size, Size),
            LayerCount = 1,
            DepthAttachment = &depth,
        };
        cmd.BeginRendering(in info);
        Pipelines.SetViewport(cmd, new int2(Size, Size));
    }

    public void EndLayer(CommandBuffer cmd) => cmd.EndRendering();

    /// Leaves every layer ready to be sampled, once they've all been drawn.
    public void EndFrame(CommandBuffer cmd)
    {
        Gpu.Barrier(cmd, _image.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.DepthAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.LateFragmentTestsBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
    }

    public void Dispose()
    {
        var (image, array, layers, sampler) = (_image, _arrayView, _layerViews, Sampler);
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroySampler(sampler, Gpu.Device.VkAllocator);
            foreach (var view in layers)
                view.Dispose();
            array.Dispose();
            image.Dispose();
        });
    }
}
