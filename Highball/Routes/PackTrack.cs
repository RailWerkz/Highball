using Highball.Trains;

namespace Highball.Routes;

/// The rail graph a pack's game runs on, as its server track has it: a region's pack keeps the edges whose middle is on its tiles,
/// renumbered, as the export wrote its REGION.hbserver (WorldExport.RegionKeeps); World.hbpack keeps them all. What the game and
/// the server compare when a player joins (RailGraph.Checksum).
public static class PackTrack
{
    public static int Checksum(RailGraph graph, PackSource pack)
    {
        if (pack.Header.Region == "World" || pack.Route.Grid is not { } grid)
            return graph.Checksum();
        // (Through the server track's own file, so the edges are kept and renumbered exactly as the server's were.)
        var built = pack.Built();
        var path = Path.Combine(Path.GetTempPath(), $"Highball-{Environment.ProcessId}-{Guid.NewGuid():N}.hbserver");
        try
        {
            RailGraphFile.Write(path, pack.Header.Zone, pack.Header.Region, graph, [], WorldExport.RegionKeeps(grid, built.Contains));
            return RailGraphFile.Read(path).Graph.Checksum();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
