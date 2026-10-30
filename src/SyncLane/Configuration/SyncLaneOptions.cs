namespace SyncLane;

/// <summary>Settings shared by every queue.</summary>
public sealed class SyncLaneOptions
{
    private readonly List<QueueOptions> _queues = [];

    /// <summary>The PostgreSQL database that holds the jobs table.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The schema the jobs table lives in. Lower-case letters, digits and underscores.</summary>
    public string Schema { get; set; } = "synclane";

    /// <summary>
    /// Create the schema and apply missing migrations at start-up. Turn it off if you apply migrations yourself with
    /// <see cref="SyncLaneSchema.Script"/>; SyncLane then only checks at start-up that they are applied.
    /// </summary>
    public bool CreateSchema { get; set; } = true;

    /// <summary>Run the planner in this process.</summary>
    public bool RunPlanner { get; set; } = true;

    /// <summary>Run the workers in this process. Turn it off in instances that only enqueue and read.</summary>
    public bool RunWorkers { get; set; } = true;

    /// <summary>How often the planner checks what is missing.</summary>
    public TimeSpan PlanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long an idle worker waits before it looks for work again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal IReadOnlyList<QueueOptions> Queues => _queues;

    /// <summary>
    /// What has been declared: every queue and its job types with their handlers. A copy, so reading it can never change
    /// a setting. Packages use it to check at start-up that what they need is declared.
    /// </summary>
    public IReadOnlyList<QueueDescription> Describe() =>
        _queues
            .Select(queue => new QueueDescription(
                queue.Name,
                queue.Types.Select(type => new JobTypeDescription(type.Name, type.Handler)).ToList()))
            .ToList();

    /// <summary>Declares a queue: its job types, its owners and how its workers behave.</summary>
    public SyncLaneOptions AddQueue(string name, Action<QueueOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (_queues.Any(queue => queue.Name == name))
            throw new SyncLaneException(SyncLaneErrors.QueueDeclaredTwice(name));

        var queue = new QueueOptions(name);
        configure(queue);
        _queues.Add(queue);
        return this;
    }

    /// <summary>The declared queue called <paramref name="name"/>, or <c>null</c>.</summary>
    internal QueueOptions? Find(string name) => _queues.FirstOrDefault(queue => queue.Name == name);

    /// <summary>The declared queue called <paramref name="name"/>; for code that has already checked it exists.</summary>
    internal QueueOptions Queue(string name) => Find(name) ?? throw new SyncLaneException(SyncLaneErrors.QueueNotDeclared(name));

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new SyncLaneException(SyncLaneErrors.ConnectionStringMissing());

        if (!SchemaName.IsValid(Schema))
            throw new SyncLaneException(SyncLaneErrors.SchemaNameInvalid(Schema));

        if (PlanInterval <= TimeSpan.Zero)
            throw new SyncLaneException(SyncLaneErrors.IntervalNotPositive(nameof(PlanInterval)));

        if (PollInterval <= TimeSpan.Zero)
            throw new SyncLaneException(SyncLaneErrors.IntervalNotPositive(nameof(PollInterval)));

        foreach (var queue in _queues)
            queue.Validate();
    }
}
