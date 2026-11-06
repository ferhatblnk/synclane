using SyncLane.Storage;

namespace SyncLane.Planning;

/// <summary>
/// Plans a queue for a set of owners: one round trip that remembers them and reads what exists, one insert for what is
/// missing.
/// The unique index on the dedup key makes the insert safe when several instances plan at once.
/// </summary>
internal sealed class QueuePlanner(JobStore store, TimeProvider time)
{
    public async Task<int> PlanAsync(QueueOptions queue, IReadOnlyList<JobOwner> owners, CancellationToken cancellationToken)
    {
        if (owners.Count == 0)
            return 0;

        // An owner source that gives values a recipe cannot use fails the round, which the log and the health check show.
        var invalid = owners.Select(OwnerValues.Check).OfType<SyncError>().ToList();
        if (invalid.Count > 0)
            throw new SyncLaneException(invalid);

        var now = time.GetUtcNow();
        var coverage = await store.GetCoverageAsync(queue.Name, owners, now, cancellationToken);

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
