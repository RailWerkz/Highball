namespace Highball.World;

/// What a bridge's end stands on.
/// Bank: the ground as it comes (a bridge over a river or a valley, with nothing mapped under its ends).
/// Slope: a spill-through end, the bank running down under the deck at SpillRun to one, paved, with the deck on a seat at its top.
/// Wall: a full-height abutment down to what's underneath, its wing walls in line with it holding the bank back, where there's no
/// room for a slope before the road or track below.
/// Joined: the deck carries straight on into another line's bridge (lanes merging or parting on it): no end at all.
public enum BridgeEndKind { Bank, Slope, Wall, Joined }

/// One end of a line's bridged stretch: its point, which end it is, what it stands on, how far it is along the deck (metres, from the
/// end) to the edge of what runs underneath, the height of that, and how far out each side its wing walls reach.
/// Under: the road or track it stands beside (a slope's toe and a wall's face run along it), how far along it the deck crosses, and
/// which side of it the end is (+1 its right).
/// Footprint: how far along what's underneath the deck reaches either side of where its middle crosses.
/// Flat: how far past the deck's side the approach's bank stays at its full height (its formation is wider than the deck) before it
/// falls away: a wall along what's underneath keeps its top that high so far, and the bank doesn't stand over it.
/// Setback: how far a wall's face, or a slope's toe, stands further back from what's underneath than it would (set by hand).
/// Cap: how far past the deck's side a wall runs along what's underneath at most (set by hand; null: as far as the bank needs).
public readonly record struct BridgeEnd(int Point, bool Start, BridgeEndKind Kind, float Run, float Floor, float Wing, NetworkLine? Under = null, float UnderS = 0f, int Side = 0,
    float Footprint = 0f, float Flat = 0f, float Setback = 0f, float? Cap = null, bool Set = false, NetworkLine? Deck = null)
{
    /// Where the deck crosses what's underneath (world x, z), its way there, and its right.
    public (double X, double Z, float Fx, float Fz, float Rx, float Rz) UnderFrame()
    {
        var u = Under!;
        var i = Array.BinarySearch(u.S, UnderS);
        i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, u.Count - 2);
        var t = Math.Clamp((UnderS - u.S[i]) / MathF.Max(u.S[i + 1] - u.S[i], 1e-4f), 0f, 1f);
        var (dx, dz) = ((float)(u.X[i + 1] - u.X[i]), (float)(u.Z[i + 1] - u.Z[i]));
        var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-5f);
        (dx, dz) = (dx / length, dz / length);
        return (u.X[i] + (u.X[i + 1] - u.X[i]) * t, u.Z[i] + (u.Z[i + 1] - u.Z[i]) * t, dx, dz, -dz, dx);
    }

    /// How far a place is from what's underneath, square across it, on this end's side (negative: the other side).
    public float FromUnder(double x, double z)
    {
        var (ux, uz, _, _, rx, rz) = UnderFrame();
        return (float)((x - ux) * rx + (z - uz) * rz) * Side;
    }
}

/// Choosing each bridge end's kind, from what crosses under the deck nearest that end: a slope where the bank has room to come down to
/// what's underneath at SpillRun to one past a SeatWidth seat (and a shoulder's width short of it), a wall where it hasn't.
public sealed partial class NetworkLines
{
    /// A paved slope's run per metre of fall (as the fill slopes are).
    public const float SpillRun = 1.5f;
    /// How far under the deck the seat at a slope's top reaches, before the slope falls away.
    public const float SeatWidth = 1.5f;
    /// How far into the gap a wall's face stands from the deck's end.
    public const float WallFace = 1.5f;
    /// Room kept between an end's slope or wall and the edge of a road or track running underneath.
    private const float UnderShoulder = 1.0f;
    public const float UnderShoulderOf = UnderShoulder;

    /// A wall end's wall: its face this far past the paving of what runs underneath, this thick, and running at most so far along it either
    /// way from the deck.
    public const float WallOff = 0.8f;
    public const float WallThick = 0.6f;
    public const float WallMost = 80f;

    /// How thick a slope's paving slab is: the bank's cut this far under its top.
    public const float SlabDepth = 0.35f;

    /// The top of a slope end's paving at a place under it: a plane along what's underneath, rising from a shoulder's width off its edge
    /// at SpillRun to one, up to the seat just under the deck's beams.
    public float SlopeAt(NetworkLine line, BridgeEnd end, double x, double z)
    {
        var off = end.FromUnder(x, z) - end.Under!.Formation - UnderShoulder - end.Setback;
        var top = line.Y[end.Point] - BridgePlanner.DeckDepth(line) - BridgeClearance;
        return MathF.Min(top, end.Floor - RoadClearance + MathF.Max(off, 0f) / SpillRun);
    }

    /// A slope end's plane at a place, not stopped at the seat: rising on at SpillRun to one from its toe (beside the deck, where the
    /// bank coned round its wing walls climbs to the approach's height).
    public float SlopeRise(BridgeEnd end, double x, double z)
    {
        var off = end.FromUnder(x, z) - end.Under!.Formation - UnderShoulder - end.Setback;
        return end.Floor - RoadClearance + MathF.Max(off, 0f) / SpillRun;
    }

    private Dictionary<NetworkLine, List<BridgeEnd>>? _bridgeEnds;
    private Dictionary<NetworkLine, List<BridgeEnd>>? _wallsBeside;

    /// Behind a wall's face, how deep its body is: its own thickness and, over that, the strip let down behind it (WallTrench samples),
    /// which it covers, so the terrain's triangles rise behind it and never through its face (they're too coarse to start at its back).
    private const float WallTrench = 1.5f;
    public float WallBody => WallThick + WallTrench * Sp;

    /// The wall ends standing beside each line (that line their Under).
    private Dictionary<NetworkLine, List<BridgeEnd>> WallsBeside()
    {
        if (_wallsBeside is null)
        {
            BridgeEndsOf(_lines[0]);
            lock (_bridgeEndsLock)
                _wallsBeside ??= _bridgeEnds!.Values.SelectMany(e => e).Where(e => e.Kind == BridgeEndKind.Wall && e.Under is not null)
                    .GroupBy(e => e.Under!).ToDictionary(g => g.Key, g => g.ToList());
        }

        return _wallsBeside;
    }

    private readonly object _bridgeEndsLock = new();

    /// Behind a wall past the deck, the bank it holds: the approach's embankment filled up to (EmbankmentAt), or where the ground's higher,
    /// cut down to the wall's top for BankHold behind it, then rising away at the fill slope's run; as far as BankReach behind it.
    private const float BankHold = 1.5f;
    private const float BankReach = 30f;

    /// Where a wall end's wall stands at s along what's underneath: its face (world x, z), the way out from it towards the bank, and the
    /// road's height there.
    public (double X, double Z, float Rx, float Rz, float Floor) WallFaceAt(BridgeEnd end, float s)
    {
        var under = end.Under!;
        var i = Array.BinarySearch(under.S, s);
        i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, under.Count - 2);
        var t = Math.Clamp((s - under.S[i]) / MathF.Max(under.S[i + 1] - under.S[i], 1e-4f), 0f, 1f);
        var (x, z) = (under.X[i] + (under.X[i + 1] - under.X[i]) * t, under.Z[i] + (under.Z[i + 1] - under.Z[i]) * t);
        var floor = under.Y[i] + (under.Y[i + 1] - under.Y[i]) * t;
        var (dx, dz) = ((float)(under.X[i + 1] - under.X[i]), (float)(under.Z[i + 1] - under.Z[i]));
        var length = MathF.Max(MathF.Sqrt(dx * dx + dz * dz), 1e-5f);
        var (rx, rz) = (-dz / length * end.Side, dx / length * end.Side);
        var off = (under.Rail ? under.Formation : PavedHalf(under, i)) + WallOff + end.Setback;
        return (x + rx * off, z + rz * off, rx, rz, floor);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(NetworkLine, int), (float Low, float High)> _wallSpans = new();

    /// How far along what's underneath a wall end's wall is under its deck (from Low to High): there its top is the deck's seat, and past
    /// it the bank's. Worked out once from the deck's bridged stretch, the same for the wall and the ground behind it.
    /// Whether a place is under a wall end's deck (its bridged stretch from this end inward, its whole width).
    public bool DeckOver(BridgeEnd end, double x, double z)
    {
        var deck = end.Deck!;
        var (first, last) = (end.Point, end.Point);
        if (end.Start)
            while (last < deck.Structures.Length && deck.Structures[last] == Structure.Bridge)
                last++;
        else
            while (first > 0 && deck.Structures[first - 1] == Structure.Bridge)
                first--;
        var half = BridgePlanner.DeckHalf(deck) + 0.3;
        for (var p = first; p < last; p++)
        {
            var (ax, az, dx, dz) = (deck.X[p], deck.Z[p], deck.X[p + 1] - deck.X[p], deck.Z[p + 1] - deck.Z[p]);
            var length2 = Math.Max(dx * dx + dz * dz, 1e-9);
            var t = ((x - ax) * dx + (z - az) * dz) / length2;
            if (t < 0.0 || t > 1.0)
                continue;
            var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
            if (ex * ex + ez * ez <= half * half)
                return true;
        }

        return false;
    }

    private (float Low, float High) WallSpan(BridgeEnd end)
    {
        var deck = end.Deck!;
        return _wallSpans.GetOrAdd((deck, end.Point), _ =>
        {
            // The deck's bridged stretch, from this end inward.
            var (first, last) = (end.Point, end.Point);
            if (end.Start)
                while (last < deck.Structures.Length && deck.Structures[last] == Structure.Bridge)
                    last++;
            else
                while (first > 0 && deck.Structures[first - 1] == Structure.Bridge)
                    first--;
            var half = BridgePlanner.DeckHalf(deck);
            bool Covered(float s)
            {
                var (x, z, _, _, _) = WallFaceAt(end, s);
                var (best, across) = (double.MaxValue, 0.0);
                for (var p = first; p < last; p++)
                {
                    var (ax, az, dx, dz) = (deck.X[p], deck.Z[p], deck.X[p + 1] - deck.X[p], deck.Z[p + 1] - deck.Z[p]);
                    var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                    var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                    if (ex * ex + ez * ez < best)
                        (best, across) = (ex * ex + ez * ez, ((x - ax) * -dz + (z - az) * dx) / Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6));
                }

                // (Past the deck's end too, within its width: on a skew, the wedge the abutment fills, as the wall's mesh has it.)
                return Math.Abs(across) <= half + 0.3 && best < Math.Pow(half + 25.0, 2);
            }

            // (Scanned the whole way along, not grown out from where the middles cross: on a skew the face there, a road's half width off
            // its middle, needn't be under the deck at all, and the deck covers the face somewhere along from it.)
            var (low, high) = (float.MaxValue, float.MinValue);
            for (var at = end.UnderS - WallMost; at <= end.UnderS + WallMost; at += 0.5f)
                if (at >= 0f && at <= end.Under!.S[^1] && Covered(at))
                    (low, high) = (MathF.Min(low, at), MathF.Max(high, at));
            return low <= high ? (low, high) : (end.UnderS, end.UnderS);
        });
    }

    public string WallSpanOf(BridgeEnd end) => end.Deck is null ? "-" : WallSpan(end).ToString();

    /// How far past the deck's side a place at s along what's underneath is, along it (0 under the deck).
    public float WallBeyond(BridgeEnd end, float s)
    {
        if (end.Deck is null)
            return MathF.Abs(s - end.UnderS);
        var (low, high) = WallSpan(end);
        return s < low ? low - s : s > high ? s - high : 0f;
    }

    /// How far out from what's underneath a wall end's face stands (as BridgeEnd.FromUnder measures).
    public float WallFaceOff(BridgeEnd end)
    {
        var (x, z, _, _, _) = WallFaceAt(end, end.UnderS);
        return end.FromUnder(x, z);
    }

    /// A wall end's wall's top past the deck: the approach's bank there, as the ground's shaped to carry the track, level across its
    /// formation and falling away at the fill slope's run, by how far the wall's face is from the track square across it (not along
    /// what's underneath: on a skew the bank comes down far more slowly along a road than square off the track). The wall's mesh and
    /// the ground behind it both go by this, so the bank meets the top.
    public float WallTop(BridgeEnd end, float s)
    {
        if (end.Deck is not { } deck)
            return end.Floor + 0.3f - WallBeyond(end, s) / SpillRun;
        var (x, z, _, _, _) = WallFaceAt(end, s);
        return EmbankmentAt(end, x, z);
    }

    /// The approach's embankment at a place near a bridge end: level with the track's formation across its width, then falling away
    /// at the fill slope's run. A wall's top is it at the wall's face, and the bank behind the wall is filled up to it.
    public float EmbankmentAt(BridgeEnd end, double x, double z)
    {
        var deck = end.Deck!;
        // The track nearest: the deck's own line or any other rail line near the end (the approach is often another line,
        // the bridge's track ending just past the deck), and its height and formation there.
        // (The deck's own line among them, so there's always one: a road's bridge over the track has no other rail line up at its
        // height, and with none the bank would fall away without end.)
        var (best, y, formation) = (double.MaxValue, deck.Y[end.Point], deck.Formation);
        foreach (var line in RailsNear(end).Append(deck))
            for (var p = 0; p + 1 < line.Count; p++)
            {
                var (ax, az, dx, dz) = (line.X[p], line.Z[p], line.X[p + 1] - line.X[p], line.Z[p + 1] - line.Z[p]);
                if (Math.Min(ax, ax + dx) > x + 100 || Math.Max(ax, ax + dx) < x - 100 || Math.Min(az, az + dz) > z + 100 || Math.Max(az, az + dz) < z - 100)
                    continue;
                var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / Math.Max(dx * dx + dz * dz, 1e-9), 0.0, 1.0);
                var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                var height = line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t;
                // (Track at much the deck's height only: not a line running underneath, or by at another level.)
                if (ex * ex + ez * ez < best && MathF.Abs(height - deck.Y[end.Point]) < 4f)
                    (best, y, formation) = (ex * ex + ez * ez, height, line.Formation);
            }

        var across = best < double.MaxValue ? (float)Math.Sqrt(best) : 0f;
        return y + 0.3f - MathF.Max(0f, across - formation - 0.5f) / SpillRun;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(NetworkLine, int), List<NetworkLine>> _railsNear = new();

    /// The rail lines (not marks) with any point within a wall's reach of a bridge end: the deck's own and the approaches.
    private List<NetworkLine> RailsNear(BridgeEnd end)
    {
        var deck = end.Deck!;
        return _railsNear.GetOrAdd((deck, end.Point), _ =>
        {
            var (cx, cz) = (deck.X[end.Point], deck.Z[end.Point]);
            const double reach = WallMost + 40.0;
            return [.. _lines.Where(l => l.Rail && !l.Mark && l.Count >= 2 && l != end.Under && l.MinX <= cx + reach && l.MaxX >= cx - reach && l.MinZ <= cz + reach && l.MaxZ >= cz - reach)];
        });
    }

    /// A cell of the tiles' grid (CellQuads squares a side; its corner samples, inclusive) where the ground's shaped again at a finer
    /// spacing (Sub times the tiles', a side) and drawn from that (Terrain's detail inserts): along every track, where its bed and slopes
    /// meet the ground, and finer still round each bridge end, where a wall's face or a wing wall meets the bank, which samples metres
    /// apart can't.
    public readonly record struct DetailRegion(int Cx, int Cz, int Sub)
    {
        public int Gx0 => Cx * CellQuads;
        public int Gz0 => Cz * CellQuads;
        public int Gx1 => (Cx + 1) * CellQuads;
        public int Gz1 => (Cz + 1) * CellQuads;
        public bool Touches(int gx0, int gz0, int gx1, int gz1) => Gx0 < gx1 && Gx1 > gx0 && Gz0 < gz1 && Gz1 > gz0;
    }

    /// A detail cell's side, in the tiles' grid squares.
    public const int CellQuads = 16;
    /// How much finer than the tiles' a cell along a track is shaped, and one round a bridge end.
    public const int CorridorSub = 4;
    public const int StructureSub = 8;
    /// How far past a track's formation its cells reach, metres: out over its slopes.
    private const float CorridorReach = 12f;

    private Dictionary<(int, int), int>? _detailCells;

    /// How much finer a detail cell's shaped (0 where it isn't one).
    public int DetailSubAt(int cx, int cz)
    {
        _ = DetailRegions;
        return _detailCells!.GetValueOrDefault((cx, cz));
    }

    /// How far in front of a wall's face and behind it its detail region reaches, metres.
    private const float DetailFront = 6f;
    private const float DetailBehind = 12f;
    /// The most samples a side a detail region may be: past that, it's left to the tiles' ground.
    private const int DetailMost = 96;

    private List<DetailRegion>? _detailRegions;

    /// Every wall end's detail region, overlapping ones merged.
    public IReadOnlyList<DetailRegion> DetailRegions
    {
        get
        {
            if (_detailRegions is { } found)
                return found;
            lock (_detailLock)
                return _detailRegions ??= FindDetailRegions();
        }
    }

    private readonly object _detailLock = new();

    /// Whether finer ground's laid at a place (a detail cell's there).
    public bool FineGroundAt(double x, double z) =>
        DetailSubAt(TerrainSource.FloorDiv((int)Math.Floor(x / _spacing), CellQuads), TerrainSource.FloorDiv((int)Math.Floor(z / _spacing), CellQuads)) > 0;

    private List<DetailRegion> FindDetailRegions()
    {
        {
            var boxes = new List<(int X0, int Z0, int X1, int Z1)>();
            foreach (var end in WallsBeside().Values.SelectMany(e => e))
            {
                if (end.Deck is not { } deck)
                    continue;
                var most = MathF.Min(WallMost, end.Footprint + (end.Cap ?? end.Flat + end.Wing));
                var (minX, minZ, maxX, maxZ) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                void Add(double x, double z) => (minX, minZ, maxX, maxZ) = (Math.Min(minX, x), Math.Min(minZ, z), Math.Max(maxX, x), Math.Max(maxZ, z));
                for (var s = end.UnderS - most; s <= end.UnderS + most; s += 1f)
                {
                    if (s < 0f || s > end.Under!.S[^1])
                        continue;
                    var (x, z, rx, rz, floor) = WallFaceAt(end, s);
                    // (Only where the wall stands: past where its top's down to the road, the road's own ground.)
                    if (WallBeyond(end, s) > 0f && WallTop(end, s) <= floor + 0.6f)
                        continue;
                    Add(x - rx * DetailFront, z - rz * DetailFront);
                    Add(x + rx * DetailBehind, z + rz * DetailBehind);
                }

                if (minX == double.MaxValue)
                    continue;
                // (And the deck's end, out past its corners: on a skew, the deck runs on over the bank well behind the wall.)
                {
                    var p = Math.Clamp(end.Point, 0, deck.Count - 2);
                    var (dx, dz) = (deck.X[p + 1] - deck.X[p], deck.Z[p + 1] - deck.Z[p]);
                    var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
                    var (ax, az) = (dx / length, dz / length);
                    var reach = BridgePlanner.DeckHalf(deck) + 6.0;
                    foreach (var (along, across) in ((double, double)[])[(-6, -reach), (-6, reach), (6, -reach), (6, reach)])
                        Add(deck.X[end.Point] + ax * along - az * across, deck.Z[end.Point] + az * along + ax * across);
                }

                var box = ((int)Math.Floor(minX / _spacing) - 1, (int)Math.Floor(minZ / _spacing) - 1, (int)Math.Ceiling(maxX / _spacing) + 1, (int)Math.Ceiling(maxZ / _spacing) + 1);
                if (box.Item3 - box.Item1 <= DetailMost && box.Item4 - box.Item2 <= DetailMost)
                    boxes.Add(box);
            }

            // Each slope end: from behind its seat out past the slope's toe, and out past the deck's sides as far as the bank coned
            // round its wing walls reaches.
            foreach (var end in _bridgeEnds!.Values.SelectMany(e => e).Where(e => e.Kind == BridgeEndKind.Slope && e.Under is not null && e.Deck is not null))
            {
                var deck = end.Deck!;
                var p = Math.Clamp(end.Point, 0, deck.Count - 2);
                var (dx, dz) = (deck.X[p + 1] - deck.X[p], deck.Z[p + 1] - deck.Z[p]);
                var length = Math.Max(Math.Sqrt(dx * dx + dz * dz), 1e-6);
                var (ax, az) = (dx / length, dz / length);
                var inward = end.Start ? 1.0 : -1.0;
                var run = Math.Max(deck.Y[end.Point] - end.Floor, 0f) * SpillRun;
                var far = Math.Min(run * 2.0 + end.Under!.Formation + 10.0, 80.0);
                var side = BridgePlanner.DeckHalf(deck) + run + 8.0;
                var (minX, minZ, maxX, maxZ) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                foreach (var (along, across) in ((double, double)[])[(-16, -side), (-16, side), (far, -side), (far, side)])
                {
                    var (x, z) = (deck.X[end.Point] + ax * along * inward - az * across, deck.Z[end.Point] + az * along * inward + ax * across);
                    (minX, minZ, maxX, maxZ) = (Math.Min(minX, x), Math.Min(minZ, z), Math.Max(maxX, x), Math.Max(maxZ, z));
                }

                var box = ((int)Math.Floor(minX / _spacing) - 1, (int)Math.Floor(minZ / _spacing) - 1, (int)Math.Ceiling(maxX / _spacing) + 1, (int)Math.Ceiling(maxZ / _spacing) + 1);
                if (box.Item3 - box.Item1 <= DetailMost && box.Item4 - box.Item2 <= DetailMost)
                    boxes.Add(box);
            }

            // The cells: each bridge end's box at the finer spacing, and along every track its formation and slopes at the other.
            var cells = new Dictionary<(int, int), int>();
            void Mark(int gx0, int gz0, int gx1, int gz1, int sub)
            {
                for (var cz = TerrainSource.FloorDiv(gz0, CellQuads); cz <= TerrainSource.FloorDiv(gz1, CellQuads); cz++)
                    for (var cx = TerrainSource.FloorDiv(gx0, CellQuads); cx <= TerrainSource.FloorDiv(gx1, CellQuads); cx++)
                        cells[(cx, cz)] = Math.Max(cells.GetValueOrDefault((cx, cz)), sub);
            }

            foreach (var b in boxes)
                Mark(b.X0, b.Z0, b.X1, b.Z1, StructureSub);
            foreach (var line in _lines.Where(l => l.Rail && !l.Mark && l.Count >= 2))
            {
                var reach = line.Formation + CorridorReach;
                for (var p = 0; p + 1 < line.Count; p++)
                {
                    var (ax, az, bx, bz) = (line.X[p], line.Z[p], line.X[p + 1], line.Z[p + 1]);
                    var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)) / (CellQuads * _spacing * 0.5)));
                    for (var k = 0; k <= steps; k++)
                    {
                        var (x, z) = (ax + (bx - ax) * k / steps, az + (bz - az) * k / steps);
                        Mark((int)Math.Floor((x - reach) / _spacing), (int)Math.Floor((z - reach) / _spacing), (int)Math.Ceiling((x + reach) / _spacing), (int)Math.Ceiling((z + reach) / _spacing), CorridorSub);
                    }
                }
            }

            _detailCells = cells;
            RailWerkz.Core.Logging.Log.Info($"Terrain: {cells.Count} detail cells ({cells.Values.Count(v => v == StructureSub)} round bridge ends)");
            return [.. cells.Select(c => new DetailRegion(c.Key.Item1, c.Key.Item2, c.Value))];
        }
    }

    /// Each detail region's ground shaped at the finer spacing, as worked out (TerrainSource.DetailHeights): these lines' own.
    public System.Collections.Concurrent.ConcurrentDictionary<DetailRegion, Lazy<float[]>> DetailCache { get; } = new();

    /// Lets down the strip each wall's body covers, beside each line in the tile, to the road's level.
    private void TrenchWalls(List<int> list, float[] heights, int samples, int gx0, int gz0)
    {
        if (_lines.Count == 0)
            return;
        var walls = WallsBeside();
        foreach (var index in list)
        {
            var line = _lines[index];
            if (!walls.TryGetValue(line, out var ends))
                continue;
            foreach (var end in ends)
            {
                // (No further than the wall goes: past its end there's nothing to cover the strip, and it was left a pit under the bank.)
                var most = MathF.Min(WallMost, end.Footprint + (end.Cap ?? end.Flat + end.Wing));
                var (from, to) = (end.UnderS - most, end.UnderS + most);

                // Each sample's nearest place on what's underneath, over all its segments, before anything's done to it: worked out a
                // segment at a time, a sample past a segment's end measured to that end looked further off than it is, and the bank
                // raised it again over the strip the segment it's beside had let down.
                var nearest = new Dictionary<int, (float D, float Along, float Road, float Face, bool Side)>();
                for (var p = 0; p + 1 < line.Count; p++)
                {
                    if (line.S[p + 1] < from || line.S[p] > to || line.Structures[p] != Structure.Ground)
                        continue;
                    var face = (line.Rail ? line.Formation : PavedHalf(line, p)) + WallOff + end.Setback;
                    var r = (face + WallBody + BankReach) / Sp;
                    var (ax, az, bx, bz) = (line.X[p] / Sp - gx0, line.Z[p] / Sp - gz0, line.X[p + 1] / Sp - gx0, line.Z[p + 1] / Sp - gz0);
                    var (x0, x1) = (Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r)));
                    var (z0, z1) = (Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - r)), Math.Min(samples - 1, (int)Math.Ceiling(Math.Max(az, bz) + r)));
                    var (dx, dz) = (bx - ax, bz - az);
                    var length2 = dx * dx + dz * dz;
                    if (length2 <= 0)
                        continue;
                    var length = Math.Sqrt(length2);
                    for (var z = z0; z <= z1; z++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / length2, 0.0, 1.0);
                            var (ex, ez) = (ax + dx * t - x, az + dz * t - z);
                            var d = (float)Math.Sqrt(ex * ex + ez * ez) * Sp;
                            var sample = z * samples + x;
                            if (nearest.TryGetValue(sample, out var had) && had.D <= d)
                                continue;
                            // To the line's right is (-dz, dx): the wall's side only.
                            var side = ((x - ax) * -dz + (z - az) * dx) / length * end.Side > 0;
                            nearest[sample] = (d, line.S[p] + (line.S[p + 1] - line.S[p]) * (float)t, line.Y[p] + (line.Y[p + 1] - line.Y[p]) * (float)t, face, side);
                        }
                }

                foreach (var (sample, (d, along, road, face, side)) in nearest)
                {
                    var reach = face + WallBody;
                    if (!side || d > reach + BankReach || d < face - WallOff || along < from || along > to)
                        continue;
                    var (sx, sz) = (sample % samples, sample / samples);
                    var (wx, wz) = ((sx + gx0) * (double)Sp, (sz + gz0) * (double)Sp);
                    // (Past the deck by the ground itself, not by where it's beside the road: on a skew, the deck runs on over the bank
                    // behind the wall well past its span along the road.)
                    var past = end.Deck is not null && WallBeyond(end, along) > 0f && !DeckOver(end, wx, wz);
                    var top = past ? WallTop(end, along) : 0f;
                    // (Only as far as the wall goes: past where its top's come down to the road, nothing covers the strip.)
                    if (past && top <= road + 0.6f)
                        continue;
                    var y = road - RoadClearance;
                    if (end.Deck is null)
                    {
                        if (d <= reach && heights[sample] > y)
                            heights[sample] = y;
                        continue;
                    }

                    // Only what the wall must stand in front of is let down: in front of its face, and the samples just
                    // behind it with one in front of it next to them (the ground's triangles between the two would climb in front of the
                    // face). The rest is the bank the wall holds back: the approach's embankment filled up to its height (which the
                    // wall's top is at its face), and where the ground's higher, cut down to the top close behind, rising away from the
                    // road beyond. The wall's top carries back over the ground's climb from the one to the other (NetworkMeshes).
                    var low = d < face;
                    if (!low && d <= reach)
                        for (var nz = -1; nz <= 1 && !low; nz++)
                            for (var nx = -1; nx <= 1 && !low; nx++)
                            {
                                var (ox, oz) = (sx + nx, sz + nz);
                                if (ox < 0 || oz < 0 || ox >= samples || oz >= samples)
                                    continue;
                                if (nearest.TryGetValue(oz * samples + ox, out var by) && (by.D < by.Face || !by.Side))
                                    low = true;
                            }

                    if (low)
                    {
                        if (heights[sample] > y)
                            heights[sample] = y;
                        continue;
                    }

                    var embankment = EmbankmentAt(end, wx, wz);
                    if (!past)
                    {
                        // Under the deck, the abutment's backfill: up to the embankment, the deck's underside over it kept clear
                        // (ClearBridges, after).
                        if (heights[sample] < embankment)
                            heights[sample] = embankment;
                        continue;
                    }

                    var back = MathF.Max(0f, d - face - WallThick);
                    var cap = top + MathF.Max(0f, back - BankHold) / SpillRun;
                    var fill = MathF.Min(cap, embankment);
                    if (heights[sample] > cap)
                        heights[sample] = cap;
                    else if (heights[sample] < fill)
                        heights[sample] = fill;
                }

            }
        }
    }

    /// Every end of a line's bridged stretches.
    public IReadOnlyList<BridgeEnd> BridgeEndsOf(NetworkLine line)
    {
        if (_bridgeEnds is null)
            lock (_bridgeEndsLock)
                _bridgeEnds ??= FindBridgeEnds();
        return _bridgeEnds.TryGetValue(line, out var ends) ? ends : [];
    }

    /// A line's bridge end at a point, if it has one there.
    public BridgeEnd? BridgeEndAt(NetworkLine line, int point)
    {
        foreach (var end in BridgeEndsOf(line))
            if (end.Point == point)
                return end;
        return null;
    }

    /// The node of a line nearest one of its points, by distance along it: what a bridge end's hand settings are kept by (a bridge
    /// lengthened to clear what's under it ends between nodes).
    public static long? EndNode(NetworkLine line, int point, int other)
    {
        long? best = null;
        var nearest = float.MaxValue;
        foreach (var (node, at) in line.Nodes)
        {
            var away = MathF.Abs(line.S[at] - line.S[point]);
            if (away < nearest && OnEndsSide(line, at, point, other))
                (nearest, best) = (away, node);
        }

        return best;
    }

    /// Whether a point of a line is on one end's side of a bridged stretch (point that end, other the far end): nearer it than the
    /// far end, so each end's settings are kept by a node of its own, however far along the track that is.
    private static bool OnEndsSide(NetworkLine line, int at, int point, int other) =>
        MathF.Abs(line.S[at] - line.S[point]) <= MathF.Abs(line.S[at] - line.S[other]);

    /// What's been set by hand on the route's bridges (Bridges.json): each end's kind, setback and wing length, found by its nodes.
    public IReadOnlyDictionary<long, BridgeEdit>? BridgeEdits { get; set; }

    /// Each bridge end's hand settings, by the nodes they were set at.
    private Dictionary<long, (BridgeEndKind? Kind, float Setback, float? Wing, float Extend)> EndSettings()
    {
        var setAt = new Dictionary<long, (BridgeEndKind? Kind, float Setback, float? Wing, float Extend)>();
        foreach (var edit in BridgeEdits?.Values ?? [])
        {
            foreach (var node in edit.StartNodes ?? [])
                setAt[node] = (edit.StartKind, edit.StartSetback, edit.StartWing, edit.StartExtend);
            foreach (var node in edit.EndNodes ?? [])
                setAt[node] = (edit.EndKind, edit.EndSetback, edit.EndWing, edit.EndExtend);
        }

        return setAt;
    }

    private int _handSet;

    /// The hand settings of the bridge end at a line's point (other: the stretch's far end): those kept by the nearest node on its side
    /// that has any (the track's nodes may be far apart: a long straight has them only at its ends).
    private static (BridgeEndKind? Kind, float Setback, float? Wing, float Extend) SettingsAt(NetworkLine line, int point, int other,
        Dictionary<long, (BridgeEndKind? Kind, float Setback, float? Wing, float Extend)> setAt)
    {
        if (setAt.Count == 0)
            return default;
        var best = float.MaxValue;
        (BridgeEndKind? Kind, float Setback, float? Wing, float Extend) found = default;
        foreach (var (node, at) in line.Nodes)
        {
            var away = MathF.Abs(line.S[at] - line.S[point]);
            if (away < best && OnEndsSide(line, at, point, other) && setAt.TryGetValue(node, out var set))
                (best, found) = (away, set);
        }

        return found;
    }

    /// Each bridged stretch lengthened by hand, by its line and its points now, and its points before: a bridge is known by its ends
    /// (BridgePlanner's key), which mustn't move with it, or it would lose what's been set on it.
    private readonly Dictionary<(NetworkLine Line, int First, int Last), (int First, int Last)> _extended = [];

    /// A bridged stretch's points as they were before it was lengthened by hand (as they are, if it wasn't).
    public (int First, int Last) Unextended(NetworkLine line, int first, int last) =>
        _extended.TryGetValue((line, first, last), out var was) ? was : (first, last);

    /// Each bridge's deck carried on over the approach at an end as far as has been set by hand (no further than a junction or the
    /// line's end), before the profiles are worked out, so the deck, its ramps and the ground follow.
    /// (toFitSlopes: the second pass, once the decks' heights are known, lengthening only the ends left to choose for themselves that
    /// nearly have room for a slope; the first lengthens only those set by hand.)
    private void ExtendBridges(bool toFitSlopes = false)
    {
        var setAt = EndSettings();
        var others = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        var uses = new Dictionary<long, int>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, _) in line.Nodes)
                uses[node] = uses.GetValueOrDefault(node) + 1;
        var extended = 0;
        var stretches = new List<Stretch>();
        foreach (var line in _lines.Where(l => !l.Mark && !l.Frozen && l.Structures.Contains(Structure.Bridge)))
        {
            var junctions = line.Nodes.Where(n => uses.GetValueOrDefault(n.Node) > 1).Select(n => n.Point).ToHashSet();
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;
                // (Lengthened as set by hand; or, an end left to choose for itself, far enough for a slope to fit if it nearly does.)
                float Wanted(int point, int other, bool start)
                {
                    var set = SettingsAt(line, point, other, setAt);
                    if (!toFitSlopes)
                        return set.Extend;
                    return set.Extend <= 0f && set.Kind is null && !line.Mark ? SlopeRoomWanted(line, first, last, start, others) : 0f;
                }

                var (atStart, atEnd) = (Wanted(first, last, true), Wanted(last, first, false));
                stretches.Add(new Stretch(line, first, last, Reach(line, first, -1, atStart, junctions), Reach(line, last, 1, atEnd, junctions), junctions));
            }
        }

        // Decks side by side (tracks beside each other over the same gap) brought out to the same ends, the furthest any of them wants:
        // lengthened each for itself, one track's deck ended where the other's ran on, and its approach's bank stood beside the other's
        // deck. Twice round, for three or more abreast.
        if (toFitSlopes)
            for (var round = 0; round < 2; round++)
                for (var i = 0; i < stretches.Count; i++)
                    for (var j = 0; j < stretches.Count; j++)
                        if (i != j)
                            stretches[i] = Level(stretches[i], stretches[j]);

        foreach (var (line, first, last, newFirst, newLast, _) in stretches)
        {
            if (newFirst == first && newLast == last)
                continue;
            for (var k = newFirst; k < newLast; k++)
                line.Structures[k] = Structure.Bridge;
            // (Known by its ends as mapped, though lengthened twice.)
            var mapped = _extended.Remove((line, first, last), out var was) ? was : (first, last);
            _extended[(line, newFirst, newLast)] = mapped;
            extended++;
        }

        if (extended > 0)
            RailWerkz.Core.Logging.Log.Info($"Bridges: {extended} lengthened (by hand, or to fit a slope)");
    }

    /// A bridged stretch being lengthened: its points as it is, and as it will be.
    private readonly record struct Stretch(NetworkLine Line, int First, int Last, int NewFirst, int NewLast, HashSet<int> Junctions);

    /// How far a bridged stretch's end may be carried on over the ground (way -1 back from its first point, 1 on from its last), as far as
    /// wanted metres: no further than the line goes, a junction, or another structure.
    private static int Reach(NetworkLine line, int from, int way, float wanted, HashSet<int> junctions)
    {
        var at = from;
        if (way < 0)
            while (wanted > 0f && at > 0 && line.S[from] - line.S[at] < wanted && !junctions.Contains(at) && line.Structures[at - 1] == Structure.Ground)
                at--;
        else
            while (wanted > 0f && at < line.Count - 1 && line.S[at] - line.S[from] < wanted && !junctions.Contains(at) && line.Structures[at] == Structure.Ground)
                at++;
        return at;
    }

    /// How near across two decks' ends must be, and how alike their ways, to be one bridge's tracks side by side.
    private const double AbreastReach = 15.0;

    /// A stretch's ends carried out as far as another's beside it reaches (along the stretch's way), if that's further.
    private static Stretch Level(Stretch a, Stretch b)
    {
        var (line, other) = (a.Line, b.Line);
        var (dx, dz) = (line.X[a.Last] - line.X[a.First], line.Z[a.Last] - line.Z[a.First]);
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length < 1.0)
            return a;
        (dx, dz) = (dx / length, dz / length);
        var (ox, oz) = (other.X[b.Last] - other.X[b.First], other.Z[b.Last] - other.Z[b.First]);
        var otherLength = Math.Sqrt(ox * ox + oz * oz);
        if (otherLength < 1.0)
            return a;
        var cos = (dx * ox + dz * oz) / otherLength;
        if (Math.Abs(cos) < 0.94)
            return a;
        // (b's ends in the order a's run, whichever way b was drawn.)
        var (bStart, bEnd) = cos > 0 ? (b.NewFirst, b.NewLast) : (b.NewLast, b.NewFirst);
        double Along(NetworkLine l, int q) => (l.X[q] - line.X[a.First]) * dx + (l.Z[q] - line.Z[a.First]) * dz;
        double Across(NetworkLine l, int q) => Math.Abs((l.X[q] - line.X[a.First]) * -dz + (l.Z[q] - line.Z[a.First]) * dx);
        // (Beside it: the other's deck across from this one's, not one further along the line.)
        if (Across(other, bStart) > AbreastReach || Across(other, bEnd) > AbreastReach)
            return a;
        if (Along(other, bEnd) < Along(line, a.First) || Along(other, bStart) > Along(line, a.Last))
            return a;

        var newFirst = a.NewFirst;
        var wantStart = Along(other, bStart);
        while (newFirst > 0 && Along(line, newFirst) > wantStart + 0.5 && !a.Junctions.Contains(newFirst) && line.Structures[newFirst - 1] == Structure.Ground)
            newFirst--;
        var newLast = a.NewLast;
        var wantEnd = Along(other, bEnd);
        while (newLast < line.Count - 1 && Along(line, newLast) < wantEnd - 0.5 && !a.Junctions.Contains(newLast) && line.Structures[newLast] == Structure.Ground)
            newLast++;
        return a with { NewFirst = newFirst, NewLast = newLast };
    }

    /// What crosses under a bridged stretch, square enough to be under it: where along the deck, how far it reaches along the deck
    /// either side (its width at the angle, and the deck's), its height there, and the sine of the angle it crosses at.
    private static List<(float S, float Reach, float Y, NetworkLine Line, float LineS, float Footprint, float Sine)> UnderStretch(
        NetworkLine line, int first, int last, List<NetworkLine> others, HashSet<long> shared, float deckHalf)
    {
        var under = new List<(float S, float Reach, float Y, NetworkLine Line, float LineS, float Footprint, float Sine)>();
        if (line.Mark)
            return under;
        var (bx0, bx1, bz0, bz1) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
        for (var q = first; q <= last; q++)
            (bx0, bx1, bz0, bz1) = (Math.Min(bx0, line.X[q]), Math.Max(bx1, line.X[q]), Math.Min(bz0, line.Z[q]), Math.Max(bz1, line.Z[q]));
        foreach (var other in others)
        {
            if (other == line || other.MaxX < bx0 || other.MinX > bx1 || other.MaxZ < bz0 || other.MinZ > bz1 || other.Nodes.Any(n => shared.Contains(n.Node)))
                continue;
            for (var i = 0; i + 1 < other.Count; i++)
            {
                if (other.Structures[i] != Structure.Ground)
                    continue;
                for (var q = first; q < last; q++)
                {
                    if (Crossing(other, i, line, q) is not { } hit || hit.Sine < 0.3f)
                        continue;
                    var s = line.S[q] + (line.S[q + 1] - line.S[q]) * hit.T;
                    var ot = (float)OtherT(other, i, line, q);
                    var y = other.Y[i] + (other.Y[i + 1] - other.Y[i]) * ot;
                    var reach = (other.Formation + UnderShoulder) / hit.Sine + deckHalf * hit.Cosine / hit.Sine;
                    under.Add((s, reach, y, other, other.S[i] + (other.S[i + 1] - other.S[i]) * ot, deckHalf / hit.Sine + 25f * hit.Cosine, hit.Sine));
                }
            }
        }

        return under;
    }

    /// How far a bridge end left to choose for itself may have its deck lengthened over the approach to make room for a slope down
    /// to what's underneath, metres along it: a slope's span is made long enough for it, as a railway's are; a wall only where it can't be.
    private const float SlopeRoomMost = 20f;

    /// For each end of each bridged stretch left to choose for itself (nothing set on it by hand), how much further along the deck
    /// it'd have to reach for a slope to fit under it, if that's more than nothing and no more than SlopeRoomMost.
    private float SlopeRoomWanted(NetworkLine line, int first, int last, bool start, List<NetworkLine> others)
    {
        var deckHalf = BridgePlanner.DeckHalf(line);
        var under = UnderStretch(line, first, last, others, line.Nodes.Select(n => n.Node).ToHashSet(), deckHalf);
        if (under.Count == 0)
            return 0f;
        var point = start ? first : last;
        var nearest = start ? under.MinBy(u => u.S - u.Reach) : under.MaxBy(u => u.S + u.Reach);
        var probe = new BridgeEnd(point, start, BridgeEndKind.Wall, 0f, nearest.Y, 0f, nearest.Line, nearest.LineS, 1);
        var room = MathF.Abs(probe.FromUnder(line.X[point], line.Z[point])) - nearest.Line.Formation - UnderShoulder;
        var fall = line.Y[point] - BridgePlanner.DeckDepth(line) - nearest.Y;
        var short_ = fall * SpillRun + 1.5f - room;
        if (short_ <= 0f)
            return 0f;
        // (Square across what's underneath it's short by that; along the deck, crossing at an angle, further.)
        var along = short_ / MathF.Max(nearest.Sine, 0.3f) + 1f;
        return along <= SlopeRoomMost ? along : 0f;
    }

    private Dictionary<NetworkLine, List<BridgeEnd>> FindBridgeEnds()
    {
        var setAt = EndSettings();

        var atNode = new Dictionary<long, List<(NetworkLine Line, int Point)>>();
        foreach (var line in _lines.Where(l => !l.Mark))
            foreach (var (node, point) in line.Nodes)
            {
                if (!atNode.TryGetValue(node, out var list))
                    atNode[node] = list = [];
                list.Add((line, point));
            }

        static bool Bridged(NetworkLine line, int point) =>
            (point > 0 && line.Structures[point - 1] == Structure.Bridge) || (point < line.Structures.Length && line.Structures[point] == Structure.Bridge);

        var found = new System.Collections.Concurrent.ConcurrentDictionary<NetworkLine, List<BridgeEnd>>();
        var others = _lines.Where(l => !l.Mark && l.Count >= 2).ToList();
        Parallel.ForEach(_lines.Where(l => l.Structures.Contains(Structure.Bridge)), line =>
        {
            var ends = new List<BridgeEnd>();
            var depth = BridgePlanner.DeckDepth(line);
            var deckHalf = BridgePlanner.DeckHalf(line);
            var shared = line.Nodes.Select(n => n.Node).ToHashSet();
            for (var p = 0; p < line.Structures.Length; p++)
            {
                if (line.Structures[p] != Structure.Bridge)
                    continue;
                var to = p;
                while (to + 1 < line.Structures.Length && line.Structures[to + 1] == Structure.Bridge)
                    to++;
                var (first, last) = (p, to + 1);
                p = to;

                // What crosses under the stretch, square enough to be under it: where along the deck, how far it reaches along the deck
                // either side (its width at the angle, and the deck's), and its height there.
                var under = UnderStretch(line, first, last, others, shared, deckHalf);

                foreach (var (point, start) in (ReadOnlySpan<(int, bool)>)[(first, true), (last, false)])
                {
                    // (What's been set on this end by hand, if anything.)
                    var set = SettingsAt(line, point, start ? last : first, setAt);
                    var isSet = set != default;
                    if (isSet)
                        Interlocked.Increment(ref _handSet);

                    var joined = line.Nodes.Where(n => n.Point == point).Any(n => atNode.TryGetValue(n.Node, out var users) && users.Any(u => u.Line != line && Bridged(u.Line, u.Point)));
                    if (joined)
                    {
                        ends.Add(new BridgeEnd(point, start, BridgeEndKind.Joined, 0f, line.Y[point], 0f));
                        continue;
                    }

                    if (under.Count == 0 || set.Kind == BridgeEndKind.Bank)
                    {
                        ends.Add(new BridgeEnd(point, start, BridgeEndKind.Bank, 0f, line.Ground[point], 0f, Set: isSet));
                        continue;
                    }

                    // The nearest edge of what's under it, from this end; and how far the end is from it, square across it.
                    var nearest = start ? under.MinBy(u => u.S - u.Reach) : under.MaxBy(u => u.S + u.Reach);
                    var run = start ? nearest.S - nearest.Reach - line.S[first] : line.S[last] - (nearest.S + nearest.Reach);
                    var floor = nearest.Y;
                    var probe = new BridgeEnd(point, start, BridgeEndKind.Wall, 0f, floor, 0f, nearest.Line, nearest.LineS, 1);
                    var away = probe.FromUnder(line.X[point], line.Z[point]);
                    var side = away >= 0f ? 1 : -1;
                    var room = MathF.Abs(away) - nearest.Line.Formation - UnderShoulder;
                    var fall = line.Y[point] - depth - floor;
                    var kind = set.Kind is BridgeEndKind.Wall or BridgeEndKind.Slope ? set.Kind.Value
                        : room >= fall * SpillRun + 1f ? BridgeEndKind.Slope : BridgeEndKind.Wall;
                    var wing = Math.Clamp((line.Y[point] - floor) * SpillRun, 1.5f, 25f);
                    var flat = MathF.Max(0f, line.Formation - deckHalf) + 0.5f;
                    // (A wall or slope set back no further than the deck reaches: it stands under it.)
                    var setback = Math.Clamp(set.Setback, 0f, MathF.Max(0f, room - 0.5f));
                    var cap = kind == BridgeEndKind.Wall ? set.Wing : null;
                    ends.Add(new BridgeEnd(point, start, kind, MathF.Max(run, 0f), floor, wing, nearest.Line, nearest.LineS, side, nearest.Footprint, flat, setback, cap, isSet, line));
                }
            }

            found[line] = ends;
        });

        var counts = found.Values.SelectMany(e => e).GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        RailWerkz.Core.Logging.Log.Info($"Bridge ends: {string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {c.Key.ToString().ToLowerInvariant()}"))}; " +
                                        $"{_handSet} found settings set by hand (of {setAt.Count} nodes set)");
        return new Dictionary<NetworkLine, List<BridgeEnd>>(found);
    }
}
