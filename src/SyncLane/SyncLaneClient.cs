using SyncLane.Planning;
using SyncLane.Storage;

namespace SyncLane;

internal sealed class SyncLaneClient(SyncLaneOptions options, JobStore store, QueuePlanner planner, TimeProvider time) : ISyncLane
{
    public Task<int> PlanAsync(string queue, JobOwner owner, CancellationToken cancellationToken = default) =>
        planner.PlanAsync(options.Queue(queue), [owner], cancellationToken);

    public async Task<bool> EnqueueAsync(NewJob job, CancellationToken cancellationToken = default)
    {
        var queue = options.Queue(job.Queue);
        var type = queue.Find(job.Type)
            ?? throw new ArgumentException($"Job type '{job.Type}' is not declared on queue '{queue.Name}'.", nameof(job));

        var planned = new PlannedJob(
            job.Owner.Key, job.Owner.LaneOrKey, type.Name, JobMode.Manual, type.Priority, job.WindowStart, job.WindowEnd, job.DedupKey);

        return await store.EnqueueAsync(queue.Name, [planned], time.GetUtcNow(), cancellationToken) == 1;
    }

    public Task<int> RequeueAsync(string queue, string? owner = null, CancellationToken cancellationToken = default) =>
        store.RequeueAsync(options.Queue(queue).Name, owner, time.GetUtcNow(), cancellationToken);

    public Task<int> CancelAsync(string queue, string owner, string reason, CancellationToken cancellationToken = default) =>
        store.CancelAsync(options.Queue(queue).Name, owner, reason, time.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default) =>
        store.GetJobsAsync(query, cancellationToken);

    public Task<OwnerProgress> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default) =>
        store.GetProgressAsync(options.Queue(queue).Name, owner, cancellationToken);
}
