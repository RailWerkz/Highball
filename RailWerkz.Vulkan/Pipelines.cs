using Brutal;

namespace RailWerkz.Vulkan;

/// What a graphics pipeline draws with. Either Formats (dynamic rendering into a RenderTarget) or RenderPass (the
/// present pass) says where it draws.
public sealed class PipelineDesc
{
    public required Shader Vertex { get; init; }
    public required Shader Fragment { get; init; }
    public required VkPipelineLayout Layout { get; init; }

    /// Vertex layout and stride; none for pipelines that make their vertices in the shader (fullscreen passes).
    public VertexAttribute[] Attributes { get; init; } = [];
    public int Stride { get; init; }

    /// Per-instance attributes from binding 1, for instanced draws (Mesh.DrawInstanced).
    public VertexAttribute[] InstanceAttributes { get; init; } = [];
    public int InstanceStride { get; init; }

    public VkCullModeFlags Cull { get; init; } = VkCullModeFlags.BackBit;
    public bool Wireframe { get; init; }

    /// Reverse-Z: GreaterOrEqual. Off for fullscreen passes.
    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; } = true;

    public VkFormat ColorFormat { get; init; } = VkFormat.R16G16B16A16SFloat;
    public VkFormat DepthFormat { get; init; } = RenderTarget.DepthFormat;
    public VkRenderPass RenderPass { get; init; }
}

/// Graphics pipelines and their layouts. Viewport and scissor are always dynamic.
public static unsafe class Pipelines
{
    /// A layout with push constants only (visible to both stages) and any descriptor set layouts.
    public static VkPipelineLayout Layout(int pushBytes, params VkDescriptorSetLayout[] sets)
    {
        var range = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Offset = (ByteSize32)0,
            Size = (ByteSize32)pushBytes,
        };
        return Gpu.Device.CreatePipelineLayout(sets, pushBytes > 0 ? [range] : [], Gpu.Device.VkAllocator);
    }

    public static VkPipeline Create(PipelineDesc desc)
    {
        fixed (byte* main = "main\0"u8)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = desc.Vertex.Module, Name = main };
            stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = desc.Fragment.Module, Name = main };

            var emptyInput = new VkPipelineVertexInputStateCreateInfo();
            var vertexInput = Mesh.Input(desc.Attributes, Math.Max(desc.Stride, 4), desc.InstanceAttributes, desc.InstanceStride);
            VkPipelineVertexInputStateCreateInfo* input = vertexInput;
            if (desc.Attributes.Length == 0)
                input = &emptyInput;
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = desc.Wireframe ? VkPolygonMode.Line : VkPolygonMode.Fill,
                CullMode = desc.Cull,
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depth = new VkPipelineDepthStencilStateCreateInfo
            {
                DepthTestEnable = new Bool32(desc.DepthTest),
                DepthWriteEnable = new Bool32(desc.DepthWrite),
                DepthCompareOp = VkCompareOp.GreaterOrEqual,
            };
            var attachment = new VkPipelineColorBlendAttachmentState
            {
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &attachment };
            var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

            var colorFormat = desc.ColorFormat;
            var rendering = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = 1,
                ColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = desc.DepthFormat,
            };

            var info = new VkGraphicsPipelineCreateInfo
            {
                Next = desc.RenderPass.IsNull() ? &rendering : null,
                StageCount = 2,
                Stages = stages,
                VertexInputState = input,
                InputAssemblyState = &inputAssembly,
                ViewportState = &viewport,
                RasterizationState = &raster,
                MultisampleState = &multisample,
                DepthStencilState = &depth,
                ColorBlendState = &blend,
                DynamicState = &dynamic,
                Layout = desc.Layout,
                RenderPass = desc.RenderPass,
            };
            var pipeline = Gpu.Device.CreateGraphicsPipeline(default, info, Gpu.Device.VkAllocator);
            GC.KeepAlive(vertexInput);
            return pipeline;
        }
    }

    /// Viewport and scissor over the whole target. The viewport is flipped (negative height) so +Y is up in clip space,
    /// as in every other API; depth runs 0..1.
    public static void SetViewport(CommandBuffer cmd, int2 size)
    {
        cmd.SetViewport(0, [new VkViewport { X = 0f, Y = size.Y, Width = size.X, Height = -size.Y, MinDepth = 0f, MaxDepth = 1f }]);
        cmd.SetScissor(0, [new VkRect2D(size.X, size.Y)]);
    }

    public static void Destroy(VkPipeline pipeline, VkPipelineLayout layout)
    {
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroyPipeline(pipeline, Gpu.Device.VkAllocator);
            if (!layout.IsNull())
                Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
        });
    }
}
