namespace SyncLane.Running;

/// <summary>What happens to a job after a failed attempt.</summary>
internal readonly record struct FailureOutcome(JobStatus Status, DateTimeOffset NextRunAt);
