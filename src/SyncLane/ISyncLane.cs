namespace SyncLane;

/// <summary>What an application does with its jobs outside the workers: plan, enqueue, inspect and repair.</summary>
public interface ISyncLane
{
    /// <summary>
    /// Plans the jobs <paramref name="owner"/> should have and does not have yet. Safe to call any number of times.
    /// Call it when an account connects, so its jobs exist before the next planning round.
    /// </summary>
    /// <returns>How many jobs were created.</returns>
    Task<int> PlanAsync(string queue, JobOwner owner, CancellationToken cancellationToken = default);

    /// <summary>Enqueues one job by hand.</summary>
    /// <returns><c>false</c> when a job with the same <see cref="NewJob.DedupKey"/> already exists.</returns>
    Task<bool> EnqueueAsync(NewJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts failed, deferred and abandoned jobs back in line with a fresh set of attempts.
    /// Cursors are kept, so each job continues where it stopped.
    /// </summary>
    /// <returns>How many jobs were requeued.</returns>
    Task<int> RequeueAsync(string queue, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>Abandons the jobs of <paramref name="owner"/> that have not started, for example when the account disconnects.</summary>
    /// <returns>How many jobs were abandoned.</returns>
    Task<int> CancelAsync(string queue, string owner, string reason, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first.</summary>
    Task<IReadOnlyList<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reports how far the first load of <paramref name="owner"/> has come.</summary>
    Task<OwnerProgress> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default);
}
