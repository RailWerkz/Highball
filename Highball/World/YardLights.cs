using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;
using Highball.Routes;

namespace Highball.World;

/// Tall lamps along the route's yards, which no map records, so they're placed here: beside yard track, every so often, clear of any
/// rail. They're drawn one at a time near the camera, so their places stay exact however far the route reaches.
public sealed class YardLights : IDisposable
{
    private const double Spacing = 130.0;
    private const float Offset = 9f;
    private const float Height = 13f;
    private const double Reach = 1500.0;
    private const double ClearOfRails = 4.5;

    private readonly Mesh _pole;
    private readonly BufferEx _standing;
    private readonly Texture2D _texture;
    private readonly TextureBinding _binding;
    private NetworkLines? _lines;
    private volatile (double X, double Y, double Z)[] _places = [];
    private readonly List<(double X, double Y, double Z)> _near = [];

    public YardLights()
    {
        _pole = PoleMesh();
        var one = new TieInstance(new float4(0f, 0f, 0f, 0.5f), new float4(0f, 0f, 1f, 0f));
        _standing = Buffers.Upload(MemoryMarshal.AsBytes(new ReadOnlySpan<TieInstance>(in one)), VkBufferUsageFlags.VertexBufferBit, "yard.pole");
        _texture = Texture2D.Solid(new byte4(95, 97, 100, 255));
        _binding = new TextureBinding(_texture);
    }

    /// The pole's lamps for this frame, and which poles to draw. On: 0 by day to 1 at night.
    public void Update(NetworkLines? lines, Camera camera, float on, LocalLights lights)
    {
        if (!ReferenceEquals(lines, _lines))
        {
            _lines = lines;
            _places = [];
            if (lines is not null)
                Task.Run(() => _places = Place(lines));
        }

        _near.Clear();
        foreach (var place in _places)
        {
            var (dx, dz) = (place.X - camera.X, place.Z - camera.Z);
            if (dx * dx + dz * dz > Reach * Reach)
                continue;

            _near.Add(place);
            if (on > 0f)
                lights.Add(new LocalLight(place.X, place.Y + Height - 0.4f, place.Z, 55f, new float3(170f, 135f, 95f) * on,
                    new float3(0f, -1f, 0f), 0.35f, 0.05f, 1.6f, new float3(2f, 1.6f, 1.15f) * on));
        }
    }

    private static (double X, double Y, double Z)[] Place(NetworkLines lines)
    {
        // A rough map of the rails, to keep the poles off them.
        var rails = new HashSet<(int, int)>();
        foreach (var line in lines.Lines)
        {
            if (!line.Rail)
                continue;
            for (var i = 0; i < line.X.Length; i++)
                rails.Add(((int)Math.Floor(line.X[i] / ClearOfRails), (int)Math.Floor(line.Z[i] / ClearOfRails)));
        }

        bool NearRail(double x, double z)
        {
            var (cx, cz) = ((int)Math.Floor(x / ClearOfRails), (int)Math.Floor(z / ClearOfRails));
            for (var a = -1; a <= 1; a++)
            {
                for (var b = -1; b <= 1; b++)
                {
                    if (rails.Contains((cx + a, cz + b)))
                        return true;
                }
            }

            return false;
        }

        var found = new List<(double, double, double)>();
        var taken = new HashSet<(int, int)>();
        foreach (var line in lines.Lines)
        {
            if (!line.Rail || line.Class != (int)RailClass.Yard || line.X.Length < 2)
                continue;

            var next = Spacing * 0.5;
            for (var i = 0; i + 1 < line.X.Length; i++)
            {
                if (line.S[i] < next)
                    continue;

                next = line.S[i] + Spacing;
                var (dx, dz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
                var length = Math.Sqrt(dx * dx + dz * dz);
                if (length < 1e-3)
                    continue;

                foreach (var side in (float[])[1f, -1f])
                {
                    var x = line.X[i] - dz / length * Offset * side;
                    var z = line.Z[i] + dx / length * Offset * side;
                    var cell = ((int)Math.Floor(x / Spacing), (int)Math.Floor(z / Spacing));
                    if (taken.Contains(cell) || NearRail(x, z))
                        continue;

                    taken.Add(cell);
                    found.Add((x, line.Y[i], z));
                    break;
                }
            }
        }

        return [.. found];
    }

    private static void Box(List<Vertex> vertices, List<uint> indices, float3 low, float3 high)
    {
        void Face(float3 a, float3 b, float3 c, float3 d, float3 normal)
        {
            var first = (uint)vertices.Count;
            vertices.Add(new Vertex(a, normal, default));
            vertices.Add(new Vertex(b, normal, default));
            vertices.Add(new Vertex(c, normal, default));
            vertices.Add(new Vertex(d, normal, default));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        var (l, h) = (low, high);
        Face(new(l.X, l.Y, h.Z), new(h.X, l.Y, h.Z), new(h.X, h.Y, h.Z), new(l.X, h.Y, h.Z), new(0f, 0f, 1f));
        Face(new(h.X, l.Y, l.Z), new(l.X, l.Y, l.Z), new(l.X, h.Y, l.Z), new(h.X, h.Y, l.Z), new(0f, 0f, -1f));
        Face(new(h.X, l.Y, h.Z), new(h.X, l.Y, l.Z), new(h.X, h.Y, l.Z), new(h.X, h.Y, h.Z), new(1f, 0f, 0f));
        Face(new(l.X, l.Y, l.Z), new(l.X, l.Y, h.Z), new(l.X, h.Y, h.Z), new(l.X, h.Y, l.Z), new(-1f, 0f, 0f));
        Face(new(l.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, l.Z), new(l.X, h.Y, l.Z), new(0f, 1f, 0f));
        Face(new(l.X, l.Y, l.Z), new(h.X, l.Y, l.Z), new(h.X, l.Y, h.Z), new(l.X, l.Y, h.Z), new(0f, -1f, 0f));
    }

    private static Mesh PoleMesh()
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        Box(vertices, indices, new float3(-0.1f, -1f, -0.1f), new float3(0.1f, Height, 0.1f));
        Box(vertices, indices, new float3(-0.5f, Height - 0.3f, -0.25f), new float3(0.5f, Height, 0.25f));
        return Mesh.Create(CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }

    /// With the part pipeline bound.
    public void Draw(CommandBuffer cmd, VkPipelineLayout layout, Camera camera, Action<float3> push)
    {
        if (_near.Count == 0)
            return;

        _binding.Bind(cmd, layout);
        DrawAll(cmd, camera, push);
    }

    /// Into a shadow cascade, with the part caster pipeline bound.
    public void DrawCasters(CommandBuffer cmd, Camera camera, Func<float3, float, bool> reaches, Action<float3> push)
    {
        foreach (var (x, y, z) in _near)
        {
            var offset = new float3((float)(x - camera.X), (float)(y - camera.Y), (float)(z - camera.Z));
            if (!reaches(offset + new float3(0f, Height * 0.5f, 0f), Height))
                continue;

            push(offset);
            _pole.DrawInstanced(cmd, _standing, 1);
        }
    }

    private void DrawAll(CommandBuffer cmd, Camera camera, Action<float3> push)
    {
        foreach (var (x, y, z) in _near)
        {
            push(new float3((float)(x - camera.X), (float)(y - camera.Y), (float)(z - camera.Z)));
            _pole.DrawInstanced(cmd, _standing, 1);
        }
    }

    public void Dispose()
    {
        _binding.Dispose();
        _texture.Dispose();
        _pole.Dispose();
        var standing = _standing;
        Gpu.Defer(() => standing.Dispose());
    }
}
