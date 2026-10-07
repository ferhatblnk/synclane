using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SyncLane.Planning;

/// <summary>Plans every queue that has an owner source, at start-up and then on every interval.</summary>
internal sealed partial class PlannerService(
    SyncLaneOptions options, QueuePlanner planner, IServiceScopeFactory scopes, TimeProvider time, ILogger<PlannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PlanInterval, time);

        do
        {
            foreach (var queue in options.Queues)
                await PlanAsync(queue, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
            if (planned > 0)
                LogPlanned(queue.Name, planned, owners.Count);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailed(exception, queue.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Planned {Planned} jobs on queue {Queue} for {Owners} owners.")]
    private partial void LogPlanned(string queue, int planned, int owners);

    [LoggerMessage(Level = LogLevel.Error, Message = "Planning queue {Queue} failed and is tried again on the next interval.")]
    private partial void LogFailed(Exception exception, string queue);
}
