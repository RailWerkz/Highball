namespace Highball.World;

/// A slope end of a bridge, worked out once: every piece of it and the ground round it come from this, so none of them measures where
/// the others are for itself. Laid out square to the deck at its end: along (a), into the gap from the end, and across (c), to the
/// deck's right, with u = |c|.
///
///   Under the deck (u under DeckHalf, a past 0): the slope's plane (NetworkLines.SlopeAt), up to the seat under the deck's beams.
///   Behind the deck's end (a under 0): the approach's formation, its ballast laid on it (NetworkLines.BallastFlanks).
///   A wing wall under each edge of the deck (u from DeckHalf to DeckHalf + WallThick), from WallFrom behind the end down to the slope's
///   toe, its top a little proud of the bank outside it; inside it the ground climbs from the slope to the bank, out of sight.
///   Outside the walls, the bank coned round them: the slope's plane carried on past the seat up to the track's level, falling away
///   outward at the fill slope, and cut back to it where the ground had been filled higher (a hillside as it was may stand).
///   A concrete block at each corner of the deck's end, over the wall's start, holding back the ballast and the bank's top.
public sealed class BridgeEndShape
{
    public const float WallThick = 0.6f;
    /// How far behind the deck's end the wing walls start, metres.
    public const float WallFrom = -2.5f;
    /// How far proud of the bank outside them the walls' tops stand.
    public const float WallProud = 0.25f;
    /// A corner block: how far behind the deck's end and how far past it, across from inside the deck's edge to outside the wall.
    public const float BlockBehind = 1.8f;
    public const float BlockAhead = 0.3f;
    public const float BlockIn = 0.5f;
    public const float BlockOut = 0.35f;
    public const float BlockRise = 0.2f;
    /// How far behind the deck's end the ground's shaped, and over how much at the outline's edges it eases back to the ground as it was.
    public const float Behind = 8f;
    public const float Ease = 6f;
    /// The back wall behind the beams' ends, and the seat they sit on, along the deck: out of sight under it.
    public const float BackWall = 0.7f;
    public const float Seat = 1.1f;

    public NetworkLine Line { get; }
    public BridgeEnd End { get; }
    private readonly NetworkLines _lines;

    public double Ex { get; }
    public double Ez { get; }
    /// Into the gap, and to its right.
    public double Fx { get; }
    public double Fz { get; }
    public double Rx => -Fz;
    public double Rz => Fx;
    public float DeckHalf { get; }
    public float DeckY { get; }
    public float SeatY { get; }
    /// How far along the toe of the slope is under each edge (left, right).
    public float ToeLeft { get; }
    public float ToeRight { get; }
    /// How far out across the bank reaches.
    public float Reach { get; }
    public (double X0, double Z0, double X1, double Z1) Bounds { get; }

    internal BridgeEndShape(NetworkLines lines, NetworkLine line, BridgeEnd end)
    {
        _lines = lines;
        Line = line;
        End = end;
        var p = Math.Clamp(end.Point, 0, line.Count - 2);
        var (dx, dz) = (line.X[p + 1] - line.X[p], line.Z[p + 1] - line.Z[p]);
        var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
        var inward = end.Start ? 1.0 : -1.0;
        (Fx, Fz) = (dx / length * inward, dz / length * inward);
        (Ex, Ez) = (line.X[end.Point], line.Z[end.Point]);
        DeckHalf = BridgePlanner.DeckHalf(line);
        DeckY = line.Y[end.Point];
        SeatY = DeckY - BridgePlanner.DeckDepth(line) - NetworkLines.BridgeClearanceOf;
        float Toe(float side)
        {
            for (var a = 0f; a < 80f; a += 0.25f)
            {
                var (x, z) = World(a, side * (DeckHalf + WallThick * 0.5f));
                if (lines.SlopeRise(end, x, z) <= end.Floor + 0.05f)
                    return a;
            }

            return 80f;
        }

        (ToeLeft, ToeRight) = (Toe(-1f), Toe(1f));
        Reach = DeckHalf + WallThick + MathF.Max(DeckY - end.Floor, 0f) * NetworkLines.SpillRun + Ease + 2f;
        var far = MathF.Max(ToeLeft, ToeRight) + Ease + 2f;
        var (minX, minZ, maxX, maxZ) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var (a, c) in ((float, float)[])[(-Behind, -Reach), (-Behind, Reach), (far, -Reach), (far, Reach)])
        {
            var (x, z) = World(a, c);
            (minX, minZ, maxX, maxZ) = (Math.Min(minX, x), Math.Min(minZ, z), Math.Max(maxX, x), Math.Max(maxZ, z));
        }

        Bounds = (minX, minZ, maxX, maxZ);
    }

    public (double X, double Z) World(float a, float c) => (Ex + Fx * a + Rx * c, Ez + Fz * a + Rz * c);
    public (float A, float C) Local(double x, double z) => ((float)((x - Ex) * Fx + (z - Ez) * Fz), (float)((x - Ex) * Rx + (z - Ez) * Rz));

    public float Toe(float c) => c < 0f ? ToeLeft : ToeRight;

    /// The track's height along the line at a along from the end (behind it, the approach's).
    public float TrackY(float a)
    {
        var s = Line.S[End.Point] + (End.Start ? a : -a);
        s = Math.Clamp(s, Line.S[0], Line.S[^1]);
        var i = Array.BinarySearch(Line.S, s);
        i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, Line.Count - 2);
        var t = Math.Clamp((s - Line.S[i]) / MathF.Max(Line.S[i + 1] - Line.S[i], 1e-4f), 0f, 1f);
        return Line.Y[i] + (Line.Y[i + 1] - Line.Y[i]) * t;
    }

    /// The approach's formation behind the end: what its bed stands on (as Conform lays it under the track).
    public float Formation(float a)
    {
        var sink = Line.SinkOf(End.Point);
        return TrackY(MathF.Min(a, 0f)) + sink - MathF.Min(sink * 0.3f, 0.06f);
    }

    /// The bank outside the walls at a place, before it falls away: the slope's plane carried on past the seat, no higher than the track.
    public float ConeTop(float a, double x, double z) => MathF.Min(_lines.SlopeRise(End, x, z), a < 0f ? Formation(a) : TrackY(a) - 0.05f);

    /// What's inside the deck's edge: the slope under it, or the formation behind its end.
    public float Inner(float a, double x, double z) => a >= 0f ? _lines.SlopeAt(Line, End, x, z) : Formation(a);

    /// The top of the wing wall on a side at a along.
    public float WallTop(float a, float side)
    {
        var (x, z) = World(a, side * (DeckHalf + WallThick));
        return MathF.Min(ConeTop(a, x, z) + WallProud, (a < 0f ? TrackY(a) : DeckY) + 0.05f);
    }

    /// Whether a place is out of sight inside the structure: a wing wall, a corner block, or the back wall and seat.
    public bool Hidden(float a, float c)
    {
        var u = MathF.Abs(c);
        if (u >= DeckHalf - 0.05f && u <= DeckHalf + WallThick + 0.05f && a >= WallFrom && a <= Toe(c) + 0.25f)
            return true;
        if (a >= -BlockBehind && a <= BlockAhead && u >= DeckHalf - BlockIn && u <= DeckHalf + WallThick + BlockOut)
            return true;
        return u <= DeckHalf && a >= -BackWall && a <= Seat;
    }

    /// The ground here as the end has it, and how wholly (1 inside, easing to 0 at the outline's edges); null outside it.
    /// least and most: what the ground's brought up to, and cut down to, from what it is (h) and was before any shaping (natural).
    public (float Least, float Most, float Weight)? Ground(double x, double z, float natural)
    {
        var (a, c) = Local(x, z);
        var u = MathF.Abs(c);
        var toe = Toe(c);
        var far = toe + Ease + 2f;
        if (a < -Behind || a > far || u > Reach)
            return null;
        var weight = MathF.Min(Math.Clamp((a + Behind) / 4f, 0f, 1f), MathF.Min(Math.Clamp((far - a) / Ease, 0f, 1f), Math.Clamp((Reach - u) / Ease, 0f, 1f)));
        if (u <= DeckHalf)
        {
            // (Under the deck past the toe, what's underneath has its own ground: the end's is only as far as the slope runs.)
            if (a > toe + 1f)
                return null;
            var inner = Inner(a, x, z);
            return (inner, inner, MathF.Max(weight, a <= toe ? 1f : 0f));
        }

        var side = MathF.Sign(c);
        var (ox, oz) = World(a, side * (DeckHalf + WallThick));
        var outer = ConeTop(a, ox, oz);
        if (u <= DeckHalf + WallThick)
        {
            // (Climbing well inside the wall, so the ground's triangles across the climb stay in it.)
            var (ix, iz) = World(a, side * DeckHalf);
            var climb = Math.Clamp((u - DeckHalf - 0.1f) / (WallThick * 0.4f), 0f, 1f);
            var inside = Inner(a, ix, iz) + (outer - Inner(a, ix, iz)) * climb;
            return (inside, inside, weight);
        }

        var across = u - DeckHalf - WallThick;
        var top = ConeTop(a, x, z);
        var cone = top - across * NetworkLines.FillSlopeOf;
        var keep = MathF.Max(cone, MathF.Min(natural, top + MathF.Max(0f, across - 4f) * NetworkLines.CutSlopeOf));
        return (cone, keep, weight);
    }
}

public sealed partial class NetworkLines
{
    public static float BridgeClearanceOf => BridgeClearance;
    public static float FillSlopeOf => FillSlope;
    public static float CutSlopeOf => CutSlope;

    private List<BridgeEndShape>? _endShapes;
    private readonly object _endShapesLock = new();

    /// Every slope end's shape.
    public IReadOnlyList<BridgeEndShape> EndShapes
    {
        get
        {
            if (_endShapes is { } found)
                return found;
            lock (_endShapesLock)
                return _endShapes ??= [.. _lines.Where(l => l.Structures.Contains(Structure.Bridge))
                    .SelectMany(l => BridgeEndsOf(l).Where(e => e.Kind == BridgeEndKind.Slope && e.Under is not null).Select(e => new BridgeEndShape(this, l, e)))];
        }
    }

    /// A line's slope end at a point, if it has one there.
    public BridgeEndShape? EndShapeAt(NetworkLine line, int point) => EndShapes.FirstOrDefault(e => e.Line == line && e.End.Point == point);

    /// What the slope ends over a place have the ground be, all at once (null where none reaches): where an end has it exactly, under
    /// its deck or in its walls, that wins over another's bank reaching there, the lowest of them if two do (decks side by side each
    /// keep their own clear); otherwise the most any brings it up to wins over the least any cuts it down to (one end after another,
    /// two side by side undid each other).
    public (float Least, float Most, float Weight, bool Exact)? EndGround(double x, double z, float natural)
    {
        (float Least, float Most, float Weight, bool Exact)? said = null;
        foreach (var shape in EndShapes)
        {
            var b = shape.Bounds;
            if (x < b.X0 || x > b.X1 || z < b.Z0 || z > b.Z1 || shape.Ground(x, z, natural) is not { } g)
                continue;
            var exact = g.Most - g.Least < 0.01f && g.Weight >= 0.999f;
            if (said is not { } had)
                said = (g.Least, g.Most, g.Weight, exact);
            else if (exact && had.Exact)
                said = had.Least <= g.Least ? had : (g.Least, g.Most, g.Weight, true);
            else if (exact)
                said = (g.Least, g.Most, g.Weight, true);
            else if (!had.Exact)
                said = (MathF.Max(had.Least, g.Least), MathF.Min(had.Most, g.Most), MathF.Max(had.Weight, g.Weight), false);
        }

        return said;
    }

    /// The ground at a place as the ends have it, from what it is (h) and was (natural).
    public static float Shaped(float h, (float Least, float Most, float Weight, bool Exact) g) =>
        h + (MathF.Min(MathF.Max(h, g.Least), MathF.Max(g.Least, g.Most)) - h) * g.Weight;

    /// The ground round every slope end shaped as they have it (EndGround): the last word, after everything else. stamped: the places
    /// wholly an end's, which nothing after may change.
    private void StampEnds(float[] heights, float[] natural, bool[] stamped, int samples, int gx0, int gz0)
    {
        var (x0, z0) = (gx0 * (double)Sp, gz0 * (double)Sp);
        var (x1, z1) = (x0 + (samples - 1) * (double)Sp, z0 + (samples - 1) * (double)Sp);
        if (!EndShapes.Any(e => e.Bounds.X1 >= x0 && e.Bounds.X0 <= x1 && e.Bounds.Z1 >= z0 && e.Bounds.Z0 <= z1))
            return;
        var (bx0, bz0, bx1, bz1) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var e in EndShapes.Where(e => e.Bounds.X1 >= x0 && e.Bounds.X0 <= x1 && e.Bounds.Z1 >= z0 && e.Bounds.Z0 <= z1))
            (bx0, bz0, bx1, bz1) = (Math.Min(bx0, e.Bounds.X0), Math.Min(bz0, e.Bounds.Z0), Math.Max(bx1, e.Bounds.X1), Math.Max(bz1, e.Bounds.Z1));
        var (ix0, ix1) = (Math.Max(0, (int)Math.Floor(bx0 / Sp) - gx0), Math.Min(samples - 1, (int)Math.Ceiling(bx1 / Sp) - gx0));
        var (iz0, iz1) = (Math.Max(0, (int)Math.Floor(bz0 / Sp) - gz0), Math.Min(samples - 1, (int)Math.Ceiling(bz1 / Sp) - gz0));
        for (var z = iz0; z <= iz1; z++)
            for (var x = ix0; x <= ix1; x++)
            {
                var sample = z * samples + x;
                if (EndGround((x + gx0) * (double)Sp, (z + gz0) * (double)Sp, natural[sample]) is not { } g)
                    continue;
                heights[sample] = Shaped(heights[sample], g);
                if (g.Weight >= 0.999f)
                    stamped[sample] = true;
            }
    }
}
