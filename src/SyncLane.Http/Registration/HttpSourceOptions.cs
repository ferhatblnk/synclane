namespace SyncLane.Http;

/// <summary>Settings and recipes of the HTTP source, given to <see cref="HttpSourceServiceCollectionExtensions.AddSyncLaneHttp"/>.</summary>
public sealed class HttpSourceOptions
{
    private readonly List<HttpRecipeBinding> _bindings = [];
    private readonly List<SyncError> _errors = [];

    /// <summary>How long one request may take, from sending it to reading the last byte. Default 100 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>The largest response read, in bytes. A larger one fails the job. Default 16 MiB.</summary>
    public long MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// The longest wait taken from a <c>Retry-After</c> header. A longer one, by mistake or by design of the other side,
    /// is cut to this. Default one hour.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The most pages one job reads with page or offset paging. Reaching it abandons the job, which stops an API that
    /// ignores the page parameter from being read forever. Default 1,000.
    /// </summary>
    public int MaxPagesPerJob { get; set; } = 1000;

    /// <summary>The <c>User-Agent</c> sent when a recipe sets none. Some APIs refuse requests without one.</summary>
    public string UserAgent { get; set; } = $"SyncLane.Http/{typeof(HttpSourceOptions).Assembly.GetName().Version?.ToString(3)}";

    internal IReadOnlyList<HttpRecipeBinding> Bindings => _bindings;

    /// <summary>Registers a recipe for a job type. Every problem with the recipe is reported when the application starts.</summary>
    /// <param name="jobType">The job type, as declared on the queue with <c>HttpRecipeHandler</c>.</param>
    /// <param name="recipe">The recipe.</param>
    public HttpRecipeBinding Recipe(string jobType, HttpRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ArgumentNullException.ThrowIfNull(recipe);

        var compiled = CompiledRecipe.Compile(recipe);
        if (!compiled.Succeeded)
            _errors.AddRange(compiled.Errors);

        return Bind(jobType, compiled.Value);
    }

    /// <summary>Registers a recipe written as JSON for a job type. Every problem is reported when the application starts.</summary>
    /// <param name="jobType">The job type, as declared on the queue with <c>HttpRecipeHandler</c>.</param>
    /// <param name="json">The recipe as JSON; see docs/recipes.md.</param>
    public HttpRecipeBinding Recipe(string jobType, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ArgumentNullException.ThrowIfNull(json);

        var parsed = HttpRecipe.Parse(json);
        if (!parsed.Succeeded)
        {
            _errors.AddRange(parsed.Errors);
            return Bind(jobType, null);
        }

        return Recipe(jobType, parsed.Value!);
    }

    /// <summary>Every problem with the settings and recipes, in the order found.</summary>
    internal IReadOnlyList<SyncError> Validate()
    {
        var errors = new List<SyncError>(_errors);

        if (Timeout <= TimeSpan.Zero)
            errors.Add(HttpErrors.OptionInvalid(nameof(Timeout), "must be longer than zero"));

        if (MaxResponseBytes < 1024)
            errors.Add(HttpErrors.OptionInvalid(nameof(MaxResponseBytes), "must be at least 1024"));

        if (MaxPagesPerJob < 1)
            errors.Add(HttpErrors.OptionInvalid(nameof(MaxPagesPerJob), "must be at least 1"));

        if (MaxRetryAfter <= TimeSpan.Zero)
            errors.Add(HttpErrors.OptionInvalid(nameof(MaxRetryAfter), "must be longer than zero"));

        if (string.IsNullOrWhiteSpace(UserAgent) || UserAgent.AsSpan().ContainsAny('\r', '\n'))
            errors.Add(HttpErrors.OptionInvalid(nameof(UserAgent), "must be one line of text"));

        errors.AddRange(_bindings
            .GroupBy(binding => binding.JobType, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => HttpErrors.RecipeDeclaredTwice(group.Key)));

        errors.AddRange(_bindings.Where(binding => !binding.HasSink).Select(binding => HttpErrors.SinkMissing(binding.JobType)));

        return errors;
    }

    private HttpRecipeBinding Bind(string jobType, CompiledRecipe? recipe)
    {
        // A recipe with errors still gets a binding, so the chained WriteTo works and every error is reported together.
        var binding = new HttpRecipeBinding(jobType, recipe!);
        _bindings.Add(binding);
        return binding;
    }
}
