using System.Globalization;
using Microsoft.Extensions.Logging;
using SyncLane.Instances;
using SyncLane.Storage;

namespace SyncLane.Monitoring;

internal sealed partial class SyncLaneMonitor(
    MonitorStore monitor,
    MonitorSql sql,
    BudgetStore budgets,
    InstanceStore instances,
    JobChangeFeed changes,
    TimeProvider time,
    ILogger<SyncLaneMonitor> logger) : ISyncLaneMonitor
{
    public async Task<SyncResult<IReadOnlyList<QueueOverview>>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<QueueOverview>>.Success(await monitor.OverviewAsync(time.GetUtcNow(), InstanceRules.GoneAfter, cancellationToken));

    public async Task<SyncListResult<OwnerFreshness>> GetFreshnessAsync(FreshnessQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<OwnerFreshness>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<OwnerFreshness>.Failure(SyncLaneErrors.SearchInvalid(search.Length));

        (DateTimeOffset, string, string, string)? after = null;
        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, 4, out var parts)
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return SyncListResult<OwnerFreshness>.Failure(SyncLaneErrors.CursorInvalid());

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1], parts[2], parts[3]);
        }

        // One row more than asked tells whether a next page exists without a second query.
        var rows = await monitor.FreshnessAsync(query.Queue, query.Owner, query.Search, after, query.Limit + 1, time.GetUtcNow(), cancellationToken);
        if (rows.Count <= query.Limit)
            return SyncListResult<OwnerFreshness>.Success(rows.Select(row => row.Row).ToList());

        rows.RemoveAt(rows.Count - 1);
        var (last, key) = rows[^1];
        var next = KeysetCursor.Encode(key.UtcTicks.ToString(CultureInfo.InvariantCulture), last.Queue, last.Owner, last.Type);
        return SyncListResult<OwnerFreshness>.Success(rows.Select(row => row.Row).ToList(), next);
    }

    public async Task<SyncListResult<OwnerFreshnessGroup>> GetFreshnessOwnersAsync(
        FreshnessOwnerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(query.Show))
            throw new ArgumentOutOfRangeException(nameof(query));

        if (query.Queues is not { Count: > 0 } queues)
            return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.QueuesInvalid("no queue was given."));

        if (queues.Any(string.IsNullOrWhiteSpace))
            return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.QueuesInvalid("a queue name is empty."));

        if (query.StaleAfter <= TimeSpan.Zero)
            return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.StaleAfterInvalid(query.StaleAfter));

        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.SearchInvalid(search.Length));

        (DateTimeOffset, string)? after = null;
        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, 2, out var parts)
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return SyncListResult<OwnerFreshnessGroup>.Failure(SyncLaneErrors.CursorInvalid());

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
        }

        var now = time.GetUtcNow();
        // A StaleAfter longer than the calendar reaches back makes only owners without completed data stale.
        var staleBefore = query.StaleAfter < now - MonitorSql.Never ? now - query.StaleAfter : MonitorSql.Never;
        var (groups, lastKey, more, total) = await monitor.FreshnessOwnersAsync(
            queues.Distinct(StringComparer.Ordinal).ToArray(), query.Search, query.Show, staleBefore, after, query.Limit,
            query.IncludeTotal, now, cancellationToken);

        var next = more ? KeysetCursor.Encode(lastKey.UtcTicks.ToString(CultureInfo.InvariantCulture), groups[^1].Owner) : null;
        return SyncListResult<OwnerFreshnessGroup>.Success(groups, next, total);
    }

    public async Task<SyncListResult<JobOwnerSummary>> GetJobOwnersAsync(JobOwnerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Queues is not { Count: > 0 } queues)
            return SyncListResult<JobOwnerSummary>.Failure(SyncLaneErrors.QueuesInvalid("no queue was given."));

        if (queues.Any(string.IsNullOrWhiteSpace))
            return SyncListResult<JobOwnerSummary>.Failure(SyncLaneErrors.QueuesInvalid("a queue name is empty."));

        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<JobOwnerSummary>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<JobOwnerSummary>.Failure(SyncLaneErrors.SearchInvalid(search.Length));

        (DateTimeOffset, string)? after = null;
        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, 2, out var parts)
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return SyncListResult<JobOwnerSummary>.Failure(SyncLaneErrors.CursorInvalid());

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
        }

        // One row more than asked tells whether a next page exists without a second query.
        var (owners, total) = await monitor.JobOwnersAsync(
            queues.Distinct(StringComparer.Ordinal).ToArray(), query.Status, query.Type, query.Search, after, query.Limit + 1,
            query.IncludeTotal, cancellationToken);
        if (owners.Count <= query.Limit)
            return SyncListResult<JobOwnerSummary>.Success(owners, null, total);

        owners.RemoveAt(owners.Count - 1);
        var last = owners[^1];
        var next = KeysetCursor.Encode(last.LastJobAt.UtcTicks.ToString(CultureInfo.InvariantCulture), last.Owner);
        return SyncListResult<JobOwnerSummary>.Success(owners, next, total);
    }

    public async Task<SyncResult<IReadOnlyList<ProblemGroup>>> GetProblemsAsync(string? queue = null, CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<ProblemGroup>>.Success(await monitor.ProblemsAsync(queue, cancellationToken));

    public async Task<SyncListResult<BudgetState>> GetBudgetsAsync(BudgetQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<BudgetState>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<BudgetState>.Failure(SyncLaneErrors.SearchInvalid(search.Length, "a budget's name or a key"));

        (string, string)? after = null;
        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, 2, out var parts))
                return SyncListResult<BudgetState>.Failure(SyncLaneErrors.CursorInvalid());

            after = (parts[0], parts[1]);
        }

        var (rows, total) = await budgets.ListAsync(
            query.Budget, query.Search, after, query.Limit + 1, query.IncludeTotal, time.GetUtcNow(), cancellationToken);
        if (rows.Count <= query.Limit)
            return SyncListResult<BudgetState>.Success(rows, null, total);

        rows.RemoveAt(rows.Count - 1);
        return SyncListResult<BudgetState>.Success(rows, KeysetCursor.Encode(rows[^1].Budget, rows[^1].Key), total);
    }

    public async Task<SyncResult<IReadOnlyList<InstanceInfo>>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        var aliveSince = time.GetUtcNow() - InstanceRules.GoneAfter;
        var rows = await instances.AllAsync(cancellationToken);
        var live = rows.Where(row => IsAlive(row)).ToList();
        return SyncResult<IReadOnlyList<InstanceInfo>>.Success(rows.Select(row => new InstanceInfo
        {
            Id = row.Id,
            Application = row.Application,
            Role = row.Role,
            RunsWorkers = row.RunsWorkers,
            RunsPlanner = row.RunsPlanner,
            ListensForChanges = row.Listens,
            FollowsSwitches = row.RunsWorkers is not null,
            Host = row.Host,
            Version = row.Version,
            Queues = row.Queues,
            Budgets = row.Budgets,
            StartedAt = row.StartedAt,
            LastHeartbeatAt = row.HeartbeatAt,
            StoppedAt = row.StoppedAt,
            IsAlive = IsAlive(row),
            SharesNameWith = IsAlive(row) ? InstanceRules.Strangers(row, live).Select(other => other.Id).ToList() : []
        }).ToList());

        bool IsAlive(InstanceRow row) => row.StoppedAt is null && row.HeartbeatAt > aliveSince;
    }

    public async Task<SyncResult<IReadOnlyList<VolumeBucket>>> GetVolumeAsync(VolumeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var to = query.To ?? time.GetUtcNow();
        if (to <= query.From)
            return SyncResult<IReadOnlyList<VolumeBucket>>.Success([]);

        return SyncResult<IReadOnlyList<VolumeBucket>>.Success(await monitor.VolumeAsync(query, to, cancellationToken));
    }

    public IAsyncEnumerable<JobChange> WatchAsync(string? queue = null, string? owner = null, CancellationToken cancellationToken = default) =>
        changes.ReadAllAsync(queue, owner, cancellationToken);

    public Task<SyncResult<int>> RequeueJobAsync(long id, string? by = null, CancellationToken cancellationToken = default) =>
        ChangeJobAsync(sql.RequeueJob, id, by, "a requeue", "failed, deferred and abandoned", cancellationToken);

    public async Task<SyncResult<int>> SkipJobAsync(long id, string reason, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var (status, skipped) = await monitor.SkipJobAsync(id, Who(by), reason, time.GetUtcNow(), cancellationToken);
        if (status is null)
            return SyncResult<int>.Failure(SyncLaneErrors.JobNotFound(id));

        if (!skipped)
            return SyncResult<int>.Failure(SyncLaneErrors.JobStatusNotAllowed(id, status, "a skip", "pending, failed, deferred and abandoned"));

        LogSkipped(id, status);
        return SyncResult<int>.Success(1);
    }

    public Task<SyncResult<int>> RestartJobAsync(long id, string? by = null, CancellationToken cancellationToken = default) =>
        ChangeJobAsync(sql.RestartJob, id, by, "a restart", "pending, completed, failed, deferred and abandoned", cancellationToken);

    public async Task<SyncResult<int>> RequeueByCodeAsync(string queue, string code, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var requeued = await monitor.RequeueByCodeAsync(queue, code, Who(by), time.GetUtcNow(), cancellationToken);
        LogRequeuedByCode(requeued, queue, code);
        return SyncResult<int>.Success(requeued);
    }

    public async Task<SyncResult<int>> PrioritizeOwnerAsync(string queue, string owner, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(owner);

        var moved = await monitor.PrioritizeOwnerAsync(queue, owner, Who(by), time.GetUtcNow(), cancellationToken);
        LogPrioritized(moved, queue, owner);
        return SyncResult<int>.Success(moved);
    }

    public async Task<SyncResult<long>> SyncNowAsync(string queue, string owner, string type, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (await monitor.SyncNowAsync(queue, owner, type, Planning.Priorities.Of(JobPriority.High, 0), Who(by), time.GetUtcNow(), cancellationToken)
            is not { } found)
            return SyncResult<long>.Failure(SyncLaneErrors.OwnerTypeNotPlanned(queue, owner, type));

        var (id, joined) = found;

        LogSyncNow(id, joined ? "stretched" : "created", queue, owner, type);
        return SyncResult<long>.Success(id);
    }

    public async Task<SyncResult<int>> PauseAsync(
        string queue, string? owner, string reason, string? by = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = time.GetUtcNow();
        if (until <= now)
            return SyncResult<int>.Failure(SyncLaneErrors.PauseEndInvalid(queue, until.Value, now));

        var paused = await monitor.PauseAsync(queue, owner ?? "", Who(by), reason, until, now, cancellationToken);
        LogPaused(queue, owner ?? "(all owners)", Who(by));
        return SyncResult<int>.Success(paused);
    }

    public async Task<SyncResult<int>> ResumeAsync(string queue, string? owner = null, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        var resumed = await monitor.ResumeAsync(queue, owner ?? "", Who(by), time.GetUtcNow(), cancellationToken);
        if (resumed > 0)
            LogResumed(queue, owner ?? "(all owners)", Who(by));

        return SyncResult<int>.Success(resumed);
    }

    public async Task<SyncResult<int>> LimitBudgetAsync(
        string budget, string? key, int limit, TimeSpan per, string reason, string? by = null, DateTimeOffset? until = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = time.GetUtcNow();
        if (limit < 1)
            return SyncResult<int>.Failure(SyncLaneErrors.BudgetLimitInvalid(budget, $"the limit is {limit}."));
        if (per <= TimeSpan.Zero)
            return SyncResult<int>.Failure(SyncLaneErrors.BudgetLimitInvalid(budget, $"the period is {per}."));
        if (until <= now)
            return SyncResult<int>.Failure(SyncLaneErrors.BudgetLimitInvalid(budget, $"the end {until.Value:u} is not after now ({now:u})."));

        var rate = limit / per.TotalSeconds;
        var set = await monitor.LimitBudgetAsync(budget, key ?? "", rate, until, Who(by), reason, now, cancellationToken);
        LogBudgetLimited(budget, key ?? "(all keys)", limit, per, Who(by));
        return SyncResult<int>.Success(set);
    }

    public async Task<SyncResult<int>> ClearBudgetLimitAsync(string budget, string? key = null, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budget);

        var cleared = await monitor.ClearBudgetLimitAsync(budget, key ?? "", Who(by), time.GetUtcNow(), cancellationToken);
        if (cleared > 0)
            LogBudgetLimitCleared(budget, key ?? "(all keys)", Who(by));

        return SyncResult<int>.Success(cleared);
    }

    public async Task<SyncResult<IReadOnlyList<BudgetLimitInfo>>> GetBudgetLimitsAsync(CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<BudgetLimitInfo>>.Success(await monitor.BudgetLimitsAsync(time.GetUtcNow(), cancellationToken));

    public async Task<SyncResult<int>> SetSwitchAsync(
        string application, string? role, bool? runWorkers, bool? runPlanner, string reason, string? by = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        role ??= "";
        if (!SyncLaneOptions.IsValidName(application))
            return SyncResult<int>.Failure(SyncLaneErrors.SwitchNameInvalid("application", application));
        if (role.Length > 0 && !SyncLaneOptions.IsValidName(role))
            return SyncResult<int>.Failure(SyncLaneErrors.SwitchNameInvalid("role", role));

        var target = $"{application}{(role.Length > 0 ? "/" + role : "")}: workers {State(runWorkers)}, planner {State(runPlanner)}";
        var set = await monitor.SetSwitchAsync(application, role, runWorkers, runPlanner, target, Who(by), reason, time.GetUtcNow(), cancellationToken);
        LogSwitchSet(target, Who(by));
        return SyncResult<int>.Success(set);

        static string State(bool? on) => on switch { true => "on", false => "off", null => "as set" };
    }

    public async Task<SyncResult<int>> SetListeningAsync(
        string application, string? role, bool? listen, string reason, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        role ??= "";
        if (!SyncLaneOptions.IsValidName(application))
            return SyncResult<int>.Failure(SyncLaneErrors.SwitchNameInvalid("application", application));
        if (role.Length > 0 && !SyncLaneOptions.IsValidName(role))
            return SyncResult<int>.Failure(SyncLaneErrors.SwitchNameInvalid("role", role));

        var target = $"{application}{(role.Length > 0 ? "/" + role : "")}: listening {(listen switch { true => "on", false => "off", null => "as set" })}";
        var set = await monitor.SetListeningAsync(application, role, listen, target, Who(by), reason, time.GetUtcNow(), cancellationToken);
        LogSwitchSet(target, Who(by));
        return SyncResult<int>.Success(set);
    }

    public async Task<SyncResult<IReadOnlyList<SwitchInfo>>> GetSwitchesAsync(CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<SwitchInfo>>.Success(await monitor.SwitchesAsync(time.GetUtcNow() - InstanceRules.GoneAfter, cancellationToken));

    public async Task<SyncResult<IReadOnlyList<PauseInfo>>> GetPausesAsync(CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<PauseInfo>>.Success(await monitor.PausesAsync(time.GetUtcNow(), cancellationToken));

    public async Task<SyncListResult<OperatorAction>> GetActionsAsync(ActionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<OperatorAction>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<OperatorAction>.Failure(SyncLaneErrors.SearchInvalid(search.Length, "an action's text"));

        long? before = null;
        if (query.Cursor is { } cursor)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return SyncListResult<OperatorAction>.Failure(SyncLaneErrors.CursorInvalid());

            before = id;
        }

        var (rows, total) = await monitor.ActionsAsync(
            query.Queue, query.Owner, query.Search, before, query.Limit + 1, query.IncludeTotal, cancellationToken);
        if (rows.Count <= query.Limit)
            return SyncListResult<OperatorAction>.Success(rows, null, total);

        rows.RemoveAt(rows.Count - 1);
        return SyncListResult<OperatorAction>.Success(rows, rows[^1].Id.ToString(CultureInfo.InvariantCulture), total);
    }

    /// <summary>The name an action is logged under when the caller gave none.</summary>
    internal const string Unnamed = "unnamed";

    private static string Who(string? by) => string.IsNullOrWhiteSpace(by) ? Unnamed : by;

    private async Task<SyncResult<int>> ChangeJobAsync(
        string statement, long id, string? by, string action, string allowed, CancellationToken cancellationToken)
    {
        var (status, changed) = await monitor.ChangeJobAsync(statement, id, Who(by), time.GetUtcNow(), cancellationToken);
        if (status is null)
            return SyncResult<int>.Failure(SyncLaneErrors.JobNotFound(id));

        if (!changed)
            return SyncResult<int>.Failure(SyncLaneErrors.JobStatusNotAllowed(id, status, action, allowed));

        LogJobChanged(id, action, status);
        return SyncResult<int>.Success(1);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} was {Status}; {Action} put it back in line.")]
    private partial void LogJobChanged(long jobId, string action, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} was {Status} and was skipped: it will not run, and the jobs behind it go on.")]
    private partial void LogSkipped(long jobId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {Count} jobs of queue {Queue} that failed with {Code}.")]
    private partial void LogRequeuedByCode(int count, string queue, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{By} paused {Owner} on queue {Queue}. Running jobs of it are stopped and nothing of it is taken until it is resumed.")]
    private partial void LogPaused(string queue, string owner, string by);

    [LoggerMessage(Level = LogLevel.Information, Message = "{By} resumed {Owner} on queue {Queue}.")]
    private partial void LogResumed(string queue, string owner, string by);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{By} limited budget {Budget} ({Key}) to {Limit} requests per {Per} until cleared or expired.")]
    private partial void LogBudgetLimited(string budget, string key, int limit, TimeSpan per, string by);

    [LoggerMessage(Level = LogLevel.Information, Message = "{By} set the switch of {Switch}.")]
    private partial void LogSwitchSet(string @switch, string by);

    [LoggerMessage(Level = LogLevel.Information, Message = "{By} lifted the limit on budget {Budget} ({Key}).")]
    private partial void LogBudgetLimitCleared(string budget, string key, string by);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Count} jobs of owner {Owner} on queue {Queue} ahead of the others.")]
    private partial void LogPrioritized(int count, string queue, string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync-now job {Id} {How} for owner {Owner}, type {Type} on queue {Queue}.")]
    private partial void LogSyncNow(long id, string how, string queue, string owner, string type);
}
