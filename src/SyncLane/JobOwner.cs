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

    /// <summary>
    /// Values of this owner that a recipe puts into its requests as <c>{{owner.&lt;name&gt;}}</c>, such as a marketplace,
    /// a region or a store's host. They are not secrets: they may land in a URL and a saved cursor, so credentials go in
    /// the credential store instead. Leave them out to keep the values last given. Names are letters, digits and
    /// underscores, starting with a letter; <c>key</c> is taken. Like the name, they are the owner's data: SyncLane
    /// stores them with the owner and never writes them to a log, a trace or a metric.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }

    internal string LaneOrKey => string.IsNullOrEmpty(Lane) ? Key : Lane;

    internal string? NameOrNull => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
}
