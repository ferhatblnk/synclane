using System.Globalization;
using SyncLane.Planning;
using SyncLane.Storage;

namespace SyncLane;

internal sealed class SyncLaneClient(SyncLaneOptions options, JobStore store, QueuePlanner planner, TimeProvider time) : ISyncLane
{
    public async Task<SyncResult<int>> PlanAsync(string queue, JobOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(owner);
        if (options.Find(queue) is not { } declared)
            return SyncResult<int>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<int>.Success(await planner.PlanAsync(declared, [owner], cancellationToken));
    }

    public async Task<SyncResult<long>> EnqueueAsync(NewJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (options.Find(job.Queue) is not { } queue)
            return SyncResult<long>.Failure(SyncLaneErrors.QueueNotDeclared(job.Queue));

        if (queue.Find(job.Type) is not { } type)
            return SyncResult<long>.Failure(SyncLaneErrors.JobTypeNotDeclared(queue.Name, job.Type));

        var planned = new PlannedJob(
            job.Owner.Key, job.Owner.LaneOrKey, type.Name, JobMode.Manual, type.Priority, job.WindowStart, job.WindowEnd, job.DedupKey);

        return await store.EnqueueOneAsync(queue.Name, planned, time.GetUtcNow(), cancellationToken) is { } id
            ? SyncResult<long>.Success(id)
            : SyncResult<long>.Failure(SyncLaneErrors.JobAlreadyExists(queue.Name, job.Owner.Key, type.Name, job.DedupKey));
    }

    public async Task<SyncResult<int>> RequeueAsync(string queue, string? owner = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (options.Find(queue) is not { } declared)
            return SyncResult<int>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<int>.Success(await store.RequeueAsync(declared.Name, owner, time.GetUtcNow(), cancellationToken));
    }

    public async Task<SyncResult<int>> CancelAsync(string queue, string owner, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reason);
        if (options.Find(queue) is not { } declared)
            return SyncResult<int>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<int>.Success(await store.CancelAsync(declared.Name, owner, reason, time.GetUtcNow(), cancellationToken));
    }

    public async Task<SyncListResult<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<Job>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        long? before = null;
        if (query.Cursor is { } cursor)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return SyncListResult<Job>.Failure(SyncLaneErrors.CursorInvalid());

            before = id;
        }

        // One row more than asked tells whether a next page exists without a second query.
        var (jobs, total) = await store.GetJobsAsync(query, before, query.Limit + 1, query.IncludeTotal, cancellationToken);
        if (jobs.Count <= query.Limit)
            return SyncListResult<Job>.Success(jobs, null, total);

        jobs.RemoveAt(jobs.Count - 1);
        return SyncListResult<Job>.Success(jobs, jobs[^1].Id.ToString(CultureInfo.InvariantCulture), total);
    }

    public async Task<SyncResult<OwnerProgress>> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(owner);
        if (options.Find(queue) is not { } declared)
            return SyncResult<OwnerProgress>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<OwnerProgress>.Success(await store.GetProgressAsync(declared.Name, owner, cancellationToken));
    }
}
