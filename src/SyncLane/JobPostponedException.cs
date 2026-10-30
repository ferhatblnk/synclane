namespace SyncLane;

/// <summary>
/// Thrown to give a job's slot back for a while without counting an attempt: nothing went wrong, the job only has to
/// wait, for example for a rate budget. The job runs again after <see cref="Delay"/>, from its cursor.
/// </summary>
/// <remarks>
/// Use it only for waits that end by themselves. A job that is postponed forever never fails, so nothing will tell you.
/// </remarks>
public sealed class JobPostponedException(string message, TimeSpan delay, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>How long the job waits before it runs again.</summary>
    public TimeSpan Delay { get; } = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
}
