using Highball.Trains;
using RailWerkz.Core.Logging;

namespace Highball.Builder;

/// The stock on the track kept with the route: every cut written down as it stands, and put back as it was left when the
/// route's opened again (standing, brakes on, its engines running).
public sealed partial class TrainPanel
{
    /// Saved cuts that couldn't be put back (their track had changed, or something stood there): kept, and saved again
    /// with the rest, so they aren't lost by a save; tried again the next time the route's opened.
    private readonly List<PlacedCut> _unplaced = [];

    /// Every cut as it stands now, its front placed on the map by toGeo (plan x, z to latitude, longitude).
    public List<PlacedCut> Snapshot(Func<double, double, (double Lat, double Lon)> toGeo)
    {
        var list = new List<PlacedCut>(_cuts.Count);
        foreach (var cut in _cuts)
        {
            var train = cut.Train;
            var (x, _, z) = train.Graph.PositionOf(train.Head);
            var (fx, fz) = train.Graph.FacingOf(train.Head);
            var (lat, lon) = toGeo(x, z);
            var cars = train.Cars.Select(c => new ConsistCar(c.Def.Id, c.Livery?.Id, c.Reversed, c.Fill > 0.0, c.Tag, c.Fill, c.Cargo)).ToList();
            list.Add(new PlacedCut(train.Name, lat, lon, Math.Atan2(fx, fz), cars,
                [.. train.Body.HandbrakeOn],
                [.. train.Body.Coupled],
                [.. train.Body.Brakes.Hose],
                ReferenceEquals(cut, _controlled),
                [.. train.Body.Systems.Select(s => s?.Capacity is null ? null : new[] { s.Fuel, s.Oil, s.Water, s.Sand })]));
        }

        list.AddRange(_unplaced);
        return list;
    }

    /// Puts saved cuts back on the track, each front where it was (toPlan: latitude, longitude to plan x, z) and facing the
    /// same way. A cut whose track has gone, or that would stand on another, is left off. Returns how many went back.
    public int Restore(IReadOnlyList<PlacedCut> saved, Func<double, double, (double X, double Z)> toPlan)
    {
        if (_graph is not { } graph)
            return 0;
        var put = 0;
        var lost = 0;
        foreach (var s in saved)
        {
            if (VehicleCount + s.Cars.Count > MaxVehicles)
                break;
            var (x, z) = toPlan(s.Lat, s.Lon);
            var (fx, fz) = (Math.Sin(s.Heading), Math.Cos(s.Heading));
            if (graph.Nearest(x, z, 5.0) is not { } near)
            {
                Log.Warn($"Placed stock: {s.Name} left off, no track within 5 m of where it stood");
                _unplaced.Add(s);
                lost++;
                continue;
            }

            var (nx, nz) = graph.FacingOf(near);
            var head = nx * fx + nz * fz >= 0.0 ? near : near.Reversed;
            var train = Trains.Train.Place(graph, new Consist { Name = s.Name, Cars = s.Cars }, head, out var why, PlacedAs.Running);
            if (train is null || Overlapping(train) is { } other)
            {
                Log.Warn($"Placed stock: {s.Name} left off, {(train is null ? why : "another cut stands there")}");
                _unplaced.Add(s);
                lost++;
                continue;
            }

            // As it was left: handbrakes, and couplings and hoses parted. (Only if every vehicle's still in the catalog,
            // so the cars line up with what was saved.)
            var body = train.Body;
            if (train.Cars.Count == s.Cars.Count)
            {
                for (var i = 0; i < body.Count && i < (s.Handbrakes?.Count ?? 0); i++)
                    if (s.Handbrakes![i])
                    {
                        body.HandbrakeOn[i] = true;
                        body.Handbrake[i] = 1.0;
                    }

                // Each engine's fuel, oil, water and sand as it was left.
                for (var i = 0; i < body.Count && i < (s.Supplies?.Count ?? 0); i++)
                    if (s.Supplies![i] is { Length: 4 } left && body.Systems[i] is { Capacity: { } full } systems)
                    {
                        systems.Fuel = Math.Clamp(left[0], 0.0, full.FuelL);
                        systems.Oil = Math.Clamp(left[1], 0.0, full.OilL);
                        systems.Water = Math.Clamp(left[2], 0.0, full.WaterL);
                        systems.Sand = Math.Clamp(left[3], 0.0, full.SandM3);
                    }

                for (var l = 0; l < body.Coupled.Length; l++)
                {
                    if (s.Hoses is { } hoses && l < hoses.Count && !hoses[l])
                    {
                        body.Brakes.Hose[l] = false;
                        body.Brakes.CockAhead[l] = body.Brakes.CockBehind[l] = 0.0;
                    }

                    if (s.Coupled is { } coupled && l < coupled.Count && !coupled[l])
                        body.Uncouple(l, out _);
                }
            }

            train.Secure();
            Hook(train);
            var cut = new Cut { Train = train };
            _cuts.Add(cut);
            if (s.Controlled && train.Cars.Any(c => c.Def.Kind == VehicleKind.Locomotive))
                _controlled = cut;
            put++;
        }

        if (_focus is null && (_controlled ?? _cuts.LastOrDefault()) is { } focus)
            Focus(focus);
        if (lost > 0)
            _notice = lost == 1 ? "A saved cut couldn't be put back where it stood; it's kept in the route's save" : $"{lost} saved cuts couldn't be put back where they stood; they're kept in the route's save";
        Log.Info($"Placed stock: {put} cuts put back{(lost > 0 ? $", {lost} left off" : "")}");
        return put;
    }
}
