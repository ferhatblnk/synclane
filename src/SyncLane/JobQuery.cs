namespace SyncLane;

/// <summary>Which jobs to list. Every filter is optional.</summary>
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

    /// <summary>How many jobs to return, newest first.</summary>
    public int Limit { get; init; } = 100;
}
