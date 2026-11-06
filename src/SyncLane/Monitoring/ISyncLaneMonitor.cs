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
    /// <param name="id">The job.</param>
    /// <param name="by">Who asks, for the actions log. Endpoints fill in the signed-in user.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.JobNotFound"/> or, for a job in another status, <see cref="SyncErrorCodes.JobStatusNotAllowed"/>.</returns>
    Task<SyncResult<int>> RequeueJobAsync(long id, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes one job out of line for good, without running it, for example a stock movement that must no longer be sent,
    /// so the jobs behind it in an ordered lane go on. The job is deleted, and the actions log keeps who skipped which job
    /// and why. Its dedup key is free again. A running job is not skipped: pause its owner first.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="reason">Why, for the actions log.</param>
    /// <param name="by">Who asks, for the actions log. Endpoints fill in the signed-in user.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.JobNotFound"/> or, for a running or completed job, <see cref="SyncErrorCodes.JobStatusNotAllowed"/>.</returns>
    Task<SyncResult<int>> SkipJobAsync(long id, string reason, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one job again from its first page, with its cursor and record count cleared: for a window whose data has to be
    /// fetched again. Any job that is not running can be restarted, a completed one too.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.JobNotFound"/> or, for a running job, <see cref="SyncErrorCodes.JobStatusNotAllowed"/>.</returns>
    Task<SyncResult<int>> RestartJobAsync(long id, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives every failed, deferred and abandoned job of the queue with this error code a fresh set of attempts, for
    /// example after fixing what <see cref="GetProblemsAsync"/> reported. Cursors are kept.
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="code">A code as <see cref="ProblemGroup.Code"/> gives it, such as <c>SL5106</c>.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were requeued.</returns>
    Task<SyncResult<int>> RequeueByCodeAsync(string queue, string code, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the owner's waiting jobs on the queue ahead of every other owner's, at every level but
    /// <see cref="JobPriority.Urgent"/>, for example for an account that has just connected and is waiting for its
    /// history. Among its own jobs the order of levels and types stays. Jobs planned later get the usual priority: to keep
    /// an owner ahead for good, give it a <see cref="JobOwner.Priority"/>. Asking again changes nothing.
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="owner">The owner.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many jobs were moved forward.</returns>
    Task<SyncResult<int>> PrioritizeOwnerAsync(string queue, string owner, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches an owner's new data now instead of at its next period, for example when the other side announces a change
    /// with a webhook. The job covers from where the owner's completed data reaches to now; a type without windows is
    /// fetched whole. It runs at <see cref="JobPriority.High"/>. While it waits, another call stretches it to now instead
    /// of adding a job, so a burst of announcements is one fetch. It needs no queue declared, like the other repairs, and
    /// is written to the actions log with <paramref name="by"/>. The planner's own jobs go on as before.
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="owner">The owner's key.</param>
    /// <param name="type">The job type to fetch.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The id of the job that will fetch, new or already waiting. <see cref="SyncErrorCodes.OwnerTypeNotPlanned"/> when
    /// the queue has never planned that owner and type.
    /// </returns>
    Task<SyncResult<long>> SyncNowAsync(string queue, string owner, string type, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a queue, or one owner on it, at once: an emergency stop. No job of it is taken, and jobs of it running on any
    /// instance are cut where they are and handed back without using an attempt. The page that was cut runs again on
    /// resume, so handlers must write idempotently, as they must anyway. Planning goes on, and new jobs wait; nothing is
    /// lost. Pausing what is paused replaces the reason and the end.
    /// </summary>
    /// <param name="queue">The queue.</param>
    /// <param name="owner">One owner on the queue, or <c>null</c> for the whole queue.</param>
    /// <param name="reason">Why, for whoever sees the pause. Required.</param>
    /// <param name="by">Who asks, for the pause and the actions log.</param>
    /// <param name="until">When the pause ends by itself, or <c>null</c> to keep it until <see cref="ResumeAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.PauseEndInvalid"/> when <paramref name="until"/> is not in the future.</returns>
    Task<SyncResult<int>> PauseAsync(
        string queue, string? owner, string reason, string? by = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default);

    /// <summary>Lifts the pause of a queue, or of one owner on it. Idle workers on every instance wake at once.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="owner">The owner whose pause to lift, or <c>null</c> for the queue's own pause.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1, or 0 when nothing was paused.</returns>
    Task<SyncResult<int>> ResumeAsync(string queue, string? owner = null, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lowers a rate budget for a while, without a deploy: for example "the API answers slowly, halve our rate for an
    /// hour". Every instance obeys from its next request. The limit only lowers the ceiling, never raises it: the budget's
    /// declared limit, or the limit the API reports, stays the upper bound. Setting a limit where one is replaces it.
    /// </summary>
    /// <param name="budget">The budget, as declared with <c>AddBudget</c>.</param>
    /// <param name="key">One key of the budget (one account or token), or <c>null</c> for every key.</param>
    /// <param name="limit">Requests allowed per <paramref name="per"/>; at least 1.</param>
    /// <param name="per">The period of <paramref name="limit"/>; positive.</param>
    /// <param name="reason">Why, for whoever sees the limit. Required.</param>
    /// <param name="by">Who asks, for the limit and the actions log.</param>
    /// <param name="until">When the limit ends by itself, or <c>null</c> to keep it until <see cref="ClearBudgetLimitAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1. Fails with <see cref="SyncErrorCodes.BudgetLimitInvalid"/> when the limit, period or end cannot be used.</returns>
    Task<SyncResult<int>> LimitBudgetAsync(
        string budget, string? key, int limit, TimeSpan per, string reason, string? by = null, DateTimeOffset? until = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lifts a temporary limit set with <see cref="LimitBudgetAsync"/>.</summary>
    /// <param name="budget">The budget.</param>
    /// <param name="key">The key whose limit to lift, or <c>null</c> for the limit on every key.</param>
    /// <param name="by">Who asks, for the actions log.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>1, or 0 when there was no such limit.</returns>
    Task<SyncResult<int>> ClearBudgetLimitAsync(string budget, string? key = null, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>The temporary budget limits in force: who set what, why, and until when.</summary>
    Task<SyncResult<IReadOnlyList<BudgetLimitInfo>>> GetBudgetLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>The pauses in force: who paused what, why, and until when.</summary>
    Task<SyncResult<IReadOnlyList<PauseInfo>>> GetPausesAsync(CancellationToken cancellationToken = default);

    /// <summary>What operators did through the monitor, newest first, with who did it and why. Kept for 400 days.</summary>
    /// <returns>One page. Fails with <see cref="SyncErrorCodes.LimitOutOfRange"/> or <see cref="SyncErrorCodes.CursorInvalid"/>.</returns>
    Task<SyncListResult<OperatorAction>> GetActionsAsync(ActionQuery query, CancellationToken cancellationToken = default);
}
