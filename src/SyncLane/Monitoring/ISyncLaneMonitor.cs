namespace SyncLane;

/// <summary>
/// What an operator asks of SyncLane: is everything all right, whose data is old and why, what is failing, and how the
/// rate budgets and instances are doing. It reads every queue in the schema, declared in this application or not, so one
/// application can watch the syncs of all. Each call is one round trip, served by an index or a small table.
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
}
