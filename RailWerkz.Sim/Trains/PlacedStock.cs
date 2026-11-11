namespace Highball.Trains;

/// A cut left on a route, as saved: its vehicles from the front (paint, which way round, load, name), where its front
/// stands (on the map, and the way it faces along the track there), each car's handbrake, each coupling's state, and
/// whether it was the one being driven.
public sealed record PlacedCut(
    string Name,
    double Lat,
    double Lon,
    double Heading,
    List<ConsistCar> Cars,
    List<bool>? Handbrakes = null,
    List<bool>? Coupled = null,
    List<bool>? Hoses = null,
    bool Controlled = false,
    List<double[]?>? Supplies = null);
