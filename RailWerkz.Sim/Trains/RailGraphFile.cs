using System.IO.Compression;
using Highball.World;

namespace Highball.Trains;

/// The server's copy of the track: the rail graph (edges with their exact plan and rail-top heights baked in, vertices,
/// switches) and the signals on it, Brotli-compressed. Everything a server needs to run trains on it without terrain.
/// A vertex's Node and a signal's Node and Toward are the world's stable track node ids.
public static class RailGraphFile
{
    private const uint Magic = 0x56534248; // "HBSV"
    private const int Version = 1;

    /// A signal as the server sees it: which track nodes it stands between, governing trains from Node towards Toward.
    public sealed record Signal(long Id, long? Node, long? Toward, double Lat, double Lon, double Heading);

    /// What's read back.
    public sealed record Contents(int Zone, string Name, RailGraph Graph, List<Signal> Signals);

    /// Writes the edges that keep says to (all if null), renumbered, with the vertices and switches they need.
    public static void Write(string path, int zone, string name, RailGraph graph, IEnumerable<Signal> all, Func<RailEdge, bool>? keep = null)
    {
        var edges = graph.Edges.Where(e => keep?.Invoke(e) ?? true).ToList();
        var edgeIndex = edges.Select((e, i) => (e.Index, i)).ToDictionary(p => p.Index, p => p.i);
        var vertices = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Order().ToList();
        var vertexIndex = vertices.Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
        bool Kept(EdgeEnd end) => edgeIndex.ContainsKey(end.Edge);
        EdgeEnd Map(EdgeEnd end) => new(edgeIndex[end.Edge], end.AtStart);
        var switches = graph.Switches.Where(s => Kept(s.Toe) && Kept(s.Normal) && Kept(s.Reverse)).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
        using var w = new BinaryWriter(brotli);
        w.Write(Magic);
        w.Write(Version);
        w.Write(zone);
        w.Write(name);

        w.Write(vertices.Count);
        foreach (var old in vertices)
        {
            var v = graph.Vertices[old];
            w.Write(v.Node);
            w.Write(v.X);
            w.Write(v.Z);
            var ends = v.Ends.Where(Kept).ToList();
            w.Write(ends.Count);
            foreach (var end in ends)
                WriteEnd(w, Map(end));
        }

        w.Write(edges.Count);
        foreach (var e in edges)
        {
            w.Write(vertexIndex[e.From]);
            w.Write(vertexIndex[e.To]);
            w.Write(e.Length);
            w.Write(e.Pieces.Length);
            foreach (var p in e.Pieces)
            {
                w.Write(p.X);
                w.Write(p.Z);
                w.Write(p.Heading);
                w.Write(p.Length);
                w.Write(p.Curvature);
                w.Write(p.Fitted);
            }

            w.Write(e.Height.Length);
            for (var i = 0; i < e.Height.Length; i++)
            {
                w.Write(e.HeightS[i]);
                w.Write(e.Height[i]);
            }
        }

        w.Write(switches.Count);
        foreach (var s in switches)
        {
            w.Write(s.Node);
            w.Write(vertexIndex[s.Vertex]);
            WriteEnd(w, Map(s.Toe));
            WriteEnd(w, Map(s.Normal));
            WriteEnd(w, Map(s.Reverse));
            w.Write(s.Side);
            w.Write(s.Number ?? -1);
        }

        var nodes = vertices.Select(v => graph.Vertices[v].Node).Where(n => n != 0).ToHashSet();
        var signals = all.Where(o => keep is null || (o.Node is { } n && nodes.Contains(n)) || (o.Toward is { } t && nodes.Contains(t))).ToList();
        w.Write(signals.Count);
        foreach (var s in signals)
        {
            w.Write(s.Id);
            w.Write(s.Node ?? 0L);
            w.Write(s.Toward ?? 0L);
            w.Write(s.Lat);
            w.Write(s.Lon);
            w.Write(s.Heading);
        }
    }

    public static Contents Read(string path)
    {
        using var file = File.OpenRead(path);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(brotli);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException($"{path} isn't server track data");
        if (r.ReadInt32() is var version && version != Version)
            throw new InvalidDataException($"{path} is version {version}, not {Version}");
        var zone = r.ReadInt32();
        var name = r.ReadString();

        var graph = new RailGraph();
        var vertexCount = r.ReadInt32();
        for (var i = 0; i < vertexCount; i++)
        {
            var v = new RailVertex { Index = i, Node = r.ReadInt64(), X = r.ReadDouble(), Z = r.ReadDouble() };
            for (var n = r.ReadInt32(); n > 0; n--)
                v.Ends.Add(ReadEnd(r));
            graph.Vertices.Add(v);
        }

        var edgeCount = r.ReadInt32();
        for (var i = 0; i < edgeCount; i++)
        {
            var (from, to, length) = (r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
            var pieces = new TrackPiece[r.ReadInt32()];
            for (var p = 0; p < pieces.Length; p++)
                pieces[p] = new TrackPiece(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadBoolean());
            var starts = new double[pieces.Length];
            for (var p = 1; p < pieces.Length; p++)
                starts[p] = starts[p - 1] + pieces[p - 1].Length;
            var count = r.ReadInt32();
            var heightS = new double[count];
            var height = new float[count];
            for (var h = 0; h < count; h++)
            {
                heightS[h] = r.ReadDouble();
                height[h] = r.ReadSingle();
            }

            var edge = new RailEdge
            {
                Index = i, Line = -1, FromPoint = 0, ToPoint = count - 1, From = from, To = to, Length = length,
                Pieces = pieces, PieceStart = starts, HeightS = heightS, Height = height,
                MinX = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxZ = double.MinValue,
            };
            // Bounds from the plan, sampled every few metres.
            for (var s = 0.0; ; s = Math.Min(length, s + 5.0))
            {
                var (x, z) = edge.Plan(s);
                (edge.MinX, edge.MaxX, edge.MinZ, edge.MaxZ) = (Math.Min(edge.MinX, x), Math.Max(edge.MaxX, x), Math.Min(edge.MinZ, z), Math.Max(edge.MaxZ, z));
                if (s >= length)
                    break;
            }

            graph.Edges.Add(edge);
        }

        for (var n = r.ReadInt32(); n > 0; n--)
        {
            var (node, vertex) = (r.ReadInt64(), r.ReadInt32());
            var (toe, normal, reverse) = (ReadEnd(r), ReadEnd(r), ReadEnd(r));
            var side = r.ReadInt32();
            var number = r.ReadInt32();
            var sw = new RailSwitch { Node = node, Vertex = vertex, Toe = toe, Normal = normal, Reverse = reverse, Side = side, Number = number < 0 ? null : number };
            graph.Switches.Add(sw);
            graph.Vertices[vertex].Switches.Add(sw);
        }

        foreach (var v in graph.Vertices)
            graph.Classify(v);

        var signals = new List<Signal>();
        for (var n = r.ReadInt32(); n > 0; n--)
        {
            var id = r.ReadInt64();
            var node = r.ReadInt64();
            var toward = r.ReadInt64();
            signals.Add(new Signal(id, node == 0 ? null : node, toward == 0 ? null : toward, r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
        }

        return new Contents(zone, name, graph, signals);
    }

    private static void WriteEnd(BinaryWriter w, EdgeEnd end)
    {
        w.Write(end.Edge);
        w.Write(end.AtStart);
    }

    private static EdgeEnd ReadEnd(BinaryReader r) => new(r.ReadInt32(), r.ReadBoolean());
}
