using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SyncLane.Storage;

/// <summary>
/// Deletes completed recurring and manual jobs older than <see cref="SyncLaneOptions.KeepCompletedJobs"/>, at start-up
/// and then every hour. Only the declared queues are cleaned, so instances of other applications sharing the table keep
/// their own rules. Batches are small and skip locked rows, so several instances can clean at once without waiting.
/// </summary>
internal sealed partial class RetentionService(
    SyncLaneOptions options, JobStore store, TimeProvider time, ILogger<RetentionService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    internal const int BatchSize = 1000;

    /// <summary>
    /// How long hourly counts and the operators' actions are kept. Both are small, so a year and more costs little, and an
    /// audit can look back that far.
    /// </summary>
    internal static readonly TimeSpan KeepStats = TimeSpan.FromDays(400);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.KeepCompletedJobs is not { } keep || options.Queues.Count == 0)
            return;

        var queues = options.Queues.Select(queue => queue.Name).ToArray();
        using var timer = new PeriodicTimer(Interval, time);

        // Sequential: a timer loop; one clean-up per interval.
        do
        {
            await CleanAsync(queues, keep, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Deletes everything that is due, one batch after the other. Returns how many jobs were deleted.</summary>
    internal async Task<int> CleanAsync(IReadOnlyList<string> queues, TimeSpan keep, CancellationToken cancellationToken)
    {
        var deleted = 0;
        try
        {
            var before = time.GetUtcNow() - keep;
            int batch;

            // Sequential: the next batch is only needed when this one came back full.
            do
            {
                batch = await store.DeleteCompletedAsync(queues, before, BatchSize, cancellationToken);
                deleted += batch;
            }
            while (batch == BatchSize);

            await store.DeleteOldStatsAsync(time.GetUtcNow() - KeepStats, time.GetUtcNow(), cancellationToken);

            if (deleted > 0)
                LogDeleted(deleted, keep);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception, deleted);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Deleted} completed jobs older than {Keep}.")]
    private partial void LogDeleted(int deleted, TimeSpan keep);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting old completed jobs stopped after {Deleted} and is tried again in an hour.")]
    private partial void LogFailed(Exception exception, int deleted);
}
