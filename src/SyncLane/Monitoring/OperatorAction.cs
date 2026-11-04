namespace SyncLane;

/// <summary>Which operator actions to list, and where to continue.</summary>
public sealed record ActionQuery
{
    /// <summary>Only actions on this queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Only actions on this owner.</summary>
    public string? Owner { get; init; }

    /// <summary>Rows per page, 1 to <see cref="JobQuery.MaxLimit"/>. Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="SyncListResult{T}.NextCursor"/> of the previous page, or <c>null</c> for the newest.</summary>
    public string? Cursor { get; init; }
}

/// <summary>Something an operator did through the monitor, and who did it. Kept for 400 days.</summary>
public sealed record OperatorAction
{
    /// <summary>The action's id; newer actions have larger ids.</summary>
    public required long Id { get; init; }

    /// <summary>When it was done.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Who did it, as the caller named them.</summary>
    public required string By { get; init; }

    /// <summary>What was done: pause, resume, requeue job, restart job, requeue by code, prioritize owner.</summary>
    public required string Action { get; init; }

    /// <summary>The queue it was done to.</summary>
    public string? Queue { get; init; }

    /// <summary>The owner it was done to, if one.</summary>
    public string? Owner { get; init; }

    /// <summary>What else names the target: a job id, an error code, the end of a pause.</summary>
    public string? Target { get; init; }

    /// <summary>Why, as they wrote it.</summary>
    public string? Reason { get; init; }

    /// <summary>How many jobs it changed.</summary>
    public int Changed { get; init; }
}
