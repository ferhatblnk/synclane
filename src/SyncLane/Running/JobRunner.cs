using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncLane.Planning;
using SyncLane.Storage;
using SyncLane.Telemetry;

namespace SyncLane.Running;

/// <summary>
/// Runs one leased job to an end: page by page, saving the cursor after each page and renewing the lease while it works.
/// </summary>
internal sealed partial class JobRunner(
    JobStore store, IServiceScopeFactory scopes, TimeProvider time, SyncLaneMetrics metrics, ILogger<JobRunner> logger)
{
    private const int HeartbeatsPerLease = 3;

    public async Task RunAsync(QueueOptions queue, Job job, string worker, CancellationToken stopping)
    {
        using var activity = SyncLaneTelemetry.Source.StartActivity("synclane.job");
        activity?.SetTag(SyncLaneMetrics.QueueTag, queue.Name)
            .SetTag(SyncLaneMetrics.TypeTag, job.Type)
            .SetTag("synclane.job.id", job.Id)
            .SetTag("synclane.job.mode", JobText.Of(job.Mode))
            .SetTag("synclane.job.attempt", job.AttemptCount);

        var started = time.GetTimestamp();
        metrics.Taken(queue.Name, job.Type, time.GetUtcNow() - job.NextRunAt);

        var outcome = await RunToAnEndAsync(queue, job, worker, activity, stopping);

        metrics.Finished(queue.Name, job.Type, outcome, time.GetElapsedTime(started));
        activity?.SetTag(SyncLaneMetrics.OutcomeTag, outcome);
    }

    private async Task<string> RunToAnEndAsync(QueueOptions queue, Job job, string worker, Activity? activity, CancellationToken stopping)
    {
        if (queue.Find(job.Type) is not { } type)
            return await AbandonAsync(job, worker, SyncLaneErrors.HandlerMissing(queue.Name, job.Type), activity);

        if (job.AttemptCount > queue.MaxAttempts + 1)
            return await AbandonAsync(job, worker, SyncLaneErrors.TooManyPickups(), activity);

        using var held = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = KeepLeaseAsync(queue, job, worker, held);

        try
        {
            return await RunPagesAsync(queue, type, job, worker, held.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await store.ReleaseAsync(job.Id, worker, time.GetUtcNow(), CancellationToken.None);
            LogReleased(job.Id, job.Type);
            return JobOutcome.Released;
        }
        catch (OperationCanceledException) when (held.IsCancellationRequested)
        {
            LogLeaseLost(job.Id, job.Type, worker);
            return JobOutcome.LeaseLost;
        }
        catch (Exception exception)
        {
            activity?.AddException(exception);
            return await FailAsync(queue, job, worker, exception, activity);
        }
        finally
        {
            await held.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<string> RunPagesAsync(QueueOptions queue, JobType type, Job job, string worker, CancellationToken cancellationToken)
    {
        var current = job;

        // Sequential: each page starts from the cursor the previous page returned.
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var started = time.GetTimestamp();
            var page = await RunPageAsync(queue, type, current, cancellationToken);
            metrics.Page(queue.Name, type.Name, page.Processed, time.GetElapsedTime(started));

            var cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
            var now = time.GetUtcNow();

            if (!await store.CheckpointAsync(current.Id, worker, cursor, page.Processed, now, now + queue.LeaseDuration, cancellationToken))
            {
                LogLeaseLost(current.Id, current.Type, worker);
                return JobOutcome.LeaseLost;
            }

            current = current with { Cursor = cursor, ProcessedCount = current.ProcessedCount + page.Processed };
        }
        while (current.Cursor is not null);

        if (!await store.CompleteAsync(current.Id, worker, time.GetUtcNow(), cancellationToken))
            return JobOutcome.LeaseLost;

        LogCompleted(current.Id, current.Type, current.ProcessedCount);
        return JobOutcome.Completed;
    }

    private async Task<JobPage> RunPageAsync(QueueOptions queue, JobType type, Job job, CancellationToken cancellationToken)
    {
        using var activity = SyncLaneTelemetry.Source.StartActivity("synclane.page");
        activity?.SetTag(SyncLaneMetrics.QueueTag, queue.Name)
            .SetTag(SyncLaneMetrics.TypeTag, type.Name)
            .SetTag("synclane.job.id", job.Id);

        await using var scope = scopes.CreateAsyncScope();
        var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(type.Handler);
        var page = await handler.RunPageAsync(job, cancellationToken);

        activity?.SetTag("synclane.page.processed", page.Processed)
            .SetTag("synclane.page.last", string.IsNullOrEmpty(page.NextCursor));
        return page;
    }

    private async Task KeepLeaseAsync(QueueOptions queue, Job job, string worker, CancellationTokenSource held)
    {
        var interval = queue.LeaseDuration / HeartbeatsPerLease;

        // Sequential: a timer loop; one renewal per interval.
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

    private async Task<string> FailAsync(QueueOptions queue, Job job, string worker, Exception exception, Activity? activity)
    {
        var now = time.GetUtcNow();
        var outcome = FailurePolicy.Decide(queue, job.AttemptCount, exception, now);
        var error = ErrorText.Describe(exception);
        activity?.SetStatus(ActivityStatusCode.Error, error);

        if (FailurePolicy.IsThrottle(exception))
            metrics.Throttled(queue.Name, job.Type);

        if (!await store.SettleAsync(job.Id, worker, outcome.Status, error, outcome.NextRunAt, now, CancellationToken.None))
        {
            LogLeaseLost(job.Id, job.Type, worker);
            return JobOutcome.LeaseLost;
        }

        if (outcome.Status == JobStatus.Failed)
        {
            LogFailed(job.Id, job.Type, job.AttemptCount, queue.MaxAttempts, outcome.NextRunAt - now, error);
            return JobOutcome.Failed;
        }

        if (outcome.Status == JobStatus.Deferred)
        {
            LogDeferred(job.Id, job.Type, job.AttemptCount, error);
            return JobOutcome.Deferred;
        }

        LogAbandoned(job.Id, job.Type, job.AttemptCount, error);
        return JobOutcome.Abandoned;
    }

    private async Task<string> AbandonAsync(Job job, string worker, SyncError error, Activity? activity)
    {
        var reason = error.ToString();
        activity?.SetStatus(ActivityStatusCode.Error, reason);

        var now = time.GetUtcNow();
        if (!await store.SettleAsync(job.Id, worker, JobStatus.Abandoned, reason, now, now, CancellationToken.None))
            return JobOutcome.LeaseLost;

        LogAbandoned(job.Id, job.Type, job.AttemptCount, reason);
        return JobOutcome.Abandoned;
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
