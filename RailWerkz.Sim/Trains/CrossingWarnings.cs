namespace Highball.Trains;

/// A grade crossing as its warning sees it: the middle of its road over the tracks, the way the road runs (a unit vector),
/// half the road's width, and how far out along each track its approach circuits reach, metres.
public readonly record struct CrossingSite(long Id, double X, double Z, double Dx, double Dz, double Half, double Approach);

/// Works every grade crossing's warning (its flashers) from where the trains are, as a crossing's track circuits do:
///
/// Each track the road crosses has an island circuit over the road (and a little either side) and an approach circuit out
/// along the track each way to the crossing's Approach, down every branch of the switches in it. While anything of a train is
/// on the island, the crossing works. In an approach circuit a train's motion is watched, as a crossing predictor does: one
/// coming towards the road starts the warning WarningTime seconds before it'd reach the island at the speed it's doing, so a
/// fast train and a slow one give the same warning. One going away (the train that's just crossed, leaving) is let be. One that
/// stops short, or creeps, keeps the warning on for Hold seconds and then lets it go, until it comes on again.
///
/// Built from the rail graph and the crossings (Build) whenever either changes; stepped with the trains (Update).
public sealed class CrossingWarnings
{
    /// Seconds of warning before a train reaches the island.
    public const double WarningTime = 25.0;

    /// How far past the road's edges (across the road, so further along a track crossing it on the skew) the island runs.
    public const double IslandMargin = 3.0;

    /// Slower than this (m/s) a train's thought standing.
    public const double Creep = 0.3;

    /// How long the warning stays on for a train that stopped (or slowed right down) in an approach circuit.
    public const double Hold = 15.0;

    /// How far along its road from its middle a crossing looks for the tracks it crosses.
    private const double Search = 30.0;

    /// A stretch of one edge in a track's circuits: from Lo to Hi along it, and its distance from the crossing at s, signed
    /// (negative one way along the track, positive the other): D = Origin + Slope * s, Slope +1 or -1.
    private readonly record struct Span(int Site, int Track, int Edge, double Lo, double Hi, double Origin, int Slope);

    private sealed class Site(CrossingSite crossing)
    {
        public CrossingSite Crossing { get; } = crossing;
        /// Each track's island, half its length along the track.
        public List<double> Islands { get; } = [];
        public bool Active;
        public double Holding;
    }

    private readonly List<Site> _sites = [];
    private readonly Dictionary<int, List<Span>> _byEdge = [];
    private readonly Dictionary<long, Site> _byId = [];

    public RailGraph? Graph { get; private set; }

    /// The crossings it was built for.
    public IReadOnlyList<CrossingSite> Sites => _sites.Select(s => s.Crossing).ToList();

    /// Whether a crossing's warning is working now.
    public bool IsActive(long id) => _byId.TryGetValue(id, out var site) && site.Active;

    /// Every track circuit of every crossing laid out again on a graph, keeping the warnings that were on.
    public void Build(RailGraph graph, IEnumerable<CrossingSite> crossings)
    {
        var was = _byId.Where(p => p.Value.Active).ToDictionary(p => p.Key, p => p.Value.Holding);
        Graph = graph;
        _sites.Clear();
        _byEdge.Clear();
        _byId.Clear();
        foreach (var crossing in crossings)
        {
            var site = new Site(crossing);
            var index = _sites.Count;
            _sites.Add(site);
            _byId[crossing.Id] = site;
            if (was.TryGetValue(crossing.Id, out var holding))
                (site.Active, site.Holding) = (true, holding);

            foreach (var (edge, s, sine) in Crossed(graph, crossing))
            {
                var track = site.Islands.Count;
                site.Islands.Add((crossing.Half + IslandMargin) / Math.Max(sine, 0.25));
                var visited = new HashSet<(int, int)>();
                Walk(graph, index, track, edge, s, +1, 0.0, +1, crossing.Approach, visited);
                Walk(graph, index, track, edge, s, -1, 0.0, -1, crossing.Approach, visited);
            }
        }
    }

    /// Where the crossing's road crosses the graph's edges, near its middle: the edge, how far along it, and the sine of the
    /// angle between the road and the track there. A place where two edges meet on the road is only had once.
    private static List<(int Edge, double S, double Sine)> Crossed(RailGraph graph, CrossingSite c)
    {
        var found = new List<(int Edge, double S, double Sine, double X, double Z)>();
        var (nx, nz) = (-c.Dz, c.Dx);
        double Across(RailEdge e, double s)
        {
            var (x, z) = e.Plan(s);
            return (x - c.X) * nx + (z - c.Z) * nz;
        }

        foreach (var edge in graph.Edges)
        {
            if (c.X < edge.MinX - Search || c.X > edge.MaxX + Search || c.Z < edge.MinZ - Search || c.Z > edge.MaxZ + Search)
                continue;
            var steps = Math.Max(1, (int)Math.Ceiling(edge.Length));
            var (s0, a0) = (0.0, Across(edge, 0.0));
            for (var i = 1; i <= steps; i++)
            {
                var s1 = edge.Length * i / steps;
                var a1 = Across(edge, s1);
                if ((a0 <= 0.0) != (a1 <= 0.0))
                {
                    var (lo, hi, alo) = (s0, s1, a0);
                    for (var k = 0; k < 40; k++)
                    {
                        var mid = (lo + hi) * 0.5;
                        var am = Across(edge, mid);
                        if ((am <= 0.0) == (alo <= 0.0))
                            (lo, alo) = (mid, am);
                        else
                            hi = mid;
                    }

                    var s = (lo + hi) * 0.5;
                    var (x, z) = edge.Plan(s);
                    var along = (x - c.X) * c.Dx + (z - c.Z) * c.Dz;
                    var heading = edge.Heading(s);
                    var sine = Math.Abs(Math.Cos(heading) * c.Dz - Math.Sin(heading) * c.Dx);
                    if (Math.Abs(along) <= Search && sine > 0.2 && found.All(f => (f.X - x) * (f.X - x) + (f.Z - z) * (f.Z - z) > 0.25))
                        found.Add((edge.Index, s, sine, x, z));
                }

                (s0, a0) = (s1, a1);
            }
        }

        return [.. found.Select(f => (f.Edge, f.S, f.Sine))];
    }

    /// Lays a track's circuit along an edge from s, going dir along it, reached `from` metres out from the crossing on side
    /// (+1 or -1), on for `left` metres more: on through every way out of each vertex it comes to (both routes of a switch met
    /// point first), as a track circuit takes in every branch up to its joints.
    private void Walk(RailGraph graph, int site, int track, int edge, double s, int dir, double from, int side, double left, HashSet<(int, int)> visited)
    {
        for (var depth = 0; depth < 64 && left > 0.0; depth++)
        {
            if (!visited.Add((edge, side)))
                return;
            var length = graph.Edges[edge].Length;
            var end = dir > 0 ? Math.Min(length, s + left) : Math.Max(0.0, s - left);
            // D = side * (from + |s' - s|) along the way it's going.
            var span = dir > 0
                ? new Span(site, track, edge, s, end, side * (from - s), side)
                : new Span(site, track, edge, end, s, side * (from + s), -side);
            if (!_byEdge.TryGetValue(edge, out var list))
                _byEdge[edge] = list = [];
            list.Add(span);

            var went = Math.Abs(end - s);
            (from, left) = (from + went, left - went);
            if (left <= 0.0)
                return;

            var onward = Onward(graph, new EdgeEnd(edge, dir < 0));
            if (onward.Count == 0)
                return;
            // Every branch but the first gets a walk of its own; the first carries on here.
            for (var i = 1; i < onward.Count; i++)
                Walk(graph, site, track, onward[i].Edge, onward[i].AtStart ? 0.0 : graph.Edges[onward[i].Edge].Length, onward[i].Departing, from, side, left, visited);
            (edge, dir) = (onward[0].Edge, onward[0].Departing);
            s = onward[0].AtStart ? 0.0 : graph.Edges[edge].Length;
        }
    }

    /// Every way on from a vertex for something arriving along one end: both routes of a switch met point first, its toe from
    /// either route, and the one way on through anything else.
    private static List<EdgeEnd> Onward(RailGraph graph, EdgeEnd arrived)
    {
        var vertex = graph.Vertices[graph.Edges[arrived.Edge].VertexAt(arrived.AtStart)];
        var ways = new List<EdgeEnd>();
        if (vertex.Kind == RailJunction.Switch)
        {
            foreach (var sw in vertex.Switches)
            {
                if (arrived == sw.Toe)
                    ways.AddRange([sw.Normal, sw.Reverse]);
                else if (arrived == sw.Normal || arrived == sw.Reverse)
                    ways.Add(sw.Toe);
            }
        }
        else if (vertex.Onward.TryGetValue(arrived, out var on))
            ways.Add(on);

        return [.. ways.Distinct()];
    }

    /// A place on a track's circuits: how far from the crossing (signed, as Span), and which way the place faces along that
    /// (+1: facing away from the crossing on the + side, i.e. D grows going the way it faces).
    private (double D, int Grows)? Locate(int site, int track, TrackPosition at)
    {
        if (!_byEdge.TryGetValue(at.Edge, out var spans))
            return null;
        (double D, int Grows)? best = null;
        foreach (var span in spans)
        {
            if (span.Site != site || span.Track != track || at.S < span.Lo - 1e-6 || at.S > span.Hi + 1e-6)
                continue;
            var d = span.Origin + span.Slope * at.S;
            if (best is null || Math.Abs(d) < Math.Abs(best.Value.D))
                best = (d, span.Slope * at.Direction);
        }

        return best;
    }

    /// Steps every crossing's warning on by dt seconds with the trains where they are now. Returns the crossings whose warning
    /// came on or went off.
    public List<long> Update(double dt, IEnumerable<Train> trains)
    {
        var changed = new List<long>();
        if (_sites.Count == 0)
            return changed;
        var list = trains.Where(t => ReferenceEquals(t.Graph, Graph)).ToList();
        for (var index = 0; index < _sites.Count; index++)
        {
            var site = _sites[index];
            var (want, present) = (false, false);
            for (var track = 0; track < site.Islands.Count && !want; track++)
            {
                var island = site.Islands[track];
                foreach (var train in list)
                {
                    // Where its cars' ends lie on this track's circuits, if they do.
                    var (lo, hi) = (double.MaxValue, double.MinValue);
                    (double D, int Grows) nearest = (double.MaxValue, 0);
                    foreach (var car in train.Cars)
                        foreach (var end in (TrackPosition[])[car.Front, car.Rear])
                            if (Locate(index, track, end) is { } at)
                            {
                                (lo, hi) = (Math.Min(lo, at.D), Math.Max(hi, at.D));
                                if (Math.Abs(at.D) < Math.Abs(nearest.D))
                                    nearest = at;
                            }

                    if (lo > hi)
                        continue;
                    // On the island: any end on it, or the train reaching from one side of it to the other.
                    if ((lo <= island && hi >= -island))
                    {
                        want = true;
                        break;
                    }

                    // In an approach: coming towards the road, at what speed, and how soon it'd reach the island.
                    var toward = -Math.Sign(nearest.D) * nearest.Grows * train.Speed;
                    if (toward > -Creep)
                        present = true;
                    if (toward > Creep && (Math.Abs(nearest.D) - island) / toward <= WarningTime)
                    {
                        want = true;
                        break;
                    }
                }
            }

            var before = site.Active;
            if (want)
                (site.Active, site.Holding) = (true, Hold);
            else if (site.Active)
            {
                // Nothing coming: off at once (the train that's crossed leaving). Something stood in an approach: held a while.
                site.Holding -= dt;
                if (!present || site.Holding <= 0.0)
                    site.Active = false;
            }

            if (site.Active != before)
                changed.Add(site.Crossing.Id);
        }

        return changed;
    }
}
