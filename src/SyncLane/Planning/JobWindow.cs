namespace SyncLane.Planning;

/// <summary>A range of time one job covers.</summary>
internal readonly record struct JobWindow(DateTimeOffset Start, DateTimeOffset End);
