namespace SyncLane.Tests.Support;

/// <summary>A handler that finishes one page and then waits until it is cancelled.</summary>
public sealed class BlockingHandler(TaskCompletionSource started) : IJobHandler
{
    public const string SecondPage = "2";

    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.Cursor is null)
            return new JobPage(SecondPage, 10);

        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new JobPage(null, 0);
    }
}
