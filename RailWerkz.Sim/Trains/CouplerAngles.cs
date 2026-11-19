namespace Highball.Trains;

/// Couplers round curves, and what their forces do to the wheels there. Each coupled pair lies along the line between its two
/// draft pivots (a little behind each pulling face, on its car's centre line), so on a curve, or where cars of unlike overhangs
/// meet, it's at an angle to each car's body: each swings on its striker, as far as its limit (Limit).
///
/// A coupler's force acts along that line, so at an angle part of it pushes the car's end sideways (outward under buff, inward
/// under draft: stringlining). The nearer truck takes it, more than all of it (levered by the end's overhang past the truck), on
/// its flanging wheels. Over a wheel's share of the car's weight, that's its L/V: past WheelClimb for ClimbTime while rolling, the
/// flange climbs the rail and the car's off (AAR's 50 ms criterion; Nadal's limit for a 75 degree flange is about 0.8 to 1.0).
public static class CouplerAngles
{
    /// The most a coupler swings either way on its striker, degrees: a locomotive's alignment-control coupler (held near centre by
    /// its draft gear's wings past this), a long car's extended draft gear (an 89 ft autorack or flat), any other freight car's.
    public const double LocomotiveSwing = 17.0;
    public const double LongCarSwing = 20.0;
    public const double FreightSwing = 15.0;

    /// Past this a car's counted long, metres over its couplers.
    public const double LongCar = 25.0;

    /// How far a coupler's draft pivot is behind its pulling face, metres (Couplers/coupler.json: reach less the knuckle's nose).
    public const double PivotBack = 0.42;

    /// The L/V past which a flange climbs the rail, and for how long it must stay past it, seconds.
    public const double WheelClimb = 1.0;
    public const double ClimbTime = 0.05;

    /// Slower than this, metres a second, a wheel can't climb (it needs to roll up the flange).
    public const double ClimbSpeed = 0.3;

    /// Past this the shank's bound in its striker, and the angle the force acts at is taken no further, degrees.
    private const double MostAngle = 25.0;

    private const double Gravity = 9.80665;

    /// A car's swing limit, degrees.
    public static double Limit(VehicleDef? def) => def is null ? FreightSwing
        : def.Kind == VehicleKind.Locomotive ? LocomotiveSwing
        : def.Length >= LongCar ? LongCarSwing : FreightSwing;

    /// Each link's couplers' angles to their own cars (degrees: the front car's rear coupler, the rear car's front coupler; 0 for an
    /// uncoupled link), and each car's worst wheel L/V from its couplers' forces, from where the cars stand now.
    public static void Measure(Train train, double[] rearAngle, double[] frontAngle, double[] lv)
    {
        var cars = train.Cars;
        Array.Clear(lv);
        for (var i = 0; i + 1 < cars.Count; i++)
        {
            rearAngle[i] = frontAngle[i] = 0.0;
            if (!train.Body.Coupled[i])
                continue;
            var (a, b) = (cars[i], cars[i + 1]);
            if (Axis(a) is not { } ua || Axis(b) is not { } ub)
                continue;

            // Each car's pivot (out along its body from its truck, PivotBack short of its pulling face), and where the two knuckles
            // meet: between the two pulling faces as the bodies hold them. A coupler's a rigid arm from its pivot to the knuckles,
            // however the slack's taken up (the slack's in the draft gear behind the pivot), so each points from its own pivot there.
            var pa = (X: a.RearAt.X - ua.X * (a.TrailingOverhang - PivotBack), Z: a.RearAt.Z - ua.Z * (a.TrailingOverhang - PivotBack));
            var pb = (X: b.FrontAt.X + ub.X * (b.LeadingOverhang - PivotBack), Z: b.FrontAt.Z + ub.Z * (b.LeadingOverhang - PivotBack));
            var meet = (X: (pa.X - ua.X * PivotBack + pb.X + ub.X * PivotBack) * 0.5, Z: (pa.Z - ua.Z * PivotBack + pb.Z + ub.Z * PivotBack) * 0.5);

            // Each coupler from its car's own outward way (the front car's rear is -axis, the rear car's front +axis) to the knuckles,
            // its arm never shorter than PivotBack (bunched past touching, the heads don't come closer).
            rearAngle[i] = Toward(-ua.X, -ua.Z, meet.X - pa.X, meet.Z - pa.Z);
            frontAngle[i] = Toward(ub.X, ub.Z, meet.X - pb.X, meet.Z - pb.Z);

            var force = Math.Abs(train.Body.CouplerForce[i]);
            if (force < 1.0)
                continue;
            // The force's angle for the wheels: no more than the curve under each end's truck makes it (its curvature times how far the
            // pivot is from the body's middle: where on a steady curve the coupler's line, the curve's tangent there, leaves the
            // body's chord). The cars as placed can stand across a kink where two pieces of track meet a little out of line, which
            // the geometry reads as a sharp angle no real track has; the curve doesn't.
            var curveA = Math.Abs(train.Graph.CurvatureOf(a.RearTruck)) * (a.Def.TruckCentres * 0.5 + a.TrailingOverhang - PivotBack) * 180.0 / Math.PI;
            var curveB = Math.Abs(train.Graph.CurvatureOf(b.FrontTruck)) * (b.Def.TruckCentres * 0.5 + b.LeadingOverhang - PivotBack) * 180.0 / Math.PI;
            lv[i] = Math.Max(lv[i], WheelLv(a, force, Math.Min(Math.Abs(rearAngle[i]), curveA), a.TrailingOverhang - PivotBack));
            lv[i + 1] = Math.Max(lv[i + 1], WheelLv(b, force, Math.Min(Math.Abs(frontAngle[i]), curveB), b.LeadingOverhang - PivotBack));
        }
    }

    /// A car's worst wheel L/V from a coupler force at an angle at one end: the sideways part, levered onto the nearer truck and
    /// shared by its axles, over a wheel's share of the car's weight.
    private static double WheelLv(TrainCar car, double force, double degrees, double overhang)
    {
        var angle = Math.Min(Math.Abs(degrees), MostAngle) * Math.PI / 180.0;
        var lateral = force * Math.Sin(angle);
        var centres = Math.Max(car.Def.TruckCentres, 1.0);
        var onTruck = lateral * (centres + Math.Max(overhang, 0.0)) / centres;
        var axlesPerTruck = Math.Max(car.Def.Axles / 2, 1);
        var perWheel = car.Mass * Gravity / (2.0 * Math.Max(car.Def.Axles, 1));
        return perWheel > 0.0 ? onTruck / axlesPerTruck / perWheel : 0.0;
    }

    /// A car's way along the train on the plan (from its rear truck to its front), or null if its trucks are on top of each other.
    private static (double X, double Z)? Axis(TrainCar car)
    {
        var (dx, dz) = (car.FrontAt.X - car.RearAt.X, car.FrontAt.Z - car.RearAt.Z);
        var length = Math.Sqrt(dx * dx + dz * dz);
        return length < 1e-3 ? null : (dx / length, dz / length);
    }

    /// The angle from a coupler's straight-out way (ox, oz) to its arm (dx, dz), degrees, signed; the arm's along-the-car part taken
    /// as at least PivotBack.
    private static double Toward(double ox, double oz, double dx, double dz)
    {
        var along = Math.Max(dx * ox + dz * oz, PivotBack);
        var across = ox * dz - oz * dx;
        return Math.Atan2(across, along) * 180.0 / Math.PI;
    }

    /// The angle from one way to another on the plan, degrees, signed.
    private static double Signed(double ax, double az, double bx, double bz) =>
        Math.Atan2(ax * bz - az * bx, ax * bx + az * bz) * 180.0 / Math.PI;
}
