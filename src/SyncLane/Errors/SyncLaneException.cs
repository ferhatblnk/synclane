namespace SyncLane;

/// <summary>
/// Thrown for a <see cref="SyncError"/> that cannot be returned as a result: a configuration problem found at start-up,
/// or a failed result the caller asked to throw with <see cref="SyncResult.ThrowIfFailed"/>.
/// </summary>
public sealed class SyncLaneException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    public SyncLaneException(SyncError error, Exception? innerException = null)
        : base(error.ToString(), innerException)
    {
        Error = error;
    }

    /// <summary>The error, with its code and kind.</summary>
    public SyncError Error { get; }
}
