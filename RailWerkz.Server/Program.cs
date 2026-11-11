using RailWerkz.Server;

// A world server with no window: dotnet run --project RailWerkz.Server -- PATH.hbserver [PORT] [WORLD]
if (args.Length < 1)
{
    Console.WriteLine("RailWerkz.Server PATH.hbserver [PORT] [WORLD]");
    return 1;
}

var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : Protocol.DefaultPort;
var world = args.Length > 2 ? args[2] : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(args[0]))) ?? "World";
using var server = new WorldServer(args[0], world, port);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    server.Stop();
};
Console.WriteLine($"Serving {world}/{server.Region} on port {port}. Ctrl+C stops it.");
server.Run();
return 0;
