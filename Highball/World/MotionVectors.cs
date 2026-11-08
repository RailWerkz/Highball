using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// How the things that move moved since last frame, for temporal anti-aliasing. It knows how the camera moved, so the still
/// world reprojects by itself; a train going along with the camera would be taken for still, and each pixel of it given the
/// history of the ground it just passed over. So after the world's drawn, whatever's moving (ModelDraw.Moving) is drawn again
/// into an image of its own: at each pixel it covers, where that point of it was last frame, as metres from where it is now.
/// The temporal pass adds that to the camera's move. Zero where nothing moving is seen.
///
/// No depth buffer of its own: each fragment looks at the depth the world was drawn with, and is kept only if it's what was
/// seen there (within a whisker: the world's depth is one sample of several).
public sealed unsafe class MotionVectors : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public float4x4 ViewProjection;
        public float4 Rotation;
        public float4 PlaceAndScale;
        public float4 PreviousRotation;
        /// xyz: where it was last frame relative to the camera now; w: the camera's near distance.
        public float4 PreviousPlaceAndNear;
    }

    private const VkShaderStageFlags Stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private int2 _size;
    private int _targetVersion = -1;

    /// What the temporal pass reads: xyz the move back to last frame's place, w 1 where something moving is seen.
    public StorageImage? Image { get; private set; }

    public MotionVectors()
    {
        var binding = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
        _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        _layout = Pipelines.Layout(Marshal.SizeOf<Push>(), _setLayout);
        using var vertex = Shader.Load("motion.vert");
        using var fragment = Shader.Load("motion.frag");
        _pipeline = Pipelines.Create(new PipelineDesc
        {
            // Only the position: it's all a point's move needs.
            Vertex = vertex, Fragment = fragment, Layout = _layout, Attributes = [Mesh.VertexLayout[0]], Stride = Marshal.SizeOf<Vertex>(),
            Cull = VkCullModeFlags.None, DepthTest = false, DepthWrite = false, SingleSample = true, DepthFormat = VkFormat.Undefined,
        });
    }

    private void Resize(RenderTarget target)
    {
        if (target.Size == _size && target.Version == _targetVersion && Image is not null)
            return;

        Gpu.WaitIdle();
        Image?.Dispose();
        if (!_pool.IsNull())
            Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);

        (_size, _targetVersion) = (target.Size, target.Version);
        Image = new StorageImage(Math.Max(_size.X, 1), Math.Max(_size.Y, 1), 1, VkFormat.R16G16B16A16SFloat, false, false, "post.motion", drawnTo: true);

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        var layout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];
        var depth = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var write = new VkWriteDescriptorSet { DstSet = _set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth };
        Gpu.Device.UpdateDescriptorSets([write], []);
    }

    /// After the world's drawn (its depth ready to read), before the temporal pass. Always clears the image, so nothing stale
    /// is read once a thing stops moving.
    public void Record(CommandBuffer cmd, RenderTarget target, Camera camera, float aspect, IReadOnlyList<ModelDraw>? models)
    {
        Resize(target);
        var image = Image!;

        // The temporal pass last read it; clear it for this frame.
        Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
            VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit);
        var clear = new VkClearColorValue();
        var range = Subresources.Full(VkImageAspectFlags.ColorBit);
        cmd.ClearColorImage(image.VkImage, VkImageLayout.General, &clear, [range]);

        var moving = models?.Any(m => m.Moving) == true;
        if (moving)
        {
            Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.ColorAttachmentWriteBit);
            var color = new VkRenderingAttachmentInfo
            {
                ImageView = image.View,
                ImageLayout = VkImageLayout.General,
                LoadOp = VkAttachmentLoadOp.Load,
                StoreOp = VkAttachmentStoreOp.Store,
            };
            var info = new VkRenderingInfo { RenderArea = new VkRect2D(_size.X, _size.Y), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &color };
            cmd.BeginRendering(in info);
            Pipelines.SetViewport(cmd, _size);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_set], []);

            // As the world was drawn this frame, jitter and all, so each pixel lines up with what's under it.
            var viewProjection = camera.JitteredViewProjection(aspect);
            foreach (var draw in models!)
            {
                if (draw.PreviousAt is not { } was)
                    continue;
                var push = new Push
                {
                    ViewProjection = viewProjection,
                    Rotation = draw.Orientation,
                    PlaceAndScale = new float4(draw.RelativeTo(camera.X, camera.Y, camera.Z), draw.Scale),
                    PreviousRotation = draw.PreviousRotation ?? draw.Orientation,
                    PreviousPlaceAndNear = new float4((float)(was.X - camera.X), (float)(was.Y - camera.Y), (float)(was.Z - camera.Z), camera.Near),
                };
                cmd.PushConstants(_layout, Stages, ByteSize.Zero, push);
                foreach (var part in draw.Model.Parts)
                    draw.Model.Mesh.DrawRange(cmd, part.FirstIndex, part.IndexCount);
            }

            cmd.EndRendering();
            Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
                VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit);
        }
        else
        {
            Gpu.Barrier(cmd, image.VkImage, Subresources.Full(VkImageAspectFlags.ColorBit), VkImageLayout.General, VkImageLayout.General,
                VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        }
    }

    public void Dispose()
    {
        Image?.Dispose();
        Pipelines.Destroy(_pipeline, _layout);
        var (pool, setLayout) = (_pool, _setLayout);
        Gpu.Defer(() =>
        {
            if (!pool.IsNull())
                Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
        });
    }
}
