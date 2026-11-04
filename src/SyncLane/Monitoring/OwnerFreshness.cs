namespace SyncLane;

/// <summary>Which owners to show, and where to continue.</summary>
public sealed record FreshnessQuery
{
    /// <summary>Only this queue. <c>null</c> ranks every queue together.</summary>
    public string? Queue { get; init; }

    /// <summary>Only this owner.</summary>
    public string? Owner { get; init; }

    /// <summary>Rows per page, 1 to <see cref="JobQuery.MaxLimit"/>. Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page, or <c>null</c> for the first.</summary>
    public string? Cursor { get; init; }
}

/// <summary>How current one owner's data of one job type is, and why it is not more current.</summary>
public sealed record OwnerFreshness
{
    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The owner.</summary>
    public required string Owner { get; init; }

    /// <summary>The job type.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// How far the owner's completed data reaches: the newest end of a completed window, or, for a type without windows,
    /// the last completion. <c>null</c> when nothing has completed yet.
    /// </summary>
    public DateTimeOffset? DataUntil { get; init; }

    /// <summary>When a job of this owner and type last completed.</summary>
    public DateTimeOffset? LastCompletedAt { get; init; }

    /// <summary>
    /// The status of the open job that matters most: abandoned, then deferred, failed, running, pending. <c>null</c> when
    /// nothing is open, which means the owner is up to date until its next period.
    /// </summary>
    public JobStatus? Status { get; init; }

    /// <summary>The error code of that job, such as <c>SL5106</c>, or its exception type.</summary>
    public string? Code { get; init; }

    /// <summary>The error text of that job.</summary>
    public string? Error { get; init; }

    /// <summary>When that job runs next.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Slices of history planned for the owner and type.</summary>
    public int HistoryTotal { get; init; }

    /// <summary>Slices of history completed.</summary>
    public int HistoryCompleted { get; init; }
}
