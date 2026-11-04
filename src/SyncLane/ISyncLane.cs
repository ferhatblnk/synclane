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

    /// <summary>
    /// Records ranges of history that another system already loaded, so the planner does not fetch them again. Each
    /// range becomes a completed initial job, visible in <see cref="GetJobsAsync"/> and counted by
    /// <see cref="GetProgressAsync"/>. History older than a range is still loaded back to the horizon, and what came
    /// after it by the next recurring window. Call it before the planner runs for these owners. Safe to call again.
    /// </summary>
    /// <returns>
    /// How many ranges were new. Nothing is marked when any range fails, with <see cref="SyncErrorCodes.QueueNotDeclared"/>,
    /// <see cref="SyncErrorCodes.JobTypeNotDeclared"/>, <see cref="SyncErrorCodes.JobTypeNotWindowed"/> or
    /// <see cref="SyncErrorCodes.LoadedRangeInvalid"/>.
    /// </returns>
    Task<SyncResult<int>> MarkLoadedAsync(string queue, IReadOnlyList<LoadedRange> ranges, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every job of <paramref name="owner"/> on the queue and what the planner knows about it, for an account
    /// that is gone for good. If the owner comes back, its history is loaded again from the start. Stop returning it from
    /// your <see cref="IOwnerSource"/> first: otherwise the next planning round creates its jobs again. A job running at
    /// that moment stops at its next page, and that page may still reach your code, so delete the owner's data after this.
    /// </summary>
    /// <returns>How many jobs were deleted. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<int>> ForgetOwnerAsync(string queue, string owner, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first, one page at a time. Follow <see cref="SyncListResult{T}.NextCursor"/> for the next page.</summary>
    /// <returns>
    /// One page. Fails with <see cref="SyncErrorCodes.LimitOutOfRange"/> or <see cref="SyncErrorCodes.CursorInvalid"/>.
    /// </returns>
    Task<SyncListResult<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reports how far the first load of <paramref name="owner"/> has come.</summary>
    /// <returns>The progress. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<OwnerProgress>> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default);
}
