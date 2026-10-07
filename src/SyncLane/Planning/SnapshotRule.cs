namespace SyncLane.Planning;

/// <summary>One initial job without a window, then one per period.</summary>
internal sealed record SnapshotRule(TimeSpan? Every, TimeSpan Offset) : PlanRule(Every, Offset);
