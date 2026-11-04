namespace SyncLane;

/// <summary>An operator's temporary limit below a budget's ceiling.</summary>
public sealed record BudgetLimitInfo
{
    /// <summary>The budget.</summary>
    public required string Budget { get; init; }

    /// <summary>The key, or <c>null</c> when the limit holds for every key of the budget.</summary>
    public string? Key { get; init; }

    /// <summary>The limit, in requests a second.</summary>
    public required double Rate { get; init; }

    /// <summary>When it ends by itself, or <c>null</c> until it is cleared.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Who set it.</summary>
    public required string By { get; init; }

    /// <summary>Why, as they wrote it.</summary>
    public required string Reason { get; init; }

    /// <summary>When it was set.</summary>
    public DateTimeOffset SetAt { get; init; }
}
