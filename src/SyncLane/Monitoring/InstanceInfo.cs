namespace SyncLane;

/// <summary>One process registered in the schema.</summary>
public sealed record InstanceInfo
{
    /// <summary>
    /// The instance: host, process id and a random part, unique even for containers that share a hostname. Worker names
    /// in <see cref="Job.LeasedBy"/> start with it.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The application it belongs to.</summary>
    public required string Application { get; init; }

    /// <summary>What this copy of the application does, from <see cref="SyncLaneOptions.Role"/>; empty when it sets none.</summary>
    public string Role { get; init; } = "";

    /// <summary>Whether it runs workers. <c>null</c> for an instance of a version before 0.7, which does not say.</summary>
    public bool? RunsWorkers { get; init; }

    /// <summary>Whether it runs the planner. <c>null</c> for an instance of a version before 0.7, which does not say.</summary>
    public bool? RunsPlanner { get; init; }

    /// <summary>
    /// Whether it listens for job changes, as its settings say; whether the listening connection is up is the health
    /// check's answer. <c>null</c> for an instance of a version before 0.7, which does not say.
    /// </summary>
    public bool? ListensForChanges { get; init; }

    /// <summary>
    /// Ids of live instances registered under the same application that declare none of the queues and job types this
    /// one declares: most likely another application that took the same name, so one switch turns both on and off and
    /// they look like one here. Give one of them a name of its own with <see cref="SyncLaneOptions.Application"/>. Empty
    /// when there are none, for an instance that is gone or declares no queue, and for instances of earlier versions,
    /// which do not say what they declare.
    /// </summary>
    public IReadOnlyList<string> SharesNameWith { get; init; } = [];

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
