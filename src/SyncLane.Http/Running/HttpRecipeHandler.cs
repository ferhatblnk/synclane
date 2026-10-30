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
/// How answers become job outcomes: 2xx is a page; 429 waits, honouring <c>Retry-After</c>; 401 renews an OAuth 2.0
/// token once, and otherwise abandons the job, as 403 does, because retrying with the same credentials cannot help;
/// 408, 5xx, timeouts, network errors and broken JSON retry with the queue's growing delay; redirects and other 4xx
/// abandon. No message ever contains a response body, a header value, a credential or a URL.
/// </remarks>
public sealed class HttpRecipeHandler : IJobHandler
{
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
        using var answer = await FetchAsync(recipe, job, pageRequest.Value, auth, cancellationToken);

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
    private async Task<Answer> FetchAsync(CompiledRecipe recipe, Job job, PageRequest page, AuthContext? auth, CancellationToken cancellationToken)
    {
        var (first, stamp) = await PrepareAsync(recipe, job, page, auth, rejected: null, cancellationToken);
        using (first)
        {
            var renewable = recipe.Auth is { Renews: true } && !stamp.Fresh;
            if (await SendAsync(recipe, first, renewable, cancellationToken) is { } answer)
                return answer;
        }

        var (second, _) = await PrepareAsync(recipe, job, page, auth, stamp.Token, cancellationToken);
        using (second)
            return await SendAsync(recipe, second, renewable: false, cancellationToken) ?? throw new UnreachableException();
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

    /// <summary>Sends one request. <c>null</c> means a 401 that <paramref name="renewable"/> allows to try again.</summary>
    private async Task<Answer?> SendAsync(CompiledRecipe recipe, HttpRequestMessage request, bool renewable, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (renewable && response.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            ThrowUnlessSuccess(response, recipe.Name);

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

    private void ThrowUnlessSuccess(HttpResponseMessage response, string recipe)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return;

        throw status switch
        {
            429 => new JobThrottledException(
                HttpErrors.Throttled(recipe).ToString(), RetryAfter.From(response, _time.GetUtcNow(), _options.MaxRetryAfter)),
            401 or 403 => new JobAbandonedException(HttpErrors.Unauthorized(recipe, status).ToString()),
            408 or >= 500 => new SyncLaneException(HttpErrors.ServerError(recipe, status)),
            >= 300 and < 400 => new JobAbandonedException(HttpErrors.Redirected(recipe, status).ToString()),
            _ => (Exception)new JobAbandonedException(HttpErrors.RequestRejected(recipe, status).ToString())
        };
    }

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
