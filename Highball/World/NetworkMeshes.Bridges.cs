using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// A bridge's steel: plate girders under the deck with stiffeners down the outer ones, cross frames between them, and a post and rail
/// railing along each side. All in the far mesh, and all lined up with the line's distance so chunks' pieces meet.
public sealed partial class NetworkMeshes
{
    private const float StiffenerGap = 2.4f;
    private const float CrossFrameGap = 7.2f;
    private const float RailPostGap = 2.4f;
    private const float GirderGap = 2.2f;
    private const float RailingHeight = 1.05f;

    /// The width of the deck each side of the centre, how far down its girders reach, and how high its edge stands.
    private static (float Half, float Depth, float Edge) DeckOf(NetworkLine line) => line.Rail
        ? (line.BallastHalfTop + 0.75f, 1.8f, NetworkLine.BallastHeight + 0.1f)
        : (line.Width * 0.5f + 0.55f, 1.4f, 0.3f);

    private static void BridgeSteel(Builder builder, NetworkLine line, int first, int last)
    {
        var (half, depth, edge) = DeckOf(line);
        var girder = Code(LineMaterial.Steel);
        var galvanised = Code(LineMaterial.Steel, 1);
        const float deckBottom = -0.25f;

        // The girders, evenly across, with the outermost just in from the deck's edge.
        var outer = half - 0.25f;
        var count = Math.Max(2, (int)MathF.Ceiling(outer * 2f / GirderGap) + 1);
        var across = new float[count];
        for (var g = 0; g < count; g++)
            across[g] = -outer + outer * 2f * g / (count - 1);

        foreach (var u in across)
            Sweep(builder, line, first, last, IBeam(u, deckBottom, -depth), _ => girder, (_, _, _) => 0f);

        // The railing's rails, a closed square tube each.
        foreach (var side in (float[])[-1f, 1f])
        {
            foreach (var height in (float[])[0.4f, 0.72f, RailingHeight])
                Sweep(builder, line, first, last, Tube(side * (half - 0.08f), edge + height, 0.04f), _ => galvanised, (_, _, _) => 0f);
        }

        void Along(float gap, Action<float3, float3, float3> place)
        {
            for (var s = MathF.Ceiling(line.S[first] / gap) * gap; s < line.S[last]; s += gap)
            {
                var (center, right) = FrameAt(line, s, builder.Origin);
                place(center, right, new float3(right.Z, 0f, -right.X));
            }
        }

        var steel = new float4(0f, 0f, girder, 0f);
        var bright = new float4(0f, 0f, galvanised, 0f);

        // Stiffeners: a flat bar down each face of the outer girders, standing out to the flange's edge.
        Along(StiffenerGap, (center, right, forward) =>
        {
            foreach (var u in (float[])[-outer - 0.11f, -outer + 0.11f, outer - 0.11f, outer + 0.11f])
                Solid(builder, center + right * u, right * 0.1f, forward * 0.018f, center.Y - depth + 0.03f, center.Y + deckBottom - 0.03f, steel);
        });

        // Cross frames between neighbouring girders.
        Along(CrossFrameGap, (center, right, forward) =>
        {
            for (var g = 0; g + 1 < count; g++)
            {
                var middle = (across[g] + across[g + 1]) * 0.5f;
                Solid(builder, center + right * middle, right * (GirderGap * 0.5f), forward * 0.05f, center.Y - depth * 0.75f, center.Y - depth * 0.35f, steel);
            }
        });

        Along(RailPostGap, (center, right, forward) =>
        {
            foreach (var side in (float[])[-1f, 1f])
                Solid(builder, center + right * (side * (half - 0.08f)), right * 0.05f, forward * 0.05f, center.Y + edge, center.Y + edge + RailingHeight + 0.05f, bright);
        });
    }

    /// An I section centred on u, listed round clockwise so the solid's on the right.
    private static (float U, float Y)[] IBeam(float u, float top, float bottom)
    {
        const float flange = 0.2f;
        const float thick = 0.04f;
        const float web = 0.015f;
        return
        [
            (u - flange, top), (u + flange, top), (u + flange, top - thick), (u + web, top - thick), (u + web, bottom + thick), (u + flange, bottom + thick),
            (u + flange, bottom), (u - flange, bottom), (u - flange, bottom + thick), (u - web, bottom + thick), (u - web, top - thick), (u - flange, top - thick), (u - flange, top),
        ];
    }

    private static (float U, float Y)[] Tube(float u, float y, float half) =>
        [(u - half, y + half), (u + half, y + half), (u + half, y - half), (u - half, y - half), (u - half, y + half)];

    /// A box closed top and bottom: center on the ground plane, half extents along right and forward.
    private static void Solid(Builder builder, float3 center, float3 right, float3 forward, float bottom, float top, float4 data)
    {
        Box(builder, center, right, forward, bottom, top, data);
        foreach (var (y, normal) in (ReadOnlySpan<(float, float3)>)[(top, new float3(0f, 1f, 0f)), (bottom, new float3(0f, -1f, 0f))])
        {
            var a = builder.Add(new float3(center.X - right.X - forward.X, y, center.Z - right.Z - forward.Z), normal, data);
            var b = builder.Add(new float3(center.X + right.X - forward.X, y, center.Z + right.Z - forward.Z), normal, data);
            var c = builder.Add(new float3(center.X - right.X + forward.X, y, center.Z - right.Z + forward.Z), normal, data);
            var d = builder.Add(new float3(center.X + right.X + forward.X, y, center.Z + right.Z + forward.Z), normal, data);
            builder.Quad(a, b, c, d);
        }
    }

    /// A wing wall from a to b: thick, level underneath, its top sloping from one height to the other.
    private static void Wing(Builder builder, float3 a, float3 b, float thickness, float bottom, float topA, float topB, float4 data)
    {
        var along = MathEx.Normalize(b - a);
        var side = new float3(-along.Z, 0f, along.X) * (thickness * 0.5f);
        var up = new float3(0f, 1f, 0f);

        void Face(float3 p, float3 q, float pTop, float qTop, float3 normal)
        {
            var v0 = builder.Add(new float3(p.X, bottom, p.Z), normal, data);
            var v1 = builder.Add(new float3(q.X, bottom, q.Z), normal, data);
            var v2 = builder.Add(new float3(p.X, pTop, p.Z), normal, data);
            var v3 = builder.Add(new float3(q.X, qTop, q.Z), normal, data);
            builder.Quad(v0, v1, v2, v3);
        }

        Face(a + side, b + side, topA, topB, MathEx.Normalize(side));
        Face(a - side, b - side, topA, topB, MathEx.Normalize(side * -1f));
        Face(b - side, b + side, topB, topB, along);

        var t0 = builder.Add(new float3(a.X + side.X, topA, a.Z + side.Z), up, data);
        var t1 = builder.Add(new float3(a.X - side.X, topA, a.Z - side.Z), up, data);
        var t2 = builder.Add(new float3(b.X + side.X, topB, b.Z + side.Z), up, data);
        var t3 = builder.Add(new float3(b.X - side.X, topB, b.Z - side.Z), up, data);
        builder.Quad(t0, t1, t2, t3);
    }
}
