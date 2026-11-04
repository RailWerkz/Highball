using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;

namespace RailWerkz.Vulkan;

/// Position, normal, texture coordinate: what terrain and models are made of.
[StructLayout(LayoutKind.Sequential)]
public struct Vertex(float3 position, float3 normal, float2 uv)
{
    public float3 Position = position;
    public float3 Normal = normal;
    public float2 Uv = uv;
}

/// A Vertex that bends with up to four bones: indices into the skeleton's joint palette (as floats) and their weights.
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex(float3 position, float3 normal, float2 uv, float4 joints, float4 weights)
{
    public float3 Position = position;
    public float3 Normal = normal;
    public float2 Uv = uv;
    public float4 Joints = joints;
    public float4 Weights = weights;
}

/// One vertex attribute: shader location, float count and byte offset.
public readonly record struct VertexAttribute(int Location, int Components, int Offset);

/// A device-local vertex buffer and an optional 32-bit index buffer. Pipelines take the layout and stride from here.
public sealed class Mesh : IDisposable
{
    private BufferEx _vertices;
    private BufferEx _indices;

    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }
    public int Stride { get; private set; }
    public VertexAttribute[] Layout { get; private set; } = [];
    public float3 BoundsMin { get; set; }
    public float3 BoundsMax { get; set; }

    public static readonly VertexAttribute[] VertexLayout =
    [
        new(0, 3, 0),
        new(1, 3, 12),
        new(2, 2, 24),
    ];

    public static readonly VertexAttribute[] SkinnedLayout =
    [
        new(0, 3, 0),
        new(1, 3, 12),
        new(2, 2, 24),
        new(3, 4, 32),
        new(4, 4, 48),
    ];

    /// A static mesh of Vertex with 32-bit indices.
    public static Mesh Create(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices) =>
        Create(vertices, VertexLayout, indices);

    /// later: copied at the start of the next frame (Buffers.UploadLater) instead of waiting for the GPU now.
    public static Mesh Create<T>(ReadOnlySpan<T> vertices, VertexAttribute[] layout, ReadOnlySpan<uint> indices = default, bool later = false)
        where T : unmanaged
    {
        var mesh = new Mesh
        {
            Stride = Unsafe.SizeOf<T>(),
            Layout = layout,
            VertexCount = vertices.Length,
            IndexCount = indices.Length,
        };

        BufferEx upload(ReadOnlySpan<byte> data, VkBufferUsageFlags usage, string name) =>
            later ? Buffers.UploadLater(data, usage, name) : Buffers.Upload(data, usage, name);
        mesh._vertices = upload(MemoryMarshal.AsBytes(vertices), VkBufferUsageFlags.VertexBufferBit, "mesh.vertices");
        if (!indices.IsEmpty)
            mesh._indices = upload(MemoryMarshal.AsBytes(indices), VkBufferUsageFlags.IndexBufferBit, "mesh.indices");
        return mesh;
    }

    /// The vertex input state for a pipeline drawing this layout from binding 0, and per-instance attributes from
    /// binding 1 if there are any.
    public static VertexInput Input(VertexAttribute[] layout, int stride, VertexAttribute[]? instanceLayout = null, int instanceStride = 0)
    {
        instanceLayout ??= [];
        var bindings = instanceLayout.Length > 0 ? 2 : 1;
        var input = new VertexInput(bindings, layout.Length + instanceLayout.Length).AddBinding(0, ByteSize.Of(stride), VkVertexInputRate.Vertex);
        if (instanceLayout.Length > 0)
            input.AddBinding(1, ByteSize.Of(instanceStride), VkVertexInputRate.Instance);

        foreach (var a in layout)
            input.AddAttribute(a.Location, 0, Format(a), ByteSize.Of(a.Offset));
        foreach (var a in instanceLayout)
            input.AddAttribute(a.Location, 1, Format(a), ByteSize.Of(a.Offset));
        return input;
    }

    private static VkFormat Format(VertexAttribute a) => a.Components switch
    {
        1 => VkFormat.R32SFloat,
        2 => VkFormat.R32G32SFloat,
        3 => VkFormat.R32G32B32SFloat,
        _ => VkFormat.R32G32B32A32SFloat,
    };

    /// Draw calls and triangles since the last ResetStats, for the dev stats overlay.
    public static int DrawCalls { get; private set; }
    public static long Triangles { get; private set; }

    public static void ResetStats()
    {
        DrawCalls = 0;
        Triangles = 0;
    }

    private void Bind(CommandBuffer cmd)
    {
        cmd.BindVertexBuffer(0, _vertices.VkBuffer, ByteSize.Zero);
        if (IndexCount > 0)
            cmd.BindIndexBuffer(_indices, VkIndexType.UInt32);
    }

    public void Draw(CommandBuffer cmd, int instances = 1)
    {
        Bind(cmd);
        DrawCalls++;
        Triangles += (IndexCount > 0 ? IndexCount : VertexCount) / 3 * instances;

        if (IndexCount > 0)
            cmd.DrawIndexed(IndexCount, instances, 0, 0, 0);
        else
            cmd.Draw(VertexCount, instances, 0, 0);
    }

    /// Draws a range of the index buffer (a submesh).
    public void DrawRange(CommandBuffer cmd, int firstIndex, int count, int instances = 1)
    {
        if (count <= 0 || instances <= 0)
            return;

        Bind(cmd);
        DrawCalls++;
        Triangles += count / 3 * instances;
        cmd.DrawIndexed(count, instances, firstIndex, 0, 0);
    }

    /// Draws this mesh's vertices with someone else's 32-bit index buffer (shared index lists, like the terrain's).
    public void DrawWith(CommandBuffer cmd, BufferEx indices, int count)
    {
        cmd.BindVertexBuffer(0, _vertices.VkBuffer, ByteSize.Zero);
        cmd.BindIndexBuffer(indices, VkIndexType.UInt32);
        DrawCalls++;
        Triangles += count / 3;
        cmd.DrawIndexed(count, 1, 0, 0, 0);
    }

    /// Draws this mesh once for each of count instances, their data read from instances (binding 1) starting at first.
    public void DrawInstanced(CommandBuffer cmd, BufferEx instances, int count, int first = 0)
    {
        if (count <= 0)
            return;

        Bind(cmd);
        cmd.BindVertexBuffer(1, instances.VkBuffer, ByteSize.Zero);
        DrawCalls++;
        Triangles += (IndexCount > 0 ? IndexCount : VertexCount) / 3 * count;
        if (IndexCount > 0)
            cmd.DrawIndexed(IndexCount, count, 0, 0, first);
        else
            cmd.Draw(VertexCount, count, 0, first);
    }

    public void Dispose()
    {
        if (_vertices.IsNull())
            return;

        var vertices = _vertices;
        var indices = _indices;
        _vertices = default;
        Gpu.Defer(() =>
        {
            vertices.Dispose();
            if (!indices.IsNull())
                indices.Dispose();
        });
    }
}
