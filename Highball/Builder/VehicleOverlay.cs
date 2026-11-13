using RailWerkz.Vulkan;
using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// What's drawn over the train on the screen: picking a vehicle by clicking it, a faint glowing outline round the picked one,
/// and name tags floating over the vehicles. The outline is the vehicle's true silhouette: its triangles are filled into a
/// coarse grid over where it lies on the screen, and the grid's edge traced and smoothed into closed lines.
public sealed class VehicleOverlay : IDisposable
{
    private readonly Dictionary<string, Mesh?> _outlineMeshes = [];

    /// A model's whole shape (every part of its file, glass and all) welded onto a coarse grid, as a mesh for the outline's
    /// mask (VehicleOutline): no holes where the game doesn't draw glass, and no hair-thin pieces. Null if it can't be read.
    public Mesh? OutlineMesh(string model)
    {
        if (_outlineMeshes.TryGetValue(model, out var mesh))
            return mesh;
        if (ShapeOf(model) is { } shape)
        {
            var vertices = shape.Points.Select(p => new Vertex(p, new float3(0f, 1f, 0f), default)).ToArray();
            mesh = Mesh.Create(vertices, shape.Triangles.Select(i => (uint)i).ToArray());
            (mesh.BoundsMin, mesh.BoundsMax) = (shape.Min, shape.Max);
        }

        return _outlineMeshes[model] = mesh;
    }

    public void Dispose()
    {
        foreach (var mesh in _outlineMeshes.Values)
            if (mesh is not null)
                Gpu.Defer(mesh.Dispose);
        _outlineMeshes.Clear();
    }

    /// A vehicle as drawn this frame: its model, and how it's turned and where it stands.
    public readonly record struct Placed(TrainCar Car, string Model, float4 Rotation, double3 At);

    /// Projects a world point to the screen, or null when it's behind the camera.
    public delegate float2? Projector(double x, double y, double z);

    /// A vehicle's outline shape: its model welded onto a coarse grid (vertex clustering), so the silhouette keeps its form
    /// to a few centimetres with a small fraction of the full model's points.
    private sealed class Shape(float3[] points, int[] triangles, float3 min, float3 max)
    {
        /// The welded points, in the model's own axes (front +z, up +y).
        public readonly float3[] Points = points;

        /// Three indices into Points a triangle.
        public readonly int[] Triangles = triangles;

        public readonly float3 Min = min, Max = max;
    }

    /// The size of the grid models are welded onto for their outlines.
    private const float OutlineCell = 0.04f;

    private readonly Dictionary<string, Shape?> _shapes = [];

    private Shape? ShapeOf(string model)
    {
        if (_shapes.TryGetValue(model, out var shape))
            return shape;
        try
        {
            var points = new List<float3>();
            var sums = new List<(float3 Sum, int Count)>();
            var cells = new Dictionary<(int, int, int), int>();
            var triangles = new List<int>();
            var seen = new HashSet<(int, int, int)>();
            var min = new float3(float.MaxValue);
            var max = new float3(float.MinValue);
            Span<int> corner = stackalloc int[3];
            foreach (var part in RailWerkz.Vulkan.Model.LoadParts(model).Values)
            {
                for (var t = 0; t + 2 < part.Indices.Length; t += 3)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var p = part.Vertices[part.Indices[t + k]].Position;
                        min = float3.Min(min, p);
                        max = float3.Max(max, p);
                        var key = ((int)MathF.Floor(p.X / OutlineCell), (int)MathF.Floor(p.Y / OutlineCell), (int)MathF.Floor(p.Z / OutlineCell));
                        if (!cells.TryGetValue(key, out var index))
                        {
                            cells[key] = index = sums.Count;
                            sums.Add((default, 0));
                        }

                        sums[index] = (sums[index].Sum + p, sums[index].Count + 1);
                        corner[k] = index;
                    }

                    // Collapsed onto one or two cells, it covers nothing; the same three cells again add nothing.
                    if (corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2])
                        continue;
                    var (a, b, c) = (corner[0], corner[1], corner[2]);
                    if (a > b) (a, b) = (b, a);
                    if (b > c) (b, c) = (c, b);
                    if (a > b) (a, b) = (b, a);
                    if (!seen.Add((a, b, c)))
                        continue;
                    triangles.Add(corner[0]);
                    triangles.Add(corner[1]);
                    triangles.Add(corner[2]);
                }
            }

            foreach (var (sum, count) in sums)
                points.Add(sum / count);

            // Anything thinner than the grid (a wire, an aerial, a helper piece the game doesn't draw) would make a hair-thin
            // line of its own in the outline, and isn't part of the vehicle's form: left out.
            var kept = new List<int>(triangles.Count);
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var (pa, pb, pc) = (points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]);
                var (ab, bc, ca) = (pb - pa, pc - pb, pa - pc);
                var cross = Cross(ab, -ca);
                var area2 = MathF.Sqrt(cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z);
                var longest = MathF.Sqrt(MathF.Max(Dot3(ab, ab), MathF.Max(Dot3(bc, bc), Dot3(ca, ca))));
                if (longest > 0f && area2 / longest >= OutlineCell)
                    kept.AddRange([triangles[t], triangles[t + 1], triangles[t + 2]]);
            }

            shape = kept.Count >= 3 ? new Shape([.. points], [.. kept], min, max) : null;
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicle outline: {model} unreadable ({e.Message})");
            shape = null;
        }

        return _shapes[model] = shape;
    }

    private static float Dot3(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static float3 Cross(float3 a, float3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// v turned by the unit quaternion q (x y z w).
    public static float3 Rotate(float4 q, float3 v)
    {
        var u = new float3(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2f;
        return v + t * q.W + Cross(u, t);
    }

    private static float2? Screen(in Placed placed, float3 local, Projector project)
    {
        var p = Rotate(placed.Rotation, local);
        return project(placed.At.X + p.X, placed.At.Y + p.Y, placed.At.Z + p.Z);
    }

    // --- Picking. ---

    /// The vehicle under the pointer, nearest the camera if several are; -1 for none. Each is taken as its box on the screen.
    public int Pick(IReadOnlyList<Placed> placed, float2 pointer, Projector project, double3 camera)
    {
        var best = -1;
        var nearest = double.MaxValue;
        Span<float2> corners = stackalloc float2[8];
        var hull = new List<float2>(8);
        for (var i = 0; i < placed.Count; i++)
        {
            if (ShapeOf(placed[i].Model) is not { } shape)
                continue;
            var seen = true;
            for (var k = 0; k < 8 && seen; k++)
            {
                var local = new float3((k & 1) == 0 ? shape.Min.X : shape.Max.X, (k & 2) == 0 ? shape.Min.Y : shape.Max.Y, (k & 4) == 0 ? shape.Min.Z : shape.Max.Z);
                if (Screen(placed[i], local, project) is { } s)
                    corners[k] = s;
                else
                    seen = false;
            }

            if (!seen)
                continue;
            Hull(corners, hull);
            if (!Inside(hull, pointer))
                continue;
            var (dx, dy, dz) = (placed[i].At.X - camera.X, placed[i].At.Y - camera.Y, placed[i].At.Z - camera.Z);
            var distance = dx * dx + dy * dy + dz * dz;
            if (distance < nearest)
                (best, nearest) = (i, distance);
        }

        return best;
    }

    /// The convex hull of a few points, anticlockwise on the screen (monotone chain).
    private static void Hull(ReadOnlySpan<float2> points, List<float2> hull)
    {
        var sorted = points.ToArray();
        Array.Sort(sorted, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        hull.Clear();
        static float Turn(float2 o, float2 a, float2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Turn(hull[^2], hull[^1], p) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        var lower = hull.Count + 1;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Turn(hull[^2], hull[^1], sorted[i]) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[i]);
        }

        hull.RemoveAt(hull.Count - 1);
    }

    private static bool Inside(List<float2> hull, float2 p)
    {
        if (hull.Count < 3)
            return false;
        for (var i = 0; i < hull.Count; i++)
        {
            var (a, b) = (hull[i], hull[(i + 1) % hull.Count]);
            if ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) < 0f)
                return false;
        }

        return true;
    }

    // --- Tags. ---

    /// A name tag over the vehicle's roof, on a short stem, fading out with distance; the picked one's in the accent.
    public void Tag(ImDrawListPtr draw, in Placed placed, Projector project, double3 camera, float2 clipMin, float2 clipMax, bool picked)
    {
        if (ShapeOf(placed.Model) is not { } shape)
            return;
        var (dx, dy, dz) = (placed.At.X - camera.X, placed.At.Y - camera.Y, placed.At.Z - camera.Z);
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var fade = 1f - Math.Clamp((float)((distance - 350.0) / 450.0), 0f, 1f);
        if (fade <= 0f)
            return;
        var roof = new float3(0f, shape.Max.Y, (shape.Min.Z + shape.Max.Z) * 0.5f);
        if (Screen(placed, roof, project) is not { } foot)
            return;
        var stem = S(14f);
        var size = S(13f);
        var label = placed.Car.Label;
        var text = Canvas.TextSize(Medium, size, label);
        var pad = new float2(S(8f), S(4f));
        var bottom = foot - new float2(0f, stem);
        var min = new float2(bottom.X - text.X * 0.5f - pad.X, bottom.Y - text.Y - pad.Y * 2f);
        var max = new float2(bottom.X + text.X * 0.5f + pad.X, bottom.Y);
        if (max.X < clipMin.X || min.X > clipMax.X || max.Y < clipMin.Y || min.Y > clipMax.Y)
            return;
        draw.PushClipRect(clipMin, clipMax, true);
        var edge = picked ? Accent : StrokeStrong;
        draw.AddLine(bottom, foot, edge.WithAlpha((picked ? 0.8f : 0.35f) * fade), S(1f));
        draw.AddRectFilled(min, max, Surface.WithAlpha(0.82f * fade), S(4f));
        draw.AddRect(min, max, edge.WithAlpha((picked ? 0.9f : 0.5f) * fade), S(4f), ImDrawFlags.None, S(1f));
        Canvas.Text(draw, Medium, size, new float2(min.X + pad.X, min.Y + pad.Y), Text.WithAlpha(fade), label);
        draw.PopClipRect();
    }
}
