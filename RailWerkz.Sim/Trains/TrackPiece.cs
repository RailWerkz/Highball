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
