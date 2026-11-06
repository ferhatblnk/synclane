namespace SyncLane.Planning;

/// <summary>
/// A job that should exist. Inserting it twice creates one row, unless the key is <c>null</c>.
/// <c>Replaces</c> lists abandoned recurring jobs this one takes over; they are deleted when it is inserted.
/// </summary>
internal sealed record PlannedJob(
    string Owner,
    string Lane,
    string Type,
    JobMode Mode,
    int Priority,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    string? DedupKey,
    IReadOnlyList<long>? Replaces = null,
    string? RequestedBy = null);
