using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Bakes trees into octahedral impostors at load time (after SmartImposter's, MIT; impostor.glsl draws them): each tree drawn
/// from Frames x Frames directions over the upper hemisphere, every frame framed on its bounding sphere, into two atlases: its
/// colour with its coverage, and its normal with its depth.
///
/// Drawn at twice the size and taken down two by two here, so each texel holds how much of it the tree covers and the average of
/// the normals it covered, left short where they disagree (impostor.frag's normal spread). The colour and normal of the texels it
/// doesn't cover are filled in from their neighbours, so filtering and mips at the silhouette don't darken it; their depth stays
/// empty, so the march passes through them.
public sealed unsafe class ImpostorBaker : IDisposable
{
    /// impostor.glsl's Frames and FramePixels.
    public const int Frames = 12;
    public const int FrameSize = 96;
    private const int Super = 2;
    private const int Drawn = Frames * FrameSize * Super;
    private const int Size = Frames * FrameSize;

    /// A piece of the tree: a range of the mesh's indices, its textures, and where its cut-out comes from (plant.frag's shape.w).
    public readonly record struct Part(int FirstIndex, int IndexCount, TextureBinding Textures, float Cutout);

    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4 Right, Up, View, Centre, Cut;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;
    private const VkFormat ColorFormat = VkFormat.R8G8B8A8SRGB;
    private const VkFormat NormalFormat = VkFormat.R8G8B8A8UNorm;

    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _colorPipeline;
    private readonly VkPipeline _normalPipeline;
    private readonly ImageEx _color;
    private readonly ImageViewEx _colorView;
    private readonly ImageEx _normal;
    private readonly ImageViewEx _normalView;
    private readonly ImageEx _depth;
    private readonly ImageViewEx _depthView;
    private readonly BufferEx _readback;

    public ImpostorBaker()
    {
        _layout = Pipelines.Layout(sizeof(Push), TextureBinding.SetLayoutFor(4));
        using var vert = Shader.Load("impostor_bake.vert");
        using var frag = Shader.Load("impostor_bake.frag");
        PipelineDesc Desc(VkFormat format) => new()
        {
            Vertex = vert, Fragment = frag, Layout = _layout, Attributes = Mesh.VertexLayout, Stride = Marshal.SizeOf<Vertex>(), Cull = VkCullModeFlags.None,
            SingleSample = true, ColorFormat = format,
        };
        _colorPipeline = Pipelines.Create(Desc(ColorFormat));
        _normalPipeline = Pipelines.Create(Desc(NormalFormat));

        (_color, _colorView) = Image(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit, VkImageAspectFlags.ColorBit, "impostor.bake.color");
        (_normal, _normalView) = Image(NormalFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit, VkImageAspectFlags.ColorBit, "impostor.bake.normal");
        (_depth, _depthView) = Image(RenderTarget.DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, VkImageAspectFlags.DepthBit, "impostor.bake.depth");
        _readback = Buffers.Create((long)Drawn * Drawn * 4, VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferCpu, "impostor.bake.readback");
    }

    private static (ImageEx, ImageViewEx) Image(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect, string name)
    {
        var image = Gpu.Allocator.CreateImage(new ImageEx.CreateInfo
        {
            Name = name,
            ImageType = VkImageType._2D,
            ImageFormat = format,
            ImageExtent = new VkExtent3D { Width = Drawn, Height = Drawn, Depth = 1 },
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
        return (image, image.CreateImageView(VkImageViewType._2D, Subresources.Full(aspect), Gpu.Device.VkAllocator));
    }

    // impostor.glsl's frameView and frameBasis.
    private static float3 FrameView(int i, int j)
    {
        var gx = (i + 0.5f) / Frames * 2f - 1f;
        var gy = (j + 0.5f) / Frames * 2f - 1f;
        var (px, pz) = ((gx + gy) * 0.5f, (gx - gy) * 0.5f);
        return Normalize(new float3(px, 1f - MathF.Abs(px) - MathF.Abs(pz), pz));
    }

    private static float3 Normalize(float3 v) => v / MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static float3 Cross(float3 a, float3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// The tree's two atlases: colour (sRGB) with coverage, and normal with depth. centre and radius: its bounding sphere, model space.
    public (Texture2D Color, Texture2D Normal) Bake(Mesh mesh, IReadOnlyList<Part> parts, float3 centre, float radius, string name)
    {
        var color = Draw(mesh, parts, centre, radius, normal: false);
        var normal = Draw(mesh, parts, centre, radius, normal: true);
        var (colorPixels, normalPixels) = Reduce(color, normal);
        return (Texture2D.FromPixels(Size, Size, colorPixels, repeat: false, mipmaps: true, srgb: true, name: $"impostor.{name}.color"),
            Texture2D.FromPixels(Size, Size, normalPixels, repeat: false, mipmaps: true, name: $"impostor.{name}.normal"));
    }

    /// Every frame of one of the two pictures, read back.
    private byte[] Draw(Mesh mesh, IReadOnlyList<Part> parts, float3 centre, float radius, bool normal)
    {
        var (image, view, pipeline) = normal ? (_normal, _normalView, _normalPipeline) : (_color, _colorView, _colorPipeline);
        Gpu.Submit(cmd =>
        {
            var colorRange = Subresources.Full(VkImageAspectFlags.ColorBit);
            Gpu.Barrier(cmd, image.VkImage, colorRange, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.None, VkAccessFlags.ColorAttachmentWriteBit);
            Gpu.Barrier(cmd, _depth.VkImage, Subresources.Full(VkImageAspectFlags.DepthBit), VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.EarlyFragmentTestsBit, VkAccessFlags.None,
                VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);

            var colorClear = new VkClearValue();
            colorClear.Color.Float32 = new float4(0f, 0f, 0f, 0f);
            var depthClear = new VkClearValue();
            depthClear.DepthStencil.Depth = 0f;
            var colorAttachment = new VkRenderingAttachmentInfo
            {
                ImageView = view.VkImageView, ImageLayout = VkImageLayout.ColorAttachmentOptimal, LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
                ClearValue = colorClear,
            };
            var depthAttachment = new VkRenderingAttachmentInfo
            {
                ImageView = _depthView.VkImageView, ImageLayout = VkImageLayout.DepthAttachmentOptimal, LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare, ClearValue = depthClear,
            };
            var info = new VkRenderingInfo
            {
                RenderArea = new VkRect2D(Drawn, Drawn), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &colorAttachment, DepthAttachment = &depthAttachment,
            };
            cmd.BeginRendering(in info);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);

            const int cell = FrameSize * Super;
            for (var j = 0; j < Frames; j++)
            {
                for (var i = 0; i < Frames; i++)
                {
                    // Flipped, as Pipelines.SetViewport, so +Y is up.
                    cmd.SetViewport(0, [new VkViewport { X = i * cell, Y = (j + 1) * cell, Width = cell, Height = -cell, MinDepth = 0f, MaxDepth = 1f }]);
                    cmd.SetScissor(0, [new VkRect2D { Offset = new VkOffset2D { X = i * cell, Y = j * cell }, Extent = new VkExtent2D(cell, cell) }]);
                    var toward = FrameView(i, j);
                    var right = Normalize(Cross(new float3(0f, 1f, 0f), toward));
                    var up = Cross(toward, right);
                    foreach (var part in parts)
                    {
                        part.Textures.Bind(cmd, _layout);
                        var push = new Push
                        {
                            Right = new float4(right, 0f), Up = new float4(up, 0f), View = new float4(toward, radius), Centre = new float4(centre, normal ? 1f : 0f),
                            Cut = new float4(part.Cutout, 0f, 0f, 0f),
                        };
                        cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
                        mesh.DrawRange(cmd, part.FirstIndex, part.IndexCount);
                    }
                }
            }

            cmd.EndRendering();
            Gpu.Barrier(cmd, image.VkImage, colorRange, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit);
            var region = new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = Drawn, Height = Drawn, Depth = 1 },
            };
            cmd.CopyImageToBuffer(image.VkImage, VkImageLayout.TransferSrcOptimal, _readback.VkBuffer, [region]);
        });

        var pixels = new byte[Drawn * Drawn * 4];
        Buffers.Invalidate(_readback);
        using (var mapped = _readback.Map())
            mapped.AsSpan()[..pixels.Length].CopyTo(pixels);
        return pixels;
    }

    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        var c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    private static byte ToSrgb(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255f), 0, 255);
    }

    /// Both pictures taken down Super x Super into the atlases' texels, and the uncovered texels' colour and normal filled in.
    private static (byte[] Color, byte[] Normal) Reduce(byte[] color, byte[] normal)
    {
        var outColor = new byte[Size * Size * 4];
        var outNormal = new byte[Size * Size * 4];
        var covered = new bool[Size * Size];
        Parallel.For(0, Size, y =>
        {
            for (var x = 0; x < Size; x++)
            {
                var (r, g, b, nx, ny, nz, depth) = (0f, 0f, 0f, 0f, 0f, 0f, 0f);
                var n = 0;
                for (var sy = 0; sy < Super; sy++)
                {
                    for (var sx = 0; sx < Super; sx++)
                    {
                        var at = ((y * Super + sy) * Drawn + x * Super + sx) * 4;
                        if (color[at + 3] < 128)
                            continue;
                        n++;
                        (r, g, b) = (r + ToLinear[color[at]], g + ToLinear[color[at + 1]], b + ToLinear[color[at + 2]]);
                        (nx, ny, nz) = (nx + normal[at] / 127.5f - 1f, ny + normal[at + 1] / 127.5f - 1f, nz + normal[at + 2] / 127.5f - 1f);
                        depth += normal[at + 3];
                    }
                }

                var o = (y * Size + x) * 4;
                if (n == 0)
                    continue;
                covered[y * Size + x] = true;
                var share = 1f / n;
                (outColor[o], outColor[o + 1], outColor[o + 2]) = (ToSrgb(r * share), ToSrgb(g * share), ToSrgb(b * share));
                outColor[o + 3] = (byte)(255 * n / (Super * Super));
                static byte Unit(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);
                (outNormal[o], outNormal[o + 1], outNormal[o + 2]) = (Unit(nx * share), Unit(ny * share), Unit(nz * share));
                outNormal[o + 3] = (byte)Math.Clamp((int)MathF.Round(depth * share), 1, 255);
            }
        });

        // The uncovered texels' colour and normal from their covered neighbours, ring by ring (their coverage and depth stay 0).
        for (var pass = 0; pass < 6; pass++)
        {
            var next = (bool[])covered.Clone();
            Parallel.For(0, Size, y =>
            {
                for (var x = 0; x < Size; x++)
                {
                    if (covered[y * Size + x])
                        continue;
                    var (c0, c1, c2, n0, n1, n2, count) = (0, 0, 0, 0, 0, 0, 0);
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (ax, ay) = (x + dx, y + dy);
                            if (ax < 0 || ay < 0 || ax >= Size || ay >= Size || !covered[ay * Size + ax])
                                continue;
                            var a = (ay * Size + ax) * 4;
                            (c0, c1, c2) = (c0 + outColor[a], c1 + outColor[a + 1], c2 + outColor[a + 2]);
                            (n0, n1, n2) = (n0 + outNormal[a], n1 + outNormal[a + 1], n2 + outNormal[a + 2]);
                            count++;
                        }
                    }

                    if (count == 0)
                        continue;
                    var o = (y * Size + x) * 4;
                    (outColor[o], outColor[o + 1], outColor[o + 2]) = ((byte)(c0 / count), (byte)(c1 / count), (byte)(c2 / count));
                    (outNormal[o], outNormal[o + 1], outNormal[o + 2]) = ((byte)(n0 / count), (byte)(n1 / count), (byte)(n2 / count));
                    next[y * Size + x] = true;
                }
            });
            covered = next;
        }

        return (outColor, outNormal);
    }

    public void Dispose()
    {
        Pipelines.Destroy(_colorPipeline, default);
        Pipelines.Destroy(_normalPipeline, _layout);
        var (color, colorView, normal, normalView, depth, depthView, readback) = (_color, _colorView, _normal, _normalView, _depth, _depthView, _readback);
        Gpu.Defer(() =>
        {
            colorView.Dispose();
            color.Dispose();
            normalView.Dispose();
            normal.Dispose();
            depthView.Dispose();
            depth.Dispose();
            readback.Dispose();
        });
    }
}
