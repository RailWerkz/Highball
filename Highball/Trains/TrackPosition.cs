namespace Highball.Trains;

/// A place on the track and a way along it: the edge, metres along it from its From vertex, and the way it faces (+1 up
/// s, -1 down). Doubles throughout: s is exact to well under a micrometre on any edge, wherever it is on the route.
/// s is measured on the plan, as the track was laid out; along the rail it's longer by sqrt(1 + grade^2), 0.03% on a 2.5%
/// grade, so whatever moves a train by its speed along the rail divides by that first.
public readonly record struct TrackPosition(int Edge, double S, int Direction)
{
    /// The same place, facing the other way.
    public TrackPosition Reversed => this with { Direction = -Direction };
}

/// A train's memory of the switches under it: which way it went through each, by node. Moves read it at switches met
/// point first and write every switch they pass; Forget drops what the last walk didn't pass, so only the switches the
/// train still stands over are kept.
public sealed class SwitchMemory
{
    private readonly Dictionary<long, bool> _ways = [];
    private readonly HashSet<long> _passed = [];

    /// Whether it went through the switch to its reverse route, if it's remembered.
    public bool? Way(long node) => _ways.TryGetValue(node, out var way) ? way : null;

    public void Took(long node, bool reversed)
    {
        _ways[node] = reversed;
        _passed.Add(node);
    }

    public int Count => _ways.Count;

    /// A copy to walk with, for something that mustn't change the train's own (drawing it).
    public SwitchMemory Copy()
    {
        var copy = new SwitchMemory();
        foreach (var (node, way) in _ways)
            copy._ways[node] = way;
        return copy;
    }

    /// Starts noting which switches are passed.
    public void Begin() => _passed.Clear();

    /// Keeps only the switches passed since Begin.
    public void Forget()
    {
        foreach (var node in _ways.Keys.Where(n => !_passed.Contains(n)).ToList())
            _ways.Remove(node);
    }

    public void Clear()
    {
        _ways.Clear();
        _passed.Clear();
    }
}

/// Why a move along the track stopped short, if it did.
public enum TrackStop
{
    None,
    /// The track ran out: a buffer stop, the edge of what's built, or a junction with no way on.
    EndOfTrack,
}

/// What happened on a move: how far it got, why it stopped if it did, and the switches it ran through set against it.
public readonly record struct TrackMove(double Moved, TrackStop Stop, IReadOnlyList<long> Trailed)
{
    public static readonly IReadOnlyList<long> None = [];
}

public sealed partial class RailGraph
{
    /// The most vertices one move may pass: guards against a loop of zero-length edges.
    private const int MaxVertices = 10000;

    /// Moves a place along the track by distance (metres; negative backs it up, keeping its facing), through each vertex
    /// the way a train would go: switches as they're set now. Stops at the end of the track.
    ///
    /// route, if given, is a train's memory of the switches under it: the way each was gone through, by node. A switch met
    /// point first goes the way route says if it has it (else as it's set now), and every switch passed is written back
    /// with the way it was taken, so the cars behind follow the front through it even if it's thrown under them.
    public TrackMove Move(ref TrackPosition position, double distance, SwitchMemory? route = null)
    {
        if (distance < 0.0)
        {
            var back = position.Reversed;
            var moved = Move(ref back, -distance, route);
            position = back.Reversed;
            return moved with { Moved = -moved.Moved };
        }

        List<long>? trailed = null;
        var left = distance;
        var (edge, s, dir) = position;
        for (var passed = 0; passed < MaxVertices; passed++)
        {
            var length = Edges[edge].Length;
            var room = dir > 0 ? length - s : s;
            if (left <= room)
            {
                s += dir * left;
                position = new TrackPosition(edge, Math.Clamp(s, 0.0, length), dir);
                return new TrackMove(distance, TrackStop.None, trailed ?? TrackMove.None);
            }

            left -= room;
            var arrived = new EdgeEnd(edge, dir < 0);
            var (next, against) = Onward(arrived, route);
            if (against is { } node)
                (trailed ??= []).Add(node);
            if (next is not { } leave)
            {
                position = new TrackPosition(edge, dir > 0 ? length : 0.0, dir);
                return new TrackMove(distance - left, TrackStop.EndOfTrack, trailed ?? TrackMove.None);
            }

            (edge, dir) = (leave.Edge, leave.Departing);
            s = leave.AtStart ? 0.0 : Edges[edge].Length;
        }

        position = new TrackPosition(edge, s, dir);
        return new TrackMove(distance - left, TrackStop.EndOfTrack, trailed ?? TrackMove.None);
    }

    /// Where a train arriving at a vertex along one end runs on to, and the switch it trails through set against it, if
    /// any (against how it's set now, whatever route remembers). A facing switch decides the way; a trailing one sends it
    /// to its toe. route as for Move.
    public (EdgeEnd? Next, long? Trailed) Onward(EdgeEnd arrived, SwitchMemory? route = null)
    {
        var vertex = Vertices[Edges[arrived.Edge].VertexAt(arrived.AtStart)];
        if (vertex.Kind != RailJunction.Switch)
            return (vertex.Onward.TryGetValue(arrived, out var on) ? on : null, null);

        // Two switches can share a node (back to back): the one met point first decides, whatever order they're in.
        EdgeEnd? facing = null, trailing = null;
        long? trailed = null;
        foreach (var sw in vertex.Switches)
        {
            var set = Reversed(sw.Node);
            if (arrived == sw.Toe)
            {
                var reversed = route?.Way(sw.Node) ?? set;
                facing = reversed ? sw.Reverse : sw.Normal;
                route?.Took(sw.Node, reversed);
            }
            else if (arrived == sw.Normal || arrived == sw.Reverse)
            {
                trailing ??= sw.Toe;
                if ((arrived == sw.Normal) == set)
                    trailed = sw.Node;
                route?.Took(sw.Node, arrived == sw.Reverse);
            }
        }

        return (facing ?? trailing, trailed);
    }

    /// The world position of a place on the track: rail tops' centre, x east, y up, z south.
    public (double X, double Y, double Z) PositionOf(TrackPosition at) => Edges[at.Edge].Position(at.S);

    /// The way a place faces, as a unit vector on the plan.
    public (double X, double Z) FacingOf(TrackPosition at)
    {
        var h = Edges[at.Edge].Heading(at.S) + (at.Direction < 0 ? Math.PI : 0.0);
        return (Math.Cos(h), Math.Sin(h));
    }

    /// Rise over run the way it faces.
    public double GradeOf(TrackPosition at) => Edges[at.Edge].Grade(at.S) * at.Direction;

    /// Curvature the way it faces (positive turns its heading up), 1 / radius.
    public double CurvatureOf(TrackPosition at) => Edges[at.Edge].Curvature(at.S) * at.Direction;

    /// The place on the track nearest (x, z) within reach metres, facing up its edge; null if there's no track that close.
    public TrackPosition? Nearest(double x, double z, double reach = 10.0)
    {
        TrackPosition? best = null;
        var bestDistance = reach * reach;
        foreach (var edge in Edges)
        {
            if (x < edge.MinX - reach || x > edge.MaxX + reach || z < edge.MinZ - reach || z > edge.MaxZ + reach)
                continue;

            // Coarse steps along it, then a closer look either side of the best.
            var step = Math.Min(2.0, edge.Length);
            var steps = Math.Max(1, (int)Math.Ceiling(edge.Length / step));
            var near = 0.0;
            var nearDistance = double.MaxValue;
            for (var i = 0; i <= steps; i++)
            {
                var s = edge.Length * i / steps;
                var (px, pz) = edge.Plan(s);
                var d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d < nearDistance)
                    (near, nearDistance) = (s, d);
            }

            var (lo, hi) = (Math.Max(0.0, near - step), Math.Min(edge.Length, near + step));
            for (var k = 0; k < 60 && hi - lo > 1e-9; k++)
            {
                var (m1, m2) = (lo + (hi - lo) / 3.0, hi - (hi - lo) / 3.0);
                var (x1, z1) = edge.Plan(m1);
                var (x2, z2) = edge.Plan(m2);
                if ((x1 - x) * (x1 - x) + (z1 - z) * (z1 - z) < (x2 - x) * (x2 - x) + (z2 - z) * (z2 - z))
                    hi = m2;
                else
                    lo = m1;
            }

            var at = (lo + hi) * 0.5;
            var (fx, fz) = edge.Plan(at);
            var distance = (fx - x) * (fx - x) + (fz - z) * (fz - z);
            if (distance < bestDistance)
                (best, bestDistance) = (new TrackPosition(edge.Index, at, 1), distance);
        }

        return best;
    }
}
