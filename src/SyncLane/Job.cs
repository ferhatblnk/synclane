namespace SyncLane;

/// <summary>One row of the jobs table. Everything SyncLane knows about a unit of work is on this row.</summary>
public sealed record Job
{
    /// <summary>Row id, assigned by the database.</summary>
    public required long Id { get; init; }

    /// <summary>The queue the job belongs to. Each queue has its own workers and its own settings.</summary>
    public required string Queue { get; init; }

    /// <summary>Who the job works for: one connected account, tenant or store.</summary>
    public required string Owner { get; init; }

    /// <summary>Jobs that share a lane never run at the same time.</summary>
    public required string Lane { get; init; }

    /// <summary>The job type, which selects the handler.</summary>
    public required string Type { get; init; }

    /// <summary>Why the job exists: first load, periodic refresh, or enqueued by hand.</summary>
    public required JobMode Mode { get; init; }

    /// <summary>Where the job is in its life.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Lower runs first. Taken from the order in which the job types were declared.</summary>
    public int Priority { get; init; }

    /// <summary>Start of the range the job covers, or <c>null</c> for a job without a window.</summary>
    public DateTimeOffset? WindowStart { get; init; }

    /// <summary>End of the range the job covers, or <c>null</c> for a job without a window.</summary>
    public DateTimeOffset? WindowEnd { get; init; }

    /// <summary>What makes the job unique for its owner and type. <c>null</c> means it is never treated as a duplicate.</summary>
    public string? DedupKey { get; init; }

    /// <summary>Where the next page starts. <c>null</c> before the first page and after the last one.</summary>
    public string? Cursor { get; init; }

    /// <summary>Records handled so far, summed over every page and every attempt.</summary>
    public long ProcessedCount { get; init; }

    /// <summary>How many times a worker has picked the job up.</summary>
    public int AttemptCount { get; init; }

    /// <summary>The last failure, or <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>The job is not picked up before this moment. Retry delays are written here.</summary>
    public DateTimeOffset NextRunAt { get; init; }

    /// <summary>The worker that holds the job, or <c>null</c>.</summary>
    public string? LeasedBy { get; init; }

    /// <summary>When the lease runs out. A running job past this moment may be taken over by another worker.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>When the row was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a worker first picked the job up.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The last sign of life from the worker that holds the job.</summary>
    public DateTimeOffset? HeartbeatAt { get; init; }

    /// <summary>When the job completed or was abandoned.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}
