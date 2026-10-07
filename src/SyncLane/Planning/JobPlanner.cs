namespace SyncLane.Planning;

/// <summary>
/// Compares the jobs an owner should have with the jobs it has and returns what is missing.
/// It has no state and no early exit, so it can be called any number of times with the same result.
/// </summary>
internal static class JobPlanner
{
    public static List<PlannedJob> Plan(QueueOptions queue, JobOwner owner, Func<string, TypeCoverage> coverageOf, DateTimeOffset now)
    {
        var missing = new List<PlannedJob>();

        foreach (var type in queue.Types)
        {
            var seen = coverageOf(type.Name);

            if (type.Rule is SnapshotRule snapshot)
                PlanSnapshot(missing, owner, type, snapshot, seen, now);
            else if (type.Rule is WindowedRule windowed)
                PlanWindowed(missing, owner, type, windowed, seen, now);
        }

        return missing;
    }

    private static void PlanSnapshot(List<PlannedJob> missing, JobOwner owner, JobType type, SnapshotRule rule, TypeCoverage seen, DateTimeOffset now)
    {
        var outstanding = seen.AnyOutstanding;

        if (!seen.HasInitial)
        {
            missing.Add(Planned(owner, type, JobMode.Initial, null, DedupKeys.InitialSnapshot));
            outstanding = true;
        }

        if (rule.Every is not { } every || outstanding)
            return;

        var period = Slots.Period(now, every, rule.Offset);
        if (IsBefore(seen.LastPlannedAt, period))
            missing.Add(Planned(owner, type, JobMode.Recurring, null, DedupKeys.Recurring(period)));
    }

    private static void PlanWindowed(List<PlannedJob> missing, JobOwner owner, JobType type, WindowedRule rule, TypeCoverage seen, DateTimeOffset now)
    {
        var today = Slots.Day(now);
        var coveredTo = seen.CoveredTo;

        foreach (var window in Slots.Backfill(seen.InitialFrom ?? today, today - rule.Horizon, rule.Slice))
        {
            missing.Add(Planned(owner, type, JobMode.Initial, window, DedupKeys.InitialWindow(window.Start)));
            if (coveredTo is null || window.End > coveredTo)
                coveredTo = window.End;
        }

        if (rule.Every is not { } every || seen.RecurringOutstanding)
            return;

        var period = Slots.Period(now, every, rule.Offset);
        var from = coveredTo ?? now - every;
        var until = Slots.WholeSeconds(now);

        if (IsBefore(seen.LastRecurringAt, period) && until > from)
            missing.Add(Planned(owner, type, JobMode.Recurring, new JobWindow(from - rule.Overlap, until), DedupKeys.Recurring(period)));
    }

    private static bool IsBefore(DateTimeOffset? last, DateTimeOffset period) =>
        last is null || last < period;

    private static PlannedJob Planned(JobOwner owner, JobType type, JobMode mode, JobWindow? window, string key) =>
        new(owner.Key, owner.LaneOrKey, type.Name, mode, type.Priority, window?.Start, window?.End, key);
}
