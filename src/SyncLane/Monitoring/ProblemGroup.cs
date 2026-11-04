namespace SyncLane;

/// <summary>Jobs that fail for the same reason: one decision instead of a row each.</summary>
public sealed record ProblemGroup
{
    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The error code, such as <c>SL5106</c>, or the exception's type for errors of your own handlers.</summary>
    public required string Code { get; init; }

    /// <summary>Failing, deferred and abandoned jobs with this code.</summary>
    public int Jobs { get; init; }

    /// <summary>How many of them are abandoned and wait for a requeue.</summary>
    public int Abandoned { get; init; }

    /// <summary>Owners affected.</summary>
    public int Owners { get; init; }

    /// <summary>When the oldest of them last changed.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>When the newest of them last changed.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>The error text of the newest one.</summary>
    public string? Example { get; init; }
}
