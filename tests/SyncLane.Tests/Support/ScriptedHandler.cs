namespace SyncLane.Tests.Support;

/// <summary>A handler whose behaviour the test supplies. It remembers the cursor of every page it was asked for.</summary>
public sealed class ScriptedHandler(Func<Job, CancellationToken, Task<JobPage>> run) : IJobHandler
{
    public List<string?> Cursors { get; } = [];

    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        Cursors.Add(job.Cursor);
        return run(job, cancellationToken);
    }
}
