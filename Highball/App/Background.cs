using System.Diagnostics;

namespace Highball.App;

/// One kind of background work, for the Background Work window: how many are running now, how many have finished, and how
/// much time they've taken. Runners: how many a job stream has; nought for work that isn't a stream.
public sealed class Activity(string name, int runners)
{
    private int _running;
    private long _done;
    private long _busy;

    public string Name { get; } = name;
    public int Runners { get; } = runners;
    public int Running => Volatile.Read(ref _running);
    public long Done => Interlocked.Read(ref _done);
    public long BusyTicks => Interlocked.Read(ref _busy);

    /// What the window read last time, to work out the rate since, and that rate in cores.
    internal long LastBusy;
    internal float Cores;

    public Scope Begin()
    {
        Interlocked.Increment(ref _running);
        return new Scope(this, Stopwatch.GetTimestamp());
    }

    public readonly struct Scope(Activity activity, long start) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Add(ref activity._busy, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref activity._done);
            Interlocked.Decrement(ref activity._running);
        }
    }
}

/// Every activity seen so far. Wrap work in `using (Background.Begin("Name"))`; a JobStream counts its own.
public static class Background
{
    private static readonly List<Activity> Activities = [];

    public static Activity Find(string name, int runners = 0)
    {
        lock (Activities)
        {
            var found = Activities.Find(a => a.Name == name);
            if (found is null)
                Activities.Add(found = new Activity(name, runners));
            return found;
        }
    }

    public static Activity.Scope Begin(string name) => Find(name).Begin();

    public static Activity[] All()
    {
        lock (Activities)
            return [.. Activities];
    }
}
