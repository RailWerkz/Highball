using Highball.Routes;

namespace Highball.World;

/// Heights pinned by hand (RouteNetwork.HeightsTag; the editor's Level and Grade). A rail line runs at a constant grade between
/// two of its pins (a span), whatever the ground does, and eases from a pin back to its own fitted profile past its last one. A
/// line meeting another inside a span takes the span's height there as a pin of its own, so they still meet. The fitting
/// passes work around the pins: each stage of FinishProfiles puts them back, the junctions hold them, and sidings don't follow
/// their main line where they're pinned. The terrain's cut and filled to the track as ever, so a level yard gets a flat pad.
public sealed partial class NetworkLines
{
    /// Each pinned rail line's pins, by point along it, in order; and which of its points they fix (spans and pins).
    private Dictionary<NetworkLine, (int Point, float Y)[]> _linePins = [];
    private Dictionary<NetworkLine, bool[]> _pinnedPoints = [];

    /// Every node's pinned height, and those taken on from spans (see above); for the junctions to hold.
    private Dictionary<long, float> _nodePins = [];

    /// The heights pinned at nodes, as the editor shows them.
    public IReadOnlyDictionary<long, float> NodePins => _nodePins;

    private Dictionary<long, (NetworkLine Line, int Point)>? _railAt;

    /// The rails' height at a node as built (their formation), and the ground's there before the track shaped it; null if no
    /// rail line runs through it here.
    public (float Track, float Ground)? HeightsAt(long node)
    {
        if (_railAt is null)
        {
            var at = new Dictionary<long, (NetworkLine, int)>();
            foreach (var line in _lines.Where(l => l.Rail && !l.Mark))
                foreach (var (n, p) in line.Nodes)
                    at.TryAdd(n, (line, p));
            _railAt = at;
        }

        // (As pinned: before a lesser bed than the main line's let the track down onto the ground, LetDownBeds.)
        if (!_railAt.TryGetValue(node, out var found))
            return null;
        var (on, point) = found;
        var sink = on.Sink is { } s && s.Length == on.Count ? s[point] : 0f;
        return (on.Y[point] + sink, on.Ground[point]);
    }

    private void FindPins()
    {
        _linePins = [];
        _pinnedPoints = [];
        _nodePins = [];
        if (Ways is null)
            return;

        var pins = new Dictionary<long, float>();
        foreach (var way in Ways.Values)
            if (way.Rail && way.Tags.ContainsKey(RouteNetwork.HeightsTag))
                foreach (var (node, metres) in RouteNetwork.PinsOf(way))
                    pins.TryAdd(node, metres);
        if (pins.Count == 0)
            return;

        var rails = _lines.Where(l => l.Rail && !l.Mark && !l.Disused && l.Count >= 2).ToList();
        // A span's heights pass to the lines meeting it inside, which may make spans of their own: a few rounds.
        for (var round = 0; round < 4; round++)
        {
            var added = false;
            foreach (var line in rails)
            {
                var mine = Pins(line, pins);
                if (mine.Length < 2)
                    continue;
                foreach (var (node, point) in line.Nodes)
                {
                    if (pins.ContainsKey(node) || point <= mine[0].Point || point >= mine[^1].Point)
                        continue;
                    pins[node] = Spanned(line, mine, point);
                    added = true;
                }
            }

            if (!added)
                break;
        }

        _nodePins = pins;
        foreach (var line in rails)
        {
            var mine = Pins(line, pins);
            if (mine.Length == 0)
                continue;
            _linePins[line] = mine;
            var fixedAt = new bool[line.Count];
            for (var p = mine[0].Point; p <= mine[^1].Point; p++)
                fixedAt[p] = true;
            _pinnedPoints[line] = fixedAt;
        }

        RailWerkz.Core.Logging.Log.Info($"Profiles: {pins.Count} heights pinned on {_linePins.Count} lines");
    }

    private static (int Point, float Y)[] Pins(NetworkLine line, Dictionary<long, float> pins) =>
        line.Nodes.Where(n => pins.ContainsKey(n.Node)).Select(n => (n.Point, pins[n.Node])).DistinctBy(p => p.Point).OrderBy(p => p.Point).ToArray();

    /// A span's height at a point inside it: straight between the pins either side, by distance along the line.
    private static float Spanned(NetworkLine line, (int Point, float Y)[] pins, int point)
    {
        var i = 0;
        while (i + 2 < pins.Length && pins[i + 1].Point <= point)
            i++;
        var (a, b) = (pins[i], pins[i + 1]);
        var span = line.S[b.Point] - line.S[a.Point];
        return span > 1e-3f ? a.Y + (b.Y - a.Y) * (line.S[point] - line.S[a.Point]) / span : a.Y;
    }

    /// Whether a point of a line is fixed by its pins (in a span, or at a pin).
    private bool Pinned(NetworkLine line, int point) => _pinnedPoints.TryGetValue(line, out var at) && at[point];

    /// A line's pins put back: straight between them, and eased from the first and last back to the line's own heights, over as
    /// far as the line's grade needs (ease: false, only a short way, for the last word after the junctions have agreed).
    private void ApplyPins(NetworkLine line, bool ease = true)
    {
        if (!_linePins.TryGetValue(line, out var pins))
            return;
        var (y, s) = (line.Y, line.S);
        var (first, last) = (pins[0], pins[^1]);
        var grade = SteepestGrade(line);

        float Reach(float delta) => ease ? MathF.Max(30f, 1.5f * MathF.Abs(delta) / grade) : 15f;

        var before = first.Y - y[first.Point];
        var reach = Reach(before);
        for (var p = first.Point - 1; p >= 0 && s[first.Point] - s[p] < reach; p--)
            y[p] += before * Ease(1f - (s[first.Point] - s[p]) / reach);
        var after = last.Y - y[last.Point];
        reach = Reach(after);
        for (var p = last.Point + 1; p < line.Count && s[p] - s[last.Point] < reach; p++)
            y[p] += after * Ease(1f - (s[p] - s[last.Point]) / reach);

        y[first.Point] = first.Y;
        for (var i = 0; i + 1 < pins.Length; i++)
        {
            var (a, b) = (pins[i], pins[i + 1]);
            var span = MathF.Max(s[b.Point] - s[a.Point], 1e-3f);
            for (var p = a.Point + 1; p <= b.Point; p++)
                y[p] = a.Y + (b.Y - a.Y) * (s[p] - s[a.Point]) / span;
        }
    }
}
