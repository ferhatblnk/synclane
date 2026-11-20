namespace SyncLane;

/// <summary>Which owners to show, grouped by owner across the queues that share owner keys, and where to continue.</summary>
public sealed record FreshnessOwnerQuery
{
    /// <summary>
    /// The queues whose owners are grouped: the queues that use the same owner keys for the same accounts, such as one
    /// queue per kind of data of one integration. An owner is identified by its key across exactly these queues. 1 or more
    /// queue names; the read fails with <see cref="SyncErrorCodes.QueuesInvalid"/> without one.
    /// </summary>
    public required IReadOnlyList<string> Queues { get; init; }

    /// <summary>
    /// Only owners whose key, or whose name on any of <see cref="Queues"/>, contains this text, ignoring case. 1 to
    /// <see cref="JobQuery.MaxSearchLength"/> characters.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>Which rows to show, and so which owners: every row, or only those that need a look. Default <see cref="FreshnessShow.All"/>.</summary>
    public FreshnessShow Show { get; init; } = FreshnessShow.All;

    /// <summary>
    /// How old completed data may be before <see cref="FreshnessShow.Stale"/> shows it. Positive. Default 24 hours; set it
    /// only when the queues run less often than daily, so that data that is old by design is not shown as stale.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Owners per page, 1 to <see cref="JobQuery.MaxLimit"/>. Default 50. Each owner comes with all its matching rows.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page, or <c>null</c> for the first.</summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Also count the matching owners into <see cref="SyncListResult{T}.Total"/>, in the same round trip. Ask for it on the
    /// first page of a screen, not on every page.
    /// </summary>
    public bool IncludeTotal { get; init; }
}

/// <summary>Which rows of an owner's freshness to show.</summary>
public enum FreshnessShow
{
    /// <summary>Every row.</summary>
    All,

    /// <summary>
    /// Rows with an open job that failed, was deferred or was abandoned: the rows where something has to be done.
    /// </summary>
    Attention,

    /// <summary>
    /// Rows whose completed data is older than <see cref="FreshnessOwnerQuery.StaleAfter"/>, or that have completed nothing
    /// yet.
    /// </summary>
    Stale,

    /// <summary>Rows held back by a pause of their queue or of their owner on it.</summary>
    Paused
}

/// <summary>One owner and how current its data is on each queue and job type, so a page never cuts an owner in half.</summary>
public sealed record OwnerFreshnessGroup
{
    /// <summary>The owner's key, the same on every queue of the query.</summary>
    public required string Owner { get; init; }

    /// <summary>
    /// The owner's name, as its owner source last gave it. Where the queues give different names, the name on the queue
    /// that sorts first wins. <c>null</c> when none was given.
    /// </summary>
    public string? OwnerName { get; init; }

    /// <summary>
    /// The owner's rows that match the query, oldest data first, each as <see cref="ISyncLaneMonitor.GetFreshnessAsync"/>
    /// returns it.
    /// </summary>
    public required IReadOnlyList<OwnerFreshness> Rows { get; init; }
}
