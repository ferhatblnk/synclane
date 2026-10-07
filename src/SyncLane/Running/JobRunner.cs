using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncLane.Planning;
using SyncLane.Storage;

namespace SyncLane.Running;

/// <summary>
/// Runs one leased job to an end: page by page, saving the cursor after each page and renewing the lease while it works.
/// </summary>
internal sealed partial class JobRunner(JobStore store, IServiceScopeFactory scopes, TimeProvider time, ILogger<JobRunner> logger)
{
    private const int HeartbeatsPerLease = 3;

    public async Task RunAsync(QueueOptions queue, Job job, string worker, CancellationToken stopping)
    {
        if (queue.Find(job.Type) is not { } type)
        {
            await AbandonAsync(job, worker, $"No handler is declared for job type '{job.Type}' on queue '{queue.Name}'.");
            return;
        }

        if (job.AttemptCount > queue.MaxAttempts + 1)
        {
            await AbandonAsync(job, worker, "The job was picked up more often than its attempts allow without ever reporting a result. Its worker probably stops while running it.");
            return;
        }

        using var held = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = KeepLeaseAsync(queue, job, worker, held);

        try
        {
            await RunPagesAsync(queue, type, job, worker, held.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await store.ReleaseAsync(job.Id, worker, time.GetUtcNow(), CancellationToken.None);
            LogReleased(job.Id, job.Type);
        }
        catch (OperationCanceledException) when (held.IsCancellationRequested)
        {
            LogLeaseLost(job.Id, job.Type, worker);
        }
        catch (Exception exception)
        {
            await FailAsync(queue, job, worker, exception);
        }
        finally
        {
            await held.CancelAsync();
            await heartbeat;
        }
    }

    private async Task RunPagesAsync(QueueOptions queue, JobType type, Job job, string worker, CancellationToken cancellationToken)
    {
        var current = job;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await RunPageAsync(type, current, cancellationToken);
            var cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
            var now = time.GetUtcNow();

            if (!await store.CheckpointAsync(current.Id, worker, cursor, page.Processed, now, now + queue.LeaseDuration, cancellationToken))
            {
                LogLeaseLost(current.Id, current.Type, worker);
                return;
            }

            current = current with { Cursor = cursor, ProcessedCount = current.ProcessedCount + page.Processed };
        }
        while (current.Cursor is not null);

        if (await store.CompleteAsync(current.Id, worker, time.GetUtcNow(), cancellationToken))
            LogCompleted(current.Id, current.Type, current.ProcessedCount);
    }

    private async Task<JobPage> RunPageAsync(JobType type, Job job, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(type.Handler);
        return await handler.RunPageAsync(job, cancellationToken);
    }

    private async Task KeepLeaseAsync(QueueOptions queue, Job job, string worker, CancellationTokenSource held)
    {
        var interval = queue.LeaseDuration / HeartbeatsPerLease;

        while (true)
        {
            try
            {
                await Task.Delay(interval, time, held.Token);

                var now = time.GetUtcNow();
                if (await store.HeartbeatAsync(job.Id, worker, now, now + queue.LeaseDuration, held.Token))
                    continue;

                await held.CancelAsync();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                LogHeartbeatFailed(exception, job.Id, job.Type);
            }
        }
    }

    private async Task FailAsync(QueueOptions queue, Job job, string worker, Exception exception)
    {
        var now = time.GetUtcNow();
        var outcome = FailurePolicy.Decide(queue, job.AttemptCount, exception, now);
        var error = ErrorText.Describe(exception);

        if (!await store.SettleAsync(job.Id, worker, outcome.Status, error, outcome.NextRunAt, now, CancellationToken.None))
        {
            LogLeaseLost(job.Id, job.Type, worker);
            return;
        }

        if (outcome.Status == JobStatus.Failed)
            LogFailed(job.Id, job.Type, job.AttemptCount, queue.MaxAttempts, outcome.NextRunAt - now, error);
        else if (outcome.Status == JobStatus.Deferred)
            LogDeferred(job.Id, job.Type, job.AttemptCount, error);
        else LogAbandoned(job.Id, job.Type, job.AttemptCount, error);
    }

    private async Task AbandonAsync(Job job, string worker, string reason)
    {
        var now = time.GetUtcNow();
        if (await store.SettleAsync(job.Id, worker, JobStatus.Abandoned, reason, now, now, CancellationToken.None))
            LogAbandoned(job.Id, job.Type, job.AttemptCount, reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} ({JobType}) completed with {Processed} records.")]
    private partial void LogCompleted(long jobId, string jobType, long processed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} ({JobType}) was handed back because the worker is stopping. It continues from its cursor.")]
    private partial void LogReleased(long jobId, string jobType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) is no longer held by {Worker}. Another worker took it over and this one stops.")]
    private partial void LogLeaseLost(long jobId, string jobType, string worker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) failed on attempt {Attempt} of {MaxAttempts} and runs again in {Delay}: {Error}")]
    private partial void LogFailed(long jobId, string jobType, int attempt, int maxAttempts, TimeSpan delay, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) is deferred after {Attempt} attempts. It gets one more once its lane is idle: {Error}")]
    private partial void LogDeferred(long jobId, string jobType, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} ({JobType}) is abandoned after {Attempt} attempts and waits for a requeue: {Error}")]
    private partial void LogAbandoned(long jobId, string jobType, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not renew the lease of job {JobId} ({JobType}). Trying again.")]
    private partial void LogHeartbeatFailed(Exception exception, long jobId, string jobType);
}
