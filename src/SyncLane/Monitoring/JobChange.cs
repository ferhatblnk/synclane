namespace SyncLane;

/// <summary>A job that was created or changed status, as it was announced after its transaction committed.</summary>
public sealed record JobChange
{
    /// <summary>The job.</summary>
    public required long Id { get; init; }

    /// <summary>Its queue.</summary>
    public required string Queue { get; init; }

    /// <summary>Its owner. Very long owner keys are cut at 500 characters.</summary>
    public required string Owner { get; init; }

    /// <summary>Its job type.</summary>
    public required string Type { get; init; }

    /// <summary>The status it now has.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>When the change was written.</summary>
    public required DateTimeOffset At { get; init; }
}
