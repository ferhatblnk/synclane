using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Telemetry;

namespace SyncLane.Planning;

/// <summary>
/// Plans every queue that has an owner source while the planner is on: when it comes on, and then on every interval.
/// </summary>
internal sealed partial class PlannerService(
    SyncLaneOptions options,
    QueuePlanner planner,
    Instances.RoleSwitch roles,
    IServiceScopeFactory scopes,
    TimeProvider time,
    SyncLaneMetrics metrics,
    Monitoring.HealthSignals health,
    ILogger<PlannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Sequential: a switch loop; planning runs while the planner is on and waits while it is off.
            while (!stoppingToken.IsCancellationRequested)
            {
                var on = await roles.PlannerOnAsync(stoppingToken);
                using var planning = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, on);
                try
                {
                    await PlanWhileOnAsync(planning.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PlanWhileOnAsync(CancellationToken planning)
    {
        using var timer = new PeriodicTimer(options.PlanInterval, time);

        // Sequential: a timer loop; one planning round per interval. Queues are planned side by side within a round.
        do
        {
            await Task.WhenAll(options.Queues.Select(queue => PlanAsync(queue, planning)));
        }
        while (await timer.WaitForNextTickAsync(planning));
    }

    private async Task PlanAsync(QueueOptions queue, CancellationToken stoppingToken)
    {
        if (queue.OwnerSource is not { } sourceType)
            return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var source = (IOwnerSource)scope.ServiceProvider.GetRequiredService(sourceType);

            var owners = await source.GetOwnersAsync(queue.Name, stoppingToken);
            var planned = await planner.PlanAsync(queue, owners, stoppingToken);
            health.Planned(queue.Name);
            if (planned > 0)
            {
                metrics.Planned(queue.Name, planned);
                LogPlanned(queue.Name, planned, owners.Count);
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            metrics.PlanFailed(queue.Name);
            health.PlanFailed(queue.Name, time.GetUtcNow());
            LogFailed(exception, queue.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Planned {Planned} jobs on queue {Queue} for {Owners} owners.")]
    private partial void LogPlanned(string queue, int planned, int owners);

    [LoggerMessage(Level = LogLevel.Error, Message = "Planning queue {Queue} failed and is tried again on the next interval.")]
    private partial void LogFailed(Exception exception, string queue);
}
