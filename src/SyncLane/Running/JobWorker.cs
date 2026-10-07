using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Storage;

namespace SyncLane.Running;

/// <summary>
/// Runs the worker slots of every queue. A slot takes one job, runs it to an end and takes the next;
/// when nothing is due it waits for the poll interval.
/// </summary>
internal sealed partial class JobWorker(
    SyncLaneOptions options, JobStore store, JobRunner runner, TimeProvider time, ILogger<JobWorker> logger) : BackgroundService
{
    private const double JitterShare = 0.25;

    private readonly string _host = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slots = options.Queues
            .SelectMany(queue => Enumerable.Range(1, queue.Concurrency)
                .Select(slot => RunSlotAsync(queue, $"{_host}/{queue.Name}#{slot}", stoppingToken)))
            .ToArray();

        LogStarted(_host, slots.Length, options.Queues.Count);
        await Task.WhenAll(slots);
    }

    private async Task RunSlotAsync(QueueOptions queue, string worker, CancellationToken stopping)
    {
        await Task.Yield();

        while (!stopping.IsCancellationRequested)
        {
            var worked = false;

            try
            {
                worked = await RunNextAsync(queue, worker, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogSlotFailed(exception, worker);
            }

            if (worked)
                continue;

            try
            {
                await Task.Delay(IdleDelay(), time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RunNextAsync(QueueOptions queue, string worker, CancellationToken stopping)
    {
        var now = time.GetUtcNow();
        var job = await store.LeaseAsync(queue.Name, worker, now, now + queue.LeaseDuration, stopping);
        if (job is null)
            return false;

        LogLeased(worker, job.Id, job.Type, job.Owner, job.AttemptCount);
        await runner.RunAsync(queue, job, worker, stopping);
        return true;
    }

    private TimeSpan IdleDelay() =>
        options.PollInterval + options.PollInterval * (Random.Shared.NextDouble() * JitterShare);

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLane worker {Host} started with {Slots} slots over {Queues} queues.")]
    private partial void LogStarted(string host, int slots, int queues);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Worker} took job {JobId} ({JobType}) of {Owner}, attempt {Attempt}.")]
    private partial void LogLeased(string worker, long jobId, string jobType, string owner, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Worker} could not take or finish a job and waits before it tries again.")]
    private partial void LogSlotFailed(Exception exception, string worker);
}
