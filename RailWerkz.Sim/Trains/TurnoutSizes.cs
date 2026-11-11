namespace Highball.Trains;

/// The standard switch sizes: frog numbers and their lead curves' radii (US practice), as modelled in
/// Tools/Blender/turnouts.py.
public static class TurnoutSizes
{
    public static readonly (int Number, float Radius)[] Sizes = [(6, 80f), (7, 112f), (8, 145f), (10, 237f), (15, 500f), (20, 890f)];

    public static float RadiusOf(int number) => Sizes.First(s => s.Number == number).Radius;
}
