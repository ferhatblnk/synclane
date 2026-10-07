namespace SyncLane.Tests.Support;

/// <summary>A two-page handler that reports to a <see cref="LaneProbe"/> while it works.</summary>
public sealed class ProbeHandler(LaneProbe probe) : IJobHandler
{
    private const string SecondPage = "2";
    private static readonly TimeSpan PageTime = TimeSpan.FromMilliseconds(15);

    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        probe.Enter(job.Lane);
        try
        {
            await Task.Delay(PageTime, cancellationToken);
        }
        finally
        {
            probe.Leave(job.Lane);
        }

        return new JobPage(job.Cursor is null ? SecondPage : null, 5);
    }
}
