using System.Collections.Concurrent;

namespace SyncLane.Running;

/// <summary>
/// Wakes idle worker slots of a queue when work arrives on it. A slot takes the signal before it looks for a job, so work
/// that arrives while it looks still wakes it. One signal wakes every slot waiting on the queue; they race for the jobs.
/// </summary>
internal sealed class QueueSignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _next = new(StringComparer.Ordinal);

    public Task Next(string queue) =>
        _next.GetOrAdd(queue, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Pulse(string queue)
    {
        if (_next.TryRemove(queue, out var signal))
            signal.TrySetResult();
    }
}
