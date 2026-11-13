using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// What's drawn over the train on the screen: picking a vehicle by clicking it, a faint glowing outline round the picked one,
/// and name tags floating over the vehicles. The outline is the vehicle's true silhouette: its triangles are filled into a
/// coarse grid over where it lies on the screen, and the grid's edge traced and smoothed into closed lines.
public sealed class VehicleOverlay
{
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
            shape = triangles.Count >= 3 ? new Shape([.. points], [.. triangles], min, max) : null;
        }
        catch (Exception e)
        {
            Log.Warn($"Vehicle outline: {model} unreadable ({e.Message})");
            shape = null;
        }

        return _shapes[model] = shape;
    }

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

    // --- The outline. ---

    private bool[] _mask = [];
    private readonly Dictionary<int, (int A, int B)> _links = [];
    private readonly List<float2> _loop = [], _smooth = [];
    private float2[] _projected = [];
    private bool[] _visible = [];

    /// A faint glowing line round the vehicle's silhouette, breathing slowly, kept inside clip.
    public void Outline(ImDrawListPtr draw, in Placed placed, Projector project, float2 clipMin, float2 clipMax, Rgba color)
    {
        if (ShapeOf(placed.Model) is not { } shape)
            return;

        var points = shape.Points;
        var triangles = shape.Triangles;
        if (_projected.Length < points.Length)
        {
            _projected = new float2[points.Length];
            _visible = new bool[points.Length];
        }

        var min = new float2(float.MaxValue);
        var max = new float2(float.MinValue);
        for (var i = 0; i < points.Length; i++)
        {
            var s = Screen(placed, points[i], project);
            _visible[i] = s is not null;
            if (s is not { } p)
                continue;
            _projected[i] = p;
            min = float2.Min(min, p);
            max = float2.Max(max, p);
        }

        // Only the part on the screen, with a little round it so the line closes where it's cut off.
        var margin = S(8f);
        min = float2.Max(min, clipMin - new float2(margin));
        max = float2.Min(max, clipMax + new float2(margin));
        if (max.X <= min.X || max.Y <= min.Y)
            return;

        // Cells about 2 px across, fewer for a vehicle filling the screen.
        var cell = MathF.Max(S(2f), MathF.Max(max.X - min.X, max.Y - min.Y) / 320f);
        var w = (int)MathF.Ceiling((max.X - min.X) / cell) + 3;
        var h = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 3;
        var origin = min - new float2(cell * 1.5f);
        if (_mask.Length < w * h)
            _mask = new bool[w * h];
        Array.Clear(_mask, 0, w * h);

        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            if (!_visible[a] || !_visible[b] || !_visible[c])
                continue;
            Fill((_projected[a] - origin) / cell, (_projected[b] - origin) / cell, (_projected[c] - origin) / cell, w, h);
        }

        // Nothing on the grid's outer ring, so every outline closes.
        for (var x = 0; x < w; x++)
            _mask[x] = _mask[(h - 1) * w + x] = false;
        for (var y = 0; y < h; y++)
            _mask[y * w] = _mask[y * w + w - 1] = false;
        Trace(w, h);
        var breathe = 0.85f + 0.15f * MathF.Sin((float)ImGui.GetTime() * 2.2f);
        draw.PushClipRect(clipMin, clipMax, true);
        var seen = new HashSet<int>();
        foreach (var start in _links.Keys)
        {
            if (!seen.Add(start))
                continue;
            _loop.Clear();
            var (previous, at) = (-1, start);
            while (true)
            {
                _loop.Add(origin + EdgePoint(at, w) * cell);
                var (a, b) = _links[at];
                var next = a != previous ? a : b;
                (previous, at) = (at, next);
                if (at == start || at < 0 || !seen.Add(at))
                    break;
            }

            if (_loop.Count < 6)
                continue;
            Smooth(_loop, _smooth);
            Smooth(_smooth, _loop);
            var line = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_loop);
            draw.AddPolyline(line, color.WithAlpha(0.07f * breathe), ImDrawFlags.Closed, S(7f));
            draw.AddPolyline(line, color.WithAlpha(0.16f * breathe), ImDrawFlags.Closed, S(3.5f));
            draw.AddPolyline(line, color.WithAlpha(0.6f * breathe), ImDrawFlags.Closed, S(1.25f));
        }

        draw.PopClipRect();
    }

    /// Marks the cells whose middles a triangle (in cell units) covers; one smaller than a cell marks the cell it's in.
    private void Fill(float2 a, float2 b, float2 c, int w, int h)
    {
        var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) - 0.5f));
        var x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) - 0.5f));
        var y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - 0.5f));
        var y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - 0.5f));
        if (x0 > x1 || y0 > y1)
            return;
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1f)
        {
            // Tiny or edge-on: just where its middle is.
            var m = (a + b + c) / 3f;
            var (mx, my) = ((int)m.X, (int)m.Y);
            if (MathF.Abs(area) > 1e-4f && mx >= 0 && mx < w && my >= 0 && my < h)
                _mask[my * w + mx] = true;
            return;
        }

        var sign = MathF.Sign(area);
        for (var y = y0; y <= y1; y++)
        {
            var py = y + 0.5f;
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var e0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) * sign;
                var e1 = ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) * sign;
                var e2 = ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) * sign;
                if (e0 >= 0f && e1 >= 0f && e2 >= 0f)
                    _mask[y * w + x] = true;
            }
        }
    }

    // The grid's edges, by number: the one from sample (i, j) rightwards is 2 (j w + i), downwards 2 (j w + i) + 1. The
    // samples are the cells' middles, and the outline crosses an edge halfway along where its ends differ.
    private static int Across(int i, int j, int w) => (j * w + i) * 2;
    private static int Down(int i, int j, int w) => (j * w + i) * 2 + 1;

    private static float2 EdgePoint(int edge, int w)
    {
        var sample = edge >> 1;
        var (i, j) = (sample % w, sample / w);
        return (edge & 1) == 0 ? new float2(i + 1f, j + 0.5f) : new float2(i + 0.5f, j + 1f);
    }

    /// Marching squares: links each edge the outline crosses to the next one along it, two links an edge.
    private void Trace(int w, int h)
    {
        _links.Clear();
        void Link(int a, int b)
        {
            _links[a] = _links.TryGetValue(a, out var l) ? (l.A, b) : (b, -1);
            _links[b] = _links.TryGetValue(b, out var m) ? (m.A, a) : (a, -1);
        }

        for (var j = 0; j + 1 < h; j++)
        {
            for (var i = 0; i + 1 < w; i++)
            {
                var c0 = _mask[j * w + i];
                var c1 = _mask[j * w + i + 1];
                var c2 = _mask[(j + 1) * w + i + 1];
                var c3 = _mask[(j + 1) * w + i];
                var top = Across(i, j, w);
                var right = Down(i + 1, j, w);
                var bottom = Across(i, j + 1, w);
                var left = Down(i, j, w);
                switch ((c0 ? 1 : 0) | (c1 ? 2 : 0) | (c2 ? 4 : 0) | (c3 ? 8 : 0))
                {
                    case 0 or 15:
                        break;
                    case 5:
                        Link(left, top);
                        Link(right, bottom);
                        break;
                    case 10:
                        Link(top, right);
                        Link(bottom, left);
                        break;
                    default:
                        Span<int> crossed = stackalloc int[2];
                        var n = 0;
                        if (c0 != c1)
                            crossed[n++] = top;
                        if (c1 != c2)
                            crossed[n++] = right;
                        if (c2 != c3)
                            crossed[n++] = bottom;
                        if (c3 != c0)
                            crossed[n++] = left;
                        Link(crossed[0], crossed[1]);
                        break;
                }
            }
        }
    }

    /// One pass of Chaikin's corner cutting round a closed line: takes the grid's steps off it.
    private static void Smooth(List<float2> from, List<float2> to)
    {
        to.Clear();
        for (var i = 0; i < from.Count; i++)
        {
            var (a, b) = (from[i], from[(i + 1) % from.Count]);
            to.Add(a * 0.75f + b * 0.25f);
            to.Add(a * 0.25f + b * 0.75f);
        }
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
