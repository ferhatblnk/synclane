namespace SyncLane;

/// <summary>Does the work of one job type, one page at a time.</summary>
public interface IJobHandler
{
    /// <summary>
    /// Handles the page that starts at <see cref="Job.Cursor"/> and returns where the next one starts.
    /// The cursor is saved after every page, so a job that stops for any reason continues from its last page.
    /// That page may run twice; make its writes idempotent.
    /// </summary>
    Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken);
}
