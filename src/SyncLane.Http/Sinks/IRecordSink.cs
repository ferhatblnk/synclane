namespace SyncLane.Http;

/// <summary>
/// Where the records of a recipe go. Called once per page with that page's records.
/// </summary>
/// <remarks>
/// A page can be written more than once: when a worker stops between your write and the cursor save, the page runs
/// again. Write with upserts keyed on something in the record, so a repeat changes nothing.
/// </remarks>
public interface IRecordSink
{
    /// <summary>Writes one page of records. Throw to fail the attempt; the job retries from the same page.</summary>
    Task WriteAsync(RecordPage page, CancellationToken cancellationToken);
}
