namespace SyncLane.Storage;

/// <summary>
/// One step of the schema. Steps are only ever added, never edited, and each one must be safe to run twice, so that a
/// script applied by hand and the start-up migration can meet on the same database.
/// </summary>
internal sealed record Migration(int Version, string Name, Func<string, string> Sql);
