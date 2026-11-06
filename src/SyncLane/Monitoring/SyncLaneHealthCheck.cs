using Microsoft.Extensions.Diagnostics.HealthChecks;
using SyncLane.Instances;
using SyncLane.Storage;

namespace SyncLane;

/// <summary>
/// Whether this instance of SyncLane can do its work. Register it with
/// <c>services.AddHealthChecks().AddCheck&lt;SyncLaneHealthCheck&gt;("synclane")</c>; <c>AddSyncLane</c> provides it.
/// </summary>
/// <remarks>
/// <para>
/// Unhealthy: the database cannot be reached, its schema lacks migrations this version needs, or this instance has not
/// renewed its registration for as long as other instances wait before they count it as gone.
/// </para>
/// <para>
/// Degraded: it has not registered yet, the latest planning round of a queue failed, or it does not hear job changes
/// (behind a pooler in transaction mode, or while it reconnects); workers still find jobs by polling.
/// </para>
/// <para>
/// Each check costs one small query. Descriptions name what is wrong and how to fix it, and never hold a connection
/// string or an exception's message: a database error is named by its type.
/// </para>
/// </remarks>
public sealed class SyncLaneHealthCheck : IHealthCheck
{
    private readonly JobStore _store;
    private readonly Monitoring.HealthSignals _signals;
    private readonly Monitoring.JobChangeFeed _feed;
    private readonly TimeProvider _time;

    internal SyncLaneHealthCheck(JobStore store, Monitoring.HealthSignals signals, Monitoring.JobChangeFeed feed, TimeProvider time)
    {
        _store = store;
        _signals = signals;
        _feed = feed;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        int missing;
        try
        {
            missing = (await _store.MissingMigrationsAsync(cancellationToken)).Count;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"SyncLane cannot reach its database ({exception.GetType().Name}). Check that PostgreSQL is up and that " +
                "ConnectionString points at it.");
        }

        if (missing > 0)
            return HealthCheckResult.Unhealthy(
                $"The SyncLane schema lacks {missing} migrations this version needs. Apply SyncLaneSchema.Script(), or set " +
                "CreateSchema to true, and start again.");

        var now = _time.GetUtcNow();
        if (_signals.HeartbeatAt is not { } heartbeat)
            return HealthCheckResult.Degraded("This instance of SyncLane has not registered yet; it is starting.");

        if (now - heartbeat >= InstanceRules.GoneAfter)
            return HealthCheckResult.Unhealthy(
                $"This instance of SyncLane has not renewed its registration since {heartbeat:O}, so other instances count " +
                "it as gone and another application may take its queues. The log says why the heartbeat fails.");

        var problems = new List<string>();
        foreach (var (queue, since) in _signals.PlanFailures.OrderBy(failure => failure.Key, StringComparer.Ordinal))
            problems.Add($"Planning queue {queue} has failed since {since:O}; no new jobs are planned for it. The log says why.");

        switch (_feed.State)
        {
            case Monitoring.FeedState.Stopped:
                problems.Add(
                    "This instance does not hear job changes: workers look for jobs every PollInterval and live views stay " +
                    "quiet. Set ListenConnectionString to a connection that can listen; the log says more.");
                break;
            case Monitoring.FeedState.Reconnecting:
                problems.Add("The connection that hears job changes is reconnecting; workers poll meanwhile.");
                break;
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy("SyncLane is running.")
            : HealthCheckResult.Degraded(string.Join(' ', problems));
    }
}
