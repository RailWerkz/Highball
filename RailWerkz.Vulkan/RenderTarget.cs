using Brutal;

namespace RailWerkz.Vulkan;

/// An offscreen colour image and depth buffer the world renders into with dynamic rendering, multisampled, then resolved
/// to plain images read as textures (colour by the tonemap pass, depth by passes after the world). Depth is reverse-Z: cleared
/// to 0, nearer is greater.
public sealed unsafe class RenderTarget : IDisposable
{
    public const VkFormat DepthFormat = VkFormat.D32SFloat;

    /// Samples per pixel: the setting's, or the most under it the GPU can do for both colour and depth.
    public static VkSampleCountFlags Samples
    {
        get
        {
            var wanted = RailWerkz.Core.Settings.Settings.Current.Msaa;
            foreach (var (count, flag) in Counts)
                if (count <= wanted && Supported(count))
                    return flag;
            return VkSampleCountFlags._1Bit;
        }
    }

    private static readonly (int Count, VkSampleCountFlags Flag)[] Counts =
        [(8, VkSampleCountFlags._8Bit), (4, VkSampleCountFlags._4Bit), (2, VkSampleCountFlags._2Bit), (1, VkSampleCountFlags._1Bit)];

    /// Whether the GPU can do this many samples per pixel for both colour and depth.
    public static bool Supported(int count)
    {
        var limits = Gpu.PhysicalDevice.GetProperties().Limits;
        var flag = Counts.First(c => c.Count == count).Flag;
        return (limits.FramebufferColorSampleCounts & limits.FramebufferDepthSampleCounts & flag) != 0;
    }

    private VkSampleCountFlags _builtSamples;

    private ImageEx _samplesColor;
    private ImageViewEx _samplesColorView;
    private ImageEx _samplesDepth;
    private ImageViewEx _samplesDepthView;
    private ImageEx _color;
    private ImageEx _depth;
    private ImageViewEx _colorView;
    private ImageViewEx _depthView;

    public VkFormat ColorFormat { get; }
    public int2 Size { get; private set; }
    public VkImageView ColorView => _colorView.VkImageView;
    public VkSampler Sampler { get; }

    /// One sample per pixel, readable after End.
    public VkImageView DepthView => _depthView.VkImageView;
    public VkSampler DepthSampler { get; }

    /// Goes up whenever the images are remade, so whatever holds their views knows to refresh.
    public int Version { get; private set; }

    public RenderTarget(VkFormat colorFormat = VkFormat.R16G16B16A16SFloat)
    {
        ColorFormat = colorFormat;
        var sampler = Presets.Sampler.SamplerLinearClamped;
        Sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);
        var depthSampler = Presets.Sampler.SamplerLinearClamped;
        depthSampler.MagFilter = VkFilter.Nearest;
        depthSampler.MinFilter = VkFilter.Nearest;
        DepthSampler = Gpu.Device.CreateSampler(in depthSampler, Gpu.Device.VkAllocator);
    }

    /// Makes the images this size if they aren't already.
    public void Resize(int2 size)
    {
        if (size == Size && !_color.IsNull() && _builtSamples == Samples)
            return;

        Release();
        Size = size;
        _color = Image(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, "world.color", VkSampleCountFlags._1Bit);
        _builtSamples = Samples;
        if (Multisampled)
        {
            _samplesColor = Image(ColorFormat, VkImageUsageFlags.ColorAttachmentBit, "world.color.samples", _builtSamples);
            _samplesColorView = _samplesColor.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);
            _samplesDepth = Image(DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, "world.depth.samples", _builtSamples);
            _samplesDepthView = _samplesDepth.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.DepthBit), Gpu.Device.VkAllocator);
        }

        _depth = Image(DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit, "world.depth", VkSampleCountFlags._1Bit);
        _colorView = _color.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);
        _depthView = _depth.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.DepthBit), Gpu.Device.VkAllocator);
        Version++;
    }

    private bool Multisampled => _builtSamples != VkSampleCountFlags._1Bit;

    private ImageEx Image(VkFormat format, VkImageUsageFlags usage, string name, VkSampleCountFlags samples) => Gpu.Device.CreateImage(new ImageEx.CreateInfo
    {
        Name = name,
        ImageType = VkImageType._2D,
        ImageFormat = format,
        ImageExtent = new VkExtent3D { Width = Size.X, Height = Size.Y, Depth = 1 },
        ImageMipLevels = 1,
        ImageArrayLayers = 1,
        ImageSamples = samples,
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
        if (Multisampled)
            Gpu.Barrier(cmd, _samplesColor.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ColorAttachmentWriteBit);
        Gpu.Barrier(cmd, _depth.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.ShaderReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ColorAttachmentWriteBit);
        if (Multisampled)
            Gpu.Barrier(cmd, _samplesDepth.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags.LateFragmentTestsBit, VkPipelineStageFlags.EarlyFragmentTestsBit, VkAccessFlags.DepthStencilAttachmentWriteBit,
                VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);

        var colorClear = new VkClearValue();
        colorClear.Color.Float32 = clear;
        var depthClear = new VkClearValue();
        depthClear.DepthStencil.Depth = 0f;

        var color = Multisampled
            ? new VkRenderingAttachmentInfo
            {
                ImageView = _samplesColorView.VkImageView,
                ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                ResolveMode = VkResolveModeFlags.AverageBit,
                ResolveImageView = _colorView.VkImageView,
                ResolveImageLayout = VkImageLayout.ColorAttachmentOptimal,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare,
                ClearValue = colorClear,
            }
            : new VkRenderingAttachmentInfo
            {
                ImageView = _colorView.VkImageView,
                ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.Store,
                ClearValue = colorClear,
            };
        var depth = Multisampled
            ? new VkRenderingAttachmentInfo
            {
                ImageView = _samplesDepthView.VkImageView,
                ImageLayout = VkImageLayout.DepthAttachmentOptimal,
                ResolveMode = VkResolveModeFlags.SampleZeroBit,
                ResolveImageView = _depthView.VkImageView,
                ResolveImageLayout = VkImageLayout.DepthAttachmentOptimal,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare,
                ClearValue = depthClear,
            }
            : new VkRenderingAttachmentInfo
            {
                ImageView = _depthView.VkImageView,
                ImageLayout = VkImageLayout.DepthAttachmentOptimal,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.Store,
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

    /// Ends rendering and leaves both one-sample images ready to be sampled.
    public void End(CommandBuffer cmd)
    {
        EndOverlay(cmd);
        Gpu.Barrier(cmd, _depth.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthStencilReadOnlyOptimal,
            VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
    }

    /// After End: starts drawing over the resolved colour image, keeping what's there, with depth left to be read.
    public void BeginOverlay(CommandBuffer cmd)
    {
        Gpu.Barrier(cmd, _color.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit);

        var color = new VkRenderingAttachmentInfo
        {
            ImageView = _colorView.VkImageView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Load,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var info = new VkRenderingInfo
        {
            RenderArea = new VkRect2D(Size.X, Size.Y),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
        };
        cmd.BeginRendering(in info);
        Pipelines.SetViewport(cmd, Size);
    }

    /// Ends rendering and leaves the colour image ready to be sampled.
    public void EndOverlay(CommandBuffer cmd)
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
        var (samplesColor, samplesColorView, multisampled) = (_samplesColor, _samplesColorView, Multisampled);
        var (samplesDepth, samplesDepthView) = (_samplesDepth, _samplesDepthView);
        _color = default;
        Gpu.Defer(() =>
        {
            if (multisampled)
            {
                samplesColorView.Dispose();
                samplesColor.Dispose();
                samplesDepthView.Dispose();
                samplesDepth.Dispose();
            }

            colorView.Dispose();
            depthView.Dispose();
            color.Dispose();
            depth.Dispose();
        });
    }

    public void Dispose()
    {
        Release();
        var (sampler, depthSampler) = (Sampler, DepthSampler);
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroySampler(sampler, Gpu.Device.VkAllocator);
            Gpu.Device.DestroySampler(depthSampler, Gpu.Device.VkAllocator);
        });
    }
}
