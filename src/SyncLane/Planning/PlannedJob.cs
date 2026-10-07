namespace SyncLane.Planning;

/// <summary>A job that should exist. Inserting it twice creates one row, unless the key is <c>null</c>.</summary>
internal sealed record PlannedJob(
    string Owner,
    string Lane,
    string Type,
    JobMode Mode,
    int Priority,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    string? DedupKey);
