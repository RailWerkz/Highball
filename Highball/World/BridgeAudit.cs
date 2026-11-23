using System.Globalization;
using System.Text;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// Measures every slope end's ground as it's drawn against its shape (BridgeEndShape), so whether bridge ends come out right is a count
/// across the route, not one camera angle. HB_BRIDGEAUDIT=1 runs it once the terrain's open; each end's faults are logged, the worst
/// with a launch line to look at them.
///   steps: neighbouring places a quarter metre apart more than StepMost apart in height, neither out of sight in the structure
///   proud: a wing wall's top standing more than ProudMost over the bank just outside it; buried: the bank over its top
///   bed: the ground behind the deck's end, under the ballast, off the approach's formation by more than BedMost
///   deck: the ground under the deck past its seat above the seat
public static class BridgeAudit
{
    private const float Step = 0.25f;
    private const float StepMost = 0.35f;
    private const float ProudMost = 0.45f;
    private const float BedMost = 0.2f;
    private const float OffMost = 0.2f;

    public static void Run(TerrainSource source, RouteInfo route)
    {
        if (source.Lines is not { } lines)
            return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<(BridgeEndShape Shape, int Steps, int Proud, int Buried, int Bed, int Deck, int Off, (float A, float C) Worst)>();
        foreach (var shape in lines.EndShapes)
        {
            var (steps, proud, buried, bed, deck, off) = (0, 0, 0, 0, 0, 0);
            var (worst, worstAt) = (0f, (0f, 0f));
            float Ground(float a, float c)
            {
                var (x, z) = shape.World(a, c);
                return source.FineHeightAt(x, z, edits: false);
            }

            var width = shape.DeckHalf + BridgeEndShape.WallThick + 4f;
            var far = MathF.Max(shape.ToeLeft, shape.ToeRight) + 2f;
            for (var a = -BridgeEndShape.Behind + 2f; a <= far; a += Step)
                for (var c = -width; c <= width; c += Step)
                {
                    // (The end's own ground only: past its slope's toe, under the deck, it's what runs underneath's.)
                    // (Nor under the approach's ballast, which covers it.)
                    if (shape.Hidden(a, c) || a > shape.Toe(c) || (a < 0f && MathF.Abs(c) < shape.Line.BallastHalfTop + 1.5f))
                        continue;
                    var h = Ground(a, c);
                    if (float.IsNaN(h))
                        continue;
                    // Off the shape: where the end has the ground wholly (its least and most the same), the ground drawn isn't that.
                    var (px, pz) = shape.World(a, c);
                    // (Not under the approach's ballast, which hides it, and is laid over the formation.)
                    var underBed = a < 0f && MathF.Abs(c) < shape.Line.BallastHalfTop + 1.5f;
                    if (!underBed && lines.EndGround(px, pz, h) is { Exact: true } want && MathF.Abs(h - want.Least) > OffMost)
                        off++;

                    foreach (var (na, nc) in ((float, float)[])[(a + Step, c), (a, c + Step)])
                    {
                        if (shape.Hidden(na, nc))
                            continue;
                        var jump = MathF.Abs(Ground(na, nc) - h);
                        if (!(jump > StepMost))
                            continue;
                        steps++;
                        if (jump > worst)
                            (worst, worstAt) = (jump, (a, c));
                    }
                }

            foreach (var side in (float[])[-1f, 1f])
                for (var a = BridgeEndShape.WallFrom; a <= shape.WallEnd(side); a += Step)
                {
                    var top = shape.WallTop(a, side);
                    var outside = Ground(a, side * (shape.DeckHalf + BridgeEndShape.WallThick + 0.2f));
                    if (float.IsNaN(outside))
                        continue;
                    var (qx, qz) = shape.World(a, side * (shape.DeckHalf + BridgeEndShape.WallThick + 0.2f));
                    var meant = shape.Ground(qx, qz, outside) is { } m ? MathF.Max(MathF.Min(outside, m.Most), m.Least) : outside;
                    if (top - MathF.Max(outside, meant - 0.15f) > ProudMost + 0.25f)
                        proud++;
                    else if (outside > top + 0.05f)
                        buried++;
                }

            var bedHalf = shape.Line.BallastHalfTop - 0.2f;
            for (var a = -3f; a <= -0.8f && shape.Line.Rail; a += Step)
                for (var c = -bedHalf; c <= bedHalf; c += Step)
                    // (Under the ballast, below the formation: the bed's left standing over nothing.)
                    if (shape.Formation(a) - Ground(a, c) is > BedMost and not float.NaN)
                        bed++;
            for (var a = BridgeEndShape.Seat; a <= MathF.Min(shape.ToeLeft, shape.ToeRight); a += Step)
                for (var c = -shape.DeckHalf + 0.2f; c <= shape.DeckHalf - 0.2f; c += Step)
                    if (Ground(a, c) > shape.SeatY + 0.05f)
                        deck++;
            results.Add((shape, steps, proud, buried, bed, deck, off, worstAt));
        }

        var ic = CultureInfo.InvariantCulture;
        string Launch(BridgeEndShape shape, (float A, float C) at)
        {
            // From behind the end and out to the side, looking at the place.
            var side = at.C >= 0f ? 1f : -1f;
            var (tx, tz) = shape.World(at.A, at.C);
            var (cx, cz) = shape.World(at.A - 14f, at.C + side * 14f);
            var geo = route.Projection!.Inverse(new RoutePoint(cx, -cz));
            var yaw = (Math.Atan2(tx - cx, -(tz - cz)) * 180.0 / Math.PI + 360.0) % 360.0;
            return string.Create(ic, $"--camera {geo.Lat:0.0000000} {geo.Lon:0.0000000} 10 {yaw:0.00} -22 --zoom 1.00");
        }

        var total = results.Aggregate((Steps: 0, Proud: 0, Buried: 0, Bed: 0, Deck: 0, Off: 0), (t, r) => (t.Steps + r.Steps, t.Proud + r.Proud, t.Buried + r.Buried, t.Bed + r.Bed, t.Deck + r.Deck, t.Off + r.Off));
        var clean = results.Count(r => r.Steps + r.Proud + r.Buried + r.Bed + r.Deck + r.Off == 0);
        Log.Info(string.Create(ic, $"Bridge audit: {results.Count} slope ends, {clean} clean; steps {total.Steps}, proud {total.Proud}, buried {total.Buried}, bed {total.Bed}, deck {total.Deck}, off {total.Off} ({watch.ElapsedMilliseconds} ms)"));
        var sb = new StringBuilder();
        foreach (var r in results.OrderByDescending(r => r.Steps + r.Proud * 2 + r.Buried * 2 + r.Bed + r.Deck + r.Off).Take(Environment.GetEnvironmentVariable("HB_AUDITALL") == "1" ? 1000 : 10))
            Log.Info(string.Create(ic, $"Bridge audit end {r.Shape.Ex:0.0},{r.Shape.Ez:0.0} rail {r.Shape.Line.Rail}: steps {r.Steps}, proud {r.Proud}, buried {r.Buried}, bed {r.Bed}, deck {r.Deck}, off {r.Off}; worst step at a {r.Worst.A:0.0} c {r.Worst.C:0.0}; {Launch(r.Shape, r.Worst)}"));
    }
}
