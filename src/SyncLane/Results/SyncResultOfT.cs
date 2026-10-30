namespace SyncLane;

/// <summary>The outcome of an operation that returns a value when it succeeds. See <see cref="SyncResult"/>.</summary>
/// <typeparam name="T">The value of a successful result.</typeparam>
public sealed class SyncResult<T> : SyncResult
{
    private SyncResult(T? value, IReadOnlyList<SyncError> errors)
        : base(errors)
    {
        Value = value;
    }

    /// <summary>The value when the operation succeeded; <c>default</c> when it failed.</summary>
    public T? Value { get; }

    /// <summary>A successful result with <paramref name="value"/>.</summary>
    public static SyncResult<T> Success(T value) => new(value, []);

    /// <summary>A failed result.</summary>
    public static new SyncResult<T> Failure(SyncError error) => new(default, One(error));

    /// <summary>A failed result with several errors. At least one is required.</summary>
    public static new SyncResult<T> Failure(IEnumerable<SyncError> errors) => new(default, Many(errors));

    /// <summary>The value, or a <see cref="SyncLaneException"/> for the first error when the operation failed.</summary>
    public T GetValueOrThrow()
    {
        ThrowIfFailed();
        return Value!;
    }
}
