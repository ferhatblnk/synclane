namespace SyncLane;

/// <summary>
/// One page of a list. Pages are read with a cursor instead of an offset, so the cost of a page does not grow with how
/// deep it is. The total is counted only when asked for, because counting a large table is not free.
/// </summary>
/// <typeparam name="T">The items of the list.</typeparam>
public sealed class SyncListResult<T> : SyncResult
{
    private SyncListResult(IReadOnlyList<T> items, string? nextCursor, long? total, IReadOnlyList<SyncError> errors)
        : base(errors)
    {
        Items = items;
        NextCursor = nextCursor;
        Total = total;
    }

    /// <summary>The items of this page. Empty when the operation failed.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Pass this to the next query to read the next page; <c>null</c> on the last page.</summary>
    public string? NextCursor { get; }

    /// <summary>There is a page after this one.</summary>
    public bool HasMore => NextCursor is not null;

    /// <summary>How many items match the query across every page, when the query asked for it; otherwise <c>null</c>.</summary>
    public long? Total { get; }

    /// <summary>A page of items.</summary>
    public static SyncListResult<T> Success(IReadOnlyList<T> items, string? nextCursor = null, long? total = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new SyncListResult<T>(items, nextCursor, total, []);
    }

    /// <summary>A failed result.</summary>
    public static new SyncListResult<T> Failure(SyncError error) => new([], null, null, One(error));

    /// <summary>A failed result with several errors. At least one is required.</summary>
    public static new SyncListResult<T> Failure(IEnumerable<SyncError> errors) => new([], null, null, Many(errors));
}
