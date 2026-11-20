namespace SyncLane;

/// <summary>
/// One owner's jobs on the queues of a <see cref="JobOwnerQuery"/>, counted by status. Only jobs that match the query
/// count. Completed recurring and manual jobs are deleted after <see cref="SyncLaneOptions.KeepCompletedJobs"/> (7 days
/// by default) and initial jobs are kept, so <see cref="Completed"/> and <see cref="Total"/> count the jobs still in the
/// table, not every job the owner ever had; <see cref="ISyncLaneMonitor.GetVolumeAsync"/> counts completions for 400 days.
/// </summary>
public sealed record JobOwnerSummary
{
    /// <summary>The owner's key.</summary>
    public required string Owner { get; init; }

    /// <summary>
    /// The owner's name, as its owner source or an enqueue last gave it on any of the queues. <c>null</c> when none was
    /// given.
    /// </summary>
    public string? OwnerName { get; init; }

    /// <summary>Jobs waiting for a worker, due now or later.</summary>
    public int Pending { get; init; }

    /// <summary>Jobs running now.</summary>
    public int Running { get; init; }

    /// <summary>
    /// Completed jobs still in the table: recurring and manual ones of the last <see cref="SyncLaneOptions.KeepCompletedJobs"/>,
    /// and every initial one.
    /// </summary>
    public int Completed { get; init; }

    /// <summary>Jobs whose last attempt failed, to run again after their delay.</summary>
    public int Failed { get; init; }

    /// <summary>Jobs out of regular attempts, waiting for their one more once nothing else waits in their lane.</summary>
    public int Deferred { get; init; }

    /// <summary>Jobs given up on, until someone requeues them.</summary>
    public int Abandoned { get; init; }

    /// <summary>Every counted job, in any status: the sum of the six counts.</summary>
    public int Total { get; init; }

    /// <summary>
    /// When the owner's newest counted job was created, taken from each queue's most recently inserted matching job: it can
    /// differ from the latest creation time by the clock difference between the processes that created them. Owners are
    /// listed newest first.
    /// </summary>
    public required DateTimeOffset LastJobAt { get; init; }
}
