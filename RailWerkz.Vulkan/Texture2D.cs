using System.Runtime.InteropServices;
using Brutal.GliApi;
using Brutal.StbApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace RailWerkz.Vulkan;

/// An RGBA8 texture with its view and sampler: loaded from Content/Textures with stb_image, or made from pixels.
public sealed unsafe class Texture2D : IDisposable
{
    private ImageEx _image;
    private ImageViewEx _view;
    private VkSampler _sampler;

    public int Width { get; }
    public int Height { get; }
    public int MipLevels { get; }
    public VkImageView View => _view.VkImageView;
    public VkSampler Sampler => _sampler;

    private Texture2D(int width, int height, int mipLevels)
    {
        Width = width;
        Height = height;
        MipLevels = mipLevels;
    }

    /// srgb for colour maps (see FromPixels).
    public static Texture2D Load(string relativePath, bool repeat = true, bool mipmaps = true, bool srgb = false) =>
        LoadFile(ContentPath.Texture(relativePath), repeat, mipmaps, srgb);

    /// An image from anywhere on disk (PNG, JPEG, BMP, TGA: whatever stb_image reads).
    public static Texture2D LoadFile(string path, bool repeat = true, bool mipmaps = true, bool srgb = false)
    {
        using var image = Stb.Load(path, 4);
        return FromPixels(image.Width, image.Height, MemoryMarshal.AsBytes(image.AsSpan<byte4>()), repeat, mipmaps, srgb: srgb, name: Path.GetFileName(path));
    }

    /// Loads, or gives back a 1x1 fallback of the given colour (and a warning) when the file can't be read.
    public static Texture2D LoadOr(string relativePath, byte4 fallback, bool repeat = true, bool mipmaps = true)
    {
        try
        {
            return Load(relativePath, repeat, mipmaps);
        }
        catch (Exception e)
        {
            Log.Warn($"Texture {relativePath}: {e.Message}");
            return Solid(fallback);
        }
    }

    public static Texture2D Solid(byte4 color)
    {
        Span<byte> pixel = [color.X, color.Y, color.Z, color.W];
        return FromPixels(1, 1, pixel, true, false, name: "solid");
    }

    /// srgb: colour maps (albedo) are stored in sRGB and read back linear; data maps (normals, masks) are not.
    public static Texture2D FromPixels(int width, int height, ReadOnlySpan<byte> rgba, bool repeat, bool mipmaps, bool linear = true, bool srgb = false, string name = "texture")
    {
        var mips = mipmaps ? VulkanHelpers.MaxMipLevels(width, height) : 1;
        var texture = new Texture2D(width, height, mips);
        var format = srgb ? VkFormat.R8G8B8A8SRGB : VkFormat.R8G8B8A8UNorm;

        texture._image = Gpu.Allocator.CreateImage(new ImageEx.CreateInfo
        {
            Name = name,
            ImageType = VkImageType._2D,
            ImageFormat = format,
            ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
            ImageMipLevels = mips,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });

        var range = Subresources.Full(VkImageAspectFlags.ColorBit);
        texture._view = texture._image.CreateImageView(VkImageViewType._2D, range, Gpu.Device.VkAllocator);
        texture.Upload(rgba);

        var sampler = repeat ? Presets.Sampler.SamplerLinearRepeat : Presets.Sampler.SamplerLinearClamped;
        if (!linear)
        {
            sampler.MagFilter = VkFilter.Nearest;
            sampler.MinFilter = VkFilter.Nearest;
        }

        sampler.MaxLod = mips;
        if (mips > 1 && Gpu.MaxAnisotropy > 0f)
        {
            sampler.AnisotropyEnable = new Brutal.Bool32(true);
            sampler.MaxAnisotropy = Gpu.MaxAnisotropy;
        }

        texture._sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);
        return texture;
    }

    /// A block-compressed DDS from Content/Textures (BC1, BC3, BC4 or BC5, with its own mip chain), uploaded as it is.
    /// srgb for colour maps: the blocks are read back linear whatever the file says.
    /// skip: how many of its largest levels to leave out, so it takes a quarter of the memory for each (it always keeps at least a
    /// 256 pixel level, if it has one).
    public static Texture2D LoadDds(string relativePath, bool srgb, int skip = 0)
    {
        var path = ContentPath.Texture(relativePath);
        using var dds = new Gli.Texture(path);
        var format = dds.Format switch
        {
            Gli.Format.RGB_DXT1_UNorm_Block8 or Gli.Format.RGB_DXT1_SRGB_Block8 or Gli.Format.RGBA_DXT1_UNorm_Block8 or Gli.Format.RGBA_DXT1_SRGB_Block8 =>
                srgb ? VkFormat.Bc1RGBASRGBBlock : VkFormat.Bc1RGBAUNormBlock,
            Gli.Format.RGBA_DXT5_UNorm_Block16 or Gli.Format.RGBA_DXT5_SRGB_Block16 => srgb ? VkFormat.Bc3SRGBBlock : VkFormat.Bc3UNormBlock,
            Gli.Format.R_ATI1N_UNorm_Block8 => VkFormat.Bc4UNormBlock,
            Gli.Format.RG_ATI2N_UNorm_Block16 => VkFormat.Bc5UNormBlock,
            _ => throw new InvalidDataException($"{relativePath}: {dds.Format} isn't a format this reads"),
        };

        var first = 0;
        while (first < skip && first + 1 < (int)dds.Levels && Math.Min(dds.ImageExtent(first + 1).X, dds.ImageExtent(first + 1).Y) >= 256)
            first++;
        var extent = dds.ImageExtent(first);
        var mips = (int)dds.Levels - first;
        var texture = new Texture2D(extent.X, extent.Y, mips);
        texture._image = Gpu.Allocator.CreateImage(new ImageEx.CreateInfo
        {
            Name = Path.GetFileName(path),
            ImageType = VkImageType._2D,
            ImageFormat = format,
            ImageExtent = new VkExtent3D { Width = extent.X, Height = extent.Y, Depth = 1 },
            ImageMipLevels = mips,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        texture._view = texture._image.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);

        // Every level, one after another, in one staging buffer.
        var regions = new VkBufferImageCopy[mips];
        var data = new byte[Enumerable.Range(first, mips).Sum(level => dds.ImageSize(level))];
        var offset = 0;
        for (var level = 0; level < mips; level++)
        {
            var bytes = (int)dds.ImageSize(first + level);
            new ReadOnlySpan<byte>(dds.ImageData(first + level, 0, 0), bytes).CopyTo(data.AsSpan(offset));
            var size = dds.ImageExtent(first + level);
            regions[level] = new VkBufferImageCopy
            {
                BufferOffset = Brutal.ByteSize.Of(offset),
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = size.X, Height = size.Y, Depth = 1 },
            };
            offset += bytes;
        }

        using var staging = Buffers.Staging(data, "texture.staging");
        var image = texture._image.VkImage;
        Gpu.Submit(cmd =>
        {
            var all = Subresources.Mips(VkImageAspectFlags.ColorBit, 0, mips, 0);
            Gpu.Barrier(cmd, image, all, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.None, VkAccessFlags.TransferWriteBit);
            cmd.CopyBufferToImage(staging.VkBuffer, image, VkImageLayout.TransferDstOptimal, regions);
            Gpu.Barrier(cmd, image, all, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        });

        var sampler = Presets.Sampler.SamplerLinearRepeat;
        sampler.MaxLod = mips;
        if (Gpu.MaxAnisotropy > 0f)
        {
            sampler.AnisotropyEnable = new Brutal.Bool32(true);
            sampler.MaxAnisotropy = Gpu.MaxAnisotropy;
        }

        texture._sampler = Gpu.Device.CreateSampler(in sampler, Gpu.Device.VkAllocator);
        return texture;
    }

    /// Replaces every pixel (same size as it was made) and remakes the mip chain.
    public void Update(ReadOnlySpan<byte> rgba)
    {
        Gpu.WaitIdle();
        Upload(rgba);
    }

    private void Upload(ReadOnlySpan<byte> rgba)
    {
        using var staging = Buffers.Staging(rgba, "texture.staging");
        var image = _image.VkImage;
        var width = Width;
        var height = Height;
        var mips = MipLevels;

        Gpu.Submit(cmd =>
        {
            var all = Subresources.Mips(VkImageAspectFlags.ColorBit, 0, mips, 0);
            Gpu.Barrier(cmd, image, all, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.None, VkAccessFlags.TransferWriteBit);

            var region = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = 1 },
            };
            cmd.CopyBufferToImage(staging.VkBuffer, image, VkImageLayout.TransferDstOptimal, [region]);

            // Each level is blitted from the one above, which first becomes a transfer source.
            var w = width;
            var h = height;
            for (var level = 1; level < mips; level++)
            {
                var src = Subresources.Single(VkImageAspectFlags.ColorBit, level - 1, 0);
                Gpu.Barrier(cmd, image, src, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal,
                    VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.TransferReadBit);

                var nw = Math.Max(1, w / 2);
                var nh = Math.Max(1, h / 2);
                var blit = new VkImageBlit
                {
                    SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level - 1, LayerCount = 1 },
                    DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = 1 },
                };
                blit.SrcOffsets[1] = new VkOffset3D { X = w, Y = h, Z = 1 };
                blit.DstOffsets[1] = new VkOffset3D { X = nw, Y = nh, Z = 1 };
                cmd.BlitImage(image, VkImageLayout.TransferSrcOptimal, image, VkImageLayout.TransferDstOptimal, [blit], VkFilter.Linear);

                Gpu.Barrier(cmd, image, src, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.TransferReadBit, VkAccessFlags.ShaderReadBit);
                w = nw;
                h = nh;
            }

            var last = Subresources.Single(VkImageAspectFlags.ColorBit, mips - 1, 0);
            Gpu.Barrier(cmd, image, last, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        });
    }

    public void Dispose()
    {
        if (_image.IsNull())
            return;

        var image = _image;
        var view = _view;
        var sampler = _sampler;
        _image = default;
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroySampler(sampler, Gpu.Device.VkAllocator);
            view.Dispose();
            image.Dispose();
        });
    }
}
