using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>
/// The gate for authentication: no credential reaches a log, a span, a metric, an error message or the database, whatever the
/// API does. Every owner below meets a different failure, and the fake API and token endpoint echo every secret they
/// receive back in bodies and exception messages, which is the worst a real one or a proxy could do.
/// </summary>
/// <remarks>
/// Not covered: spans and logs of <see cref="HttpClient"/> itself, which the fake handler replaces. SyncLane turns off the
/// client's own logging; tracing it is the application's choice.
/// </remarks>
public class CredentialLeakTests : PostgresTest
{
    // "halfway" stops on its second page, so its cursor, the next page's URL, stays in the database to be searched.
    private static readonly string[] Owners = ["ok", "halfway", "unauthorized", "broken", "unreachable", "garbage", "throttled", "refused", "down"];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _marker = Guid.NewGuid().ToString("N");

    [PostgresTheory]
    [InlineData("""{ "type": "bearer", "credential": "example-shop" }""")]
    [InlineData("""{ "type": "basic", "credential": "example-shop" }""")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "X-Api-Key" }""")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "api_key" }""")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "https://auth.example.com/token" }""")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "refreshToken", "tokenUrl": "https://auth.example.com/token", "clientAuth": "body" }""")]
    [InlineData("""{ "type": "signed", "credential": "example-shop", "signer": "example-hmac" }""")]
    public async Task No_credential_reaches_a_log_a_span_a_metric_an_error_or_the_database(string auth)
    {
        var seen = new Seen();
        using var spans = seen.ListenToSpans();
        using var metrics = seen.ListenToMetrics();

        using var host = Host(auth, seen);
        await host.StartAsync();
        var client = host.Services.GetRequiredService<ISyncLane>();

        // Sequential: one enqueue per owner; the order does not matter, and a test does not need a batch.
        foreach (var owner in Owners)
            (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner(owner), Type = "orders" })).ThrowIfFailed();

        var jobs = await WaitAsync(found => found.Count == Owners.Length && found.All(job => job.Status is JobStatus.Completed or JobStatus.Abandoned));
        await host.StopAsync();

        var stored = await DumpSchemaAsync();
        var everything = string.Join('\n', [stored, .. seen.Logs, .. seen.Spans, .. seen.Metrics]);

        Assert.Equal(JobStatus.Completed, jobs.Single(job => job.Owner == "ok").Status);
        Assert.All(jobs.Where(job => job.Owner is "unauthorized" or "broken" or "unreachable"), job => Assert.NotNull(job.Error));
        Assert.Contains("page=2", jobs.Single(job => job.Owner == "halfway").Cursor);
        Assert.NotEmpty(seen.Logs);
        Assert.NotEmpty(seen.Spans);
        Assert.All(Forbidden(), secret => Assert.False(
            everything.Contains(secret, StringComparison.Ordinal),
            $"A secret was found: {Around(everything, secret)}"));
    }

    /// <summary>Every secret, and every form a request carries it in.</summary>
    private IEnumerable<string> Forbidden()
    {
        yield return _marker;

        foreach (var owner in Owners)
        {
            var credential = Credential(owner);
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.ClientId}:{credential.ClientSecret}"));
        }
    }

    private Credential Credential(string owner) => new()
    {
        Token = $"tok-{owner}-{_marker}",
        Username = $"user-{owner}-{_marker}",
        Password = $"pass-{owner}-{_marker}",
        ClientId = $"client-{owner}-{_marker}",
        ClientSecret = $"secret-{owner}-{_marker}",
        RefreshToken = $"refresh-{owner}-{_marker}",
        Values = new Dictionary<string, string> { ["secret"] = $"hmac-{owner}-{_marker}" }
    };

    private IHost Host(string auth, Seen seen)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(seen);

        var vault = new CredentialVault();
        foreach (var owner in Owners)
            vault.Add(owner, "example-shop", Credential(owner));

        builder.Services.AddSingleton(vault);
        builder.Services.AddSingleton(new SignatureLog());
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PollInterval = TimeSpan.FromMilliseconds(50);
            options.AddQueue(Queue, queue =>
            {
                queue.Handle<HttpRecipeHandler>("orders");
                queue.MaxAttempts = 2;
                queue.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
                queue.ThrottleBaseDelay = TimeSpan.FromMilliseconds(50);
            });
        });
        builder.Services.AddSyncLaneHttp(http =>
        {
            http.Credentials<VaultStore>();
            http.Signer<TestSigner>("example-hmac");
            http.Recipe("orders", Recipe(auth)).WriteTo((_, _) => Task.CompletedTask);
        });
        builder.Services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeApi(AnswerAsync));

        return builder.Build();
    }

    private static string Recipe(string auth) =>
        $$$"""
        {
          "name": "test/orders",
          "request": { "url": "https://api.example.com/{{owner.key}}/orders" },
          "records": "$.items",
          "pagination": { "type": "nextUrl", "from": "$.next" },
          "auth": {{{auth}}}
        }
        """;

    /// <summary>An API and a token endpoint as careless as they come: everything they receive goes back out.</summary>
    private static async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var echo = $"{url} {request.Headers} {body}";

        if (url.Host == "auth.example.com")
        {
            // The client id arrives in the form or in the Basic header; it names the owner.
            var sent = $"{echo} {Basic(request)}";
            if (sent.Contains("client-refused-", StringComparison.Ordinal))
                return FakeApi.Response(HttpStatusCode.BadRequest, Json(new { error = "invalid_grant", error_description = echo }));

            if (sent.Contains("client-down-", StringComparison.Ordinal))
                throw new HttpRequestException(HttpRequestError.ConnectionError, $"Could not reach {echo}");

            // The access token and the rotated refresh token carry the marker, so they are searched for too.
            var marker = Marker(sent);
            return FakeApi.Response(HttpStatusCode.OK, Json(new
            {
                access_token = $"access-{marker}",
                token_type = "Bearer",
                expires_in = 3600,
                refresh_token = $"rotated-{marker}"
            }));
        }

        var owner = url.Segments[1].TrimEnd('/');
        return owner switch
        {
            "unauthorized" => FakeApi.Response(HttpStatusCode.Unauthorized, Json(new { message = echo })),
            "broken" => FakeApi.Response(HttpStatusCode.InternalServerError, Json(new { message = echo })),
            "unreachable" => throw new HttpRequestException(HttpRequestError.ConnectionError, $"Proxy refused {echo}"),
            "garbage" => FakeApi.Response(HttpStatusCode.OK, $"<html>{echo}</html>"),
            "throttled" => Throttled(echo),
            "halfway" when url.Query.Contains("page=2", StringComparison.Ordinal) => FakeApi.Response(HttpStatusCode.Forbidden, Json(new { message = echo })),
            _ when url.Query.Contains("page=2", StringComparison.Ordinal) => FakeApi.Response(HttpStatusCode.OK, """{ "items": [] }"""),
            _ => FakeApi.Response(HttpStatusCode.OK, Json(new { items = new[] { 1 }, next = $"{url.AbsoluteUri}{(url.Query.Length > 0 ? "&" : "?")}page=2" }))
        };
    }

    private static HttpResponseMessage Throttled(string echo)
    {
        var response = FakeApi.Response(HttpStatusCode.TooManyRequests, Json(new { message = echo }));
        response.Headers.TryAddWithoutValidation("Retry-After", "0");
        return response;
    }

    private static string Basic(HttpRequestMessage request) =>
        request.Headers.Authorization is { Scheme: "Basic", Parameter: { } encoded } ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) : "";

    private static string Marker(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, "client-[a-z]+-([0-9a-f]{32})");
        return match.Success ? match.Groups[1].Value : "none";
    }

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Around(string text, string secret)
    {
        var at = text.IndexOf(secret, StringComparison.Ordinal);
        return text.Substring(Math.Max(0, at - 200), Math.Min(text.Length - Math.Max(0, at - 200), 400));
    }

    private async Task<IReadOnlyList<Job>> WaitAsync(Func<IReadOnlyList<Job>, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);

        // Sequential: polling the table until every job has reached an end.
        while (true)
        {
            var jobs = await JobsAsync();
            if (done(jobs))
                return jobs;

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    /// <summary>Everything the application could see: log lines with their values and exceptions, spans, metric tags.</summary>
    private sealed class Seen : ILoggerProvider
    {
        public ConcurrentQueue<string> Logs { get; } = new();

        public ConcurrentQueue<string> Spans { get; } = new();

        public ConcurrentQueue<string> Metrics { get; } = new();

        public ActivityListener ListenToSpans()
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Spans.Enqueue(string.Join(' ',
                [
                    activity.DisplayName,
                    activity.StatusDescription ?? "",
                    .. activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"),
                    .. activity.Events.SelectMany(found => found.Tags.Select(tag => $"{found.Name}:{tag.Key}={tag.Value}")),
                    .. activity.Baggage.Select(item => $"{item.Key}={item.Value}")
                ]))
            };

            ActivitySource.AddActivityListener(listener);
            return listener;
        }

        public MeterListener ListenToMetrics()
        {
            var listener = new MeterListener { InstrumentPublished = (instrument, found) => found.EnableMeasurementEvents(instrument) };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
            listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
            listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Record(instrument, tags));
            listener.Start();
            return listener;
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = new StringBuilder(instrument.Name);
            foreach (var (key, value) in tags)
                text.Append(' ').Append(key).Append('=').Append(value);

            Metrics.Enqueue(text.ToString());
        }

        private sealed class Logger(Seen seen, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(' ', pairs.Select(pair => $"{pair.Key}={pair.Value}")) : "";
                seen.Logs.Enqueue($"{category} {formatter(state, exception)} {values} {exception}");
            }
        }
    }
}
