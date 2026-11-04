namespace SyncLane;

/// <summary>Which budgets to show, and where to continue.</summary>
public sealed record BudgetQuery
{
    /// <summary>Only this budget.</summary>
    public string? Budget { get; init; }

    /// <summary>Rows per page, 1 to <see cref="JobQuery.MaxLimit"/>. Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page, or <c>null</c> for the first.</summary>
    public string? Cursor { get; init; }
}

/// <summary>One bucket of a rate budget, shared by every instance.</summary>
public sealed record BudgetState
{
    /// <summary>The budget's name.</summary>
    public required string Budget { get; init; }

    /// <summary>Whose bucket: an owner, or a shared key.</summary>
    public required string Key { get; init; }

    /// <summary>Tokens in the bucket when it was last refilled.</summary>
    public double Tokens { get; init; }

    /// <summary>The rate the bucket has learned, in requests a second. <c>null</c> until it learns one.</summary>
    public double? Rate { get; init; }

    /// <summary>The rate that last caused a throttle, in requests a second.</summary>
    public double? RefusedRate { get; init; }

    /// <summary>When the bucket pays out again after a pause, or <c>null</c> when it is not paused.</summary>
    public DateTimeOffset? PausedUntil { get; init; }

    /// <summary>When the API last refused a request on this key.</summary>
    public DateTimeOffset? LastThrottledAt { get; init; }

    /// <summary>Tokens the bucket has given, ever.</summary>
    public long Taken { get; init; }
}
