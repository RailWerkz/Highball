using Highball.App;
using RailWerkz.Core.Logging;

try
{
    HighballApp.Run(args);
}
catch (Exception e)
{
    Log.Error($"Fatal: {e}");
    Log.Shutdown();
    throw;
}
