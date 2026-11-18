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

    /// With the world drawn smaller than the screen (temporal upscaling), textures are read as sharp as they'd be at full size:
    /// the GPU picks mips for the smaller image, so they're biased by log2 of its share (-0.42 at 75%). Temporal anti-aliasing
    /// settles the extra detail. Read when a texture's made (a change of Render resolution applies to textures loaded after).
    public static float UpscaleLodBias
    {
        get
        {
            var s = RailWerkz.Core.Settings.Settings.Current;
            return s.TemporalAA ? MathF.Log2(Math.Clamp(s.RenderScale, 0.5f, 1f)) : 0f;
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
        if (mips > 1)
            sampler.MipLodBias = UpscaleLodBias;
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

        // Every level, one after another, in one staging buffer.
        var levels = new (int Offset, int Width, int Height)[mips];
        var data = new byte[Enumerable.Range(first, mips).Sum(level => dds.ImageSize(level))];
        var offset = 0;
        for (var level = 0; level < mips; level++)
        {
            var bytes = (int)dds.ImageSize(first + level);
            new ReadOnlySpan<byte>(dds.ImageData(first + level, 0, 0), bytes).CopyTo(data.AsSpan(offset));
            var size = dds.ImageExtent(first + level);
            levels[level] = (offset, size.X, size.Y);
            offset += bytes;
        }

        return FromBlocks(Path.GetFileName(path), format, extent.X, extent.Y, data, levels);
    }

    /// A block-compressed KTX2 from Content/Textures (BC1, BC3, BC4, BC5 or BC7, each level as it is or Zstandard-compressed),
    /// uploaded as it is. srgb for colour maps: the blocks are read back linear whatever the file says.
    public static Texture2D LoadKtx2(string relativePath, bool srgb)
    {
        var path = ContentPath.Texture(relativePath);
        var file = File.ReadAllBytes(path);
        ReadOnlySpan<byte> identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        if (file.Length < 80 || !file.AsSpan(0, 12).SequenceEqual(identifier))
            throw new InvalidDataException($"{relativePath} isn't a KTX2 file");

        uint U32(int at) => BitConverter.ToUInt32(file, at);
        var vkFormat = U32(12);
        var (width, height) = ((int)U32(20), (int)U32(24));
        var mips = Math.Max((int)U32(40), 1);
        var supercompression = U32(44);
        var format = vkFormat switch
        {
            131 or 132 or 133 or 134 => srgb ? VkFormat.Bc1RGBASRGBBlock : VkFormat.Bc1RGBAUNormBlock,
            137 or 138 => srgb ? VkFormat.Bc3SRGBBlock : VkFormat.Bc3UNormBlock,
            139 => VkFormat.Bc4UNormBlock,
            141 => VkFormat.Bc5UNormBlock,
            145 or 146 => srgb ? VkFormat.Bc7SRGBBlock : VkFormat.Bc7UNormBlock,
            _ => throw new InvalidDataException($"{relativePath}: VkFormat {vkFormat} isn't a format this reads"),
        };
        if (supercompression is not (0 or 2))
            throw new InvalidDataException($"{relativePath}: supercompression {supercompression} isn't one this reads");

        // The level index: biggest first, each its place in the file, its length there, and its length unpacked.
        var pieces = new byte[mips][];
        using var zstd = supercompression == 2 ? new ZstdSharp.Decompressor() : null;
        for (var level = 0; level < mips; level++)
        {
            var at = 80 + level * 24;
            var (start, length, unpacked) = ((int)BitConverter.ToUInt64(file, at), (int)BitConverter.ToUInt64(file, at + 8), (int)BitConverter.ToUInt64(file, at + 16));
            var stored = file.AsSpan(start, length);
            pieces[level] = zstd is null ? stored.ToArray() : zstd.Unwrap(stored, unpacked).ToArray();
        }

        var levels = new (int Offset, int Width, int Height)[mips];
        var data = new byte[pieces.Sum(p => p.Length)];
        var offset = 0;
        for (var level = 0; level < mips; level++)
        {
            pieces[level].CopyTo(data, offset);
            levels[level] = (offset, Math.Max(width >> level, 1), Math.Max(height >> level, 1));
            offset += pieces[level].Length;
        }

        return FromBlocks(Path.GetFileName(path), format, width, height, data, levels);
    }

    /// A texture of compressed blocks, every level in data one after another.
    private static Texture2D FromBlocks(string name, VkFormat format, int width, int height, byte[] data, (int Offset, int Width, int Height)[] levels)
    {
        var mips = levels.Length;
        var texture = new Texture2D(width, height, mips);
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
            ImageUsage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            ImageInitialLayout = VkImageLayout.Undefined,
            AllocPreference = MemoryPreference.PreferGpu,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
        });
        texture._view = texture._image.CreateImageView(VkImageViewType._2D, Subresources.Full(VkImageAspectFlags.ColorBit), Gpu.Device.VkAllocator);

        var regions = new VkBufferImageCopy[mips];
        for (var level = 0; level < mips; level++)
        {
            regions[level] = new VkBufferImageCopy
            {
                BufferOffset = Brutal.ByteSize.Of(levels[level].Offset),
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = levels[level].Width, Height = levels[level].Height, Depth = 1 },
            };
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
        sampler.MipLodBias = mips > 1 ? UpscaleLodBias : 0f;
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
