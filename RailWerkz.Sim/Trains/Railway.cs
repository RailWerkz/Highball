using RailWerkz.Core.Logging;

namespace Highball.Trains;

/// Every cut of stock on the track and what happens between them: stepping them on, cuts coupling when they meet
/// gently (both taken off when they don't), uncoupled cuts drawing apart into cuts of their own, and switches forced
/// over when a train runs through one set against it. No drawing, input or sound: the editor's train panel drives and
/// shows it, and a server can run it on its own.
public class Railway
{
    /// The most vehicles there can be on the track at once.
    public const int MaxVehicles = 10000;

    /// Run together faster than this (mph, closing) and both cuts are taken off: there's no crash yet to show.
    public const double CoupleSpeed = 15.0;

    /// An uncoupled cut drawn this far (metres) from the rest of its train becomes a cut of its own.
    public const double SplitGap = 3.0;

    /// A cut on the track. Whoever shows it can make its own kind (NewCut) to keep what it needs to draw it.
    public class Cut(Train train)
    {
        public Train Train { get; internal set; } = train;

        /// Seconds it's stood still: a cut left standing a while isn't stepped, until something touches it.
        public double Still;

        public bool Asleep => Still > 3.0;

        /// Another player's, shown where they say it is: never stepped, coupled to or saved here.
        public bool Remote { get; internal set; }
    }

    private readonly List<Cut> _cuts = [];

    /// Makes a cut for a train: from the cut it was split from or the first of those it was joined from (or null).
    public Func<Train, Cut?, Cut> NewCut { get; set; } = (train, _) => new Cut(train);

    public IReadOnlyList<Cut> Cuts => _cuts;

    public RailGraph? Graph { get; private set; }

    /// The switches trains run through; one run through set against the train is forced over.
    public SwitchStates Switches { get; set; } = new();

    /// The cut being driven, if any.
    public Cut? Controlled { get; set; }

    /// Saved cuts that couldn't be put back (their track had changed, or something stood there): kept, and saved again
    /// with the rest, so they aren't lost by a save; tried again the next time the route's opened.
    private readonly List<PlacedCut> _unplaced = [];

    /// Something the crew should hear about.
    public event Action<string>? Notice;

    /// A cut was taken off the track.
    public event Action<Cut>? Removed;

    /// A cut became another: coupled into a longer one, or split (this the half that carries on as it).
    public event Action<Cut, Cut>? Replaced;

    public int VehicleCount => _cuts.Sum(c => c.Train.Cars.Count);

    private void Tell(string what) => Notice?.Invoke(what);

    /// Puts every cut onto a new graph (the track was laid out again), taking off any whose track has gone.
    public void Use(RailGraph graph)
    {
        Graph = graph;
        var lost = 0;
        foreach (var cut in _cuts.ToList())
            if (!cut.Train.MoveTo(graph))
            {
                Remove(cut);
                lost++;
            }

        if (lost > 0)
            Tell(lost == 1 ? "Track changed: 1 cut removed" : $"Track changed: {lost} cuts removed");
    }

    /// Takes a cut off the track.
    public void Remove(Cut cut)
    {
        _cuts.Remove(cut);
        if (ReferenceEquals(Controlled, cut))
            Controlled = null;
        Removed?.Invoke(cut);
    }

    /// Puts a train on the track as a cut of its own, unless stock already stands there (why says so).
    public Cut? Add(Train train, Cut? from, out string? why)
    {
        if (Overlapping(train) is { } there)
        {
            why = $"There's already stock there ({there.Train.Name})";
            return null;
        }

        Hook(train);
        var cut = NewCut(train, from);
        _cuts.Add(cut);
        why = null;
        return cut;
    }

    /// Another player's train, put on the track to be shown where they say it is (see Train.Mirror).
    public Cut AddRemote(Train train)
    {
        var cut = NewCut(train, null);
        cut.Remote = true;
        _cuts.Add(cut);
        return cut;
    }

    /// A cut's train swapped for another made from it (turned end for end, say).
    public void Swap(Cut cut, Train train)
    {
        Hook(train);
        cut.Train = train;
        cut.Still = 0.0;
    }

    /// Hooks a cut's train up to tell the crew what happens and to force switches it runs through.
    private void Hook(Train train)
    {
        train.Happened = what =>
        {
            Log.Info($"Train {train.Name}: {what}");
            Tell(what);
        };
        train.RunThrough = node =>
        {
            Log.Info($"Train {train.Name}: ran through switch {node}, set against it");
            Tell("Ran through a switch: forced over");
            Switches.Set(node, !train.Graph.Reversed(node));
        };
    }

    /// A cut already standing where a train would be put: any of its trucks within a car's width of the new one's.
    public Cut? Overlapping(Train train)
    {
        foreach (var cut in _cuts)
        {
            if (!ReferenceEquals(cut.Train.Graph, train.Graph))
                continue;
            foreach (var a in train.Cars)
                foreach (var b in cut.Train.Cars)
                    foreach (var p in (ReadOnlySpan<(double X, double Y, double Z)>)[a.FrontAt, a.RearAt])
                        foreach (var q in (ReadOnlySpan<(double X, double Y, double Z)>)[b.FrontAt, b.RearAt])
                            if ((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z) < 2.0 * 2.0 && Math.Abs(p.Y - q.Y) < 3.0)
                                return cut;
        }

        return null;
    }

    /// Steps every cut on by dt seconds (one left standing a while sleeps, unless it's driven or awake is it), then
    /// splits cuts drawn apart and couples cuts that met.
    public void Step(double dt, Cut? awake = null)
    {
        foreach (var cut in _cuts)
        {
            if (cut.Remote || (!ReferenceEquals(cut, Controlled) && !ReferenceEquals(cut, awake) && cut.Asleep))
                continue;
            cut.Train.Update(dt);
            var moving = cut.Train.Body.Velocity.Any(v => Math.Abs(v) > 1e-4);
            cut.Still = moving ? 0.0 : cut.Still + dt;
        }

        Splits();
        Contacts(dt);
    }

    // --- Cuts meeting, and cuts drawing apart. ---

    /// Each end of a cut: where its coupler is, which way it faces out, and how fast it's moving out that way.
    private static ((double X, double Y, double Z) At, (double X, double Z) Out, double Speed) End(Train train, bool front)
    {
        var at = front ? train.Head : train.Cars[^1].Rear;
        var (fx, fz) = train.Graph.FacingOf(at);
        return front
            ? (train.Graph.PositionOf(at), (fx, fz), train.Body.Velocity[0])
            : (train.Graph.PositionOf(at), (-fx, -fz), -train.Body.Velocity[^1]);
    }

    /// Cuts whose ends have met: coupled into one train under CoupleSpeed, both taken off above it.
    private void Contacts(double dt)
    {
        for (var i = 0; i < _cuts.Count; i++)
        {
            for (var j = i + 1; j < _cuts.Count; j++)
            {
                var (a, b) = (_cuts[i], _cuts[j]);
                if (a.Remote || b.Remote || !ReferenceEquals(a.Train.Graph, b.Train.Graph) || (a.Asleep && b.Asleep))
                    continue;
                foreach (var frontA in (ReadOnlySpan<bool>)[true, false])
                {
                    foreach (var frontB in (ReadOnlySpan<bool>)[true, false])
                    {
                        var ea = End(a.Train, frontA);
                        var eb = End(b.Train, frontB);
                        // Facing each other on the same track, near enough to have met this frame at the speed they're closing.
                        if (ea.Out.X * eb.Out.X + ea.Out.Z * eb.Out.Z > -0.7)
                            continue;
                        var closing = ea.Speed + eb.Speed;
                        var (dx, dy, dz) = (ea.At.X - eb.At.X, ea.At.Y - eb.At.Y, ea.At.Z - eb.At.Z);
                        var apart = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (apart > 0.35 + Math.Max(0.0, closing) * dt * 1.5 || closing < -0.05)
                            continue;

                        var mph = Math.Abs(closing) * 2.2369363;
                        if (mph > CoupleSpeed)
                        {
                            Log.Info($"Trains: {a.Train.Name} and {b.Train.Name} met at {mph:0.0} mph: both taken off");
                            Tell($"{a.Train.Name} and {b.Train.Name} hit at {mph:0} mph: both removed");
                            Remove(a);
                            Remove(b);
                            return;
                        }

                        Couple(a, frontA, b, frontB, mph);
                        return;
                    }
                }
            }
        }
    }

    /// Two cuts that met, as one: the driven one (or else the first) keeps the controls and stays the right way round.
    private void Couple(Cut a, bool frontA, Cut b, bool frontB, double mph)
    {
        // The driven cut (or else the first) leads, so its engine's brake valve and controls work the train: turned end for
        // end if it met at its front, so it meets the other at its rear; the other turned as needed to meet it at its front.
        if (ReferenceEquals(b, Controlled))
            (a, frontA, b, frontB) = (b, frontB, a, frontA);
        var joined = Train.Join(a.Train, frontA, b.Train, !frontB, -0.02);
        if (joined is null)
            return;

        Hook(joined);
        var cut = NewCut(joined, null);
        var wasControlled = ReferenceEquals(a, Controlled) || ReferenceEquals(b, Controlled);
        _cuts.Remove(a);
        _cuts.Remove(b);
        _cuts.Add(cut);
        if (wasControlled)
            Controlled = cut;
        Replaced?.Invoke(a, cut);
        Replaced?.Invoke(b, cut);
        Log.Info($"Trains: coupled into {joined.Name}, {joined.Cars.Count} vehicles, at {mph:0.0} mph");
        Tell(mph > 4.0
            ? $"Coupled hard, {mph:0.0} mph · air hose not joined"
            : "Coupled · air hose not joined");
    }

    /// Uncoupled cuts drawn far enough from the rest of their train become cuts of their own.
    private void Splits()
    {
        for (var i = 0; i < _cuts.Count; i++)
        {
            var cut = _cuts[i];
            if (cut.Remote)
                continue;
            var body = cut.Train.Body;
            for (var link = 0; link < body.Coupled.Length; link++)
            {
                if (body.Coupled[link] || body.Gap(link) < SplitGap || cut.Train.Split(link) is not var (front, rear))
                    continue;
                Hook(front);
                Hook(rear);
                Log.Info($"Trains: {cut.Train.Name} drew apart into {front.Cars.Count} and {rear.Cars.Count} vehicles");
                var (f, r) = (NewCut(front, cut), NewCut(rear, cut));
                _cuts[i] = f;
                _cuts.Add(r);
                // The driven engine's half stays driven.
                if (ReferenceEquals(Controlled, cut))
                    Controlled = cut.Train.Body.Brakes.Control <= link ? f : r;
                Replaced?.Invoke(cut, ReferenceEquals(Controlled, r) ? r : f);
                break;
            }
        }
    }

    // --- Saving and putting back. ---

    /// Every cut as it stands now, its front placed on the map by toGeo (plan x, z to latitude, longitude).
    public List<PlacedCut> Snapshot(Func<double, double, (double Lat, double Lon)> toGeo)
    {
        var list = new List<PlacedCut>(_cuts.Count);
        foreach (var cut in _cuts.Where(c => !c.Remote))
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
                ReferenceEquals(cut, Controlled),
                [.. train.Body.Systems.Select(s => s?.Capacity is null ? null : new[] { s.Fuel, s.Oil, s.Water, s.Sand })]));
        }

        list.AddRange(_unplaced);
        return list;
    }

    /// Puts saved cuts back on the track, each front where it was (toPlan: latitude, longitude to plan x, z) and facing the
    /// same way. A cut whose track has gone, or that would stand on another, is left off. Returns how many went back.
    public int Restore(IReadOnlyList<PlacedCut> saved, Func<double, double, (double X, double Z)> toPlan)
    {
        if (Graph is not { } graph)
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
            var train = Train.Place(graph, new Consist { Name = s.Name, Cars = s.Cars }, head, out var why, PlacedAs.Running);
            if (train is null || Overlapping(train) is not null)
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
            var cut = Add(train, null, out _)!;
            if (s.Controlled && train.Cars.Any(c => c.Def.Kind == VehicleKind.Locomotive))
                Controlled = cut;
            put++;
        }

        if (lost > 0)
            Tell(lost == 1 ? "1 saved cut couldn't be restored (still saved)" : $"{lost} saved cuts couldn't be restored (still saved)");
        Log.Info($"Placed stock: {put} cuts put back{(lost > 0 ? $", {lost} left off" : "")}");
        return put;
    }
}
