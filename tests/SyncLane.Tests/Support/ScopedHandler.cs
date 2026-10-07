namespace SyncLane.Tests.Support;

/// <summary>A three-page handler that records its own instance, to show that every page gets a new one.</summary>
public sealed class ScopedHandler(ScopeLog log) : IJobHandler
{
    private const int Pages = 3;

    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        log.Instances.Add(this);
        var page = job.Cursor is null ? 1 : int.Parse(job.Cursor);
        return Task.FromResult(new JobPage(page < Pages ? (page + 1).ToString() : null, 1));
    }
}
