namespace SyncLane.Http;

/// <summary>
/// Where the records of a recipe go. Called once per page with that page's records.
/// </summary>
/// <remarks>
/// A page can be written more than once: when a worker stops between your write and the cursor save, the page runs
/// again. Write with upserts keyed on something in the record, so a repeat changes nothing; <see cref="RecordIdentity"/>
/// reads keys and versions and removes duplicates within the page for you. For PostgreSQL or a service of yours, use
/// <see cref="HttpRecipeBinding.WriteToPostgres"/> or <see cref="HttpRecipeBinding.WriteToHttp"/> instead of writing a sink.
/// </remarks>
public interface IRecordSink
{
    /// <summary>Writes one page of records. Throw to fail the attempt; the job retries from the same page.</summary>
    Task WriteAsync(RecordPage page, CancellationToken cancellationToken);
}
