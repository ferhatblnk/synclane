namespace SyncLane;

/// <summary>Which jobs to list, and which page. Every filter is optional.</summary>
public sealed record JobQuery
{
    /// <summary>The largest page <see cref="ISyncLane.GetJobsAsync"/> returns.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Only jobs of this queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Only jobs of this owner.</summary>
    public string? Owner { get; init; }

    /// <summary>Only jobs in this status.</summary>
    public JobStatus? Status { get; init; }

    /// <summary>How many jobs to return, from 1 to <see cref="MaxLimit"/>.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page. Leave it out to start from the newest job.</summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Also count every matching job into <see cref="SyncListResult{T}.Total"/>. It costs a count over the table, so
    /// ask for it on the first page of a screen, not on every page.
    /// </summary>
    public bool IncludeTotal { get; init; }
}
