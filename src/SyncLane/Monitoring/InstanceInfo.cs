namespace SyncLane;

/// <summary>One process registered in the schema.</summary>
public sealed record InstanceInfo
{
    /// <summary>The instance: host and process id. Worker names in <see cref="Job.LeasedBy"/> start with it.</summary>
    public required string Id { get; init; }

    /// <summary>The application it belongs to.</summary>
    public required string Application { get; init; }

    /// <summary>The machine it runs on.</summary>
    public required string Host { get; init; }

    /// <summary>The SyncLane version it runs.</summary>
    public required string Version { get; init; }

    /// <summary>The queues it runs with its planner or workers. Empty for an instance that only calls the API.</summary>
    public required IReadOnlyList<string> Queues { get; init; }

    /// <summary>The rate budgets it declares.</summary>
    public required IReadOnlyList<BudgetDescription> Budgets { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When it last said it is alive.</summary>
    public DateTimeOffset LastHeartbeatAt { get; init; }

    /// <summary>
    /// When it stopped gracefully. <c>null</c> while it runs, and for an instance that died without stopping: that one is
    /// recognised by its last heartbeat. Either kind is listed for a day after its last heartbeat.
    /// </summary>
    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>It has not stopped and said it is alive within the last two minutes.</summary>
    public bool IsAlive { get; init; }
}
