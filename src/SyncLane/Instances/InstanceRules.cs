namespace SyncLane.Instances;

/// <summary>What instances may share, decided from the rows of the live ones. No state, no database.</summary>
internal static class InstanceRules
{
    /// <summary>How often an instance says it is alive.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>An instance silent this long no longer holds its queues: four heartbeats were missed.</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(2);

    /// <summary>An instance silent this long is deleted from the table. Until then it stays visible as gone.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// The queues this instance runs, with its planner or its workers. An instance with neither only calls
    /// <see cref="ISyncLane"/>, takes no job and claims no queue.
    /// </summary>
    public static IReadOnlyList<string> Runs(SyncLaneOptions options, bool workers, bool planner) =>
        planner || workers ? options.Queues.Select(queue => queue.Name).ToList() : [];

    /// <summary>
    /// What this instance declares: one entry per job type, with its queue and handler, whether it runs them or not. A
    /// web instance whose workers are off declares what the worker built from the same code declares.
    /// </summary>
    public static IReadOnlyList<string> Declarations(SyncLaneOptions options) =>
        options.Queues
            .SelectMany(queue => queue.Types.Select(type => $"{queue.Name}/{type.Name}/{type.Handler.FullName ?? type.Handler.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Live instances registered under this one's application that declare none of what it declares: most likely another
    /// application that took the same name. Copies of one application declare the same, and a deploy that adds or removes
    /// a queue still leaves most of it in common. Instances that declare nothing, and those of versions that do not say,
    /// are left out.
    /// </summary>
    public static List<InstanceRow> Strangers(InstanceRow me, IEnumerable<InstanceRow> live)
    {
        if (me.Declares is not { Count: > 0 } mine)
            return [];

        return live
            .Where(other => other.Id != me.Id && other.Application == me.Application && other.StoppedAt is null)
            .Where(other => other.Declares is { Count: > 0 } theirs && !theirs.Intersect(mine, StringComparer.Ordinal).Any())
            .OrderBy(other => other.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One error per queue this instance runs that a live instance of another application runs too. Instances of one
    /// application share their queues: they are copies of the same code.
    /// </summary>
    public static List<SyncError> Conflicts(InstanceIdentity me, IReadOnlyList<string> queues, IEnumerable<InstanceRow> live, DateTimeOffset now)
    {
        var errors = new List<SyncError>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in live.Where(other => other.Application != me.Application).OrderBy(other => other.Id, StringComparer.Ordinal))
        {
            foreach (var queue in queues.Where(queue => other.Queues.Contains(queue) && reported.Add(queue)))
                errors.Add(SyncLaneErrors.QueueRunByAnotherApplication(queue, other.Application, other.Id, now - other.HeartbeatAt, GoneAfter));
        }

        return errors;
    }

    /// <summary>Budgets this instance declares that a live instance declares with another limit, period or burst.</summary>
    public static IEnumerable<(BudgetDescription Mine, BudgetDescription Theirs, InstanceRow Instance)> Disagreements(
        IReadOnlyList<BudgetDescription> mine, IEnumerable<InstanceRow> live) =>
        from other in live
        from theirs in other.Budgets
        join budget in mine on theirs.Name equals budget.Name
        where budget.Limit != theirs.Limit || budget.Per != theirs.Per || budget.Burst != theirs.Burst
        select (budget, theirs, other);
}
