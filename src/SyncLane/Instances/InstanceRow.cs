namespace SyncLane.Instances;

/// <summary>
/// One registered instance, as the table holds it. What it runs is <c>null</c> for instances of versions before the
/// switches, which do not write it.
/// </summary>
internal sealed record InstanceRow(
    string Id,
    string Application,
    string Host,
    string Version,
    IReadOnlyList<string> Queues,
    IReadOnlyList<BudgetDescription> Budgets,
    DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset? StoppedAt = null,
    string Role = "",
    bool? RunsWorkers = null,
    bool? RunsPlanner = null,
    bool? Listens = null);
