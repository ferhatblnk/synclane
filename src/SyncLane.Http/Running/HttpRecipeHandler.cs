using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SyncLane.Http;

/// <summary>
/// Runs a job from its recipe: builds the request, authenticates it, sends it, finds the records and hands them to the
/// recipe's sink. Declare job types with it, for example <c>queue.Windowed&lt;HttpRecipeHandler&gt;("orders", ...)</c>,
/// and register their recipes with <c>AddSyncLaneHttp</c>.
/// </summary>
/// <remarks>
/// With a rate budget, every request first waits for a token, and the headers the recipe names tell the budget how many
/// requests are left. How answers become job outcomes: 2xx is a page; 429 pauses the recipe's budget for everyone on the
/// key and waits, honouring <c>Retry-After</c>, or the recipe's reset header without one; a 403 with <c>Retry-After</c>,
/// or whose remaining header says zero, is the same "slow down"; 401 renews an OAuth 2.0 token once, and otherwise
/// abandons the job, as any other 403 does, because retrying with the same credentials cannot help;
/// 408, 5xx, timeouts, network errors and broken JSON retry with the queue's growing delay; redirects and other 4xx
/// abandon. No message ever contains a response body, a header value, a credential or a URL.
/// </remarks>
public sealed class HttpRecipeHandler : IJobHandler
{
    private static readonly TimeSpan MinResetWait = TimeSpan.FromSeconds(1);

    private readonly RecipeRegistry _recipes;
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;
    private readonly HttpSourceOptions _options;
    private readonly TimeProvider _time;

    internal HttpRecipeHandler(RecipeRegistry recipes, HttpClient client, IServiceProvider services, HttpSourceOptions options, TimeProvider time)
    {
        _recipes = recipes;
        _client = client;
        _services = services;
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_recipes.TryGet(job.Type, out var binding))
            throw new JobAbandonedException(HttpErrors.RecipeMissing(job.Queue, job.Type).ToString());

        var recipe = binding.Recipe;
        var pageRequest = recipe.Paginator.Request(job.Cursor, _options.MaxPagesPerJob);
        if (!pageRequest.Succeeded)
            throw new JobAbandonedException(string.Join("; ", pageRequest.Errors));

        var auth = recipe.Auth is { } scheme ? await AuthenticateAsync(recipe, scheme, job, cancellationToken) : null;
        var budget = recipe.Rate is { } rate ? new Budget(_services.GetRequiredService<RateBudgets>(), rate.Budget, rate.KeyOf(job), rate.Headers) : null;
        using var answer = await FetchAsync(recipe, job, pageRequest.Value, auth, budget, cancellationToken);

        var records = recipe.Records.Find(answer.Document.RootElement, recipe.Name);
        if (!records.Succeeded)
            throw new SyncLaneException(records.Errors);

        // The next cursor is worked out before the sink runs, so a pagination that cannot go on writes nothing.
        var next = recipe.Paginator.Next(job.Cursor, new PageAnswer(answer.Document.RootElement, records.Value, answer.Links, answer.Url));
        if (!next.Succeeded)
            throw new JobAbandonedException(string.Join("; ", next.Errors));

        var page = new RecordPage(job, recipe.Name, records.Value);
        await WriteAsync(binding, page, cancellationToken);

        return new JobPage(next.Value, page.Count);
    }

    /// <summary>The owner's credential for this page, or an abandoned job when the store has none.</summary>
    private async Task<AuthContext> AuthenticateAsync(CompiledRecipe recipe, RecipeAuth scheme, Job job, CancellationToken cancellationToken)
    {
        var store = _services.GetRequiredService<ICredentialStore>();
        var key = new CredentialKey(job.Owner, scheme.Credential);
        var credential = await store.GetAsync(key, cancellationToken)
            ?? throw new JobAbandonedException(HttpErrors.CredentialMissing(recipe.Name, scheme.Credential).ToString());

        return new AuthContext(key, credential, store, _services);
    }

    /// <summary>
    /// Sends the page's request and returns its answer, or throws the exception that gives the right outcome. A token
    /// the API rejects with 401 is renewed and the request sent once more, unless the token was fetched just now.
    /// </summary>
    private async Task<Answer> FetchAsync(
        CompiledRecipe recipe, Job job, PageRequest page, AuthContext? auth, Budget? budget, CancellationToken cancellationToken)
    {
        var (first, stamp) = await PrepareAsync(recipe, job, page, auth, rejected: null, cancellationToken);
        using (first)
        {
            var renewable = recipe.Auth is { Renews: true } && !stamp.Fresh;
            if (await SendAsync(recipe, first, renewable, budget, cancellationToken) is { } answer)
                return answer;
        }

        var (second, _) = await PrepareAsync(recipe, job, page, auth, stamp.Token, cancellationToken);
        using (second)
            return await SendAsync(recipe, second, renewable: false, budget, cancellationToken) ?? throw new UnreachableException();
    }

    /// <summary>The request for the page, with its credential.</summary>
    private async Task<(HttpRequestMessage Request, AuthStamp Stamp)> PrepareAsync(
        CompiledRecipe recipe, Job job, PageRequest page, AuthContext? auth, string? rejected, CancellationToken cancellationToken)
    {
        var rendered = RequestRenderer.Render(recipe, job, page);
        if (!rendered.Succeeded)
            throw new JobAbandonedException(string.Join("; ", rendered.Errors));

        var request = rendered.Value!;
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        if (auth is null)
            return (request, AuthStamp.None);

        try
        {
            return (request, await recipe.Auth!.ApplyAsync(request, auth, rejected, cancellationToken));
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends one request, after its budget gives a token. <c>null</c> means a 401 that <paramref name="renewable"/>
    /// allows to try again.
    /// </summary>
    private async Task<Answer?> SendAsync(
        CompiledRecipe recipe, HttpRequestMessage request, bool renewable, Budget? budget, CancellationToken cancellationToken)
    {
        var permit = budget is null ? null : await budget.Budgets.WaitAsync(budget.Name, budget.Key, cancellationToken);

        using var timeout = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (renewable && response.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            var reading = budget?.Headers?.Read(response, _time.GetUtcNow(), _options.MaxRetryAfter) ?? default;
            if (IsThrottle(response, reading))
                throw new JobThrottledException(HttpErrors.Throttled(recipe.Name, (int)response.StatusCode).ToString(), await PauseAsync(response, reading, budget, permit, cancellationToken));

            ThrowUnlessSuccess(response, recipe.Name);

            if (permit is not null && reading.Remaining is { } remaining)
                await budget!.Budgets.ReportRemainingAsync(permit, remaining, reading.ResetsIn, cancellationToken);

            var links = response.Headers.TryGetValues("Link", out var values) ? values.ToArray() : [];
            var (document, tooLarge) = await JsonBody.ReadAsync(response.Content, _options.MaxResponseBytes, linked.Token);
            if (tooLarge)
                throw new JobAbandonedException(HttpErrors.ResponseTooLarge(recipe.Name, _options.MaxResponseBytes).ToString());

            return document is null
                ? throw new SyncLaneException(HttpErrors.ResponseNotJson(recipe.Name))
                : new Answer(document, links, request.RequestUri!);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SyncLaneException(HttpErrors.Timeout(recipe.Name, _options.Timeout), exception);
        }
        catch (HttpRequestException exception)
        {
            // The exception's own message may hold the URL, with a key in its query, or a proxy's credentials, and the
            // engine stores every message of the chain. Only the kind of failure is kept.
            throw new SyncLaneException(HttpErrors.Unreachable(recipe.Name, exception.HttpRequestError));
        }
    }

    private static void ThrowUnlessSuccess(HttpResponseMessage response, string recipe)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return;

        throw status switch
        {
            401 or 403 => new JobAbandonedException(HttpErrors.Unauthorized(recipe, status).ToString()),
            408 or >= 500 => new SyncLaneException(HttpErrors.ServerError(recipe, status)),
            >= 300 and < 400 => new JobAbandonedException(HttpErrors.Redirected(recipe, status).ToString()),
            _ => (Exception)new JobAbandonedException(HttpErrors.RequestRejected(recipe, status).ToString())
        };
    }

    /// <summary>
    /// Whether an answer says "slow down": a 429, or a 403 that says when to come back or that nothing is left. Some APIs
    /// answer a spent limit with 403; without one of these signals a 403 still means the credentials are refused.
    /// </summary>
    private static bool IsThrottle(HttpResponseMessage response, RateReading reading) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null || reading.Remaining <= 0));

    /// <summary>
    /// How long the job that was told to slow down waits: <c>Retry-After</c>, or else the reset the recipe's headers
    /// gave, at least a second, because a reset in whole seconds may have been rounded down. With a budget, the whole
    /// budget pauses for everyone on the key, on every instance, and the job waits as long as the pause.
    /// </summary>
    private async Task<TimeSpan?> PauseAsync(
        HttpResponseMessage response, RateReading reading, Budget? budget, BudgetPermit? permit, CancellationToken cancellationToken)
    {
        var asked = RetryAfter.From(response, _time.GetUtcNow(), _options.MaxRetryAfter)
            ?? (reading.ResetsIn is { } reset ? (reset > MinResetWait ? reset : MinResetWait) : null);

        return budget is null || permit is null ? asked : await budget.Budgets.ThrottledAsync(permit, asked, cancellationToken);
    }

    /// <summary>The rate budget a page's requests draw from, whose bucket, and the headers that say what is left.</summary>
    private sealed record Budget(RateBudgets Budgets, string Name, string Key, RateHeaders? Headers);

    /// <summary>A parsed answer and what paging needs from its headers.</summary>
    private sealed record Answer(JsonDocument Document, IReadOnlyList<string> Links, Uri Url) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private Task WriteAsync(HttpRecipeBinding binding, RecordPage page, CancellationToken cancellationToken) =>
        binding.SinkDelegate is { } write
            ? write(page, cancellationToken)
            : ((IRecordSink)_services.GetRequiredService(binding.SinkType!)).WriteAsync(page, cancellationToken);
}
