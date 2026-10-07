using SyncLane.Storage;

namespace SyncLane.Planning;

/// <summary>
/// Plans a queue for a set of owners: one read for what exists, one insert for what is missing.
/// The unique index on the dedup key makes the insert safe when several instances plan at once.
/// </summary>
internal sealed class QueuePlanner(JobStore store, TimeProvider time)
{
    public async Task<int> PlanAsync(QueueOptions queue, IReadOnlyList<JobOwner> owners, CancellationToken cancellationToken)
    {
        if (owners.Count == 0)
            return 0;

        var now = time.GetUtcNow();
        var coverage = await store.GetCoverageAsync(queue.Name, owners.Select(owner => owner.Key).ToHashSet(), cancellationToken);

        var missing = owners
            .SelectMany(owner => JobPlanner.Plan(
                queue,
                owner,
                type => coverage.GetValueOrDefault(new CoverageKey(owner.Key, type), TypeCoverage.None),
                now))
            .ToList();

        return await store.EnqueueAsync(queue.Name, missing, now, cancellationToken);
    }
}
