using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// Temporal anti-aliasing (and upscaling, with Render resolution under 100%) by AMD FidelityFX FSR 2.2.1 (MIT: its shaders and
/// licence are in Content/Shaders/Post/Fsr2). It does what plain TAA can't: it tracks the thin things that come and go between
/// frames (far ties, rails, wires: its "locks") and keeps them steady instead of letting them flicker, rejects history by depth and
/// motion rather than colour alone, and sharpens the result (RCAS).
///
/// Each frame: the world's motion vectors (fsr2_motion.comp), then AMD's six passes as its own host code (ffx_fsr2.cpp) runs them:
/// the luminance pyramid and auto exposure, the depth and motion dilated and last frame's depth rebuilt, the depth clip (what's
/// newly uncovered), the locks, the accumulation into the history at the picture's size, and RCAS into the picture.
public sealed unsafe class Fsr2 : IDisposable
{
    // ffx_fsr2_private.h's Fsr2Constants, as std140 lays out cbFSR2 (the same).
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public int2 RenderSize, MaxRenderSize, DisplaySize, InputColorResourceDimensions, LumaMipDimensions;
        public int LumaMipLevelToUse, FrameIndex;
        public float4 DeviceToViewDepth;
        public float2 Jitter, MotionVectorScale, DownscaleFactor, MotionVectorJitterCancellation;
        public float PreExposure, PreviousFramePreExposure, TanHalfFov, JitterSequenceLength, DeltaTime, DynamicResChangeFactor, ViewSpaceToMetersFactor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpdConstants
    {
        public uint Mips, NumWorkGroups;
        public uint2 WorkGroupOffset, RenderSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MotionPush
    {
        public float4x4 PreviousViewProjection;
        public float4 Right, Up, Forward, Moved;
    }

    /// ffx_fsr2_resources.h: the luminance pyramid's level the shading change is read from (and level 5, SPD's other stored one).
    private const int ShadingChangeMip = 4;

    private enum Kind { Sampled, Storage, Uniform }

    private enum Pass { Luminance, Reconstruct, DepthClip, Lock, Accumulate, Rcas }

    /// Each pass's bindings in set 1, as its ffx_fsr2_*_pass.glsl numbers them (those our build of it uses: checked against the
    /// compiled SPIR-V).
    private static readonly (int Binding, Kind Kind)[][] Bindings =
    [
        [(0, Kind.Sampled), (1, Kind.Storage), (2, Kind.Storage), (3, Kind.Storage), (4, Kind.Storage), (5, Kind.Uniform), (6, Kind.Uniform)],
        [(0, Kind.Sampled), (1, Kind.Sampled), (2, Kind.Sampled), (3, Kind.Sampled), (5, Kind.Storage), (6, Kind.Storage), (7, Kind.Storage), (11, Kind.Storage), (12, Kind.Uniform)],
        [(0, Kind.Sampled), (1, Kind.Sampled), (2, Kind.Sampled), (3, Kind.Sampled), (4, Kind.Sampled), (6, Kind.Sampled), (7, Kind.Sampled), (8, Kind.Sampled), (10, Kind.Sampled),
            (12, Kind.Storage), (13, Kind.Storage), (14, Kind.Uniform)],
        [(0, Kind.Sampled), (1, Kind.Storage), (2, Kind.Storage), (3, Kind.Uniform)],
        [(0, Kind.Sampled), (1, Kind.Sampled), (2, Kind.Sampled), (3, Kind.Sampled), (4, Kind.Sampled), (6, Kind.Sampled), (10, Kind.Sampled), (12, Kind.Sampled),
            (13, Kind.Storage), (14, Kind.Storage), (16, Kind.Storage), (17, Kind.Storage), (18, Kind.Uniform)],
        [(0, Kind.Sampled), (1, Kind.Sampled), (2, Kind.Storage), (3, Kind.Uniform), (4, Kind.Uniform)],
    ];

    private static readonly string[] Shaders = ["fsr2_compute_luminance_pyramid", "fsr2_reconstruct_previous_depth", "fsr2_depth_clip", "fsr2_lock", "fsr2_accumulate", "fsr2_rcas"];

    private readonly VkSampler _point, _linear;
    private readonly VkDescriptorSetLayout _samplerSetLayout;
    private readonly VkDescriptorSetLayout[] _setLayouts = new VkDescriptorSetLayout[6];
    private readonly VkPipelineLayout[] _layouts = new VkPipelineLayout[6];
    private readonly VkPipeline[] _pipelines = new VkPipeline[6];

    private readonly VkDescriptorSetLayout _motionSetLayout;
    private readonly VkPipelineLayout _motionLayout;
    private readonly VkPipeline _motionPipeline;

    private readonly BufferEx _constants, _spd, _rcas;

    private VkDescriptorPool _pool;
    private VkDescriptorSet _samplerSet, _motionSet;
    /// Each pass's sets, one for each frame's parity (which of each ping-ponged pair is this frame's); RCAS's also by which of
    /// the post-process's two pictures it writes.
    private readonly VkDescriptorSet[,] _sets = new VkDescriptorSet[5, 2];
    private readonly VkDescriptorSet[,] _rcasSets = new VkDescriptorSet[2, 2];

    private StorageImage? _motionVectors, _reconstructedDepth, _dilatedDepth, _lockInputLuma, _dilatedReactive, _preparedInput, _lumaMips, _spdAtomic, _autoExposure,
        _newLocks, _defaultReactive;
    private readonly StorageImage?[] _dilatedMotion = new StorageImage?[2], _lockStatus = new StorageImage?[2], _upscaled = new StorageImage?[2], _lumaHistory = new StorageImage?[2];
    private VkImageView _lumaShadingChange, _lumaMip5;

    private int2 _render, _display;
    private int _targetVersion = -1;
    private (StorageImage A, StorageImage B)? _outputs;
    private int _frameIndex, _jitterIndex, _parity;
    private bool _reset = true;

    /// How many jitter positions to cycle through: ffxFsr2GetJitterPhaseCount, 8 times the upscaling's area ratio.
    public int PhaseCount { get; private set; } = 8;

    public Fsr2()
    {
        var point = Presets.Sampler.SamplerPointClamped;
        _point = Gpu.Device.CreateSampler(in point, Gpu.Device.VkAllocator);
        var linear = Presets.Sampler.SamplerLinearClamped;
        _linear = Gpu.Device.CreateSampler(in linear, Gpu.Device.VkAllocator);
        _samplerSetLayout = SetLayout([(0, VkDescriptorType.Sampler), (1, VkDescriptorType.Sampler)]);

        for (var p = 0; p < 6; p++)
        {
            _setLayouts[p] = SetLayout(Bindings[p].Select(b => (b.Binding, TypeOf(b.Kind))).ToArray());
            _layouts[p] = Gpu.Device.CreatePipelineLayout([_samplerSetLayout, _setLayouts[p]], [], Gpu.Device.VkAllocator);
            using var shader = Shader.Load($"Post/Fsr2/{Shaders[p]}.comp");
            _pipelines[p] = Pipelines.CreateCompute(shader, _layouts[p]);
        }

        _motionSetLayout = SetLayout([(0, VkDescriptorType.StorageImage), (1, VkDescriptorType.CombinedImageSampler), (2, VkDescriptorType.CombinedImageSampler)]);
        var range = new VkPushConstantRange { StageFlags = VkShaderStageFlags.ComputeBit, Offset = (ByteSize32)0, Size = (ByteSize32)sizeof(MotionPush) };
        _motionLayout = Gpu.Device.CreatePipelineLayout([_motionSetLayout], [range], Gpu.Device.VkAllocator);
        using (var shader = Shader.Load("Post/Fsr2/fsr2_motion.comp"))
            _motionPipeline = Pipelines.CreateCompute(shader, _motionLayout);

        var uniform = VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit;
        _constants = Buffers.Create(sizeof(Constants), uniform, MemoryPreference.PreferGpu, "fsr2.constants");
        _spd = Buffers.Create(sizeof(SpdConstants), uniform, MemoryPreference.PreferGpu, "fsr2.spd");
        _rcas = Buffers.Create(16, uniform, MemoryPreference.PreferGpu, "fsr2.rcas");
    }

    private static VkDescriptorType TypeOf(Kind kind) => kind switch
    {
        Kind.Sampled => VkDescriptorType.SampledImage,
        Kind.Storage => VkDescriptorType.StorageImage,
        _ => VkDescriptorType.UniformBuffer,
    };

    private static VkDescriptorSetLayout SetLayout((int Binding, VkDescriptorType Type)[] bindings)
    {
        var list = stackalloc VkDescriptorSetLayoutBinding[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
            list[i] = new VkDescriptorSetLayoutBinding { Binding = bindings[i].Binding, DescriptorType = bindings[i].Type, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit };
        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = bindings.Length, Bindings = list };
        return Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
    }

    private VkDescriptorSet Allocate(VkDescriptorSetLayout layout)
    {
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        return sets[0];
    }

    /// Where to draw the world this frame: FSR 2's jitter (ffxFsr2GetJitterOffset), the next of PhaseCount Halton (2, 3) points
    /// within a pixel, as an offset in clip space for the camera's projection. Before anything's recorded with the camera.
    public void Jitter(Camera camera, int2 renderSize)
    {
        var i = _jitterIndex % Math.Max(PhaseCount, 1) + 1;
        var (x, y) = (Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
        _jitterPixels = new float2(x, y);
        camera.Jitter = new float2(2f * x / Math.Max(renderSize.X, 1), -2f * y / Math.Max(renderSize.Y, 1));
    }

    private float2 _jitterPixels;

    private static float Halton(int index, int radix)
    {
        var (result, fraction) = (0f, 1f / radix);
        for (; index > 0; index /= radix, fraction /= radix)
            result += index % radix * fraction;
        return result;
    }

    /// The history starts afresh next frame (a cut, a teleport, a resize).
    public void Reset() => _reset = true;

    private void Resize(RenderTarget target, int2 display, StorageImage motion, StorageImage outputA, StorageImage outputB)
    {
        if (target.Size == _render && display == _display && target.Version == _targetVersion && _outputs is { } o && ReferenceEquals(o.A, outputA) && ReferenceEquals(o.B, outputB)
            && _motionVectors is not null)
            return;

        DisposeImages();
        (_render, _display, _targetVersion, _outputs) = (target.Size, display, target.Version, (outputA, outputB));
        _reset = true;
        var (rw, rh) = (Math.Max(_render.X, 1), Math.Max(_render.Y, 1));
        var (dw, dh) = (Math.Max(display.X, 1), Math.Max(display.Y, 1));
        StorageImage Image(int w, int h, VkFormat format, string name, bool mips = false) => new(w, h, 1, format, mips, false, name);

        _motionVectors = Image(rw, rh, VkFormat.R16G16SFloat, "fsr2.motion");
        _reconstructedDepth = Image(rw, rh, VkFormat.R32UInt, "fsr2.prevdepth");
        _dilatedDepth = Image(rw, rh, VkFormat.R16SFloat, "fsr2.dilateddepth");
        _lockInputLuma = Image(rw, rh, VkFormat.R16SFloat, "fsr2.lockluma");
        _dilatedReactive = Image(rw, rh, VkFormat.R8G8UNorm, "fsr2.reactive");
        _preparedInput = Image(rw, rh, VkFormat.R16G16B16A16UNorm, "fsr2.prepared");
        _lumaMips = Image(Math.Max(rw / 2, 1), Math.Max(rh / 2, 1), VkFormat.R16SFloat, "fsr2.lumamips", mips: true);
        _lumaShadingChange = _lumaMips.MipView(Math.Min(ShadingChangeMip, _lumaMips.MipLevels - 1));
        _lumaMip5 = _lumaMips.MipView(Math.Min(5, _lumaMips.MipLevels - 1));
        _spdAtomic = Image(1, 1, VkFormat.R32UInt, "fsr2.spdatomic");
        _autoExposure = Image(1, 1, VkFormat.R32G32SFloat, "fsr2.exposure");
        _newLocks = Image(dw, dh, VkFormat.R8UNorm, "fsr2.newlocks");
        _defaultReactive = Image(1, 1, VkFormat.R8UNorm, "fsr2.noreactive");
        for (var i = 0; i < 2; i++)
        {
            _dilatedMotion[i] = Image(rw, rh, VkFormat.R16G16SFloat, "fsr2.dilatedmotion");
            _lockStatus[i] = Image(dw, dh, VkFormat.R16G16SFloat, "fsr2.lockstatus");
            _upscaled[i] = Image(dw, dh, VkFormat.R16G16B16A16SFloat, "fsr2.upscaled");
            _lumaHistory[i] = Image(dw, dh, VkFormat.R8G8B8A8UNorm, "fsr2.lumahistory");
        }

        // The SPD counter starts at 0 (the pass puts it back each time), the empty reactive masks at 0.
        var (atomic, noReactive) = (_spdAtomic.VkImage, _defaultReactive.VkImage);
        Gpu.Submit(cmd =>
        {
            var zero = new VkClearColorValue();
            var range = Subresources.Full(VkImageAspectFlags.ColorBit);
            cmd.ClearColorImage(atomic, VkImageLayout.General, &zero, [range]);
            cmd.ClearColorImage(noReactive, VkImageLayout.General, &zero, [range]);
        });

        // The sets.
        var sizes = stackalloc VkDescriptorPoolSize[5];
        const int sets = 5 * 2 + 4 + 2;
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = sets * 10 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = sets * 6 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = sets * 2 };
        sizes[3] = new VkDescriptorPoolSize { Type = VkDescriptorType.Sampler, DescriptorCount = 2 };
        sizes[4] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = sets, PoolSizeCount = 5, PoolSizes = sizes };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        _samplerSet = Allocate(_samplerSetLayout);
        var pointInfo = new VkDescriptorImageInfo { Sampler = _point };
        var linearInfo = new VkDescriptorImageInfo { Sampler = _linear };
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _samplerSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.Sampler, ImageInfo = &pointInfo },
            new VkWriteDescriptorSet { DstSet = _samplerSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.Sampler, ImageInfo = &linearInfo },
        ], []);

        // The motion vectors' pass: its image, the depth and the moving things' motion.
        _motionSet = Allocate(_motionSetLayout);
        var mvOut = new VkDescriptorImageInfo { ImageView = _motionVectors.View, ImageLayout = VkImageLayout.General };
        var depthIn = new VkDescriptorImageInfo { Sampler = target.DepthSampler, ImageView = target.DepthView, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var motionIn = new VkDescriptorImageInfo { Sampler = motion.Sampler, ImageView = motion.View, ImageLayout = VkImageLayout.General };
        Gpu.Device.UpdateDescriptorSets(
        [
            new VkWriteDescriptorSet { DstSet = _motionSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageImage, ImageInfo = &mvOut },
            new VkWriteDescriptorSet { DstSet = _motionSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depthIn },
            new VkWriteDescriptorSet { DstSet = _motionSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &motionIn },
        ], []);

        // What each pass binds where, by parity p (this frame's of each pair is p, last frame's 1 - p).
        var color = (target.ColorView, VkImageLayout.ShaderReadOnlyOptimal);
        var depth = (target.DepthView, VkImageLayout.DepthStencilReadOnlyOptimal);
        static (VkImageView, VkImageLayout) G(StorageImage image) => (image.View, VkImageLayout.General);
        for (var p = 0; p < 2; p++)
        {
            var (cur, prev) = (p, 1 - p);
            _sets[(int)Pass.Luminance, p] = Write(Pass.Luminance, [color, G(_spdAtomic), (_lumaShadingChange, VkImageLayout.General), (_lumaMip5, VkImageLayout.General), G(_autoExposure)],
                [_constants, _spd]);
            _sets[(int)Pass.Reconstruct, p] = Write(Pass.Reconstruct, [G(_motionVectors), depth, color, G(_autoExposure), G(_reconstructedDepth), G(_dilatedMotion[cur]!), G(_dilatedDepth),
                G(_lockInputLuma)], [_constants]);
            _sets[(int)Pass.DepthClip, p] = Write(Pass.DepthClip, [G(_reconstructedDepth), G(_dilatedMotion[cur]!), G(_dilatedDepth), G(_defaultReactive), G(_defaultReactive),
                G(_dilatedMotion[prev]!), G(_motionVectors), color, G(_autoExposure), G(_dilatedReactive), G(_preparedInput)], [_constants]);
            _sets[(int)Pass.Lock, p] = Write(Pass.Lock, [G(_lockInputLuma), G(_newLocks), G(_reconstructedDepth)], [_constants]);
            _sets[(int)Pass.Accumulate, p] = Write(Pass.Accumulate, [G(_autoExposure), G(_dilatedReactive), G(_dilatedMotion[cur]!), G(_upscaled[prev]!), G(_lockStatus[prev]!),
                G(_preparedInput), G(_lumaMips), G(_lumaHistory[prev]!), G(_upscaled[cur]!), G(_lockStatus[cur]!), G(_newLocks), G(_lumaHistory[cur]!)], [_constants]);
            for (var k = 0; k < 2; k++)
                _rcasSets[p, k] = Write(Pass.Rcas, [G(_autoExposure), G(_upscaled[cur]!), G(k == 0 ? outputA : outputB)], [_constants, _rcas]);
        }
    }

    /// A pass's set: its images in its bindings' order (sampled or storage as the pass takes each), then its uniform buffers.
    private VkDescriptorSet Write(Pass pass, (VkImageView View, VkImageLayout Layout)[] images, BufferEx[] buffers)
    {
        var set = Allocate(_setLayouts[(int)pass]);
        var bindings = Bindings[(int)pass];
        var imageInfos = stackalloc VkDescriptorImageInfo[images.Length];
        var bufferInfos = stackalloc VkDescriptorBufferInfo[buffers.Length];
        var writes = new VkWriteDescriptorSet[bindings.Length];
        int image = 0, buffer = 0;
        for (var i = 0; i < bindings.Length; i++)
        {
            var (binding, kind) = bindings[i];
            if (kind == Kind.Uniform)
            {
                bufferInfos[buffer] = new VkDescriptorBufferInfo { Buffer = buffers[buffer].VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(BufferSize(buffers[buffer])) };
                writes[i] = new VkWriteDescriptorSet { DstSet = set, DstBinding = binding, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &bufferInfos[buffer] };
                buffer++;
            }
            else
            {
                imageInfos[image] = new VkDescriptorImageInfo { ImageView = images[image].View, ImageLayout = images[image].Layout };
                writes[i] = new VkWriteDescriptorSet { DstSet = set, DstBinding = binding, DescriptorCount = 1, DescriptorType = TypeOf(kind), ImageInfo = &imageInfos[image] };
                image++;
            }
        }

        Gpu.Device.UpdateDescriptorSets(writes, []);
        return set;
    }

    private int BufferSize(BufferEx buffer) =>
        buffer.VkBuffer.Equals(_constants.VkBuffer) ? sizeof(Constants) : buffer.VkBuffer.Equals(_spd.VkBuffer) ? sizeof(SpdConstants) : 16;

    /// One frame: the world (target, drawn with this frame's Jitter) into output, the post-process's picture this frame (one of
    /// outputA and outputB, which it ping-pongs), at display size. Outside any render pass. sharpness 0 to 1 (RCAS).
    public void Dispatch(CommandBuffer cmd, RenderTarget target, int2 display, StorageImage motion, StorageImage outputA, StorageImage outputB, bool toB,
        Camera camera, float aspect, float4x4 previousViewProjection, float3 moved, float sharpness, float deltaTime)
    {
        Resize(target, display, motion, outputA, outputB);
        var (rw, rh) = (_render.X, _render.Y);
        var (dw, dh) = (_display.X, _display.Y);
        var reset = _reset;
        _reset = false;
        _frameIndex = reset ? 0 : _frameIndex + 1;
        PhaseCount = Math.Max(1, (int)(8f * MathF.Pow(dw / (float)rw, 2f)));

        // The motion vectors.
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        var push = new MotionPush
        {
            PreviousViewProjection = previousViewProjection, Right = new float4(camera.Right * tanX, 0f), Up = new float4(camera.Up * tanY, 0f),
            Forward = new float4(camera.Forward, camera.Near), Moved = new float4(moved, 0f),
        };
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _motionPipeline);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, _motionLayout, 0, [_motionSet], []);
        cmd.PushConstants(_motionLayout, VkShaderStageFlags.ComputeBit, ByteSize.Zero, push);
        cmd.Dispatch((rw + 7) / 8, (rh + 7) / 8, 1);

        // The constants (fsr2Dispatch's).
        var constants = new Constants
        {
            RenderSize = new int2(rw, rh), MaxRenderSize = new int2(rw, rh), DisplaySize = new int2(dw, dh), InputColorResourceDimensions = new int2(rw, rh),
            LumaMipLevelToUse = ShadingChangeMip, LumaMipDimensions = new int2(rw / (2 << ShadingChangeMip), rh / (2 << ShadingChangeMip)), FrameIndex = _frameIndex,
            // Reversed and infinite: depth is near / distance along the view (setupDeviceDepthToViewSpaceDepthParams).
            DeviceToViewDepth = new float4(-1.1920929e-7f, camera.Near, tanX, tanY),
            Jitter = _jitterPixels,
            // fsr2_motion.comp writes them in uv already.
            MotionVectorScale = new float2(1f, 1f),
            DownscaleFactor = new float2(rw / (float)dw, rh / (float)dh),
            PreExposure = 1f, PreviousFramePreExposure = 1f, TanHalfFov = tanX, JitterSequenceLength = PhaseCount,
            DeltaTime = Math.Clamp(deltaTime, 0f, 1f), DynamicResChangeFactor = 0f, ViewSpaceToMetersFactor = 1f,
        };
        // SpdSetup over the whole render size.
        var (groupsX, groupsY) = ((uint)((rw - 1) / 64 + 1), (uint)((rh - 1) / 64 + 1));
        var spd = new SpdConstants
        {
            Mips = (uint)Math.Min(Math.Floor(Math.Log2(Math.Max(rw, rh))), 12), NumWorkGroups = groupsX * groupsY,
            WorkGroupOffset = new uint2(0, 0), RenderSize = new uint2((uint)rw, (uint)rh),
        };
        // FsrRcasCon: sharpness 0..1 taken to stops (2 none, 0 most), then to a linear amount, as a float and as two halves.
        var amount = MathF.Pow(2f, -(2f - 2f * Math.Clamp(sharpness, 0f, 1f)));
        var half = (uint)BitConverter.HalfToUInt16Bits((Half)amount);
        var rcas = new uint4(BitConverter.SingleToUInt32Bits(amount), half | (half << 16), 0u, 0u);
        cmd.UpdateBuffer(_constants.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<Constants>(in constants)));
        cmd.UpdateBuffer(_spd.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<SpdConstants>(in spd)));
        cmd.UpdateBuffer(_rcas.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<uint4>(in rcas)));

        // Afresh: last frame's lock status and history cleared, the luminance pyramid, and the exposure to "none yet" (-1, 1e8).
        var p = _parity;
        if (reset)
        {
            var range = Subresources.Full(VkImageAspectFlags.ColorBit);
            var mips = Subresources.Mips(VkImageAspectFlags.ColorBit, 0, _lumaMips!.MipLevels, 0);
            var zero = new VkClearColorValue();
            var exposure = new VkClearColorValue();
            exposure.Float32[0] = -1f;
            exposure.Float32[1] = 1e8f;
            foreach (var image in (ReadOnlySpan<StorageImage?>)[_lockStatus[0], _lockStatus[1], _upscaled[0], _upscaled[1], _preparedInput, _lumaHistory[0], _lumaHistory[1],
                         _dilatedMotion[0], _dilatedMotion[1]])
                cmd.ClearColorImage(image!.VkImage, VkImageLayout.General, &zero, [range]);
            cmd.ClearColorImage(_lumaMips.VkImage, VkImageLayout.General, &zero, [mips]);
            cmd.ClearColorImage(_autoExposure!.VkImage, VkImageLayout.General, &exposure, [range]);
        }

        var ready = new VkMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.TransferWriteBit | VkAccessFlags.ShaderWriteBit,
            DstAccessMask = VkAccessFlags.UniformReadBit | VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [ready], [], []);

        Run(cmd, Pass.Luminance, _sets[(int)Pass.Luminance, p], (int)groupsX, (int)groupsY);
        Run(cmd, Pass.Reconstruct, _sets[(int)Pass.Reconstruct, p], (rw + 7) / 8, (rh + 7) / 8);
        Run(cmd, Pass.DepthClip, _sets[(int)Pass.DepthClip, p], (rw + 7) / 8, (rh + 7) / 8);
        Run(cmd, Pass.Lock, _sets[(int)Pass.Lock, p], (rw + 7) / 8, (rh + 7) / 8);
        Run(cmd, Pass.Accumulate, _sets[(int)Pass.Accumulate, p], (dw + 7) / 8, (dh + 7) / 8);
        Run(cmd, Pass.Rcas, _rcasSets[p, toB ? 1 : 0], (dw + 15) / 16, (dh + 15) / 16);

        _parity = 1 - _parity;
        _jitterIndex++;
    }

    private void Run(CommandBuffer cmd, Pass pass, VkDescriptorSet set, int x, int y)
    {
        var layout = _layouts[(int)pass];
        cmd.BindPipeline(VkPipelineBindPoint.Compute, _pipelines[(int)pass]);
        cmd.BindDescriptorSets(VkPipelineBindPoint.Compute, layout, 0, [_samplerSet, set], []);
        cmd.Dispatch(Math.Max(x, 1), Math.Max(y, 1), 1);
        var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None,
            [barrier], [], []);
    }

    private void DisposeImages()
    {
        var pool = _pool;
        _pool = default;
        if (!pool.IsNull())
            Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator));
        foreach (var image in (ReadOnlySpan<StorageImage?>)[_motionVectors, _reconstructedDepth, _dilatedDepth, _lockInputLuma, _dilatedReactive, _preparedInput, _lumaMips,
                     _spdAtomic, _autoExposure, _newLocks, _defaultReactive, .. _dilatedMotion, .. _lockStatus, .. _upscaled, .. _lumaHistory])
            image?.Dispose();
        _motionVectors = null;
    }

    public void Dispose()
    {
        DisposeImages();
        foreach (var pipeline in _pipelines)
            Pipelines.Destroy(pipeline, default);
        Pipelines.Destroy(_motionPipeline, default);
        var (constants, spd, rcas) = (_constants, _spd, _rcas);
        var (point, linear, samplerSetLayout, setLayouts, layouts, motionSetLayout, motionLayout) =
            (_point, _linear, _samplerSetLayout, _setLayouts.ToArray(), _layouts.ToArray(), _motionSetLayout, _motionLayout);
        Gpu.Defer(() =>
        {
            constants.Dispose();
            spd.Dispose();
            rcas.Dispose();
            Gpu.Device.DestroySampler(point, Gpu.Device.VkAllocator);
            Gpu.Device.DestroySampler(linear, Gpu.Device.VkAllocator);
            foreach (var layout in layouts)
                Gpu.Device.DestroyPipelineLayout(layout, Gpu.Device.VkAllocator);
            foreach (var setLayout in setLayouts)
                Gpu.Device.DestroyDescriptorSetLayout(setLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyPipelineLayout(motionLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(motionSetLayout, Gpu.Device.VkAllocator);
            Gpu.Device.DestroyDescriptorSetLayout(samplerSetLayout, Gpu.Device.VkAllocator);
        });
    }
}
