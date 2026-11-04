namespace SyncLane;

/// <summary>
/// What an operator asks of SyncLane: is everything all right, whose data is old and why, what is failing, and how the
/// rate budgets and instances are doing. It reads every queue in the schema, declared in this application or not, so one
/// application can watch the syncs of all. Each call is one round trip, served by an index or a small table.
/// <para>
/// Its actions repair what the reads show. Each one names its target (a job, an error code, an owner), never touches a
/// running job, and returns how many jobs it changed.
/// </para>
/// </summary>
public interface ISyncLaneMonitor
{
    /// <summary>One line per queue: what is due, running, failing and given up, and what was done in the last 24 hours.</summary>
    Task<SyncResult<IReadOnlyList<QueueOverview>>> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Owners and job types ranked by how old their newest completed data is, oldest first, each with the open job that
    /// explains it. Follow <see cref="SyncListResult{T}.NextCursor"/> for the next page.
    /// </summary>
    /// <returns>One page. Fails with <see cref="SyncErrorCodes.LimitOutOfRange"/> or <see cref="SyncErrorCodes.CursorInvalid"/>.</returns>
    Task<SyncListResult<OwnerFreshness>> GetFreshnessAsync(FreshnessQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Failing, deferred and abandoned jobs grouped by their error code, most jobs first: forty rows become one line.
    /// The code is the first <c>SLxxxx</c> in the error, or the exception's type for errors of your own handlers.
    /// </summary>
    Task<SyncResult<IReadOnlyList<ProblemGroup>>> GetProblemsAsync(string? queue = null, CancellationToken cancellationToken = default);

    /// <summary>Every rate budget and key: the rate it has learned, whether it is paused, and when it was last refused.</summary>
    /// <returns>One page. Fails with <see cref="SyncErrorCodes.LimitOutOfRange"/> or <see cref="SyncErrorCodes.CursorInvalid"/>.</returns>
    Task<SyncListResult<BudgetState>> GetBudgetsAsync(BudgetQuery query, CancellationToken cancellationToken = default);

    /// <summary>The processes registered in the schema, of every application, with whether they are alive.</summary>
    Task<SyncResult<IReadOnlyList<InstanceInfo>>> GetInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completed jobs, records, failed attempts and abandoned jobs per hour or day, for charts. The counts are kept for
    /// 400 days, after the jobs themselves are deleted.
    /// </summary>
    Task<SyncResult<IReadOnlyList<VolumeBucket>>> GetVolumeAsync(VolumeQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Jobs as they are created and change status, from every instance of every application on the schema, a moment
    /// after each change commits. Only the queue or owner given, if any. It reads until <paramref name="cancellationToken"/>
    /// is cancelled. Changes made while the connection was broken are not replayed, and a reader more than 1,000 changes
    /// behind loses the oldest: read the current state with the other calls after a gap.
    /// </summary>
    IAsyncEnumerable<JobChange> WatchAsync(string? queue = null, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>Gives one failed, deferred or abandoned job a fresh set of attempts. Its cursor is kept, so it continues where it stopped.</summary>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.JobNotFound"/> or, for a job in another status, <see cref="SyncErrorCodes.JobStatusNotAllowed"/>.</returns>
    Task<SyncResult<int>> RequeueJobAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one job again from its first page, with its cursor and record count cleared: for a window whose data has to be
    /// fetched again. Any job that is not running can be restarted, a completed one too.
    /// </summary>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.JobNotFound"/> or, for a running job, <see cref="SyncErrorCodes.JobStatusNotAllowed"/>.</returns>
    Task<SyncResult<int>> RestartJobAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives every failed, deferred and abandoned job of the queue with this error code a fresh set of attempts, for
    /// example after fixing what <see cref="GetProblemsAsync"/> reported. Cursors are kept.
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="code">A code as <see cref="ProblemGroup.Code"/> gives it, such as <c>SL5106</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were requeued.</returns>
    Task<SyncResult<int>> RequeueByCodeAsync(string queue, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the owner's waiting jobs on the queue ahead of every other owner's, for example for an account that has just
    /// connected and is waiting for its history. Among its own jobs the order of the types stays. Jobs planned later get
    /// the usual priority. Asking again changes nothing.
    /// </summary>
    /// <returns>How many jobs were moved forward.</returns>
    Task<SyncResult<int>> PrioritizeOwnerAsync(string queue, string owner, CancellationToken cancellationToken = default);
}
