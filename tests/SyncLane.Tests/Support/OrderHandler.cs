namespace SyncLane.Tests.Support;

/// <summary>Stands in for a windowed job type in tests that only need its name.</summary>
public sealed class OrderHandler : IJobHandler
{
    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken) =>
        Task.FromResult(new JobPage(null, 1));
}
