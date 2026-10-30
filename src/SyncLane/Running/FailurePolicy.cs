namespace SyncLane.Running;

/// <summary>
/// Decides what a failed attempt means. A job retries with a growing delay, is deferred when its regular attempts
/// are used up, and is abandoned when the deferred attempt fails too.
/// </summary>
internal static class FailurePolicy
{
    public static FailureOutcome Decide(QueueOptions queue, int attempt, Exception exception, DateTimeOffset now)
    {
        if (Find<JobAbandonedException>(exception) is not null || attempt > queue.MaxAttempts)
            return new FailureOutcome(JobStatus.Abandoned, now);

        if (attempt == queue.MaxAttempts)
            return new FailureOutcome(JobStatus.Deferred, now);

        return new FailureOutcome(JobStatus.Failed, now + Delay(queue, attempt, exception));
    }

    public static TimeSpan Delay(QueueOptions queue, int attempt, Exception exception)
    {
        if (Find<JobThrottledException>(exception) is not { } throttled)
            return Backoff(attempt, queue.RetryBaseDelay, queue.RetryMaxDelay);

        return throttled.RetryAfter ?? Backoff(attempt, queue.ThrottleBaseDelay, queue.ThrottleMaxDelay);
    }

    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        var ticks = baseDelay.Ticks * Math.Pow(2, Math.Max(attempt - 1, 0));
        return ticks < maxDelay.Ticks ? TimeSpan.FromTicks((long)ticks) : maxDelay;
    }

    public static bool IsThrottle(Exception exception) => Find<JobThrottledException>(exception) is not null;

    /// <summary>The wait, when the job only has to wait and the attempt does not count.</summary>
    public static TimeSpan? Postponement(Exception exception) => Find<JobPostponedException>(exception)?.Delay;

    private static TException? Find<TException>(Exception exception) where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException found)
                return found;
        }

        return null;
    }
}
