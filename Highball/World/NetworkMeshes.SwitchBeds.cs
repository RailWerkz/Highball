using System.Runtime.CompilerServices;
using Brutal.Numerics;

namespace Highball.World;

/// A switch's ballast as one surface: across its footprint, from the point of switch to where the two routes' beds part,
/// a single grid whose height everywhere is the higher of the two routes' own bed profiles there (each from its own
/// height, bed and sink), so the bed runs on unbroken from one route to the other whatever their heights, with nothing
/// laid over or pushed into anything else. The routes' own beds stop where it starts and start again where it stops,
/// meeting its edges exactly.
///
/// Each row runs across the normal route to the middle between the two, then on across the reverse route square to it,
/// so the grid's last row is each route's own section where its own bed takes over. The middle is where the surface
/// changes from the normal route's texture frame to the reverse route's, so each route's stones and the oil under it run
/// on unbroken into its own bed.
public sealed partial class NetworkMeshes
{
    /// Rows along the switch; across each half, the points of the beds' own section (so the grid meets their sections
    /// point for point where they take over) and TopSteps more across the top.
    private const float SwitchBedStep = 0.5f;
    private const int BedCurve = 8;
    private const int TopSteps = 12;
    private const int HalfColumns = 1 + BedCurve + TopSteps;

    /// The normal half's offsets toward the reverse route: the far side's section points, then the top on to the middle.
    private static float NormalColumn(int c, BedShape shape, float run, float middle) => c switch
    {
        0 => -(shape.Half + run + BedTapers),
        1 => -(shape.Half + run),
        _ when c < 2 + BedCurve => -(shape.Half + run * (BedCurve - 1 - (c - 2)) / BedCurve),
        _ => -shape.Half + (middle + shape.Half) * (c - 1 - BedCurve) / TopSteps,
    };

    /// The reverse half's offsets away from the normal route: the top from the middle, then the far side's section points.
    private static float ReverseColumn(int c, BedShape shape, float run, float middle) => c switch
    {
        _ when c <= TopSteps => middle + (shape.Half - middle) * c / TopSteps,
        _ when c < TopSteps + BedCurve => shape.Half + run * (c - TopSteps) / BedCurve,
        _ when c == TopSteps + BedCurve => shape.Half + run,
        _ => shape.Half + run + BedTapers,
    };

    // As the routes' own beds (FarRail's Section).
    private const float BedWalk = 0.02f;
    private const float BedTapers = 0.6f;

    private static readonly ConditionalWeakTable<Turnout, SwitchBedEnds> SwitchBeds = new();

    /// Where a switch's surface ends on each route (leg distances), or Ok false if it has none (on a bridge, say) and its
    /// routes' beds are laid as before.
    private sealed record SwitchBedEnds(bool Ok, float Normal, float Reverse);

    private static SwitchBedEnds SwitchBedOf(Turnout t) => SwitchBeds.GetValue(t, FindSwitchBed);

    private static SwitchBedEnds FindSwitchBed(Turnout t)
    {
        var none = new SwitchBedEnds(false, 0f, 0f);
        var end = MathF.Min(t.BallastNormal, t.Normal.Length - 0.25f);
        if (end < 2f || t.Normal.Line.Embedded || t.Reverse.Line.Embedded)
            return none;

        var (mid, _, _) = Middle(t, end);
        if (Turnout.Offset(t.Reverse, mid.X, mid.Z) is not { } foot || foot.S > t.Reverse.Length - 0.25f)
            return none;

        static bool Ground(Leg leg, float to)
        {
            var (a, b) = (leg.LineS(0f), leg.LineS(to));
            var (from, until) = (MathF.Min(a, b), MathF.Max(a, b));
            var line = leg.Line;
            for (var p = 0; p + 1 < line.Count; p++)
                if (line.S[p + 1] > from && line.S[p] < until && line.Structures[p] != Structure.Ground)
                    return false;
            return true;
        }

        return Ground(t.Normal, end) && Ground(t.Reverse, foot.S) ? new SwitchBedEnds(true, end, foot.S) : none;
    }

    /// The line distances a route's own bed leaves to its switches' surfaces.
    private static List<(float From, float To)> SwitchBedCuts(NetworkLine line)
    {
        var cuts = new List<(float, float)>();
        foreach (var zone in line.TurnoutZones)
        {
            var ends = SwitchBedOf(zone.Turnout);
            if (!ends.Ok)
                continue;
            var leg = zone.Normal ? zone.Turnout.Normal : zone.Turnout.Reverse;
            var (a, b) = (leg.LineS(0f), leg.LineS(zone.Normal ? ends.Normal : ends.Reverse));
            cuts.Add((MathF.Min(a, b), MathF.Max(a, b)));
        }

        return cuts;
    }

    /// The point halfway between the routes at s along the normal route, on its square, and how far apart they are there.
    private static ((double X, double Z) Point, float Spread, (float X, float Z) Toward) Middle(Turnout t, float s)
    {
        var (x, z, _, _, _, rx, rz) = t.Normal.At(s);
        var spread = Turnout.Offset(t.Reverse, x, z) is { } at ? MathF.Max(0f, -at.Offset * t.Side) : t.SpreadAt(s);
        var (tx, tz) = (rx * t.Side, rz * t.Side);
        return ((x + tx * spread * 0.5, z + tz * spread * 0.5), spread, (tx, tz));
    }

    /// A route's bed shape: its top half-width, the run of its slopes, how far it fades out, all as FarRail lays it.
    private readonly record struct BedShape(float Half, float FadeOut)
    {
        public static BedShape Of(NetworkLine line) => line.Bed == TrackBed.Sunken
            ? new BedShape(line.BallastHalfTop - 0.7f, NetworkMeshes.Fading * 1.4f)
            : new BedShape(line.BallastHalfTop, NetworkMeshes.Fading);

        public static float Run(float sink) =>
            MathF.Max(0.35f, (NetworkLine.BallastHeight - sink - BedWalk) * NetworkLine.BallastSlope * NetworkLine.BedRun);

        /// The bed's height over the line's own height, d metres out from its middle.
        public float At(float d, float sink)
        {
            const float h = NetworkLine.BallastHeight;
            d = MathF.Abs(d);
            var toe = sink + BedWalk;
            var run = Run(sink);
            if (d <= Half)
                return h;
            if (d < Half + run)
            {
                var eased = MathF.Pow((d - Half) / run, 0.72f);
                return toe + (h - toe) * (1f - eased * eased * (3f - 2f * eased));
            }

            var under = sink - MathF.Min(sink * 0.3f, 0.06f) - 0.03f;
            return d < Half + run + BedTapers ? toe + (under - toe) * (d - Half - run) / BedTapers : under;
        }

    }

    /// A route's bed in the world at (x, z): its height there (its own height at the nearest point plus its profile),
    /// how far out from its middle and along it that is, and its top there.
    private static (float Height, float Out, float Along, float Top)? BedAt(Leg leg, BedShape shape, double x, double z)
    {
        if (Turnout.Offset(leg, x, z) is not { } at)
            return null;
        var (_, _, y, _, _, _, _) = leg.At(at.S);
        return (y + shape.At(at.Offset, leg.Line.SinkAt(leg.LineS(at.S))), at.Offset, at.S, y + NetworkLine.BallastHeight);
    }

    /// How far out from a track's middle its ties reach, and a little more: no other bed may stand over them.
    private const float TieClear = TieLength * 0.5f + 0.08f;

    /// How far a switch's surface sinks out of sight where another switch's surface lies over the same ground (the two
    /// switches of a crossover share the track between them): each place has one owner, the surface it's deeper inside,
    /// and the other drops under it, easing down over the first half metre in so the two never meet at the same height.
    private const float YieldDepth = 0.05f;
    private const float YieldEase = 0.5f;

    /// How far inside a switch's surface (x, z) lies, in metres; at or below zero, outside it.
    private static float DepthIn(Turnout t, double x, double z)
    {
        var ends = SwitchBedOf(t);
        if (!ends.Ok || Turnout.Offset(t.Normal, x, z) is not { } n || Turnout.Offset(t.Reverse, x, z) is not { } r)
            return 0f;
        var along = MathF.Min(MathF.Min(n.S, ends.Normal - n.S), ends.Reverse - r.S);
        var (shapeN, shapeR) = (BedShape.Of(t.Normal.Line), BedShape.Of(t.Reverse.Line));
        var reachN = shapeN.Half + BedShape.Run(t.Normal.Line.SinkAt(t.Normal.LineS(n.S))) + BedTapers;
        var reachR = shapeR.Half + BedShape.Run(t.Reverse.Line.SinkAt(t.Reverse.LineS(r.S))) + BedTapers;
        var (a, b) = (n.Offset * t.Side, r.Offset * t.Side);
        var across = MathF.Max(MathF.Min(a + reachN, -b), MathF.Min(reachR - b, a));
        return MathF.Min(along, across);
    }

    /// Other switches whose surfaces may share ground with this one's.
    private static List<Turnout> Neighbours(Turnout t, IReadOnlyList<Turnout> all)
    {
        var reach = SwitchBedOf(t).Normal + 60f;
        return all.Where(o => o != t && SwitchBedOf(o).Ok && (o.X - t.X) * (o.X - t.X) + (o.Z - t.Z) * (o.Z - t.Z) < reach * reach * 4.0).ToList();
    }

    private void SwitchBed(Builder builder, Turnout t, IReadOnlyList<Turnout> all)
    {
        var ends = SwitchBedOf(t);
        if (!ends.Ok)
            return;
        var neighbours = Neighbours(t, all);

        var (normal, reverse, side) = (t.Normal, t.Reverse, t.Side);
        var (shapeN, shapeR) = (BedShape.Of(normal.Line), BedShape.Of(reverse.Line));
        var origin = builder.Origin;
        float Ballast(NetworkLine line) => Code(LineMaterial.Ballast, line.Tracks + (line.Disused ? 8 : 0) + (line.BedHeight < 0.12f ? 16 : 0));
        var (ballastN, ballastR, gravel) = (Ballast(normal.Line), Ballast(reverse.Line), Code(LineMaterial.Gravel));
        var railOffset = normal.Line.Gauge * 0.5f + RailHeadHalf;

        var rows = new List<float>();
        for (var s = 0f; s < ends.Normal - 0.05f; s += SwitchBedStep)
            rows.Add(s);
        rows.Add(ends.Normal);

        // Each row: the normal half's points (across from its far side to the middle) and the reverse half's (from the
        // middle out past the reverse route's far side), each with its texture frame (u across in its line's own sense,
        // v along).
        const int count = HalfColumns + 1;
        var positions = new (double X, double Z, float Y)[rows.Count, count * 2];
        var frames = new (float U, float V)[rows.Count, count * 2];
        var edges = new (double X, double Z, float Y, float U, float V)[rows.Count, 4];

        // The higher of the two beds, but never over either track's ties (each lies at its own top): under the normal
        // route's long timbers, out under the reverse route as far as they go, the normal route's top holds.
        float Higher(double x, double z)
        {
            var n = BedAt(normal, shapeN, x, z);
            var v = BedAt(reverse, shapeR, x, z);
            var height = MathF.Max(n?.Height ?? float.MinValue, v?.Height ?? float.MinValue);
            if (n is { } a)
            {
                var toward = a.Out * side;
                var timbers = a.Along <= t.TimberEnd ? t.SpreadAt(a.Along) : 0f;
                if (toward >= -TieClear && toward <= timbers + TieClear)
                    height = MathF.Min(height, a.Top);
            }

            if (v is { } b && MathF.Abs(b.Out) <= TieClear)
                height = MathF.Min(height, b.Top);

            // Where another switch's surface owns the ground, this one sinks under it.
            if (neighbours.Count > 0)
            {
                var own = DepthIn(t, x, z);
                foreach (var o in neighbours)
                {
                    var theirs = DepthIn(o, x, z);
                    if (theirs > 0f && (theirs > own || (theirs == own && o.Node < t.Node)))
                    {
                        height -= YieldDepth * Math.Clamp(theirs / YieldEase, 0f, 1f);
                        break;
                    }
                }
            }

            return height;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var s = rows[r];
            var (nx, nz, ny, _, _, _, _) = normal.At(s);
            var (mid, spread, toward) = Middle(t, s);
            var sinkN = normal.Line.SinkAt(normal.LineS(s));
            var vN = normal.LineS(s) % Wrap;

            var runN = BedShape.Run(sinkN);
            for (var c = 0; c < count; c++)
            {
                var a = NormalColumn(c, shapeN, runN, spread * 0.5f);
                var (x, z) = c == HalfColumns ? mid : (nx + toward.X * a, nz + toward.Z * a);
                positions[r, c] = (x, z, Higher(x, z));
                frames[r, c] = (side * a * normal.Direction, vN);
            }

            // The reverse half, square to the reverse route through the middle point.
            var foot = Turnout.Offset(reverse, mid.X, mid.Z);
            var sR = foot?.S ?? 0f;
            var (rx0, rz0, ry, _, _, rrx, rrz) = reverse.At(sR);
            var sinkR = reverse.Line.SinkAt(reverse.LineS(sR));
            var vR = reverse.LineS(sR) % Wrap;
            var start = (foot?.Offset ?? 0f) * side;
            var runR = BedShape.Run(sinkR);
            for (var c = 0; c < count; c++)
            {
                var b = ReverseColumn(c, shapeR, runR, start);
                var (x, z) = c == 0 ? mid : (rx0 + rrx * side * b, rz0 + rrz * side * b);
                positions[r, count + c] = c == 0 ? positions[r, HalfColumns] : (x, z, Higher(x, z));
                frames[r, count + c] = (side * b * reverse.Direction, vR);
            }

            // The fades into the ground beyond each far side: from where the bed gives out to its toe, under the taper.
            foreach (var (k, a) in (ReadOnlySpan<(int, float)>)[(0, -(shapeN.Half + runN + shapeN.FadeOut)), (1, -(shapeN.Half + runN))])
                edges[r, k] = (nx + toward.X * a, nz + toward.Z * a, ny + (k == 0 ? sinkN + GivesOut : sinkN + BedWalk), side * a * normal.Direction, vN);
            foreach (var (k, b) in (ReadOnlySpan<(int, float)>)[(2, shapeR.Half + runR), (3, shapeR.Half + runR + shapeR.FadeOut)])
                edges[r, k] = (rx0 + rrx * side * b, rz0 + rrz * side * b, ry + (k == 3 ? sinkR + GivesOut : sinkR + BedWalk), side * b * reverse.Direction, vR);
        }

        float3 At((double X, double Z, float Y) p) => new((float)(p.X - origin.X), p.Y, (float)(p.Z - origin.Z));

        // Normals from the grid's neighbours along and across, each half on its own.
        float3 NormalAt(int r, int c, int from, int to)
        {
            var (r0, r1) = (Math.Max(r - 1, 0), Math.Min(r + 1, rows.Count - 1));
            var (c0, c1) = (Math.Max(c - 1, from), Math.Min(c + 1, to));
            var along = At(positions[r1, c]) - At(positions[r0, c]);
            var across = At(positions[r, c1]) - At(positions[r, c0]);
            var n = new float3(across.Y * along.Z - across.Z * along.Y, across.Z * along.X - across.X * along.Z, across.X * along.Y - across.Y * along.X);
            if (n.Y < 0f)
                n = -n;
            var length = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            return length > 1e-6f ? n / length : new float3(0f, 1f, 0f);
        }

        // Quads wound as the beds' own are: across in the leg's right-hand sense, then along.
        void Grid(int from, int to, float code)
        {
            var start = (uint)builder.Vertices.Count;
            var width = to - from + 1;
            for (var r = 0; r < rows.Count; r++)
                for (var c = from; c <= to; c++)
                    builder.Add(At(positions[r, c]), NormalAt(r, c, from, to), new float4(frames[r, c].U, frames[r, c].V, code, railOffset));
            for (var r = 0; r + 1 < rows.Count; r++)
                for (var c = 0; c + 1 < width; c++)
                {
                    var q = start + (uint)(r * width + c);
                    var next = q + (uint)width;
                    if (side > 0)
                        builder.Quad(q, q + 1, next, next + 1);
                    else
                        builder.Quad(q + 1, q, next + 1, next);
                }
        }

        Grid(0, HalfColumns, ballastN);
        Grid(count, count * 2 - 1, ballastR);

        // The fades: Data.w how far in from where it gives out, below zero, so they're drawn with the fades.
        foreach (var (inner, outer) in (ReadOnlySpan<(int, int)>)[(0, 1), (2, 3)])
        {
            var start = (uint)builder.Vertices.Count;
            for (var r = 0; r < rows.Count; r++)
                foreach (var k in (ReadOnlySpan<int>)[inner, outer])
                {
                    var e = edges[r, k];
                    var givesOut = k is 0 or 3;
                    builder.Add(new float3((float)(e.X - origin.X), e.Y, (float)(e.Z - origin.Z)), new float3(0f, 1f, 0f),
                        new float4(e.U, e.V, gravel, givesOut ? -0.3f : -Fading - 0.3f));
                }
            for (var r = 0u; r + 1 < rows.Count; r++)
            {
                var q = start + r * 2;
                if (side > 0)
                    builder.Quad(q, q + 1, q + 2, q + 3);
                else
                    builder.Quad(q + 1, q, q + 3, q + 2);
            }
        }
    }
}
