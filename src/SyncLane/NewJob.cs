namespace SyncLane;

/// <summary>A job to enqueue by hand, outside the plan.</summary>
public sealed record NewJob
{
    /// <summary>The queue to put the job in.</summary>
    public required string Queue { get; init; }

    /// <summary>Who the job works for.</summary>
    public required JobOwner Owner { get; init; }

    /// <summary>The job type. It must be declared on the queue.</summary>
    public required string Type { get; init; }

    /// <summary>Start of the range to cover, if the job has one.</summary>
    public DateTimeOffset? WindowStart { get; init; }

    /// <summary>End of the range to cover, if the job has one.</summary>
    public DateTimeOffset? WindowEnd { get; init; }

    /// <summary>
    /// How soon this job runs compared with the other due jobs of its queue. Leave it <c>null</c> to use the owner's
    /// <see cref="JobOwner.Priority"/>.
    /// </summary>
    public JobPriority? Priority { get; init; }

    /// <summary>
    /// Enqueueing the same key twice for the same owner and type creates one job.
    /// Leave it <c>null</c> to always create a new one.
    /// </summary>
    public string? DedupKey { get; init; }
}
