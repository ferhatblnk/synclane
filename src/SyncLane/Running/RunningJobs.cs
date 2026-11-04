using System.Collections.Concurrent;

namespace SyncLane.Running;

/// <summary>
/// The jobs this process is running, so a pause can cut them at once: the pause's announcement cancels the jobs it
/// covers, and each is handed back without using an attempt.
/// </summary>
internal sealed class RunningJobs
{
    private readonly ConcurrentDictionary<long, (string Queue, string Owner, CancellationTokenSource Pause)> _jobs = new();

    /// <summary>Tracks <paramref name="job"/> until the returned handle is disposed. Its token is cancelled by a pause.</summary>
    public Tracked Track(Job job)
    {
        var pause = new CancellationTokenSource();
        _jobs[job.Id] = (job.Queue, job.Owner, pause);
        return new Tracked(this, job.Id, pause);
    }

    /// <summary>Cuts every running job of the queue, or of one owner on it (<paramref name="owner"/> empty means all). Returns how many.</summary>
    public int Pause(string queue, string owner)
    {
        var cut = 0;
        foreach (var (_, running) in _jobs)
        {
            if (running.Queue != queue || (owner.Length > 0 && running.Owner != owner))
                continue;

            if (TryCancel(running.Pause))
                cut++;
        }

        return cut;
    }

    private static bool TryCancel(CancellationTokenSource pause)
    {
        try
        {
            pause.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The job ended between being found and being cut: nothing left to stop.
            return false;
        }
    }

    /// <summary>One running job. Disposing it stops tracking.</summary>
    public sealed class Tracked(RunningJobs owner, long id, CancellationTokenSource pause) : IDisposable
    {
        public CancellationToken Paused => pause.Token;

        public bool IsPaused => pause.IsCancellationRequested;

        /// <summary>Cuts the job, for a pause the worker learned of at a checkpoint or heartbeat.</summary>
        public void Pause() => TryCancel(pause);

        public void Dispose()
        {
            owner._jobs.TryRemove(id, out _);
            pause.Dispose();
        }
    }
}
