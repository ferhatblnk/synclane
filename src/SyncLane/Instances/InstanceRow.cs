namespace SyncLane.Instances;

/// <summary>One registered instance, as the table holds it.</summary>
internal sealed record InstanceRow(
    string Id,
    string Application,
    string Host,
    string Version,
    IReadOnlyList<string> Queues,
    IReadOnlyList<BudgetDescription> Budgets,
    DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt);
