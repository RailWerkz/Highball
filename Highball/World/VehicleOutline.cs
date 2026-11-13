using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// What to outline this frame: a vehicle's outline shape (VehicleOverlay.OutlineMesh), how it's turned and where it stands,
/// and whether it's the driven one (else the picked one).
public readonly record struct Outlined(Mesh Shape, float4 Rotation, double3 At, bool Driven);

/// The glowing line round the driven and picked vehicles, on the GPU: their silhouettes drawn flat into a two-channel mask at
/// half the screen's size (so nothing under a couple of pixels across makes a line of its own), then, over the finished picture and only across their part of the screen, a line, a softer line and a faint
/// halo wherever the mask changes nearby (outline.frag).
public sealed unsafe class VehicleOutline : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MaskPush
    {
        public float4x4 ViewProjection;
        public float4 Channel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlowPush
    {
        public float4 Driven;
        public float4 Picked;
        public float4 Params;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly VkPipelineLayout _maskLayout;
    private readonly VkPipeline _mask;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _glowLayout;
    private readonly VkPipeline _glow;
    private readonly FrameRing<ModelInstance> _instances = new(VkBufferUsageFlags.VertexBufferBit, "outline.instances");
    private readonly ModelInstance[] _one = new ModelInstance[1];
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private StorageImage? _image;
    private int2 _size;

    /// Where on the screen the outlines are this frame, in pixels (empty for none).
    private (int2 Min, int2 Max) _area;

    /// The colours, from the interface's palette.
    public float4 DrivenColor { get; set; } = new(0.3f, 0.85f, 0.5f, 1f);
    public float4 PickedColor { get; set; } = new(0.35f, 0.6f, 1f, 1f);

    /// Pixels per interface unit (Style.S(1)), so the line is as wide as the rest of the interface's.
    public float Scale { get; set; } = 1f;

    public VehicleOutline()
    {
        _maskLayout = Pipelines.Layout(Marshal.SizeOf<MaskPush>());
        using var maskVert = Shader.Load("outline_mask.vert");
        using var maskFrag = Shader.Load("outline_mask.frag");
        _mask = Pipelines.Create(new PipelineDesc
        {
            Vertex = maskVert, Fragment = maskFrag, Layout = _maskLayout, Attributes = [Mesh.VertexLayout[0]], Stride = Marshal.SizeOf<Vertex>(),
            InstanceAttributes = ModelInstance.Layout[..2], InstanceStride = Marshal.SizeOf<ModelInstance>(),
            Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false, SingleSample = true, DepthFormat = VkFormat.Undefined,
            ColorFormat = VkFormat.R8G8UNorm, Blend = true,
        });

        var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
        _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        _glowLayout = Pipelines.Layout(Marshal.SizeOf<GlowPush>(), _setLayout);
        using var fullscreen = Shader.Load("fullscreen.vert");
        using var glowFrag = Shader.Load("outline.frag");
        _glow = Pipelines.Create(new PipelineDesc
        {
            Vertex = fullscreen, Fragment = glowFrag, Layout = _glowLayout, Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false,
            RenderPass = Gpu.PresentPass, ColorFormat = Gpu.SwapchainFormat, Blend = true,
        });
    }

    /// Draws the outlined vehicles' silhouettes into the mask. After the world, outside any render pass; the instances go
    /// into the frame's instance buffer with the rest.
    public void Record(CommandBuffer cmd, int2 size, Camera camera, float aspect, IReadOnlyList<Outlined>? outlined)
    {
        _area = default;
        if (outlined is null || outlined.Count == 0)
            return;

        Resize(new int2((size.X + 1) / 2, (size.Y + 1) / 2));

        // Their part of the screen, from their bounding spheres, with room for the halo.
        var (min, max) = (new float2(float.MaxValue), new float2(float.MinValue));
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        foreach (var o in outlined)
        {
            var (centre, radius) = Sphere(o, camera);
            var depth = centre.X * camera.Forward.X + centre.Y * camera.Forward.Y + centre.Z * camera.Forward.Z;
            if (depth - radius <= camera.Near * 2f)
            {
                (min, max) = (new float2(0f), new float2(size.X, size.Y));
                break;
            }

            var x = centre.X * camera.Right.X + centre.Y * camera.Right.Y + centre.Z * camera.Right.Z;
            var y = centre.X * camera.Up.X + centre.Y * camera.Up.Y + centre.Z * camera.Up.Z;
            var px = new float2((x / (depth * tanY * aspect) * 0.5f + 0.5f) * size.X, (0.5f - y / (depth * tanY) * 0.5f) * size.Y);
            // A sphere's image is a little larger than radius over depth when it's off to the side; the margin covers it.
            var r = radius / ((depth - radius) * tanY) * 0.5f * size.Y * 1.15f;
            min = float2.Min(min, px - new float2(r));
            max = float2.Max(max, px + new float2(r));
        }

        var margin = 8f * Scale + 2f;
        var lo = new int2(Math.Clamp((int)(min.X - margin), 0, size.X), Math.Clamp((int)(min.Y - margin), 0, size.Y));
        var hi = new int2(Math.Clamp((int)MathF.Ceiling(max.X + margin), 0, size.X), Math.Clamp((int)MathF.Ceiling(max.Y + margin), 0, size.Y));
        if (hi.X <= lo.X || hi.Y <= lo.Y)
            return;
        _area = (lo, hi);

        var image = _image!;
        Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.ShaderReadBit, VkAccessFlags.ColorAttachmentWriteBit);
        var color = new VkRenderingAttachmentInfo
        {
            ImageView = image.View,
            ImageLayout = VkImageLayout.General,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var rendering = new VkRenderingInfo { RenderArea = new VkRect2D(_size.X, _size.Y), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &color };
        cmd.BeginRendering(in rendering);
        Pipelines.SetViewport(cmd, _size);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _mask);
        var push = new MaskPush { ViewProjection = camera.ViewProjection(aspect) };
        foreach (var o in outlined)
        {
            var relative = new float3((float)(o.At.X - camera.X), (float)(o.At.Y - camera.Y), (float)(o.At.Z - camera.Z));
            _one[0] = new ModelInstance { Rotation = o.Rotation, PlaceAndScale = new float4(relative, 1f), PreviousRotation = o.Rotation, PreviousPlace = new float4(relative, 0f) };
            var (buffer, first) = _instances.Append(_one);
            push.Channel = o.Driven ? new float4(1f, 0f, 0f, 0f) : new float4(0f, 1f, 0f, 0f);
            cmd.PushConstants(_maskLayout, Stages, ByteSize.Zero, push);
            o.Shape.DrawInstanced(cmd, buffer, 1, first);
        }

        cmd.EndRendering();
        Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
    }

    /// Lays the glow over the picture, across the outlines' part of the screen only. Inside the present pass.
    public void Composite(CommandBuffer cmd, double time)
    {
        if (_area.Max.X <= _area.Min.X || _image is null)
            return;

        var extent = Gpu.Extent;
        cmd.SetViewport(0, [new VkViewport { X = 0f, Y = 0f, Width = extent.X, Height = extent.Y, MinDepth = 0f, MaxDepth = 1f }]);
        var (lo, hi) = _area;
        cmd.SetScissor(0, [new VkRect2D { Offset = new VkOffset2D { X = lo.X, Y = lo.Y }, Extent = new VkExtent2D(hi.X - lo.X, hi.Y - lo.Y) }]);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _glow);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _glowLayout, 0, [_set], []);
        var breathe = 0.85f + 0.15f * MathF.Sin((float)time * 2.2f);
        var push = new GlowPush { Driven = DrivenColor, Picked = PickedColor, Params = new float4(breathe, Scale, 1f / extent.X, 1f / extent.Y) };
        cmd.PushConstants(_glowLayout, Stages, ByteSize.Zero, push);
        cmd.Draw(3, 1, 0, 0);
        cmd.SetScissor(0, [new VkRect2D(extent.X, extent.Y)]);
    }

    /// An outlined vehicle's bounding sphere relative to the camera.
    private static (float3 Centre, float Radius) Sphere(in Outlined o, Camera camera)
    {
        var (min, max) = (o.Shape.BoundsMin, o.Shape.BoundsMax);
        var half = (max - min) * 0.5f;
        var middle = Rotate(o.Rotation, (min + max) * 0.5f);
        var at = new float3((float)(o.At.X - camera.X), (float)(o.At.Y - camera.Y), (float)(o.At.Z - camera.Z));
        return (at + middle, MathF.Sqrt(half.X * half.X + half.Y * half.Y + half.Z * half.Z));
    }

    private static float3 Rotate(float4 q, float3 v)
    {
        static float3 Cross(float3 a, float3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        var u = new float3(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2f;
        return v + t * q.W + Cross(u, t);
    }

    private void Resize(int2 size)
    {
        if (_image is not null && size == _size)
            return;

        var (oldImage, oldPool) = (_image, _pool);
        if (oldImage is not null)
            Gpu.Defer(() =>
            {
                oldImage.Dispose();
                Gpu.Device.DestroyDescriptorPool(oldPool, Gpu.Device.VkAllocator);
            });

        _size = size;
        _image = new StorageImage(Math.Max(size.X, 1), Math.Max(size.Y, 1), 1, VkFormat.R8G8UNorm, false, false, "outline.mask", drawnTo: true);
        var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &poolSize };
        _pool = Gpu.Device.CreateDescriptorPool(in poolInfo, Gpu.Device.VkAllocator);
        var layout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];
        var imageInfo = new VkDescriptorImageInfo { Sampler = _image.Sampler, ImageView = _image.View, ImageLayout = VkImageLayout.General };
        var write = new VkWriteDescriptorSet { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &imageInfo };
        Gpu.Device.UpdateDescriptorSets([write], []);
    }

    public void Dispose()
    {
        var (image, pool, setLayout) = (_image, _pool, _setLayout);
        Gpu.Defer(() =>
        {
            image?.Dispose();
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
        });
        _instances.Dispose();
        Pipelines.Destroy(_mask, _maskLayout);
        Pipelines.Destroy(_glow, _glowLayout);
    }
}
