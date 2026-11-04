namespace SyncLane;

/// <summary>The state of one queue, across every instance that runs it.</summary>
public sealed record QueueOverview
{
    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>Owners that have jobs planned on the queue.</summary>
    public int Owners { get; init; }

    /// <summary>Live instances that run the queue with a planner or workers.</summary>
    public int Instances { get; init; }

    /// <summary>Jobs due now, waiting for a free slot or lane.</summary>
    public int Due { get; init; }

    /// <summary>Jobs waiting without using an attempt, for example for a rate budget.</summary>
    public int Postponed { get; init; }

    /// <summary>Jobs running now.</summary>
    public int Running { get; init; }

    /// <summary>Jobs that failed and wait for their next attempt.</summary>
    public int Retrying { get; init; }

    /// <summary>Jobs that used their attempts and wait for one more with the lane to themselves.</summary>
    public int Deferred { get; init; }

    /// <summary>Jobs given up, waiting for a person: fix the cause, then requeue them.</summary>
    public int Abandoned { get; init; }

    /// <summary>Jobs completed in the last 24 hours.</summary>
    public int CompletedLastDay { get; init; }

    /// <summary>Records of the jobs completed in the last 24 hours.</summary>
    public long RecordsLastDay { get; init; }

    /// <summary>Failed attempts in the last 24 hours.</summary>
    public int FailuresLastDay { get; init; }
}
