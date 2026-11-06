using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// One lamp in the world. Range: how far it reaches. Color: at one metre. A spot has Outer above -1: the cosine of the edge of its
/// beam, along Facing; Inner is where it starts to fade. Glow: the width of the bright spot seen on the lamp itself, 0 for none.
public readonly record struct LocalLight(double X, double Y, double Z, float Range, float3 Color, float3 Facing = default, float Inner = 0f, float Outer = -1f, float Glow = 0f, float3 GlowColor = default);

/// All the lamps that matter this frame: those of the route, those a screen adds, and the grid over the view that says which reach what
/// (local_lights.glsl). A cell holds fifteen lights at most, and the nearest two hundred and fifty-five are all that are used.
public sealed unsafe class LocalLights : IDisposable
{
    public const int MaxLights = 255;
    private const int Across = 16;
    private const int Down = 9;
    private const int Slices = 16;
    private const int CellWords = 4;
    private const float FarSlice = 3000f;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuLight
    {
        public float4 PlaceRange;
        public float4 ColorOuter;
        public float4 FacingInner;
    }

    private readonly BufferEx _lights;
    private readonly BufferEx _cells;
    private readonly GpuLight[] _gpu = new GpuLight[MaxLights];
    private readonly uint[] _grid = new uint[Across * Down * Slices * CellWords];
    private readonly List<LocalLight> _wanted = [];
    private readonly List<LocalLight> _used = [];
    private readonly List<int> _touched = [];

    public VkBuffer LightBuffer => _lights.VkBuffer;
    public VkBuffer CellBuffer => _cells.VkBuffer;
    public static long LightBufferSize => MaxLights * sizeof(GpuLight);
    public static long CellBufferSize => Across * Down * Slices * CellWords * sizeof(uint);

    /// What the shaders need to find a cell: cells per pixel across and down, slices per log of a distance, and whether there are lights.
    public float4 Measures { get; private set; }

    /// The lights in use this frame, for drawing their glows.
    public IReadOnlyList<LocalLight> Used => _used;

    public LocalLights()
    {
        _lights = Buffers.Create(LightBufferSize, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "lights.list");
        _cells = Buffers.Create(CellBufferSize, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferGpu, "lights.cells");
    }

    /// Starts a frame's list of lights.
    public void Begin() => _wanted.Clear();

    public void Add(in LocalLight light) => _wanted.Add(light);

    /// Picks the nearest lights, sorts them into the grid and writes both for the frame. Outside any render pass, before the world.
    public void Upload(CommandBuffer cmd, Camera camera, int2 size, float aspect)
    {
        var (cx, cy, cz) = (camera.X, camera.Y, camera.Z);
        double Distance(in LocalLight l)
        {
            var (dx, dy, dz) = (l.X - cx, l.Y - cy, l.Z - cz);
            return Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz) - l.Range, 0.0);
        }

        _used.Clear();
        if (_wanted.Count > MaxLights)
            _wanted.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        for (var i = 0; i < _wanted.Count && _used.Count < MaxLights; i++)
            _used.Add(_wanted[i]);

        foreach (var index in _touched)
            _grid[index] = 0u;
        _touched.Clear();

        var (right, up, forward) = (camera.Right, camera.Up, camera.Forward);
        var tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var tanX = tanY * aspect;
        var sliceScale = Slices / MathF.Log(FarSlice);

        for (var n = 0; n < _used.Count; n++)
        {
            var light = _used[n];
            var place = new float3((float)(light.X - cx), (float)(light.Y - cy), (float)(light.Z - cz));
            _gpu[n] = new GpuLight
            {
                PlaceRange = new float4(place, light.Range),
                ColorOuter = new float4(light.Color, light.Outer),
                FacingInner = new float4(light.Facing, light.Inner),
            };

            var (vx, vy, vz) = (MathEx.Dot(place, right), MathEx.Dot(place, up), MathEx.Dot(place, forward));
            var (nearest, farthest) = (vz - light.Range, vz + light.Range);
            if (farthest <= 0f)
                continue;

            var firstSlice = Math.Clamp((int)(MathF.Log(MathF.Max(nearest, 1f)) * sliceScale), 0, Slices - 1);
            var lastSlice = Math.Clamp((int)(MathF.Log(MathF.Max(farthest, 1f)) * sliceScale), 0, Slices - 1);
            for (var slice = firstSlice; slice <= lastSlice; slice++)
            {
                var sliceNear = MathF.Max(MathF.Exp(slice / sliceScale), nearest);
                var sliceFar = slice == Slices - 1 ? farthest : MathF.Min(MathF.Exp((slice + 1) / sliceScale), farthest);
                sliceNear = MathF.Max(sliceNear, 0.5f);
                sliceFar = MathF.Max(sliceFar, sliceNear);

                // Where the sphere can be on screen, for anything in this slice: wider the nearer it's taken to be.
                static (float Low, float High) Reach(float at, float radius, float near, float far, float tan)
                {
                    var (a, b) = ((at - radius) / (at - radius < 0f ? near : far), (at + radius) / (at + radius > 0f ? near : far));
                    return (a / tan, b / tan);
                }

                var (xLow, xHigh) = Reach(vx, light.Range, sliceNear, sliceFar, tanX);
                var (yLow, yHigh) = Reach(vy, light.Range, sliceNear, sliceFar, tanY);
                if (xHigh < -1f || xLow > 1f || yHigh < -1f || yLow > 1f)
                    continue;

                var (x0, x1) = (Cell(xLow, Across), Cell(xHigh, Across));
                // Pixel rows run down the screen, and clip space's y up it.
                var (y0, y1) = (Cell(-yHigh, Down), Cell(-yLow, Down));
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                        Put(((slice * Down + y) * Across + x) * CellWords, n);
                }
            }
        }

        var count = _used.Count;
        Measures = new float4(Across / (float)size.X, Down / (float)size.Y, sliceScale, count > 0 ? 1f : 0f);
        if (count > 0)
        {
            cmd.UpdateBuffer(_lights.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(_gpu.AsSpan(0, count)));
            cmd.UpdateBuffer(_cells.VkBuffer, ByteSize.Zero, MemoryMarshal.AsBytes(_grid.AsSpan()));
            var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit };
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [barrier], [], []);
        }
    }

    private static int Cell(float ndc, int cells) => Math.Clamp((int)((ndc * 0.5f + 0.5f) * cells), 0, cells - 1);

    private void Put(int index, int light)
    {
        var count = _grid[index] & 255u;
        if (count >= 15u)
            return;

        if (count == 0u)
        {
            for (var w = 0; w < CellWords; w++)
                _touched.Add(index + w);
        }

        var place = count + 1u;
        _grid[index + (int)(place / 4u)] |= (uint)light << (int)(place % 4u * 8u);
        _grid[index] = (_grid[index] & ~255u) | (count + 1u);
    }

    public void Dispose()
    {
        var (lights, cells) = (_lights, _cells);
        Gpu.Defer(() =>
        {
            lights.Dispose();
            cells.Dispose();
        });
    }
}
