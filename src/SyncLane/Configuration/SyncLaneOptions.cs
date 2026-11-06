using System.Text.RegularExpressions;

namespace SyncLane;

/// <summary>Settings shared by every queue.</summary>
public sealed partial class SyncLaneOptions
{
    private readonly List<QueueOptions> _queues = [];
    private readonly List<BudgetDescription> _budgets = [];

    /// <summary>The PostgreSQL database that holds the jobs table.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// The same database, for the one connection each process keeps open to hear job changes: they wake idle workers at
    /// once and feed <see cref="ISyncLaneMonitor.WatchAsync"/>. Leave it out to listen on <see cref="ConnectionString"/>.
    /// Set it when <see cref="ConnectionString"/> goes through a pooler in transaction mode, which accepts <c>LISTEN</c>
    /// but never delivers what it hears: point it at the server itself, or at a pooler in session mode. Whichever is
    /// used, the connection first checks, on a channel of its own, that it hears a probe sent through
    /// <see cref="ConnectionString"/>. If it does not, it logs a warning and stops listening, and workers look for jobs
    /// every <see cref="PollInterval"/>.
    /// </summary>
    public string? ListenConnectionString { get; set; }

    /// <summary>The connection string the change feed listens on.</summary>
    internal string ListenConnection => string.IsNullOrWhiteSpace(ListenConnectionString) ? ConnectionString : ListenConnectionString;

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

    /// <summary>
    /// The name instances register under. Instances of one application share their queues; a live instance of another
    /// application that runs one of them stops this one at start-up. Defaults to the host's application name, which is
    /// the entry assembly's name unless the host sets another.
    /// </summary>
    public string? Application { get; set; }

    /// <summary>
    /// How long completed recurring and manual jobs stay in the table before instances that run the planner delete them.
    /// Initial jobs, and jobs that have not completed, are never deleted. Default 7 days; <c>null</c> keeps every job.
    /// </summary>
    public TimeSpan? KeepCompletedJobs { get; set; } = TimeSpan.FromDays(7);

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
    /// with <see cref="RateBudgets"/>. Each key, usually an owner, gets a bucket of its own. The limit is a ceiling: when
    /// the API answers "slow down", the bucket's rate drops below it and climbs back while requests go through.
    /// </summary>
    /// <param name="name">The budget's name, used by recipes and handlers and in metrics.</param>
    /// <param name="limit">How many requests per <paramref name="per"/>, at a steady pace.</param>
    /// <param name="per">The period of <paramref name="limit"/>, for example one second or one minute.</param>
    /// <param name="burst">
    /// How many requests may go at once after a quiet spell. Default 1, which spaces requests evenly: the safe choice
    /// when the API's algorithm is not known, and for a window that resets on the clock.
    /// </param>
    /// <param name="start">
    /// The share of <paramref name="limit"/> a new bucket starts at, from 0.01 to 1. Default 1, for a limit the API
    /// documents. Lower it for a limit nobody publishes: declare a generous ceiling, start at a quarter of it, and the
    /// rate climbs while the API accepts and backs off when it refuses. It never goes above the limit.
    /// </param>
    public SyncLaneOptions AddBudget(string name, int limit, TimeSpan per, int burst = 1, double start = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_budgets.Any(budget => budget.Name == name))
            throw new SyncLaneException(SyncLaneErrors.BudgetDeclaredTwice(name));

        _budgets.Add(new BudgetDescription(name, limit, per, burst, start));
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

    /// <summary>The declared budget called <paramref name="name"/>, or <c>null</c>.</summary>
    internal BudgetDescription? FindBudget(string name) => _budgets.FirstOrDefault(budget => budget.Name == name);

    /// <summary>Replaces a declared budget with the same name, for settings that come from configuration.</summary>
    internal void Override(BudgetDescription budget) => _budgets[_budgets.FindIndex(declared => declared.Name == budget.Name)] = budget;

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

        if (Application is not null && !IsValidName(Application))
            throw new SyncLaneException(SyncLaneErrors.ApplicationNameInvalid(Application));

        if (KeepCompletedJobs <= TimeSpan.Zero)
            throw new SyncLaneException(SyncLaneErrors.IntervalNotPositive(nameof(KeepCompletedJobs)));

        foreach (var budget in _budgets)
        {
            if (!IsValidName(budget.Name))
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "the name must be 1 to 100 letters, digits, dots, underscores, colons, slashes or hyphens"));

            if (budget.Limit < 1)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "limit must be at least 1"));

            if (budget.Per <= TimeSpan.Zero)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "per must be longer than zero"));

            if (budget.Burst < 1)
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "burst must be at least 1"));

            if (!(budget.Start is >= 0.01 and <= 1))
                throw new SyncLaneException(SyncLaneErrors.BudgetSettingInvalid(budget.Name, "start must be from 0.01 to 1"));
        }

        foreach (var queue in _queues)
            queue.Validate();
    }

    /// <summary>A budget or application name: 1 to 100 letters, digits, dots, underscores, colons, slashes or hyphens.</summary>
    internal static bool IsValidName(string name) => Name().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex Name();
}
