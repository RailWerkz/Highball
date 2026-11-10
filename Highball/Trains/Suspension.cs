namespace Highball.Trains;

/// How a car body sits and moves on its truck springs, for drawing only (the dynamics don't see it): lower the more it
/// carries, and a slight roll as it runs, from the track under it and from leaning out on curves.
public static class Suspension
{
    /// How far a body settles from empty to its full rated load. Freight truck springs give about two inches between the
    /// two, out of their travel; a locomotive's weight doesn't change, so it doesn't settle.
    public const double FullLoadSag = 0.045;

    /// The height (over the rail tops) the body rolls about: about its centre plates on the truck bolsters.
    public const double RollCentre = 1.0;

    /// How far a car's body sits lower than it does empty, in metres.
    public static double Sag(VehicleDef def, double fill) => def.CanLoad ? FullLoadSag * Math.Clamp(fill, 0.0, 1.0) : 0.0;

    /// How much lower a locomotive sits with its tanks and sand boxes full than dry: its springs are stiff for its weight,
    /// so about half an inch.
    public const double FullSuppliesSag = 0.012;

    /// A locomotive's body sits and rolls as a heavy car does, always: its own weight is most of it. Its supplies (share of
    /// full, 0 to 1) make it heavier still. Returns the load it rolls as, for Step.
    public static double LocoLoad(double suppliesShare) => 0.65 + 0.35 * Math.Clamp(suppliesShare, 0.0, 1.0);

    // The roll is a damped spring: a body rocks a little under one hertz, a loaded one slower, and settles in a couple of
    // swings. What drives it is kept tiny, well under a degree, so it reads as weight, not as a boat.
    private const double EmptyHz = 0.9;
    private const double LoadedHz = 0.65;
    private const double Damping = 0.22;
    /// Most the track's unevenness tips a body at speed, radians (about a fifth of a degree).
    private const double TrackRoll = 0.0035;
    /// Leaning out on a curve, radians per m/s² of sideways acceleration, and the most it ever leans.
    private const double LeanPerAccel = 0.004;
    private const double MaxLean = 0.008;
    /// The speed the track's unevenness reaches its full effect at, m/s (about 45 mph).
    private const double FullRollSpeed = 20.0;

    /// One car's roll and how fast it's changing.
    public struct Roll
    {
        public double Angle, Rate;
    }

    /// Moves a car's roll on by dt seconds. speed is the train's (either way); curvature 1 / radius under the car, and
    /// inward how much its sideways axis points to the inside of the curve (-1 to 1, 0 on straight track); (x, z) where it
    /// is, so every car rocks the same way over the same piece of track. A positive roll tips its top towards -x.
    public static void Step(ref Roll roll, double dt, double speed, double curvature, double inward, double fill, double x, double z)
    {
        if (dt <= 0.0)
            return;

        fill = Math.Clamp(fill, 0.0, 1.0);
        var v = Math.Abs(speed);
        var pace = Math.Clamp(v / FullRollSpeed, 0.0, 1.0);
        pace *= pace * (3.0 - 2.0 * pace);

        // A heavier body is pushed about less by the same track.
        var track = TrackRoll * pace * (1.0 - 0.35 * fill) * Unevenness(x, z);
        // Out on a curve: the top towards the outside, so -x when x points in.
        var lean = Math.Clamp(LeanPerAccel * v * v * curvature, 0.0, MaxLean) * inward;
        var target = track + lean;

        var omega = 2.0 * Math.PI * (EmptyHz + (LoadedHz - EmptyHz) * fill);
        // In short steps, so a slow frame doesn't throw it.
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 120.0)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var accel = omega * omega * (target - roll.Angle) - 2.0 * Damping * omega * roll.Rate;
            roll.Rate += accel * h;
            roll.Angle += roll.Rate * h;
        }

        roll.Angle = Math.Clamp(roll.Angle, -0.02, 0.02);
    }

    /// The track's unevenness at a place, -1 to 1: a few lengths of wave, from about four metres to fourteen, crossing at
    /// odd angles, so it never repeats along a line.
    private static double Unevenness(double x, double z) =>
        0.5 * Math.Sin(0.41 * x + 0.17 * z) + 0.3 * Math.Sin(0.23 * z - 0.67 * x + 1.3) + 0.2 * Math.Sin(1.13 * x + 0.89 * z + 2.1);
}
