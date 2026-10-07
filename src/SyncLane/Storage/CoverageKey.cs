namespace SyncLane.Storage;

/// <summary>Identifies the coverage of one job type for one owner.</summary>
internal readonly record struct CoverageKey(string Owner, string Type);
