using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Storage;

namespace SyncLane.Instances;

/// <summary>
/// Registers this process in the instances table before the planner and the workers start, and refuses to start when
/// a live instance of another application runs one of its queues: their workers would take each other's jobs and give
/// up the types they do not know. Then it says it is alive every half minute, and marks itself stopped when it stops.
/// </summary>
internal sealed partial class InstanceRegistry(
    SyncLaneOptions options,
    InstanceStore store,
    InstanceIdentity identity,
    TimeProvider time,
    ILogger<InstanceRegistry> logger) : BackgroundService
{
    private InstanceRow? _me;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var queues = InstanceRules.Runs(options);
        var me = new InstanceRow(identity.Id, identity.Application, identity.Host, identity.Version, queues, options.DescribeBudgets(), now, now);

        var (live, refused) = await store.RegisterAsync(
            me, now - InstanceRules.GoneAfter, others => InstanceRules.Conflicts(identity, queues, others, now), cancellationToken);
        if (refused.Count > 0)
            throw new SyncLaneException(refused);

        foreach (var (mine, theirs, other) in InstanceRules.Disagreements(me.Budgets, live))
            LogBudgetDisagrees(mine.Name, mine.Limit, mine.Per, mine.Burst, other.Id, other.Application, theirs.Limit, theirs.Per, theirs.Burst);

        _me = me;
        LogRegistered(identity.Id, identity.Application, queues.Count);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InstanceRules.HeartbeatInterval, time);

        // Sequential: a timer loop; one heartbeat per interval.
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await HeartbeatAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_me is null)
            return;

        try
        {
            await store.StopAsync(identity.Id, time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStopFailed(exception, identity.Id, InstanceRules.GoneAfter);
        }
    }

    internal async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_me is null)
            return;

        try
        {
            var now = time.GetUtcNow();
            await store.HeartbeatAsync(_me, now, now - InstanceRules.ForgetAfter, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogHeartbeatFailed(exception, identity.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLane instance {Instance} of {Application} registered, running {Queues} queues.")]
    private partial void LogRegistered(string instance, string application, int queues);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Budget {Budget} is declared here as {Limit} per {Per} with burst {Burst}, but instance {Instance} of {Application} " +
        "declares {OtherLimit} per {OtherPer} with burst {OtherBurst}. A budget is shared, so instances that disagree pace it " +
        "differently. Declare it the same way everywhere; during a deploy that changes it this passes by itself.")]
    private partial void LogBudgetDisagrees(
        string budget, int limit, TimeSpan per, int burst, string instance, string application, int otherLimit, TimeSpan otherPer, int otherBurst);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLane instance {Instance} could not say it is alive and tries again in half a minute.")]
    private partial void LogHeartbeatFailed(Exception exception, string instance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLane instance {Instance} could not mark itself stopped; it counts as gone {GoneAfter} after its last heartbeat.")]
    private partial void LogStopFailed(Exception exception, string instance, TimeSpan goneAfter);
}
