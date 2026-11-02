using Brutal;

namespace RailWerkz.Vulkan;

/// An offscreen colour image and depth buffer the world renders into with dynamic rendering, then read as a texture
/// (by the tonemap pass). Depth is reverse-Z: cleared to 0, nearer is greater.
public sealed unsafe class RenderTarget : IDisposable
{
    public const VkFormat DepthFormat = VkFormat.D32SFloat;

    private ImageEx _color;
    private ImageEx _depth;
    private ImageViewEx _colorView;
    private ImageViewEx _depthView;

    public VkFormat ColorFormat { get; }
    public int2 Size { get; private set; }
    public VkImageView ColorView => _colorView.VkImageView;
    public VkSampler Sampler { get; }

    /// Goes up whenever the images are remade, so whatever holds their views knows to refresh.
    public int Version { get; private set; }

    public RenderTarget(VkFormat colorFormat = VkFormat.R16G16B16A16SFloat)
    {
        ColorFormat = colorFormat;
        var sampler = Presets.Sampler.SamplerLinearClamped;
        Sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);
    }

    /// Makes the images this size if they aren't already.
    public void Resize(int2 size)
    {
        if (size == Size && !_color.IsNull())
            return;

        Release();
        Size = size;
        _color = Image(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, "world.color");
        _depth = Image(DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, "world.depth");
        _colorView = _color.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);
        _depthView = _depth.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.DepthBit), Gpu.Device.VkAllocator);
        Version++;
    }

    private ImageEx Image(VkFormat format, VkImageUsageFlags usage, string name) => Gpu.Device.CreateImage(new ImageEx.CreateInfo
    {
        Name = name,
        ImageType = VkImageType._2D,
        ImageFormat = format,
        ImageExtent = new VkExtent3D { Width = Size.X, Height = Size.Y, Depth = 1 },
        ImageMipLevels = 1,
        ImageArrayLayers = 1,
        ImageSamples = VkSampleCountFlags._1Bit,
        ImageTiling = VkImageTiling.Optimal,
        ImageUsage = usage,
        ImageSharingMode = VkSharingMode.Exclusive,
        ImageInitialLayout = VkImageLayout.Undefined,
        AllocPreference = MemoryPreference.PreferGpu,
        AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
    });

    /// Clears both images and starts rendering into them, with the viewport and scissor covering the target.
    public void Begin(CommandBuffer cmd, float4 clear)
    {
        Gpu.Barrier(cmd, _color.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.ShaderReadBit, VkAccessFlags.ColorAttachmentWriteBit);
        Gpu.Barrier(cmd, _depth.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.LateFragmentTestsBit, VkPipelineStageFlags.EarlyFragmentTestsBit, VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);

        var colorClear = new VkClearValue();
        colorClear.Color.Float32 = clear;
        var depthClear = new VkClearValue();
        depthClear.DepthStencil.Depth = 0f;

        var color = new VkRenderingAttachmentInfo
        {
            ImageView = _colorView.VkImageView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = colorClear,
        };
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _depthView.VkImageView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = depthClear,
        };
        var info = new VkRenderingInfo
        {
            RenderArea = new VkRect2D(Size.X, Size.Y),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
            DepthAttachment = &depth,
        };
        cmd.BeginRendering(in info);
        Pipelines.SetViewport(cmd, Size);
    }

    /// Ends rendering and leaves the colour image ready to be sampled.
    public void End(CommandBuffer cmd)
    {
        cmd.EndRendering();
        Gpu.Barrier(cmd, _color.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
    }

    private void Release()
    {
        if (_color.IsNull())
            return;

        var (color, depth, colorView, depthView) = (_color, _depth, _colorView, _depthView);
        _color = default;
        Gpu.Defer(() =>
        {
            colorView.Dispose();
            depthView.Dispose();
            color.Dispose();
            depth.Dispose();
        });
    }

    public void Dispose()
    {
        Release();
        var sampler = Sampler;
        Gpu.Defer(() => Gpu.Device.DestroySampler(sampler, Gpu.Device.VkAllocator));
    }
}
