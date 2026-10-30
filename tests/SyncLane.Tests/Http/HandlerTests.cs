using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;

namespace SyncLane.Tests.Http;

public class HandlerTests
{
    private const string Orders =
        """
        {
          "name": "test/orders",
          "request": {
            "url": "https://api.example.com/v1/shops/{{owner.key}}/orders",
            "query": { "from": "{{window.start}}", "to": "{{window.end}}" }
          },
          "records": "$.content"
        }
        """;

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_page_is_fetched_and_its_records_reach_the_sink()
    {
        var api = FakeApi.Json("""{ "content": [{ "id": 1 }, { "id": 2 }], "total": 2 }""");
        var written = new List<string>();

        var page = await RunAsync(api, http => http.Recipe("orders", Orders).WriteTo((records, _) =>
        {
            written.AddRange(records.Records.EnumerateArray().Select(record => record.GetProperty("id").ToString()));
            return Task.CompletedTask;
        }));

        var request = Assert.Single(api.Requests);
        Assert.Equal(new JobPage(null, 2), page);
        Assert.Equal(["1", "2"], written);
        Assert.Equal("https://api.example.com/v1/shops/shop-7/orders?from=2026-10-01T00%3A00%3A00Z&to=2026-10-08T00%3A00%3A00Z", request.RequestUri!.AbsoluteUri);
        Assert.StartsWith("SyncLane.Http/", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_sink_type_gets_a_scope_of_its_own_for_every_page()
    {
        var api = FakeApi.Json("""{ "content": [{ "id": 9 }] }""");
        var collected = new ConcurrentBag<string>();

        await RunAsync(api, http => http.Recipe("orders", Orders).WriteTo<CollectingSink>(), services => services.AddSingleton(collected));

        Assert.Equal("9", Assert.Single(collected));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("864000", 3600)]
    public async Task A_429_waits_as_long_as_asked_but_never_longer_than_the_limit(string retryAfter, int seconds)
    {
        var api = new FakeApi((_, _) =>
        {
            var response = FakeApi.Response(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });

        var thrown = await Assert.ThrowsAsync<JobThrottledException>(() => RunAsync(api));

        Assert.Equal(TimeSpan.FromSeconds(seconds), thrown.RetryAfter);
        Assert.StartsWith(HttpErrorCodes.Throttled, thrown.Message);
    }

    [Fact]
    public async Task A_429_without_retry_after_leaves_the_wait_to_the_queue()
    {
        var thrown = await Assert.ThrowsAsync<JobThrottledException>(() => RunAsync(FakeApi.Json("{}", HttpStatusCode.TooManyRequests)));

        Assert.Null(thrown.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpErrorCodes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, HttpErrorCodes.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, HttpErrorCodes.RequestRejected)]
    [InlineData(HttpStatusCode.BadRequest, HttpErrorCodes.RequestRejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, HttpErrorCodes.RequestRejected)]
    [InlineData(HttpStatusCode.Found, HttpErrorCodes.Redirected)]
    [InlineData(HttpStatusCode.MovedPermanently, HttpErrorCodes.Redirected)]
    public async Task Answers_that_retrying_cannot_change_abandon_the_job(HttpStatusCode status, string code)
    {
        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(FakeApi.Json("{}", status)));

        Assert.StartsWith(code, thrown.Message);
        Assert.Contains(((int)status).ToString(), thrown.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Answers_that_may_pass_are_retried(HttpStatusCode status)
    {
        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(FakeApi.Json("{}", status)));

        Assert.Equal((HttpErrorCodes.ServerError, SyncErrorKind.Unavailable), (thrown.Error.Code, thrown.Error.Kind));
    }

    [Fact]
    public async Task No_message_repeats_the_body_or_the_url()
    {
        var failed = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(FakeApi.Json("""{ "error": "secret-body" }""", HttpStatusCode.InternalServerError)));
        var broken = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(FakeApi.Json("<html>secret-body</html>")));

        Assert.Equal(HttpErrorCodes.ResponseNotJson, broken.Error.Code);
        Assert.All([failed.Message, broken.Message], message =>
        {
            Assert.DoesNotContain("secret-body", message);
            Assert.DoesNotContain("shop-7", message);
            Assert.DoesNotContain("api.example.com", message);
        });
        Assert.Null(broken.InnerException);
    }

    [Fact]
    public async Task A_response_over_the_limit_is_refused_with_or_without_a_length()
    {
        var big = "{ \"content\": [\"" + new string('x', 4000) + "\"] }";
        var withLength = FakeApi.Json(big);
        var withoutLength = new FakeApi((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(big)))
        }));

        var first = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(withLength, limit: 1024));
        var second = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(withoutLength, limit: 1024));

        Assert.StartsWith(HttpErrorCodes.ResponseTooLarge, first.Message);
        Assert.StartsWith(HttpErrorCodes.ResponseTooLarge, second.Message);
    }

    [Fact]
    public async Task A_slow_answer_times_out_and_is_retried()
    {
        var api = new FakeApi(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return FakeApi.Response(HttpStatusCode.OK);
        });

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(api, timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal((HttpErrorCodes.Timeout, SyncErrorKind.Unavailable), (thrown.Error.Code, thrown.Error.Kind));
    }

    [Fact]
    public async Task A_stopping_worker_is_not_mistaken_for_a_timeout()
    {
        using var stopping = new CancellationTokenSource();
        var api = new FakeApi(async (_, cancellationToken) =>
        {
            await stopping.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return FakeApi.Response(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(api, cancellationToken: stopping.Token));
    }

    [Fact]
    public async Task A_network_failure_is_retried_and_keeps_its_cause()
    {
        var api = new FakeApi((_, _) => throw new HttpRequestException("Connection refused"));

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(api));

        Assert.Equal(HttpErrorCodes.Unreachable, thrown.Error.Code);
        Assert.IsType<HttpRequestException>(thrown.InnerException);
    }

    [Fact]
    public async Task An_answer_without_the_records_is_retried()
    {
        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => RunAsync(FakeApi.Json("""{ "error": "try later" }""")));

        Assert.Equal(HttpErrorCodes.RecordsNotFound, thrown.Error.Code);
    }

    [Fact]
    public async Task A_job_without_a_window_or_a_recipe_is_abandoned_before_anything_is_sent()
    {
        var api = FakeApi.Json("""{ "content": [] }""");

        var noWindow = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(api, job: Job() with { WindowStart = null, WindowEnd = null }));
        var noRecipe = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(api, job: Job() with { Type = "invoices" }));

        Assert.StartsWith(HttpErrorCodes.WindowMissing, noWindow.Message);
        Assert.StartsWith(HttpErrorCodes.RecipeMissing, noRecipe.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void The_connection_follows_no_redirect_and_accepts_compressed_answers()
    {
        using var handler = HttpSource.CreatePrimaryHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }

    [Fact]
    public void Every_problem_with_the_registration_is_thrown_together()
    {
        var thrown = Assert.Throws<SyncLaneException>(() => new ServiceCollection().AddSyncLaneHttp(http =>
        {
            http.Timeout = TimeSpan.Zero;
            http.Recipe("orders", Orders);
            http.Recipe("orders", Orders).WriteTo((_, _) => Task.CompletedTask);
            http.Recipe("products", """{ "name": "p", "request": { "url": "http://api.example.com" } }""").WriteTo((_, _) => Task.CompletedTask);
        }));

        Assert.Equal(
            [HttpErrorCodes.UrlInvalid, HttpErrorCodes.OptionInvalid, HttpErrorCodes.RecipeDeclaredTwice, HttpErrorCodes.SinkMissing],
            thrown.Errors.Select(error => error.Code));
    }

    private static async Task<JobPage> RunAsync(
        FakeApi api,
        Action<HttpSourceOptions>? configure = null,
        Action<IServiceCollection>? register = null,
        Job? job = null,
        long limit = 16 * 1024 * 1024,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        register?.Invoke(services);
        services.AddSyncLaneHttp(configure ?? (http =>
        {
            http.MaxResponseBytes = limit;
            http.Timeout = timeout ?? TimeSpan.FromSeconds(5);
            http.Recipe("orders", Orders).WriteTo((_, _) => Task.CompletedTask);
        }));
        services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>();

        return await handler.RunPageAsync(job ?? Job(), cancellationToken);
    }

    private static Job Job() => new()
    {
        Id = 1,
        Queue = "shops",
        Owner = "shop-7",
        Lane = "shop-7",
        Type = "orders",
        Mode = JobMode.Initial,
        Status = JobStatus.Running,
        WindowStart = October,
        WindowEnd = October.AddDays(7)
    };

    private sealed class CollectingSink(ConcurrentBag<string> collected) : IRecordSink
    {
        public Task WriteAsync(RecordPage page, CancellationToken cancellationToken)
        {
            foreach (var record in page.Records.EnumerateArray())
                collected.Add(record.GetProperty("id").ToString());

            return Task.CompletedTask;
        }
    }

    /// <summary>A body whose length is not known up front, as with chunked transfer.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
