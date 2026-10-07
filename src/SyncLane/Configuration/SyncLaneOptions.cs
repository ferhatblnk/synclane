using System.Text.RegularExpressions;

namespace SyncLane;

/// <summary>Settings shared by every queue.</summary>
public sealed partial class SyncLaneOptions
{
    private readonly List<QueueOptions> _queues = [];

    /// <summary>The PostgreSQL database that holds the jobs table.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The schema the jobs table lives in. Lower-case letters, digits and underscores.</summary>
    public string Schema { get; set; } = "synclane";

    /// <summary>Create the schema, table and indexes at start-up when they are missing. Turn it off if you run <c>docs/schema.sql</c> yourself.</summary>
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

    /// <summary>Declares a queue: its job types, its owners and how its workers behave.</summary>
    public SyncLaneOptions AddQueue(string name, Action<QueueOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (_queues.Any(queue => queue.Name == name))
            throw new InvalidOperationException($"Queue '{name}' is declared twice.");

        var queue = new QueueOptions(name);
        configure(queue);
        _queues.Add(queue);
        return this;
    }

    internal QueueOptions Queue(string name) =>
        _queues.FirstOrDefault(queue => queue.Name == name)
        ?? throw new ArgumentException($"Queue '{name}' is not declared.", nameof(name));

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SyncLane needs a ConnectionString.");

        if (!SchemaName().IsMatch(Schema))
            throw new InvalidOperationException("Schema must start with a lower-case letter or underscore and contain only lower-case letters, digits and underscores.");

        if (PlanInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("PlanInterval must be positive.");

        if (PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("PollInterval must be positive.");

        foreach (var queue in _queues)
            queue.Validate();
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}
