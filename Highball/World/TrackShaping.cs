using System.IO.Compression;
using System.Runtime.InteropServices;
using Highball.Routes;
using RailWerkz.Core.Logging;

namespace Highball.World;

/// The lines shaped by hand: each one's straights and arcs by the line's key, kept with the route (TrackEdits.bin.gz) and
/// laid over the solved track, whatever's been solved since.
public sealed class TrackEdits
{
    private const int Version = 1;

    private readonly Dictionary<long, Alignment> _shapes = [];

    /// Changed since it was loaded or saved.
    public bool Unsaved { get; private set; }

    public bool Has(long key) => _shapes.ContainsKey(key);

    public Alignment? Get(long key) => _shapes.GetValueOrDefault(key);

    /// A line's shape by hand; null puts it back to as solved.
    public void Set(long key, Alignment? shape)
    {
        if (shape is null)
            _shapes.Remove(key);
        else
            _shapes[key] = shape;
        Unsaved = true;
    }

    /// As they are now, for a rebuild on another thread.
    public Dictionary<long, Alignment> Snapshot() => new(_shapes);

    private static string PathIn(RouteInfo route) => Path.Combine(route.Folder, "TrackEdits.bin.gz");

    public static TrackEdits Load(RouteInfo route)
    {
        var edits = new TrackEdits();
        var path = PathIn(route);
        if (!File.Exists(path))
            return edits;

        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(zip, 1 << 16));
            if (r.ReadInt32() != Version)
                throw new InvalidDataException("a newer format");
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var key = r.ReadInt64();
                var pieces = new TrackPiece[r.ReadInt32()];
                r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(pieces.AsSpan()));
                var u = new double[r.ReadInt32()];
                r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(u.AsSpan()));
                edits._shapes[key] = Alignment.Restored(pieces, u);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Log.Warn($"Track edits unreadable: {e.Message}");
            edits._shapes.Clear();
        }

        return edits;
    }

    public void Save(RouteInfo route)
    {
        var path = PathIn(route);
        var temporary = path + ".tmp";
        using (var file = File.Create(temporary))
        using (var zip = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(Version);
            w.Write(_shapes.Count);
            foreach (var (key, shape) in _shapes)
            {
                w.Write(key);
                w.Write(shape.Pieces.Count);
                w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(shape.Pieces)));
                w.Write(shape.U.Length);
                w.Write(MemoryMarshal.AsBytes(shape.U.AsSpan()));
            }
        }

        File.Move(temporary, path, true);
        Unsaved = false;
    }
}

/// A change to a line's shape, for undo: its hand shape before and after (null: as solved).
public sealed record TrackShapeEdit(long Key, Alignment? Before, Alignment? After);

/// Reshaping a line by its handles. Everything here works out a new shape and touches nothing; a handle at a junction
/// stays put, along with the way the line runs through it, so the track either side of a move still joins what it joined.
public static class TrackShaping
{
    /// Handles on a line are at least this far apart, unless put there by hand.
    private const float HandleSpacing = 25f;
    /// How near the pointer has to be to the mapped line or the straight between two handles for a handle to snap to it.
    private const double SnapReach = 0.75;

    /// What a change would come to: the new shape, its tightest curve, and how far from the map it goes at most.
    public sealed record Result(Alignment Shape, double Radius, double OffMap);

    /// A line's handles: its ends, its junctions, and the joints between its pieces.
    public static List<int> Handles(NetworkLine line, Func<long, bool> junction, IEnumerable<int> extra)
    {
        var fixedPoints = new SortedSet<int> { 0, line.Count - 1 };
        foreach (var (node, point) in line.Nodes)
            if (junction(node))
                fixedPoints.Add(point);
        var handles = new SortedSet<int>(fixedPoints);
        foreach (var p in extra)
            if (p > 0 && p < line.Count - 1)
                handles.Add(p);

        var alignment = line.Alignment!;
        var (walked, at) = (0.0, 0);
        foreach (var piece in alignment.Pieces)
        {
            walked += piece.Length;
            while (at < line.Count - 1 && alignment.U[at] < walked)
                at++;
            if (handles.All(p => MathF.Abs(line.S[p] - line.S[at]) >= HandleSpacing))
                handles.Add(at);
        }

        return [.. handles];
    }

    private static (double X, double Z) Direction(double heading) => (Math.Cos(heading), Math.Sin(heading));

    private static (double X, double Z) Unit(double x, double z)
    {
        var length = Math.Max(Math.Sqrt(x * x + z * z), 1e-9);
        return (x / length, z / length);
    }

    private static Alignment Copy(Alignment shape) => Alignment.Restored([.. shape.Pieces], (double[])shape.U.Clone());

    /// Where a handle goes for the pointer at (x, z): on to the straight between its neighbours, or the mapped line, if
    /// it's that close.
    public static (double X, double Z) Snapped(NetworkLine line, List<int> handles, int point, double x, double z)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return (x, z);
        var (a, b) = (handles[i - 1], handles[i + 1]);
        var (dx, dz) = Unit(line.X[b] - line.X[a], line.Z[b] - line.Z[a]);
        var along = (x - line.X[a]) * dx + (z - line.Z[a]) * dz;
        var (cx, cz) = (line.X[a] + dx * along, line.Z[a] + dz * along);
        if ((cx - x) * (cx - x) + (cz - z) * (cz - z) < SnapReach * SnapReach)
            return (cx, cz);

        var (mx, mz) = (line.MappedX, line.MappedZ);
        var (best, bx, bz) = (SnapReach * SnapReach, x, z);
        for (var k = Math.Max(0, point - 20); k < Math.Min(mx.Length - 1, point + 20); k++)
        {
            var (sx, sz) = (mx[k + 1] - mx[k], mz[k + 1] - mz[k]);
            var t = Math.Clamp(((x - mx[k]) * sx + (z - mz[k]) * sz) / Math.Max(sx * sx + sz * sz, 1e-9), 0.0, 1.0);
            var (px, pz) = (mx[k] + sx * t, mz[k] + sz * t);
            var d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < best)
                (best, bx, bz) = (d, px, pz);
        }

        return (bx, bz);
    }

    /// A handle moved: the line from the handle before it to the handle after it laid in again through the new place,
    /// running there along the line between those two. Null if no curve goes that way.
    public static Result? Move(NetworkLine line, List<int> handles, int point, double x, double z)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return null;
        var (from, to) = (handles[i - 1], handles[i + 1]);
        var shape = line.Alignment!;
        var (start, end) = ((line.X[from], line.Z[from]), (line.X[to], line.Z[to]));
        var through = Unit(end.Item1 - start.Item1, end.Item2 - start.Item2);
        if (Alignment.Join(start, Direction(shape.HeadingAt(shape.U[from])), (x, z), through) is not { } first ||
            Alignment.Join((x, z), through, end, Direction(shape.HeadingAt(shape.U[to]))) is not { } second)
            return null;

        var (l1, l2) = (Alignment.LengthOf(first.Pieces), Alignment.LengthOf(second.Pieces));
        var along = new double[to - from + 1];
        for (var p = from; p <= to; p++)
            along[p - from] = p <= point
                ? l1 * (shape.U[p] - shape.U[from]) / Math.Max(shape.U[point] - shape.U[from], 1e-9)
                : l1 + l2 * (shape.U[p] - shape.U[point]) / Math.Max(shape.U[to] - shape.U[point], 1e-9);
        List<TrackPiece> pieces = [.. first.Pieces, .. second.Pieces];
        var result = Copy(shape);
        result.Splice(from, to, pieces, along);
        return Finish(line, result, Math.Min(first.Radius, second.Radius), from, to);
    }

    /// A handle taken out: one curve from the handle before it to the handle after it.
    public static Result? Remove(NetworkLine line, List<int> handles, int point)
    {
        var i = handles.IndexOf(point);
        if (i <= 0 || i >= handles.Count - 1)
            return null;
        var (from, to) = (handles[i - 1], handles[i + 1]);
        var shape = line.Alignment!;
        if (Alignment.Join((line.X[from], line.Z[from]), Direction(shape.HeadingAt(shape.U[from])), (line.X[to], line.Z[to]), Direction(shape.HeadingAt(shape.U[to]))) is not { } curve)
            return null;
        var result = Copy(shape);
        result.Splice(from, to, curve.Pieces);
        return Finish(line, result, curve.Radius, from, to);
    }

    /// Exactly straight between two handles, and curves either side on to it from the handles beyond. A junction at either
    /// end keeps its place, and the line turns there if it has to; canTurn says which handles the curves may start from.
    public static Result? Straighten(NetworkLine line, List<int> handles, int a, int b, Func<int, bool> isJunction)
    {
        if (a > b)
            (a, b) = (b, a);
        var (i, j) = (handles.IndexOf(a), handles.IndexOf(b));
        if (i < 0 || j <= i)
            return null;

        var shape = line.Alignment!;
        var (pa, pb) = ((line.X[a], line.Z[a]), (line.X[b], line.Z[b]));
        var direction = Unit(pb.Item1 - pa.Item1, pb.Item2 - pa.Item2);
        var length = Math.Sqrt((pb.Item1 - pa.Item1) * (pb.Item1 - pa.Item1) + (pb.Item2 - pa.Item2) * (pb.Item2 - pa.Item2));
        var result = Copy(shape);
        var (radius, from, to) = (double.MaxValue, a, b);
        result.Splice(a, b, [new TrackPiece(pa.Item1, pa.Item2, Math.Atan2(direction.Z, direction.X), length, 0.0, true)]);

        if (i > 0 && !isJunction(a))
        {
            var before = handles[i - 1];
            if (Alignment.Join((line.X[before], line.Z[before]), Direction(shape.HeadingAt(shape.U[before])), pa, direction) is not { } curve)
                return null;
            result.Splice(before, a, curve.Pieces);
            (radius, from) = (Math.Min(radius, curve.Radius), before);
        }

        if (j < handles.Count - 1 && !isJunction(b))
        {
            var after = handles[j + 1];
            if (Alignment.Join(pb, direction, (line.X[after], line.Z[after]), Direction(shape.HeadingAt(shape.U[after]))) is not { } curve)
                return null;
            result.Splice(b, after, curve.Pieces);
            (radius, to) = (Math.Min(radius, curve.Radius), after);
        }

        return Finish(line, result, radius, from, to);
    }

    /// How far past each end of an eased stretch the track beyond is looked at for how sharply it's curving there.
    private const double EaseLook = 25.0;
    /// About how long the easement's pieces are: each a short arc, so together they bend smoothly (a spiral, as built). A long
    /// stretch takes longer ones, so there are never more than EasePieces.
    private const double EaseStep = 2.0;
    private const int EasePieces = 150;
    /// How far off the track as it was the curve may wander for a little smoothness: the bigger, the smoother and the further.
    public static double EaseHold { get; set; } = 0.5;
    /// The length at which the curve's sharpness weighs as much as how fast it changes: about how long its spirals run.
    private const double EaseSpiral = 45.0;

    /// One smooth curve between two handles in place of whatever's there: it leaves the first handle and arrives at the second
    /// on the track there, at the curvature the track beyond each is running at, and in between its curvature changes as
    /// gently as it can (spirals into and out of the body of the curve), at whatever length makes it gentlest. Null if no
    /// curve between them will do.
    public static Result? Ease(NetworkLine line, int a, int b, Func<int, bool> isJunction)
    {
        if (a > b)
            (a, b) = (b, a);
        // A junction between them stays where it is, so the curve can't run through one.
        for (var p = a + 1; p < b; p++)
            if (isJunction(p))
                return null;
        var shape = line.Alignment!;
        var (ua, ub) = (shape.U[a], shape.U[b]);
        var length = ub - ua;
        if (b - a < 1 || length < 5.0)
            return null;

        // The ends: where they are, the way the track runs through them, and how sharply it curves just beyond each.
        var total = shape.Length;
        double Heading(double u) => shape.HeadingAt(Math.Clamp(u, 0.0, total));
        var start = Alignment.Position(shape.Pieces, ua);
        var end = Alignment.Position(shape.Pieces, ub);
        var leave = Heading(ua - 1e-6);
        var turn = Turned(Heading, ua, ub);
        var before = Math.Min(EaseLook, ua);
        var after = Math.Min(EaseLook, total - ub);
        var curveIn = before > 2.0 ? Turned(Heading, ua - before, ua) / before : 0.0;
        var curveOut = after > 2.0 ? Turned(Heading, ub, ub + after) / after : 0.0;

        // The track as it is, every metre, for the curve to keep close to.
        var samples = Math.Max(2, (int)Math.Ceiling(length));
        var (refX, refZ) = (new double[samples + 1], new double[samples + 1]);
        for (var k = 0; k <= samples; k++)
            (refX[k], refZ[k]) = Alignment.Position(shape.Pieces, ua + length * k / samples);

        // The gentlest length is solved for along with the curvature, from a few starting lengths in case one goes astray.
        var chord = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Z - start.Z) * (end.Z - start.Z));
        (double L, double E, double[]? K) best = (0.0, double.MaxValue, null);
        foreach (var from in (double[])[Math.Max(length, chord * 1.001), chord * 1.01, Math.Max(length, chord) * 1.25])
            if (EaseSolve(start, end, leave, turn, curveIn, curveOut, from, chord, refX, refZ) is { } s && s.Energy < best.E)
                best = (s.Length, s.Energy, s.Curvature);
        if (best.K is null)
            return null;

        // The curve as short arcs, one per step.
        var curvature = best.K!;
        var h = best.L / curvature.Length;
        var pieces = new List<TrackPiece>(curvature.Length);
        var (x, z, heading) = (start.X, start.Z, leave);
        var sharpest = 0.0;
        foreach (var kappa in curvature)
        {
            var piece = new TrackPiece(x, z, heading, h, Math.Abs(kappa) < 1e-9 ? 0.0 : kappa, true);
            pieces.Add(piece);
            (x, z) = piece.At(h);
            heading = piece.HeadingAt(h);
            sharpest = Math.Max(sharpest, Math.Abs(kappa));
        }

        var result = Copy(shape);
        result.Splice(a, b, pieces);
        return Finish(line, result, sharpest < 1e-9 ? double.MaxValue : 1.0 / sharpest, a, b);
    }

    /// How far the track turns from u0 to u1, counting every bit of it (so a turn past half round counts as one).
    private static double Turned(Func<double, double> heading, double u0, double u1)
    {
        var steps = Math.Max(1, (int)Math.Ceiling((u1 - u0) / 1.0));
        var (sum, previous) = (0.0, heading(u0 + 1e-6));
        for (var k = 1; k <= steps; k++)
        {
            var next = heading(u0 + (u1 - u0) * k / steps - (k == steps ? 1e-6 : 0.0));
            sum += Math.IEEERemainder(next - previous, 2.0 * Math.PI);
            previous = next;
        }

        return sum;
    }

    /// The smoothest curve from start (leaving at heading) to end, turning by turn on the way and keeping near the track as it
    /// was (refX, refZ): its curvature over each of its steps, its length, and what it costs. The cost adds up how fast its
    /// curvature changes, how sharp it is, and how far it strays (by EaseHold); each step's curvature and the length are the
    /// unknowns, and the three things the end must come to (its place and heading) are held. Each round works out where the
    /// curve runs now and how that moves with every unknown, and solves for the cheapest unknowns with the end held (Gauss-Newton).
    private static (double[] Curvature, double Length, double Energy)? EaseSolve((double X, double Z) start, (double X, double Z) end, double leave, double turn,
        double curveIn, double curveOut, double length, double chord, double[] refX, double[] refZ)
    {
        var n = Math.Clamp((int)Math.Round(length / EaseStep), 8, EasePieces);
        var kappa = new double[n];
        for (var k = 0; k < n; k++)
            kappa[k] = turn / length;
        var weight = 1.0 / (EaseHold * EaseHold * Math.Pow(EaseSpiral, 4));

        var (px, pz) = (new double[n + 1], new double[n + 1]);
        var (qx, qz) = (new double[n + 1], new double[n + 1]);
        (double X, double Z, double Heading) Run(double h, double[] xs, double[] zs)
        {
            var (x, z, heading) = (start.X, start.Z, leave);
            (xs[0], zs[0]) = (x, z);
            for (var k = 0; k < n; k++)
            {
                (x, z) = new TrackPiece(x, z, heading, h, kappa[k], true).At(h);
                heading += kappa[k] * h;
                (xs[k + 1], zs[k + 1]) = (x, z);
            }

            return (x, z, heading);
        }

        // How far a place is off the old track, to its right, and the old track's right there.
        (double Off, double Nx, double Nz) Stray(double x, double z)
        {
            var (best, off, nx, nz) = (double.MaxValue, 0.0, 0.0, 0.0);
            for (var j = 0; j + 1 < refX.Length; j++)
            {
                var (dx, dz) = (refX[j + 1] - refX[j], refZ[j + 1] - refZ[j]);
                var span = Math.Max(dx * dx + dz * dz, 1e-12);
                var t = Math.Clamp(((x - refX[j]) * dx + (z - refZ[j]) * dz) / span, 0.0, 1.0);
                var (cx, cz) = (refX[j] + dx * t - x, refZ[j] + dz * t - z);
                var d = cx * cx + cz * cz;
                if (d >= best)
                    continue;
                var unit = Math.Sqrt(span);
                (best, nx, nz) = (d, -dz / unit, dx / unit);
                off = (x - refX[j] - dx * t) * nx + (z - refZ[j] - dz * t) * nz;
            }

            return (off, nx, nz);
        }

        var size = n + 1;
        for (var round = 0; round < 40; round++)
        {
            var h = length / n;
            var alpha = h * h / (EaseSpiral * EaseSpiral);
            var (x, z, heading) = Run(h, px, pz);
            var (ex, ez, eh) = (x - end.X, z - end.Z, heading - leave - turn);
            var delta = length * 1e-4;
            var (lx, lz, lh) = Run((length + delta) / n, qx, qz);

            // The end's place and heading, by each unknown (the length is the last).
            var jac = new double[3, size];
            for (var k = 0; k < n; k++)
            {
                var (mx, mz) = (x - (px[k] + px[k + 1]) * 0.5, z - (pz[k] + pz[k + 1]) * 0.5);
                (jac[0, k], jac[1, k], jac[2, k]) = (-mz * h, mx * h, h);
            }

            (jac[0, n], jac[1, n], jac[2, n]) = ((lx - x) / delta, (lz - z) / delta, (lh - heading) / delta);

            // How far each point between strays, and how that moves with each unknown.
            var stray = new double[n - 1];
            var by = new double[n - 1, size];
            for (var i = 1; i < n; i++)
            {
                var (off, nx, nz) = Stray(px[i], pz[i]);
                stray[i - 1] = off;
                for (var k = 0; k < i; k++)
                {
                    var (mx, mz) = (px[i] - (px[k] + px[k + 1]) * 0.5, pz[i] - (pz[k] + pz[k + 1]) * 0.5);
                    by[i - 1, k] = (-mz * nx + mx * nz) * h;
                }

                by[i - 1, n] = ((qx[i] - px[i]) * nx + (qz[i] - pz[i]) * nz) / delta;
            }

            // The cost as a quadratic in the new unknowns y (half of y'Hy - b'y), from the smoothness (tridiagonal, over h) and the
            // straying (linearised, times h). With the end held, the system to solve is [H J'; J 0][y; mu] = [b; r].
            var all = size + 3;
            var m = new double[all, all + 1];
            for (var k = 0; k < n; k++)
            {
                m[k, k] += (2.0 + alpha) / h;
                if (k > 0)
                    m[k, k - 1] -= 1.0 / h;
                if (k + 1 < n)
                    m[k, k + 1] -= 1.0 / h;
            }

            m[0, all] += curveIn / h;
            m[n - 1, all] += curveOut / h;
            m[n, n] += 1e-9;
            m[n, all] += 1e-9 * length;
            var now = new double[size];
            Array.Copy(kappa, now, n);
            now[n] = length;
            for (var i = 0; i < n - 1; i++)
            {
                var target = -stray[i];
                for (var k = 0; k < size; k++)
                    target += by[i, k] * now[k];
                for (var k = 0; k < size; k++)
                {
                    if (by[i, k] == 0.0)
                        continue;
                    var wk = weight * h * by[i, k];
                    m[k, all] += wk * target;
                    for (var q = 0; q < size; q++)
                        m[k, q] += wk * by[i, q];
                }
            }

            for (var r = 0; r < 3; r++)
            {
                var held = -(r == 0 ? ex : r == 1 ? ez : eh);
                for (var k = 0; k < size; k++)
                {
                    m[size + r, k] = jac[r, k];
                    m[k, size + r] = jac[r, k];
                    held += jac[r, k] * now[k];
                }

                m[size + r, all] = held;
            }

            if (Solve(m) is not { } y)
                return null;
            var change = 0.0;
            for (var k = 0; k < n; k++)
            {
                change = Math.Max(change, Math.Abs(y[k] - kappa[k]));
                kappa[k] = y[k];
            }

            var step = Math.Clamp(y[n] - length, -0.2 * length, 0.2 * length);
            length = Math.Max(length + step, chord * 0.999);
            var held3 = Math.Abs(ex) < 1e-4 && Math.Abs(ez) < 1e-4 && Math.Abs(eh) < 1e-7;
            if (round > 0 && held3 && ((change < 1e-6 && Math.Abs(step) < 1e-3) || round == 39))
            {
                var (fx, fz, _) = Run(length / n, px, pz);
                if (Math.Abs(fx - end.X) > 1e-3 || Math.Abs(fz - end.Z) > 1e-3)
                    return null;
                var strayCost = 0.0;
                for (var i = 1; i < n; i++)
                    strayCost += Math.Pow(Stray(px[i], pz[i]).Off, 2);
                return (kappa, length, Energy(kappa, curveIn, curveOut, length / n, alpha) + weight * (length / n) * strayCost);
            }
        }

        return null;
    }

    /// The easement's cost: how fast its curvature changes (from the track's beyond each end, too) and how sharp it is.
    private static double Energy(double[] kappa, double curveIn, double curveOut, double h, double alpha)
    {
        var sum = (kappa[0] - curveIn) * (kappa[0] - curveIn) + (kappa[^1] - curveOut) * (kappa[^1] - curveOut);
        for (var k = 0; k + 1 < kappa.Length; k++)
            sum += (kappa[k + 1] - kappa[k]) * (kappa[k + 1] - kappa[k]);
        foreach (var v in kappa)
            sum += alpha * v * v;
        return sum / h;
    }

    /// x for a symmetric tridiagonal system, middle all diagonal and -1 either side (Thomas's algorithm).
    private static double[] SolveTridiagonal(int n, double diagonal, double[] rhs)
    {
        var c = new double[n];
        var d = new double[n];
        var m = diagonal;
        c[0] = -1.0 / m;
        d[0] = rhs[0] / m;
        for (var i = 1; i < n; i++)
        {
            m = diagonal + c[i - 1];
            c[i] = -1.0 / m;
            d[i] = (rhs[i] + d[i - 1]) / m;
        }

        var x = new double[n];
        x[n - 1] = d[n - 1];
        for (var i = n - 2; i >= 0; i--)
            x[i] = d[i] - c[i] * x[i + 1];
        return x;
    }

    /// x for a small square system, given as its matrix with the right-hand side as a last column (Gaussian elimination).
    private static double[]? Solve(double[,] m)
    {
        var n = m.GetLength(0);
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-300)
                return null;
            for (var c = 0; c <= n; c++)
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < n; r++)
            {
                if (r == col)
                    continue;
                var f = m[r, col] / m[col, col];
                for (var c = col; c <= n; c++)
                    m[r, c] -= f * m[col, c];
            }
        }

        var x = new double[n];
        for (var r = 0; r < n; r++)
            x[r] = m[r, n] / m[r, r];
        return x;
    }

    private static Result Finish(NetworkLine line, Alignment shape, double radius, int from, int to)
    {
        var off = 0.0;
        var pieces = shape.Pieces;
        for (var p = from; p <= to; p++)
        {
            var (x, z) = Alignment.Position(pieces, shape.U[p]);
            off = Math.Max(off, TrackQuality.OffMapped(line, x, z, p));
        }

        return new Result(shape, radius, off);
    }
}
