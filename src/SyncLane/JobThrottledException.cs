namespace SyncLane;

/// <summary>Thrown by a handler when the other side says "slow down". The job waits longer than after an ordinary failure.</summary>
public sealed class JobThrottledException(string message, TimeSpan? retryAfter = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>How long the other side asked to wait, when it said so.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
