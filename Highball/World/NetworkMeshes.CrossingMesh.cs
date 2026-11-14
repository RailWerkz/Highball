namespace Highball.World;

/// Where a grade crossing's surface is solid, for the terrain to leave a hole there: in the crossing's own frame (T along its road
/// from Origin, W across it, to the road's left), on a grid of Cell metres from (T0, W0), Columns along by Rows across. Each cell is
/// two words: its state (0 outside, 1 inside, 2 on the outline; 4 added if its centre's inside) and, on the outline, the first of
/// its edges in Edges and how many (first shifted up 8 bits). Edges: four floats each (T, W of one end, then the other).
public sealed record CrossingHole(long Id, double OriginX, double OriginZ, double Dx, double Dz, float T0, float W0, float Cell, int Columns, int Rows,
    uint[] Cells, float[] Edges);

public sealed partial class NetworkMeshes
{
    /// A grade crossing's surface as one mesh: the road and its ramps, the panels with their rail slots, the ballast running into it
    /// and the fill round it, sharing every vertex and edge, with nothing laid over or under anything else. Points relative to the
    /// crossing's place; Data as line.frag reads it. Hole: where it's solid, which the terrain leaves out.
    private sealed record CrossingMesh(float3[] Points, float3[] Normals, float4[] Data, uint[] Indices, CrossingHole Hole);

    /// What a part of a crossing's surface is.
    private enum CrossingPart { Road, Panel, Slot, Ballast, Fill, Fade }

    /// The grid a crossing's surface is cut from, how wide the band is where its fill fades into the ground round its outside,
    /// how far past the panels' ends the ballast is the crossing's, and how far either way a joint with the road or the ballast
    /// running on is drawn to them.
    private const float CrossingCell = 0.5f;
    private const float CrossingBand = 1.5f;
    private const float CrossingBedOn = 1f;
    private const float CrossingJoin = 2f;
    /// How near a joint with the road running on the crossing's fill is that road's shoulder exactly (well inside the band round the
    /// hole's edge where the terrain stays whole).
    private const float JoinExact = 0.3f;
    /// How far out past a bed's outer edge the ground beside it, beyond the crossing's stretch of it, is left to the terrain.
    private const float BedSide = CrossingBand + 2f;
    /// How deep a rail's slot through the panels is, and how wide its walls; a panel joint's dip.
    private const float SlotDepth = 0.06f;
    private const float SlotWall = 0.012f;
    private const float JointDip = 0.01f;
    /// How softly the road's fill and the ballast's heap are run together where they meet.
    private const float FillRound = 0.6f;

    /// A track the road crosses, as the mesh sees it: a frame at the crossing (its centre line, the way it runs, its right), and how
    /// far along it either way its panels run.
    private sealed class CrossingTrack(Leg leg, float s, float offset, float from, float to)
    {
        public Leg Leg { get; } = leg;
        public float S { get; } = s;
        public float Offset { get; } = offset;
        public float From { get; } = from;
        public float To { get; } = to;
        public double Ox, Oz, Fx, Fz, Rx, Rz;

        public (double Lat, double Along) Local(double x, double z) => ((x - Ox) * Rx + (z - Oz) * Rz, (x - Ox) * Fx + (z - Oz) * Fz);
        public (double X, double Z) World(double lat, double along) => (Ox + Rx * lat + Fx * along, Oz + Rz * lat + Fz * along);
    }

    /// A line's ballast bed as the mesh sees it: a frame on the line's own centre, the stretch of it the crossing takes over (from
    /// CutLow to CutHigh along it), and its section there.
    private sealed class CrossingBed(NetworkLine line, float s)
    {
        public NetworkLine Line { get; } = line;
        public Leg Leg { get; } = new(line, 0, 1);
        public float S { get; } = s;
        public float CutLow, CutHigh, Toe, Outer;
        public double Ox, Oz, Fx, Fz, Rx, Rz;
        private readonly Dictionary<int, (float U, float Y)[]> _sections = [];

        public (double Lat, double Along) Local(double x, double z) => ((x - Ox) * Rx + (z - Oz) * Rz, (x - Ox) * Fx + (z - Oz) * Fz);
        public (double X, double Z) World(double lat, double along) => (Ox + Rx * lat + Fx * along, Oz + Rz * lat + Fz * along);

        /// Its section at s along the line (as FarRail sweeps it), a quarter metre at a time.
        public (float U, float Y)[] Section(float at)
        {
            var key = (int)MathF.Round(at * 4f);
            if (!_sections.TryGetValue(key, out var section))
                _sections[key] = section = BedSection(Line, false, 0f, 0f, 0f, Line.SinkAt(key * 0.25f));
            return section;
        }

        /// The bed's surface lat across from its centre, along from the crossing: its rock out to the toes, then its fade out over the
        /// ground to its outer edge; nothing past that.
        public float? Height(double lat, double along)
        {
            var s = S + (float)along;
            var p = Section(s);
            var u = (float)lat;
            var n = p.Length;
            var y = Leg.At(s).Y;
            if (u <= p[0].U || u >= p[n - 1].U)
                return null;
            if (u < p[2].U)
                return y + p[0].Y + (p[2].Y - p[0].Y) * (u - p[0].U) / (p[2].U - p[0].U);
            if (u > p[n - 3].U)
                return y + p[n - 3].Y + (p[n - 1].Y - p[n - 3].Y) * (u - p[n - 3].U) / (p[n - 1].U - p[n - 3].U);
            for (var i = 2; i < n - 3; i++)
                if (u <= p[i + 1].U)
                {
                    var span = p[i + 1].U - p[i].U;
                    return y + (span < 1e-5f ? p[i + 1].Y : p[i].Y + (p[i + 1].Y - p[i].Y) * (u - p[i].U) / span);
                }

            return y + p[n - 3].Y;
        }
    }

    /// Builds a crossing's surface as one mesh. (cx, cz) and (dx, dz): the crossing's middle and the way its road runs; half the
    /// road's width; where its ramps end along the road; deck(t, w) the road's surface over it, ramps and all; offLow and offHigh how
    /// far across the roads running on from each end actually end; the tracks it crosses and the stretches of them its panels cover.
    /// Also returns the stretches of each line's bed the crossing takes over, for the bed to leave out.
    private (CrossingMesh? Mesh, List<(int Line, float From, float To)> BedCuts) BuildCrossingMesh(long id, double cx, double cz, double dx, double dz,
        float half, float rampLow, float rampHigh, Func<float, float, float> deck, float offLow, float offHigh,
        List<(Leg Leg, float S, float Offset)> trackList, List<(int Line, float From, float To)> covered, float reach, float g, float railTop,
        float paint, bool joints, DeckRoad? road, double baseX, double baseZ)
    {
        var (nx, nz) = (-dz, dx);
        (double X, double Z) World(double t, double w) => (cx + dx * t + nx * w, cz + dz * t + nz * w);
        (double T, double W) Local(double x, double z) => ((x - cx) * dx + (z - cz) * dz, (x - cx) * nx + (z - cz) * nz);
        var bedCuts = new List<(int Line, float From, float To)>();

        // The tracks, each in a frame of its own where it crosses.
        var tracks = new List<CrossingTrack>();
        for (var j = 0; j < trackList.Count; j++)
        {
            var (leg, s, offset) = trackList[j];
            var track = new CrossingTrack(leg, s, offset, covered[j].From - s, covered[j].To - s);
            var (px, pz, _, fx, fz, rx, rz) = leg.At(s);
            (track.Ox, track.Oz, track.Fx, track.Fz, track.Rx, track.Rz) = (px + rx * offset, pz + rz * offset, fx, fz, rx, rz);
            tracks.Add(track);
        }

        // Their lines' beds: the stretch the crossing takes over runs CrossingBedOn past the panels each way.
        var beds = new List<CrossingBed>();
        foreach (var group in tracks.GroupBy(t => t.Leg.Line.Index))
        {
            var first = group.First();
            var bed = new CrossingBed(first.Leg.Line, first.S);
            var (px, pz, _, fx, fz, rx, rz) = bed.Leg.At(bed.S);
            (bed.Ox, bed.Oz, bed.Fx, bed.Fz, bed.Rx, bed.Rz) = (px, pz, fx, fz, rx, rz);
            (bed.CutLow, bed.CutHigh) = (group.Min(t => t.From) - CrossingBedOn, group.Max(t => t.To) + CrossingBedOn);
            var section = bed.Section(bed.S);
            (bed.Toe, bed.Outer) = (section[^3].U, section[^1].U);
            // On a skew the bed running on past a square cut would lie alongside the road, under its fill: the cut moves on out each
            // way till the bed beyond (out to its fade's edge and the band past it) is wholly clear of the road and its fill.
            var clear = half + ShoulderOut + 1.5f;
            bool Clear(float along)
            {
                var sides = ((double[])[-bed.Outer - BedSide, bed.Outer + BedSide]).Select(lat =>
                {
                    var (x, z) = bed.World(lat, along);
                    return Local(x, z).W;
                }).ToArray();
                return Math.Sign(sides[0]) == Math.Sign(sides[1]) && Math.Abs(sides[0]) >= clear && Math.Abs(sides[1]) >= clear;
            }

            for (var k = 0; k < 160 && !Clear(bed.CutLow); k++)
                bed.CutLow -= 0.25f;
            for (var k = 0; k < 160 && !Clear(bed.CutHigh); k++)
                bed.CutHigh += 0.25f;
            beds.Add(bed);
            bedCuts.Add((bed.Line.Index, bed.S + bed.CutLow, bed.S + bed.CutHigh));
        }

        // --- What's where, and how high. ---

        bool InRoad(double t, double w) => Math.Abs(w) <= half + 1e-4 && t >= rampLow - 1e-4 && t <= rampHigh + 1e-4;
        // Beyond the ramps' ends the road runs on, its surface and shoulders its own: none of the crossing there.
        bool RoadBeyond(double t, double w) => (t < rampLow - 1e-4 || t > rampHigh + 1e-4) && Math.Abs(w) < half + ShoulderOut - 1e-4;
        // Beyond the stretch the crossing takes over, each bed runs on as itself, and the ground beside it is the terrain's: none of
        // the crossing there (alongside the bed, the two would meet on a step).
        bool BedBeyond(double x, double z)
        {
            foreach (var bed in beds)
            {
                var (lat, along) = bed.Local(x, z);
                if ((along < bed.CutLow - 1e-4 || along > bed.CutHigh + 1e-4) && Math.Abs(lat) < bed.Outer + BedSide - 1e-4)
                    return true;
            }

            return false;
        }

        // A rail's slot through the panels, each side of the track: from its outer edge to its inner.
        var flange = RailHeadHalf + PanelFlangeway;
        (float From, float To)[] slots = [(-g - RailHeadHalf - PanelGapOutside, -g + flange), (g - flange, g + RailHeadHalf + PanelGapOutside)];

        CrossingTrack? PanelAt(double x, double z, double margin = 1e-4)
        {
            foreach (var track in tracks)
            {
                var (lat, along) = track.Local(x, z);
                if (Math.Abs(lat) <= reach + margin && along >= track.From - margin && along <= track.To + margin)
                    return track;
            }

            return null;
        }

        float PanelHeight(CrossingTrack track, double x, double z, out bool slot)
        {
            var (lat, along) = track.Local(x, z);
            var y = track.Leg.At(track.S + (float)along).Y + railTop;
            slot = false;
            foreach (var (from, to) in slots)
            {
                var into = MathF.Min((float)lat - from, to - (float)lat);
                if (into > 1e-4f)
                {
                    slot = into > SlotWall * 0.5f;
                    return y - SlotDepth * MathF.Min(1f, into / SlotWall);
                }
            }

            if (joints)
            {
                var along0 = (float)along - track.From;
                var joint = along0 - MathF.Round(along0 / PanelLength) * PanelLength;
                if (MathF.Abs(joint) < 0.01f && along0 > 0.01f && along0 < track.To - track.From - 0.01f)
                    return y - JointDip * (1f - MathF.Abs(joint) / 0.01f);
            }

            return y;
        }

        // The road's fill beside it: its own shoulder's shape (as RoadStretch sweeps it), down from just under its edge, and on down
        // past it at the same slope.
        float RoadFill(double t, double w)
        {
            var side = w < 0 ? -1f : 1f;
            var edge = deck(Math.Clamp((float)t, rampLow, rampHigh), side * half);
            var under = edge - RoadRise - 0.002f;
            var out_ = (float)Math.Abs(w) - half;
            var shoulder = under + 0.04f - (out_ + 0.05f) * (0.39f / 0.65f);
            return out_ < 0.02f ? edge + (shoulder - edge) * MathF.Max(0f, out_) / 0.02f : shoulder;
        }

        (float? Y, CrossingBed? Bed, double Lat, double Along) BedAt(double x, double z)
        {
            (float? Y, CrossingBed? Bed, double Lat, double Along) best = (null, null, 0, 0);
            foreach (var bed in beds)
            {
                var (lat, along) = bed.Local(x, z);
                var along2 = Math.Clamp(along, bed.CutLow - CrossingJoin, bed.CutHigh + CrossingJoin);
                if (bed.Height(lat, along2) is { } y && (best.Y is null || y > best.Y))
                    best = (y, bed, lat, along);
            }

            return best;
        }

        static float SoftMax(float a, float b, float k)
        {
            var h = MathF.Max(k - MathF.Abs(a - b), 0f) / k;
            return MathF.Max(a, b) + h * h * k * 0.25f;
        }

        // The surface at a place off the road and panels: the road's fill and the ballast's heap run softly together, and never under
        // the ground. Near where the road runs on beyond a ramp it's the road's own edge exactly, and near where a bed runs on beyond
        // the crossing, the bed's own section exactly, so they meet edge to edge.
        // The ground as it's drawn: the terrain splits each square of its grid into two triangles, along either diagonal (whichever's
        // gentler, patch by patch), which on a steep bank lies well off the grid's smooth (bilinear) height. Taken as the higher of the
        // two splits, so the surface never runs in under the terrain beside it, whichever way the terrain's split there.
        float Ground(double x, double z)
        {
            var spacing = _source.Spacing;
            var (fx, fz) = (x / spacing, z / spacing);
            var (gx, gz) = ((int)Math.Floor(fx), (int)Math.Floor(fz));
            var (tx, tz) = ((float)(fx - gx), (float)(fz - gz));
            if (!_source.TryHeight(gx, gz, out var a) || !_source.TryHeight(gx + 1, gz, out var b) || !_source.TryHeight(gx, gz + 1, out var c)
                || !_source.TryHeight(gx + 1, gz + 1, out var d))
                return _source.HeightAt(x, z);
            // Split a-d: (a, b, d) above it, (a, c, d) below; split b-c: (a, b, c) or (b, c, d).
            var alongAD = tx >= tz ? a + (b - a) * tx + (d - b) * tz : a + (c - a) * tz + (d - c) * tx;
            var alongBC = tx + tz <= 1f ? a + (b - a) * tx + (c - a) * tz : d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
            return MathF.Max(alongAD, alongBC);
        }

        // The ballast heaped up against the panels round their outside: level with their tops at their edges, down at the ballast's
        // own slope from there, so no panel stands up out of the ground on a wall.
        float PanelFill(double x, double z)
        {
            var best = float.MinValue;
            foreach (var track in tracks)
            {
                var (lat, along) = track.Local(x, z);
                var (dl, da) = (Math.Max(0, Math.Abs(lat) - reach), Math.Max(0, Math.Max(along - track.To, track.From - along)));
                var d = (float)Math.Sqrt(dl * dl + da * da);
                if (d > 2f)
                    continue;
                var top = track.Leg.At(track.S + (float)Math.Clamp(along, track.From, track.To)).Y + railTop - 0.005f;
                best = MathF.Max(best, top - d / NetworkLine.BallastSlope);
            }

            return best;
        }

        float Fill(double t, double w, double x, double z)
        {
            var ground = Ground(x, z);
            var fill = RoadFill(t, w);
            var (bedY, bed, lat, along) = BedAt(x, z);
            var y = bedY is { } b ? SoftMax(fill, b, FillRound) : fill;
            if (PanelFill(x, z) is var heap && heap > float.MinValue)
                y = SoftMax(y, heap, 0.15f);
            y = MathF.Max(y, ground);

            var toJoint = (float)Math.Min(Math.Abs(t - rampLow), Math.Abs(t - rampHigh));
            if (toJoint < CrossingJoin && Math.Abs(w) < half + ShoulderOut + CrossingJoin)
            {
                var weight = 1f - toJoint / CrossingJoin;
                y += (MathF.Max(fill, ground) - y) * weight * weight;
            }

            foreach (var near in beds)
            {
                var (l, a) = near.Local(x, z);
                var toCut = (float)Math.Min(Math.Abs(a - near.CutLow), Math.Abs(a - near.CutHigh));
                // Only over the bed itself: past its outer edge nothing runs on beyond the cut to meet.
                if (toCut >= CrossingJoin || near.Height(l, Math.Clamp(a, near.CutLow, near.CutHigh)) is not { } exact)
                    continue;
                var weight = 1f - toCut / CrossingJoin;
                y += (exact - y) * weight * weight;
            }

            // Never under the ground as it's drawn, and a hair over it, as the fade round it is: where it ran in under the terrain, the
            // hole there showed the sky, and where it lay on it the terrain left round the hole's edge showed through.
            y = MathF.Max(y, ground + GivesOut);

            // But right at a joint with the road running on, its shoulder exactly, under the ground or not, so the two meet edge to
            // edge; that close to the crossing's edge the terrain's whole, the hole well clear of it.
            if (Math.Abs(w) < half + ShoulderOut + JoinExact)
                y += (fill - y) * MathF.Max(0f, 1f - toJoint / JoinExact);
            return y;
        }

        // --- The cuts. ---

        // Every edge the surface has to have, as a segment in (t, w).
        var segments = new List<((double T, double W) A, (double T, double W) B)>();
        void Segment((double X, double Z) a, (double X, double Z) b) => segments.Add((Local(a.X, a.Z), Local(b.X, b.Z)));
        void Line(double t0, double w0, double t1, double w1) => segments.Add(((t0, w0), (t1, w1)));

        // How far it all reaches: the road's fill and the beds' fades and a band past them.
        var (tMin, tMax, wMax) = (rampLow - 4f, rampHigh + 4f, half + ShoulderOut + 5f);
        foreach (var bed in beds)
            foreach (var along in (double[])[bed.CutLow, bed.CutHigh])
                foreach (var lat in (double[])[-bed.Outer - BedSide, bed.Outer + BedSide])
                {
                    var (x, z) = bed.World(lat, along);
                    var (t, w) = Local(x, z);
                    (tMin, tMax, wMax) = (Math.Min(tMin, (float)t), Math.Max(tMax, (float)t), Math.Max(wMax, (float)Math.Abs(w)));
                }

        (tMin, tMax, wMax) = (MathF.Floor((tMin - 1f) / CrossingCell) * CrossingCell, MathF.Ceiling((tMax + 1f) / CrossingCell) * CrossingCell,
            MathF.Ceiling((wMax + 1f) / CrossingCell) * CrossingCell);

        foreach (var side in (float[])[-1f, 1f])
        {
            Line(rampLow, side * half, rampHigh, side * half);
            Line(rampLow, side * (half + 0.02f), rampHigh, side * (half + 0.02f));
            Line(tMin, side * (half + ShoulderOut), tMax, side * (half + ShoulderOut));
        }

        foreach (var t in (float[])[rampLow, rampHigh])
            Line(t, -(half + ShoulderOut), t, half + ShoulderOut);

        foreach (var track in tracks)
        {
            var (f, to) = (track.From, track.To);
            void Across(double lat0, double lat1, double along) => Segment(track.World(lat0, along), track.World(lat1, along));
            void Along(double lat, double a0, double a1) => Segment(track.World(lat, a0), track.World(lat, a1));
            const double wall = 0.02;
            foreach (var s in (double[])[-1, 1])
            {
                Along(s * reach, f, to);
                Along(s * (reach + wall), f - wall, to + wall);
            }

            foreach (var a in (double[])[f, to])
                Across(-reach, reach, a);
            Across(-reach - wall, reach + wall, f - wall);
            Across(-reach - wall, reach + wall, to + wall);
            foreach (var (from, end) in slots)
                foreach (var lat in (double[])[from, from + SlotWall, end - SlotWall, end])
                    Along(lat, f, to);
            if (joints)
                for (var a = f + PanelLength; a < to - 0.1f; a += PanelLength)
                    foreach (var o in (double[])[-0.01, 0, 0.01])
                        Across(-reach, reach, a + o);
        }

        foreach (var bed in beds)
        {
            foreach (var (u, _) in bed.Section(bed.S))
                Segment(bed.World(u, bed.CutLow - CrossingJoin), bed.World(u, bed.CutHigh + CrossingJoin));
            foreach (var a in (double[])[bed.CutLow, bed.CutHigh])
                Segment(bed.World(-bed.Outer - BedSide, a), bed.World(bed.Outer + BedSide, a));
            foreach (var lat in (double[])[-bed.Outer - BedSide, bed.Outer + BedSide])
                foreach (var (from, to) in new (double, double)[] { (bed.CutLow - 40, bed.CutLow), (bed.CutHigh, bed.CutHigh + 40) })
                    Segment(bed.World(lat, from), bed.World(lat, to));
        }

        // The grid's cells, each cut by the segments through it into convex pieces.
        var columns = (int)MathF.Round((tMax - tMin) / CrossingCell);
        var rowsAcross = (int)MathF.Round(2f * wMax / CrossingCell);
        var buckets = new List<int>[columns * rowsAcross];
        for (var i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            var (c0, c1) = ((int)Math.Floor((Math.Min(a.T, b.T) - tMin) / CrossingCell), (int)Math.Floor((Math.Max(a.T, b.T) - tMin) / CrossingCell));
            var (r0, r1) = ((int)Math.Floor((Math.Min(a.W, b.W) + wMax) / CrossingCell), (int)Math.Floor((Math.Max(a.W, b.W) + wMax) / CrossingCell));
            for (var c = Math.Max(0, c0); c <= Math.Min(columns - 1, c1); c++)
                for (var r = Math.Max(0, r0); r <= Math.Min(rowsAcross - 1, r1); r++)
                    (buckets[c * rowsAcross + r] ??= []).Add(i);
        }

        var pieces = new List<List<(double T, double W)>>();
        for (var c = 0; c < columns; c++)
            for (var r = 0; r < rowsAcross; r++)
            {
                var (t0, w0) = (tMin + c * CrossingCell, -wMax + r * CrossingCell);
                var cell = new List<List<(double T, double W)>> { new() { (t0, w0), (t0 + CrossingCell, w0), (t0 + CrossingCell, w0 + CrossingCell), (t0, w0 + CrossingCell) } };
                foreach (var i in buckets[c * rowsAcross + r] ?? [])
                {
                    var (a, b) = segments[i];
                    var next = new List<List<(double T, double W)>>();
                    foreach (var piece in cell)
                        next.AddRange(Split(piece, a, b));
                    cell = next;
                }

                pieces.AddRange(cell);
            }

        // --- What each piece is. ---

        var parts = new CrossingPart[pieces.Count];
        var keep = new bool[pieces.Count];
        var fillY = new float[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            var (t, w) = (pieces[i].Average(p => p.T), pieces[i].Average(p => p.W));
            var (x, z) = World(t, w);
            if (RoadBeyond(t, w) || BedBeyond(x, z))
                continue;
            keep[i] = true;
            if (PanelAt(x, z) is { } panel)
            {
                PanelHeight(panel, x, z, out var slot);
                parts[i] = slot ? CrossingPart.Slot : CrossingPart.Panel;
                continue;
            }

            if (InRoad(t, w))
            {
                parts[i] = CrossingPart.Road;
                continue;
            }

            var y = Fill(t, w, x, z);
            var (bedY, bed, lat, along) = BedAt(x, z);
            var onBed = bedY is { } b && b >= RoadFill(t, w) - 0.02f && bed is not null;
            if (onBed && Math.Abs(lat) <= bed!.Toe)
                parts[i] = CrossingPart.Ballast;
            else if (onBed && (along < bed!.CutLow + CrossingJoin || along > bed.CutHigh - CrossingJoin) && PanelAt(x, z, 0.5) is null)
                parts[i] = CrossingPart.Fade;
            else if (y > Ground(x, z) + GivesOut + 0.03f)
                parts[i] = CrossingPart.Fill;
            else
                parts[i] = CrossingPart.Fade;
            fillY[i] = y;
        }

        // --- The vertices, shared. ---

        var vertices = new List<(double T, double W)>();
        var lookup = new Dictionary<(long, long), int>();
        int VertexOf((double T, double W) p)
        {
            var key = ((long)Math.Round(p.T * 1e5), (long)Math.Round(p.W * 1e5));
            if (lookup.TryGetValue(key, out var index))
                return index;
            lookup[key] = vertices.Count;
            vertices.Add(p);
            return vertices.Count - 1;
        }

        var rings = new List<int>[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
            if (keep[i])
                rings[i] = [.. pieces[i].Select(VertexOf)];

        // Every vertex lying along another piece's edge goes into that edge too, so no edge has a vertex on it the piece beside it
        // hasn't: the surface is closed, crack-free, everywhere.
        var near = new Dictionary<(int, int), List<int>>();
        for (var v = 0; v < vertices.Count; v++)
        {
            var key = ((int)Math.Floor(vertices[v].T / CrossingCell), (int)Math.Floor(vertices[v].W / CrossingCell));
            if (!near.TryGetValue(key, out var list))
                near[key] = list = [];
            list.Add(v);
        }

        for (var i = 0; i < pieces.Count; i++)
        {
            if (!keep[i])
                continue;
            var ring = rings[i];
            var whole = new List<int>();
            for (var k = 0; k < ring.Count; k++)
            {
                var (a, b) = (vertices[ring[k]], vertices[ring[(k + 1) % ring.Count]]);
                whole.Add(ring[k]);
                var (et, ew) = (b.T - a.T, b.W - a.W);
                var length2 = et * et + ew * ew;
                if (length2 < 1e-12)
                    continue;
                var between = new List<(double U, int V)>();
                for (var gx = (int)Math.Floor(Math.Min(a.T, b.T) / CrossingCell) - 1; gx <= (int)Math.Floor(Math.Max(a.T, b.T) / CrossingCell) + 1; gx++)
                    for (var gz = (int)Math.Floor(Math.Min(a.W, b.W) / CrossingCell) - 1; gz <= (int)Math.Floor(Math.Max(a.W, b.W) / CrossingCell) + 1; gz++)
                        if (near.TryGetValue((gx, gz), out var list))
                            foreach (var v in list)
                            {
                                if (v == ring[k] || v == ring[(k + 1) % ring.Count])
                                    continue;
                                var p = vertices[v];
                                var u = ((p.T - a.T) * et + (p.W - a.W) * ew) / length2;
                                if (u <= 1e-7 || u >= 1 - 1e-7)
                                    continue;
                                var (qt, qw) = (a.T + et * u - p.T, a.W + ew * u - p.W);
                                if (qt * qt + qw * qw < 1e-10)
                                    between.Add((u, v));
                            }

                between.Sort((p, q) => p.U.CompareTo(q.U));
                whole.AddRange(between.Select(p => p.V));
            }

            rings[i] = whole;
        }

        // How far each vertex is from the solid part, along the surface: the fade band runs CrossingBand out from it.
        bool Solid(int i) => keep[i] && parts[i] != CrossingPart.Fade;
        var reachOut = new double[vertices.Count];
        Array.Fill(reachOut, double.MaxValue);
        var links = new Dictionary<int, List<int>>();
        var queue = new PriorityQueue<int, double>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!keep[i])
                continue;
            var ring = rings[i];
            for (var k = 0; k < ring.Count; k++)
            {
                var (a, b) = (ring[k], ring[(k + 1) % ring.Count]);
                (links.TryGetValue(a, out var la) ? la : links[a] = []).Add(b);
                (links.TryGetValue(b, out var lb) ? lb : links[b] = []).Add(a);
                if (Solid(i) && reachOut[a] > 0)
                {
                    reachOut[a] = 0;
                    queue.Enqueue(a, 0);
                }
            }
        }

        while (queue.TryDequeue(out var v, out var d))
        {
            if (d > reachOut[v] || d > CrossingBand)
                continue;
            foreach (var u in links[v])
            {
                var (pv, pu) = (vertices[v], vertices[u]);
                var nd = d + Math.Sqrt((pv.T - pu.T) * (pv.T - pu.T) + (pv.W - pu.W) * (pv.W - pu.W));
                if (nd < reachOut[u])
                {
                    reachOut[u] = nd;
                    queue.Enqueue(u, nd);
                }
            }
        }

        // The fade's pieces out past the band are left off; so's the bed's own fade where it's not by a joint with the bed beyond.
        for (var i = 0; i < pieces.Count; i++)
            if (keep[i] && parts[i] == CrossingPart.Fade && rings[i].Min(v => reachOut[v]) > CrossingBand)
                keep[i] = false;

        // --- Where each vertex is, and how high. ---

        // Near a joint, the crossing's moved across to where the road or the bed beyond actually runs, so their ends meet exactly.
        (double X, double Z) Placed((double T, double W) p)
        {
            var (x, z) = World(p.T, p.W);
            var shift = offLow * Math.Clamp((rampLow + 3.0 - p.T) / 3.0, 0, 1) + offHigh * Math.Clamp((p.T - (rampHigh - 3.0)) / 3.0, 0, 1);
            (x, z) = (x + nx * shift, z + nz * shift);
            foreach (var bed in beds)
            {
                var (lat, along) = bed.Local(x, z);
                if (Math.Abs(lat) > bed.Outer + CrossingBand + CrossingJoin)
                    continue;
                foreach (var cut in (float[])[bed.CutLow, bed.CutHigh])
                {
                    var weight = Math.Clamp(1.0 - Math.Abs(along - cut) / CrossingJoin, 0, 1);
                    if (weight <= 0)
                        continue;
                    var (rx, rz, _, _, _, ax, az) = bed.Leg.At(bed.S + cut);
                    var (lx, lz) = bed.World(lat, cut);
                    var (realX, realZ) = (rx + ax * lat, rz + az * lat);
                    (x, z) = (x + (realX - lx) * weight, z + (realZ - lz) * weight);
                }
            }

            return (x, z);
        }

        var used = new bool[vertices.Count];
        var touchesFade = new bool[vertices.Count];
        for (var i = 0; i < pieces.Count; i++)
            if (keep[i])
                foreach (var v in rings[i])
                {
                    used[v] = true;
                    touchesFade[v] |= parts[i] == CrossingPart.Fade;
                }

        var places = new (double X, double Z)[vertices.Count];
        var heights = new float[vertices.Count];
        for (var v = 0; v < vertices.Count; v++)
        {
            if (!used[v])
                continue;
            var p = vertices[v];
            places[v] = Placed(p);
            var (x, z) = World(p.T, p.W);
            var (px, pz) = places[v];
            float y;
            if (PanelAt(x, z) is { } panel)
                y = PanelHeight(panel, x, z, out _);
            else if (InRoad(p.T, p.W))
                y = deck((float)p.T, (float)p.W);
            else
                y = Fill(p.T, p.W, px, pz);
            if (touchesFade[v])
                y = MathF.Max(y, Ground(px, pz) + GivesOut);
            heights[v] = y;
        }

        // --- The triangles, and what each corner of them is drawn as. ---

        var points = new List<float3>();
        var data = new List<float4>();
        var normals = new List<float3>();
        var indices = new List<uint>();
        var copies = new Dictionary<(int, CrossingPart), int>();
        // Which vertex each point is a copy of (-1 for a fan's middle), and which parts share their shading across a vertex: the
        // ballast, fill and fade are one ground, smooth over where one becomes another.
        var owners = new List<int>();
        var groups = new List<int>();
        var ballastOf = beds.ToDictionary(b => b, b => Code(LineMaterial.Ballast, b.Line.Tracks + (b.Line.Disused ? 8 : 0) + (b.Line.BedHeight < 0.12f ? 16 : 0)));
        var gravel = Code(LineMaterial.Gravel);
        var asphalt = Code(LineMaterial.Asphalt);
        float3 Relative(double x, double z, float y) => new((float)(x - baseX), y, (float)(z - baseZ));

        float4 DataOf((double X, double Z) at, float3 p, CrossingPart part, double reached)
        {
            switch (part)
            {
                case CrossingPart.Road:
                    return road is { } r
                        ? new float4(Dot(p, r.Across) - r.AcrossAt, (Dot(p, r.Along) + r.AlongAt) % Wrap, r.Code, 1000f)
                        : new float4(p.X, p.X + p.Z, asphalt, 0f);
                case CrossingPart.Panel:
                case CrossingPart.Slot:
                    return new float4(p.X, p.X + p.Z, paint, 0f);
            }

            // On the ballast, its fill and its fade: across the nearest bed and along it, as the bed's own are.
            var bed = beds.MinBy(b => Math.Abs(b.Local(at.X, at.Z).Lat))!;
            var (lat, along) = bed.Local(at.X, at.Z);
            var s = (bed.S + (float)along) % Wrap;
            if (part == CrossingPart.Ballast)
                return new float4((float)lat, s, ballastOf[bed], bed.Line.Gauge * 0.5f + RailHeadHalf);
            if (part == CrossingPart.Fill)
                return new float4((float)lat, s, gravel, 0f);

            // The fade: as solid as the bed's own at its toe where it leaves the solid part, gone by the band's (or the bed fade's) edge.
            var faded = Math.Clamp(reached / CrossingBand, 0, 1);
            var across = Math.Abs(lat);
            if (across > bed.Toe && across < bed.Outer)
                faded = Math.Min(faded, (across - bed.Toe) / (bed.Outer - bed.Toe));
            return new float4((float)lat, s, gravel, -0.3f - (float)(1.0 - faded) * Fading);
        }

        int CopyOf(int v, CrossingPart part)
        {
            if (copies.TryGetValue((v, part), out var index))
                return index;
            var p = Relative(places[v].X, places[v].Z, heights[v]);
            copies[(v, part)] = points.Count;
            owners.Add(v);
            groups.Add(part is CrossingPart.Ballast or CrossingPart.Fill or CrossingPart.Fade ? 0 : (int)part + 1);
            points.Add(p);
            normals.Add(new float3(0f, 0f, 0f));
            data.Add(DataOf(places[v], p, part, reachOut[v]));
            return points.Count - 1;
        }

        void Triangle(int a, int b, int c)
        {
            var (pa, pb, pc) = (points[a], points[b], points[c]);
            var normal = Cross(pb - pa, pc - pa);
            if (normal.Y < 0f)
            {
                normal = normal * -1f;
                (b, c) = (c, b);
            }

            if (Dot(normal, normal) < 1e-14f)
                return;
            // (The game's front faces wind so that this cross points into the surface.)
            indices.AddRange([(uint)a, (uint)c, (uint)b]);
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }

        for (var i = 0; i < pieces.Count; i++)
        {
            if (!keep[i])
                continue;
            var ring = rings[i];
            var part = parts[i];
            var corners = ring.Select(v => CopyOf(v, part)).ToArray();
            if (corners.Length == 3)
                Triangle(corners[0], corners[1], corners[2]);
            else if (corners.Length == 4 && ring.Count == pieces[i].Count)
            {
                Triangle(corners[0], corners[1], corners[2]);
                Triangle(corners[0], corners[2], corners[3]);
            }
            else
            {
                // A fan from its middle, over every vertex along its edges.
                var (t, w) = (ring.Average(v => vertices[v].T), ring.Average(v => vertices[v].W));
                var middle = VertexOf((t, w));
                if (middle >= used.Length)
                {
                    var (x, z) = World(t, w);
                    var place = Placed((t, w));
                    var y = PanelAt(x, z) is { } panel ? PanelHeight(panel, x, z, out _) : InRoad(t, w) ? deck((float)t, (float)w) : Fill(t, w, place.X, place.Z);
                    if (part == CrossingPart.Fade)
                        y = MathF.Max(y, Ground(place.X, place.Z) + GivesOut);
                    var reached = ring.Average(v => Math.Min(reachOut[v], CrossingBand));
                    var p = Relative(place.X, place.Z, y);
                    var centre = points.Count;
                    owners.Add(-1);
                    groups.Add(-1);
                    points.Add(p);
                    normals.Add(new float3(0f, 0f, 0f));
                    data.Add(DataOf(place, p, part, reached));
                    for (var k = 0; k < corners.Length; k++)
                        Triangle(centre, corners[k], corners[(k + 1) % corners.Length]);
                }
                else
                    for (var k = 1; k + 1 < corners.Length; k++)
                        Triangle(corners[0], corners[k], corners[k + 1]);
            }
        }

        if (indices.Count == 0)
            return (null, bedCuts);
        var shared = new Dictionary<(int, int), float3>();
        for (var c = 0; c < points.Count; c++)
            if (owners[c] >= 0 && groups[c] == 0)
                shared[(owners[c], 0)] = shared.GetValueOrDefault((owners[c], 0)) + normals[c];
        for (var c = 0; c < points.Count; c++)
            if (owners[c] >= 0 && groups[c] == 0)
                normals[c] = shared[(owners[c], 0)];
        var unit = normals.Select(n => n * (1f / MathF.Max(MathF.Sqrt(Dot(n, n)), 1e-9f))).ToArray();

        // --- The hole: the solid part's outline. ---

        var edgeCount = new Dictionary<(int, int), int>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!Solid(i))
                continue;
            var ring = rings[i];
            for (var k = 0; k < ring.Count; k++)
            {
                var (a, b) = (ring[k], ring[(k + 1) % ring.Count]);
                var key = a < b ? (a, b) : (b, a);
                edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
            }
        }

        var outline = edgeCount.Where(e => e.Value == 1).Select(e =>
        {
            var (a, b) = (Local(places[e.Key.Item1].X, places[e.Key.Item1].Z), Local(places[e.Key.Item2].X, places[e.Key.Item2].Z));
            return (A: ((float)a.T, (float)a.W), B: ((float)b.T, (float)b.W));
        }).ToList();

        var hole = HoleOf(id, cx, cz, dx, dz, outline);


        return (new CrossingMesh([.. points], unit, [.. data], [.. indices], hole), bedCuts);
    }

    /// A convex polygon split by the line through a and b, if the segment a-b runs through it: one piece each side, or itself.
    private static IEnumerable<List<(double T, double W)>> Split(List<(double T, double W)> polygon, (double T, double W) a, (double T, double W) b)
    {
        var (lt, lw) = (b.T - a.T, b.W - a.W);
        var length2 = lt * lt + lw * lw;
        if (length2 < 1e-12)
            return [polygon];
        double Side((double T, double W) p) => (lt * (p.W - a.W) - lw * (p.T - a.T)) / Math.Sqrt(length2);
        var sides = polygon.Select(Side).ToArray();
        if (sides.All(s => s > -1e-6) || sides.All(s => s < 1e-6))
            return [polygon];

        // Only where the segment itself reaches: its stretch across the polygon has to overlap it.
        var along = polygon.Select(p => ((p.T - a.T) * lt + (p.W - a.W) * lw) / length2).ToArray();
        if (along.Max() < -1e-6 || along.Min() > 1 + 1e-6)
            return [polygon];

        var left = new List<(double T, double W)>();
        var right = new List<(double T, double W)>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var (p, q) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var (sp, sq) = (sides[i], sides[(i + 1) % polygon.Count]);
            if (sp >= -1e-6)
                left.Add(p);
            if (sp <= 1e-6)
                right.Add(p);
            if ((sp > 1e-6 && sq < -1e-6) || (sp < -1e-6 && sq > 1e-6))
            {
                var u = sp / (sp - sq);
                var cut = (p.T + (q.T - p.T) * u, p.W + (q.W - p.W) * u);
                left.Add(cut);
                right.Add(cut);
            }
        }

        static double Area(List<(double T, double W)> poly)
        {
            var sum = 0.0;
            for (var i = 0; i < poly.Count; i++)
            {
                var (p, q) = (poly[i], poly[(i + 1) % poly.Count]);
                sum += p.T * q.W - q.T * p.W;
            }

            return Math.Abs(sum) * 0.5;
        }

        if (left.Count < 3 || right.Count < 3 || Area(left) < 1e-7 || Area(right) < 1e-7)
            return [polygon];
        return [left, right];
    }

    /// The hole a crossing leaves in the terrain, from its solid part's outline: a grid of HoleCell squares, each in the hole only if it
    /// lies wholly inside the outline, HoleMargin clear of it. So the terrain never opens past the surface's edge (where the surface,
    /// never under the ground there, covers what's left of it), and the shader's test is one lookup.
    private const float HoleCell = 0.25f;
    private const float HoleMargin = 0.2f;

    private static CrossingHole HoleOf(long id, double cx, double cz, double dx, double dz, List<((float T, float W) A, (float T, float W) B)> outline)
    {
        if (outline.Count == 0)
            return new CrossingHole(id, cx, cz, dx, dz, 0f, 0f, HoleCell, 0, 0, [], []);
        var (t0, t1) = (outline.Min(e => MathF.Min(e.A.T, e.B.T)), outline.Max(e => MathF.Max(e.A.T, e.B.T)));
        var (w0, w1) = (outline.Min(e => MathF.Min(e.A.W, e.B.W)), outline.Max(e => MathF.Max(e.A.W, e.B.W)));
        var columns = (int)MathF.Ceiling((t1 - t0) / HoleCell) + 1;
        var rows = (int)MathF.Ceiling((w1 - w0) / HoleCell) + 1;

        // The outline's edges by the grid rows they span, for the inside test and the distance to them.
        var near = new List<int>[rows];
        for (var e = 0; e < outline.Count; e++)
        {
            var (a, b) = outline[e];
            var (r0, r1) = ((int)MathF.Floor((MathF.Min(a.W, b.W) - w0) / HoleCell) - 2, (int)MathF.Floor((MathF.Max(a.W, b.W) - w0) / HoleCell) + 2);
            for (var r = Math.Max(0, r0); r <= Math.Min(rows - 1, r1); r++)
                (near[r] ??= []).Add(e);
        }

        var clear = HoleCell * 0.7072f + HoleMargin;
        var cells = new uint[columns * rows * 2];
        for (var r = 0; r < rows; r++)
        {
            var w = w0 + (r + 0.5f) * HoleCell;
            if (near[r] is not { } list)
                continue;
            for (var c = 0; c < columns; c++)
            {
                var t = t0 + (c + 0.5f) * HoleCell;
                var (inside, closest) = (false, float.MaxValue);
                foreach (var e in list)
                {
                    var (a, b) = outline[e];
                    if ((a.W > w) != (b.W > w) && t < a.T + (w - a.W) / (b.W - a.W) * (b.T - a.T))
                        inside = !inside;
                    var (et, ew) = (b.T - a.T, b.W - a.W);
                    var u = Math.Clamp(((t - a.T) * et + (w - a.W) * ew) / MathF.Max(et * et + ew * ew, 1e-9f), 0f, 1f);
                    var (qt, qw) = (a.T + et * u - t, a.W + ew * u - w);
                    closest = MathF.Min(closest, qt * qt + qw * qw);
                }

                if (inside && closest > clear * clear)
                    cells[(r * columns + c) * 2] = 1u;
            }
        }

        return new CrossingHole(id, cx, cz, dx, dz, t0, w0, HoleCell, columns, rows, cells, []);
    }
}
