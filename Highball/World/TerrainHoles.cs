using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The holes the terrain leaves where grade crossings' surfaces are solid (NetworkMeshes.CrossingMesh), for terrain.frag: one
/// storage buffer of words. First the count and a header per hole (its origin from the camera and the way its road runs, its
/// grid's corner, cell size and size, and where its cells and edges start), then each hole's cells and edges (CrossingHole). The
/// headers are written every frame, the camera having moved; the rest only when the holes near the camera change.
public sealed unsafe class TerrainHoles : IDisposable
{
    /// The most holes the terrain leaves at once, the nearest to the camera, and how near they have to be.
    public const int MaxHoles = 32;
    private const double Reach = 4000.0;
    private const int HeaderWords = 4 + MaxHoles * 12;
    private const int CapacityWords = 1 << 20;

    private static VkDescriptorSetLayout _setLayout;
    private readonly BufferEx _buffer;
    private readonly VkDescriptorPool _pool;
    private readonly uint[] _header = new uint[HeaderWords];
    private List<CrossingHole> _holes = [];
    private (int Cells, int Edges)[] _bases = [];
    private bool _dirty = true;

    public VkDescriptorSet Set { get; }

    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            if (!_setLayout.IsNull())
                return _setLayout;
            var binding = new VkDescriptorSetLayoutBinding
            {
                Binding = 0, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit,
            };
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding };
            _setLayout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
            return _setLayout;
        }
    }

    public TerrainHoles()
    {
        _buffer = Buffers.Create(CapacityWords * 4L, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "terrain.holes");
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1 };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);
        var layout = SetLayout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        Set = sets[0];
        var buffer = new VkDescriptorBufferInfo { Buffer = _buffer.VkBuffer, Offset = ByteSize.Zero, Range = ByteSize.Of(CapacityWords * 4L) };
        VkWriteDescriptorSet[] writes = [new() { DstSet = Set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &buffer }];
        Gpu.Device.UpdateDescriptorSets(writes, []);
    }

    /// The holes near the camera written for this frame. Outside a render pass: it writes the buffer in the command stream.
    public void Update(CommandBuffer cmd, NetworkMeshes? network, Camera camera)
    {
        var holes = network?.CrossingHoles(camera.X, camera.Z).Where(h => h.Distance < Reach).OrderBy(h => h.Distance).Take(MaxHoles)
            .Select(h => h.Hole).ToList() ?? [];
        if (holes.Count != _holes.Count || holes.Where((h, i) => !ReferenceEquals(h, _holes[i])).Any())
            (_holes, _dirty) = (holes, true);

        // The previous frame's reads done before it's written again.
        var before = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ShaderReadBit, DstAccessMask = VkAccessFlags.TransferWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [before], [], []);

        if (_dirty)
        {
            _dirty = false;
            var body = new List<uint>();
            var bases = new List<(int, int)>();
            var kept = new List<CrossingHole>();
            foreach (var hole in _holes)
            {
                if (HeaderWords + body.Count + hole.Cells.Length + hole.Edges.Length > CapacityWords)
                    break;
                var cells = HeaderWords + body.Count;
                body.AddRange(hole.Cells);
                var edges = HeaderWords + body.Count;
                foreach (var f in hole.Edges)
                    body.Add(BitConverter.SingleToUInt32Bits(f));
                bases.Add((cells, edges));
                kept.Add(hole);
            }

            (_holes, _bases) = (kept, [.. bases]);
            var words = body.ToArray();
            const int chunk = 65536 / 4;
            for (var at = 0; at < words.Length; at += chunk)
            {
                var span = new ReadOnlySpan<uint>(words, at, Math.Min(chunk, words.Length - at));
                cmd.UpdateBuffer(_buffer.VkBuffer, ByteSize.Of((HeaderWords + at) * 4L), MemoryMarshal.AsBytes(span));
            }
        }

        static uint F(double v) => BitConverter.SingleToUInt32Bits((float)v);
        _header[0] = (uint)_holes.Count;
        for (var i = 0; i < _holes.Count; i++)
        {
            var (hole, (cells, edges)) = (_holes[i], _bases[i]);
            var h = 4 + i * 12;
            (_header[h], _header[h + 1], _header[h + 2], _header[h + 3]) = (F(hole.OriginX - camera.X), F(hole.OriginZ - camera.Z), F(hole.Dx), F(hole.Dz));
            (_header[h + 4], _header[h + 5], _header[h + 6], _header[h + 7]) = (F(hole.T0), F(hole.W0), F(hole.Cell), 0u);
            (_header[h + 8], _header[h + 9], _header[h + 10], _header[h + 11]) = ((uint)hole.Columns, (uint)hole.Rows, (uint)cells, (uint)edges);
        }

        cmd.UpdateBuffer(_buffer.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(_header, 0, 4 + _holes.Count * 12)));
        var after = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [after], [], []);
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) => cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 4, [Set], []);

    public void Dispose()
    {
        var (pool, buffer) = (_pool, _buffer);
        Gpu.Defer(() =>
        {
            Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator);
            buffer.Dispose();
        });
    }
}
