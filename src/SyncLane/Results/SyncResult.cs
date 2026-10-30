namespace SyncLane;

/// <summary>
/// The outcome of an operation that can fail for an expected reason: an undeclared queue, a duplicate job, a limit out
/// of range. Expected failures come back as <see cref="Errors"/>, never as exceptions. A database that cannot be
/// reached, a cancelled call or a bug still throw.
/// </summary>
/// <remarks>Immutable and safe to share between threads.</remarks>
public class SyncResult
{
    private static readonly SyncResult Ok = new([]);

    /// <summary>Creates a result with <paramref name="errors"/>; no errors means success.</summary>
    protected SyncResult(IReadOnlyList<SyncError> errors)
    {
        Errors = errors;
    }

    /// <summary>The operation did what was asked.</summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>Why the operation failed. Empty when it succeeded.</summary>
    public IReadOnlyList<SyncError> Errors { get; }

    /// <summary>The first error, or <c>null</c> when the operation succeeded.</summary>
    public SyncError? Error => Errors.Count == 0 ? null : Errors[0];

    /// <summary>A result without errors.</summary>
    public static SyncResult Success() => Ok;

    /// <summary>A failed result.</summary>
    public static SyncResult Failure(SyncError error) => new(One(error));

    /// <summary>A failed result with several errors. At least one is required.</summary>
    public static SyncResult Failure(IEnumerable<SyncError> errors) => new(Many(errors));

    /// <summary>Throws a <see cref="SyncLaneException"/> carrying every error when the operation failed.</summary>
    public void ThrowIfFailed()
    {
        if (!Succeeded)
            throw new SyncLaneException(Errors);
    }

    /// <summary>The code and message of every error, or <c>Succeeded</c>.</summary>
    public override string ToString() => Succeeded ? "Succeeded" : string.Join("; ", Errors);

    private protected static SyncError[] One(SyncError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return [error];
    }

    private protected static SyncError[] Many(IEnumerable<SyncError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(list.Length, nameof(errors));
        foreach (var error in list)
            ArgumentNullException.ThrowIfNull(error, nameof(errors));

        return list;
    }
}
