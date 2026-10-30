using System.Text.RegularExpressions;

namespace SyncLane;

/// <summary>Settings shared by every queue.</summary>
public sealed partial class SyncLaneOptions
{
    private readonly List<QueueOptions> _queues = [];
    private readonly List<BudgetDescription> _budgets = [];

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

    /// <summary>
    /// The longest a request waits for a token of a rate budget inside a running job. A longer wait gives the job's slot
    /// back, without using an attempt, and the job runs again when the token is due. Default 30 seconds.
    /// </summary>
    public TimeSpan MaxBudgetWait { get; set; } = TimeSpan.FromSeconds(30);

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

    /// <summary>The declared rate budgets. A copy, so reading it can never change a setting.</summary>
    public IReadOnlyList<BudgetDescription> DescribeBudgets() => _budgets.ToList();

    /// <summary>
    /// Declares a rate budget: how fast requests to one account may go, shared by every job and queue that draws from it
    /// with <see cref="RateBudgets"/>. Each key, usually an owner, gets a bucket of its own.
    /// </summary>
    /// <param name="name">The budget's name, used by recipes and handlers and in metrics.</param>
    /// <param name="limit">How many requests per <paramref name="per"/>, at a steady pace.</param>
    /// <param name="per">The period of <paramref name="limit"/>, for example one second or one minute.</param>
    /// <param name="burst">
    /// How many requests may go at once after a quiet spell. Default 1, which spaces requests evenly: the safe choice
    /// when the API's algorithm is not known, and for a window that resets on the clock.
    /// </param>
    public SyncLaneOptions AddBudget(string name, int limit, TimeSpan per, int burst = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_budgets.Any(budget => budget.Name == name))
            throw new SyncLaneException(SyncLaneErrors.BudgetDeclaredTwice(name));

        _budgets.Add(new BudgetDescription(name, limit, per, burst));
        return this;
    }

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

        if (MaxBudgetWait <= TimeSpan.Zero)
            throw new SyncLaneException(SyncLaneErrors.IntervalNotPositive(nameof(MaxBudgetWait)));

        foreach (var budget in _budgets)
        {
            if (!BudgetName().IsMatch(budget.Name))
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "the name must be 1 to 100 letters, digits, dots, underscores, colons, slashes or hyphens"));

            if (budget.Limit < 1)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "limit must be at least 1"));

            if (budget.Per <= TimeSpan.Zero)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "per must be longer than zero"));

            if (budget.Burst < 1)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "burst must be at least 1"));
        }

        foreach (var queue in _queues)
            queue.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex BudgetName();
}
