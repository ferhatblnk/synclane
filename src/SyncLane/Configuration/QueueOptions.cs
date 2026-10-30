using SyncLane.Planning;

namespace SyncLane;

/// <summary>One queue: the job types it runs, who it runs them for, and how its workers behave.</summary>
public sealed class QueueOptions
{
    private readonly List<JobType> _types = [];

    internal QueueOptions(string name) => Name = name;

    /// <summary>The queue name, written to the <c>queue</c> column.</summary>
    public string Name { get; }

    /// <summary>How many jobs of this queue one process runs at once. With 1 the queue is strictly sequential.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>Regular attempts before a job is deferred. A deferred job gets one more.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a worker may stay silent before another worker takes its job over. Renewed while the job runs.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay after the first failure. It doubles with every further attempt.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of the delay after a failure.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Delay after the first <see cref="JobThrottledException"/> that names no wait of its own. It doubles with every further attempt.</summary>
    public TimeSpan ThrottleBaseDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of the delay after a <see cref="JobThrottledException"/>.</summary>
    public TimeSpan ThrottleMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    internal IReadOnlyList<JobType> Types => _types;

    internal Type? OwnerSource { get; private set; }

    /// <summary>Sets who the planner plans for. Without it only <see cref="ISyncLane.PlanAsync"/> creates jobs.</summary>
    public QueueOptions Owners<TSource>() where TSource : class, IOwnerSource
    {
        OwnerSource = typeof(TSource);
        return this;
    }

    /// <summary>
    /// Declares a type that reads everything on each run and has no window.
    /// Every owner gets one initial job, and one more per <paramref name="every"/> after that.
    /// </summary>
    /// <param name="type">The job type, written to the <c>type</c> column.</param>
    /// <param name="every">How often to refresh. Leave it out for a type that is loaded once.</param>
    /// <param name="offset">Shifts the start of each period, for example two hours for a daily job at 02:00 UTC.</param>
    public QueueOptions Snapshot<THandler>(string type, TimeSpan? every = null, TimeSpan offset = default)
        where THandler : class, IJobHandler =>
        Add<THandler>(type, new SnapshotRule(every, offset));

    /// <summary>
    /// Declares a type that reads a range of time on each run.
    /// Every owner gets its history cut into slices, newest first, and one job per <paramref name="every"/> for whatever came after.
    /// </summary>
    /// <param name="type">The job type, written to the <c>type</c> column.</param>
    /// <param name="horizon">How far back the history goes. Raising it later fills only the gap.</param>
    /// <param name="slice">The length of one slice of history. One slice is one job, so this is the largest unit of work that cannot be split.</param>
    /// <param name="every">How often to pick up what is new. Leave it out for a type that only loads history.</param>
    /// <param name="overlap">How far each new window reaches back into the previous one, to cover late arrivals.</param>
    /// <param name="offset">Shifts the start of each period.</param>
    public QueueOptions Windowed<THandler>(
        string type, TimeSpan horizon, TimeSpan slice, TimeSpan? every = null, TimeSpan overlap = default, TimeSpan offset = default)
        where THandler : class, IJobHandler =>
        Add<THandler>(type, new WindowedRule(horizon, slice, every, overlap, offset));

    /// <summary>Declares a type that is never planned and only runs when enqueued by hand.</summary>
    public QueueOptions Handle<THandler>(string type) where THandler : class, IJobHandler =>
        Add<THandler>(type, null);

    internal JobType? Find(string type) => _types.FirstOrDefault(candidate => candidate.Name == type);

    internal void Validate()
    {
        Require(Concurrency >= 1, nameof(Concurrency), "must be at least 1");
        Require(MaxAttempts >= 1, nameof(MaxAttempts), "must be at least 1");
        Require(LeaseDuration >= TimeSpan.FromSeconds(1), nameof(LeaseDuration), "must be at least one second");
        Require(RetryBaseDelay > TimeSpan.Zero && RetryMaxDelay >= RetryBaseDelay, nameof(RetryMaxDelay), "must not be lower than a positive RetryBaseDelay");
        Require(ThrottleBaseDelay > TimeSpan.Zero && ThrottleMaxDelay >= ThrottleBaseDelay, nameof(ThrottleMaxDelay), "must not be lower than a positive ThrottleBaseDelay");

        foreach (var type in _types)
            type.Rule?.Validate(Name, type.Name);
    }

    private QueueOptions Add<THandler>(string type, PlanRule? rule) where THandler : class, IJobHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (Find(type) is not null)
            throw new SyncLaneException(SyncLaneErrors.JobTypeDeclaredTwice(Name, type));

        _types.Add(new JobType(type, _types.Count, typeof(THandler), rule));
        return this;
    }

    private void Require(bool condition, string setting, string rule)
    {
        if (!condition)
            throw new SyncLaneException(SyncLaneErrors.QueueSettingInvalid(Name, setting, rule));
    }
}
