namespace SyncLane;

/// <summary>Who a job works for: one connected account, tenant or store.</summary>
/// <param name="Key">Stable identifier of the owner.</param>
/// <param name="Lane">
/// The lane the owner's jobs run in. Owners that share a rate limit should share a lane.
/// When it is left out, the owner has a lane of its own.
/// </param>
/// <param name="Name">
/// What people call the owner, for example a company and its store, shown and searched by the monitor. It may change at
/// any time; the key may not. Leave it out to keep the name last given. It is the owner's data: SyncLane stores it with
/// the jobs and never writes it to a log, a trace or a metric.
/// </param>
public sealed record JobOwner(string Key, string? Lane = null, string? Name = null)
{
    /// <summary>
    /// How soon the owner's jobs run compared with other owners' in the queue, for example <see cref="JobPriority.High"/>
    /// for a new customer whose history should load first. It applies to jobs created from now on; jobs already waiting
    /// keep theirs. A job enqueued with its own <see cref="NewJob.Priority"/> uses that instead.
    /// </summary>
    public JobPriority Priority { get; init; } = JobPriority.Normal;

    internal string LaneOrKey => string.IsNullOrEmpty(Lane) ? Key : Lane;

    internal string? NameOrNull => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
}
