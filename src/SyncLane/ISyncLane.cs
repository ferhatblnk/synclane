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
    /// Enqueues one job by hand, and when a job with the same owner, type and <see cref="NewJob.DedupKey"/> is there
    /// already, lets it take this request's place instead of refusing it: the latest request for a key wins. That job
    /// keeps its id and its place in an ordered lane, takes the new arguments, priority and requester, and runs again
    /// from the start with a fresh set of attempts, whether it was waiting, had failed, was given up or had completed.
    /// <para>
    /// A job that is running stays running: its lease is revoked, so whatever its attempt reports is refused, and it is
    /// taken again with the new arguments when that lease runs out. Its lane stays closed until then.
    /// </para>
    /// <para>
    /// Only jobs enqueued by hand are replaced, never a planned one. Without a <see cref="NewJob.DedupKey"/> there is
    /// nothing to replace, and every call creates a job.
    /// </para>
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The job and whether it replaced one. Fails as <see cref="EnqueueAsync"/> does, with
    /// <see cref="SyncErrorCodes.JobAlreadyExists"/> only when the key belongs to a planned job.
    /// </returns>
    Task<SyncResult<EnqueuedJob>> EnqueueOrReplaceAsync(NewJob job, CancellationToken cancellationToken = default);

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
    /// Completes an owner's jobs of one type by their <see cref="NewJob.DedupKey"/>, without running them: for work
    /// that was done some other way, such as a delivery a later direct call got through. Jobs that are waiting, failed,
    /// deferred or abandoned are completed, and so is one that is running: whatever its attempt then reports is
    /// refused and does not reopen it. Its lane is free at once, while that attempt may still be inside your handler
    /// until its next page or heartbeat.
    /// <para>
    /// Only jobs enqueued by hand are completed, never a planned one, and nothing is counted as work done: the hourly
    /// volume and what an owner's data covers stay as they are. A key that matches no unfinished job is not an error.
    /// </para>
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="owner">The owner's key.</param>
    /// <param name="type">The job type.</param>
    /// <param name="dedupKeys">The keys of the jobs to complete. A key given twice counts once.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// How many jobs were completed. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/> or
    /// <see cref="SyncErrorCodes.JobTypeNotDeclared"/>.
    /// </returns>
    Task<SyncResult<int>> CompleteAsync(string queue, string owner, string type, IReadOnlyList<string> dedupKeys, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Reads one job by its id, with its <see cref="Job.Arguments"/>, which a list leaves out. Any job of the schema can
    /// be read, whichever application runs its queue.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job. Fails with <see cref="SyncErrorCodes.JobNotFound"/>.</returns>
    Task<SyncResult<Job>> GetJobAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Reports how far the first load of <paramref name="owner"/> has come.</summary>
    /// <returns>The progress. Fails with <see cref="SyncErrorCodes.QueueNotDeclared"/>.</returns>
    Task<SyncResult<OwnerProgress>> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default);
}
