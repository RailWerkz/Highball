using System.Globalization;
using Highball.World;

namespace Highball.Builder;

public enum StampGroup { Switches, ThreeWay, Crossovers, Scissors, Slips, Diamonds, Ladders, Straights, Curves }

/// One track of a piece, in the piece's own metres: Along it from the piece's origin (its point of switch, if it has one), Off to
/// the right of that. Shared: its points that are an earlier way's: (point, that way, that way's point).
public sealed record StampWay(List<(double Along, double Off)> Points, List<(int Point, int Way, int Other)> Shared);

/// A premade piece of track for the Place palette. Entry: where it's picked up, the first point of its first way. Through: the way
/// a track already there takes the place of, where it's clicked onto one. Switches: (way, point, frog number). Problem: why it can't
/// be built at this spacing.
public sealed record TrackStamp(string Id, string Name, string Label, StampGroup Group, StampWay[] Ways, (int Way, int Point, int Size)[] Switches, int? Through, int Size, string? Problem = null)
{
    public double Length => Ways.Sum(w => w.Points.Zip(w.Points.Skip(1), (a, b) => Math.Sqrt((b.Along - a.Along) * (b.Along - a.Along) + (b.Off - a.Off) * (b.Off - a.Off))).Sum());
}

/// What a piece would lay at a place: the ways for TrackEditor.LayWays, the switches to size, where it's anchored, and what to say.
public sealed record StampPlan(List<PlannedWay> Ways, (int Way, int Point, int Size)[] Switches, double AtX, double AtZ, string Summary, string? Problem);

/// The Place palette's track pieces, built from the same curves the switches and crossovers are named by, and how each is
/// laid at a click: on a track's loose end it carries on from there; on a track, a switch or ladder leaves it and a crossover
/// joins it to the track beside it; anywhere else it's laid whole, turned as asked.
public static class TrackStamps
{
    private const double Step = 2.0;
    private const double Stub = 10.0;
    private const double OnTrack = 4.0;
    private const double AtPoint = 2.5;
    private const double LooseEndSnap = 8.0;
    private const double DesignOnTrack = 2.5;
    private const double DesignAtPoint = 0.5;
    private const double JunctionClearance = 3.0;
    private const double ShortestStub = 6.0;
    private const double RunClearance = 3.0;
    private const double FeetPerMeter = 3.28084;

    private static readonly int[] ScissorsSizes = [6, 7, 8, 10, 15];
    private static readonly int[] SlipAngles = [10, 15, 20];
    private static readonly int[] LadderSizes = [6, 8, 10];
    private static readonly int[] LadderTracks = [3, 4, 6];
    private static readonly int[] DiamondAngles = [15, 20, 30, 45, 60, 90];
    private static readonly double[] CurveAngles = [15.0, 30.0, 45.0, 90.0];
    private static readonly double[] StraightsMetric = [10.0, 25.0, 50.0, 100.0, 250.0, 500.0];
    private static readonly double[] StraightsFeet = [50.0, 100.0, 200.0, 500.0, 1000.0, 2640.0];
    private static readonly double[] RadiiMetric = [100.0, 150.0, 200.0, 300.0, 500.0, 800.0];
    private static readonly double[] RadiiFeet = [350.0, 500.0, 700.0, 1000.0, 1500.0, 2500.0];

    public static List<TrackStamp> All(float spacing, bool imperial)
    {
        var all = new List<TrackStamp>();
        foreach (var (number, _) in Turnout.Sizes)
        {
            all.Add(Switch(number, -1, spacing));
            all.Add(Switch(number, 1, spacing));
        }

        foreach (var (number, _) in Turnout.Sizes)
        {
            all.Add(Crossover(number, -1, spacing));
            all.Add(Crossover(number, 1, spacing));
        }

        foreach (var (number, _) in Turnout.Sizes)
        {
            all.Add(ThreeWay(number, -1, spacing));
            all.Add(ThreeWay(number, 1, spacing));
        }

        foreach (var number in ScissorsSizes)
            all.Add(Scissors(number, spacing, imperial));

        foreach (var degrees in SlipAngles)
        {
            all.Add(Slip(degrees, false));
            all.Add(Slip(degrees, true));
        }

        foreach (var degrees in DiamondAngles)
            all.Add(Diamond(degrees));

        foreach (var number in LadderSizes)
            foreach (var tracks in LadderTracks)
                all.Add(Ladder(number, tracks, spacing));

        string Unit(double value) => imperial ? string.Create(CultureInfo.InvariantCulture, $"{value:#,0} ft") : string.Create(CultureInfo.InvariantCulture, $"{value:#,0} m");
        var scale = imperial ? 1.0 / FeetPerMeter : 1.0;
        foreach (var length in imperial ? StraightsFeet : StraightsMetric)
            all.Add(Straight(length * scale, Unit(length)));
        foreach (var radius in imperial ? RadiiFeet : RadiiMetric)
            foreach (var degrees in CurveAngles)
                all.Add(Curve(radius * scale, degrees, Unit(radius)));
        return all;
    }

    // --- The pieces. ---

    /// The curve whose frog angle names a size, which is what the fitting goes by, and how far it runs and rises.
    private static (double Angle, double Radius, double Run, double Lift) Lead(int number)
    {
        var angle = Math.Atan(1.0 / number);
        var radius = 4.0 * Turnout.RailOffset / (angle * angle);
        return (angle, radius, radius * Math.Sin(angle), radius * (1.0 - Math.Cos(angle)));
    }

    private static List<(double, double)> Line(double from, double to, params double[] breaks)
    {
        var points = new List<(double, double)> { (from, 0.0) };
        var start = from;
        foreach (var at in breaks.Append(to))
        {
            var steps = Math.Max(1, (int)Math.Ceiling((at - start) / Step));
            for (var k = 1; k <= steps; k++)
                points.Add((start + (at - start) * k / steps, 0.0));
            start = at;
        }

        return points;
    }

    private static int IndexAt(List<(double Along, double Off)> points, double along) =>
        points.FindIndex(p => Math.Abs(p.Along - along) < 1e-6);

    private static List<(double, double)> Sampled(params TrackPiece[] pieces) =>
        YardTool.Sampled(pieces, []).Points.Select(p => (p.X, p.Z)).ToList();

    private static TrackPiece Next(TrackPiece last, double length, double curvature)
    {
        var (x, z) = last.At(last.Length);
        return new TrackPiece(x, z, last.HeadingAt(last.Length), length, curvature, true);
    }

    /// A switch: a stub, the point of switch at the origin, the through route, and the diverging route out to a track's spacing off.
    private static TrackStamp Switch(int number, int side, float spacing)
    {
        var (angle, radius, run, lift) = Lead(number);
        var curve = new TrackPiece(0.0, 0.0, 0.0, radius * angle, side / radius, true);
        var tail = Math.Max(15.0, (spacing - lift) / Math.Sin(angle));
        var branch = Sampled(curve, Next(curve, tail, 0.0));
        var through = Line(-Stub, branch[^1].Item1, 0.0);
        var hand = side < 0 ? "L" : "R";
        return new TrackStamp($"switch{number}{hand}", $"No. {number} switch, {(side < 0 ? "left" : "right")}", $"No. {number} {hand}", StampGroup.Switches,
        [
            new StampWay(through, []),
            new StampWay(branch, [(0, 0, IndexAt(through, 0.0))]),
        ], [(1, 0, number)], 0, number);
    }

    /// A three-way switch: one route off to each side of the through track. The second set of points stands past the first's
    /// blades and has its own blades done before the first's frog, the way a lapped three-way's do. first: the side the first
    /// route leaves to.
    private static TrackStamp ThreeWay(int number, int first, float spacing)
    {
        var (angle, radius, run, lift) = Lead(number);
        var stagger = Math.Sqrt(2.0 * radius * Turnout.HeelSpread) + 1.5;
        var tail = Math.Max(15.0, (spacing - lift) / Math.Sin(angle));
        List<(double, double)> Branch(double at, int side)
        {
            var curve = new TrackPiece(at, 0.0, 0.0, radius * angle, side / radius, true);
            return Sampled(curve, Next(curve, tail, 0.0));
        }

        var (a, b) = (Branch(0.0, first), Branch(stagger, -first));
        var through = Line(-Stub, b[^1].Item1, 0.0, stagger);
        var hand = first < 0 ? "L" : "R";
        return new TrackStamp($"threeway{number}{hand}", $"No. {number} three-way, first {(first < 0 ? "left" : "right")}", $"No. {number} {hand}", StampGroup.ThreeWay,
        [
            new StampWay(through, []),
            new StampWay(a, [(0, 0, IndexAt(through, 0.0))]),
            new StampWay(b, [(0, 0, IndexAt(through, stagger))]),
        ], [(1, 0, number), (2, 0, number)], 0, number);
    }

    /// The closest tracks can be for a scissors of a size: its diamond clear of all four frogs.
    private static float ScissorsSpacing(int number)
    {
        var (frog, radius) = (Turnout.FrogOf(number), Turnout.RadiusOf(number));
        var slope = frog / radius;
        var theta = 2.0 * Math.Atan(slope);
        var reach = Turnout.RailOffset / Math.Sin(theta) + Turnout.RailOffset / Math.Tan(theta) + Diamonds.Tail + 1.0;
        return MathF.Ceiling((float)(4.0 * Turnout.RailOffset + 2.0 * reach * slope) * 10f) / 10f;
    }

    /// Two crossovers leaning opposite ways between two tracks, with a diamond where they cross. Built wider apart than asked if
    /// its size needs it.
    private static TrackStamp Scissors(int number, float spacing, bool imperial)
    {
        var used = MathF.Max(spacing, ScissorsSpacing(number));
        var total = CrossoverFitting.Length(number, used);
        var a = Line(0.0, total + 2.0 * Stub, Stub, Stub + total);
        var b = a.Select(p => (p.Item1, (double)used)).ToList();

        // (An even count of steps, so both have a point at the middle.)
        var half = Math.Max(2, (int)Math.Ceiling(total / Step / 2.0));
        List<(double, double)> Link(bool up) => Enumerable.Range(0, 2 * half + 1).Select(k =>
        {
            var s = total * k / (2 * half);
            var off = (double)CrossoverFitting.Offset(number, used, (float)s);
            return (Stub + s, up ? off : used - off);
        }).ToList();

        var last = 2 * half;
        var wide = imperial ? string.Create(CultureInfo.InvariantCulture, $"{used * FeetPerMeter:0.0} ft") : string.Create(CultureInfo.InvariantCulture, $"{used:0.0} m");
        return new TrackStamp($"scissors{number}", $"No. {number} scissors crossover, tracks {wide} apart", $"No. {number}", StampGroup.Scissors,
        [
            new StampWay(a, []),
            new StampWay(b, []),
            new StampWay(Link(true), [(0, 0, IndexAt(a, Stub)), (last, 1, IndexAt(b, Stub + total))]),
            new StampWay(Link(false), [(0, 1, IndexAt(b, Stub)), (last, 0, IndexAt(a, Stub + total)), (half, 2, half)]),
        ], [(2, 0, number), (2, last, number), (3, 0, number), (3, last, number)], null, number);
    }

    /// A slip: two tracks crossing, and a route from one to the other beside the crossing (a double slip has one each side), with
    /// a set of points on each track either side of the diamond. The curve is the gentlest that keeps its frogs clear of the diamond,
    /// and the switches are named by the angle that makes.
    private static TrackStamp Slip(int degrees, bool both)
    {
        var theta = degrees * Math.PI / 180.0;
        var (sin, cos, tangent) = (Math.Sin(theta), Math.Cos(theta), Math.Tan(theta / 2.0));
        var reach = Turnout.RailOffset / sin + Turnout.RailOffset / Math.Tan(theta) + Diamonds.Tail + 1.0;
        var d = 20.0;
        while (Math.Sqrt(4.0 * (d / tangent) * Turnout.RailOffset) + reach > d)
            d += 1.0;
        var half = d + 20.0;
        var radius = d / tangent;

        // Both tracks as lines through the middle, the first along, the second turned to the right by the angle.
        var a = Line(-half, half, -d, 0.0, d);
        var b = a.Select(p => (p.Item1 * cos, p.Item1 * sin)).ToList();
        var arc = new TrackPiece(-d, 0.0, 0.0, radius * theta, 1.0 / radius, true);
        var (first, second) = (IndexAt(a, -d), IndexAt(a, d));
        var slip = Sampled(arc);
        var ways = new List<StampWay>
        {
            new(a, []),
            new(b, [(IndexAt(a, 0.0), 0, IndexAt(a, 0.0))]),
            new(slip, [(0, 0, first), (slip.Count - 1, 1, second)]),
        };
        var name = both ? "Double" : "Single";
        if (both)
        {
            // The other: from the second track's near end on to the first track's far end, the first turned end for end about the middle.
            var other = slip.Select(p => (-p.Item1, -p.Item2)).Reverse().ToList();
            ways.Add(new StampWay(other, [(0, 1, first), (other.Count - 1, 0, second)]));
        }

        return new TrackStamp($"slip{name.ToLowerInvariant()}{degrees}", $"{name} slip, tracks crossing at {degrees} degrees", string.Create(CultureInfo.InvariantCulture, $"{name} {degrees}\u00b0"), StampGroup.Slips, [.. ways], [], null, 0);
    }

    /// A crossover and the two tracks it joins, leaning to one side going forward.
    private static TrackStamp Crossover(int number, int side, float spacing)
    {
        var least = 4f * Turnout.RailOffset + 0.3f;
        var total = CrossoverFitting.Length(number, spacing);
        var a = Line(0.0, total + 2.0 * Stub, Stub, Stub + total);
        var b = a.Select(p => (p.Item1, side * (double)spacing)).ToList();
        var steps = Math.Max(4, (int)Math.Ceiling(total / Step));
        var link = Enumerable.Range(0, steps + 1).Select(k =>
        {
            var s = (float)(total * k / steps);
            return (Stub + s, side * (double)CrossoverFitting.Offset(number, spacing, s));
        }).ToList();
        var hand = side < 0 ? "L" : "R";
        return new TrackStamp($"crossover{number}{hand}", $"No. {number} crossover, {(side < 0 ? "left" : "right")}", $"No. {number} {hand}", StampGroup.Crossovers,
        [
            new StampWay(a, []),
            new StampWay(b, []),
            new StampWay(link, [(0, 0, IndexAt(a, Stub)), (steps, 1, IndexAt(b, Stub + total))]),
        ], [(2, 0, number), (2, steps, number)], null, number, spacing < least ? "Tracks too close for a crossover" : null);
    }

    /// Two tracks crossing at their middles.
    private static TrackStamp Diamond(int degrees)
    {
        const double half = 40.0;
        var a = Line(0.0, 2.0 * half, half);
        var (cos, sin) = (Math.Cos(degrees * Math.PI / 180.0), Math.Sin(degrees * Math.PI / 180.0));
        var b = Line(-half, half, 0.0).Select(p => (half + p.Item1 * cos, p.Item1 * sin)).ToList();
        var middle = (int)Math.Round(half / Step);
        return new TrackStamp($"diamond{degrees}", $"Diamond, {degrees} degrees", string.Create(CultureInfo.InvariantCulture, $"{degrees}°"), StampGroup.Diamonds,
        [
            new StampWay(a, []),
            new StampWay(b, [(middle, 0, IndexAt(a, half))]),
        ], [], null, 0);
    }

    /// A ladder off a track: the track it leaves, its lead, and a yard track off the lead for each switch along it.
    private static TrackStamp Ladder(int number, int tracks, float spacing)
    {
        const double body = 60.0;
        var (angle, radius, run, lift) = Lead(number);
        var (sine, cosine) = (Math.Sin(angle), Math.Cos(angle));
        double SwitchOn(int track) => (track * spacing - 2.0 * lift) / sine;
        var problem = spacing / sine - radius * angle < 6.0 || (spacing - 2.0 * lift) / sine < 4.0 ? $"Tracks too close for a No. {number} ladder" : null;
        var straight = SwitchOn(tracks - 1);
        var starts = run + straight * cosine + run;
        var reach = starts + body;

        var first = new TrackPiece(0.0, 0.0, 0.0, radius * angle, 1.0 / radius, true);
        var along = Next(first, Math.Max(straight, 1.0), 0.0);
        var back = Next(along, radius * angle, -1.0 / radius);
        var rest = Next(back, body, 0.0);
        TrackPiece[] pieces = [first, along, back, rest];
        var breaks = Enumerable.Range(1, tracks - 2).Select(t => radius * angle + SwitchOn(t)).ToList();
        var (leadPoints, marks) = problem is null ? YardTool.Sampled(pieces, breaks) : ([(0.0, 0.0), (1.0, 0.0)], []);

        var through = Line(-Stub, reach, 0.0);
        var ways = new List<StampWay>
        {
            new(through, []),
            new(leadPoints.Select(p => (p.X, p.Z)).ToList(), [(0, 0, IndexAt(through, 0.0))]),
        };
        var switches = new List<(int, int, int)> { (1, 0, number) };
        if (problem is null)
            for (var t = 1; t <= tracks - 2; t++)
            {
                var (x, z) = Alignment.Position(pieces, breaks[t - 1]);
                var curve = new TrackPiece(x, z, angle, radius * angle, -1.0 / radius, true);
                var (endX, _) = curve.At(curve.Length);
                var branch = Sampled(curve, Next(curve, Math.Max(reach - endX, 5.0), 0.0));
                ways.Add(new StampWay(branch, [(0, 1, marks[t - 1])]));
                switches.Add((ways.Count - 1, 0, number));
            }

        return new TrackStamp($"ladder{number}x{tracks}", $"No. {number} ladder, {tracks} tracks", $"{tracks} on No. {number}", StampGroup.Ladders, [.. ways], [.. switches], 0, number, problem);
    }

    private static TrackStamp Straight(double length, string name) =>
        new($"straight{name}", $"Straight, {name}", name, StampGroup.Straights, [new StampWay(Line(0.0, length), [])], [], null, 0);

    private static TrackStamp Curve(double radius, double degrees, string name)
    {
        var points = Sampled(new TrackPiece(0.0, 0.0, 0.0, radius * degrees * Math.PI / 180.0, 1.0 / radius, true));
        return new TrackStamp($"curve{name}x{degrees}", $"Curve, radius {name}, {degrees:0} degrees", string.Create(CultureInfo.InvariantCulture, $"{name} {degrees:0}°"), StampGroup.Curves, [new StampWay(points, [])], [], null, 0);
    }

    // --- Laying one. ---

    /// What the piece would lay with the pointer here. heading: radians, used wherever a track doesn't say; mirror: to the other side.
    public static StampPlan? Plan(TrackStamp stamp, TrackEditor tracks, double x, double z, double heading, bool mirror, Func<double, string> length)
    {
        if (stamp.Problem is { } refused)
            return new StampPlan([], [], x, z, "", refused);

        var (cos, sin) = (Math.Cos(heading), Math.Sin(heading));

        if (tracks.LooseEndNear(x, z, LooseEndSnap) is { } end && tracks.LeavingEnd(end) is { } leaving)
        {
            var (ex, ez) = tracks.PositionOf(end);
            return Whole(stamp, tracks, ex, ez, leaving, mirror, new LayPoint(ex, ez, Node: end), length);
        }

        if (tracks.TrackNear(x, z, OnTrack, AtPoint) is { } on)
        {
            // Which way along the track: however the piece is turned, whichever end that's nearer.
            var u = on.Runs.X * cos + on.Runs.Z * sin >= 0.0 ? on.Runs : (X: -on.Runs.X, Z: -on.Runs.Z);
            if (stamp.Group is StampGroup.Crossovers or StampGroup.Scissors)
                return CrossoverOn(stamp, tracks, on, x, z, length, mirror);
            if (stamp.Through is not null)
                return OnTrackPlan(stamp, tracks, on.Point, u, mirror, length);
        }

        return Whole(stamp, tracks, x, z, (cos, sin), mirror, null, length);
    }

    private static (double X, double Z) World(double ax, double az, (double X, double Z) u, double m, double along, double off) =>
        (ax + u.X * along - u.Z * off * m, az + u.Z * along + u.X * off * m);

    /// The whole piece with its first point here, joined to what's given there.
    private static StampPlan Whole(TrackStamp stamp, TrackEditor tracks, double x, double z, (double X, double Z) u, bool mirror, LayPoint? joined, Func<double, string> length)
    {
        var m = mirror ? -1.0 : 1.0;
        var (ea, eo) = stamp.Ways[0].Points[0];
        var ways = new List<PlannedWay>();
        var blocked = false;
        foreach (var way in stamp.Ways)
        {
            var points = way.Points.Select(p => World(x, z, u, m, p.Along - ea, p.Off - eo)).ToList();
            var isFirst = ways.Count == 0;
            var lay = points.Select(p => new LayPoint(p.X, p.Z)).ToList();
            if (isFirst && joined is { } at)
                lay[0] = at;
            blocked |= tracks.RunsInto(points, 0.0, isFirst && joined is not null, false);
            ways.Add(new PlannedWay(lay, null, way.Shared));
        }

        return new StampPlan(ways, stamp.Switches, x, z, $"{stamp.Name}, {length(stamp.Length)}", blocked ? "Runs into another track" : null);
    }

    /// A switch or ladder leaving a track here: the track stands in for the piece's through way, and only the rest is laid.
    private static StampPlan OnTrackPlan(TrackStamp stamp, TrackEditor tracks, LayPoint at, (double X, double Z) u, bool mirror, Func<double, string> length)
    {
        var m = mirror ? -1.0 : 1.0;
        var dropped = stamp.Through!.Value;
        int Moved(int way) => way > dropped ? way - 1 : way;

        var ways = new List<PlannedWay>();
        var (blocked, shortOfTrack) = (false, false);
        for (var w = 0; w < stamp.Ways.Length; w++)
        {
            if (w == dropped)
                continue;
            var way = stamp.Ways[w];
            var points = way.Points.Select(p => World(at.X, at.Z, u, m, p.Along, p.Off)).ToList();
            var lay = points.Select(p => new LayPoint(p.X, p.Z)).ToList();
            var shared = new List<(int, int, int)>();
            foreach (var (point, other, index) in way.Shared)
            {
                if (other != dropped)
                    shared.Add((point, Moved(other), index));
                else if (Math.Abs(way.Points[point].Along) < 1e-6)
                    lay[point] = at;
                else if (tracks.TrackNear(points[point].X, points[point].Z, DesignOnTrack, DesignAtPoint) is { } further)
                    lay[point] = further.Point;
                else
                    shortOfTrack = true;
            }

            blocked |= tracks.RunsInto(points, 0.0, way.Shared.Any(s => s.Way == dropped), false);
            blocked |= tracks.RunsInto(points.Skip(points.Count * 4 / 5).ToList(), RunClearance, false, false);
            ways.Add(new PlannedWay(lay, null, shared));
        }

        var frog = stamp.Ways.Where((_, w) => w != dropped).SelectMany(w => w.Shared.Where(s => s.Way == dropped).Select(s => w.Points[s.Point].Along)).Max() + Turnout.FrogOf(stamp.Size);
        (double X, double Z) Along(double s) => (at.X + u.X * s, at.Z + u.Z * s);
        var (far, back, stub) = (Along(frog + JunctionClearance), Along(-JunctionClearance), Along(-ShortestStub));
        var problem = at.Node is { } node && tracks.IsJunction(node) ? "Already a junction here"
            : shortOfTrack || tracks.TrackNear(far.X, far.Z, DesignOnTrack, DesignAtPoint) is null || tracks.TrackNear(stub.X, stub.Z, DesignOnTrack, DesignAtPoint) is null ? "Too near the end of the track"
            : tracks.JunctionAlong(back, far) ? $"Another junction is in the way of a No. {stamp.Size}"
            : blocked ? "Runs into another track"
            : null;
        var switches = stamp.Switches.Select(s => (Moved(s.Way), s.Point, s.Size)).ToArray();
        return new StampPlan(ways, switches, at.X, at.Z, stamp.Name, problem);
    }

    /// A scissors crossover between the track clicked and the one this far off to the side n, centred on the click.
    private static StampPlan ScissorsOn(TrackStamp stamp, TrackEditor tracks, LayPoint at, (double X, double Z) u, (double X, double Z) n, float spacing, Func<double, string> length)
    {
        if (spacing < ScissorsSpacing(stamp.Size))
            return new StampPlan([], [], at.X, at.Z, "", $"Tracks {length(spacing)} apart: a No. {stamp.Size} scissors needs {length(ScissorsSpacing(stamp.Size))}");

        var piece = Scissors(stamp.Size, spacing, false);
        var total = CrossoverFitting.Length(stamp.Size, spacing);
        var from = Stub + total / 2.0;
        (double X, double Z) World(double along, double off) => (at.X + u.X * (along - from) + n.X * off, at.Z + u.Z * (along - from) + n.Z * off);

        var ways = new List<PlannedWay>();
        var (blocked, shortOfTrack) = (false, false);
        foreach (var way in piece.Ways.Skip(2))
        {
            var points = way.Points.Select(p => World(p.Along, p.Off)).ToList();
            var lay = points.Select(p => new LayPoint(p.X, p.Z)).ToList();
            var shared = new List<(int, int, int)>();
            foreach (var (point, other, index) in way.Shared)
            {
                if (other >= 2)
                    shared.Add((point, other - 2, index));
                else if (tracks.TrackNear(points[point].X, points[point].Z, DesignOnTrack, DesignAtPoint) is { } on)
                    lay[point] = on.Point;
                else
                    shortOfTrack = true;
            }

            blocked |= tracks.RunsInto(points, 0.0, true, true);
            ways.Add(new PlannedWay(lay, null, shared));
        }

        var (a0, a1, b0, b1) = (World(Stub - JunctionClearance, 0.0), World(Stub + total + JunctionClearance, 0.0), World(Stub - JunctionClearance, spacing), World(Stub + total + JunctionClearance, spacing));
        var problem = shortOfTrack ? $"A track ends too soon for a No. {stamp.Size}"
            : tracks.JunctionAlong(a0, a1) || tracks.JunctionAlong(b0, b1) ? $"Another junction is in the way of a No. {stamp.Size}"
            : blocked ? "Runs into another track"
            : null;
        var switches = piece.Switches.Select(s => (s.Way - 2, s.Point, s.Size)).ToArray();
        return new StampPlan(ways, switches, at.X, at.Z, $"No. {stamp.Size} scissors, tracks {length(spacing)} apart", problem);
    }

    /// A crossover between the track clicked and the one beside it, on the side the pointer is if there's one each side.
    private static StampPlan CrossoverOn(TrackStamp stamp, TrackEditor tracks, (LayPoint Point, (double X, double Z) Runs) on, double x, double z, Func<double, string> length, bool mirror)
    {
        var (ax, az) = (on.Point.X, on.Point.Z);
        var u = on.Runs;
        var right = (X: -u.Z, Z: u.X);
        var sides = (x - ax) * right.X + (z - az) * right.Z >= 0.0 ? new[] { 1.0, -1.0 } : [-1.0, 1.0];

        foreach (var side in sides)
            for (var off = (double)YardTool.MinSpacing; off <= YardTool.MaxSpacing; off += 0.25)
            {
                var (ox, oz) = (ax + right.X * side * off, az + right.Z * side * off);
                if (tracks.TrackNear(ox, oz, 0.3, DesignAtPoint) is null)
                    continue;

                if (stamp.Group == StampGroup.Scissors)
                    return ScissorsOn(stamp, tracks, on.Point, u, (right.X * side, right.Z * side), (float)off, length);

                // Leaning its way: going forward along u, the other track has to be on the side the piece leans to.
                var leans = stamp.Id.EndsWith('R') != mirror ? 1.0 : -1.0;
                var w = side == leans ? u : (X: -u.X, Z: -u.Z);
                var n = (X: right.X * side, Z: right.Z * side);
                var plan = YardTool.CrossoverOf(tracks, (ax, az), w, n, 0.0, (float)off, stamp.Size, length, out var problem);
                if (plan is null)
                    return new StampPlan([], [], ax, az, "", problem);
                return new StampPlan(plan.Ways, [(0, 0, stamp.Size), (0, plan.Ways[0].Points.Count - 1, stamp.Size)], ax, az, plan.Summary, plan.Problem);
            }

        return new StampPlan([], [], ax, az, "", $"No track beside this one within {length(YardTool.MaxSpacing)}");
    }
}
