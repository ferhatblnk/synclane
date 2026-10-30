namespace SyncLane;

/// <summary>
/// Thrown for <see cref="SyncError"/>s that cannot be returned as a result: configuration problems found at start-up,
/// or a failed result the caller asked to throw with <see cref="SyncResult.ThrowIfFailed"/>.
/// </summary>
public sealed class SyncLaneException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    public SyncLaneException(SyncError error, Exception? innerException = null)
        : this([error], innerException)
    {
    }

    /// <summary>Creates the exception for several errors found together, such as every problem of one recipe.</summary>
    public SyncLaneException(IReadOnlyList<SyncError> errors, Exception? innerException = null)
        : base(Describe(errors), innerException)
    {
        Errors = errors;
    }

    /// <summary>The first error, with its code and kind.</summary>
    public SyncError Error => Errors[0];

    /// <summary>Every error, in the order they were found.</summary>
    public IReadOnlyList<SyncError> Errors { get; }

    private static string Describe(IReadOnlyList<SyncError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentOutOfRangeException.ThrowIfZero(errors.Count, nameof(errors));
        return string.Join(Environment.NewLine, errors);
    }
}
