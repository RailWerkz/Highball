namespace RailWerkz.Vulkan;

/// A 2D or 3D image that compute shaders write and any shader reads. It stays in the general layout for its whole life, so all
/// it needs between passes is a memory barrier.
public sealed unsafe class StorageImage : IDisposable
{
    private readonly ImageEx _image;
    private readonly ImageViewEx _view;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int MipLevels { get; }
    public VkImageView View => _view.VkImageView;
    public VkSampler Sampler { get; }

    /// depth: 1 for a 2D image. repeat: the sampler wraps rather than clamps.
    public StorageImage(int width, int height, int depth, VkFormat format, bool mipmaps, bool repeat, string name)
    {
        (Width, Height, Depth) = (width, height, depth);
        MipLevels = mipmaps ? VulkanHelpers.MaxMipLevels(width, Math.Max(height, depth)) : 1;
        var is3D = depth > 1;

        _image = Gpu.Device.CreateImage(new ImageEx.CreateInfo
        {
            Name = name,
            ImageType = is3D ? VkImageType._3D : VkImageType._2D,
            ImageFormat = format,
            ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = depth },
            ImageMipLevels = MipLevels,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        _view = _image.CreateImageView(is3D ? VkImageViewType._3D : VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);

        var sampler = repeat ? Presets.Sampler.SamplerLinearRepeat : Presets.Sampler.SamplerLinearClamped;
        sampler.MaxLod = MipLevels;
        Sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);

        var image = _image.VkImage;
        var mips = MipLevels;
        Gpu.Submit(cmd => Gpu.Barrier(cmd, image, Subresources.Mips(VkImageAspectFlags.ColorBit, 0, mips, 0), VkImageLayout.Undefined, VkImageLayout.General,
            VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.None, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit));
    }

    /// Fills the smaller mip levels from the largest, once a shader has written it.
    public void GenerateMips(CommandBuffer cmd)
    {
        var (w, h, d) = (Width, Height, Depth);
        for (var level = 1; level < MipLevels; level++)
        {
            var written = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [written], [], []);

            var (nw, nh, nd) = (Math.Max(1, w / 2), Math.Max(1, h / 2), Math.Max(1, d / 2));
            var blit = new VkImageBlit
            {
                SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level - 1, LayerCount = 1 },
                DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = 1 },
            };
            blit.SrcOffsets[1] = new VkOffset3D { X = w, Y = h, Z = d };
            blit.DstOffsets[1] = new VkOffset3D { X = nw, Y = nh, Z = nd };
            cmd.BlitImage(_image.VkImage, VkImageLayout.General, _image.VkImage, VkImageLayout.General, [blit], VkFilter.Linear);
            (w, h, d) = (nw, nh, nd);
        }

        var done = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [done], [], []);
    }

    public void Dispose()
    {
        var (image, view, sampler) = (_image, _view, Sampler);
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroySampler(sampler, Gpu.Device.VkAllocator);
            view.Dispose();
            image.Dispose();
        });
    }
}
