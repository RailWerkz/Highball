namespace Highball.World;

public sealed partial class NetworkMeshes
{
    /// A grade crossing's surface as one closed solid, the way a crossing's laid in a game: a box. Its top is the road's deck (ramps
    /// and all), its gravel shoulders, and the panels with their rail slots, all sharing every vertex and edge; its four sides are
    /// skirts running straight down from the top's outline to well under the ground, so wherever the ground round it lies, the
    /// crossing stands on it with no edge left in the air. The tracks' beds run on whole through it, under its top, and the
    /// ground's shaped under and round it (ConformPads). Points relative to the crossing's place; Data as line.frag reads it.
    private sealed record CrossingMesh(float3[] Points, float3[] Normals, float4[] Data, uint[] Indices);

    /// What a part of a crossing's top is.
    private enum CrossingPart { Road, Shoulder, Panel, Slot }

    /// The grid a crossing's top is cut from.
    private const float CrossingCell = 0.5f;
    /// How far under the ground round it a crossing's sides run on down.
    private const float CrossingSkirt = 0.75f;
    /// How far under the ground (or ballast) a crossing's embankment runs on before its foot.
    private const float FootUnder = 0.15f;
    /// How deep a rail's slot through the panels is, and how wide its walls; a panel joint's dip.
    private const float SlotDepth = 0.06f;
    /// How wide a planked crossing's timber is, either side of each rail's flangeway (a 12 in plank).
    private const float PlankWidth = 0.30f;
    private const float SlotWall = 0.012f;
    private const float JointDip = 0.01f;

    /// A track the road crosses, as the mesh sees it: a frame at the crossing (its centre line, the way it runs, its right), and how
    /// far along it either way its panels run.
    private sealed class CrossingTrack(Leg leg, float s, float from, float to)
    {
        public Leg Leg { get; } = leg;
        public float S { get; } = s;
        public float From { get; } = from;
        public float To { get; } = to;
        public double Ox, Oz, Fx, Fz, Rx, Rz;

        public (double Lat, double Along) Local(double x, double z) => ((x - Ox) * Rx + (z - Oz) * Rz, (x - Ox) * Fx + (z - Oz) * Fz);
        public (double X, double Z) World(double lat, double along) => (Ox + Rx * lat + Fx * along, Oz + Rz * lat + Fz * along);
    }

    /// Builds a crossing as one closed solid. (cx, cz) and (dx, dz): the crossing's middle and the way its road runs; half the
    /// road's width (its gravel shoulders run ShoulderOut on past that, and so does the crossing); where its ramps end along the
    /// road; deck(t, w) the road's surface over it, ramps and panels and all; offLow and offHigh how far across the roads running on
    /// from each end actually end; the tracks it crosses and the stretches of them its panels cover.
    private CrossingMesh? BuildCrossingMesh(long id, double cx, double cz, double dx, double dz,
        float half, float rampLow, float rampHigh, Func<float, float, float> deck, float offLow, float offHigh,
        List<(Leg Leg, float S, float Offset)> trackList, List<(int Line, float From, float To)> covered, float reach, float g, float railTop,
        float paint, bool joints, DeckRoad? road, double baseX, double baseZ, float? fill = null)
    {
        var (nx, nz) = (-dz, dx);
        (double X, double Z) World(double t, double w) => (cx + dx * t + nx * w, cz + dz * t + nz * w);
        (double T, double W) Local(double x, double z) => ((x - cx) * dx + (z - cz) * dz, (x - cx) * nx + (z - cz) * nz);
        var outer = half + ShoulderOut;

        var tracks = new List<CrossingTrack>();
        for (var j = 0; j < trackList.Count; j++)
        {
            var (leg, s, offset) = trackList[j];
            var track = new CrossingTrack(leg, s, covered[j].From - s, covered[j].To - s);
            var (px, pz, _, fx, fz, rx, rz) = leg.At(s);
            (track.Ox, track.Oz, track.Fx, track.Fz, track.Rx, track.Rz) = (px + rx * offset, pz + rz * offset, fx, fz, rx, rz);
            tracks.Add(track);
        }

        // A rail's slot through the panels, each side of the track: from its outer edge to its inner.
        var flange = RailHeadHalf + PanelFlangeway;
        (float From, float To)[] slots = [(-g - RailHeadHalf - PanelGapOutside, -g + flange), (g - flange, g + RailHeadHalf + PanelGapOutside)];

        CrossingTrack? PanelAt(double x, double z)
        {
            foreach (var track in tracks)
            {
                var (lat, along) = track.Local(x, z);
                if (Math.Abs(lat) <= reach + 1e-4 && along >= track.From - 1e-4 && along <= track.To + 1e-4)
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

        // The top at (t, w): the panels over the tracks, the deck across the road, and its gravel shoulders sloping off its edges
        // as the road's own do beyond.
        float Top(double t, double w, (double X, double Z) at, bool panels = true)
        {
            if (panels && PanelAt(at.X, at.Z) is { } panel)
                return PanelHeight(panel, at.X, at.Z, out _);
            var ct = (float)Math.Clamp(t, rampLow, rampHigh);
            if (Math.Abs(w) <= half + 1e-4)
                return deck(ct, (float)w);
            var side = w < 0 ? -1f : 1f;
            var edge = deck(ct, side * half);
            var out_ = (float)Math.Abs(w) - half;
            // (As the road's own shoulders: from a hair under the edge, down and out. The deck's edge is its surface, RoadRise over
            // the road's line.)
            return edge - ShoulderUnder - out_ * ((ShoulderDrop - ShoulderUnder) / ShoulderOut);
        }

        // The ground as it's drawn: the terrain splits each square of its grid into two triangles along one diagonal or the other
        // (Terrain.Diagonals); taken as the terrain takes it, so the embankment meets it exactly.
        var flipsOf = new Dictionary<(int, int), bool[]?>();
        bool Flipped(int gx, int gz)
        {
            const int quads = Terrain.Quads;
            var (px, pz) = (Math.DivRem(gx, quads, out var qx), Math.DivRem(gz, quads, out var qz));
            if (qx < 0) (px, qx) = (px - 1, qx + quads);
            if (qz < 0) (pz, qz) = (pz - 1, qz + quads);
            if (!flipsOf.TryGetValue((px, pz), out var flips))
            {
                var (x0, z0) = (px * quads, pz * quads);
                var count = 0;
                for (var iz = 0; iz < quads; iz++)
                    for (var ix = 0; ix < quads; ix++)
                    {
                        if (!_source.TryHeight(x0 + ix, z0 + iz, out var a) || !_source.TryHeight(x0 + ix + 1, z0 + iz, out var b)
                            || !_source.TryHeight(x0 + ix, z0 + iz + 1, out var c) || !_source.TryHeight(x0 + ix + 1, z0 + iz + 1, out var d))
                            continue;
                        if (MathF.Abs(b - c) - MathF.Abs(a - d) <= Terrain.FlipGain * _source.Spacing)
                            continue;
                        flips ??= new bool[quads * quads];
                        flips[iz * quads + ix] = true;
                        count++;
                    }

                flipsOf[(px, pz)] = flips = count >= Terrain.FewestFlips ? flips : null;
            }

            return flips is not null && flips[qz * quads + qx];
        }

        float Ground(double x, double z)
        {
            var spacing = _source.Spacing;
            var (fx, fz) = (x / spacing, z / spacing);
            var (gx, gz) = ((int)Math.Floor(fx), (int)Math.Floor(fz));
            var (tx, tz) = ((float)(fx - gx), (float)(fz - gz));
            if (!_source.TryHeight(gx, gz, out var a) || !_source.TryHeight(gx + 1, gz, out var b) || !_source.TryHeight(gx, gz + 1, out var c)
                || !_source.TryHeight(gx + 1, gz + 1, out var d))
                return _source.HeightAt(x, z);
            return Flipped(gx, gz)
                ? tx >= tz ? a + (b - a) * tx + (d - b) * tz : a + (c - a) * tz + (d - c) * tx
                : tx + tz <= 1f ? a + (b - a) * tx + (c - a) * tz : d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
        }

        // The crossed lines' ballast beds, as their own meshes draw them (FarRail, BedSection): the highest bed surface at (x, z).
        var beds = trackList.Select(t => t.Leg.Line).Distinct().Select(line => (Line: line, Leg: new Leg(line, 0, 1))).ToList();
        var sections = new Dictionary<(int, int), (float U, float Y)[]>();
        float? BedTop(double x, double z)
        {
            float? best = null;
            foreach (var (line, leg) in beds)
            {
                var (bi, bd) = (0, double.MaxValue);
                for (var i = 0; i < line.Count; i++)
                {
                    var dd = (line.X[i] - x) * (line.X[i] - x) + (line.Z[i] - z) * (line.Z[i] - z);
                    if (dd < bd)
                        (bi, bd) = (i, dd);
                }

                if (bd > 60.0 * 60.0)
                    continue;
                var s = line.S[bi];
                for (var k = 0; k < 6; k++)
                {
                    var (qx, qz, _, fx, fz, _, _) = leg.At(s);
                    s += (float)((x - qx) * fx + (z - qz) * fz);
                }

                if (s <= 0f || s >= line.S[^1])
                    continue;
                var (lx, lz, y, _, _, rx, rz) = leg.At(s);
                var u = (float)((x - lx) * rx + (z - lz) * rz);
                var key = (line.Index, (int)MathF.Round(s * 4f));
                if (!sections.TryGetValue(key, out var p))
                    sections[key] = p = BedSection(line, false, 0f, 0f, 0f, line.SinkAt(s));
                var n = p.Length;
                if (u <= p[0].U || u >= p[n - 1].U)
                    continue;
                float h;
                if (u < p[2].U)
                    h = p[0].Y + (p[2].Y - p[0].Y) * (u - p[0].U) / (p[2].U - p[0].U);
                else if (u > p[n - 3].U)
                    h = p[n - 3].Y + (p[n - 1].Y - p[n - 3].Y) * (u - p[n - 3].U) / (p[n - 1].U - p[n - 3].U);
                else
                {
                    h = p[n - 3].Y;
                    for (var i = 2; i < n - 3; i++)
                        if (u <= p[i + 1].U)
                        {
                            var span = p[i + 1].U - p[i].U;
                            h = span < 1e-5f ? p[i + 1].Y : p[i].Y + (p[i + 1].Y - p[i].Y) * (u - p[i].U) / span;
                            break;
                        }
                }

                if (best is null || y + h > best)
                    best = y + h;
            }

            return best;
        }

        // --- Every edge the top has to have, cut into a grid over its outline. ---

        var segments = new List<((double T, double W) A, (double T, double W) B)>();
        void Segment((double X, double Z) a, (double X, double Z) b) => segments.Add((Local(a.X, a.Z), Local(b.X, b.Z)));
        foreach (var side in (float[])[-1f, 1f])
        {
            segments.Add(((rampLow, side * half), (rampHigh, side * half)));
            segments.Add(((rampLow, side * (half + 0.02f)), (rampHigh, side * (half + 0.02f))));
        }

        foreach (var track in tracks)
        {
            var (f, to) = (track.From, track.To);
            foreach (var s in (double[])[-1, 1])
                Segment(track.World(s * reach, f), track.World(s * reach, to));
            foreach (var a in (double[])[f, to])
                Segment(track.World(-reach, a), track.World(reach, a));
            foreach (var (from, end) in slots)
                foreach (var lat in (double[])[from, from + SlotWall, end - SlotWall, end])
                    Segment(track.World(lat, f), track.World(lat, to));
            if (joints)
                for (var a = f + PanelLength; a < to - 0.1f; a += PanelLength)
                    foreach (var o in (double[])[-0.01, 0, 0.01])
                        Segment(track.World(-reach, a + o), track.World(reach, a + o));
        }

        // The grid's lines, with the outline exactly at the ramps' ends and the shoulders' edges.
        static List<double> Lines(double from, double to)
        {
            var lines = new List<double> { from };
            for (var v = Math.Floor(from / CrossingCell) * CrossingCell + CrossingCell; v < to - 0.05; v += CrossingCell)
                if (v > from + 0.05)
                    lines.Add(v);
            lines.Add(to);
            return lines;
        }

        var ts = Lines(rampLow, rampHigh);
        var ws = Lines(-outer, outer);
        var pieces = new List<List<(double T, double W)>>();
        for (var c = 0; c + 1 < ts.Count; c++)
            for (var r = 0; r + 1 < ws.Count; r++)
            {
                var (t0, t1, w0, w1) = (ts[c], ts[c + 1], ws[r], ws[r + 1]);
                var cell = new List<List<(double T, double W)>> { new() { (t0, w0), (t1, w0), (t1, w1), (t0, w1) } };
                foreach (var (a, b) in segments)
                {
                    if (Math.Max(a.T, b.T) < t0 || Math.Min(a.T, b.T) > t1 || Math.Max(a.W, b.W) < w0 || Math.Min(a.W, b.W) > w1)
                        continue;
                    var next = new List<List<(double T, double W)>>();
                    foreach (var piece in cell)
                        next.AddRange(Split(piece, a, b));
                    cell = next;
                }

                pieces.AddRange(cell);
            }

        // --- What each piece is. ---

        var parts = new CrossingPart[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            var (t, w) = (pieces[i].Average(p => p.T), pieces[i].Average(p => p.W));
            var (x, z) = World(t, w);
            if (PanelAt(x, z) is { } panel)
            {
                PanelHeight(panel, x, z, out var slot);
                parts[i] = slot ? CrossingPart.Slot : CrossingPart.Panel;
            }
            else
                parts[i] = Math.Abs(w) <= half ? CrossingPart.Road : CrossingPart.Shoulder;
        }

        // --- The vertices, shared, with every vertex lying along another piece's edge put into that edge too, so the top's closed. ---

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

        var rings = pieces.Select(piece => piece.Select(VertexOf).ToList()).ToList();
        var near = new Dictionary<(int, int), List<int>>();
        for (var v = 0; v < vertices.Count; v++)
        {
            var key = ((int)Math.Floor(vertices[v].T / CrossingCell), (int)Math.Floor(vertices[v].W / CrossingCell));
            if (!near.TryGetValue(key, out var list))
                near[key] = list = [];
            list.Add(v);
        }

        for (var i = 0; i < rings.Count; i++)
        {
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

        // Near each end the crossing's moved across to where the road beyond actually runs, so their ends meet exactly.
        (double X, double Z) Placed((double T, double W) p)
        {
            var (x, z) = World(p.T, p.W);
            var shift = offLow * Math.Clamp((rampLow + 3.0 - p.T) / 3.0, 0, 1) + offHigh * Math.Clamp((p.T - (rampHigh - 3.0)) / 3.0, 0, 1);
            return (x + nx * shift, z + nz * shift);
        }

        var places = vertices.Select(Placed).ToArray();
        var heights = vertices.Select(p => Top(p.T, p.W, World(p.T, p.W))).ToArray();
        // The road's own surface at each vertex, panels or not: where the road's top meets a panel's edge it runs on under the
        // panel at its own height (the panel's wall stands over it), never up the panel's edge to its top.
        var roadHeights = vertices.Select(p => Top(p.T, p.W, World(p.T, p.W), false)).ToArray();

        // --- The triangles. ---

        var points = new List<float3>();
        var data = new List<float4>();
        var normals = new List<float3>();
        var indices = new List<uint>();
        var gravel = Code(LineMaterial.Gravel);
        var asphalt = Code(LineMaterial.Asphalt);
        float3 Relative(double x, double z, float y) => new((float)(x - baseX), y, (float)(z - baseZ));

        // The road's markings across it by how far across the crossing's own road a place is (w, before its ends are moved
        // across onto the roads running on): the crossing's road and the road beyond meet edge to edge, so their lines do too,
        // where measured from the road's line at one end they'd stand off it by as much as the road's end drifts.
        var across = road is { } dr && Dot(new float3((float)nx, 0f, (float)nz), dr.Across) < 0f ? -1f : 1f;
        float4 DataOf((double X, double Z) at, float3 p, CrossingPart part, double w = 0.0) => part switch
        {
            CrossingPart.Road => road is { } r
                ? new float4(across * (float)w, (Dot(p, r.Along) + r.AlongAt) % Wrap, r.Code, 1000f)
                : new float4(p.X, p.X + p.Z, asphalt, 0f),
            CrossingPart.Shoulder => new float4((float)WrapTexture(at.X), (float)WrapTexture(at.Z), gravel, 0f),
            _ => new float4(p.X, p.X + p.Z, paint, 0f),
        };

        // Each part's own copy of a vertex, its normal summed over that part's triangles (smooth within a part, creased between).
        var copies = new Dictionary<(int, CrossingPart), int>();
        int CopyOf(int v, CrossingPart part)
        {
            if (copies.TryGetValue((v, part), out var index))
                return index;
            var p = Relative(places[v].X, places[v].Z, part is CrossingPart.Panel or CrossingPart.Slot ? heights[v] : roadHeights[v]);
            copies[(v, part)] = points.Count;
            points.Add(p);
            normals.Add(new float3(0f, 0f, 0f));
            data.Add(DataOf(places[v], p, part, vertices[v].W));
            return points.Count - 1;
        }

        // A triangle facing `toward` (the game's front faces wind so the cross product points into the surface).
        void Triangle(int a, int b, int c, float3 toward, bool sum)
        {
            var normal = Cross(points[b] - points[a], points[c] - points[a]);
            if (Dot(normal, normal) < 1e-14f)
                return;
            if (Dot(normal, toward) < 0f)
            {
                (b, c) = (c, b);
                normal = normal * -1f;
            }

            indices.AddRange([(uint)a, (uint)c, (uint)b]);
            if (sum)
            {
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }
        }

        var up = new float3(0f, 1f, 0f);
        for (var i = 0; i < pieces.Count; i++)
        {
            // (The panels are slabs of their own, below.)
            if (parts[i] is CrossingPart.Panel or CrossingPart.Slot)
                continue;
            var ring = rings[i];
            var corners = ring.Select(v => CopyOf(v, parts[i])).ToArray();
            if (corners.Length == 3)
                Triangle(corners[0], corners[1], corners[2], up, true);
            else if (corners.Length == 4 && ring.Count == pieces[i].Count)
            {
                Triangle(corners[0], corners[1], corners[2], up, true);
                Triangle(corners[0], corners[2], corners[3], up, true);
            }
            else
            {
                // A fan from its middle, over every vertex along its edges.
                var (t, w) = (ring.Average(v => vertices[v].T), ring.Average(v => vertices[v].W));
                var place = Placed((t, w));
                var p = Relative(place.X, place.Z, Top(t, w, World(t, w), false));
                var centre = points.Count;
                points.Add(p);
                normals.Add(new float3(0f, 0f, 0f));
                data.Add(DataOf(place, p, parts[i], w));
                for (var k = 0; k < corners.Length; k++)
                    Triangle(centre, corners[k], corners[(k + 1) % corners.Length], up, true);
            }
        }

        // --- The sides. ---
        //
        // Along the road's two sides, an embankment of gravel from the top's edge outward and down at the ballast's slope till it
        // meets what's there, the ground or a track's ballast bed, wherever that lies: so the crossing never stands up on a wall
        // whatever the ground round it does (where the ground's shaped to its fill, the embankment's only a hand's width). At its
        // foot it goes on a little under, and then straight down. Across the road at each end the road runs on from it: there
        // it goes straight down, out of sight under the road's end.

        var uses = new Dictionary<(int, int), (int Count, int Piece)>();
        for (var i = 0; i < rings.Count; i++)
            for (var k = 0; k < rings[i].Count; k++)
            {
                var (a, b) = (rings[i][k], rings[i][(k + 1) % rings[i].Count]);
                var key = a < b ? (a, b) : (b, a);
                uses[key] = (uses.TryGetValue(key, out var u) ? u.Count + 1 : 1, i);
            }

        const float bank = 1f / NetworkLine.BallastSlope;
        const float bankMost = 10f;
        float Floor(double x, double z)
        {
            var g = Ground(x, z);
            return BedTop(x, z) is { } bed ? MathF.Max(g, bed) : g;
        }

        // The side's top edge: the shoulders' outer edge, under the panels as well (where it's hidden under them), so the
        // embankment runs unbroken the length of the side and heaps against a panel's wall where one stands out past it.
        float SideTop(int v)
        {
            var p = vertices[v];
            return deck((float)Math.Clamp(p.T, rampLow, rampHigh), (p.W < 0 ? -1f : 1f) * half) - ShoulderDrop;
        }

        // Each side vertex's foot: how far out the embankment runs from it, and how high its foot is.
        var feet = new Dictionary<int, (double X, double Z, float Y, bool Reached)>();
        (double X, double Z, float Y, bool Reached) Foot(int v)
        {
            if (feet.TryGetValue(v, out var known))
                return known;
            var (px, pz) = places[v];
            var sign = vertices[v].W < 0 ? -1.0 : 1.0;
            var (ox, oz) = (nx * sign, nz * sign);
            var y = SideTop(v);
            var reached = false;
            var d = 0.0;
            // Out till it's well under what it meets (so the straight edge from one foot to the next stays under it even where
            // what's under it curves, as a ballast bed's shoulder does), there where its foot lies (tested at the foot itself, so a floor that
            // drops away just past where it's met, as a ballast bed's fade ends over lower ground, can't leave the foot in the air).
            for (; d < bankMost; d += 0.05)
                if (y - (float)d * bank <= Floor(px + ox * d, pz + oz * d) - FootUnder)
                {
                    reached = true;
                    break;
                }

            d = Math.Min(d, bankMost);
            return feet[v] = (px + ox * d, pz + oz * d, y - (float)d * bank, reached);
        }

        // From (x, z) at height y, out along (ox, oz) at the ballast's slope till well under what's there: where it lands.
        (double X, double Z, float Y) Reach(double x, double z, float y, double ox, double oz)
        {
            var d = 0.0;
            for (; d < bankMost; d += 0.05)
                if (y - (float)d * bank <= Floor(x + ox * d, z + oz * d) - FootUnder)
                    break;
            d = Math.Min(d, bankMost);
            return (x + ox * d, z + oz * d, y - (float)d * bank);
        }

        int Point((double X, double Z) at, float y, float3 normal)
        {
            var p = Relative(at.X, at.Z, y);
            points.Add(p);
            normals.Add(normal);
            data.Add(DataOf(at, p, CrossingPart.Shoulder));
            return points.Count - 1;
        }

        // The outline's edges: each used by one piece of the road's top (round the panels the panels' own walls stand). How many
        // of the sides' edges each vertex has, so a run of side that stops (at a corner, or against a panel) is closed off.
        bool OnPanel(int piece) => parts[piece] is CrossingPart.Panel or CrossingPart.Slot;
        bool Side((double T, double W) p, (double T, double W) q) => Math.Abs(Math.Abs(p.W) - outer) < 1e-4 && Math.Abs(Math.Abs(q.W) - outer) < 1e-4;
        var outline = uses.Where(e => e.Value.Count == 1 && (!OnPanel(e.Value.Piece) || Side(vertices[e.Key.Item1], vertices[e.Key.Item2]))).Select(e => e.Key).ToList();
        var sideCount = new Dictionary<int, int>();
        foreach (var (a, b) in outline)
            if (Side(vertices[a], vertices[b]))
                foreach (var v in (int[])[a, b])
                    sideCount[v] = sideCount.GetValueOrDefault(v) + 1;

        var (worst, unreached, sideEdges) = (0f, 0, 0);
        foreach (var (a, b) in outline)
        {
            var (pa, pb) = (vertices[a], vertices[b]);
            var end = Math.Abs(pa.T - pb.T) < 1e-6 && (Math.Abs(pa.T - rampLow) < 1e-4 || Math.Abs(pa.T - rampHigh) < 1e-4);
            if (end)
            {
                var toward = new float3((float)(pa.T < 0 ? -dx : dx), 0f, (float)(pa.T < 0 ? -dz : dz));
                int Down(int v, bool bottom) => Point(places[v], bottom ? MathF.Min(roadHeights[v], Ground(places[v].X, places[v].Z)) - CrossingSkirt : roadHeights[v], toward);
                var (ta, tb, ba, bb) = (Down(a, false), Down(b, false), Down(a, true), Down(b, true));
                Triangle(ta, tb, bb, toward, false);
                Triangle(ta, bb, ba, toward, false);
                continue;
            }

            if (!Side(pa, pb))
                continue;
            sideEdges++;
            var (fa, fb) = (Foot(a), Foot(b));
            var sign = pa.W < 0 ? -1f : 1f;
            var outward = new float3((float)nx * sign, 0f, (float)nz * sign);
            var slopeNormal = outward * bank + new float3(0f, 1f, 0f);
            var zero = new float3(0f, 0f, 0f);
            // The embankment, top edge to foot.
            var (ea, eb, sa, sb) = (Point(places[a], SideTop(a), zero), Point(places[b], SideTop(b), zero), Point((fa.X, fa.Z), fa.Y, zero), Point((fb.X, fb.Z), fb.Y, zero));
            Triangle(ea, eb, sb, slopeNormal, true);
            Triangle(ea, sb, sa, slopeNormal, true);
            // Then, where it never met anything, straight down from its foot.
            int Under((double X, double Z, float Y, bool Reached) f, bool bottom) =>
                Point((f.X, f.Z), bottom ? MathF.Min(f.Y, Ground(f.X, f.Z)) - CrossingSkirt : f.Y, outward);
            if (!fa.Reached || !fb.Reached)
            {
                var (ua, ub, la, lb) = (Under(fa, false), Under(fb, false), Under(fa, true), Under(fb, true));
                Triangle(ua, ub, lb, outward, false);
                Triangle(ua, lb, la, outward, false);
            }
            // Where a run of side stops (a corner, where the road runs on), the embankment's end sloped off too, hipped: down
            // along the road and round the corner at the same slope, so no cut face is left there.
            foreach (var (v, other, f) in new[] { (a, b, fa), (b, a, fb) })
            {
                if (sideCount.GetValueOrDefault(v) != 1)
                    continue;
                var way = vertices[v].T > vertices[other].T ? 1.0 : -1.0;
                var (ox2, oz2) = (nx * sign, nz * sign);
                var (ax2, az2) = (dx * way, dz * way);
                var (hx, hz) = ((ox2 + ax2) * Math.Sqrt(0.5), (oz2 + az2) * Math.Sqrt(0.5));
                var (px, pz) = places[v];
                var top = SideTop(v);
                var e = Reach(px, pz, top, ax2, az2);
                var c = Reach(px, pz, top, hx, hz);
                var endNormal = new float3((float)ax2, 0f, (float)az2) * bank + new float3(0f, 1f, 0f);
                var (tv, tf, tc, te) = (Point(places[v], top, zero), Point((f.X, f.Z), f.Y, zero), Point((c.X, c.Z), c.Y, zero), Point((e.X, e.Z), e.Y, zero));
                Triangle(tv, tf, tc, slopeNormal + endNormal, true);
                Triangle(tv, tc, te, endNormal, true);
            }

            foreach (var f in new[] { fa, fb })
            {
                worst = MathF.Max(worst, f.Y - Floor(f.X, f.Z));
                if (!f.Reached)
                    unreached++;
            }
        }

        // --- The panels. ---
        //
        // Each track's panels as one slab, square to the track as they're laid (not cut along the road's edges): their top level
        // with the rail tops, with its slots and joints; their four sides straight down to the ballast (or the ground, or into the
        // road's box where they lie in it), in the panels' own material.
        //
        // Planked (fill): only a timber plank either side of each rail's slot is wood; the rest of the slab, between the rails and out to
        // its edges, is asphalt (fill), all of it level with the rail tops. Each strip has its own vertices where the two meet.
        float PaintAcross(double lat) => fill is { } asphalt && !slots.Any(s => lat > s.From - PlankWidth && lat < s.To + PlankWidth) ? asphalt : paint;
        foreach (var track in tracks)
        {
            var lats = new List<double> { -reach, reach };
            foreach (var (from, to) in slots)
            {
                lats.AddRange([from, from + SlotWall, to - SlotWall, to]);
                if (fill is not null)
                    lats.AddRange([from - PlankWidth, to + PlankWidth]);
            }

            lats = [.. lats.Where(l => l >= -reach - 1e-6 && l <= reach + 1e-6).Distinct().Order()];
            var bands = Enumerable.Range(0, lats.Count - 1).Select(i => PaintAcross((lats[i] + lats[i + 1]) * 0.5)).ToArray();
            var alongs = new List<double> { track.From, track.To };
            for (var a = track.From + 0.5; a < track.To - 0.05; a += 0.5)
                alongs.Add(a);
            if (joints)
                for (var a = track.From + PanelLength; a < track.To - 0.1; a += PanelLength)
                    alongs.AddRange([a - 0.01, a, a + 0.01]);
            alongs = [.. alongs.Distinct().Order()];

            var tops = new float[lats.Count, alongs.Count];
            var at = new (double X, double Z)[lats.Count, alongs.Count];
            for (var i = 0; i < lats.Count; i++)
                for (var j = 0; j < alongs.Count; j++)
                {
                    at[i, j] = track.World(lats[i], alongs[j]);
                    tops[i, j] = PanelHeight(track, at[i, j].X, at[i, j].Z, out _);
                }

            // A vertex for each grid point in each paint that meets there (one, but where a plank meets the asphalt).
            var grid = new Dictionary<(int I, int J, float Paint), int>();
            int SlabTop(int i, int j, float band)
            {
                if (grid.TryGetValue((i, j, band), out var index))
                    return index;
                points.Add(Relative(at[i, j].X, at[i, j].Z, tops[i, j]));
                normals.Add(new float3(0f, 0f, 0f));
                data.Add(new float4((float)lats[i], (float)(alongs[j] - track.From), band, 0f));
                return grid[(i, j, band)] = points.Count - 1;
            }

            for (var i = 0; i + 1 < lats.Count; i++)
                for (var j = 0; j + 1 < alongs.Count; j++)
                {
                    var band = bands[i];
                    Triangle(SlabTop(i, j, band), SlabTop(i + 1, j, band), SlabTop(i + 1, j + 1, band), up, true);
                    Triangle(SlabTop(i, j, band), SlabTop(i + 1, j + 1, band), SlabTop(i, j + 1, band), up, true);
                }

            // Its sides, edge by edge round it: (i, j) of each corner, and the way out; each stretch in the paint of the strip it's under.
            void Wall((int I, int J)[] run, double fx, double fz)
            {
                var toward = new float3((float)fx, 0f, (float)fz);
                var u = 0.0;
                for (var k = 0; k + 1 < run.Length; k++)
                {
                    var (a, b) = (run[k], run[k + 1]);
                    var side = bands[Math.Min(Math.Min(a.I, b.I), bands.Length - 1)];
                    var length = Math.Sqrt((at[b.I, b.J].X - at[a.I, a.J].X) * (at[b.I, b.J].X - at[a.I, a.J].X) + (at[b.I, b.J].Z - at[a.I, a.J].Z) * (at[b.I, b.J].Z - at[a.I, a.J].Z));
                    int Corner((int I, int J) c, bool bottom, double cu)
                    {
                        var (x, z) = at[c.I, c.J];
                        var y = bottom ? MathF.Min(tops[c.I, c.J], Floor(x, z)) - 0.05f : tops[c.I, c.J];
                        points.Add(Relative(x, z, y));
                        normals.Add(toward);
                        data.Add(new float4((float)cu, y, side, 0f));
                        return points.Count - 1;
                    }

                    var (ta, tb, ba, bb) = (Corner(a, false, u), Corner(b, false, u + length), Corner(a, true, u), Corner(b, true, u + length));
                    Triangle(ta, tb, bb, toward, false);
                    Triangle(ta, bb, ba, toward, false);
                    u += length;
                }
            }

            var (lastI, lastJ) = (lats.Count - 1, alongs.Count - 1);
            Wall([.. Enumerable.Range(0, alongs.Count).Select(j => (0, j))], -track.Rx, -track.Rz);
            Wall([.. Enumerable.Range(0, alongs.Count).Select(j => (lastI, j))], track.Rx, track.Rz);
            Wall([.. Enumerable.Range(0, lats.Count).Select(i => (i, 0))], -track.Fx, -track.Fz);
            Wall([.. Enumerable.Range(0, lats.Count).Select(i => (i, lastJ))], track.Fx, track.Fz);
        }

        if (Environment.GetEnvironmentVariable("RW_CROSSING_AUDIT") is not null)
        {
            var sb = new System.Text.StringBuilder();
            var worstOver = (D: -9f, X: 0.0, Z: 0.0);
            var capWorst = (D: -9f, T: 0.0, W: 0.0);
            for (var gt = rampLow - 6.0; gt <= rampHigh + 6.0; gt += 0.5)
                for (var gw = -outer - 8.0; gw <= outer + 8.0; gw += 0.5)
                {
                    var (x, z) = World(gt, gw);
                    if (BedTop(x, z) is { } bt)
                    {
                        if (_lines.BedClearance(x, z) is { } cap && cap - bt > capWorst.D)
                            capWorst = (cap - bt, gt, gw);
                        var over = Ground(x, z) - bt;
                        if (over > worstOver.D)
                            worstOver = (over, x, z);
                        if (over > -0.01f && sb.Length < 1500)
                            sb.Append($"[t{gt:F1} w{gw:F1} {over:+0.00}] ");
                    }
                }

            RailWerkz.Core.Logging.Log.Info($"Crossing ground audit {id} ({string.Join("/", beds.Select(b => $"{(Highball.Routes.RailClass)b.Line.Class} bed {b.Line.BedHeight:F2} top {b.Line.BallastHalfTop:F2}"))}): highest ground over the drawn bed {worstOver.D:+0.00;-0.00} m at {worstOver.X:F1},{worstOver.Z:F1}; brush limit over the drawn bed by up to {capWorst.D:+0.00;-0.00} m (t {capWorst.T:F1} w {capWorst.W:F1}); {sb}");
        }

        if (Environment.GetEnvironmentVariable("RW_CROSSING_AUDIT") is not null)
            RailWerkz.Core.Logging.Log.Info($"Crossing audit {id}: {sideEdges} side edges, highest wall left under an embankment's foot {worst:F2} m, {unreached} feet that never met the ground");

        if (indices.Count == 0)
            return null;
        var unit = normals.Select(n => n * (1f / MathF.Max(MathF.Sqrt(Dot(n, n)), 1e-9f))).ToArray();
        return new CrossingMesh([.. points], unit, [.. data], [.. indices]);
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

    private static double WrapTexture(double v) => v - Math.Floor(v / WorldRenderer.TexturePeriod) * WorldRenderer.TexturePeriod;
}
