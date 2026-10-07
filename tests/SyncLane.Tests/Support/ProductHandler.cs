namespace SyncLane.Tests.Support;

/// <summary>Stands in for a snapshot job type in tests that only need its name.</summary>
public sealed class ProductHandler : IJobHandler
{
    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken) =>
        Task.FromResult(new JobPage(null, 1));
}
