namespace SyncLane.AspNetCore;

/// <summary>
/// A job as the endpoints show it. Its cursor is left out: it is the other side's paging state and can hold a token.
/// </summary>
public sealed record JobView
{
    /// <summary>The job.</summary>
    public required long Id { get; init; }

    /// <summary>Its queue.</summary>
    public required string Queue { get; init; }

    /// <summary>Its owner.</summary>
    public required string Owner { get; init; }

    /// <summary>The owner's name, when its owner source or the enqueue gave one.</summary>
    public string? OwnerName { get; init; }

    /// <summary>Its lane.</summary>
    public required string Lane { get; init; }

    /// <summary>Its job type.</summary>
    public required string Type { get; init; }

    /// <summary>Initial, recurring or manual.</summary>
    public required JobMode Mode { get; init; }

    /// <summary>Its status.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>The stored order, lower first.</summary>
    public int Priority { get; init; }

    /// <summary>The level it was created with.</summary>
    public required JobPriority PriorityLevel { get; init; }

    /// <summary>Whether an operator moved its owner forward.</summary>
    public bool Prioritized { get; init; }

    /// <summary>What makes it unique for its owner and type.</summary>
    public string? DedupKey { get; init; }

    /// <summary>The start of the range it covers.</summary>
    public DateTimeOffset? WindowStart { get; init; }

    /// <summary>The end of the range it covers.</summary>
    public DateTimeOffset? WindowEnd { get; init; }

    /// <summary>Records handled so far.</summary>
    public long ProcessedCount { get; init; }

    /// <summary>Attempts since it last saved a page.</summary>
    public int AttemptCount { get; init; }

    /// <summary>The last failure.</summary>
    public string? Error { get; init; }

    /// <summary>When it runs next.</summary>
    public DateTimeOffset NextRunAt { get; init; }

    /// <summary>The worker that holds it.</summary>
    public string? LeasedBy { get; init; }

    /// <summary>When its worker last said it is still at it.</summary>
    public DateTimeOffset? HeartbeatAt { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it first started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it completed or was given up.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    internal static JobView Of(Job job) => new()
    {
        Id = job.Id,
        Queue = job.Queue,
        Owner = job.Owner,
        OwnerName = job.OwnerName,
        Lane = job.Lane,
        Type = job.Type,
        Mode = job.Mode,
        Status = job.Status,
        Priority = job.Priority,
        PriorityLevel = job.PriorityLevel,
        Prioritized = job.Prioritized,
        DedupKey = job.DedupKey,
        WindowStart = job.WindowStart,
        WindowEnd = job.WindowEnd,
        ProcessedCount = job.ProcessedCount,
        AttemptCount = job.AttemptCount,
        Error = job.Error,
        NextRunAt = job.NextRunAt,
        LeasedBy = job.LeasedBy,
        HeartbeatAt = job.HeartbeatAt,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt
    };
}

/// <summary>One page of a list. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next one; it is <c>null</c> on the last.</summary>
public sealed record PageOf<T>(IReadOnlyList<T> Items, string? NextCursor);
