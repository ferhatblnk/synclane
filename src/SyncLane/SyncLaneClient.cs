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

        if (OwnerValues.Check(owner) is { } invalid)
            return SyncResult<int>.Failure(invalid);

        return SyncResult<int>.Success(await planner.PlanAsync(declared, [owner], cancellationToken));
    }

    public async Task<SyncResult<long>> EnqueueAsync(NewJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Check(job, out var queue, out var planned) is { } invalid)
            return SyncResult<long>.Failure(invalid);

        return await store.EnqueueOneAsync(queue, planned, job.Owner.NameOrNull, job.Owner.Values, time.GetUtcNow(), cancellationToken) is { } id
            ? SyncResult<long>.Success(id)
            : SyncResult<long>.Failure(SyncLaneErrors.JobAlreadyExists(queue, planned.Owner, planned.Type, planned.DedupKey));
    }

    public async Task<SyncResult<EnqueuedJob>> EnqueueOrReplaceAsync(NewJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Check(job, out var queue, out var planned) is { } invalid)
            return SyncResult<EnqueuedJob>.Failure(invalid);

        return await store.EnqueueOrReplaceAsync(queue, planned, job.Owner.NameOrNull, job.Owner.Values, time.GetUtcNow(), cancellationToken) is { } enqueued
            ? SyncResult<EnqueuedJob>.Success(enqueued)
            : SyncResult<EnqueuedJob>.Failure(SyncLaneErrors.JobAlreadyExists(queue, planned.Owner, planned.Type, planned.DedupKey));
    }

    /// <summary>What both ways of enqueueing check before they write, and the row they then write.</summary>
    private SyncError? Check(NewJob job, out string queueName, out PlannedJob planned)
    {
        queueName = job.Queue;
        planned = null!;
        var level = job.Priority ?? job.Owner.Priority;
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(job));

        if (options.Find(job.Queue) is not { } queue)
            return SyncLaneErrors.QueueNotDeclared(job.Queue);

        if (queue.Find(job.Type) is not { } type)
            return SyncLaneErrors.JobTypeNotDeclared(queue.Name, job.Type);

        if (OwnerValues.Check(job.Owner) is { } invalid)
            return invalid;

        if (job.RequestedBy is { } by && (by.Length > NewJobLimits.RequestedBy || by.AsSpan().ContainsAnyInRange('\0', '\u001f')))
            return SyncLaneErrors.RequestedByInvalid(by.Length);

        if (job.Arguments is { } arguments && (arguments.Length > NewJobLimits.Arguments || arguments.Contains('\0')))
            return SyncLaneErrors.ArgumentsInvalid(arguments.Length);

        queueName = queue.Name;
        planned = new PlannedJob(
            job.Owner.Key, job.Owner.LaneOrKey, type.Name, JobMode.Manual, Priorities.Of(level, type.Priority),
            job.WindowStart, job.WindowEnd, job.DedupKey, RequestedBy: job.RequestedBy, Arguments: job.Arguments);
        return null;
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

    public async Task<SyncResult<int>> MarkLoadedAsync(string queue, IReadOnlyList<LoadedRange> ranges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(ranges);
        if (options.Find(queue) is not { } declared)
            return SyncResult<int>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        var now = time.GetUtcNow();
        var loaded = LoadedHistory.Plan(declared, ranges, now);
        if (!loaded.Succeeded)
            return SyncResult<int>.Failure(loaded.Errors);

        return SyncResult<int>.Success(await store.MarkLoadedAsync(declared.Name, loaded.Value!, now, cancellationToken));
    }

    public async Task<SyncResult<int>> ForgetOwnerAsync(string queue, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(owner);
        if (options.Find(queue) is not { } declared)
            return SyncResult<int>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<int>.Success(await store.ForgetOwnerAsync(declared.Name, owner, cancellationToken));
    }

    public async Task<SyncListResult<Job>> GetJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > JobQuery.MaxLimit)
            return SyncListResult<Job>.Failure(SyncLaneErrors.LimitOutOfRange(query.Limit));

        if (query.Search is { } search && !JobQuery.IsUsableSearch(search))
            return SyncListResult<Job>.Failure(SyncLaneErrors.SearchInvalid(search.Length));

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

    public async Task<SyncResult<Job>> GetJobAsync(long id, CancellationToken cancellationToken = default) =>
        await store.FindAsync(id, cancellationToken) is { } job
            ? SyncResult<Job>.Success(job)
            : SyncResult<Job>.Failure(SyncLaneErrors.JobNotFound(id));

    public async Task<SyncResult<OwnerProgress>> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(owner);
        if (options.Find(queue) is not { } declared)
            return SyncResult<OwnerProgress>.Failure(SyncLaneErrors.QueueNotDeclared(queue));

        return SyncResult<OwnerProgress>.Success(await store.GetProgressAsync(declared.Name, owner, cancellationToken));
    }
}

internal static class NewJobLimits
{
    public const int RequestedBy = 200;

    public const int Arguments = 262_144;
}
