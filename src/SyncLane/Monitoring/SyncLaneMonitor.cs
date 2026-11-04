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
    TimeProvider time,
    ILogger<SyncLaneMonitor> logger) : ISyncLaneMonitor
{
    /// <summary>
    /// How far a prioritized owner's jobs move forward. Type priorities are declaration positions, so this puts the owner
    /// ahead of every other owner and keeps its own order.
    /// </summary>
    internal const int PriorityBoost = 1_000_000;

    public async Task<SyncResult<IReadOnlyList<QueueOverview>>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<QueueOverview>>.Success(await monitor.OverviewAsync(time.GetUtcNow(), InstanceRules.GoneAfter, cancellationToken));

    public async Task<SyncListResult<OwnerFreshness>> GetFreshnessAsync(FreshnessQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<OwnerFreshness>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

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
        var rows = await monitor.FreshnessAsync(query.Queue, query.Owner, after, query.Limit + 1, cancellationToken);
        if (rows.Count <= query.Limit)
            return SyncListResult<OwnerFreshness>.Success(rows.Select(row => row.Row).ToList());

        rows.RemoveAt(rows.Count - 1);
        var (last, key) = rows[^1];
        var next = KeysetCursor.Encode(key.UtcTicks.ToString(CultureInfo.InvariantCulture), last.Queue, last.Owner, last.Type);
        return SyncListResult<OwnerFreshness>.Success(rows.Select(row => row.Row).ToList(), next);
    }

    public async Task<SyncResult<IReadOnlyList<ProblemGroup>>> GetProblemsAsync(string? queue = null, CancellationToken cancellationToken = default) =>
        SyncResult<IReadOnlyList<ProblemGroup>>.Success(await monitor.ProblemsAsync(queue, cancellationToken));

    public async Task<SyncListResult<BudgetState>> GetBudgetsAsync(BudgetQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<BudgetState>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        (string, string)? after = null;
        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, 2, out var parts))
                return SyncListResult<BudgetState>.Failure(SyncLaneErrors.CursorInvalid());

            after = (parts[0], parts[1]);
        }

        var rows = await budgets.ListAsync(query.Budget, after, query.Limit + 1, time.GetUtcNow(), cancellationToken);
        if (rows.Count <= query.Limit)
            return SyncListResult<BudgetState>.Success(rows);

        rows.RemoveAt(rows.Count - 1);
        return SyncListResult<BudgetState>.Success(rows, KeysetCursor.Encode(rows[^1].Budget, rows[^1].Key));
    }

    public async Task<SyncResult<IReadOnlyList<InstanceInfo>>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        var aliveSince = time.GetUtcNow() - InstanceRules.GoneAfter;
        var rows = await instances.AllAsync(cancellationToken);
        return SyncResult<IReadOnlyList<InstanceInfo>>.Success(rows.Select(row => new InstanceInfo
        {
            Id = row.Id,
            Application = row.Application,
            Host = row.Host,
            Version = row.Version,
            Queues = row.Queues,
            Budgets = row.Budgets,
            StartedAt = row.StartedAt,
            LastHeartbeatAt = row.HeartbeatAt,
            IsAlive = row.HeartbeatAt > aliveSince
        }).ToList());
    }

    public async Task<SyncResult<IReadOnlyList<VolumeBucket>>> GetVolumeAsync(VolumeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var to = query.To ?? time.GetUtcNow();
        if (to <= query.From)
            return SyncResult<IReadOnlyList<VolumeBucket>>.Success([]);

        return SyncResult<IReadOnlyList<VolumeBucket>>.Success(await monitor.VolumeAsync(query, to, cancellationToken));
    }

    public Task<SyncResult<int>> RequeueJobAsync(long id, CancellationToken cancellationToken = default) =>
        ChangeJobAsync(sql.RequeueJob, id, "a requeue", "failed, deferred and abandoned", cancellationToken);

    public Task<SyncResult<int>> RestartJobAsync(long id, CancellationToken cancellationToken = default) =>
        ChangeJobAsync(sql.RestartJob, id, "a restart", "pending, completed, failed, deferred and abandoned", cancellationToken);

    public async Task<SyncResult<int>> RequeueByCodeAsync(string queue, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var requeued = await monitor.RequeueByCodeAsync(queue, code, time.GetUtcNow(), cancellationToken);
        LogRequeuedByCode(requeued, queue, code);
        return SyncResult<int>.Success(requeued);
    }

    public async Task<SyncResult<int>> PrioritizeOwnerAsync(string queue, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(owner);

        var moved = await monitor.PrioritizeOwnerAsync(queue, owner, PriorityBoost, time.GetUtcNow(), cancellationToken);
        LogPrioritized(moved, queue, owner);
        return SyncResult<int>.Success(moved);
    }

    private async Task<SyncResult<int>> ChangeJobAsync(string statement, long id, string action, string allowed, CancellationToken cancellationToken)
    {
        var (status, changed) = await monitor.ChangeJobAsync(statement, id, time.GetUtcNow(), cancellationToken);
        if (status is null)
            return SyncResult<int>.Failure(SyncLaneErrors.JobNotFound(id));

        if (!changed)
            return SyncResult<int>.Failure(SyncLaneErrors.JobStatusNotAllowed(id, status, action, allowed));

        LogJobChanged(id, action, status);
        return SyncResult<int>.Success(1);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} was {Status}; {Action} put it back in line.")]
    private partial void LogJobChanged(long jobId, string action, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {Count} jobs of queue {Queue} that failed with {Code}.")]
    private partial void LogRequeuedByCode(int count, string queue, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Count} jobs of owner {Owner} on queue {Queue} ahead of the others.")]
    private partial void LogPrioritized(int count, string queue, string owner);
}
