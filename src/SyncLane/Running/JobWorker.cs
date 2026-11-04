using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Instances;
using SyncLane.Monitoring;
using SyncLane.Storage;

namespace SyncLane.Running;

/// <summary>
/// Runs the worker slots of every queue. A slot takes one job, runs it to an end and takes the next; when nothing is due
/// it waits for the poll interval, or less when a job is created or put back in line on its queue, from any instance.
/// </summary>
internal sealed partial class JobWorker(
    SyncLaneOptions options,
    JobStore store,
    JobRunner runner,
    InstanceIdentity identity,
    JobChangeFeed changes,
    RunningJobs running,
    TimeProvider time,
    ILogger<JobWorker> logger)
    : BackgroundService
{
    private readonly QueueSignals _signals = new();

    private const double JitterShare = 0.25;

    private readonly string _host = identity.Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slots = options.Queues
            .SelectMany(queue => Enumerable.Range(1, queue.Concurrency)
                .Select(slot => RunSlotAsync(queue, $"{_host}/{queue.Name}#{slot}", stoppingToken)))
            .ToArray();

        LogStarted(_host, slots.Length, options.Queues.Count);
        await Task.WhenAll([.. slots, WakeOnNewWorkAsync(stoppingToken), ObeyPausesAsync(stoppingToken)]);
    }

    /// <summary>
    /// Cuts this process's running jobs a pause covers, the moment the pause is announced; wakes the queue's slots when it
    /// is resumed.
    /// </summary>
    private async Task ObeyPausesAsync(CancellationToken stopping)
    {
        try
        {
            // Sequential: reading the announcements in order.
            await foreach (var signal in changes.ReadPausesAsync(stopping))
            {
                if (signal.Resumed)
                    _signals.Pulse(signal.Queue);
                else if (running.Pause(signal.Queue, signal.Owner) is var cut and > 0)
                    LogCut(cut, signal.Queue);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Wakes the slots of a queue when one of its jobs becomes pending: created, requeued, restarted or handed back.</summary>
    private async Task WakeOnNewWorkAsync(CancellationToken stopping)
    {
        try
        {
            // Sequential: reading the change feed in order.
            await foreach (var change in changes.ReadAllAsync(null, null, stopping))
            {
                if (change.Status == JobStatus.Pending)
                    _signals.Pulse(change.Queue);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RunSlotAsync(QueueOptions queue, string worker, CancellationToken stopping)
    {
        await Task.Yield();

        // Sequential: one slot runs one job at a time by design; slots run side by side.
        while (!stopping.IsCancellationRequested)
        {
            var worked = false;
            var woken = _signals.Next(queue.Name);

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

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            await Task.WhenAny(Task.Delay(IdleDelay(), time, idle.Token), woken);
            await idle.CancelAsync();
            if (stopping.IsCancellationRequested)
                return;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "A pause on queue {Queue} stopped {Cut} running jobs here.")]
    private partial void LogCut(int cut, string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLane worker {Host} started with {Slots} slots over {Queues} queues.")]
    private partial void LogStarted(string host, int slots, int queues);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Worker} took job {JobId} ({JobType}) of {Owner}, attempt {Attempt}.")]
    private partial void LogLeased(string worker, long jobId, string jobType, string owner, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Worker} could not take or finish a job and waits before it tries again.")]
    private partial void LogSlotFailed(Exception exception, string worker);
}
