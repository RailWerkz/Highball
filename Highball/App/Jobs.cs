using Brutal.Concurrency.Jobs;
using RailWerkz.Core.Logging;

namespace Highball.App;

/// A stream of background work on BRUTAL's job system (Brutal.Concurrency). A JobScheduler runs a batch of jobs spread
/// over its runners (thread pool work items) and takes no more until the batch is done, so the stream goes through in
/// batches: whenever the last one has finished, its owner hands over the jobs it most wants done now. Work that's no
/// longer wanted by then is never started, and the nearest or most urgent always goes first.
public sealed class JobStream : IDisposable
{
    private readonly JobScheduler _scheduler;

    /// How many jobs run at once.
    public int Runners { get; }

    /// A batch is still running.
    public bool Busy => _scheduler.IsExecuting;

    /// A batch's size: a few jobs a runner, so the scheduler can even out their costs between them.
    public int BatchSize => Runners * 3;

    /// The cores background work can have: all but two, which the main thread and the driver keep.
    public static int Cores => Math.Max(2, Environment.ProcessorCount - 2);

    /// share: the part of Cores this stream's runners take (at least one runner).
    public JobStream(string name, double share, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        Runners = Math.Max(1, (int)Math.Round(Cores * share));
        _scheduler = new JobScheduler((uint)Runners, name, priority);
    }

    /// Starts a batch if the last one has finished: each job with its rough cost, which the scheduler balances the
    /// runners by. False, with nothing taken, while a batch is still running.
    public bool TryRun(IEnumerable<(IJob Job, int Cost)> jobs)
    {
        if (Busy)
            return false;

        var any = false;
        foreach (var (job, cost) in jobs)
        {
            _scheduler.Queue(job, Math.Max(1, cost));
            any = true;
        }

        if (any)
            _scheduler.ExecuteJobs();
        return true;
    }

    /// Drops what hasn't started and waits for what has.
    public void Dispose()
    {
        _scheduler.AbortJobs(false);
        _scheduler.Wait();
    }
}

/// A job that runs a delegate, logging rather than losing anything it throws.
public sealed class Job(string name, Action run) : IJob
{
    public void Run()
    {
        try
        {
            run();
        }
        catch (Exception e)
        {
            Log.Error($"Job {name}: {e}");
        }
    }
}

/// Work split across the cores within a frame and waited for, on BRUTAL's job system: a scheduler with a runner for
/// each core background work can have. The main thread waits for the slices, so they may read (not change) anything
/// it has ready, and each writes only what's its own.
public static class FrameJobs
{
    private static JobScheduler? _scheduler;

    private static JobScheduler Scheduler => _scheduler ??= new JobScheduler((uint)JobStream.Cores, "Frame", ThreadPriority.Normal);

    /// How many slices count items make, at least minimum to a slice: two a core at most, one if there's little.
    public static int Slices(int count, int minimum) => Math.Clamp(count / Math.Max(1, minimum), 1, JobStream.Cores * 2);

    /// Runs body(slice, from, to) over [0, count) in that many slices and waits for them all; one slice runs here.
    public static void For(int count, int slices, Action<int, int, int> body)
    {
        if (slices <= 1 || count <= 1)
        {
            body(0, 0, count);
            return;
        }

        var scheduler = Scheduler;
        var size = (count + slices - 1) / slices;
        for (var slice = 0; slice < slices; slice++)
        {
            var (from, to, index) = (slice * size, Math.Min(count, (slice + 1) * size), slice);
            if (from < to)
                scheduler.Queue(new Job("frame slice", () => body(index, from, to)), to - from);
        }

        scheduler.ExecuteJobs();
        scheduler.Wait();
    }
}
