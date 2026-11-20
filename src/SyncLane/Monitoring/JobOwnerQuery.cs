namespace SyncLane;

/// <summary>
/// Whose jobs to summarise, and where to continue. An owner is known by its key on each queue, so the query names the
/// queues whose owners share keys, and each owner's jobs on all of them are counted together.
/// </summary>
public sealed record JobOwnerQuery
{
    /// <summary>
    /// The queues to read, one or more: those whose owner keys mean the same account, such as the orders and the stock
    /// queue of one integration. Owners with the same key on these queues are one row. Required.
    /// </summary>
    public IReadOnlyList<string>? Queues { get; init; }

    /// <summary>Only jobs in this status: the counts, the newest job and which owners are listed all follow it.</summary>
    public JobStatus? Status { get; init; }

    /// <summary>Only jobs of this type.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// Only owners whose key or name, on any of the queues, contains this text, ignoring case. 1 to
    /// <see cref="JobQuery.MaxSearchLength"/> characters.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>Owners per page, 1 to <see cref="JobQuery.MaxLimit"/>. Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page, or <c>null</c> for the first.</summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Also count the matching owners into <see cref="SyncListResult{T}.Total"/>, to show "page 2 of 7". It costs about
    /// as much as a page, so ask for it on the first page of a screen, not on every page.
    /// </summary>
    public bool IncludeTotal { get; init; }
}
