using RailWerkz.Core.IO;

namespace RailWerkz.Core.Logging;

/// Logs to stdout and to a file in the user's data folder. The last lines are kept for the in-game log panels.
public static class Log
{
    public enum Level { Info, Warn, Error }

    public readonly record struct Line(DateTime Time, Level Level, string Text);

    private static readonly Lock Gate = new();
    private static readonly List<Line> Recent = [];
    private static StreamWriter? _file;

    public static void Init()
    {
        try
        {
            // (HB_LOGFILE: a log of its own, for copies of the game run side by side in tests.)
            var path = Environment.GetEnvironmentVariable("HB_LOGFILE") is { Length: > 0 } own ? own : Path.Combine(ContentPath.UserData, $"{ContentPath.AppName}.log");
            _file = new StreamWriter(path, false) { AutoFlush = true };
        }
        catch (IOException)
        {
            _file = null; // a second copy of the game has it open
        }
    }

    public static void Info(string text) => Write(Level.Info, text);
    public static void Warn(string text) => Write(Level.Warn, text);
    public static void Error(string text) => Write(Level.Error, text);

    public static void Write(Level level, string text)
    {
        var line = new Line(DateTime.Now, level, text);
        var tag = level switch { Level.Info => "--", Level.Warn => "!!", _ => "XX" };
        var formatted = $"{line.Time:HH:mm:ss} {tag} {text}";

        lock (Gate)
        {
            Recent.Add(line);

            if (Recent.Count > 200)
                Recent.RemoveAt(0);

            Console.WriteLine(formatted);
            _file?.WriteLine(formatted);
        }
    }

    /// A copy of the recent lines, oldest first.
    public static Line[] Snapshot()
    {
        lock (Gate)
            return [.. Recent];
    }

    public static void Shutdown()
    {
        _file?.Dispose();
        _file = null;
    }
}
