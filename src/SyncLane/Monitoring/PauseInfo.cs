namespace SyncLane;

/// <summary>A pause in force: no job of the queue, or of the owner on it, is taken until it is lifted or expires.</summary>
public sealed record PauseInfo
{
    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The owner, or <c>null</c> when the whole queue is paused.</summary>
    public string? Owner { get; init; }

    /// <summary>Who paused it.</summary>
    public required string By { get; init; }

    /// <summary>Why, as they wrote it.</summary>
    public required string Reason { get; init; }

    /// <summary>When it was paused.</summary>
    public DateTimeOffset PausedAt { get; init; }

    /// <summary>When it ends by itself, or <c>null</c> until someone resumes it.</summary>
    public DateTimeOffset? Until { get; init; }
}
