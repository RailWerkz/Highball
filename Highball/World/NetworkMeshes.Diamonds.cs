using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Highball.App;
using Highball.Routes;
using RailWerkz.Core.Utils;
using RailWerkz.Vulkan;

namespace Highball.World;

/// The shapes that are built to fit where they land: a diamond's whole assembly and a grade crossing's deck. Nothing here is
/// a fixed model stretched into place; each piece is worked out from the tracks it sits on.
public sealed partial class NetworkMeshes
{
    private const float Flangeway = 1.875f * 0.0254f;

    /// A second rail's centre from its running rail's; how far it runs past the rail crossings; the flangeway's floor.
    private const float SecondRail = 2f * RailHeadHalf + Flangeway;
    private const float SecondTail = 0.65f;
    private const float FillerTop = 0.135f;
    private const float SecondFlare = 0.45f;
    private const float BoltStep = 0.34f;

    private static readonly (float U, float Y)[] SecondSection = [.. RailProfile.Where(p => p.Y >= 0.045f)];

    private static readonly (float U, float Y)[] FillerSection =
    [
        (-Flangeway / 2f - 0.012f, 0f), (-Flangeway / 2f - 0.012f, FillerTop), (Flangeway / 2f + 0.012f, FillerTop), (Flangeway / 2f + 0.012f, 0f),
    ];

    private static readonly (float U, float Y)[] BarSection = [(-0.025f, 0.03f), (-0.025f, 0.125f), (0.025f, 0.125f), (0.025f, 0.03f)];

    private static float3 Cross(float3 a, float3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// A flat face of a solid, whichever way its points run: it's turned to face away from inside.
    private static void Face(Builder builder, ReadOnlySpan<float3> points, float3 inside, float code, float height)
    {
        var normal = Cross(points[1] - points[0], points[2] - points[0]);
        var length = MathF.Sqrt(Dot(normal, normal));
        if (length < 1e-9f)
            return;
        normal *= 1f / length;

        // (The game's front faces wind so that this cross points inward.)
        var outward = Dot(normal, points[0] - inside) >= 0f;
        var facing = outward ? normal : normal * -1f;
        var data = new float4(points[0].X, points[0].X + points[0].Z, code, height);
        var start = (uint)builder.Vertices.Count;
        foreach (var point in points)
            builder.Add(point, facing, data);
        for (var k = 1; k + 1 < points.Length; k++)
        {
            if (outward)
                builder.Indices.AddRange([start, start + (uint)k + 1, start + (uint)k]);
            else
                builder.Indices.AddRange([start, start + (uint)k, start + (uint)k + 1]);
        }
    }

    /// A length of section along a track, from s0 to s1, off to its right; each end cut on its slant (metres along per metre
    /// across) so it runs along the other track.
    private static void SweepAlong(Builder builder, CrossingAssembly.Track track, float off, float s0, float s1, float slant0, float slant1, (float U, float Y)[] section, float code, float flareTo = 0f)
    {
        if (s1 - s0 < 0.002f)
            return;
        var count = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 1f));
        var rings = new float3[count + 1][];
        var bas = NetworkLine.BallastHeight + KitRailBase;
        for (var k = 0; k <= count; k++)
        {
            var t = (float)k / count;
            var slant = slant0 * (1f - t) + slant1 * t;
            var s = s0 + (s1 - s0) * t;
            var extra = flareTo * t;
            rings[k] = new float3[section.Length];
            for (var i = 0; i < section.Length; i++)
            {
                var (u, v) = section[i];
                var (x, z, y, _, _, rx, rz) = track.At(s + u * slant);
                var across = off + extra + u;
                rings[k][i] = new float3((float)(x + rx * across - builder.Origin.X), y + bas + v, (float)(z + rz * across - builder.Origin.Z));
            }
        }

        var inside = new float3(0f, 0f, 0f);
        var n = section.Length;
        for (var k = 0; k < count; k++)
        {
            var middle = new float3(0f, 0f, 0f);
            foreach (var p in rings[k])
                middle += p;
            foreach (var p in rings[k + 1])
                middle += p;
            inside = middle * (1f / (2f * n));
            for (var i = 0; i < n; i++)
            {
                var i2 = (i + 1) % n;
                Face(builder, [rings[k][i], rings[k + 1][i], rings[k + 1][i2], rings[k][i2]], inside, code, MathF.Max(section[i].Y, section[i2].Y));
            }
        }

        // The ends, in strips across the section: it pairs up, one side with the other.
        foreach (var (ring, near) in new[] { (rings[0], inside), (rings[count], inside) })
            for (var i = 0; i + 1 < n / 2; i++)
                Face(builder, [ring[i], ring[i + 1], ring[n - 2 - i], ring[n - 1 - i]], near, code, section[i + 1].Y);
    }

    /// A length along a track with bands of the other's offset taken out: the pieces left, and whether each end is a cut.
    private static List<(float From, float To, bool CutFrom, bool CutTo)> PiecesBetween(CrossingAssembly assembly, int i, float off, float from, float to, (float Lo, float Hi)[] bands)
    {
        var (track, other) = (assembly.Tracks[i], assembly.Tracks[1 - i]);
        var cuts = new List<(float Lo, float Hi)>();
        foreach (var (lo, hi) in bands)
        {
            var a = CrossingAssembly.Along(track, other, off, lo, assembly.Middle[i], assembly.Middle[1 - i]);
            var b = CrossingAssembly.Along(track, other, off, hi, assembly.Middle[i], assembly.Middle[1 - i]);
            cuts.Add((MathF.Min(a, b), MathF.Max(a, b)));
        }

        cuts.Sort((p, q) => p.Lo.CompareTo(q.Lo));
        var pieces = new List<(float, float, bool, bool)>();
        var (at, cutAt) = (from, false);
        foreach (var (lo, hi) in cuts)
        {
            if (lo > at)
                pieces.Add((at, MathF.Min(lo, to), cutAt, lo < to));
            if (hi > at)
                (at, cutAt) = (hi, true);
        }

        if (at < to)
            pieces.Add((at, to, cutAt, false));
        return pieces;
    }

    private static float Slant(CrossingAssembly.Track track, CrossingAssembly.Track other, float s, float t)
    {
        var (_, _, _, fx, fz, rx, rz) = track.At(s);
        var (_, _, _, _, _, ox, oz) = other.At(t);
        var along = fx * ox + fz * oz;
        return MathF.Abs(along) < 0.05f ? 0f : -(rx * ox + rz * oz) / along;
    }

    private static void Bolt(Builder builder, float3 centre, float3 axis, float radius, float length, float code)
    {
        var up = MathF.Abs(axis.Y) > 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f);
        var v = MathEx.Normalize(Cross(axis, up));
        var u = Cross(v, axis);
        float3 At(int k, float along, float r)
        {
            var t = MathF.PI / 3f * k;
            return centre + u * (MathF.Cos(t) * r) + v * (MathF.Sin(t) * r) + axis * along;
        }

        var inside = centre + axis * (length * 0.5f);
        var thread = radius * 0.45f;
        for (var k = 0; k < 6; k++)
        {
            Face(builder, [At(k, 0f, radius), At(k + 1, 0f, radius), At(k + 1, length, radius), At(k, length, radius)], inside, code, 0.1f);
            Face(builder, [At(k, length, thread), At(k + 1, length, thread), At(k + 1, length + 0.018f, thread), At(k, length + 0.018f, thread)], centre + axis * (length + 0.009f), code, 0.1f);
        }

        Face(builder, [At(0, length, radius), At(1, length, radius), At(2, length, radius), At(3, length, radius), At(4, length, radius), At(5, length, radius)], inside, code, 0.1f);
    }

    private static void Block(Builder builder, ReadOnlySpan<(double X, double Z)> corners, float y0, float y1, float code, float3 origin)
    {
        var n = corners.Length;
        Span<float3> low = stackalloc float3[n];
        Span<float3> high = stackalloc float3[n];
        var middle = new float3(0f, 0f, 0f);
        for (var k = 0; k < n; k++)
        {
            low[k] = new float3((float)(corners[k].X - origin.X), y0, (float)(corners[k].Z - origin.Z));
            high[k] = low[k] with { Y = y1 };
            middle += low[k] + high[k];
        }

        var inside = middle * (1f / (2f * n));
        Face(builder, low, inside, code, 0f);
        Face(builder, high, inside, code, 0f);
        for (var k = 0; k < n; k++)
            Face(builder, [low[k], low[(k + 1) % n], high[(k + 1) % n], high[k]], inside, code, 0f);
    }

    /// A diamond's whole bolted assembly, laid out on the two tracks as they really run.
    private static void DiamondBuild(Builder builder, CrossingAssembly c)
    {
        var code = Code(LineMaterial.Rail);
        var g = Turnout.RailOffset;
        var o = builder.Origin;
        (float Lo, float Hi)[] channels = [(-(g - RailHeadHalf), -(g - RailHeadHalf) + Flangeway), ((g - RailHeadHalf) - Flangeway, g - RailHeadHalf)];
        (float Lo, float Hi)[] railBands = [(-g - 0.11f, -g + 0.055f + SecondRail), (g - 0.055f - SecondRail, g + 0.11f)];
        var bas = c.Y + NetworkLine.BallastHeight + KitRailBase;

        for (var i = 0; i < 2; i++)
        {
            var (track, other) = (c.Tracks[i], c.Tracks[1 - i]);
            var (from, to) = c.Range[i];
            var (innerFrom, innerTo) = (MathF.Max(from + Diamonds.Tail - SecondTail, from), MathF.Min(to - Diamonds.Tail + SecondTail, to));
            var slant = Slant(track, other, c.Middle[i], c.Middle[1 - i]);
            foreach (var side in (int[])[-1, 1])
            {
                var off = side * g;
                foreach (var (s0, s1, cut0, cut1) in PiecesBetween(c, i, off, from, to, channels))
                    SweepAlong(builder, track, off, s0, s1, cut0 ? slant : 0f, cut1 ? slant : 0f, RailProfile, code);

                var inner = off - side * SecondRail;
                foreach (var (s0, s1, cut0, cut1) in PiecesBetween(c, i, inner, innerFrom, innerTo, channels))
                    SweepAlong(builder, track, inner, s0, s1, cut0 ? slant : 0f, cut1 ? slant : 0f, SecondSection, code);

                // Flaring away at each end.
                if (innerTo + SecondFlare < to)
                    SweepAlong(builder, track, inner, innerTo, innerTo + SecondFlare, 0f, 0f, SecondSection, code, -side * 0.07f);
                if (innerFrom - SecondFlare > from)
                    SweepAlong(builder, track, inner - side * 0.07f, innerFrom - SecondFlare, innerFrom, 0f, 0f, SecondSection, code, side * 0.07f);

                SweepAlong(builder, track, off - side * (SecondRail / 2f), innerFrom, innerTo, 0f, 0f, FillerSection, code);

                // The heavy bar along the rail's outer web, either side of the other track's rails.
                var outer = off + side * 0.034f;
                foreach (var (s0, s1, cut0, cut1) in PiecesBetween(c, i, outer, from + 0.05f, to - 0.05f, railBands))
                    SweepAlong(builder, track, outer, s0, s1, cut0 ? slant : 0f, cut1 ? slant : 0f, BarSection, code);

                // Bolts through it all, nuts on the far side, a brace on every other one.
                var crossings = ((float[])[-g, g]).Select(v => CrossingAssembly.Along(track, other, off, v, c.Middle[i], c.Middle[1 - i])).ToArray();
                var count = (int)((innerTo - innerFrom) * 0.5f / BoltStep);
                var centre = (innerFrom + innerTo) * 0.5f;
                for (var k = -count; k <= count; k++)
                {
                    var s = centre + k * BoltStep;
                    if (crossings.Any(x => MathF.Abs(s - x) < 0.16f + 0.12f * MathF.Abs(slant)))
                        continue;
                    var (x0, z0, y, _, _, rx, rz) = track.At(s);
                    var axis = new float3(rx * side, 0f, rz * side);
                    var height = y + NetworkLine.BallastHeight + KitRailBase + 0.085f;
                    float3 At(float across) => new((float)(x0 + rx * across - o.X), height, (float)(z0 + rz * across - o.Z));
                    Bolt(builder, At(off + side * 0.059f), axis, 0.026f, 0.032f, code);
                    Bolt(builder, At(inner - side * 0.0088f), axis * -1f, 0.024f, 0.032f, code);
                    if (k % 2 == 0)
                    {
                        var brace = y + NetworkLine.BallastHeight + KitRailBase;
                        Block(builder, [track.On(s - 0.06f, off + side * 0.058f), track.On(s + 0.06f, off + side * 0.058f), track.On(s + 0.06f, off + side * 0.13f), track.On(s - 0.06f, off + side * 0.13f)], brace + 0.012f, brace + 0.045f, code, o);
                    }
                }
            }
        }

        // The castings, laid out by distance off each track: each corner's a plate round a pocket that shows the ballast, ribs
        // round its edge; bars from corner to corner under the rails, with a web along the middle's edge.
        var (bottom, top, rib, web) = (c.Y + NetworkLine.BallastHeight + 0.012f, bas + 0.012f, bas + 0.06f, bas + 0.1f);
        var shrink = MathF.Max(MathF.Sin(c.Degrees * MathF.PI / 180f), 0.3f);
        var (inside, outside, pocket0, pocket1) = (g - 0.25f, g + 0.46f, g + 0.15f, g + 0.38f);
        var arm = 0.55f * shrink + 0.1f;
        void Plate(float a0, float a1, float b0, float b1, float y0, float y1) =>
            Block(builder, [c.Place(a0, b0), c.Place(a1, b0), c.Place(a1, b1), c.Place(a0, b1)], y0, y1, code, o);
        void Stud(float a, float b, float y, float radius, float length)
        {
            var (x, z) = c.Place(a, b);
            Bolt(builder, new float3((float)(x - o.X), y, (float)(z - o.Z)), new float3(0f, 1f, 0f), radius, length, code);
        }

        foreach (var sa in (float[])[-1f, 1f])
        {
            foreach (var sb in (float[])[-1f, 1f])
            {
                void Box(float a0, float a1, float b0, float b1, float y0, float y1) => Plate(sa * a0, sa * a1, sb * b0, sb * b1, y0, y1);
                Box(inside, pocket0, inside, outside, bottom, top);
                Box(pocket1, outside, inside, outside, bottom, top);
                Box(pocket0, pocket1, inside, pocket0, bottom, top);
                Box(pocket0, pocket1, pocket1, outside, bottom, top);
                Box(outside - 0.035f, outside, inside, outside, top, rib);
                Box(inside, outside - 0.035f, outside - 0.035f, outside, top, rib);
                Box(pocket0 - 0.02f, pocket0 + 0.015f, pocket0, pocket1, top, rib);
                Box(pocket0, pocket1, pocket0 - 0.02f, pocket0 + 0.015f, top, rib);
                Box(g - 0.2f, g + 0.14f, outside, outside + arm, bottom, top);
                Box(outside, outside + arm, g - 0.2f, g + 0.14f, bottom, top);
                foreach (var (a, b) in ((float, float)[])[(pocket1 + 0.04f, inside + 0.1f), (pocket1 + 0.04f, pocket0 + 0.04f), (pocket0 - 0.05f, outside - 0.06f), (inside + 0.1f, pocket1 + 0.04f), (pocket1 + 0.04f, outside - 0.05f), (pocket0 + 0.1f, pocket1 + 0.04f), (pocket0 + 0.1f, pocket0 - 0.05f)])
                    Stud(sa * a, sb * b, top, 0.022f, 0.02f);
            }

            Plate(sa * (g - 0.25f), sa * (g + 0.12f), -inside, inside, bottom, top);
            Plate(-inside, inside, sa * (g - 0.25f), sa * (g + 0.12f), bottom, top);
            Plate(sa * (g - 0.25f), sa * (g - 0.205f), -inside, inside, top, web);
            Plate(-inside, inside, sa * (g - 0.25f), sa * (g - 0.205f), top, web);
            for (var k = -2; k <= 2; k++)
            {
                var t = k * inside / 2.6f;
                Stud(sa * (g - 0.23f), t, web, 0.02f, 0.016f);
                Stud(t, sa * (g - 0.23f), web, 0.02f, 0.016f);
            }
        }
    }

    // --- A grade crossing's deck. ---

    /// Tracks this near one another along the road are one crossing.
    private const float CrossingTrackGap = 12f;
    private const float CrossingSearch = 40f;
    private const float CrossingOuter = 1.2f;
    private const float CrossingRamp = 6f;
    private const float CrossingMastBack = 3f;
    private const float CrossingRowStep = 1.5f;

    /// One quad of a deck, relative to the crossing's place, and whether it's a side (else it faces up).
    private readonly record struct DeckQuad(float3 A, float3 B, float3 C, float3 D, float3 Inside, bool Shoulder = false);

    /// The road a deck's a part of: what its surface is, and how a place on the deck maps to the road's own coordinates across and along.
    private readonly record struct DeckRoad(float Code, float3 Across, float AcrossAt, float3 Along, float AlongAt);

    /// What a grade crossing's made of: its deck, and its masts in world metres.
    private sealed record CrossingDeck(List<DeckQuad> Quads, (double X, double Z, float Y, float3 Face)[] Masts, DeckRoad? Road = null, (int Line, float From, float To)? Gap = null);

    /// The road running along a crossing here, if there is one: the nearest place on it to (x, z), and the way it runs there.
    private (NetworkLine Line, int Segment, float T, double X, double Z, double Dx, double Dz)? RoadAlong(double x, double z, double dx, double dz)
    {
        (NetworkLine, int, float, double, double, double, double)? found = null;
        var best = 12.0 * 12.0;
        foreach (var road in _lines.Lines)
        {
            if (road.Rail || road.Mark || x < road.MinX - 12.0 || x > road.MaxX + 12.0 || z < road.MinZ - 12.0 || z > road.MaxZ + 12.0)
                continue;
            for (var i = 0; i + 1 < road.Count; i++)
            {
                var (sx, sz) = (road.X[i + 1] - road.X[i], road.Z[i + 1] - road.Z[i]);
                var length = Math.Max(Math.Sqrt(sx * sx + sz * sz), 1e-6);
                var (ux, uz) = (sx / length, sz / length);
                if (Math.Abs(ux * dx + uz * dz) < 0.9)
                    continue;
                var t = Math.Clamp((x - road.X[i]) * ux + (z - road.Z[i]) * uz, 0.0, length);
                var (px, pz) = (road.X[i] + ux * t, road.Z[i] + uz * t);
                var away = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (away < best)
                    (best, found) = (away, (road, i, (float)(t / length), px, pz, ux, uz));
            }
        }

        return found;
    }

    /// The rail lines the road's axis crosses near the place, as far as they run on side by side.
    private List<(NetworkLine Line, float S, float T)> TracksAcross(double x, double z, double dx, double dz)
    {
        var hits = new List<(NetworkLine Line, float S, float T)>();
        foreach (var line in _lines.Lines)
        {
            if (!line.Rail || line.Disused || x < line.MinX - CrossingSearch || x > line.MaxX + CrossingSearch || z < line.MinZ - CrossingSearch || z > line.MaxZ + CrossingSearch)
                continue;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var (sx, sz) = (line.X[i + 1] - line.X[i], line.Z[i + 1] - line.Z[i]);
                var denominator = dx * sz - dz * sx;
                if (Math.Abs(denominator) < 1e-9)
                    continue;
                var (qx, qz) = (line.X[i] - x, line.Z[i] - z);
                var t = (qx * sz - qz * sx) / denominator;
                var u = (qx * dz - qz * dx) / denominator;
                if (u < 0.0 || u >= 1.0 || Math.Abs(t) > CrossingSearch)
                    continue;
                var length = Math.Sqrt(sx * sx + sz * sz);
                if (Math.Abs(denominator) / length < 0.25)
                    continue;
                hits.Add((line, line.S[i] + (line.S[i + 1] - line.S[i]) * (float)u, (float)t));
            }
        }

        if (hits.Count == 0)
            return hits;
        hits.Sort((a, b) => a.T.CompareTo(b.T));
        var near = hits.FindIndex(h => MathF.Abs(h.T) == hits.Min(q => MathF.Abs(q.T)));
        var (low, high) = (near, near);
        while (low > 0 && hits[low].T - hits[low - 1].T <= CrossingTrackGap)
            low--;
        while (high + 1 < hits.Count && hits[high + 1].T - hits[high].T <= CrossingTrackGap)
            high++;
        return hits.GetRange(low, high - low + 1);
    }

    /// The level to run a crossing's ramp down to: a road's surface, if a road's there, or else the ground.
    private float RampEnd(double x, double z)
    {
        var (best, found) = (9.0 * 9.0, (float?)null);
        foreach (var road in _lines.Lines)
        {
            if (road.Rail || x < road.MinX - 9.0 || x > road.MaxX + 9.0 || z < road.MinZ - 9.0 || z > road.MaxZ + 9.0)
                continue;
            for (var i = 0; i < road.Count; i++)
            {
                var away = (road.X[i] - x) * (road.X[i] - x) + (road.Z[i] - z) * (road.Z[i] - z);
                if (away < best)
                    (best, found) = (away, road.Y[i] + RoadRise);
            }
        }

        return (found ?? _source.HeightAt(x, z)) + 0.015f;
    }

    /// The deck across every track the road meets here: level with the rail tops from outside the first track to outside the
    /// last, gaps only where the rails and their flanges run, a ramp down each end, its sides closed.
    private CrossingDeck Deck(PlacedObject o, double cx, double cz)
    {
        var angle = o.Heading * Math.PI / 180.0;
        var (dx, dz) = (Math.Cos(angle), Math.Sin(angle));
        // On a road it's a part of the road: as wide as it, on its middle, and going its way.
        var (baseX, baseZ) = (cx, cz);
        var onRoad = RoadAlong(cx, cz, dx, dz);
        if (onRoad is { } along)
        {
            var sign = along.Dx * dx + along.Dz * dz < 0.0 ? -1.0 : 1.0;
            (dx, dz) = (along.Dx * sign, along.Dz * sign);
            var aside = (along.X - cx) * -dz + (along.Z - cz) * dx;
            (cx, cz) = (cx + -dz * aside, cz + dx * aside);
        }

        var (nx, nz) = (-dz, dx);
        var (shiftX, shiftZ) = ((float)(cx - baseX), (float)(cz - baseZ));
        var across = TracksAcross(cx, cz, dx, dz);
        var quads = new List<DeckQuad>();
        if (across.Count == 0)
            return new CrossingDeck(quads, []);

        var width = onRoad?.Line.Width ?? o.Width;
        var half = width * 0.5f;
        var rows = Math.Max(2, (int)MathF.Ceiling(width / CrossingRowStep));
        var g = Turnout.RailOffset;
        var flange = RailHeadHalf + 0.055f;
        var railTop = NetworkLine.BallastHeight + KitRailBase + RailTop - 0.004f;

        // Along each row: where each rail crosses it, by distance along the road, with its height and how square it crosses.
        var railRows = new List<List<(float T, float Y, float Sine)>>();
        foreach (var k in Enumerable.Range(0, rows + 1))
        {
            var w = -half + width * k / rows;
            var (ox, oz) = (cx + nx * w, cz + nz * w);
            var rails = new List<(float T, float Y, float Sine)>();
            foreach (var (line, hit, _) in across)
            {
                var (leg, s) = (new Leg(line, 0, 1), hit);
                for (var step = 0; step < 4; step++)
                {
                    var (px, pz, _, fx, fz, _, _) = leg.At(s);
                    var slope = fx * nx + fz * nz;
                    if (Math.Abs(slope) < 0.1)
                        slope = Math.Sign(slope + 1e-6) * 0.1;
                    s -= (float)(((px - ox) * nx + (pz - oz) * nz) / slope);
                }

                var (qx, qz, y, gx, gz, _, _) = leg.At(s);
                rails.Add(((float)((qx - ox) * dx + (qz - oz) * dz), y + railTop, MathF.Max((float)Math.Abs(gx * nx + gz * nz), 0.25f)));
            }

            rails.Sort((a, b) => a.T.CompareTo(b.T));
            railRows.Add(rails);
        }

        float LevelLow(List<(float T, float Y, float Sine)> rails) => rails[0].T - (g + CrossingOuter) / rails[0].Sine;
        float LevelHigh(List<(float T, float Y, float Sine)> rails) => rails[^1].T + (g + CrossingOuter) / rails[^1].Sine;

        // Every row's ramps start and end at the same distance along the road, so the deck's ends lie square across it, as the
        // stretch of road it takes the place of is cut. (On a skewed crossing a row's ramp is the longer the further its level begins.)
        var rampLow = railRows.Min(LevelLow) - CrossingRamp;
        var rampHigh = railRows.Max(LevelHigh) + CrossingRamp;

        // Along each row: every edge's distance along the road, and its height.
        var edges = new List<(float T, float Y)[]>();
        foreach (var k in Enumerable.Range(0, rows + 1))
        {
            var w = -half + width * k / rows;
            var (ox, oz) = (cx + nx * w, cz + nz * w);
            var rails = railRows[k];
            var row = new List<(float T, float Y)>();
            var (first, last) = (rails[0], rails[^1]);
            var (levelLow, levelHigh) = (LevelLow(rails), LevelHigh(rails));
            row.Add((rampLow, RampEnd(ox + dx * rampLow, oz + dz * rampLow)));
            row.Add((levelLow, first.Y));
            foreach (var (t, y, sine) in rails)
                foreach (var sign in (float[])[-1f, 1f])
                {
                    var at = t + sign * g / sine;
                    row.Add((at - flange / sine, y));
                    row.Add((at + flange / sine, y));
                }

            row.Add((levelHigh, last.Y));
            row.Add((rampHigh, RampEnd(ox + dx * rampHigh, oz + dz * rampHigh)));
            edges.Add([.. row]);
        }

        float3 Point(int k, int e, float drop = 0f)
        {
            var w = -half + width * k / rows;
            var (t, y) = edges[k][e];
            return new float3((float)(nx * w + dx * t) + shiftX, y - drop, (float)(nz * w + dz * t) + shiftZ);
        }

        // Every second gap between edges is a panel: ramp, level, then between each pair of gaps.
        var count = edges[0].Length;
        for (var k = 0; k < rows; k++)
            for (var e = 0; e + 1 < count; e++)
            {
                // Edges 0-1 ramp, 1-2 level to the first rail, then pairs round each rail are its gap.
                var gap = e >= 2 && e < count - 3 && (e - 2) % 2 == 0;
                if (gap)
                    continue;
                var (a, b, c, d) = (Point(k, e), Point(k, e + 1), Point(k + 1, e + 1), Point(k + 1, e));
                var middle = (a + b + c + d) * 0.25f;
                quads.Add(new DeckQuad(a, b, c, d, middle - new float3(0f, 1f, 0f)));
            }

        // The sides: on a road, a gravel shoulder sloping away like its own; otherwise straight down to the ballast.
        foreach (var (k, toward) in new[] { (0, 1f), (rows, -1f) })
            for (var e = 0; e + 1 < count; e++)
            {
                var (a, b) = (Point(k, e), Point(k, e + 1));
                var (c, d) = (Point(k, e + 1, 0.7f), Point(k, e, 0.7f));
                if (onRoad is not null)
                {
                    var outward = new float3((float)(nx * toward), 0f, (float)(nz * toward)) * -1.2f;
                    (c, d) = (c + outward, d + outward);
                }

                var middle = (a + b + c + d) * 0.25f + new float3((float)(nx * toward), 0f, (float)(nz * toward));
                quads.Add(new DeckQuad(a, b, c, d, middle, onRoad is not null));
            }

        // A mast each end, back from the deck on the right of the road's traffic coming to it.
        var centre = edges[rows / 2];
        var masts = new List<(double X, double Z, float Y, float3 Face)>();
        foreach (var (sign, t) in new[] { (1f, centre[count - 2].T + CrossingMastBack), (-1f, centre[1].T - CrossingMastBack) })
        {
            var toward = new float3((float)dx * sign, 0f, (float)dz * sign);
            var right = new float3(toward.Z, 0f, -toward.X);
            var (mx, mz) = (cx + dx * t + right.X * (half + 1.5), cz + dz * t + right.Z * (half + 1.5));
            masts.Add((mx, mz, _source.HeightAt(mx, mz), toward));
        }

        DeckRoad? road = null;
        (int, float, float)? stretch = null;
        if (onRoad is { } on)
        {
            // The stretch of the road the deck takes the place of, from one ramp's end to the other's.
            var footS = on.Line.S[on.Segment] + (on.Line.S[on.Segment + 1] - on.Line.S[on.Segment]) * on.T;
            var footT = (float)((on.X - cx) * dx + (on.Z - cz) * dz);
            var runsSign = on.Dx * dx + on.Dz * dz < 0.0 ? -1f : 1f;
            var (s0, s1) = (footS + runsSign * (centre[0].T - footT), footS + runsSign * (centre[^1].T - footT));
            stretch = (on.Line.Index, MathF.Min(s0, s1), MathF.Max(s0, s1));
            var (line, segment) = (on.Line, on.Segment);
            var marks = line.Unpaved ? 0 : (RoadClass)line.Class switch { RoadClass.Highway => 3, RoadClass.Major => 2, RoadClass.Minor => 1, _ => 0 };
            // The road's across is to its right as it runs.
            var (ax, az) = (-on.Dz, on.Dx);
            var (runs, sideways) = (new float3((float)on.Dx, 0f, (float)on.Dz), new float3((float)ax, 0f, (float)az));
            var foot = new float3((float)(on.X - baseX), 0f, (float)(on.Z - baseZ));
            road = new DeckRoad(RoadSurface(line, marks), sideways, Dot(foot, sideways), runs, line.S[segment] + (line.S[segment + 1] - line.S[segment]) * on.T - Dot(foot, runs));
        }

        return new CrossingDeck(quads, [.. masts], road, stretch);
    }
}
