using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>A recipe with a <c>rate</c> section, one page at a time, against the database that holds the buckets.</summary>
public class BudgetedRecipeTests : PostgresTest
{
    [PostgresFact]
    public async Task Every_request_waits_for_a_token_of_the_recipes_budget()
    {
        var api = new TimedApi();
        await using var source = Source(api, budget: options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => source.RunAsync("shop-7")));

        Assert.InRange(api.Spread.TotalSeconds, 0.18, 2);
    }

    [PostgresFact]
    public async Task A_429_pauses_the_budget_for_every_page_of_the_key_and_no_other()
    {
        var api = new TimedApi(first: () =>
        {
            var response = FakeApi.Response(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return response;
        });
        await using var source = Source(api, budget: options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));

        var throttled = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-7"));
        await source.RunAsync("shop-8");
        await source.RunAsync("shop-7");

        Assert.InRange(throttled.Delay.TotalSeconds, 0.95, 1);
        var (throttledAt, otherOwner, sameOwner) = (api.Times[0], api.Times[1], api.Times[2]);
        Assert.InRange((otherOwner - throttledAt).TotalSeconds, 0, 0.2);
        Assert.InRange((sameOwner - throttledAt).TotalSeconds, 0.95, 3);
    }

    [PostgresFact]
    public async Task Owners_with_one_key_share_one_bucket_and_a_long_wait_postpones_the_job_before_anything_is_sent()
    {
        var api = new TimedApi();
        await using var source = Source(
            api,
            budget: options =>
            {
                options.MaxBudgetWait = TimeSpan.FromMilliseconds(100);
                options.AddBudget("shop", 1, TimeSpan.FromMinutes(1));
            },
            rate: """{ "budget": "shop", "key": "shared-account" }""");

        await source.RunAsync("shop-7");
        var postponed = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-8"));

        Assert.StartsWith(SyncErrorCodes.BudgetWaitTooLong, postponed.Message);
        Assert.Single(api.Times);
    }

    [PostgresFact]
    public async Task A_429_without_retry_after_waits_for_the_reset_the_recipes_headers_give()
    {
        var api = new TimedApi(() => Answer(HttpStatusCode.TooManyRequests, ("X-Left", "0"), ("X-Reset", "5")));
        await using var source = Source(api, budget: Shop, rate: WithHeaders);

        var throttled = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-7"));

        Assert.InRange(throttled.Delay.TotalSeconds, 4.9, 5);
    }

    [PostgresFact]
    public async Task Retry_after_wins_over_the_reset_header()
    {
        var api = new TimedApi(() => Answer(HttpStatusCode.TooManyRequests, ("Retry-After", "2"), ("X-Reset", "30")));
        await using var source = Source(api, budget: Shop, rate: WithHeaders);

        var throttled = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-7"));

        Assert.InRange(throttled.Delay.TotalSeconds, 1.9, 2);
    }

    [PostgresFact]
    public async Task A_403_that_says_nothing_is_left_is_a_throttle_until_the_reset_not_a_refusal()
    {
        var api = new TimedApi(() => Answer(HttpStatusCode.Forbidden, ("X-Left", "0"), ("X-Reset", "5")));
        await using var source = Source(api, budget: Shop, rate: WithHeaders);

        var throttled = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-7"));

        Assert.StartsWith(HttpErrorCodes.Throttled, throttled.Message);
        Assert.InRange(throttled.Delay.TotalSeconds, 4.9, 5);
    }

    [PostgresFact]
    public async Task A_403_with_retry_after_is_a_throttle_and_a_403_with_neither_signal_is_still_a_refusal()
    {
        var api = new TimedApi(index => index switch
        {
            1 => Answer(HttpStatusCode.Forbidden, ("Retry-After", "3")),
            2 => Answer(HttpStatusCode.Forbidden, ("X-Left", "41")),
            _ => null
        });
        await using var source = Source(api, budget: Shop, rate: WithHeaders);

        var throttled = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-7"));
        var refused = await Assert.ThrowsAsync<JobAbandonedException>(() => source.RunAsync("shop-8"));

        Assert.InRange(throttled.Delay.TotalSeconds, 2.9, 3);
        Assert.StartsWith(HttpErrorCodes.Unauthorized, refused.Message);
    }

    [PostgresFact]
    public async Task After_an_answer_that_says_nothing_is_left_the_next_request_of_the_key_waits_for_the_reset()
    {
        var api = new TimedApi(index => index == 1 ? Answer(HttpStatusCode.OK, ("X-Left", "0"), ("X-Reset", "1")) : null);
        await using var source = Source(api, budget: Shop, rate: WithHeaders);

        await source.RunAsync("shop-7");
        await source.RunAsync("shop-8");
        await source.RunAsync("shop-7");

        var (first, otherOwner, sameOwner) = (api.Times[0], api.Times[1], api.Times[2]);
        Assert.InRange((otherOwner - first).TotalSeconds, 0, 0.2);
        Assert.InRange((sameOwner - first).TotalSeconds, 0.95, 3);
    }

    [PostgresFact]
    public async Task Without_declared_headers_nothing_is_read()
    {
        var api = new TimedApi(index => index == 1 ? Answer(HttpStatusCode.OK, ("X-Left", "0"), ("X-Reset", "30")) : null);
        await using var source = Source(api, budget: Shop);

        await source.RunAsync("shop-7");
        await source.RunAsync("shop-7");

        Assert.InRange((api.Times[1] - api.Times[0]).TotalSeconds, 0, 0.2);
    }

    private const string WithHeaders = """{ "budget": "shop", "headers": { "remaining": "X-Left", "reset": "X-Reset" } }""";

    private static void Shop(SyncLaneOptions options) => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10);

    private static HttpResponseMessage Answer(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = FakeApi.Response(status, status == HttpStatusCode.OK ? """{ "items": [] }""" : "{}");
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);

        return response;
    }

    private BudgetedSource Source(TimedApi api, Action<SyncLaneOptions> budget, string rate = """{ "budget": "shop" }""") =>
        new(api, budget, RateTests.Recipe(rate), ConnectionString!, Schema);

    /// <summary>The handler with the engine's budgets, run one page at a time without workers.</summary>
    private sealed class BudgetedSource : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public BudgetedSource(TimedApi api, Action<SyncLaneOptions> budget, string recipe, string connectionString, string schema)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSyncLane(options =>
            {
                options.ConnectionString = connectionString;
                options.Schema = schema;
                budget(options);
                options.AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
            });
            services.AddSyncLaneHttp(http => http.Recipe("orders", recipe).WriteTo((_, _) => Task.CompletedTask));
            services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
            _provider = services.BuildServiceProvider();
        }

        public async Task<JobPage> RunAsync(string owner)
        {
            await using var scope = _provider.CreateAsyncScope();
            var job = new Job { Id = 1, Queue = "shops", Owner = owner, Lane = owner, Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
            return await scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>().RunPageAsync(job, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    /// <summary>Answers with an empty page unless the test says otherwise for the nth request, and notes when each request arrived.</summary>
    private sealed class TimedApi(Func<int, HttpResponseMessage?>? answer = null) : HttpMessageHandler
    {
        public TimedApi(Func<HttpResponseMessage> first)
            : this(index => index == 1 ? first() : null)
        {
        }

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TimeSpan> _times = new();
        private int _count;

        public IReadOnlyList<TimeSpan> Times => _times.ToArray();

        public TimeSpan Spread => Times.Count < 2 ? TimeSpan.Zero : Times.Max() - Times.Min();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _times.Enqueue(_clock.Elapsed);
            var index = Interlocked.Increment(ref _count);
            return Task.FromResult(answer?.Invoke(index) ?? FakeApi.Response(HttpStatusCode.OK, """{ "items": [] }"""));
        }
    }
}
