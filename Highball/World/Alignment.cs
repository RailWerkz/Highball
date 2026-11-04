namespace Highball.World;

/// A length of track as it's really laid: straight (no curvature) or a circular arc. Heading: the direction it leaves
/// (X, Z) in, as an angle in the X-Z plane; curvature is signed, positive turning the heading up. Fitted: worked out as
/// track, rather than a chord between two mapped points.
public readonly record struct TrackPiece(double X, double Z, double Heading, double Length, double Curvature, bool Fitted)
{
    public (double X, double Z) At(double u)
    {
        if (Math.Abs(Curvature) < 1e-9)
            return (X + Math.Cos(Heading) * u, Z + Math.Sin(Heading) * u);
        var end = Heading + Curvature * u;
        return (X + (Math.Sin(end) - Math.Sin(Heading)) / Curvature, Z - (Math.Cos(end) - Math.Cos(Heading)) / Curvature);
    }

    public double HeadingAt(double u) => Heading + Curvature * u;
}

/// A rail line's real shape: the straights and arcs it's made of, and how far along them each of its points lies. This is
/// what the track is; the line's X and Z are only samples of it, so whatever changes the track changes this, and a straight
/// stays exactly straight however many steps it goes through.
public sealed class Alignment
{
    public List<TrackPiece> Pieces { get; private set; } = [];

    /// Distance along the pieces at each of the line's points.
    public double[] U { get; private set; } = [];

    /// One read back as it was saved.
    public static Alignment Restored(TrackPiece[] pieces, double[] u) => new() { Pieces = [.. pieces], U = u };

    /// The line as it stands, each segment a straight.
    public static Alignment FromPoints(double[] x, double[] z)
    {
        var alignment = new Alignment { U = new double[x.Length] };
        for (var i = 0; i + 1 < x.Length; i++)
        {
            var (dx, dz) = (x[i + 1] - x[i], z[i + 1] - z[i]);
            var length = Math.Sqrt(dx * dx + dz * dz);
            alignment.Pieces.Add(new TrackPiece(x[i], z[i], Math.Atan2(dz, dx), length, 0.0, false));
            alignment.U[i + 1] = alignment.U[i] + length;
        }

        return alignment;
    }

    public double Length => U.Length == 0 ? 0.0 : U[^1];

    /// The way the track runs u along it.
    public double HeadingAt(double u)
    {
        foreach (var piece in Pieces)
        {
            if (u <= piece.Length)
                return piece.HeadingAt(Math.Max(u, 0.0));
            u -= piece.Length;
        }

        var last = Pieces[^1];
        return last.HeadingAt(last.Length);
    }

    public static double LengthOf(IReadOnlyList<TrackPiece> pieces) => pieces.Sum(p => p.Length);

    /// A point u along a run of pieces.
    public static (double X, double Z) Position(IReadOnlyList<TrackPiece> pieces, double u)
    {
        foreach (var piece in pieces)
        {
            if (u <= piece.Length)
                return piece.At(Math.Max(u, 0.0));
            u -= piece.Length;
        }

        var last = pieces[^1];
        return last.At(last.Length);
    }

    /// The same track, run the other way.
    public static List<TrackPiece> Reversed(IReadOnlyList<TrackPiece> pieces)
    {
        var reversed = new List<TrackPiece>(pieces.Count);
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var piece = pieces[i];
            var (x, z) = piece.At(piece.Length);
            reversed.Add(piece with { X = x, Z = z, Heading = piece.HeadingAt(piece.Length) + Math.PI, Curvature = -piece.Curvature });
        }

        return reversed;
    }

    /// The track between two of the line's points replaced: pieces runs from the first to the second, and the points between
    /// keep their shares of the distance, or lie along it at along (from the first point), if given.
    public void Splice(int from, int to, IReadOnlyList<TrackPiece> pieces, double[]? along = null)
    {
        var (a, b) = (U[from], U[to]);
        var list = new List<TrackPiece>();
        var walked = 0.0;
        foreach (var piece in Pieces)
        {
            var (start, end) = (walked, walked + piece.Length);
            walked = end;
            if (start < a)
                list.Add(piece with { Length = Math.Min(piece.Length, a - start) });
        }

        list.AddRange(pieces);
        walked = 0.0;
        foreach (var piece in Pieces)
        {
            var (start, end) = (walked, walked + piece.Length);
            walked = end;
            if (end <= b)
                continue;
            var cut = Math.Max(b - start, 0.0);
            var (x, z) = piece.At(cut);
            list.Add(piece with { X = x, Z = z, Heading = piece.HeadingAt(cut), Length = piece.Length - cut });
        }

        var length = LengthOf(pieces);
        var shift = length - (b - a);
        var span = Math.Max(b - a, 1e-9);
        var u = new double[U.Length];
        for (var p = 0; p < U.Length; p++)
            u[p] = p <= from ? U[p] : p >= to ? U[p] + shift : a + (along is not null ? along[p - from] : (U[p] - a) / span * length);
        (Pieces, U) = (list, u);
    }

    /// One of the line's points moved to where it must be, with a straight each side of it to the points next to it.
    public void Snap(NetworkLine line, int point, double x, double z)
    {
        void Chord(int from, int to, (double X, double Z) a, (double X, double Z) b)
        {
            var (dx, dz) = (b.X - a.X, b.Z - a.Z);
            Splice(from, to, [new TrackPiece(a.X, a.Z, Math.Atan2(dz, dx), Math.Sqrt(dx * dx + dz * dz), 0.0, false)]);
        }

        if (point > 0)
            Chord(point - 1, point, (line.X[point - 1], line.Z[point - 1]), (x, z));
        if (point < line.Count - 1)
            Chord(point, point + 1, (x, z), (line.X[point + 1], line.Z[point + 1]));
        (line.X[point], line.Z[point]) = (x, z);
    }

    /// Writes the track between two points (all of it by default) to the line's points.
    public void Sample(NetworkLine line, int from = 0, int to = int.MaxValue)
    {
        to = Math.Min(to, line.Count - 1);
        var (piece, start) = (0, 0.0);
        for (var p = from; p <= to; p++)
        {
            while (piece < Pieces.Count - 1 && U[p] > start + Pieces[piece].Length)
                start += Pieces[piece++].Length;
            (line.X[p], line.Z[p]) = Pieces[piece].At(Math.Clamp(U[p] - start, 0.0, Pieces[piece].Length));
        }
    }

    /// One curve between two points and headings as two arcs meeting smoothly (a straight where an arc would be one). Its
    /// pieces, its tightest radius, and whether it bends one way only. Null if the two can't be joined going forward.
    public static (List<TrackPiece> Pieces, double Radius, bool OneWay)? Join((double X, double Z) a, (double X, double Z) leave, (double X, double Z) b, (double X, double Z) arrive)
    {
        var (vx, vz) = (b.X - a.X, b.Z - a.Z);
        var vv = vx * vx + vz * vz;
        if (vv < 1e-6)
            return null;
        var vt = vx * (leave.X + arrive.X) + vz * (leave.Z + arrive.Z);
        var dot = leave.X * arrive.X + leave.Z * arrive.Z;
        var bend = 2.0 * (1.0 - dot);
        double d;
        if (bend < 1e-9)
        {
            if (vt <= 1e-9)
                return null;
            d = vv / (2.0 * vt);
        }
        else
            d = (-vt + Math.Sqrt(vt * vt + 2.0 * bend * vv)) / (2.0 * bend);
        if (d <= 0.0)
            return null;

        var meet = ((a.X + leave.X * d + b.X - arrive.X * d) / 2.0, (a.Z + leave.Z * d + b.Z - arrive.Z * d) / 2.0);
        if (ArcFrom(a, leave, meet) is not { } first || ArcFrom(meet, (Math.Cos(first.HeadingAt(first.Length)), Math.Sin(first.HeadingAt(first.Length))), b) is not { } second)
            return null;

        var tightest = Math.Min(RadiusOf(first), RadiusOf(second));
        var pieces = new List<TrackPiece>();
        if (first.Length > 1e-6)
            pieces.Add(first);
        if (second.Length > 1e-6)
            pieces.Add(second);
        var turns = pieces.Where(p => Math.Abs(p.Curvature) > 1.0 / StraightRadius).Select(p => Math.Sign(p.Curvature)).Distinct().Count();
        return (pieces, tightest, turns <= 1);
    }

    /// A curve gentler than this counts as straight, when judging whether one doubles back.
    private const double StraightRadius = 3000.0;

    private static double RadiusOf(TrackPiece piece) => Math.Abs(piece.Curvature) < 1e-9 ? double.MaxValue : 1.0 / Math.Abs(piece.Curvature);

    private static TrackPiece? ArcFrom((double X, double Z) from, (double X, double Z) heading, (double X, double Z) to)
    {
        var (cx, cz) = (to.X - from.X, to.Z - from.Z);
        var cc = cx * cx + cz * cz;
        var angle = Math.Atan2(heading.Z, heading.X);
        if (cc < 1e-12)
            return new TrackPiece(from.X, from.Z, angle, 0.0, 0.0, true);
        var cross = heading.X * cz - heading.Z * cx;
        var along = heading.X * cx + heading.Z * cz;
        if (along <= 0.0)
            return null;
        if (Math.Abs(cross) < 1e-9)
            return new TrackPiece(from.X, from.Z, angle, Math.Sqrt(cc), 0.0, true);
        var curvature = 2.0 * cross / cc;
        return new TrackPiece(from.X, from.Z, angle, 2.0 * Math.Atan2(cross, along) / curvature, curvature, true);
    }
}
