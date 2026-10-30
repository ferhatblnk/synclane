using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SyncLane.Http;

/// <summary>
/// Runs a job from its recipe: builds the request, sends it, finds the records and hands them to the recipe's sink.
/// Declare job types with it, for example <c>queue.Windowed&lt;HttpRecipeHandler&gt;("orders", ...)</c>, and register their
/// recipes with <c>AddSyncLaneHttp</c>.
/// </summary>
/// <remarks>
/// How answers become job outcomes: 2xx is a page; 429 waits, honouring <c>Retry-After</c>; 401 and 403 abandon the job,
/// because retrying with the same credentials cannot help; 408, 5xx, timeouts, network errors and broken JSON retry
/// with the queue's growing delay; redirects and other 4xx abandon. No message ever contains a response body, a header
/// value or a URL.
/// </remarks>
public sealed class HttpRecipeHandler : IJobHandler
{
    private const int ReadChunk = 81920;

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
        using var document = await FetchAsync(recipe, job, cancellationToken);

        var records = recipe.Records.Find(document.RootElement, recipe.Name);
        if (!records.Succeeded)
            throw new SyncLaneException(records.Errors);

        var page = new RecordPage(job, recipe.Name, records.Value);
        await WriteAsync(binding, page, cancellationToken);

        return new JobPage(null, page.Count);
    }

    /// <summary>Sends the page's request and returns its JSON, or throws the exception that gives the right outcome.</summary>
    private async Task<JsonDocument> FetchAsync(CompiledRecipe recipe, Job job, CancellationToken cancellationToken)
    {
        var rendered = RequestRenderer.Render(recipe, job);
        if (!rendered.Succeeded)
            throw new JobAbandonedException(string.Join("; ", rendered.Errors));

        using var request = rendered.Value!;
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        using var timeout = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            ThrowUnlessSuccess(response, recipe.Name);
            return await ReadJsonAsync(response.Content, recipe.Name, linked.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SyncLaneException(HttpErrors.Timeout(recipe.Name, _options.Timeout), exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SyncLaneException(HttpErrors.Unreachable(recipe.Name), exception);
        }
    }

    private void ThrowUnlessSuccess(HttpResponseMessage response, string recipe)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return;

        throw status switch
        {
            429 => new JobThrottledException(HttpErrors.Throttled(recipe).ToString(), RetryAfter(response)),
            401 or 403 => new JobAbandonedException(HttpErrors.Unauthorized(recipe, status).ToString()),
            408 or >= 500 => new SyncLaneException(HttpErrors.ServerError(recipe, status)),
            >= 300 and < 400 => new JobAbandonedException(HttpErrors.Redirected(recipe, status).ToString()),
            _ => (Exception)new JobAbandonedException(HttpErrors.RequestRejected(recipe, status).ToString())
        };
    }

    /// <summary>The wait the other side asked for, never negative and never longer than <see cref="HttpSourceOptions.MaxRetryAfter"/>.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : null);
        if (wait is not { } asked)
            return null;

        return asked < TimeSpan.Zero ? TimeSpan.Zero : asked > _options.MaxRetryAfter ? _options.MaxRetryAfter : asked;
    }

    /// <summary>Reads the body up to the size limit and parses it. The body is never put into a message.</summary>
    private async Task<JsonDocument> ReadJsonAsync(HttpContent content, string recipe, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new JobAbandonedException(HttpErrors.ResponseTooLarge(recipe, _options.MaxResponseBytes).ToString());

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new ArrayBufferWriter<byte>();

        // Sequential: reading one stream.
        while (true)
        {
            var read = await stream.ReadAsync(buffer.GetMemory(ReadChunk), cancellationToken);
            if (read == 0)
                break;

            buffer.Advance(read);
            if (buffer.WrittenCount > _options.MaxResponseBytes)
                throw new JobAbandonedException(HttpErrors.ResponseTooLarge(recipe, _options.MaxResponseBytes).ToString());
        }

        try
        {
            return JsonDocument.Parse(buffer.WrittenMemory);
        }
        catch (JsonException)
        {
            // The parser's message quotes the text it choked on, which is part of the body, so it is not kept.
            throw new SyncLaneException(HttpErrors.ResponseNotJson(recipe));
        }
    }

    private Task WriteAsync(HttpRecipeBinding binding, RecordPage page, CancellationToken cancellationToken) =>
        binding.SinkDelegate is { } write
            ? write(page, cancellationToken)
            : ((IRecordSink)_services.GetRequiredService(binding.SinkType!)).WriteAsync(page, cancellationToken);
}
