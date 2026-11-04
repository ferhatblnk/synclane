namespace SyncLane.Planning;

/// <summary>
/// Turns ranges of history loaded by another system into completed initial jobs. Every range is checked first, and one
/// bad range rejects the whole call, so a migration never ends up half marked.
/// </summary>
internal static class LoadedHistory
{
    public static SyncResult<List<PlannedJob>> Plan(QueueOptions queue, IReadOnlyList<LoadedRange> ranges, DateTimeOffset now)
    {
        var jobs = new List<PlannedJob>(ranges.Count);
        var errors = new List<SyncError>();

        foreach (var range in ranges)
        {
            ArgumentNullException.ThrowIfNull(range);
            ArgumentNullException.ThrowIfNull(range.Owner);

            if (Check(queue, range, now) is { } error)
                errors.Add(error);
            else
                jobs.Add(Loaded(queue.Find(range.Type)!, range));
        }

        return errors.Count == 0 ? SyncResult<List<PlannedJob>>.Success(jobs) : SyncResult<List<PlannedJob>>.Failure(errors);
    }

    private static SyncError? Check(QueueOptions queue, LoadedRange range, DateTimeOffset now)
    {
        if (queue.Find(range.Type) is not { } type)
            return SyncLaneErrors.JobTypeNotDeclared(queue.Name, range.Type);

        if (type.Rule is not WindowedRule rule)
            return SyncLaneErrors.JobTypeNotWindowed(queue.Name, type.Name);

        if (range.Start >= range.End)
            return Invalid("must start before it ends");

        if (range.End > now)
            return Invalid("ends in the future; mark only history that was already loaded");

        // A type without every never plans past its history, so a gap between the range and today would stay empty.
        if (rule.Every is null && range.End < Slots.Day(now))
            return Invalid("ends before today (UTC), and the type has no 'every', so nothing after it would ever be loaded; " +
                           "mark history up to today, or declare 'every' on the type");

        return null;

        SyncError Invalid(string rule) => SyncLaneErrors.LoadedRangeInvalid(queue.Name, range.Type, range.Owner.Key, rule);
    }

    private static PlannedJob Loaded(JobType type, LoadedRange range) => new(
        range.Owner.Key,
        range.Owner.LaneOrKey,
        type.Name,
        JobMode.Initial,
        type.Priority,
        range.Start,
        range.End,
        DedupKeys.Loaded(range.Start, range.End));
}
