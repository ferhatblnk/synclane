namespace SyncLane;

/// <summary>
/// What an application does with its jobs outside the workers: plan, enqueue, inspect and repair.
/// Every call returns a <see cref="SyncResult"/>: an expected failure, such as an undeclared queue or a duplicate job,
/// comes back in <see cref="SyncResult.Errors"/> with a stable code. A database that cannot be reached, a cancelled
/// call and a <c>null</c> argument still throw.
/// </summary>
public interface ISyncLane
{
    /// <summary>
    /// Plans the jobs <paramref name="owner"/> should have and does not have yet. Safe to call any number of times.
    /// Call it when an account connects, so its jobs exist before the next planning round.
    /// </summary>
    /// <returns>How many jobs were created. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<int>> PlanAsync(string queue, JobOwner owner, CancellationToken cancellationToken = default);

    /// <summary>Enqueues one job by hand.</summary>
    /// <returns>
    /// The id of the new job. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>,
    /// <see cref="SyncErrorCodes.JobTypeNotDeclared"/> or, when a job with the same
    /// <see cref="NewJob.DedupKey"/> exists, <see cref="SyncErrorCodes.JobAlreadyExists"/>.
    /// </returns>
    Task<SyncResult<long>> EnqueueAsync(NewJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts failed, deferred and abandoned jobs back in line with a fresh set of attempts.
    /// Cursors are kept, so each job continues where it stopped.
    /// </summary>
    /// <returns>How many jobs were requeued. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<int>> RequeueAsync(string queue, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>Abandons the jobs of <paramref name="owner"/> that have not started, for example when the account disconnects.</summary>
    /// <returns>How many jobs were abandoned. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<int>> CancelAsync(string queue, string owner, string reason, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first, one page at a time. Follow <see cref="SyncListResult{T}.NextCursor"/> for the next page.</summary>
    /// <returns>
    /// One page. Fails with <see cref="SyncErrorCodes.LimitOutOfRange"/> or <see cref="SyncErrorCodes.CursorInvalid"/>.
    /// </returns>
    Task<SyncListResult<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reports how far the first load of <paramref name="owner"/> has come.</summary>
    /// <returns>The progress. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<OwnerProgress>> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default);
}
