namespace SyncLane;

/// <summary>What a handler reports after one page.</summary>
/// <param name="NextCursor">Where the next page starts, or <c>null</c> when the job is finished.</param>
/// <param name="Processed">How many records this page handled.</param>
public readonly record struct JobPage(string? NextCursor, int Processed);
